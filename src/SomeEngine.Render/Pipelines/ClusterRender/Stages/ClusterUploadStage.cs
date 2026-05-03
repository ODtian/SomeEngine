using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: 负责创建全局 GPU 资源 + Upload Instance/BVH 数据。
/// 所有 View 共享此 Stage 的产物。Uniform buffer 由 Pipeline 自行管理。
/// </summary>
public class ClusterUploadStage(
    RenderContext context,
    ClusterResourceManager clusterMgr,
    InstanceDataManager instanceMgr
)
{
    internal sealed class Resources : IDisposable
    {
        internal const string PatchShaderFile = "bvh_patch.slang";
        internal const string PatchEntryPoint = "main";

        internal IPipelineState? PatchPSO;
        internal ShaderAsset? PatchShaderAsset;
        internal readonly SRBPool PatchPool = new();

        private bool _initialized;
        private readonly Lock _initLock = new();

        internal void EnsureInitialized(RenderContext context)
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                var device = context.Device;
                if (device == null) return;

                string path = ClusterStageUtils.ShaderPath(PatchShaderFile);
                var patchShaderAsset = SlangShaderImporter.Import(path);
                PatchShaderAsset = patchShaderAsset;

                var cs = patchShaderAsset.CreateShader(context, PatchEntryPoint);
                PatchPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc
                    {
                        Name = "BVH Patch PSO",
                        PipelineType = PipelineType.Compute,
                        ResourceLayout = new PipelineResourceLayoutDesc
                        {
                            DefaultVariableType = ShaderResourceVariableType.Dynamic,
                        },
                    },
                    Cs = cs,
                });

                _initialized = true;
            }
        }

        public void Dispose()
        {
            PatchPool.Dispose();
            PatchPSO?.Dispose();
        }
    }

    private readonly Resources _resources = new();
    private ClusterBVHPatchPass? _bvhPatchPass;
    private bool _initialized;

    public void Init()
    {
        if (_initialized) return;
        _bvhPatchPass = new ClusterBVHPatchPass(context, _resources);
        _bvhPatchPass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 向 RenderGraph 添加全局数据 Upload pass，返回全局资源 Handle。
    /// </summary>
    public ClusterGlobalResources AddPasses(RenderGraph graph)
    {
        if (!_initialized) Init();

        // ─── Create global data buffers ───
        int maxInstances = Math.Max(instanceMgr.Count, 1);
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
                Size = (ulong)(maxInstances * InstanceHeaderLayout.StrideBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            }
        );
        var hInstanceDataHeap = graph.CreateBuffer(
            "InstanceDataHeap",
            new BufferDesc
            {
                Size = Math.Max((ulong)instanceMgr.MetadataByteCount, 16ul),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            }
        );
        var hGlobalBVH = graph.CreateBuffer("GlobalBVH", clusterMgr.GlobalBVHDesc);
        var hPageHeap = graph.CreateBuffer("PageHeap", clusterMgr.PageHeapDesc);

        // ─── Resource Upload pass ───
        graph.AddPass(new ClusterResourceUploadPass(clusterMgr, hGlobalBVH, hPageHeap));

        // ─── BVH Patch pass ───
        var patches = clusterMgr.ExtractPendingPatches();
        if (patches.Count > 0)
        {
            var hPatchBuffer = graph.CreateBuffer(
                "PatchNodeIndices",
                new BufferDesc
                {
                    Size = (ulong)patches.Count * 8,
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

        // ─── Instance Data Upload pass ───
        if (instanceMgr.Count > 0)
        {
            graph.AddPass(
                new ClusterUploadInstanceDataPass(
                    instanceMgr,
                    hGlobalTransform,
                    hGlobalInstanceHeader,
                    hInstanceDataHeap
                )
            );
        }

        return new ClusterGlobalResources(
            hGlobalBVH, hPageHeap, hGlobalTransform, hGlobalInstanceHeader,
            hInstanceDataHeap
        );
    }

    public void Dispose()
    {
        _bvhPatchPass?.Dispose();
        _resources.Dispose();
    }
}
