using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterCullPass(
    RenderContext context,
    ClusterResourceManager clusterManager,
    InstanceSyncSystem transformSystem
) : RenderPass("ClusterCull"), IDisposable
{
    private readonly RenderContext _context = context;
    private ShaderAsset? _cullShaderAsset;
    private IPipelineState? _cullPSO;
    private IShaderResourceBinding? _cullSRB;
    private IPipelineState? _cullUpdateArgsPSO;
    private IShaderResourceBinding? _cullUpdateArgsSRB;
    private bool _initialized;

    // RenderGraph handles set by orchestrator
    public RGResourceHandle HCandidateClusters,
        HCandidateArgs,
        HCandidateCount;
    public RGResourceHandle HVisibleClusters,
        HIndirectDrawArgs;
    public RGResourceHandle HCullingUniforms;

    private IBuffer? _cullingUniformBuffer;

    public void SetFrameData(IBuffer cullingUB)
    {
        _cullingUniformBuffer = cullingUB;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = _context.Device;
        if (device == null)
            return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_cull.slang"
            )
        );
        _cullShaderAsset = SlangShaderImporter.Import(shaderPath);

        using var cs = _cullShaderAsset.CreateShader(_context, "main");
        var ci = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Cluster Cull PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _cullShaderAsset.GetResourceVariables(
                        _context,
                        (name, cat) =>
                            (
                                name == "CandidateClusters"
                                || name == "CandidateCount"
                                || name == "DrawArgs"
                                || name == "VisibleClusters"
                                || name == "Instances"
                            )
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            Cs = cs,
        };

        _cullPSO = device.CreateComputePipelineState(ci);
        if (_cullPSO != null)
            _cullSRB = _cullPSO.CreateShaderResourceBinding(false);

        using var upCs = _cullShaderAsset.CreateShader(_context, "UpdateIndirectArgs");
        var upCi = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Cull Update Args PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _cullShaderAsset.GetResourceVariables(
                        _context,
                        (name, cat) =>
                            (name == "CandidateCount" || name == "CandidateArgs")
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            Cs = upCs,
        };

        _cullUpdateArgsPSO = device.CreateComputePipelineState(upCi);
        if (_cullUpdateArgsPSO != null)
            _cullUpdateArgsSRB = _cullUpdateArgsPSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HCandidateClusters, ResourceState.ShaderResource);
        builder.ReadBuffer(HCandidateArgs, ResourceState.IndirectArgument);
        builder.ReadBuffer(HCandidateCount, ResourceState.UnorderedAccess);
        builder.ReadBuffer(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.WriteBuffer(HVisibleClusters, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HIndirectDrawArgs, ResourceState.UnorderedAccess);
    }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx)
    {
        if (_cullPSO == null || _cullSRB == null)
            return;
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candArgs = rgCtx.GetBuffer(HCandidateArgs);
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        if (candidates == null || visible == null || drawArgs == null)
            return;

        // Bind transient resources
        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);

        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "PageHeap")
            ?.Set(
                clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "CandidateClusters")
            ?.Set(
                candidates.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "CandidateCount")
            ?.Set(
                candCount?.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(
                drawArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "VisibleClusters")
            ?.Set(
                visible.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        if (transformSystem.GlobalTransformBuffer != null)
            _cullSRB
                .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "Instances")
                ?.Set(
                    transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );

        // Update indirect args from candidate count
        if (
            _cullUpdateArgsPSO != null
            && _cullUpdateArgsSRB != null
            && candCount != null
            && candArgs != null
        )
        {
            _cullUpdateArgsSRB
                .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "CandidateCount")
                ?.Set(
                    candCount.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _cullUpdateArgsSRB
                .GetVariable(_context, _cullShaderAsset, ShaderType.Compute, "CandidateArgs")
                ?.Set(
                    candArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            ctx.SetPipelineState(_cullUpdateArgsPSO);
            ctx.CommitShaderResources(_cullUpdateArgsSRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(
                new DispatchComputeAttribs
                {
                    ThreadGroupCountX = 1,
                    ThreadGroupCountY = 1,
                    ThreadGroupCountZ = 1,
                }
            );
        }

        ctx.TransitionResourceStates([
            new StateTransitionDesc
            {
                Resource = candArgs,
                OldState = ResourceState.UnorderedAccess,
                NewState = ResourceState.IndirectArgument,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = candidates,
                OldState = ResourceState.UnorderedAccess,
                NewState = ResourceState.ShaderResource,
                Flags = StateTransitionFlags.UpdateState,
            },
        ]);

        ctx.SetPipelineState(_cullPSO);
        ctx.CommitShaderResources(_cullSRB, ResourceStateTransitionMode.Transition);
        ctx.DispatchComputeIndirect(
            new DispatchComputeIndirectAttribs
            {
                AttribsBuffer = candArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
            }
        );
    }

    public void Dispose()
    {
        _cullSRB?.Dispose();
        _cullPSO?.Dispose();
        _cullUpdateArgsSRB?.Dispose();
        _cullUpdateArgsPSO?.Dispose();
    }
}
