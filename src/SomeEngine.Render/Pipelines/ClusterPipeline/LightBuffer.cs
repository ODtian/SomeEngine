using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
internal struct GPULight
{
    public const int SizeInBytes = 144;

    public Vector3 Position;
    public float Range;
    public Vector3 Direction;
    public float InnerConeCos;
    public Vector3 Color;
    public float Intensity;
    public float OuterConeCos;
    public uint LayerMask;
    public int CookieIndex;
    public float CookieStrength;
    public Matrix4x4 WorldToLightCookie;
    public Vector4 CookieScaleOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClusterLightGrid
{
    public const int SizeInBytes = 8;

    public uint Offset;
    public uint Count;
}

internal struct ClusterRange
{
    public bool Valid;
    public uint MinTileX;
    public uint MinTileY;
    public uint MaxTileX;
    public uint MaxTileY;
    public uint MinSlice;
    public uint MaxSlice;
}

internal readonly record struct LightGrid(
    RenderGraphHandle Buffer,
    RenderGraphHandle ClusterLightGrid,
    RenderGraphHandle LightIndexList,
    LightCounts Counts,
    LightGridUniforms Uniforms)
{
    public uint DirectionalLightCount => Counts.DirectionalCount;
    public uint PointLightCount => Counts.PointCount;
    public uint SpotLightCount => Counts.SpotCount;
    public uint TotalLightCount => checked(Counts.DirectionalCount + Counts.PointCount + Counts.SpotCount);
}

internal sealed class LightBuffer : IDisposable
{
    public const uint LightGridTileSize = 16;
    private const uint BruteForceLightGridThreshold = 16;

    private static readonly int CheckedGpuLightSize = CheckGpuLightSize();
    private readonly IDevice _device;
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _lightViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _clusterGridViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _indexListViews = new();
    private BufferHandle _lightBuffer;
    private BufferHandle _clusterGrid;
    private BufferHandle _indexList;
    private BufferHandle _lightUploadBuffer;
    private BufferHandle _clusterGridUploadBuffer;
    private BufferHandle _indexListUploadBuffer;
    private RenderGraph? _frameGraph;
    private ResourceState _lightState = ResourceState.Common;
    private ResourceState _clusterGridState = ResourceState.Common;
    private ResourceState _indexListState = ResourceState.Common;
    private int _lightBytes;
    private int _clusterGridBytes;
    private int _indexListBytes;
    private int _lightUploadBufferBytes;
    private int _clusterGridUploadBufferBytes;
    private int _indexListUploadBufferBytes;
    private GPULight[] _packedLights = [];
    private ClusterLightGrid[] _clusterLightGrid = [];
    private uint[] _lightIndexList = [];
    private uint[] _clusterLightCounts = [];
    private ClusterRange[] _pointRanges = [];
    private ClusterRange[] _spotRanges = [];
    private byte[] _lightSnapshot = [];
    private byte[] _clusterGridSnapshot = [];
    private byte[] _indexListSnapshot = [];
    private uint _lightUploadVersion;
    private LightGridUploadKey _gridUploadKey;
    private bool _gridUploadKeyValid;
    private int _bruteForceLightIndexCapacity;
    private DirectionalGridKey _directionalGridKey;
    private LightGridUniforms _directionalGridUniforms;
    private bool _directionalGridValid;
    private bool _disposed;

    public LightBuffer(IDevice device)
    {
        _ = CheckedGpuLightSize;
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public LightGrid AddFrame(
        RenderGraph graph,
        in SceneLights lights,
        uint lightVersion,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);

        uint directionalCount;
        uint pointCount;
        uint spotCount;
        int lightCount;
        using (Profiler.BeginScope("LightBuffer.Pack"))
        {
            Pack(
                lights,
                ref _packedLights,
                out directionalCount,
                out pointCount,
                out spotCount,
                out lightCount);
        }

        GetLightGrid(
            lights,
            shadeData,
            screenWidth,
            screenHeight,
            depthSliceCount,
            lightVersion,
            directionalCount,
            pointCount,
            out ReadOnlySpan<ClusterLightGrid> clusterLightGrid,
            out ReadOnlySpan<uint> lightIndexList,
            out LightGridUniforms lightGridUniforms,
            out LightGridUploadKey gridUploadKey);

        ReadOnlySpan<byte> lightBytes = MemoryMarshal.AsBytes(_packedLights.AsSpan(0, Math.Max(lightCount, 1)));
        ReadOnlySpan<byte> clusterGridBytes = MemoryMarshal.AsBytes(clusterLightGrid);
        ReadOnlySpan<byte> indexListBytes = MemoryMarshal.AsBytes(lightIndexList);

        bool lightCreated;
        bool gridCreated;
        bool indexCreated;
        using (Profiler.BeginScope("LightBuffer.EnsureBuffers"))
        {
            lightCreated = EnsureBuffer(
                ref _lightBuffer,
                ref _lightBytes,
                LightDesc(lightBytes.Length),
                _lightViews,
                ref _lightState);
            gridCreated = EnsureBuffer(
                ref _clusterGrid,
                ref _clusterGridBytes,
                ClusterGridDesc(clusterGridBytes.Length),
                _clusterGridViews,
                ref _clusterGridState);
            indexCreated = EnsureBuffer(
                ref _indexList,
                ref _indexListBytes,
                IndexListDesc(indexListBytes.Length),
                _indexListViews,
                ref _indexListState);
        }

        RenderGraphHandle lightBuffer = graph.ImportBuffer(
            "LightBuffer",
            _lightBuffer,
            LightDesc(_lightBytes),
            new ImportDesc(_lightState)
            {
                AllowWrite = true,
            },
            _lightViews);
        RenderGraphHandle clusterGrid = graph.ImportBuffer(
            "ClusterLightGrid",
            _clusterGrid,
            ClusterGridDesc(_clusterGridBytes),
            new ImportDesc(_clusterGridState)
            {
                AllowWrite = true,
            },
            _clusterGridViews);
        RenderGraphHandle indexList = graph.ImportBuffer(
            "LightIndexList",
            _indexList,
            IndexListDesc(_indexListBytes),
            new ImportDesc(_indexListState)
            {
                AllowWrite = true,
            },
            _indexListViews);
        graph.ExtractBuffer(
            lightBuffer,
            ResourceState.ShaderResource,
            (_, state) => _lightState = state);
        graph.ExtractBuffer(
            clusterGrid,
            ResourceState.ShaderResource,
            (_, state) => _clusterGridState = state);
        graph.ExtractBuffer(
            indexList,
            ResourceState.ShaderResource,
            (_, state) => _indexListState = state);
        _frameGraph = graph;

        using (Profiler.BeginScope("LightBuffer.UploadDiff"))
        {
            UploadLightBuffer(
                graph,
                lightBuffer,
                lightBytes,
                lightVersion,
                lightCreated);
            UploadLightGrid(
                graph,
                clusterGrid,
                clusterGridBytes,
                indexList,
                indexListBytes,
                gridUploadKey,
                gridCreated,
                indexCreated);
        }

        return new LightGrid(
            lightBuffer,
            clusterGrid,
            indexList,
            new LightCounts
            {
                DirectionalCount = directionalCount,
                PointCount = pointCount,
                SpotCount = spotCount,
            },
            lightGridUniforms);
    }

    public static LightGrid Add(
        RenderGraph graph,
        in SceneLights lights,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount)
    {
        ArgumentNullException.ThrowIfNull(graph);

        GPULight[] data = Pack(lights, out uint directionalCount, out uint pointCount, out uint spotCount);
        BuildLightGrid(
            lights,
            shadeData,
            screenWidth,
            screenHeight,
            depthSliceCount,
            directionalCount,
            pointCount,
            out ClusterLightGrid[] clusterLightGrid,
            out uint[] lightIndexList,
            out LightGridUniforms lightGridUniforms);

        RenderGraphHandle buffer = graph.CreateBuffer(
            "LightBuffer",
            new BufferDesc
            {
                Name = "LightBuffer",
                SizeInBytes = checked((ulong)data.Length * GPULight.SizeInBytes),
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
                InitialState = ResourceState.Common,
                StrideInBytes = GPULight.SizeInBytes,
            });
        BufferUploadPasses.AddUploadPass(
            graph,
            "LightBuffer Upload",
            buffer,
            0,
            MemoryMarshal.AsBytes(data.AsSpan()));

        RenderGraphHandle clusterGrid = graph.CreateBuffer(
            "ClusterLightGrid",
            new BufferDesc
            {
                Name = "ClusterLightGrid",
                SizeInBytes = checked((ulong)Math.Max(clusterLightGrid.Length, 1) * ClusterLightGrid.SizeInBytes),
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
                InitialState = ResourceState.Common,
                StrideInBytes = ClusterLightGrid.SizeInBytes,
            });
        BufferUploadPasses.AddUploadPass(
            graph,
            "ClusterLightGrid Upload",
            clusterGrid,
            0,
            MemoryMarshal.AsBytes(clusterLightGrid.AsSpan()));

        RenderGraphHandle indexList = graph.CreateBuffer(
            "LightIndexList",
            new BufferDesc
            {
                Name = "LightIndexList",
                SizeInBytes = checked((ulong)Math.Max(lightIndexList.Length, 1) * sizeof(uint)),
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
                InitialState = ResourceState.Common,
                StrideInBytes = sizeof(uint),
            });
        BufferUploadPasses.AddUploadPass(
            graph,
            "LightIndexList Upload",
            indexList,
            0,
            MemoryMarshal.AsBytes(lightIndexList.AsSpan()));

        return new LightGrid(
            buffer,
            clusterGrid,
            indexList,
            new LightCounts
            {
                DirectionalCount = directionalCount,
                PointCount = pointCount,
                SpotCount = spotCount,
            },
            lightGridUniforms);
    }

    internal static GPULight[] Pack(
        in SceneLights lights,
        out uint directionalCount,
        out uint pointCount,
        out uint spotCount)
    {
        _ = CheckedGpuLightSize;
        GPULight[] data = [];
        Pack(lights, ref data, out directionalCount, out pointCount, out spotCount, out _);
        return data;
    }

    private static void Pack(
        in SceneLights lights,
        ref GPULight[] data,
        out uint directionalCount,
        out uint pointCount,
        out uint spotCount,
        out int total)
    {
        ReadOnlySpan<DirectionalLight> directionalLights = lights.DirectionalLights.Span;
        ReadOnlySpan<PointLight> pointLights = lights.PointLights.Span;
        ReadOnlySpan<SpotLight> spotLights = lights.SpotLights.Span;

        directionalCount = checked((uint)directionalLights.Length);
        pointCount = checked((uint)pointLights.Length);
        spotCount = checked((uint)spotLights.Length);

        total = checked(directionalLights.Length + pointLights.Length + spotLights.Length);
        EnsureLength(ref data, Math.Max(total, 1));
        if (total == 0)
            data[0] = default;

        int index = 0;

        for (int i = 0; i < directionalLights.Length; i++)
        {
            DirectionalLight light = directionalLights[i];
            data[index++] = new GPULight
            {
                Direction = light.Direction,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = light.CookieIndex,
                CookieStrength = light.CookieStrength,
                WorldToLightCookie = light.WorldToLightCookie,
                CookieScaleOffset = light.CookieScaleOffset,
            };
        }

        for (int i = 0; i < pointLights.Length; i++)
        {
            PointLight light = pointLights[i];
            data[index++] = new GPULight
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = light.CookieIndex,
                CookieStrength = light.CookieStrength,
                WorldToLightCookie = light.WorldToLightCookie,
                CookieScaleOffset = light.CookieScaleOffset,
            };
        }

        for (int i = 0; i < spotLights.Length; i++)
        {
            SpotLight light = spotLights[i];
            data[index++] = new GPULight
            {
                Position = light.Position,
                Range = light.Range,
                Direction = light.Direction,
                InnerConeCos = light.InnerConeCos,
                Color = light.Color,
                Intensity = light.Intensity,
                OuterConeCos = light.OuterConeCos,
                LayerMask = light.LayerMask,
                CookieIndex = light.CookieIndex,
                CookieStrength = light.CookieStrength,
                WorldToLightCookie = light.WorldToLightCookie,
                CookieScaleOffset = light.CookieScaleOffset,
            };
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _device.WaitIdle();
        ClearBindings();
        DestroyBuffer(ref _lightBuffer, _lightViews);
        DestroyBuffer(ref _clusterGrid, _clusterGridViews);
        DestroyBuffer(ref _indexList, _indexListViews);
        DestroyUploadBuffer(ref _lightUploadBuffer, ref _lightUploadBufferBytes);
        DestroyUploadBuffer(ref _clusterGridUploadBuffer, ref _clusterGridUploadBufferBytes);
        DestroyUploadBuffer(ref _indexListUploadBuffer, ref _indexListUploadBufferBytes);
        _frameGraph = null;
        _disposed = true;
    }

    private void GetLightGrid(
        in SceneLights lights,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount,
        uint lightVersion,
        uint directionalCount,
        uint pointCount,
        out ReadOnlySpan<ClusterLightGrid> clusterLightGrid,
        out ReadOnlySpan<uint> lightIndexList,
        out LightGridUniforms uniforms,
        out LightGridUploadKey uploadKey)
    {
        ReadOnlySpan<PointLight> pointLights = lights.PointLights.Span;
        ReadOnlySpan<SpotLight> spotLights = lights.SpotLights.Span;
        if (pointLights.IsEmpty && spotLights.IsEmpty)
        {
            using (Profiler.BeginScope("LightBuffer.GetLightGrid.Directional"))
            {
                GetDirectionalLightGrid(
                    screenWidth,
                    screenHeight,
                    depthSliceCount,
                    out clusterLightGrid,
                    out lightIndexList,
                    out uniforms);
            }
            uploadKey = new LightGridUploadKey(
                Math.Max(screenWidth, 1u),
                Math.Max(screenHeight, 1u),
                Math.Max(depthSliceCount, 1u),
                0,
                directionalCount,
                default,
                default,
                default);
            return;
        }

        _directionalGridValid = false;
        uint spotCount = checked((uint)spotLights.Length);
        uint nonDirectionalCount = checked(pointCount + spotCount);
        if (nonDirectionalCount <= BruteForceLightGridThreshold)
        {
            using (Profiler.BeginScope("LightBuffer.GetLightGrid.BruteForce"))
            {
                uint width = Math.Max(screenWidth, 1u);
                uint height = Math.Max(screenHeight, 1u);
                uint sliceCount = Math.Max(depthSliceCount, 1u);
                uint tileCountX = DivRoundUp(width, LightGridTileSize);
                uint tileCountY = DivRoundUp(height, LightGridTileSize);
                int clusterCount = checked((int)(tileCountX * tileCountY * sliceCount));
                int requiredIndexCount = checked((int)nonDirectionalCount);
                _bruteForceLightIndexCapacity = Math.Max(_bruteForceLightIndexCapacity, requiredIndexCount);
                int indexCount = Math.Max(_bruteForceLightIndexCapacity, 1);
                uploadKey = new LightGridUploadKey(
                    width,
                    height,
                    sliceCount,
                    checked((uint)indexCount),
                    directionalCount,
                    default,
                    default,
                    default);

                if (!_gridUploadKeyValid
                    || _gridUploadKey != uploadKey
                    || _clusterLightGrid.Length < clusterCount
                    || _lightIndexList.Length < indexCount)
                {
                    using (Profiler.BeginScope("LightBuffer.GetLightGrid.BruteForce.Rebuild"))
                    {
                        EnsureLength(ref _clusterLightGrid, clusterCount);
                        EnsureLength(ref _lightIndexList, indexCount);
                        for (int i = 0; i < indexCount; i++)
                            _lightIndexList[i] = checked(directionalCount + (uint)i);

                        Array.Fill(
                            _clusterLightGrid,
                            new ClusterLightGrid
                            {
                                Offset = 0,
                                Count = checked((uint)indexCount),
                            },
                            0,
                            clusterCount);
                    }
                }
                else
                {
                    using var _ = Profiler.BeginScope("LightBuffer.GetLightGrid.BruteForce.Reuse");
                }

                Matrix4x4 view = Matrix4x4.Transpose(shadeData.View);
                uniforms = new LightGridUniforms
                {
                    LightGridTileSizeX = LightGridTileSize,
                    LightGridTileSizeY = LightGridTileSize,
                    LightGridTileCountX = tileCountX,
                    LightGridTileCountY = tileCountY,
                    LightGridZParams = GetLightGridZParams(pointLights, spotLights, view, sliceCount),
                    DepthSliceCount = sliceCount,
                };
                clusterLightGrid = _clusterLightGrid.AsSpan(0, clusterCount);
                lightIndexList = _lightIndexList.AsSpan(0, indexCount);
            }
            return;
        }

        uploadKey = new LightGridUploadKey(
            screenWidth,
            screenHeight,
            depthSliceCount,
            lightVersion,
            directionalCount,
            shadeData.View,
            shadeData.ViewProj,
            shadeData.CameraPos);
        using (Profiler.BeginScope("LightBuffer.GetLightGrid.Build"))
        {
            BuildLightGrid(
                lights,
                shadeData,
                screenWidth,
                screenHeight,
                depthSliceCount,
                directionalCount,
                pointCount,
                out clusterLightGrid,
                out lightIndexList,
                out uniforms);
        }
    }

    internal void BuildLightGrid(
        in SceneLights lights,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount,
        uint directionalCount,
        uint pointCount,
        out ReadOnlySpan<ClusterLightGrid> clusterLightGrid,
        out ReadOnlySpan<uint> lightIndexList,
        out LightGridUniforms uniforms)
    {
        using var scope = Profiler.BeginScope("LightBuffer.BuildGrid");
        uint width = Math.Max(screenWidth, 1u);
        uint height = Math.Max(screenHeight, 1u);
        uint sliceCount = Math.Max(depthSliceCount, 1u);
        uint tileCountX = DivRoundUp(width, LightGridTileSize);
        uint tileCountY = DivRoundUp(height, LightGridTileSize);
        uint clustersPerSlice = checked(tileCountX * tileCountY);
        int clusterCount = checked((int)(clustersPerSlice * sliceCount));
        EnsureLength(ref _clusterLightGrid, clusterCount);
        EnsureLength(ref _clusterLightCounts, clusterCount);

        Array.Clear(_clusterLightCounts, 0, clusterCount);

        ReadOnlySpan<PointLight> pointLights = lights.PointLights.Span;
        ReadOnlySpan<SpotLight> spotLights = lights.SpotLights.Span;
        Matrix4x4 viewProj = Matrix4x4.Transpose(shadeData.ViewProj);
        Matrix4x4 view = Matrix4x4.Transpose(shadeData.View);
        Vector3 cameraPosition = shadeData.CameraPos;
        uint spotOffset = checked(directionalCount + pointCount);
        Vector4 lightGridZParams = GetLightGridZParams(pointLights, spotLights, view, sliceCount);
        EnsureLength(ref _pointRanges, pointLights.Length);
        EnsureLength(ref _spotRanges, spotLights.Length);

        for (int localIndex = 0; localIndex < pointLights.Length; localIndex++)
        {
            PointLight light = pointLights[localIndex];
            if (!ProjectLightBoundsToTiles(
                    light.Position,
                    light.Range,
                    viewProj,
                    view,
                    lightGridZParams,
                    sliceCount,
                    cameraPosition,
                    width,
                    height,
                    out uint minTileX,
                    out uint minTileY,
                    out uint maxTileX,
                    out uint maxTileY,
                    out uint minSlice,
                    out uint maxSlice))
            {
                _pointRanges[localIndex].Valid = false;
                continue;
            }

            _pointRanges[localIndex] = new ClusterRange
            {
                Valid = true,
                MinTileX = minTileX,
                MinTileY = minTileY,
                MaxTileX = maxTileX,
                MaxTileY = maxTileY,
                MinSlice = minSlice,
                MaxSlice = maxSlice,
            };
            AddLightRangeCounts(
                minTileX,
                minTileY,
                maxTileX,
                maxTileY,
                minSlice,
                maxSlice,
                tileCountX,
                clustersPerSlice);
        }

        for (int localIndex = 0; localIndex < spotLights.Length; localIndex++)
        {
            SpotLight light = spotLights[localIndex];
            if (!ProjectLightBoundsToTiles(
                    light.Position,
                    light.Range,
                    viewProj,
                    view,
                    lightGridZParams,
                    sliceCount,
                    cameraPosition,
                    width,
                    height,
                    out uint minTileX,
                    out uint minTileY,
                    out uint maxTileX,
                    out uint maxTileY,
                    out uint minSlice,
                    out uint maxSlice))
            {
                _spotRanges[localIndex].Valid = false;
                continue;
            }

            _spotRanges[localIndex] = new ClusterRange
            {
                Valid = true,
                MinTileX = minTileX,
                MinTileY = minTileY,
                MaxTileX = maxTileX,
                MaxTileY = maxTileY,
                MinSlice = minSlice,
                MaxSlice = maxSlice,
            };
            AddLightRangeCounts(
                minTileX,
                minTileY,
                maxTileX,
                maxTileY,
                minSlice,
                maxSlice,
                tileCountX,
                clustersPerSlice);
        }

        uint totalIndices = 0;
        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            uint count = _clusterLightCounts[clusterIndex];
            _clusterLightGrid[clusterIndex] = new ClusterLightGrid
            {
                Offset = totalIndices,
                Count = count,
            };
            _clusterLightCounts[clusterIndex] = totalIndices;
            totalIndices = checked(totalIndices + count);
        }

        int indexCount = checked((int)Math.Max(totalIndices, 1u));
        EnsureLength(ref _lightIndexList, indexCount);
        if (totalIndices == 0)
            _lightIndexList[0] = 0;

        for (int localIndex = 0; localIndex < pointLights.Length; localIndex++)
        {
            ClusterRange range = _pointRanges[localIndex];
            if (!range.Valid)
                continue;

            WriteLightRangeIndices(
                range.MinTileX,
                range.MinTileY,
                range.MaxTileX,
                range.MaxTileY,
                range.MinSlice,
                range.MaxSlice,
                tileCountX,
                clustersPerSlice,
                checked(directionalCount + (uint)localIndex));
        }

        for (int localIndex = 0; localIndex < spotLights.Length; localIndex++)
        {
            ClusterRange range = _spotRanges[localIndex];
            if (!range.Valid)
                continue;

            WriteLightRangeIndices(
                range.MinTileX,
                range.MinTileY,
                range.MaxTileX,
                range.MaxTileY,
                range.MinSlice,
                range.MaxSlice,
                tileCountX,
                clustersPerSlice,
                checked(spotOffset + (uint)localIndex));
        }

        uniforms = new LightGridUniforms
        {
            LightGridTileSizeX = LightGridTileSize,
            LightGridTileSizeY = LightGridTileSize,
            LightGridTileCountX = tileCountX,
            LightGridTileCountY = tileCountY,
            LightGridZParams = lightGridZParams,
            DepthSliceCount = sliceCount,
        };
        clusterLightGrid = _clusterLightGrid.AsSpan(0, clusterCount);
        lightIndexList = _lightIndexList.AsSpan(0, indexCount);
    }

    private static bool ProjectLightBoundsToTiles(
        Vector3 position,
        float range,
        in Matrix4x4 viewProj,
        in Matrix4x4 view,
        Vector4 lightGridZParams,
        uint depthSliceCount,
        Vector3 cameraPosition,
        uint screenWidth,
        uint screenHeight,
        out uint minTileX,
        out uint minTileY,
        out uint maxTileX,
        out uint maxTileY,
        out uint minSlice,
        out uint maxSlice)
    {
        minSlice = 0;
        maxSlice = 0;
        if (range <= 0.0f
            || !ProjectSphereBounds(
                position,
                range,
                viewProj,
                cameraPosition,
                screenWidth,
                screenHeight,
                out minTileX,
                out minTileY,
                out maxTileX,
                out maxTileY))
        {
            minTileX = 0;
            minTileY = 0;
            maxTileX = 0;
            maxTileY = 0;
            return false;
        }

        float centerDepth = ViewDepth(position, view);
        minSlice = DepthToSlice(centerDepth - range, lightGridZParams, depthSliceCount);
        maxSlice = DepthToSlice(centerDepth + range, lightGridZParams, depthSliceCount);
        return true;
    }

    private void AddLightRangeCounts(
        uint minTileX,
        uint minTileY,
        uint maxTileX,
        uint maxTileY,
        uint minSlice,
        uint maxSlice,
        uint tileCountX,
        uint clustersPerSlice)
    {
        for (uint slice = minSlice; slice <= maxSlice; slice++)
        {
            int sliceOffset = checked((int)(slice * clustersPerSlice));
            for (uint tileY = minTileY; tileY <= maxTileY; tileY++)
            {
                int rowOffset = checked(sliceOffset + (int)(tileY * tileCountX));
                int start = checked(rowOffset + (int)minTileX);
                int end = checked(rowOffset + (int)maxTileX);
                for (int clusterIndex = start; clusterIndex <= end; clusterIndex++)
                    _clusterLightCounts[clusterIndex]++;
            }
        }
    }

    private void WriteLightRangeIndices(
        uint minTileX,
        uint minTileY,
        uint maxTileX,
        uint maxTileY,
        uint minSlice,
        uint maxSlice,
        uint tileCountX,
        uint clustersPerSlice,
        uint lightIndex)
    {
        for (uint slice = minSlice; slice <= maxSlice; slice++)
        {
            int sliceOffset = checked((int)(slice * clustersPerSlice));
            for (uint tileY = minTileY; tileY <= maxTileY; tileY++)
            {
                int rowOffset = checked(sliceOffset + (int)(tileY * tileCountX));
                int start = checked(rowOffset + (int)minTileX);
                int end = checked(rowOffset + (int)maxTileX);
                for (int clusterIndex = start; clusterIndex <= end; clusterIndex++)
                {
                    int writeOffset = checked((int)_clusterLightCounts[clusterIndex]++);
                    _lightIndexList[writeOffset] = lightIndex;
                }
            }
        }
    }

    private void GetDirectionalLightGrid(
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount,
        out ReadOnlySpan<ClusterLightGrid> clusterLightGrid,
        out ReadOnlySpan<uint> lightIndexList,
        out LightGridUniforms uniforms)
    {
        uint width = Math.Max(screenWidth, 1u);
        uint height = Math.Max(screenHeight, 1u);
        uint sliceCount = Math.Max(depthSliceCount, 1u);
        uint tileCountX = DivRoundUp(width, LightGridTileSize);
        uint tileCountY = DivRoundUp(height, LightGridTileSize);
        int clusterCount = checked((int)(tileCountX * tileCountY * sliceCount));
        const int indexCount = 1;
        var key = new DirectionalGridKey(width, height, sliceCount);
        if (!_directionalGridValid || _directionalGridKey != key)
        {
            EnsureLength(ref _clusterLightGrid, clusterCount);
            Array.Clear(_clusterLightGrid, 0, clusterCount);

            EnsureLength(ref _lightIndexList, indexCount);
            _lightIndexList[0] = 0;

            _directionalGridUniforms = new LightGridUniforms
            {
                LightGridTileSizeX = LightGridTileSize,
                LightGridTileSizeY = LightGridTileSize,
                LightGridTileCountX = tileCountX,
                LightGridTileCountY = tileCountY,
                LightGridZParams = new Vector4(0.0f, 1.0f, 1.0f, sliceCount),
                DepthSliceCount = sliceCount,
            };
            _directionalGridKey = key;
            _directionalGridValid = true;
        }

        uniforms = _directionalGridUniforms;
        clusterLightGrid = _clusterLightGrid.AsSpan(0, clusterCount);
        lightIndexList = _lightIndexList.AsSpan(0, indexCount);
    }

    private bool EnsureBuffer(
        ref BufferHandle buffer,
        ref int capacityBytes,
        BufferDesc desc,
        FlatDictionary<BufferViewKey, BufferViewHandle> views,
        ref ResourceState state)
    {
        int requiredBytes = checked((int)desc.SizeInBytes);
        if (buffer.IsValid && capacityBytes >= requiredBytes)
            return false;

        if (buffer.IsValid)
        {
            _device.WaitIdle();
            ClearBindings();
            DestroyBuffer(ref buffer, views);
        }

        capacityBytes = requiredBytes;
        state = ResourceState.Common;
        buffer = _device.CreateBuffer(desc);
        return true;
    }

    private void EnsureUploadBuffer(
        ref BufferHandle buffer,
        ref int capacityBytes,
        string name,
        int requiredBytes)
    {
        if (buffer.IsValid && capacityBytes >= requiredBytes)
            return;

        if (buffer.IsValid)
        {
            _device.WaitIdle();
            _device.Destroy(buffer);
        }

        capacityBytes = Math.Max(requiredBytes, 1);
        buffer = _device.CreateBuffer(
            new BufferDesc
            {
                Name = name,
                SizeInBytes = checked((ulong)capacityBytes),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
    }

    private void WriteUploadBuffer(BufferHandle buffer, ReadOnlySpan<byte> data)
    {
        Memory<byte> mapped = _device.MapBuffer(buffer, MapMode.Write, 0, data.Length);
        data.CopyTo(mapped.Span);
        _device.UnmapBuffer(buffer);
    }

    private void UploadBufferCopy(
        RenderGraph graph,
        string name,
        RenderGraphHandle destination,
        ReadOnlySpan<byte> data,
        ref BufferHandle uploadBuffer,
        ref int uploadBytes)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (data.IsEmpty)
            throw new ArgumentException("light upload data must not be empty.", nameof(data));

        string sourceName = $"{name} Source";
        EnsureUploadBuffer(ref uploadBuffer, ref uploadBytes, sourceName, data.Length);
        WriteUploadBuffer(uploadBuffer, data);
        RenderGraphHandle source = graph.ImportBuffer(
            sourceName,
            uploadBuffer,
            new BufferDesc
            {
                Name = sourceName,
                SizeInBytes = checked((ulong)data.Length),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            new ImportDesc(ResourceState.CopySource));
        BufferCopyPasses.AddCopyPass(
            graph,
            name,
            source,
            destination,
            0,
            0,
            checked((ulong)data.Length));
    }

    private void UploadIfChanged(
        RenderGraph graph,
        string name,
        RenderGraphHandle destination,
        ReadOnlySpan<byte> data,
        ref BufferHandle uploadBuffer,
        ref int uploadBytes,
        ref byte[] snapshot,
        bool force)
    {
        if (!force && data.SequenceEqual(snapshot))
            return;

        UploadBufferCopy(graph, name, destination, data, ref uploadBuffer, ref uploadBytes);
        if (snapshot.Length != data.Length)
            snapshot = new byte[data.Length];
        data.CopyTo(snapshot);
    }

    private void UploadLightBuffer(
        RenderGraph graph,
        RenderGraphHandle destination,
        ReadOnlySpan<byte> data,
        uint lightVersion,
        bool force)
    {
        if (lightVersion == 0)
        {
            _lightUploadVersion = 0;
            UploadIfChanged(
                graph,
                "LightBuffer Upload",
                destination,
                data,
                ref _lightUploadBuffer,
                ref _lightUploadBufferBytes,
                ref _lightSnapshot,
                force);
            return;
        }

        if (force || _lightUploadVersion != lightVersion)
        {
            UploadBufferCopy(
                graph,
                "LightBuffer Upload",
                destination,
                data,
                ref _lightUploadBuffer,
                ref _lightUploadBufferBytes);
        }
        _lightUploadVersion = lightVersion;
        _lightSnapshot = [];
    }

    private void UploadLightGrid(
        RenderGraph graph,
        RenderGraphHandle clusterGrid,
        ReadOnlySpan<byte> clusterGridBytes,
        RenderGraphHandle indexList,
        ReadOnlySpan<byte> indexListBytes,
        LightGridUploadKey key,
        bool gridCreated,
        bool indexCreated)
    {
        if (key.LightVersion == 0)
        {
            _gridUploadKeyValid = false;
            UploadIfChanged(
                graph,
                "ClusterLightGrid Upload",
                clusterGrid,
                clusterGridBytes,
                ref _clusterGridUploadBuffer,
                ref _clusterGridUploadBufferBytes,
                ref _clusterGridSnapshot,
                gridCreated);
            UploadIfChanged(
                graph,
                "LightIndexList Upload",
                indexList,
                indexListBytes,
                ref _indexListUploadBuffer,
                ref _indexListUploadBufferBytes,
                ref _indexListSnapshot,
                indexCreated);
            return;
        }

        bool changed = !_gridUploadKeyValid || _gridUploadKey != key;
        if (gridCreated || changed)
        {
            UploadBufferCopy(
                graph,
                "ClusterLightGrid Upload",
                clusterGrid,
                clusterGridBytes,
                ref _clusterGridUploadBuffer,
                ref _clusterGridUploadBufferBytes);
        }
        if (indexCreated || changed)
        {
            UploadBufferCopy(
                graph,
                "LightIndexList Upload",
                indexList,
                indexListBytes,
                ref _indexListUploadBuffer,
                ref _indexListUploadBufferBytes);
        }

        _gridUploadKey = key;
        _gridUploadKeyValid = true;
        _clusterGridSnapshot = [];
        _indexListSnapshot = [];
    }

    private static BufferDesc LightDesc(int bytes)
        => new()
        {
            Name = "LightBuffer",
            SizeInBytes = checked((ulong)Math.Max(bytes, GPULight.SizeInBytes)),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = GPULight.SizeInBytes,
        };

    private static BufferDesc ClusterGridDesc(int bytes)
        => new()
        {
            Name = "ClusterLightGrid",
            SizeInBytes = checked((ulong)Math.Max(bytes, ClusterLightGrid.SizeInBytes)),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = ClusterLightGrid.SizeInBytes,
        };

    private static BufferDesc IndexListDesc(int bytes)
        => new()
        {
            Name = "LightIndexList",
            SizeInBytes = checked((ulong)Math.Max(bytes, sizeof(uint))),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = sizeof(uint),
        };

    private void ClearBindings()
    {
        if (_lightViews.Count == 0 && _clusterGridViews.Count == 0 && _indexListViews.Count == 0)
            return;

        RenderGraph? graph = _frameGraph;
        if (graph is { IsDisposed: false })
            graph.ClearBindSets(waitForGpu: true);
    }

    private void DestroyBuffer(
        ref BufferHandle buffer,
        FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        DestroyViews(views);

        if (buffer.IsValid)
        {
            _device.Destroy(buffer);
            buffer = default;
        }
    }

    private void DestroyUploadBuffer(
        ref BufferHandle buffer,
        ref int capacityBytes)
    {
        if (buffer.IsValid)
        {
            _device.Destroy(buffer);
            buffer = default;
        }

        capacityBytes = 0;
    }

    private void DestroyViews(FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
            _device.Destroy(pair.Value);
        views.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LightBuffer));
    }

    private static int CheckGpuLightSize()
    {
        int size = Marshal.SizeOf<GPULight>();
        if (size != GPULight.SizeInBytes)
            throw new InvalidOperationException($"GPULight layout must stay {GPULight.SizeInBytes} bytes.");
        return size;
    }

    internal static void BuildLightGrid(
        in SceneLights lights,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount,
        uint directionalCount,
        uint pointCount,
        out ClusterLightGrid[] clusterLightGrid,
        out uint[] lightIndexList,
        out LightGridUniforms uniforms)
    {
        uint width = Math.Max(screenWidth, 1u);
        uint height = Math.Max(screenHeight, 1u);
        uint sliceCount = Math.Max(depthSliceCount, 1u);
        uint tileCountX = DivRoundUp(width, LightGridTileSize);
        uint tileCountY = DivRoundUp(height, LightGridTileSize);
        uint clustersPerSlice = checked(tileCountX * tileCountY);
        int clusterCount = checked((int)(clustersPerSlice * sliceCount));
        clusterLightGrid = new ClusterLightGrid[clusterCount];

        ReadOnlySpan<PointLight> pointLights = lights.PointLights.Span;
        ReadOnlySpan<SpotLight> spotLights = lights.SpotLights.Span;

        if (pointLights.IsEmpty && spotLights.IsEmpty)
        {
            lightIndexList = [0u];
            uniforms = new LightGridUniforms
            {
                LightGridTileSizeX = LightGridTileSize,
                LightGridTileSizeY = LightGridTileSize,
                LightGridTileCountX = tileCountX,
                LightGridTileCountY = tileCountY,
                LightGridZParams = new Vector4(0.0f, 1.0f, 1.0f, sliceCount),
                DepthSliceCount = sliceCount,
            };
            return;
        }

        Matrix4x4 viewProj = Matrix4x4.Transpose(shadeData.ViewProj);
        Matrix4x4 view = Matrix4x4.Transpose(shadeData.View);
        var indices = new List<uint>();
        Vector3 cameraPosition = shadeData.CameraPos;
        uint spotOffset = checked(directionalCount + pointCount);
        Vector4 lightGridZParams = GetLightGridZParams(pointLights, spotLights, view, sliceCount);

        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            uint offset = checked((uint)indices.Count);
            AddPointLights(
                indices,
                pointLights,
                directionalCount,
                clusterIndex,
                tileCountX,
                tileCountY,
                width,
                height,
                viewProj,
                view,
                lightGridZParams,
                sliceCount,
                cameraPosition);
            AddSpotLights(
                indices,
                spotLights,
                spotOffset,
                clusterIndex,
                tileCountX,
                tileCountY,
                width,
                height,
                viewProj,
                view,
                lightGridZParams,
                sliceCount,
                cameraPosition);
            clusterLightGrid[clusterIndex] = new ClusterLightGrid
            {
                Offset = offset,
                Count = checked((uint)indices.Count - offset),
            };
        }

        lightIndexList = indices.Count == 0 ? [0u] : [.. indices];
        uniforms = new LightGridUniforms
        {
            LightGridTileSizeX = LightGridTileSize,
            LightGridTileSizeY = LightGridTileSize,
            LightGridTileCountX = tileCountX,
            LightGridTileCountY = tileCountY,
            LightGridZParams = lightGridZParams,
            DepthSliceCount = sliceCount,
        };
    }

    private static void AddPointLights(
        List<uint> indices,
        ReadOnlySpan<PointLight> lights,
        uint lightOffset,
        int clusterIndex,
        uint tileCountX,
        uint tileCountY,
        uint screenWidth,
        uint screenHeight,
        in Matrix4x4 viewProj,
        in Matrix4x4 view,
        Vector4 lightGridZParams,
        uint depthSliceCount,
        Vector3 cameraPosition)
    {
        for (int localIndex = 0; localIndex < lights.Length; localIndex++)
        {
            PointLight light = lights[localIndex];
            if (IntersectsSphere(
                clusterIndex,
                tileCountX,
                tileCountY,
                screenWidth,
                screenHeight,
                light.Position,
                light.Range,
                viewProj,
                view,
                lightGridZParams,
                depthSliceCount,
                cameraPosition))
            {
                indices.Add(checked(lightOffset + (uint)localIndex));
            }
        }
    }

    private static void AddSpotLights(
        List<uint> indices,
        ReadOnlySpan<SpotLight> lights,
        uint lightOffset,
        int clusterIndex,
        uint tileCountX,
        uint tileCountY,
        uint screenWidth,
        uint screenHeight,
        in Matrix4x4 viewProj,
        in Matrix4x4 view,
        Vector4 lightGridZParams,
        uint depthSliceCount,
        Vector3 cameraPosition)
    {
        for (int localIndex = 0; localIndex < lights.Length; localIndex++)
        {
            SpotLight light = lights[localIndex];
            if (IntersectsSphere(
                clusterIndex,
                tileCountX,
                tileCountY,
                screenWidth,
                screenHeight,
                light.Position,
                light.Range,
                viewProj,
                view,
                lightGridZParams,
                depthSliceCount,
                cameraPosition))
            {
                indices.Add(checked(lightOffset + (uint)localIndex));
            }
        }
    }

    private static bool IntersectsSphere(
        int clusterIndex,
        uint tileCountX,
        uint tileCountY,
        uint screenWidth,
        uint screenHeight,
        Vector3 center,
        float radius,
        in Matrix4x4 viewProj,
        in Matrix4x4 view,
        Vector4 lightGridZParams,
        uint depthSliceCount,
        Vector3 cameraPosition)
    {
        if (radius <= 0.0f)
            return false;

        if (!ProjectSphereBounds(
            center,
            radius,
            viewProj,
            cameraPosition,
            screenWidth,
            screenHeight,
            out uint minTileX,
            out uint minTileY,
            out uint maxTileX,
            out uint maxTileY))
        {
            return false;
        }

        uint cluster = checked((uint)clusterIndex);
        uint clustersPerSlice = checked(tileCountX * tileCountY);
        uint sliceIndex = cluster / clustersPerSlice;
        uint tile = cluster % clustersPerSlice;
        uint tileX = tile % tileCountX;
        uint tileY = tile / tileCountX;
        return tileX >= minTileX
            && tileX <= Math.Min(maxTileX, tileCountX - 1u)
            && tileY >= minTileY
            && tileY <= Math.Min(maxTileY, tileCountY - 1u)
            && IntersectsDepthSlice(center, radius, view, lightGridZParams, depthSliceCount, sliceIndex);
    }

    private static bool IntersectsDepthSlice(
        Vector3 center,
        float radius,
        in Matrix4x4 view,
        Vector4 lightGridZParams,
        uint depthSliceCount,
        uint sliceIndex)
    {
        float centerDepth = ViewDepth(center, view);
        uint minSlice = DepthToSlice(centerDepth - radius, lightGridZParams, depthSliceCount);
        uint maxSlice = DepthToSlice(centerDepth + radius, lightGridZParams, depthSliceCount);
        return sliceIndex >= minSlice && sliceIndex <= maxSlice;
    }

    private static Vector4 GetLightGridZParams(
        ReadOnlySpan<PointLight> pointLights,
        ReadOnlySpan<SpotLight> spotLights,
        in Matrix4x4 view,
        uint depthSliceCount)
    {
        float minDepth = float.PositiveInfinity;
        float maxDepth = float.NegativeInfinity;

        for (int i = 0; i < pointLights.Length; i++)
        {
            PointLight light = pointLights[i];
            AddDepthBounds(light.Position, light.Range, view, ref minDepth, ref maxDepth);
        }

        for (int i = 0; i < spotLights.Length; i++)
        {
            SpotLight light = spotLights[i];
            AddDepthBounds(light.Position, light.Range, view, ref minDepth, ref maxDepth);
        }

        if (!float.IsFinite(minDepth) || !float.IsFinite(maxDepth))
        {
            minDepth = 0.0f;
            maxDepth = 1.0f;
        }

        if (maxDepth <= minDepth)
            maxDepth = minDepth + 1.0f;

        return new Vector4(
            minDepth,
            maxDepth,
            1.0f / (maxDepth - minDepth),
            depthSliceCount);
    }

    private static void AddDepthBounds(
        Vector3 position,
        float range,
        in Matrix4x4 view,
        ref float minDepth,
        ref float maxDepth)
    {
        if (range <= 0.0f)
            return;

        float depth = ViewDepth(position, view);
        minDepth = MathF.Min(minDepth, depth - range);
        maxDepth = MathF.Max(maxDepth, depth + range);
    }

    private static float ViewDepth(Vector3 position, in Matrix4x4 view)
        => Vector4.Transform(new Vector4(position, 1.0f), view).Z;

    private static uint DepthToSlice(float depth, Vector4 lightGridZParams, uint depthSliceCount)
    {
        float normalizedDepth = Math.Clamp(
            (depth - lightGridZParams.X) * lightGridZParams.Z,
            0.0f,
            0.99999994f);
        return Math.Min((uint)(normalizedDepth * lightGridZParams.W), depthSliceCount - 1u);
    }

    private static bool ProjectSphereBounds(
        Vector3 center,
        float radius,
        in Matrix4x4 viewProj,
        Vector3 cameraPosition,
        uint screenWidth,
        uint screenHeight,
        out uint minTileX,
        out uint minTileY,
        out uint maxTileX,
        out uint maxTileY)
    {
        minTileX = 0;
        minTileY = 0;
        maxTileX = DivRoundUp(screenWidth, LightGridTileSize) - 1u;
        maxTileY = DivRoundUp(screenHeight, LightGridTileSize) - 1u;

        if (Vector3.DistanceSquared(cameraPosition, center) <= radius * radius)
            return true;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        bool hasProjectedPoint = false;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + new Vector3(x * radius, y * radius, z * radius);
                    if (!TryProject(corner, viewProj, screenWidth, screenHeight, out Vector2 pixel))
                        continue;

                    minX = MathF.Min(minX, pixel.X);
                    minY = MathF.Min(minY, pixel.Y);
                    maxX = MathF.Max(maxX, pixel.X);
                    maxY = MathF.Max(maxY, pixel.Y);
                    hasProjectedPoint = true;
                }
            }
        }

        if (!hasProjectedPoint)
            return false;

        minTileX = PixelToTile(MathF.Floor(minX), screenWidth);
        minTileY = PixelToTile(MathF.Floor(minY), screenHeight);
        maxTileX = PixelToTile(MathF.Ceiling(maxX), screenWidth);
        maxTileY = PixelToTile(MathF.Ceiling(maxY), screenHeight);
        return true;
    }

    private static bool TryProject(
        Vector3 position,
        in Matrix4x4 viewProj,
        uint screenWidth,
        uint screenHeight,
        out Vector2 pixel)
    {
        Vector4 clip = Vector4.Transform(new Vector4(position, 1.0f), viewProj);
        if (clip.W <= 0.0001f)
        {
            pixel = default;
            return false;
        }

        float invW = 1.0f / clip.W;
        float ndcX = clip.X * invW;
        float ndcY = clip.Y * invW;
        pixel = new Vector2(
            (ndcX * 0.5f + 0.5f) * screenWidth,
            (1.0f - (ndcY * 0.5f + 0.5f)) * screenHeight);
        return true;
    }

    private static uint PixelToTile(float pixel, uint extent)
    {
        float maxPixel = Math.Max(extent, 1u) - 1u;
        float clamped = Math.Clamp(pixel, 0.0f, maxPixel);
        return (uint)(clamped / LightGridTileSize);
    }

    private static void EnsureLength<T>(ref T[] values, int length)
    {
        if (values.Length < length)
            Array.Resize(ref values, length);
    }

    private static uint DivRoundUp(uint value, uint divisor)
        => checked((value + divisor - 1u) / divisor);

    private readonly record struct DirectionalGridKey(
        uint Width,
        uint Height,
        uint DepthSliceCount);

    private readonly record struct LightGridUploadKey(
        uint Width,
        uint Height,
        uint DepthSliceCount,
        uint LightVersion,
        uint DirectionalCount,
        Matrix4x4 View,
        Matrix4x4 ViewProj,
        Vector3 CameraPosition);

}
