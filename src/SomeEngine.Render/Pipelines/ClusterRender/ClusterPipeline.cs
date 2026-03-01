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
    public uint Pad4;
    public uint Pad5;

    public Matrix4x4 PrevViewProj;
    public uint HasPrevHistory;
    public uint HiZMipCount;
    public Vector2 HiZInvSize;
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
    public bool UseHiZ { get; set; } = true;
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
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private ITexture? _prevHiZTexture = null;
    private ITexture? _currHiZTexture = null;
    private uint _hizWidth = 0;
    private uint _hizHeight = 0;
    private uint _hizMipCount = 0;
    private bool _hasPrevHistory = false;

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
        _cullUpdateArgsPass = new ClusterCullUpdateArgsPass(context);
        _cullUpdateArgsPass.Init();
        _cullUpdateArgsPassPhase2 = new ClusterCullUpdateArgsPass(context, "Cull Update Args Phase2");
        _cullUpdateArgsPassPhase2.Init();
        
        _cullPassLegacy = new ClusterCullPass(context, clusterManager, transformSystem, ClusterCullPhase.Legacy, "ClusterCull Legacy");
        _cullPassLegacy.Init();
        _cullPassPhase1 = new ClusterCullPass(context, clusterManager, transformSystem, ClusterCullPhase.Phase1, "ClusterCull Phase1");
        _cullPassPhase1.Init();
        _cullPassPhase2 = new ClusterCullPass(context, clusterManager, transformSystem, ClusterCullPhase.Phase2, "ClusterCull Phase2");
        _cullPassPhase2.Init();

        _drawPassLegacy = new ClusterDrawPass(context, clusterManager, transformSystem, "ClusterDraw Legacy");
        _drawPassLegacy.Init();
        _drawPassPhase1 = new ClusterDrawPass(context, clusterManager, transformSystem, "ClusterDraw Phase1");
        _drawPassPhase1.Init();
        _drawPassPhase2 = new ClusterDrawPass(context, clusterManager, transformSystem, "ClusterDraw Phase2");
        _drawPassPhase2.Init();

        _hizBuildPass = new HiZBuildPass(context);
        _hizBuildPass.Init();
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

        PromoteCurrentHiZHistory();
        UpdateHiZState();
        ValidateHiZHistoryForCurrentFrame();
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

        RGResourceHandle hPhase2CandidateClusters = RGResourceHandle.Invalid;
        RGResourceHandle hPhase2CandidateCount = RGResourceHandle.Invalid;
        RGResourceHandle hPhase2CandidateArgs = RGResourceHandle.Invalid;
        RGResourceHandle hPhase2VisibleClusters = RGResourceHandle.Invalid;
        RGResourceHandle hPhase2IndirectDrawArgs = RGResourceHandle.Invalid;

        if (UseHiZ)
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
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                    Mode = BufferMode.Raw,
                    ElementByteStride = 4,
                }
            );
            hPhase2VisibleClusters = graph.CreateBuffer(
                "Phase2VisibleClusters",
                new BufferDesc
                {
                    Size = (ulong)(_maxDraws * 16),
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    Mode = BufferMode.Structured,
                    ElementByteStride = 16,
                }
            );
            hPhase2IndirectDrawArgs = graph.CreateBuffer(
                "Phase2IndirectDrawArgs",
                new BufferDesc
                {
                    Size = 256,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                    Mode = BufferMode.Raw,
                }
            );
        }
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

        RGResourceHandle hCurrHiZ = RGResourceHandle.Invalid;
        RGResourceHandle hPrevHiZ = RGResourceHandle.Invalid;
        bool hasPrevHistory = false;
        bool useHiZ = UseHiZ;

        if (useHiZ)
        {
            hCurrHiZ = graph.CreateTexture(
                "CurrHiZ",
                new TextureDesc
                {
                    Name = "CurrHiZ",
                    Type = ResourceDimension.Tex2d,
                    Width = _hizWidth,
                    Height = _hizHeight,
                    MipLevels = _hizMipCount,
                    Format = TextureFormat.R32_Float,
                    Usage = Usage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                }
            );

            if (_hasPrevHistory && _prevHiZTexture != null)
            {
                hPrevHiZ = graph.RegisterExternalTexture(
                    "PrevHiZ",
                    _prevHiZTexture,
                    ResourceState.ShaderResource
                );
                hasPrevHistory = hPrevHiZ.IsValid;
            }
        }

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
        RGResourceHandle hPageFaultReadback = RGResourceHandle.Invalid;
        if (clusterManager.PageFaultReadbackBuffer != null)
        {
            hPageFaultReadback = graph.ImportBuffer(
                "PageFaultReadback",
                clusterManager.PageFaultReadbackBuffer,
                ResourceState.CopyDest
            );
        }

        // Create managed buffers for InstanceSyncSystem
        int maxInstances = Math.Max(transformSystem.Count, 1);
        var hGlobalTransform = graph.CreateBuffer(
            "GlobalTransform",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuTransform.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuTransform.SizeInBytes
            }
        );
        var hGlobalInstanceHeader = graph.CreateBuffer(
            "GlobalInstanceHeader",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuInstanceHeader.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuInstanceHeader.SizeInBytes
            }
        );
        var hGlobalBVH = graph.ImportBuffer(
            "GlobalBVH",
            clusterManager.GlobalBVHBuffer!,
            ResourceState.Unknown
        );
        var hPageHeap = graph.ImportBuffer(
            "PageHeap",
            clusterManager.PageHeap!,
            ResourceState.Unknown
        );

        graph.AddPass(
            new ClusterResourceUploadPass(
                clusterManager,
                hGlobalBVH,
                hPageHeap
            )
        );

        graph.AddPass(
            new ClusterBVHPatchPass(
                clusterManager,
                hGlobalBVH
            )
        );

        if (transformSystem.Count > 0)
        {
            graph.AddPass(
                new ClusterUploadInstanceDataPass(
                    transformSystem,
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
                hBvhDebugCount,
                useHiZ ? hPhase2CandidateCount : RGResourceHandle.Invalid
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
        _bvhTraversePass.HBvhDebugBuffer = hBvhDebug;
        _bvhTraversePass.HBvhDebugCountBuffer = hBvhDebugCount;
        _bvhTraversePass.HPageFaultBuffer = hPageFaultBuffer;
        _bvhTraversePass.HCullingUniforms = hCullingUB;
        _bvhTraversePass.HGlobalTransformBuffer = hGlobalTransform;
        _bvhTraversePass.HGlobalInstanceHeaderBuffer = hGlobalInstanceHeader;
        _bvhTraversePass.HGlobalBVHBuffer = hGlobalBVH;
        _bvhTraversePass.HPageHeap = hPageHeap;
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
            DebugBVHDepth,
            _prevViewProjT,
            hasPrevHistory,
            _hizMipCount,
            (_hizWidth > 0 && _hizHeight > 0)
                ? new Vector2(1.0f / _hizWidth, 1.0f / _hizHeight)
                : Vector2.Zero
        );

        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, true, "BVH Clear Args A"));
        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, false, "BVH Clear Args B"));

        if (transformSystem.Count > 0)
        {
            graph.AddPass(new ClusterBVHInitQueuePass(_bvhTraversePass));
            graph.AddPass(new ClusterBVHUpdateArgsPass(_bvhTraversePass, true, "BVH Update Init Args"));

            bool currentIsA = true;
            for (int depth = 0; depth < 32; depth++)
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

                if (depth < 8)
                {
                    graph.AddPass(
                        new ClusterBVHArgsReadbackPass(
                            _bvhTraversePass,
                            nextIsA,
                            depth,
                            $"BVH Readback D{depth}"
                        )
                    );
                }

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

        if (useHiZ)
        {
            // Phase 1 Cull
            _cullPassPhase1!.HCandidateClusters = hCandidateClusters;
            _cullPassPhase1.HCandidateArgs = hCandidateArgs;
            _cullPassPhase1.HCandidateCount = hCandidateCount;
            _cullPassPhase1.HVisibleClusters = hVisibleClusters;
            _cullPassPhase1.HIndirectDrawArgs = hIndirectDrawArgs;
            _cullPassPhase1.HHiZTexture = hPrevHiZ;
            _cullPassPhase1.HCullingUniforms = hCullingUB;
            _cullPassPhase1.HGlobalTransformBuffer = hGlobalTransform;
            _cullPassPhase1.HPageHeap = hPageHeap;
            _cullPassPhase1.HPhase2CandidateClusters = hPhase2CandidateClusters;
            _cullPassPhase1.HPhase2CandidateCount = hPhase2CandidateCount;
            _cullPassPhase1.SetFrameData(_cullingUniformBuffer!);
            graph.AddPass(_cullPassPhase1);

            // Phase 1 Draw
            _drawPassPhase1!.HVisibleClusters = hVisibleClusters;
            _drawPassPhase1.HIndirectDrawArgs = hIndirectDrawArgs;
            _drawPassPhase1.HColorTarget = colorTarget;
            _drawPassPhase1.HDepthTarget = depthTarget;
            _drawPassPhase1.HDrawUniforms = hDrawUB;
            _drawPassPhase1.HGlobalTransformBuffer = hGlobalTransform;
            _drawPassPhase1.HPageHeap = hPageHeap;
            _drawPassPhase1.SetFrameData(_drawUniformBuffer!, DebugMode, WireframeEnabled, OverdrawEnabled);
            graph.AddPass(_drawPassPhase1);

            // HiZ Build
            graph.AddPass(new HiZMip0Pass(_hizBuildPass!, depthTarget, hCurrHiZ));
            for (uint mip = 1; mip < _hizMipCount; mip++)
            {
                graph.AddPass(new HiZDownsamplePass(_hizBuildPass!, hCurrHiZ, mip));
            }

            // Phase 2 Update Args
            _cullUpdateArgsPassPhase2!.HCandidateCount = hPhase2CandidateCount;
            _cullUpdateArgsPassPhase2.HCandidateArgs = hPhase2CandidateArgs;
            graph.AddPass(_cullUpdateArgsPassPhase2);

            // Phase 2 Cull
            _cullPassPhase2!.HCandidateClusters = hPhase2CandidateClusters;
            _cullPassPhase2.HCandidateArgs = hPhase2CandidateArgs;
            _cullPassPhase2.HCandidateCount = hPhase2CandidateCount;
            _cullPassPhase2.HVisibleClusters = hPhase2VisibleClusters;
            _cullPassPhase2.HIndirectDrawArgs = hPhase2IndirectDrawArgs;
            _cullPassPhase2.HHiZTexture = hCurrHiZ; // Use current frame HiZ for Phase 2!
            _cullPassPhase2.HCullingUniforms = hCullingUB;
            _cullPassPhase2.HGlobalTransformBuffer = hGlobalTransform;
            _cullPassPhase2.HPageHeap = hPageHeap;
            _cullPassPhase2.SetFrameData(_cullingUniformBuffer!);
            graph.AddPass(_cullPassPhase2);

            // Phase 2 Draw
            _drawPassPhase2!.HVisibleClusters = hPhase2VisibleClusters;
            _drawPassPhase2.HIndirectDrawArgs = hPhase2IndirectDrawArgs;
            _drawPassPhase2.HColorTarget = colorTarget;
            _drawPassPhase2.HDepthTarget = depthTarget; // Depth is readonly in Phase 2 ? Keep as target for now
            _drawPassPhase2.HDrawUniforms = hDrawUB;
            _drawPassPhase2.HGlobalTransformBuffer = hGlobalTransform;
            _drawPassPhase2.HPageHeap = hPageHeap;
            _drawPassPhase2.SetFrameData(_drawUniformBuffer!, DebugMode, WireframeEnabled, OverdrawEnabled);
            graph.AddPass(_drawPassPhase2);

            Matrix4x4 currentViewProjT = Matrix4x4.Transpose(_view * _proj);
            graph.QueueTextureExtraction(
                hCurrHiZ,
                texture =>
                {
                    _currHiZTexture = texture;
                    _hasPrevHistory = texture != null;
                    if (texture != null)
                    {
                        _prevViewProjT = currentViewProjT;
                    }
                }
            );
        }
        else
        {
            // Legacy Cull
            _cullPassLegacy!.HCandidateClusters = hCandidateClusters;
            _cullPassLegacy.HCandidateArgs = hCandidateArgs;
            _cullPassLegacy.HCandidateCount = hCandidateCount;
            _cullPassLegacy.HVisibleClusters = hVisibleClusters;
            _cullPassLegacy.HIndirectDrawArgs = hIndirectDrawArgs;
            _cullPassLegacy.HCullingUniforms = hCullingUB;
            _cullPassLegacy.HGlobalTransformBuffer = hGlobalTransform;
            _cullPassLegacy.HPageHeap = hPageHeap;
            _cullPassLegacy.SetFrameData(_cullingUniformBuffer!);
            graph.AddPass(_cullPassLegacy);

            // Legacy Draw
            _drawPassLegacy!.HVisibleClusters = hVisibleClusters;
            _drawPassLegacy.HIndirectDrawArgs = hIndirectDrawArgs;
            _drawPassLegacy.HColorTarget = colorTarget;
            _drawPassLegacy.HDepthTarget = depthTarget;
            _drawPassLegacy.HDrawUniforms = hDrawUB;
            _drawPassLegacy.HGlobalTransformBuffer = hGlobalTransform;
            _drawPassLegacy.HPageHeap = hPageHeap;
            _drawPassLegacy.SetFrameData(_drawUniformBuffer!, DebugMode, WireframeEnabled, OverdrawEnabled);
            graph.AddPass(_drawPassLegacy);

            _hasPrevHistory = false;
        }

        // Wire Debug pass
        if (VisualiseBVH)
        {
            graph.AddPass(
                new ClusterDebugBVHPass(
                    _debugPass!,
                    hBvhDebug,
                    hBvhDebugCount,
                    _drawUniformBuffer!,
                    colorTarget,
                    depthTarget,
                    hDrawUB
                )
            );
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
                    _copyUniformBuffer!,
                    hCopyUB
                )
            );

            graph.AddPass(
                new ClusterDebugSphereDrawPass(
                    _debugPass!,
                    hVisibleClusters,
                    hDebugIndirectArgsBuffer,
                    _drawUniformBuffer!,
                    hPageHeap,
                    colorTarget,
                    depthTarget,
                    hDrawUB
                )
            );
        }
    }

    private void UpdateUniforms()
    {
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var viewProjT = Matrix4x4.Transpose(_view * _proj);
        var viewT = Matrix4x4.Transpose(_view);
        var hizInvSize =
            (_hizWidth > 0 && _hizHeight > 0)
                ? new Vector2(1.0f / _hizWidth, 1.0f / _hizHeight)
                : Vector2.Zero;
        bool hasPrevHistory = _hasPrevHistory && _prevHiZTexture != null;

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
            Pad4 = 0,
            Pad5 = 0,
            PrevViewProj = _prevViewProjT,
            HasPrevHistory = hasPrevHistory ? 1u : 0u,
            HiZMipCount = _hizMipCount,
            HiZInvSize = hizInvSize,
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

    private void PromoteCurrentHiZHistory()
    {
        if (_currHiZTexture == null)
            return;

        if (_prevHiZTexture != null && !ReferenceEquals(_prevHiZTexture, _currHiZTexture))
        {
            _prevHiZTexture.Dispose();
        }

        _prevHiZTexture = _currHiZTexture;
        _currHiZTexture = null;
    }

    private void ValidateHiZHistoryForCurrentFrame()
    {
        if (!_hasPrevHistory || _prevHiZTexture == null)
        {
            _hasPrevHistory = false;
            return;
        }

        if (IsHiZHistoryCompatible(_prevHiZTexture))
            return;

        _prevHiZTexture.Dispose();
        _prevHiZTexture = null;
        _hasPrevHistory = false;
    }

    private bool IsHiZHistoryCompatible(ITexture texture)
    {
        var desc = texture.GetDesc();
        return desc.Type == ResourceDimension.Tex2d
            && desc.Format == TextureFormat.R32_Float
            && desc.Width == _hizWidth
            && desc.Height == _hizHeight
            && Math.Max(1u, desc.MipLevels) == _hizMipCount;
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
        _hizMipCount = CalculateMipCount(width, height);
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

    public void Dispose()
    {
        if (_currHiZTexture != null && !ReferenceEquals(_currHiZTexture, _prevHiZTexture))
        {
            _currHiZTexture.Dispose();
        }
        _currHiZTexture = null;
        _prevHiZTexture?.Dispose();
        _prevHiZTexture = null;

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
        _cullingUniformBuffer?.Dispose();
        _drawUniformBuffer?.Dispose();
        _copyUniformBuffer?.Dispose();
    }
}
