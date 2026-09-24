using System.Runtime.InteropServices;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterDebugFeature : IRenderFeature
{
    private const int CounterReadbackWordCount = 14;
    private const int CounterReadbackByteCount = CounterReadbackWordCount * sizeof(uint);
    internal const ulong TextureDataPlacementAlignment = 512;
    private RenderContext? _context;
    private RenderGraphHandle _drawArgsHandle = RenderGraphHandle.Invalid;
    private RenderGraphHandle _candidateCountHandle = RenderGraphHandle.Invalid;
    private RenderGraphHandle _candidateArgsHandle = RenderGraphHandle.Invalid;
    private RenderGraphHandle _phase2CandidateCountHandle = RenderGraphHandle.Invalid;
    private RenderGraphHandle _phase2DrawArgsHandle = RenderGraphHandle.Invalid;
    private uint _candidateCount;
    private readonly uint[] _drawArgs = new uint[4];
    private readonly uint[] _candidateArgs = new uint[4];
    private uint _phase2Count;
    private readonly uint[] _phase2DrawArgs = new uint[4];
    private byte[]? _hiZData;
    private uint[] _hiZMipWidths = [];
    private uint[] _hiZMipHeights = [];
    private uint[] _hiZMipRowPitches = [];
    private ulong[] _hiZMipOffsets = [];
    private float[] _hiZMipMin = [];
    private float[] _hiZMipMax = [];

    public string Name => "ClusterPipeline.Debug";
    public bool CaptureCullingCounters { get; set; }
    public bool CaptureHiZ { get; set; }

    public uint CandidateCount => _candidateCount;
    public uint Phase1DrawVertexCount => _drawArgs[0];
    public uint Phase1DrawSWCount => _drawArgs[1];
    public uint Phase1DrawHWCount => _drawArgs[2];
    public uint Phase1DrawInstanceCount => _drawArgs[1] + _drawArgs[2];
    public uint CandidateDispatchX => _candidateArgs[0];
    public uint Phase2CandidateCount => _phase2Count;
    public uint Phase2DrawVertexCount => _phase2DrawArgs[0];
    public uint Phase2DrawSWCount => _phase2DrawArgs[1];
    public uint Phase2DrawHWCount => _phase2DrawArgs[2];
    public uint Phase2DrawInstanceCount => _phase2DrawArgs[1] + _phase2DrawArgs[2];
    public uint HiZMipCount => _hiZData == null ? 0u : checked((uint)_hiZMipWidths.Length);

    public void Initialize(RenderContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public void SetCounterResources(
        RenderGraphHandle drawArgs,
        RenderGraphHandle candidateCount,
        RenderGraphHandle candidateArgs,
        RenderGraphHandle phase2CandidateCount = default,
        RenderGraphHandle phase2DrawArgs = default)
    {
        _drawArgsHandle = drawArgs;
        _candidateCountHandle = candidateCount;
        _candidateArgsHandle = candidateArgs;
        _phase2CandidateCountHandle = phase2CandidateCount;
        _phase2DrawArgsHandle = phase2DrawArgs;
    }

    public void ClearCounterResources()
    {
        _drawArgsHandle = RenderGraphHandle.Invalid;
        _candidateCountHandle = RenderGraphHandle.Invalid;
        _candidateArgsHandle = RenderGraphHandle.Invalid;
        _phase2CandidateCountHandle = RenderGraphHandle.Invalid;
        _phase2DrawArgsHandle = RenderGraphHandle.Invalid;
    }

    public void AddPasses(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (CaptureCullingCounters)
            AddCounterReadback(graph);
        if (CaptureHiZ)
            AddHiZ(graph);
    }

    public uint HiZWidth(uint mip)
        => mip < _hiZMipWidths.Length ? _hiZMipWidths[checked((int)mip)] : 0u;

    public uint HiZHeight(uint mip)
        => mip < _hiZMipHeights.Length ? _hiZMipHeights[checked((int)mip)] : 0u;

    public float HiZMin(uint mip)
        => mip < _hiZMipMin.Length ? _hiZMipMin[checked((int)mip)] : 0.0f;

    public float HiZMax(uint mip)
        => mip < _hiZMipMax.Length ? _hiZMipMax[checked((int)mip)] : 0.0f;

    public bool TryHiZ(uint mip, uint x, uint y, out float value)
    {
        value = 0.0f;
        if (_hiZData == null || mip >= _hiZMipWidths.Length)
            return false;

        int index = checked((int)mip);
        if (x >= _hiZMipWidths[index] || y >= _hiZMipHeights[index])
            return false;

        ulong byteOffset = _hiZMipOffsets[index]
            + (ulong)y * _hiZMipRowPitches[index]
            + (ulong)x * sizeof(float);
        int offset = checked((int)byteOffset);
        if (offset < 0 || offset + sizeof(float) > _hiZData.Length)
            return false;

        value = MemoryMarshal.Read<float>(_hiZData.AsSpan(offset, sizeof(float)));
        return true;
    }

    public void Dispose()
    {
        _hiZData = null;
        _hiZMipWidths = [];
        _hiZMipHeights = [];
        _hiZMipRowPitches = [];
        _hiZMipOffsets = [];
        _hiZMipMin = [];
        _hiZMipMax = [];
    }

    private void AddCounterReadback(RenderGraph graph)
    {
        RenderGraphHandle drawArgs = _drawArgsHandle;
        RenderGraphHandle candidateCount = _candidateCountHandle;
        RenderGraphHandle candidateArgs = _candidateArgsHandle;
        if (!drawArgs.IsValid || !candidateCount.IsValid || !candidateArgs.IsValid)
            return;

        RenderGraphHandle phase2CandidateCount = _phase2CandidateCountHandle;
        RenderGraphHandle phase2DrawArgs = _phase2DrawArgsHandle;
        bool hasPhase2Count = phase2CandidateCount.IsValid;
        bool hasPhase2DrawArgs = phase2DrawArgs.IsValid;
        var readback = graph.CreateBuffer(
            "ClusterDebug.CounterReadback",
            new BufferDesc
            {
                Name = "ClusterDebug.CounterReadback",
                SizeInBytes = CounterReadbackByteCount,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
                Raw = true,
            });

        graph.AddCopyPass(
            "Cluster Debug Counters Readback",
            builder =>
            {
                builder.Read(drawArgs, ResourceState.CopySource);
                builder.Read(candidateCount, ResourceState.CopySource);
                builder.Read(candidateArgs, ResourceState.CopySource);
                if (hasPhase2Count)
                    builder.Read(phase2CandidateCount, ResourceState.CopySource);
                if (hasPhase2DrawArgs)
                    builder.Read(phase2DrawArgs, ResourceState.CopySource);
                builder.Write(readback, ResourceState.CopyDestination);
            },
            context =>
            {
                context.CopyBuffer(candidateCount, 0, readback, 0, 4);
                context.CopyBuffer(drawArgs, 0, readback, 4, 16);
                context.CopyBuffer(candidateArgs, 0, readback, 20, 16);
                if (hasPhase2Count)
                    context.CopyBuffer(phase2CandidateCount, 0, readback, 36, 4);
                if (hasPhase2DrawArgs)
                    context.CopyBuffer(phase2DrawArgs, 0, readback, 40, 16);
            });

        graph.ExtractBuffer(
            readback,
            ResourceState.CopyDestination,
            (buffer, _) => ReadCounters(buffer, hasPhase2Count, hasPhase2DrawArgs));
    }

    private void AddHiZ(RenderGraph graph)
    {
        RenderGraphHandle hiZ = FindHiZ(graph);
        if (!hiZ.IsValid)
        {
            ClearHiZ();
            return;
        }

        TextureDesc desc = graph.GetTextureDesc(hiZ);
        if (desc.Format != Format.R32Float || desc.MipLevels == 0)
        {
            ClearHiZ();
            return;
        }

        uint mipCount = desc.MipLevels;
        int mipLength = checked((int)mipCount);
        uint[] widths = new uint[mipLength];
        uint[] heights = new uint[mipLength];
        uint[] rowPitches = new uint[mipLength];
        ulong[] offsets = new ulong[mipLength];
        ulong rowPitchAlignment = _context?.GraphicsDevice?.Limits.TextureRowPitchAlignment ?? 256ul;
        ulong byteSize = HiZLayout(desc, rowPitchAlignment, widths, heights, rowPitches, offsets);

        var readback = graph.CreateBuffer(
            "ClusterDebug.HiZReadback",
            new BufferDesc
            {
                Name = "ClusterDebug.HiZReadback",
                SizeInBytes = byteSize,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
                Raw = true,
            });

        graph.AddCopyPass(
            "Cluster Debug HiZ Readback",
            builder =>
            {
                for (uint mip = 0; mip < mipCount; mip++)
                    builder.Read(hiZ, ResourceState.CopySource, SubResourceRange.Mip(mip));
                builder.Write(readback, ResourceState.CopyDestination);
            },
            context =>
            {
                for (uint mip = 0; mip < mipCount; mip++)
                {
                    int index = checked((int)mip);
                    uint width = widths[index];
                    uint height = heights[index];
                    var sourceRegion = new TextureCopyRegion(mip, 0, 0, 0, 0, width, height, 1);
                    var destinationRegion = new BufferTextureCopy(
                        offsets[index],
                        rowPitches[index],
                        checked(rowPitches[index] * height));
                    context.CopyToBuffer(hiZ, sourceRegion, readback, destinationRegion);
                }
            });

        graph.ExtractBuffer(
            readback,
            ResourceState.CopyDestination,
            (buffer, _) => ReadHiZ(buffer, byteSize, widths, heights, rowPitches, offsets));
    }

    private void ReadCounters(
        BufferHandle readback,
        bool hasPhase2Count,
        bool hasPhase2DrawArgs)
    {
        IDevice device = RequireDevice();
        bool mapped = false;
        try
        {
            Memory<byte> bytes = device.MapBuffer(readback, MapMode.Read, 0, CounterReadbackByteCount);
            mapped = true;
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes.Span);
            if (words.Length < CounterReadbackWordCount)
                return;

            _candidateCount = words[0];
            _drawArgs[0] = words[1];
            _drawArgs[1] = words[2];
            _drawArgs[2] = words[3];
            _drawArgs[3] = words[4];
            _candidateArgs[0] = words[5];
            _candidateArgs[1] = words[6];
            _candidateArgs[2] = words[7];
            _candidateArgs[3] = words[8];
            _phase2Count = hasPhase2Count ? words[9] : 0;
            if (hasPhase2DrawArgs)
            {
                _phase2DrawArgs[0] = words[10];
                _phase2DrawArgs[1] = words[11];
                _phase2DrawArgs[2] = words[12];
                _phase2DrawArgs[3] = words[13];
            }
            else
            {
                Array.Clear(_phase2DrawArgs);
            }
        }
        finally
        {
            if (mapped)
                device.UnmapBuffer(readback);
            device.Destroy(readback);
        }
    }

    private void ReadHiZ(
        BufferHandle readback,
        ulong byteSize,
        uint[] widths,
        uint[] heights,
        uint[] rowPitches,
        ulong[] offsets)
    {
        IDevice device = RequireDevice();
        bool mapped = false;
        try
        {
            Memory<byte> bytes = device.MapBuffer(readback, MapMode.Read, 0, checked((int)byteSize));
            mapped = true;
            _hiZData = bytes.Span.ToArray();
            _hiZMipWidths = widths;
            _hiZMipHeights = heights;
            _hiZMipRowPitches = rowPitches;
            _hiZMipOffsets = offsets;
            HiZRanges(_hiZData, widths, heights, rowPitches, offsets, out _hiZMipMin, out _hiZMipMax);
        }
        finally
        {
            if (mapped)
                device.UnmapBuffer(readback);
            device.Destroy(readback);
        }
    }

    private IDevice RequireDevice()
        => _context?.GraphicsDevice
            ?? throw new InvalidOperationException("cluster debug feature requires an initialized RHI device.");

    private void ClearHiZ()
    {
        _hiZData = null;
        _hiZMipWidths = [];
        _hiZMipHeights = [];
        _hiZMipRowPitches = [];
        _hiZMipOffsets = [];
        _hiZMipMin = [];
        _hiZMipMax = [];
    }

    private static RenderGraphHandle FindHiZ(RenderGraph graph)
    {
        return graph.Blackboard.TryGet<SceneTextures>(out SceneTextures sceneTextures)
            ? sceneTextures.HiZ
            : RenderGraphHandle.Invalid;
    }

    internal static ulong HiZLayout(
        TextureDesc desc,
        ulong rowPitchAlignment,
        uint[] widths,
        uint[] heights,
        uint[] rowPitches,
        ulong[] offsets)
    {
        uint mipCount = desc.MipLevels;
        if (widths.Length < mipCount
            || heights.Length < mipCount
            || rowPitches.Length < mipCount
            || offsets.Length < mipCount)
        {
            throw new ArgumentException("HiZ readback layout arrays must cover every mip.");
        }

        ulong byteSize = 0;
        for (uint mip = 0; mip < mipCount; mip++)
        {
            int index = checked((int)mip);
            uint width = Math.Max(1u, desc.Width >> index);
            uint height = Math.Max(1u, desc.Height >> index);
            uint rowPitch = checked((uint)AlignUp((ulong)width * sizeof(float), rowPitchAlignment));
            ulong slicePitch = checked((ulong)rowPitch * height);
            widths[index] = width;
            heights[index] = height;
            rowPitches[index] = rowPitch;
            byteSize = AlignUp(byteSize, TextureDataPlacementAlignment);
            offsets[index] = byteSize;
            byteSize = checked(byteSize + slicePitch);
        }

        return byteSize;
    }

    private static void HiZRanges(
        byte[] data,
        uint[] widths,
        uint[] heights,
        uint[] rowPitches,
        ulong[] offsets,
        out float[] minValues,
        out float[] maxValues)
    {
        int mipCount = widths.Length;
        minValues = new float[mipCount];
        maxValues = new float[mipCount];

        for (int mip = 0; mip < mipCount; mip++)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            uint width = widths[mip];
            uint height = heights[mip];
            uint rowPitch = rowPitches[mip];
            ulong baseOffset = offsets[mip];

            for (uint y = 0; y < height; y++)
            {
                ulong rowOffset = baseOffset + y * rowPitch;
                for (uint x = 0; x < width; x++)
                {
                    ulong byteOffset = rowOffset + x * sizeof(float);
                    int offset = checked((int)byteOffset);
                    if (offset < 0 || offset + sizeof(float) > data.Length)
                        continue;

                    float value = MemoryMarshal.Read<float>(data.AsSpan(offset, sizeof(float)));
                    if (!float.IsFinite(value))
                        continue;

                    min = MathF.Min(min, value);
                    max = MathF.Max(max, value);
                }
            }

            if (!float.IsFinite(min) || !float.IsFinite(max))
            {
                min = 0.0f;
                max = 0.0f;
            }

            minValues[mip] = min;
            maxValues[mip] = max;
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            return value;
        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
