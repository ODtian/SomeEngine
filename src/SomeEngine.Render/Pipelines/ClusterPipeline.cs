using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public enum ClusterDebugMode
{
    None,
    ClusterID,
    LODLevel,
}

[StructLayout(LayoutKind.Sequential)]
public struct CullingUniforms
{
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos;
    public float LodThreshold;
    public float LodScale;
    public uint MaxQueueNodes;
    public uint MaxCandidates;
    public uint Pad2;
    public int ForcedLODLevel;
    public uint InstanceCount;
    public uint DebugMode;
    public uint VisualiseBVH;
    public int DebugBVHDepth;
    public uint CurrentDepth;
}

[StructLayout(LayoutKind.Sequential)]
public struct DrawUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public uint PageTableSize;
    public uint DebugMode;
    public Vector2 Pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct CopyUniforms
{
    public uint SphereVertexCount;
    public uint Pad0,
        Pad1,
        Pad2;
}

public class ClusterPipeline(
    RenderContext context,
    InstanceSyncSystem transformSystem,
    ClusterResourceManager clusterManager
) : IDisposable
{
    private IBuffer? _cullingUniformBuffer;
    private IBuffer? _drawUniformBuffer;
    private IBuffer? _copyUniformBuffer;

    private ClusterBVHTraversePass? _bvhTraversePass;
    private ClusterCullPass? _cullPass;
    private ClusterDrawPass? _drawPass;
    private ClusterDebugPass? _debugPass;
    private readonly ClusterStreamer _clusterStreamer = new(clusterManager);

    private bool _initialized;
    internal const uint MaxDraws = 2500000;
    private uint _maxDraws = MaxDraws;

    public ClusterDebugMode DebugMode { get; set; } = ClusterDebugMode.None;
    public bool WireframeEnabled { get; set; }
    public bool OverdrawEnabled { get; set; }
    public bool DebugSpheresEnabled { get; set; }
    public bool VisualiseBVH { get; set; }
    public int DebugBVHDepth { get; set; } = -1;
    public bool BypassCulling { get; set; }

    public bool DebugClusterID
    {
        get => DebugMode == ClusterDebugMode.ClusterID;
        set
        {
            if (value)
                DebugMode = ClusterDebugMode.ClusterID;
            else if (DebugMode == ClusterDebugMode.ClusterID)
                DebugMode = ClusterDebugMode.None;
        }
    }

    public bool DebugLOD
    {
        get => DebugMode == ClusterDebugMode.LODLevel;
        set
        {
            if (value)
                DebugMode = ClusterDebugMode.LODLevel;
            else if (DebugMode == ClusterDebugMode.LODLevel)
                DebugMode = ClusterDebugMode.None;
        }
    }

    public uint[] DebugBVHGroupCount => _bvhTraversePass?.DebugBVHGroupCount ?? Array.Empty<uint>();
    public uint[] DebugBVHItemCount => _bvhTraversePass?.DebugBVHItemCount ?? Array.Empty<uint>();
    public uint LastPageFaultCount => _clusterStreamer.LastFrameFaultCount;
    public uint LastLoadedPageCount => _clusterStreamer.LastFrameLoadedPages;

    private Matrix4x4 _view = Matrix4x4.Identity;
    private Matrix4x4 _proj = Matrix4x4.Identity;
    private Vector3 _cameraPos;
    private float _lodThreshold = 1.0f,
        _lodScale = 500.0f;
    private int _forcedLODLevel = -1;

    public void SetCamera(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        in Vector3 cameraPos,
        float lodThreshold,
        float lodScale,
        int forcedLODLevel = -1
    )
    {
        _view = view;
        _proj = proj;
        _cameraPos = cameraPos;
        _lodThreshold = lodThreshold;
        _lodScale = lodScale;
        _forcedLODLevel = forcedLODLevel;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        _cullingUniformBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "Culling Uniforms",
                Size = (ulong)Marshal.SizeOf<CullingUniforms>(),
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        _drawUniformBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "Draw Uniforms",
                Size = 256,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        _copyUniformBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "Copy Uniforms",
                Size = 16,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );

        _bvhTraversePass = new ClusterBVHTraversePass(
            context,
            clusterManager,
            transformSystem,
            faults =>
            {
                _clusterStreamer.EnqueueFaultNodes(faults);
                _clusterStreamer.Update();
            }
        );
        _bvhTraversePass.Init();
        _cullPass = new ClusterCullPass(context, clusterManager, transformSystem);
        _cullPass.Init();
        _drawPass = new ClusterDrawPass(context, clusterManager, transformSystem);
        _drawPass.Init();
        _debugPass = new ClusterDebugPass(context, clusterManager);
        _debugPass.Init();

        _initialized = true;
    }

    public void AddToRenderGraph(
        RenderGraph graph,
        RGResourceHandle colorTarget,
        RGResourceHandle depthTarget
    )
    {
        if (!_initialized)
            Init();
        if (!_initialized)
            return;

        UpdateUniforms();

        // Create transient RenderGraph buffers
        var hCandidateClusters = graph.CreateBuffer(
            "CandidateClusters",
            new BufferDesc
            {
                Size = (ulong)(_maxDraws * 12),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 12,
            }
        );
        var hCandidateArgs = graph.CreateBuffer(
            "CandidateArgs",
            new BufferDesc
            {
                Size = 16,
                BindFlags =
                    BindFlags.UnorderedAccess
                    | BindFlags.IndirectDrawArgs
                    | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );
        var hCandidateCount = graph.CreateBuffer(
            "CandidateCount",
            new BufferDesc
            {
                Size = 4,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );
        var hIndirectDrawArgs = graph.CreateBuffer(
            "IndirectDrawArgs",
            new BufferDesc
            {
                Size = 256,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                Mode = BufferMode.Raw,
            }
        );
        var hVisibleClusters = graph.CreateBuffer(
            "VisibleClusters",
            new BufferDesc
            {
                Size = (ulong)(_maxDraws * 16),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 16,
            }
        );
        var hBvhDebug = graph.CreateBuffer(
            "BVHDebug",
            new BufferDesc
            {
                Size = (ulong)(32 * _maxDraws),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 32,
            }
        );
        var hBvhDebugCount = graph.CreateBuffer(
            "BVHDebugCount",
            new BufferDesc
            {
                Size = 256,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                Mode = BufferMode.Raw,
            }
        );

        // Import persistent uniform buffers
        if (clusterManager.PageFaultBuffer == null)
            return;

        var hCullingUB = graph.ImportBuffer(
            "CullingUniforms",
            _cullingUniformBuffer!,
            ResourceState.ConstantBuffer
        );
        var hDrawUB = graph.ImportBuffer(
            "DrawUniforms",
            _drawUniformBuffer!,
            ResourceState.ConstantBuffer
        );
        var hCopyUB = graph.ImportBuffer(
            "CopyUniforms",
            _copyUniformBuffer!,
            ResourceState.ConstantBuffer
        );
        var hPageFaultBuffer = graph.ImportBuffer(
            "PageFaultBuffer",
            clusterManager.PageFaultBuffer,
            ResourceState.UnorderedAccess
        );

        // Wire BVH Traverse pass
        _bvhTraversePass!.HCandidateClusters = hCandidateClusters;
        _bvhTraversePass.HCandidateArgs = hCandidateArgs;
        _bvhTraversePass.HCandidateCount = hCandidateCount;
        _bvhTraversePass.HIndirectDrawArgs = hIndirectDrawArgs;
        _bvhTraversePass.HBvhDebugBuffer = hBvhDebug;
        _bvhTraversePass.HBvhDebugCountBuffer = hBvhDebugCount;
        _bvhTraversePass.HPageFaultBuffer = hPageFaultBuffer;
        _bvhTraversePass.HCullingUniforms = hCullingUB;
        _bvhTraversePass.SetFrameData(
            _cullingUniformBuffer!,
            _view,
            _proj,
            _cameraPos,
            _lodThreshold,
            _lodScale,
            _forcedLODLevel,
            BypassCulling,
            VisualiseBVH,
            DebugBVHDepth
        );
        graph.AddPass(_bvhTraversePass);

        // Wire Cull pass
        _cullPass!.HCandidateClusters = hCandidateClusters;
        _cullPass.HCandidateArgs = hCandidateArgs;
        _cullPass.HCandidateCount = hCandidateCount;
        _cullPass.HVisibleClusters = hVisibleClusters;
        _cullPass.HIndirectDrawArgs = hIndirectDrawArgs;
        _cullPass.HCullingUniforms = hCullingUB;
        _cullPass.SetFrameData(_cullingUniformBuffer!);
        graph.AddPass(_cullPass);

        // Wire Draw pass
        _drawPass!.HVisibleClusters = hVisibleClusters;
        _drawPass.HIndirectDrawArgs = hIndirectDrawArgs;
        _drawPass.HColorTarget = colorTarget;
        _drawPass.HDepthTarget = depthTarget;
        _drawPass.HDrawUniforms = hDrawUB;
        _drawPass.SetFrameData(_drawUniformBuffer!, DebugMode, WireframeEnabled, OverdrawEnabled);
        graph.AddPass(_drawPass);

        // Wire Debug pass
        if (VisualiseBVH || DebugSpheresEnabled)
        {
            _debugPass!.HBvhDebugBuffer = hBvhDebug;
            _debugPass.HBvhDebugCountBuffer = hBvhDebugCount;
            _debugPass.HVisibleClusters = hVisibleClusters;
            _debugPass.HIndirectDrawArgs = hIndirectDrawArgs;
            _debugPass.HColorTarget = colorTarget;
            _debugPass.HDepthTarget = depthTarget;
            _debugPass.HDrawUniforms = hDrawUB;
            _debugPass.HCopyUniforms = hCopyUB;
            _debugPass.SetFrameData(
                _drawUniformBuffer!,
                _copyUniformBuffer!,
                VisualiseBVH,
                DebugSpheresEnabled
            );
            graph.AddPass(_debugPass);
        }
    }

    private void UpdateUniforms()
    {
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var viewProjT = Matrix4x4.Transpose(_view * _proj);
        var viewT = Matrix4x4.Transpose(_view);

        var cSpan = ctx.MapBuffer<CullingUniforms>(
            _cullingUniformBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        cSpan[0] = new CullingUniforms
        {
            ViewProj = viewProjT,
            CameraPos = _cameraPos,
            LodThreshold = _lodThreshold,
            LodScale = _lodScale,
            MaxQueueNodes = 4 * 1024 * 1024u,
            MaxCandidates = _maxDraws,
            ForcedLODLevel = _forcedLODLevel,
            InstanceCount = (uint)transformSystem.Count,
            DebugMode = BypassCulling ? 1u : 0u,
            VisualiseBVH = VisualiseBVH ? 1u : 0u,
            DebugBVHDepth = DebugBVHDepth,
        };
        ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);

        uint drawDebugMode = DebugClusterID ? 1u : (DebugLOD ? 2u : 0u);
        var dSpan = ctx.MapBuffer<DrawUniforms>(
            _drawUniformBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        dSpan[0] = new DrawUniforms
        {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = clusterManager.PageCount,
            DebugMode = drawDebugMode,
        };
        ctx.UnmapBuffer(_drawUniformBuffer, MapType.Write);
    }

    public void Dispose()
    {
        _bvhTraversePass?.Dispose();
        _cullPass?.Dispose();
        _drawPass?.Dispose();
        _debugPass?.Dispose();
        _cullingUniformBuffer?.Dispose();
        _drawUniformBuffer?.Dispose();
        _copyUniformBuffer?.Dispose();
    }
}
