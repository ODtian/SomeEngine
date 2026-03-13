using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Data;
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

public enum HiZDebugMode
{
    Legacy,
    Phase1Only,
    Phase1OnlyPassAll,
    Phase1ThenHiZ,
    Full2Phase,
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
    public uint Pad3;
    public uint DumpHiZData;
    public uint CurrentDepth;
    public uint Pad5;
    public uint Pad6;

    public Matrix4x4 PrevViewProj;
    public uint HasPrevHistory;
    public uint HiZMipCount;
    public Vector2 HiZInvSize;

    public Matrix4x4 View;
    public float P00;
    public float P11;
    public Vector2 Pad7;

    public Vector3 QuantOrigin;
    public float QuantStep;

    public Matrix4x4 PrevView;
    public float PrevP00;
    public float PrevP11;
    public Vector2 Pad8;
}

[StructLayout(LayoutKind.Sequential)]
public struct DrawUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public uint PageTableSize;
    public uint DebugMode;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public Vector3 QuantOrigin;
    public float QuantStep;
}

[StructLayout(LayoutKind.Sequential)]
public struct ShadeBinUniforms
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaterialCount;
    public uint Pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct ShadeUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public uint PageTableSize;
    public uint DebugMode;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public Vector3 QuantOrigin;
    public float QuantStep;
    public uint MaterialID;
    public uint MaterialCount;
    public uint Pad0;
    public uint Pad1;
    public Vector3 LightDir;
    public float LightIntensity;
    public Vector3 AmbientColor;
    public float Pad2;
    public Vector3 CameraPos;
    public float Pad3;
}

[StructLayout(LayoutKind.Sequential)]
public struct CopyUniforms
{
    public uint SphereVertexCount;
    public uint Pad0,
        Pad1,
        Pad2;
}

public class ClusterRenderFeature(
    RenderContext context,
    InstanceDataManager instanceManager,
    ClusterResourceManager clusterManager
) : IRenderFeature
{
    private ClusterBVHTraversePass? _bvhTraversePass;
    private ClusterBVHPatchPass? _bvhPatchPass;
    private ClusterCullUpdateArgsPass? _cullUpdateArgsPass;
    private ClusterCullUpdateArgsPass? _cullUpdateArgsPassPhase2;
    private ClusterCullPass? _cullPassLegacy;
    private ClusterCullPass? _cullPassPhase1;
    private ClusterCullPass? _cullPassPhase2;
    private ClusterDrawPass? _drawPassLegacy;
    private ClusterDrawPass? _drawPassPhase1;
    private ClusterDrawPass? _drawPassPhase2;
    private HiZBuildPass? _hizBuildPass;
    private ClusterDebugPass? _debugPass;
    internal ClusterDebugReadbackPass? _debugReadbackPass;
    private ClusterDebugAABBPass? _debugAABBPass;
    private ClusterResolvePass? _resolvePass;
    private ClusterShadeBinningResources? _shadeBinningResources;
    private ClusterShadeBinCountPass? _shadeBinCountPass;
    private ClusterShadeBinReservePass? _shadeBinReservePass;
    private ClusterShadeBinScatterPass? _shadeBinScatterPass;
    private ClusterMaterialShadePass? _materialShadePass;
    private readonly ClusterStreamer _clusterStreamer = new(clusterManager);

    private bool _initialized;
    internal const uint MaxDraws = 2500000;
    private uint _maxDraws = MaxDraws;

    public string Name => "Cluster Rendering";

    public ClusterDebugMode DebugMode { get; set; } = ClusterDebugMode.None;
    public bool WireframeEnabled { get; set; }
    public bool OverdrawEnabled { get; set; }
    public bool DebugSpheresEnabled { get; set; }
    public HiZDebugMode HiZMode { get; set; } = HiZDebugMode.Full2Phase;
    public bool BypassCulling { get; set; }
    public bool DumpNextFrame { get; set; }
    public bool DebugShowHiZAABBs { get; set; }
    public bool UseVisBuffer { get; set; } = true;

    // Readback data for HiZ AABB debug visualization (1-frame latency)
    private byte[]? _lastDebugHiZData;
    public ReadOnlySpan<byte> DebugHiZData => _lastDebugHiZData ?? ReadOnlySpan<byte>.Empty;

    private bool _freezeCullingCamera;
    public bool FreezeCullingCamera
    {
        get => _freezeCullingCamera;
        set
        {
            if (value && !_freezeCullingCamera)
            {
                // Capture snapshot on freeze
                _frozenView = _view;
                _frozenProj = _proj;
                _frozenCameraPos = _cameraPos;
                _frozenLodThreshold = _lodThreshold;
                _frozenLodScale = _lodScale;
                _frozenForcedLODLevel = _forcedLODLevel;
            }
            _freezeCullingCamera = value;
        }
    }

    // Frozen camera snapshot for debug culling
    private Matrix4x4 _frozenView;
    private Matrix4x4 _frozenProj;
    private Vector3 _frozenCameraPos;
    private float _frozenLodThreshold;
    private float _frozenLodScale;
    private int _frozenForcedLODLevel;

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

    public uint LastPageFaultCount => _clusterStreamer.LastFrameFaultCount;
    public uint LastLoadedPageCount => _clusterStreamer.LastFrameLoadedPages;

    public uint DebugCandidateCount => _debugReadbackPass?.CandidateCount ?? 0;
    public uint DebugDrawVertexCount => _debugReadbackPass?.DrawVertexCount ?? 0;
    public uint DebugDrawInstanceCount => _debugReadbackPass?.DrawInstanceCount ?? 0;
    public uint DebugPhase2DrawVertexCount => _debugReadbackPass?.Phase2DrawVertexCount ?? 0;
    public uint DebugPhase2DrawInstanceCount => _debugReadbackPass?.Phase2DrawInstanceCount ?? 0;
    public uint DebugCandidateArgsX => _debugReadbackPass?.CandidateArgs[0] ?? 0;
    public uint DebugPhase2Count => _debugReadbackPass?.Phase2CandidateCount ?? 0;

    private Matrix4x4 _view = Matrix4x4.Identity;
    private Matrix4x4 _proj = Matrix4x4.Identity;
    private Vector3 _cameraPos;
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private Matrix4x4 _prevView = Matrix4x4.Identity;
    private Matrix4x4 _prevProj = Matrix4x4.Identity;
    private bool _pingPong = false;
    private uint _hizWidth = 0;
    private uint _hizHeight = 0;
    private uint _hizMipCount = 0;
    private bool _hasPrevHistory = false;
    private HiZDebugMode _prevHiZMode = HiZDebugMode.Full2Phase;

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

    public void Initialize(RenderContext renderContext) => Init();

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        _bvhTraversePass = new ClusterBVHTraversePass(
            context,
            clusterManager,
            instanceManager,
            faults =>
            {
                _clusterStreamer.EnqueueFaultNodes(faults);
                _clusterStreamer.Update();
            }
        );
        _bvhTraversePass.Init();

        _bvhPatchPass = new ClusterBVHPatchPass(context);
        _bvhPatchPass.Init();

        _cullUpdateArgsPass = new ClusterCullUpdateArgsPass(context);
        _cullUpdateArgsPass.Init();
        _cullUpdateArgsPassPhase2 = new ClusterCullUpdateArgsPass(
            context,
            "Cull Update Args Phase2"
        );
        _cullUpdateArgsPassPhase2.Init();


        _cullPassLegacy = new ClusterCullPass(
            context,
            clusterManager,
            ClusterCullPhase.Legacy,
            "ClusterCull Legacy"
        );
        _cullPassLegacy.Init();
        _cullPassPhase1 = new ClusterCullPass(
            context,
            clusterManager,
            ClusterCullPhase.Phase1,
            "ClusterCull Phase1"
        );
        _cullPassPhase1.Init();
        _cullPassPhase2 = new ClusterCullPass(
            context,
            clusterManager,
            ClusterCullPhase.Phase2,
            "ClusterCull Phase2"
        );
        _cullPassPhase2.Init();

        _drawPassLegacy = new ClusterDrawPass(context, clusterManager, "ClusterDraw Legacy");
        _drawPassLegacy.Init();
        _drawPassPhase1 = new ClusterDrawPass(context, clusterManager, "ClusterDraw Phase1");
        _drawPassPhase1.Init();
        _drawPassPhase2 = new ClusterDrawPass(context, clusterManager, "ClusterDraw Phase2");
        _drawPassPhase2.Init();

        _hizBuildPass = new HiZBuildPass(context);
        _hizBuildPass.Init();
        _debugPass = new ClusterDebugPass(context);
        _debugPass.Init();
        _debugReadbackPass = new ClusterDebugReadbackPass(context);
        _resolvePass = new ClusterResolvePass(context);
        _resolvePass.Init();

        _shadeBinningResources = new ClusterShadeBinningResources();
        _shadeBinningResources.Init(context);
        _shadeBinCountPass = new ClusterShadeBinCountPass(context, _shadeBinningResources);
        _shadeBinReservePass = new ClusterShadeBinReservePass(context, _shadeBinningResources);
        _shadeBinScatterPass = new ClusterShadeBinScatterPass(context, _shadeBinningResources);
        _materialShadePass = new ClusterMaterialShadePass(context);
        _materialShadePass.Init();

        _initialized = true;
    }

    private class UploadUniformsData
    {
        public RenderGraphHandle HCullingUB;
        public RenderGraphHandle HDrawUB;
        public RenderGraphHandle HCopyUB;

        public CullingUniforms CullingData;
        public DrawUniforms DrawData;
        public CopyUniforms CopyData;
    }

    public void AddPasses(RenderGraph graph)
    {
        if (!_initialized)
            Init();
        if (!_initialized)
            return;

        var colorTarget = graph.GetResourceHandle("ColorTarget");
        var depthTarget = graph.GetResourceHandle("DepthTarget");

        UpdateHiZState();

        var hCullingUB = graph.CreateBuffer(
            "CullingUniforms",
            new BufferDesc
            {
                Size = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<CullingUniforms>(),
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        var hDrawUB = graph.CreateBuffer(
            "DrawUniforms",
            new BufferDesc
            {
                Size = 256,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        var hCopyUB = graph.CreateBuffer(
            "CopyUniforms",
            new BufferDesc
            {
                Size = 16,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );

        // Determine culling camera: use frozen snapshot if debug freeze is active
        var cullView = _freezeCullingCamera ? _frozenView : _view;
        var cullProj = _freezeCullingCamera ? _frozenProj : _proj;
        var cullCameraPos = _freezeCullingCamera ? _frozenCameraPos : _cameraPos;
        var cullLodThreshold = _freezeCullingCamera ? _frozenLodThreshold : _lodThreshold;
        var cullLodScale = _freezeCullingCamera ? _frozenLodScale : _lodScale;
        var cullForcedLOD = _freezeCullingCamera ? _frozenForcedLODLevel : _forcedLODLevel;

        var cullViewProjT = Matrix4x4.Transpose(cullView * cullProj);
        var viewProjT = Matrix4x4.Transpose(_view * _proj);
        var viewT = Matrix4x4.Transpose(_view);
        var hizInvSize =
            (_hizWidth > 0 && _hizHeight > 0)
                ? new Vector2(1.0f / _hizWidth, 1.0f / _hizHeight)
                : Vector2.Zero;
        bool hasPrevHistory = _hasPrevHistory;

        var cullingData = new CullingUniforms
        {
            ViewProj = cullViewProjT,
            CameraPos = cullCameraPos,
            LodThreshold = cullLodThreshold,
            LodScale = cullLodScale,
            MaxQueueNodes = 4 * 1024 * 1024u,
            MaxCandidates = _maxDraws,
            ForcedLODLevel = cullForcedLOD,
            InstanceCount = (uint)instanceManager.Count,
            DebugMode = BypassCulling ? 1u : 0u,
            Pad3 = 0,
            DumpHiZData = DumpNextFrame ? 1u : 0u,
            CurrentDepth = 0,
            Pad5 = DebugShowHiZAABBs ? 1u : 0u,
            PrevViewProj = _prevViewProjT,
            HasPrevHistory = hasPrevHistory ? 1u : 0u,
            HiZMipCount = _hizMipCount,
            HiZInvSize = hizInvSize,
            View = Matrix4x4.Transpose(cullView),
            P00 = cullProj.M11,
            P11 = cullProj.M22,
            Pad7 = default,
            QuantOrigin = clusterManager.QuantOrigin,
            QuantStep = clusterManager.QuantStep,
            PrevView = Matrix4x4.Transpose(_prevView),
            PrevP00 = _prevProj.M11,
            PrevP11 = _prevProj.M22,
            Pad8 = default,
        };
        uint drawDebugMode = DebugClusterID ? 1u : (DebugLOD ? 2u : 0u);
        uint screenWidth = context.SwapChain?.GetDesc().Width ?? 1;
        uint screenHeight = context.SwapChain?.GetDesc().Height ?? 1;
        var drawData = new DrawUniforms
        {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = clusterManager.PageCount,
            DebugMode = drawDebugMode,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            QuantOrigin = clusterManager.QuantOrigin,
            QuantStep = clusterManager.QuantStep,
        };
        var copyData = new CopyUniforms { SphereVertexCount = 1536 };

        graph.AddPass<UploadUniformsData>(
            "UploadUniforms",
            (builder, data) =>
            {
                data.HCullingUB = hCullingUB;
                data.HDrawUB = hDrawUB;
                data.HCopyUB = hCopyUB;
                data.CullingData = cullingData;
                data.DrawData = drawData;
                data.CopyData = copyData;

                builder.Write(hCullingUB, ResourceState.ConstantBuffer);
                builder.Write(hDrawUB, ResourceState.ConstantBuffer);
                builder.Write(hCopyUB, ResourceState.ConstantBuffer);
            },
            (rgCtx, data) =>
            {
                var ctx = rgCtx.RenderContext.ImmediateContext;
                if (ctx == null)
                    return;

                var cBuf = rgCtx.GetBuffer(data.HCullingUB);
                var dBuf = rgCtx.GetBuffer(data.HDrawUB);
                var cpBuf = rgCtx.GetBuffer(data.HCopyUB);

                if (cBuf != null)
                {
                    var cSpan = ctx.MapBuffer<CullingUniforms>(
                        cBuf,
                        MapType.Write,
                        MapFlags.Discard
                    );
                    cSpan[0] = data.CullingData;
                    ctx.UnmapBuffer(cBuf, MapType.Write);
                }
                if (dBuf != null)
                {
                    var dSpan = ctx.MapBuffer<DrawUniforms>(dBuf, MapType.Write, MapFlags.Discard);
                    dSpan[0] = data.DrawData;
                    ctx.UnmapBuffer(dBuf, MapType.Write);
                }
                if (cpBuf != null)
                {
                    var cpSpan = ctx.MapBuffer<CopyUniforms>(
                        cpBuf,
                        MapType.Write,
                        MapFlags.Discard
                    );
                    cpSpan[0] = data.CopyData;
                    ctx.UnmapBuffer(cpBuf, MapType.Write);
                }
            }
        );

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
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
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

        RenderGraphHandle hPhase2CandidateClusters = RenderGraphHandle.Invalid;
        RenderGraphHandle hPhase2CandidateCount = RenderGraphHandle.Invalid;
        RenderGraphHandle hPhase2CandidateArgs = RenderGraphHandle.Invalid;


        bool useHiZBuffers =
            HiZMode != HiZDebugMode.Legacy && HiZMode != HiZDebugMode.Phase1OnlyPassAll;

        if (useHiZBuffers)
        {
            hPhase2CandidateClusters = graph.CreateBuffer(
                "Phase2CandidateClusters",
                new BufferDesc
                {
                    Size = (ulong)(_maxDraws * 12),
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    Mode = BufferMode.Structured,
                    ElementByteStride = 12,
                }
            );
            hPhase2CandidateCount = graph.CreateBuffer(
                "Phase2CandidateCount",
                new BufferDesc
                {
                    Size = 4,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    Mode = BufferMode.Raw,
                    ElementByteStride = 4,
                }
            );
            hPhase2CandidateArgs = graph.CreateBuffer(
                "Phase2CandidateArgs",
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
        }

        // Phase 2 DrawArgs (bytes 0-15: DrawInstanced args, byte 16: Phase1 visible count = offset)
        // Always allocated so Resolve/Draw can bind unconditionally
        var hPhase2IndirectDrawArgs = graph.CreateBuffer(
            "Phase2IndirectDrawArgs",
            new BufferDesc
            {
                // 20 bytes: 4 uints for DrawInstanced + 1 uint for Phase1Offset
                Size = 256,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            }
        );
        var hBvhQueueA = graph.CreateBuffer(
            "BVHQueueA",
            new BufferDesc
            {
                Size = 4ul * 1024 * 1024 * 8,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = 8,
            }
        );
        var hBvhQueueB = graph.CreateBuffer(
            "BVHQueueB",
            new BufferDesc
            {
                Size = 4ul * 1024 * 1024 * 8,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = 8,
            }
        );
        var hBvhArgsA = graph.CreateBuffer(
            "BVHArgsA",
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
        var hBvhArgsB = graph.CreateBuffer(
            "BVHArgsB",
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
        var hBvhReadback = graph.CreateBuffer(
            "BVHReadback",
            new BufferDesc
            {
                Size = 4096,
                Usage = Usage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            }
        );
        // Mark both the readback buffer and color target as output so that
        // BuildReachablePassSet keeps BOTH the BVH readback chain AND the
        // rendering (cull→draw) chain alive.
        graph.MarkOutput(hBvhReadback);
        graph.MarkOutput(colorTarget);

        var hDebugHiZOutput = RenderGraphHandle.Invalid;
        if (DumpNextFrame || DebugShowHiZAABBs)
        {
            hDebugHiZOutput = graph.CreateBuffer(
                "DebugHiZOutput",
                new BufferDesc
                {
                    Size = 196612, // 4 + 4096 * 48
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    Mode = BufferMode.Raw,
                    ElementByteStride = 4,
                }
            );
        }
        else
        {
            // Shader requires a valid UAV bound even if DumpHiZData == 0
            hDebugHiZOutput = graph.CreateBuffer(
                "DebugHiZOutputDummy",
                new BufferDesc
                {
                    Size = 16,
                    BindFlags = BindFlags.UnorderedAccess,
                    Mode = BufferMode.Raw,
                    ElementByteStride = 4,
                }
            );
        }

        var hCurrHiZ = RenderGraphHandle.Invalid; // Current frame's HiZ (written twice: Phase 1 depth, then full depth)
        var hPrevHiZ = RenderGraphHandle.Invalid; // Previous frame's full HiZ → used by Phase 1 cull
        bool hasPrevHistoryValid = false;
        bool useHiZ = HiZMode != HiZDebugMode.Legacy && HiZMode != HiZDebugMode.Phase1OnlyPassAll;

        // Reset HiZ history when the mode changes to avoid D3D12 barrier
        // mismatch from cached textures with stale tracked state.
        if (HiZMode != _prevHiZMode)
        {
            _hasPrevHistory = false;
            _prevHiZMode = HiZMode;
        }

        // Always create HiZ textures so the RG cache tracks their D3D12
        // state every frame, preventing barrier mismatches on mode switch.
        if (_hizWidth > 0 && _hizHeight > 0 && _hizMipCount > 0)
        {
            var hizDesc = new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = _hizWidth,
                Height = _hizHeight,
                MipLevels = _hizMipCount,
                Format = TextureFormat.R32_Float,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            };

            // Ping-pong A/B: hCurrHiZ is written twice per frame
            // (Phase 1 depth first, then overwritten with full Phase 1+2 depth).
            // hPrevHiZ holds the previous frame's full HiZ for Phase 1 cull.
            string currName = _pingPong ? "HiZ_A" : "HiZ_B";
            string prevName = _pingPong ? "HiZ_B" : "HiZ_A";

            hCurrHiZ = graph.CreateTexture(currName, hizDesc with { Name = currName });
            hPrevHiZ = graph.CreateTexture(prevName, hizDesc with { Name = prevName });
            hasPrevHistoryValid = useHiZ && _hasPrevHistory && hPrevHiZ.IsValid;

            if (useHiZ)
                _pingPong = !_pingPong;
        }

        // Create RG-managed buffers from descriptors
        var hPageFaultBuffer = graph.CreateBuffer("PageFaultBuffer", clusterManager.PageFaultDesc);
        var hPageFaultReadback = graph.CreateBuffer(
            "PageFaultReadback",
            clusterManager.PageFaultReadbackDesc
        );

        // Create managed buffers for InstanceSyncSystem
        int maxInstances = Math.Max(instanceManager.Count, 1);
        var hGlobalTransform = graph.CreateBuffer(
            "GlobalTransform",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuTransform.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuTransform.SizeInBytes,
            }
        );
        var hGlobalInstanceHeader = graph.CreateBuffer(
            "GlobalInstanceHeader",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuInstanceHeader.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuInstanceHeader.SizeInBytes,
            }
        );
        var hGlobalBVH = graph.CreateBuffer("GlobalBVH", clusterManager.GlobalBVHDesc);
        var hPageHeap = graph.CreateBuffer("PageHeap", clusterManager.PageHeapDesc);

        graph.AddPass(new ClusterResourceUploadPass(clusterManager, hGlobalBVH, hPageHeap));

        var patches = clusterManager.ExtractPendingPatches();
        if (patches.Count > 0)
        {
            var hPatchBuffer = graph.CreateBuffer(
                "PatchNodeIndices",
                new BufferDesc
                {
                    Size = (ulong)patches.Count * 8, // 2 uints = 8 bytes
                    Usage = Usage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                    Mode = BufferMode.Structured,
                    ElementByteStride = 8,
                }
            );
            var hPatchUniforms = graph.CreateBuffer(
                "PatchUniforms",
                new BufferDesc
                {
                    Size = 16,
                    Usage = Usage.Dynamic,
                    BindFlags = BindFlags.UniformBuffer,
                    CPUAccessFlags = CpuAccessFlags.Write,
                }
            );

            _bvhPatchPass!.Patches = patches;
            _bvhPatchPass.HGlobalBVH = hGlobalBVH;
            _bvhPatchPass.HPatchBuffer = hPatchBuffer;
            _bvhPatchPass.HPatchUniforms = hPatchUniforms;
            graph.AddPass(_bvhPatchPass);
        }

        if (instanceManager.Count > 0)
        {
            graph.AddPass(
                new ClusterUploadInstanceDataPass(
                    instanceManager,
                    hGlobalTransform,
                    hGlobalInstanceHeader
                )
            );
        }

        graph.AddPass(
            new ClusterClearBuffersPass(
                hIndirectDrawArgs,
                hCandidateArgs,
                hCandidateCount,
                hPageFaultBuffer,
                useHiZ ? hPhase2CandidateCount : RenderGraphHandle.Invalid,
                useHiZ ? hPhase2IndirectDrawArgs : RenderGraphHandle.Invalid
            )
        );

        // Wire BVH Traverse pass (split into fine-grained passes)
        _bvhTraversePass!.HCandidateClusters = hCandidateClusters;
        _bvhTraversePass.HCandidateArgs = hCandidateArgs;
        _bvhTraversePass.HCandidateCount = hCandidateCount;
        _bvhTraversePass.HIndirectDrawArgs = hIndirectDrawArgs;
        _bvhTraversePass.HQueueA = hBvhQueueA;
        _bvhTraversePass.HQueueB = hBvhQueueB;
        _bvhTraversePass.HArgsA = hBvhArgsA;
        _bvhTraversePass.HArgsB = hBvhArgsB;
        _bvhTraversePass.HReadbackBuffer = hBvhReadback;
        _bvhTraversePass.HPageFaultBuffer = hPageFaultBuffer;
        _bvhTraversePass.HPageFaultReadbackBuffer = hPageFaultReadback;
        _bvhTraversePass.HCullingUniforms = hCullingUB;
        _bvhTraversePass.HGlobalTransformBuffer = hGlobalTransform;
        _bvhTraversePass.HGlobalInstanceHeaderBuffer = hGlobalInstanceHeader;
        _bvhTraversePass.HGlobalBVHBuffer = hGlobalBVH;
        _bvhTraversePass.HPageHeap = hPageHeap;
        _bvhTraversePass.SetFrameData(
            cullView,
            cullProj,
            cullCameraPos,
            cullLodThreshold,
            cullLodScale,
            cullForcedLOD,
            BypassCulling,
            _prevViewProjT,
            hasPrevHistory,
            _hizMipCount,
            (_hizWidth > 0 && _hizHeight > 0)
                ? new Vector2(1.0f / _hizWidth, 1.0f / _hizHeight)
                : Vector2.Zero
        );

        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, true, "BVH Clear Args A"));
        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, false, "BVH Clear Args B"));

        if (instanceManager.Count > 0)
        {
            graph.AddPass(new ClusterBVHInitQueuePass(_bvhTraversePass));
            graph.AddPass(
                new ClusterBVHUpdateArgsPass(_bvhTraversePass, true, "BVH Update Init Args")
            );

            bool currentIsA = true;
            for (int depth = 0; depth < 8; depth++)
            {
                bool nextIsA = !currentIsA;

                graph.AddPass(
                    new ClusterBVHTraverseDepthPass(
                        _bvhTraversePass,
                        currentIsA,
                        depth,
                        $"BVH Traverse D{depth}"
                    )
                );

                graph.AddPass(
                    new ClusterBVHUpdateArgsPass(
                        _bvhTraversePass,
                        nextIsA,
                        $"BVH Update Args D{depth}"
                    )
                );

                graph.AddPass(
                    new ClusterBVHClearArgsPass(
                        _bvhTraversePass,
                        currentIsA,
                        $"BVH Clear Recycle D{depth}"
                    )
                );

                currentIsA = nextIsA;
            }

            graph.AddPass(new ClusterBVHReadbackPass(_bvhTraversePass));
        }

        graph.AddPass(new ClusterBVHPageFaultCopyPass(_bvhTraversePass, hPageFaultReadback));

        // Update Args for Cull Pass
        _cullUpdateArgsPass!.HCandidateCount = hCandidateCount;
        _cullUpdateArgsPass.HCandidateArgs = hCandidateArgs;
        graph.AddPass(_cullUpdateArgsPass);

        // Create VisBuffer texture for VisBuffer rendering mode
        var hVisBuffer = RenderGraphHandle.Invalid;
        var hResolveTarget = RenderGraphHandle.Invalid;
        if (UseVisBuffer)
        {
            hVisBuffer = graph.CreateTexture(
                "VisBuffer",
                new TextureDesc
                {
                    Type = ResourceDimension.Tex2d,
                    Width = screenWidth,
                    Height = screenHeight,
                    MipLevels = 1,
                    Format = TextureFormat.R32_UInt,
                    Usage = Usage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                    ClearValue = new OptimizedClearValue
                    {
                        Format = TextureFormat.R32_UInt,
                        Color = new Vector4(0, 0, 0, 0),
                    },
                }
            );

            // Clear VisBuffer to 0 before rendering
            graph.AddPass<object>(
                "ClearVisBuffer",
                (builder, _) =>
                {
                    builder.Write(hVisBuffer, ResourceState.RenderTarget);
                },
                (rgCtx, _) =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var rtv = rgCtx.GetTextureView(hVisBuffer, TextureViewType.RenderTarget);
                    if (ctx2 != null && rtv != null)
                    {
                        ctx2.SetRenderTargets([rtv], null, ResourceStateTransitionMode.Verify);
                        ctx2.ClearRenderTarget(rtv, new Vector4(0, 0, 0, 0), ResourceStateTransitionMode.Verify);
                    }
                }
            );

            // Create intermediate resolve target with UAV support
            // (Swap chain back buffer lacks ALLOW_UNORDERED_ACCESS flag)
            hResolveTarget = graph.CreateTexture(
                "ResolveTarget",
                new TextureDesc
                {
                    Type = ResourceDimension.Tex2d,
                    Width = screenWidth,
                    Height = screenHeight,
                    MipLevels = 1,
                    Format = TextureFormat.RGBA8_UNorm,
                    Usage = Usage.Default,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget,
                }
            );
        }

        if (HiZMode == HiZDebugMode.Legacy || HiZMode == HiZDebugMode.Phase1OnlyPassAll)
        {
            // Clear debug buffer counter before cull
            if (DebugShowHiZAABBs)
            {
                graph.AddPass<object>(
                    "ClearDebugHiZ",
                    (builder, _) =>
                    {
                        builder.Write(hDebugHiZOutput, ResourceState.CopyDest);
                    },
                    (rgCtx, _) =>
                    {
                        var buf = rgCtx.GetBuffer(hDebugHiZOutput);
                        if (buf != null)
                        {
                            var ctx2 = rgCtx.RenderContext.ImmediateContext;
                            ReadOnlySpan<uint> zero = stackalloc uint[] { 0 };
                            ctx2?.UpdateBuffer(buf, 0, zero, ResourceStateTransitionMode.Verify);
                        }
                    }
                );
            }

            // Legacy Cull (or Phase1OnlyPassAll using legacy logic)
            _cullPassLegacy!.HCandidateClusters = hCandidateClusters;
            _cullPassLegacy.HCandidateArgs = hCandidateArgs;
            _cullPassLegacy.HCandidateCount = hCandidateCount;
            _cullPassLegacy.HVisibleClusters = hVisibleClusters;
            _cullPassLegacy.HIndirectDrawArgs = hIndirectDrawArgs;
            _cullPassLegacy.HCullingUniforms = hCullingUB;
            _cullPassLegacy.HGlobalTransformBuffer = hGlobalTransform;
            _cullPassLegacy.HPageHeap = hPageHeap;
            _cullPassLegacy.HDebugHiZOutput = hDebugHiZOutput;
            graph.AddPass(_cullPassLegacy);

            // Legacy Draw
            _drawPassLegacy!.HVisibleClusters = hVisibleClusters;
            _drawPassLegacy.HIndirectDrawArgs = hIndirectDrawArgs;
            _drawPassLegacy.HColorTarget = colorTarget;
            _drawPassLegacy.HDepthTarget = depthTarget;
            _drawPassLegacy.HVisBufferTarget = hVisBuffer;
            _drawPassLegacy.HDrawUniforms = hDrawUB;
            _drawPassLegacy.HGlobalTransformBuffer = hGlobalTransform;
            _drawPassLegacy.HPageHeap = hPageHeap;
            _drawPassLegacy.HVisibleClusterMeta = hPhase2IndirectDrawArgs; // Cleared to 0 → offset=0
            _drawPassLegacy.SetFrameData(DebugMode, WireframeEnabled, OverdrawEnabled, UseVisBuffer);
            graph.AddPass(_drawPassLegacy);

            // Resolve / Shading pass (VisBuffer -> ResolveTarget)
            if (UseVisBuffer)
            {
                AddShadingPasses(
                    graph, hVisBuffer, hVisibleClusters, hPageHeap,
                    hGlobalTransform, hGlobalInstanceHeader,
                    hResolveTarget, colorTarget, hDrawUB,
                    screenWidth, screenHeight, drawDebugMode
                );
            }

            _hasPrevHistory = false;
        }
        else
        {
            // Clear debug buffer counter before cull
            if (DebugShowHiZAABBs)
            {
                graph.AddPass<object>(
                    "ClearDebugHiZ",
                    (builder, _) =>
                    {
                        builder.Write(hDebugHiZOutput, ResourceState.CopyDest);
                    },
                    (rgCtx, _) =>
                    {
                        var buf = rgCtx.GetBuffer(hDebugHiZOutput);
                        if (buf != null)
                        {
                            var ctx2 = rgCtx.RenderContext.ImmediateContext;
                            ReadOnlySpan<uint> zero = stackalloc uint[] { 0 };
                            ctx2?.UpdateBuffer(buf, 0, zero, ResourceStateTransitionMode.Verify);
                        }
                    }
                );
            }

            // Phase1 Cull
            _cullPassPhase1!.HCandidateClusters = hCandidateClusters;
            _cullPassPhase1.HCandidateArgs = hCandidateArgs;
            _cullPassPhase1.HCandidateCount = hCandidateCount;
            _cullPassPhase1.HVisibleClusters = hVisibleClusters;
            _cullPassPhase1.HIndirectDrawArgs = hIndirectDrawArgs;
            _cullPassPhase1.HHiZTexture =
                HiZMode == HiZDebugMode.Phase1Only ? RenderGraphHandle.Invalid : hPrevHiZ; // Use prev frame's FULL HiZ (Phase 1+2 depth)
            _cullPassPhase1.HCullingUniforms = hCullingUB;
            _cullPassPhase1.HGlobalTransformBuffer = hGlobalTransform;
            _cullPassPhase1.HPageHeap = hPageHeap;
            _cullPassPhase1.HPhase2CandidateClusters = hPhase2CandidateClusters;
            _cullPassPhase1.HPhase2CandidateCount = hPhase2CandidateCount;
            _cullPassPhase1.HDebugHiZOutput = hDebugHiZOutput;
            graph.AddPass(_cullPassPhase1);

            // Phase 1 Draw
            _drawPassPhase1!.HVisibleClusters = hVisibleClusters;
            _drawPassPhase1.HIndirectDrawArgs = hIndirectDrawArgs;
            _drawPassPhase1.HColorTarget = colorTarget;
            _drawPassPhase1.HDepthTarget = depthTarget;
            _drawPassPhase1.HVisBufferTarget = hVisBuffer;
            _drawPassPhase1.HDrawUniforms = hDrawUB;
            _drawPassPhase1.HGlobalTransformBuffer = hGlobalTransform;
            _drawPassPhase1.HPageHeap = hPageHeap;
            _drawPassPhase1.HVisibleClusterMeta = hPhase2IndirectDrawArgs; // Cleared to 0 → offset=0 for Phase 1
            _drawPassPhase1.SetFrameData(DebugMode, WireframeEnabled, OverdrawEnabled, UseVisBuffer);
            graph.AddPass(_drawPassPhase1);

            if (HiZMode == HiZDebugMode.Phase1ThenHiZ || HiZMode == HiZDebugMode.Full2Phase)
            {
                // Phase 1 HiZ Build (from Phase 1 depth, for Phase 2 cull)
                // Writes into hCurrHiZ which will be overwritten later by full HiZ build
                graph.AddPass(new HiZMip0Pass(_hizBuildPass!, depthTarget, hCurrHiZ));
                for (uint mip = 1; mip < _hizMipCount; mip++)
                {
                    graph.AddPass(new HiZDownsamplePass(_hizBuildPass!, hCurrHiZ, mip));
                }
            }

            if (HiZMode == HiZDebugMode.Full2Phase)
            {
                // Phase 2 Update Args
                _cullUpdateArgsPassPhase2!.HCandidateCount = hPhase2CandidateCount;
                _cullUpdateArgsPassPhase2.HCandidateArgs = hPhase2CandidateArgs;
                graph.AddPass(_cullUpdateArgsPassPhase2);

                // Phase 2 Cull — reads Phase1's DrawArgs for N1, writes Phase2DrawArgs for N2 atomic
                _cullPassPhase2!.HCandidateClusters = hPhase2CandidateClusters;
                _cullPassPhase2.HCandidateArgs = hPhase2CandidateArgs;
                _cullPassPhase2.HCandidateCount = hPhase2CandidateCount;
                _cullPassPhase2.HVisibleClusters = hVisibleClusters;
                _cullPassPhase2.HIndirectDrawArgs = hIndirectDrawArgs;       // Phase1's DrawArgs (read N1)
                _cullPassPhase2.HPhase2IndirectDrawArgs = hPhase2IndirectDrawArgs; // Phase2's own DrawArgs (atomic N2)
                _cullPassPhase2.HHiZTexture = hCurrHiZ;
                _cullPassPhase2.HCullingUniforms = hCullingUB;
                _cullPassPhase2.HGlobalTransformBuffer = hGlobalTransform;
                _cullPassPhase2.HPageHeap = hPageHeap;
                _cullPassPhase2.HDebugHiZOutput = hDebugHiZOutput;
                graph.AddPass(_cullPassPhase2);

                // Phase 2 Draw — indirect from Phase2DrawArgs, offset from Phase1's DrawArgs[4]
                _drawPassPhase2!.HVisibleClusters = hVisibleClusters;
                _drawPassPhase2.HIndirectDrawArgs = hPhase2IndirectDrawArgs;      // Phase2's DrawArgs (InstanceCount=N2)
                _drawPassPhase2.HVisibleClusterMeta = hIndirectDrawArgs;          // Phase1's DrawArgs (byte 4 = N1 offset)
                _drawPassPhase2.HColorTarget = colorTarget;
                _drawPassPhase2.HDepthTarget = depthTarget;
                _drawPassPhase2.HVisBufferTarget = hVisBuffer;
                _drawPassPhase2.HDrawUniforms = hDrawUB;
                _drawPassPhase2.HGlobalTransformBuffer = hGlobalTransform;
                _drawPassPhase2.HPageHeap = hPageHeap;
                _drawPassPhase2.SetFrameData(DebugMode, WireframeEnabled, OverdrawEnabled, UseVisBuffer);
                graph.AddPass(_drawPassPhase2);

                // Full HiZ Build (overwrite hCurrHiZ with complete Phase 1+2 depth for next frame's Phase 1)
                graph.AddPass(new HiZMip0Pass(_hizBuildPass!, depthTarget, hCurrHiZ));
                for (uint mip = 1; mip < _hizMipCount; mip++)
                {
                    graph.AddPass(new HiZDownsamplePass(_hizBuildPass!, hCurrHiZ, mip));
                }

                // Resolve / Shading pass (VisBuffer -> ResolveTarget) after all draws complete
                if (UseVisBuffer)
                {
                    AddShadingPasses(
                        graph, hVisBuffer, hVisibleClusters, hPageHeap,
                        hGlobalTransform, hGlobalInstanceHeader,
                        hResolveTarget, colorTarget, hDrawUB,
                        screenWidth, screenHeight, drawDebugMode
                    );
                }
            }

            Matrix4x4 currentViewProjT = Matrix4x4.Transpose(_view * _proj);
            if (HiZMode == HiZDebugMode.Phase1ThenHiZ || HiZMode == HiZDebugMode.Full2Phase)
            {
                // RG caching handles history automatically via name-based caching.
                // The previous frame's "CurrHiZ" is still alive in the cache.
                _hasPrevHistory = true;
            }
            _prevViewProjT = currentViewProjT;
            _prevView = _view;
            _prevProj = _proj;
            if (HiZMode == HiZDebugMode.Phase1Only)
                _hasPrevHistory = false;
            else
                _hasPrevHistory = true;
        }

        // One-shot HiZ dump (F5)
        if (DumpNextFrame)
        {
            var hDumpDummy = graph.CreateBuffer(
                "DumpDummy",
                new BufferDesc
                {
                    Name = "DumpDummy",
                    Size = 4,
                    Usage = Usage.Default,
                    BindFlags = BindFlags.UnorderedAccess,
                    Mode = BufferMode.Raw,
                }
            );
            var dumpPass = new ClusterDebugDumpPass(context)
            {
                HHiZTexture = hCurrHiZ,
                HPhase1HiZTexture = RenderGraphHandle.Invalid, // Same as hCurrHiZ, already dumped
                HDepthTexture = depthTarget,
                HDebugHiZOutput = hDebugHiZOutput,
                HDummyOutput = hDumpDummy,
                HiZWidth = _hizWidth,
                HiZHeight = _hizHeight,
                HiZMipCount = _hizMipCount,
                ViewProj = cullViewProjT,
                PrevViewProj = _prevViewProjT,
                CameraPos = cullCameraPos,
                HiZInvSize = hizInvSize,
                HasPrevHistory = hasPrevHistory,
            };
            graph.AddPass(dumpPass);
            graph.MarkOutput(hDumpDummy);
            DumpNextFrame = false;
        }

        // Debug readback pass - always add to capture stats
        if (_debugReadbackPass != null)
        {
            var hDebugReadback = graph.CreateBuffer(
                "DebugReadback",
                new BufferDesc
                {
                    Name = "ClusterDebugReadbackBuffer",
                    Size = 80,
                    Usage = Usage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                }
            );
            _debugReadbackPass.HCandidateCount = hCandidateCount;
            _debugReadbackPass.HIndirectDrawArgs = hIndirectDrawArgs;
            _debugReadbackPass.HCandidateArgs = hCandidateArgs;
            _debugReadbackPass.HPhase2CandidateCount = hPhase2CandidateCount;
            _debugReadbackPass.HPhase2IndirectDrawArgs = hPhase2IndirectDrawArgs;
            _debugReadbackPass.HDebugReadbackBuffer = hDebugReadback;
            if (DebugShowHiZAABBs)
            {
                var hDebugHiZReadback = graph.CreateBuffer(
                    "DebugHiZReadback",
                    new BufferDesc
                    {
                        Name = "DebugHiZReadbackStaging",
                        Size = 196612,
                        Usage = Usage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None,
                    }
                );
                _debugReadbackPass.HDebugHiZOutput = hDebugHiZOutput;
                _debugReadbackPass.HDebugHiZReadback = hDebugHiZReadback;
                graph.MarkOutput(hDebugHiZReadback);
            }
            else
            {
                _debugReadbackPass.HDebugHiZOutput = RenderGraphHandle.Invalid;
                _debugReadbackPass.HDebugHiZReadback = RenderGraphHandle.Invalid;
            }
            graph.AddPass(_debugReadbackPass);
            graph.MarkOutput(hDebugReadback);

            // Copy data from readback pass for ImGui rendering
            _lastDebugHiZData = _debugReadbackPass.DebugHiZData;
        }

        if (DebugSpheresEnabled)
        {
            var hDebugIndirectArgsBuffer = graph.CreateBuffer(
                "DebugIndirectArgs",
                new BufferDesc
                {
                    Size = 256,
                    BindFlags =
                        BindFlags.UnorderedAccess
                        | BindFlags.IndirectDrawArgs
                        | BindFlags.ShaderResource,
                    Mode = BufferMode.Raw,
                }
            );

            graph.AddPass(
                new ClusterDebugSphereCopyPass(
                    _debugPass!,
                    hIndirectDrawArgs,
                    hDebugIndirectArgsBuffer,
                    hCopyUB
                )
            );

            graph.AddPass(
                new ClusterDebugSphereDrawPass(
                    _debugPass!,
                    hVisibleClusters,
                    hDebugIndirectArgsBuffer,
                    hPageHeap,
                    colorTarget,
                    depthTarget,
                    hDrawUB
                )
            );
        }

        // Debug AABB wireframe (GPU-rendered)
        if (DebugShowHiZAABBs)
        {
            _debugAABBPass ??= new ClusterDebugAABBPass(context);
            _debugAABBPass.HDebugHiZOutput = hDebugHiZOutput;
            _debugAABBPass.HColorTarget = colorTarget;
            graph.AddPass(_debugAABBPass);
        }
    }

    private void UpdateHiZState()
    {
        uint width = context.SwapChain?.GetDesc().Width ?? 0;
        uint height = context.SwapChain?.GetDesc().Height ?? 0;

        if (width == 0 || height == 0)
        {
            _hizWidth = 1;
            _hizHeight = 1;
            _hizMipCount = 1;
            return;
        }

        _hizWidth = width;
        _hizHeight = height;
        _hizMipCount = CalculateMipCount(_hizWidth, _hizHeight);
    }

    private static uint CalculateMipCount(uint width, uint height)
    {
        uint levels = 1;
        uint size = Math.Max(width, height);
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }

        return levels;
    }

    /// <summary>
    /// Adds shading passes (Binning + Material Shade) or falls back to debug Resolve.
    /// Shared between Legacy and 2-Phase paths.
    /// </summary>
    private void AddShadingPasses(
        RenderGraph graph,
        RenderGraphHandle hVisBuffer,
        RenderGraphHandle hVisibleClusters,
        RenderGraphHandle hPageHeap,
        RenderGraphHandle hGlobalTransform,
        RenderGraphHandle hGlobalInstanceHeader,
        RenderGraphHandle hResolveTarget,
        RenderGraphHandle colorTarget,
        RenderGraphHandle hDrawUB,
        uint screenWidth,
        uint screenHeight,
        uint drawDebugMode)
    {
        // For debug modes, use the existing resolve pass
        if (drawDebugMode != 0 && _resolvePass != null)
        {
            _resolvePass.HVisBuffer = hVisBuffer;
            _resolvePass.HDepthTarget = graph.GetResourceHandle("DepthTarget");
            _resolvePass.HVisibleClusters = hVisibleClusters;
            _resolvePass.HPageHeap = hPageHeap;
            _resolvePass.HGlobalTransformBuffer = hGlobalTransform;
            _resolvePass.HDrawUniforms = hDrawUB;
            _resolvePass.HColorTarget = hResolveTarget;
            graph.AddPass(_resolvePass);

            graph.AddPass<object>(
                "CopyResolveToBackBuffer",
                (builder, _) =>
                {
                    builder.Read(hResolveTarget, ResourceState.CopySource);
                    builder.Write(colorTarget, ResourceState.CopyDest);
                },
                (rgCtx, _) =>
                {
                    var src = rgCtx.GetTexture(hResolveTarget);
                    var dst = rgCtx.GetTexture(colorTarget);
                    if (src != null && dst != null)
                    {
                        var ctx2 = rgCtx.RenderContext.ImmediateContext;
                        ctx2?.CopyTexture(new CopyTextureAttribs
                        {
                            SrcTexture = src,
                            DstTexture = dst,
                            SrcTextureTransitionMode = ResourceStateTransitionMode.Verify,
                            DstTextureTransitionMode = ResourceStateTransitionMode.Verify,
                        });
                    }
                }
            );
            return;
        }

        // --- Multi-material shading pipeline ---
        const int MaxMaterials = 256;
        uint activeMaterialCount = 1; // TODO: track actual unique material count (was incorrectly using instanceManager.Count)

        // Create shading buffers
        var hBinUniforms = graph.CreateBuffer("ShadeBinUniforms", new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<ShadeBinUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        var hShadeUniforms = graph.CreateBuffer("ShadeUniforms", new BufferDesc
        {
            Size = 256, // padded to 256 for constant buffer alignment
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        var hBinCounts = graph.CreateBuffer("BinCounts", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinOffsets = graph.CreateBuffer("BinOffsets", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinScatterCount = graph.CreateBuffer("BinScatterCount", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hPixelCoordBuffer = graph.CreateBuffer("PixelCoordBuffer", new BufferDesc
        {
            Size = (ulong)(screenWidth * screenHeight * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinIndirectArgs = graph.CreateBuffer("BinIndirectArgs", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 12),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        // Upload shade bin uniforms
        var binUniformData = new ShadeBinUniforms
        {
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            MaterialCount = activeMaterialCount,
        };
        graph.AddPass<object>(
            "UploadShadeBinUniforms",
            (builder, _) =>
            {
                builder.Write(hBinUniforms, ResourceState.ConstantBuffer);
            },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinUniforms);
                if (ctx2 != null && buf != null)
                {
                    var mapped = ctx2.MapBuffer<ShadeBinUniforms>(buf, MapType.Write, MapFlags.Discard);
                    mapped[0] = binUniformData;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        // Upload shade uniforms
        var viewProjT = Matrix4x4.Transpose(_view * _proj);
        var viewT = Matrix4x4.Transpose(_view);
        var shadeUniformData = new ShadeUniforms
        {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = clusterManager.PageCount,
            DebugMode = drawDebugMode,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            QuantOrigin = clusterManager.QuantOrigin,
            QuantStep = clusterManager.QuantStep,
            MaterialID = 0,
            MaterialCount = activeMaterialCount,
            LightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f)),
            LightIntensity = 1.0f,
            AmbientColor = new Vector3(0.15f, 0.15f, 0.15f),
            CameraPos = _cameraPos,
        };
        graph.AddPass<object>(
            "UploadShadeUniforms",
            (builder, _) =>
            {
                builder.Write(hShadeUniforms, ResourceState.ConstantBuffer);
            },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hShadeUniforms);
                if (ctx2 != null && buf != null)
                {
                    var mapped = ctx2.MapBuffer<ShadeUniforms>(buf, MapType.Write, MapFlags.Discard);
                    mapped[0] = shadeUniformData;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        // Clear bin counts
        graph.AddPass<object>(
            "ClearBinCounts",
            (builder, _) =>
            {
                builder.Write(hBinCounts, ResourceState.CopyDest);
            },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinCounts);
                if (ctx2 != null && buf != null)
                {
                    Span<byte> zeros = stackalloc byte[MaxMaterials * 4];
                    zeros.Clear();
                    ctx2.UpdateBuffer(buf, 0, (ReadOnlySpan<byte>)zeros, ResourceStateTransitionMode.Verify);
                }
            }
        );

        // Shade Bin Count pass
        _shadeBinCountPass!.HVisBuffer = hVisBuffer;
        _shadeBinCountPass.HVisibleClusters = hVisibleClusters;
        _shadeBinCountPass.HInstanceHeaders = hGlobalInstanceHeader;
        _shadeBinCountPass.HShadeBinUniforms = hBinUniforms;
        _shadeBinCountPass.HBinCounts = hBinCounts;
        graph.AddPass(_shadeBinCountPass);

        // Shade Bin Reserve pass
        _shadeBinReservePass!.HShadeBinUniforms = hBinUniforms;
        _shadeBinReservePass.HBinCounts = hBinCounts;
        _shadeBinReservePass.HBinOffsets = hBinOffsets;
        _shadeBinReservePass.HBinScatterCount = hBinScatterCount;
        _shadeBinReservePass.HBinIndirectArgs = hBinIndirectArgs;
        graph.AddPass(_shadeBinReservePass);

        // Shade Bin Scatter pass
        _shadeBinScatterPass!.HVisBuffer = hVisBuffer;
        _shadeBinScatterPass.HVisibleClusters = hVisibleClusters;
        _shadeBinScatterPass.HInstanceHeaders = hGlobalInstanceHeader;
        _shadeBinScatterPass.HShadeBinUniforms = hBinUniforms;
        _shadeBinScatterPass.HBinOffsets = hBinOffsets;
        _shadeBinScatterPass.HBinScatterCount = hBinScatterCount;
        _shadeBinScatterPass.HPixelCoordBuffer = hPixelCoordBuffer;
        graph.AddPass(_shadeBinScatterPass);

        // Clear resolve target before shading
        graph.AddPass<object>(
            "ClearResolveTarget",
            (builder, _) =>
            {
                builder.Write(hResolveTarget, ResourceState.RenderTarget);
            },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var tex = rgCtx.GetTexture(hResolveTarget);
                if (ctx2 != null && tex != null)
                {
                    var rtv = tex.GetDefaultView(TextureViewType.RenderTarget);
                    if (rtv != null)
                    {
                        ctx2.SetRenderTargets([rtv], null, ResourceStateTransitionMode.Verify);
                        ctx2.ClearRenderTarget(rtv, new System.Numerics.Vector4(0, 0, 0, 0), ResourceStateTransitionMode.Verify);
                    }
                }
            }
        );

        // Material Shade pass
        _materialShadePass!.HVisBuffer = hVisBuffer;
        _materialShadePass.HVisibleClusters = hVisibleClusters;
        _materialShadePass.HPageHeap = hPageHeap;
        _materialShadePass.HInstances = hGlobalTransform;
        _materialShadePass.HShadeUniforms = hShadeUniforms;
        _materialShadePass.HPixelCoordBuffer = hPixelCoordBuffer;
        _materialShadePass.HBinOffsets = hBinOffsets;
        _materialShadePass.HBinIndirectArgs = hBinIndirectArgs;
        _materialShadePass.HOutputColor = hResolveTarget;
        _materialShadePass.ShadeUniformData = shadeUniformData;
        _materialShadePass.ActiveMaterialCount = activeMaterialCount;
        graph.AddPass(_materialShadePass);

        // Copy shade result to back buffer
        graph.AddPass<object>(
            "CopyShadedToBackBuffer",
            (builder, _) =>
            {
                builder.Read(hResolveTarget, ResourceState.CopySource);
                builder.Write(colorTarget, ResourceState.CopyDest);
            },
            (rgCtx, _) =>
            {
                var src = rgCtx.GetTexture(hResolveTarget);
                var dst = rgCtx.GetTexture(colorTarget);
                if (src != null && dst != null)
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    ctx2?.CopyTexture(new CopyTextureAttribs
                    {
                        SrcTexture = src,
                        DstTexture = dst,
                        SrcTextureTransitionMode = ResourceStateTransitionMode.Verify,
                        DstTextureTransitionMode = ResourceStateTransitionMode.Verify,
                    });
                }
            }
        );
    }

    public void Dispose()
    {
        // HiZ textures are managed by RG via ping-pong naming, no manual disposal needed.

        _bvhPatchPass?.Dispose();
        _bvhTraversePass?.Dispose();
        _cullUpdateArgsPass?.Dispose();
        _cullUpdateArgsPassPhase2?.Dispose();
        _cullPassLegacy?.Dispose();
        _cullPassPhase1?.Dispose();
        _cullPassPhase2?.Dispose();
        _drawPassLegacy?.Dispose();
        _drawPassPhase1?.Dispose();
        _drawPassPhase2?.Dispose();
        _hizBuildPass?.Dispose();
        _debugPass?.Dispose();
        _debugAABBPass?.Dispose();
        _resolvePass?.Dispose();
        _shadeBinningResources?.Dispose();
        _materialShadePass?.Dispose();
    }
}
