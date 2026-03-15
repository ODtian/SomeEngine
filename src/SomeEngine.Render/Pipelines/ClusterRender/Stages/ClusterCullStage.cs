using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: 剔除 — 从候选 Cluster 中筛选可见的。
/// 支持 Legacy / Phase1Only / Full2Phase 模式。
/// 管理 HiZ 历史的跨帧状态（ping-pong）。
/// </summary>
public class ClusterCullStage : IDisposable
{
    private readonly RenderContext _context;
    private ClusterCullPass? _cullPassLegacy;
    private ClusterCullPass? _cullPassPhase1;
    private ClusterCullPass? _cullPassPhase2;
    private ClusterCullUpdateArgsPass? _cullUpdateArgsPassPhase2;
    private HiZBuildPass? _hizBuildPass;

    // HiZ ping-pong state
    private bool _pingPong;
    private bool _hasPrevHistory;
    private HiZDebugMode _prevHiZMode = HiZDebugMode.Full2Phase;
    private uint _hizWidth, _hizHeight, _hizMipCount;

    private bool _initialized;

    public ClusterCullStage(RenderContext context)
    {
        _context = context;
    }

    public void Init()
    {
        if (_initialized) return;

        _cullPassLegacy = new ClusterCullPass(_context, ClusterCullPhase.Legacy, "CullLegacy");
        _cullPassLegacy.Init();
        _cullPassPhase1 = new ClusterCullPass(_context, ClusterCullPhase.Phase1, "CullPhase1");
        _cullPassPhase1.Init();
        _cullPassPhase2 = new ClusterCullPass(_context, ClusterCullPhase.Phase2, "CullPhase2");
        _cullPassPhase2.Init();
        _cullUpdateArgsPassPhase2 = new ClusterCullUpdateArgsPass(_context, "CullUpdateArgsPhase2");
        _cullUpdateArgsPassPhase2.Init();
        _hizBuildPass = new HiZBuildPass(_context);
        _hizBuildPass.Init();

        _initialized = true;
    }

    /// <summary>
    /// 更新 HiZ 尺寸（应在 AddPasses 前调用）。
    /// </summary>
    public void UpdateHiZState(uint screenWidth, uint screenHeight)
    {
        if (screenWidth == 0 || screenHeight == 0)
        {
            _hizWidth = 1; _hizHeight = 1; _hizMipCount = 1;
            return;
        }
        _hizWidth = screenWidth;
        _hizHeight = screenHeight;
        _hizMipCount = CalculateMipCount(screenWidth, screenHeight);
    }

    /// <summary>HiZ mip 数，供 UploadConfig 引用。</summary>
    public uint HiZMipCount => _hizMipCount;
    public uint HiZWidth => _hizWidth;
    public uint HiZHeight => _hizHeight;
    public bool HasPrevHistory => _hasPrevHistory;

    /// <summary>
    /// 向 RenderGraph 添加 Cull 相关 pass。
    /// 返回 CullOutput 包含 VisibleClusters 和 DrawArgs。
    /// 同时在 Full2Phase 模式下透出 HiZ handle（供后续帧使用）。
    /// </summary>
    public ClusterCullOutput AddPasses(
        RenderGraph graph,
        in ClusterTraverseOutput traverse,
        in ClusterGlobalResources globals,
        in ClusterCullConfig config,
        RenderGraphHandle colorTarget,
        RenderGraphHandle depthTarget,
        bool debugShowHiZAABBs = false
    )
    {
        if (!_initialized) Init();

        uint maxDraws = ClusterRenderFeature.MaxDraws;

        // ─── Create cull output buffers ───
        var hVisibleClusters = config.OutputVisibleClusters.IsValid
            ? config.OutputVisibleClusters
            : graph.CreateBuffer("VisibleClusters", new BufferDesc
            {
                Size = (ulong)(maxDraws * 16),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 16,
            });

        var hIndirectDrawArgs = config.OutputDrawArgs.IsValid
            ? config.OutputDrawArgs
            : graph.CreateBuffer("IndirectDrawArgs", new BufferDesc
            {
                Size = 256,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            });

        // Phase2IndirectDrawArgs and ZeroOffsetBuffer are created and cleared by TraverseStage
        var hPhase2IndirectDrawArgs = traverse.Phase2IndirectDrawArgs;

        // ─── HiZ mode management ───
        bool useHiZBuffers = config.HiZMode != HiZDebugMode.Legacy && config.HiZMode != HiZDebugMode.Phase1OnlyPassAll;
        bool useHiZ = useHiZBuffers;

        if (config.HiZMode != _prevHiZMode)
        {
            _hasPrevHistory = false;
            _prevHiZMode = config.HiZMode;
        }

        // Phase 2 candidate buffers
        var hPhase2CandidateClusters = RenderGraphHandle.Invalid;
        var hPhase2CandidateCount = RenderGraphHandle.Invalid;
        var hPhase2CandidateArgs = RenderGraphHandle.Invalid;

        if (useHiZBuffers)
        {
            hPhase2CandidateClusters = graph.CreateBuffer("Phase2CandidateClusters", new BufferDesc
            {
                Size = (ulong)(maxDraws * 12),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 12,
            });
            hPhase2CandidateCount = graph.CreateBuffer("Phase2CandidateCount", new BufferDesc
            {
                Size = 4,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            });
            hPhase2CandidateArgs = graph.CreateBuffer("Phase2CandidateArgs", new BufferDesc
            {
                Size = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            });
        }

        // ─── HiZ textures (always create for RG cache tracking) ───
        var hCurrHiZ = RenderGraphHandle.Invalid;
        var hPrevHiZ = RenderGraphHandle.Invalid;
        bool hasPrevHistoryValid = false;

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

            string currName = _pingPong ? "HiZ_A" : "HiZ_B";
            string prevName = _pingPong ? "HiZ_B" : "HiZ_A";
            hCurrHiZ = graph.CreateTexture(currName, hizDesc with { Name = currName });
            hPrevHiZ = graph.CreateTexture(prevName, hizDesc with { Name = prevName });
            hasPrevHistoryValid = useHiZ && _hasPrevHistory && hPrevHiZ.IsValid;

            if (useHiZ) _pingPong = !_pingPong;
        }

        // ─── Debug HiZ output buffer ───
        var hDebugHiZOutput = graph.CreateBuffer(
            debugShowHiZAABBs ? "DebugHiZOutput" : "DebugHiZOutputDummy",
            new BufferDesc
            {
                Size = debugShowHiZAABBs ? 196612u : 16u,
                BindFlags = BindFlags.UnorderedAccess | (debugShowHiZAABBs ? BindFlags.ShaderResource : BindFlags.None),
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );

        var hZeroOffsetBuffer = traverse.ZeroOffsetBuffer;

        // ─── Clear Phase2 candidate buffers only (Phase2IndirectDrawArgs already cleared by TraverseStage) ───
        if (useHiZBuffers)
        {
            graph.AddPass(new ClusterClearBuffersPass(
                RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid, hPhase2CandidateCount, RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid, hPhase2CandidateArgs
            ));
        }


        // Clear debug buffer if needed
        if (debugShowHiZAABBs)
        {
            graph.AddPass<object>(
                "ClearDebugHiZ",
                (builder, _) => { builder.Write(hDebugHiZOutput, ResourceState.CopyDest); },
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

        // ─── Dispatch cull path ───
        if (config.HiZMode == HiZDebugMode.Legacy || config.HiZMode == HiZDebugMode.Phase1OnlyPassAll)
        {
            // Legacy / Phase1OnlyPassAll
            _cullPassLegacy!.HCandidateClusters = traverse.CandidateClusters;
            _cullPassLegacy.HCandidateArgs = traverse.CandidateArgs;
            _cullPassLegacy.HCandidateCount = traverse.CandidateCount;
            _cullPassLegacy.HVisibleClusters = hVisibleClusters;
            _cullPassLegacy.HIndirectDrawArgs = hIndirectDrawArgs;
            _cullPassLegacy.HCullingUniforms = globals.CullingUniforms;
            _cullPassLegacy.HGlobalTransformBuffer = globals.GlobalTransform;
            _cullPassLegacy.HPageHeap = globals.PageHeap;
            _cullPassLegacy.HDebugHiZOutput = hDebugHiZOutput;
            graph.AddPass(_cullPassLegacy);

            _hasPrevHistory = false;

            return new ClusterCullOutput(hVisibleClusters, hIndirectDrawArgs, hPhase2IndirectDrawArgs, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, hDebugHiZOutput);
        }

        // Phase1 Cull
        _cullPassPhase1!.HCandidateClusters = traverse.CandidateClusters;
        _cullPassPhase1.HCandidateArgs = traverse.CandidateArgs;
        _cullPassPhase1.HCandidateCount = traverse.CandidateCount;
        _cullPassPhase1.HVisibleClusters = hVisibleClusters;
        _cullPassPhase1.HIndirectDrawArgs = hIndirectDrawArgs;
        _cullPassPhase1.HHiZTexture = config.HiZMode == HiZDebugMode.Phase1Only ? RenderGraphHandle.Invalid : hPrevHiZ;
        _cullPassPhase1.HCullingUniforms = globals.CullingUniforms;
        _cullPassPhase1.HGlobalTransformBuffer = globals.GlobalTransform;
        _cullPassPhase1.HPageHeap = globals.PageHeap;
        _cullPassPhase1.HPhase2CandidateClusters = hPhase2CandidateClusters;
        _cullPassPhase1.HPhase2CandidateCount = hPhase2CandidateCount;
        _cullPassPhase1.HDebugHiZOutput = hDebugHiZOutput;
        graph.AddPass(_cullPassPhase1);

        // Update history
        if (config.HiZMode == HiZDebugMode.Phase1Only)
            _hasPrevHistory = false;
        else
            _hasPrevHistory = true;

        // NOTE: Phase1 HiZ Build and Phase2 Cull are NOT added here.
        // They must be added by the pipeline AFTER Phase1 Draw so that
        // the HiZ is built from real depth, not the cleared depth buffer.
        return new ClusterCullOutput(hVisibleClusters, hIndirectDrawArgs, hPhase2IndirectDrawArgs, hCurrHiZ, hPhase2CandidateCount, hPhase2CandidateClusters, hPhase2CandidateArgs, hDebugHiZOutput);
    }

    /// <summary>
    /// Phase2 passes（在 Phase1 Draw + HiZ Build 之后调用）。
    /// </summary>
    public void AddPhase2Passes(
        RenderGraph graph,
        in ClusterCullOutput cullOut,
        in ClusterGlobalResources globals)
    {
        if (!_initialized) return;

        // Phase 2 Update Args
        _cullUpdateArgsPassPhase2!.HCandidateCount = cullOut.Phase2CandidateCount;
        _cullUpdateArgsPassPhase2.HCandidateArgs = cullOut.Phase2CandidateArgs;
        graph.AddPass(_cullUpdateArgsPassPhase2);

        // Phase 2 Cull
        _cullPassPhase2!.HCandidateClusters = cullOut.Phase2CandidateClusters;
        _cullPassPhase2.HCandidateArgs = cullOut.Phase2CandidateArgs;
        _cullPassPhase2.HCandidateCount = cullOut.Phase2CandidateCount;
        _cullPassPhase2.HVisibleClusters = cullOut.VisibleClusters;
        _cullPassPhase2.HIndirectDrawArgs = cullOut.DrawArgs;
        _cullPassPhase2.HPhase2IndirectDrawArgs = cullOut.Phase2DrawArgs;
        _cullPassPhase2.HHiZTexture = cullOut.HiZ;
        _cullPassPhase2.HCullingUniforms = globals.CullingUniforms;
        _cullPassPhase2.HGlobalTransformBuffer = globals.GlobalTransform;
        _cullPassPhase2.HPageHeap = globals.PageHeap;
        _cullPassPhase2.HDebugHiZOutput = cullOut.DebugHiZOutput;
        graph.AddPass(_cullPassPhase2);
    }

    /// <summary>
    /// 全 HiZ 重建（在 Phase2 Draw 后调用，为下帧 Phase1 准备完整深度）。
    /// </summary>
    public void AddFinalHiZBuild(RenderGraph graph, RenderGraphHandle depthTarget, RenderGraphHandle hCurrHiZ)
    {
        if (!_initialized || _hizBuildPass == null) return;
        graph.AddPass(new HiZMip0Pass(_hizBuildPass, depthTarget, hCurrHiZ));
        for (uint mip = 1; mip < _hizMipCount; mip++)
            graph.AddPass(new HiZDownsamplePass(_hizBuildPass, hCurrHiZ, mip));
    }

    private static uint CalculateMipCount(uint width, uint height)
    {
        uint levels = 1;
        uint size = Math.Max(width, height);
        while (size > 1) { size >>= 1; levels++; }
        return levels;
    }

    public void Dispose()
    {
        _cullPassLegacy?.Dispose();
        _cullPassPhase1?.Dispose();
        _cullPassPhase2?.Dispose();
        _cullUpdateArgsPassPhase2?.Dispose();
        _hizBuildPass?.Dispose();
    }
}
