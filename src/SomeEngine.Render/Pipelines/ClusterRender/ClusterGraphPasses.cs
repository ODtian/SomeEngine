using System;
using System.IO;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterResourceUploadPass(
    ClusterResourceManager resourceManager,
    RGResourceHandle globalBVH,
    RGResourceHandle pageHeap
) : RenderPass("Cluster Resource Upload")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(globalBVH, ResourceState.CopyDest);
        builder.WriteBuffer(pageHeap, ResourceState.CopyDest);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        var bvhBuffer = graphContext.GetBuffer(globalBVH);
        var heapBuffer = graphContext.GetBuffer(pageHeap);
        if (bvhBuffer != null && heapBuffer != null)
        {
            resourceManager.ExecutePendingUploads(context, bvhBuffer, heapBuffer);
        }
    }
}

internal sealed class ClusterBVHPatchPass(
    ClusterResourceManager resourceManager,
    RGResourceHandle globalBVH
) : RenderPass("Cluster BVH Patch")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(globalBVH, ResourceState.UnorderedAccess);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        var bvhBuffer = graphContext.GetBuffer(globalBVH);
        if (bvhBuffer != null)
        {
            resourceManager.ExecutePendingPatches(context, bvhBuffer);
        }
    }
}

internal sealed class ClusterUploadInstanceDataPass(
    InstanceSyncSystem transformSystem,
    RGResourceHandle globalTransform,
    RGResourceHandle globalInstanceHeader
) : RenderPass("Upload Instance Data")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(globalTransform, ResourceState.CopyDest);
        builder.WriteBuffer(globalInstanceHeader, ResourceState.CopyDest);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        if (transformSystem.Count <= 0)
            return;

        var globalTransformBuffer = graphContext.GetBuffer(globalTransform);
        var globalInstanceHeaderBuffer = graphContext.GetBuffer(globalInstanceHeader);
        if (globalTransformBuffer == null || globalInstanceHeaderBuffer == null)
            return;

        unsafe
        {
            fixed (GpuTransform* pT = transformSystem.CpuTransforms)
            {
                graphContext.CommandList.UpdateBuffer(
                    globalTransformBuffer,
                    0,
                    (uint)(transformSystem.Count * GpuTransform.SizeInBytes),
                    (IntPtr)pT,
                    ResourceStateTransitionMode.Verify
                );
            }

            fixed (GpuInstanceHeader* pH = transformSystem.CpuHeaders)
            {
                graphContext.CommandList.UpdateBuffer(
                    globalInstanceHeaderBuffer,
                    0,
                    (uint)(transformSystem.Count * GpuInstanceHeader.SizeInBytes),
                    (IntPtr)pH,
                    ResourceStateTransitionMode.Verify
                );
            }
        }
    }
}

internal sealed class ClusterClearBuffersPass(
    RGResourceHandle indirectDrawArgs,
    RGResourceHandle candidateArgs,
    RGResourceHandle candidateCount,
    RGResourceHandle pageFaultBuffer,
    RGResourceHandle bvhDebugCount,
    RGResourceHandle phase2CandidateCount
) : RenderPass("Clear Cluster Buffers")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(indirectDrawArgs, ResourceState.CopyDest);
        builder.WriteBuffer(candidateArgs, ResourceState.CopyDest);
        builder.WriteBuffer(candidateCount, ResourceState.CopyDest);
        builder.WriteBuffer(pageFaultBuffer, ResourceState.CopyDest);
        builder.WriteBuffer(bvhDebugCount, ResourceState.CopyDest);

        if (phase2CandidateCount.IsValid)
            builder.WriteBuffer(phase2CandidateCount, ResourceState.CopyDest);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        var drawArgsBuffer = graphContext.GetBuffer(indirectDrawArgs);
        var candidateArgsBuffer = graphContext.GetBuffer(candidateArgs);
        var candidateCountBuffer = graphContext.GetBuffer(candidateCount);
        var pageFault = graphContext.GetBuffer(pageFaultBuffer);
        var debugCountBuffer = graphContext.GetBuffer(bvhDebugCount);

        if (
            drawArgsBuffer == null
            || candidateArgsBuffer == null
            || candidateCountBuffer == null
            || pageFault == null
            || debugCountBuffer == null
        )
            return;

        Span<uint> resetDrawArgs = [372, 0, 0, 0];
        graphContext.CommandList.UpdateBuffer(
            drawArgsBuffer,
            0,
            resetDrawArgs,
            ResourceStateTransitionMode.Verify
        );

        Span<uint> resetCandidateArgs = [1, 1, 1, 0];
        graphContext.CommandList.UpdateBuffer(
            candidateArgsBuffer,
            0,
            resetCandidateArgs,
            ResourceStateTransitionMode.Verify
        );

        Span<uint> zeroCount = [0u];
        graphContext.CommandList.UpdateBuffer(
            candidateCountBuffer,
            0,
            zeroCount,
            ResourceStateTransitionMode.Verify
        );
        graphContext.CommandList.UpdateBuffer(pageFault, 0, zeroCount, ResourceStateTransitionMode.Verify);

        Span<uint> resetDebugArgs = [24, 0, 0, 0];
        graphContext.CommandList.UpdateBuffer(
            debugCountBuffer,
            0,
            resetDebugArgs,
            ResourceStateTransitionMode.Verify
        );

        if (!phase2CandidateCount.IsValid)
            return;

        var phase2CountBuffer = graphContext.GetBuffer(phase2CandidateCount);
        if (phase2CountBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                phase2CountBuffer,
                0,
                zeroCount,
                ResourceStateTransitionMode.Verify
            );
    }
}

internal sealed class ClusterBVHReadbackPass(ClusterBVHTraversePass bvhPass) : RenderPass("BVH Readback")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupReadbackPass(builder);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteReadbackPass(context, graphContext);
    }
}

internal sealed class ClusterBVHClearArgsPass(ClusterBVHTraversePass bvhPass, bool clearArgsA, string name)
    : RenderPass(name)
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupClearArgsPass(builder, clearArgsA);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteClearArgsPass(context, graphContext, clearArgsA);
    }
}

internal sealed class ClusterBVHInitQueuePass(ClusterBVHTraversePass bvhPass) : RenderPass("BVH Init Queue")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupInitQueuePass(builder);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteInitQueuePass(context, graphContext);
    }
}

internal sealed class ClusterBVHUpdateArgsPass(ClusterBVHTraversePass bvhPass, bool targetIsA, string name)
    : RenderPass(name)
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupUpdateArgsPass(builder, targetIsA);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteUpdateArgsPass(context, graphContext, targetIsA);
    }
}

internal sealed class ClusterBVHTraverseDepthPass(
    ClusterBVHTraversePass bvhPass,
    bool currentIsA,
    int depth,
    string name
) : RenderPass(name)
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupTraversePass(builder, currentIsA);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteTraversePass(context, graphContext, currentIsA, depth);
    }
}

internal sealed class ClusterBVHArgsReadbackPass(
    ClusterBVHTraversePass bvhPass,
    bool argsA,
    int depth,
    string name
) : RenderPass(name)
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupArgsReadbackPass(builder, argsA);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecuteArgsReadbackPass(context, graphContext, argsA, depth);
    }
}

internal sealed class ClusterBVHPageFaultCopyPass(
    ClusterBVHTraversePass bvhPass,
    RGResourceHandle hPageFaultReadback
) : RenderPass("BVH Copy Page Faults")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupPageFaultCopyPass(builder, hPageFaultReadback);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        bvhPass.ExecutePageFaultCopyPass(context, graphContext, hPageFaultReadback);
    }
}

internal sealed class HiZMip0Pass(
    HiZBuildPass hizPass,
    RGResourceHandle hDepth,
    RGResourceHandle hHiZ
) : RenderPass("HiZ Build Mip0")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        hizPass.SetupMip0(builder, hDepth, hHiZ);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        hizPass.ExecuteMip0(context, graphContext, hDepth, hHiZ);
    }
}

internal sealed class HiZDownsamplePass(
    HiZBuildPass hizPass,
    RGResourceHandle hHiZ,
    uint mip
) : RenderPass($"HiZ Downsample Mip{mip}")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        hizPass.SetupDownsample(builder, hHiZ, mip);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        hizPass.ExecuteDownsample(context, graphContext, hHiZ, mip);
    }
}

internal sealed class ClusterDebugBVHPass(
    ClusterDebugPass debugPass,
    RGResourceHandle hBvhDebug,
    RGResourceHandle hBvhDebugCount,
    IBuffer drawUniformBuffer,
    RGResourceHandle hColor,
    RGResourceHandle hDepth,
    RGResourceHandle hDrawUB
) : RenderPass("Debug BVH AABBs")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        debugPass.SetupBVH(builder, hBvhDebug, hBvhDebugCount, hDrawUB, hColor, hDepth);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        debugPass.ExecuteBVH(context, graphContext, hBvhDebug, hBvhDebugCount, drawUniformBuffer);
    }
}

internal sealed class ClusterDebugSphereCopyPass(
    ClusterDebugPass debugPass,
    RGResourceHandle hIndirectDrawArgs,
    RGResourceHandle hDebugIndirectArgs,
    IBuffer copyUniformBuffer,
    RGResourceHandle hCopyUB
) : RenderPass("Debug Sphere Copy Args")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        debugPass.SetupSphereCopy(builder, hIndirectDrawArgs, hDebugIndirectArgs, hCopyUB);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        debugPass.ExecuteSphereCopy(context, graphContext, hIndirectDrawArgs, hDebugIndirectArgs, copyUniformBuffer);
    }
}

internal sealed class ClusterDebugSphereDrawPass(
    ClusterDebugPass debugPass,
    RGResourceHandle hVisibleClusters,
    RGResourceHandle hDebugIndirectArgs,
    IBuffer drawUniformBuffer,
    RGResourceHandle hPageHeap,
    RGResourceHandle hColor,
    RGResourceHandle hDepth,
    RGResourceHandle hDrawUB
) : RenderPass("Debug Sphere Draw")
{
    public override void Setup(RenderGraphBuilder builder)
    {
        debugPass.SetupSphereDraw(builder, hVisibleClusters, hDebugIndirectArgs, hDrawUB, hPageHeap, hColor, hDepth);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        debugPass.ExecuteSphereDraw(context, graphContext, hVisibleClusters, hDebugIndirectArgs, hPageHeap, drawUniformBuffer);
    }
}

internal sealed class ClusterCullUpdateArgsPass(RenderContext context, string passName = "Cull Update Args")
    : RenderPass(passName), IDisposable
{
    private readonly RenderContext _context = context;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private bool _initialized;

    public RGResourceHandle HCandidateCount = RGResourceHandle.Invalid;
    public RGResourceHandle HCandidateArgs = RGResourceHandle.Invalid;

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
        var shaderAsset = SlangShaderImporter.Import(shaderPath);

        using var cs = shaderAsset.CreateShader(_context, "UpdateIndirectArgs");
        var ci = new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Cull Update Args PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = shaderAsset.GetResourceVariables(
                        _context,
                        name =>
                            (name == "CandidateCount" || name == "CandidateArgs")
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            Cs = cs,
        };

        _pso = device.CreateComputePipelineState(ci);
        if (_pso != null)
            _srb = _pso.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HCandidateCount, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HCandidateArgs, ResourceState.UnorderedAccess);
    }

    public override void Execute(RenderContext context, RenderGraphContext graphContext)
    {
        if (_pso == null || _srb == null)
            return;

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var count = graphContext.GetBuffer(HCandidateCount);
        var args = graphContext.GetBuffer(HCandidateArgs);
        if (count == null || args == null)
            return;

        _srb.GetVariableByName(ShaderType.Compute, "CandidateCount")
            ?.Set(count.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        _srb.GetVariableByName(ShaderType.Compute, "CandidateArgs")
            ?.Set(args.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_pso);
        ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = 1,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void Dispose()
    {
        _srb?.Dispose();
        _pso?.Dispose();
    }
}

