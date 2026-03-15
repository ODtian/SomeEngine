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
    RenderGraphHandle globalBVH,
    RenderGraphHandle pageHeap
) : IRenderGraphPass
{
    public string Name => "Cluster Resource Upload";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Write(globalBVH, ResourceState.CopyDest);
        builder.Write(pageHeap, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        var context = graphContext.RenderContext;
        var bvhBuffer = graphContext.GetBuffer(globalBVH);
        var heapBuffer = graphContext.GetBuffer(pageHeap);
        if (bvhBuffer != null && heapBuffer != null)
        {
            resourceManager.ExecutePendingUploads(context, bvhBuffer, heapBuffer);
        }
    }
}

internal sealed class ClusterBVHPatchPass : IRenderGraphPass, IDisposable
{
    private IPipelineState? _patchPSO;
    private IShaderResourceBinding? _patchSRB;
    private bool _initialized = false;
    private readonly RenderContext _context;

    public IReadOnlyList<ClusterResourceManager.BVHPatchData>? Patches;
    public RenderGraphHandle HGlobalBVH;
    public RenderGraphHandle HPatchBuffer;
    public RenderGraphHandle HPatchUniforms;

    public string Name => "Cluster BVH Patch";

    public ClusterBVHPatchPass(RenderContext context)
    {
        _context = context;
    }

    public void Init()
    {
        if (_initialized || _context.Device == null)
            return;

        string path = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/bvh_patch.slang"
            )
        );
        var patchShaderAsset = SlangShaderImporter.Import(path);

        var ci = new ComputePipelineStateCreateInfo();
        ci.PSODesc.Name = "BVH Patch PSO";
        ci.PSODesc.PipelineType = PipelineType.Compute;
        using var cs = patchShaderAsset.CreateShader(_context, "main");
        ci.Cs = cs;
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Dynamic;

        _patchPSO = _context.Device.CreateComputePipelineState(ci);
        if (_patchPSO != null)
        {
            _patchSRB = _patchPSO.CreateShaderResourceBinding(false);
        }

        _initialized = true;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Write(HGlobalBVH, ResourceState.UnorderedAccess);
        builder.Read(HPatchBuffer, ResourceState.ShaderResource);
        builder.Read(HPatchUniforms, ResourceState.ConstantBuffer);
    }

    public struct PatchUniforms
    {
        public uint PatchCount;
        public uint Pad0,
            Pad1,
            Pad2;
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (Patches == null || Patches.Count == 0)
            return;
        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null || _patchPSO == null || _patchSRB == null)
            return;

        var bvhBuffer = graphContext.GetBuffer(HGlobalBVH);
        var patchBuffer = graphContext.GetBuffer(HPatchBuffer);
        var uniformsBuffer = graphContext.GetBuffer(HPatchUniforms);

        if (bvhBuffer == null || patchBuffer == null || uniformsBuffer == null)
            return;

        var uniforms = new PatchUniforms
        {
            PatchCount = (uint)Patches.Count,
            Pad0 = 0,
            Pad1 = 0,
            Pad2 = 0,
        };
        var uSpan = ctx.MapBuffer<PatchUniforms>(uniformsBuffer, MapType.Write, MapFlags.Discard);
        uSpan[0] = uniforms;
        ctx.UnmapBuffer(uniformsBuffer, MapType.Write);

        var pSpan = ctx.MapBuffer<ClusterResourceManager.BVHPatchData>(
            patchBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        for (int i = 0; i < Patches.Count; i++)
            pSpan[i] = Patches[i];
        ctx.UnmapBuffer(patchBuffer, MapType.Write);

        _patchSRB
            .GetVariableByName(ShaderType.Compute, "GlobalBVH")
            ?.Set(
                bvhBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _patchSRB
            .GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformsBuffer, SetShaderResourceFlags.None);
        _patchSRB
            .GetVariableByName(ShaderType.Compute, "Patches")
            ?.Set(
                patchBuffer.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        ctx.SetPipelineState(_patchPSO);
        ctx.CommitShaderResources(_patchSRB, ResourceStateTransitionMode.Verify);
        uint groups = ((uint)Patches.Count + 63) / 64;
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = groups,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void Dispose()
    {
        _patchPSO?.Dispose();
        _patchSRB?.Dispose();
    }
}

internal sealed class ClusterUploadInstanceDataPass(
    InstanceDataManager transformSystem,
    RenderGraphHandle globalTransform,
    RenderGraphHandle globalInstanceHeader,
    RenderGraphHandle globalInstanceDataHeap
) : IRenderGraphPass
{
    public string Name => "Upload Instance Data";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Write(globalTransform, ResourceState.CopyDest);
        builder.Write(globalInstanceHeader, ResourceState.CopyDest);
        builder.Write(globalInstanceDataHeap, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (transformSystem.Count <= 0)
            return;

        var globalTransformBuffer = graphContext.GetBuffer(globalTransform);
        var globalInstanceHeaderBuffer = graphContext.GetBuffer(globalInstanceHeader);
        var globalInstanceDataHeapBuffer = graphContext.GetBuffer(globalInstanceDataHeap);
        if (globalTransformBuffer == null || globalInstanceHeaderBuffer == null || globalInstanceDataHeapBuffer == null)
            return;

        graphContext.CommandList.UpdateBuffer(
            globalTransformBuffer,
            0,
            (ReadOnlySpan<GpuTransform>)transformSystem.CpuTransforms,
            ResourceStateTransitionMode.Verify
        );

        graphContext.CommandList.UpdateBuffer(
            globalInstanceHeaderBuffer,
            0,
            (ReadOnlySpan<GpuInstanceHeader>)transformSystem.CpuHeaders,
            ResourceStateTransitionMode.Verify
        );

        if (transformSystem.MetadataByteCount > 0)
        {
            graphContext.CommandList.UpdateBuffer(
                globalInstanceDataHeapBuffer,
                0,
                (ReadOnlySpan<byte>)transformSystem.CpuMetadata,
                ResourceStateTransitionMode.Verify
            );
        }
    }
}

internal sealed class ClusterClearBuffersPass(
    RenderGraphHandle indirectDrawArgs,
    RenderGraphHandle candidateArgs,
    RenderGraphHandle candidateCount,
    RenderGraphHandle pageFaultBuffer,
    RenderGraphHandle phase2CandidateCount,
    RenderGraphHandle phase2IndirectDrawArgs,
    RenderGraphHandle zeroOffsetBuffer,
    RenderGraphHandle phase2CandidateArgs
) : IRenderGraphPass
{
    public string Name => "Clear Cluster Buffers";

    public void Setup(RenderGraphBuilder builder)
    {
        if (indirectDrawArgs.IsValid) builder.Write(indirectDrawArgs, ResourceState.CopyDest);
        if (candidateArgs.IsValid) builder.Write(candidateArgs, ResourceState.CopyDest);
        if (candidateCount.IsValid) builder.Write(candidateCount, ResourceState.CopyDest);
        if (pageFaultBuffer.IsValid) builder.Write(pageFaultBuffer, ResourceState.CopyDest);

        if (phase2CandidateCount.IsValid)
            builder.Write(phase2CandidateCount, ResourceState.CopyDest);
        if (phase2IndirectDrawArgs.IsValid)
            builder.Write(phase2IndirectDrawArgs, ResourceState.CopyDest);
        if (zeroOffsetBuffer.IsValid)
            builder.Write(zeroOffsetBuffer, ResourceState.CopyDest);
        if (phase2CandidateArgs.IsValid)
            builder.Write(phase2CandidateArgs, ResourceState.CopyDest);

    }

    public void Execute(RenderGraphContext graphContext)
    {
        var drawArgsBuffer = indirectDrawArgs.IsValid ? graphContext.GetBuffer(indirectDrawArgs) : null;
        var candidateArgsBuffer = candidateArgs.IsValid ? graphContext.GetBuffer(candidateArgs) : null;
        var candidateCountBuffer = candidateCount.IsValid ? graphContext.GetBuffer(candidateCount) : null;
        var pageFault = pageFaultBuffer.IsValid ? graphContext.GetBuffer(pageFaultBuffer) : null;

        Span<uint> resetDrawArgs = [372, 0, 0, 0];
        if (drawArgsBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                drawArgsBuffer,
                0,
                resetDrawArgs,
                ResourceStateTransitionMode.Verify
            );

        Span<uint> resetCandidateArgs = [1, 1, 1, 0];
        if (candidateArgsBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                candidateArgsBuffer,
                0,
                resetCandidateArgs,
                ResourceStateTransitionMode.Verify
            );

        Span<uint> zeroCount = [0u];
        if (candidateCountBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                candidateCountBuffer,
                0,
                zeroCount,
                ResourceStateTransitionMode.Verify
            );
        if (pageFault != null)
            graphContext.CommandList.UpdateBuffer(
                pageFault,
                0,
                zeroCount,
                ResourceStateTransitionMode.Verify
            );

        if (phase2CandidateCount.IsValid)
        {
            var phase2CountBuffer = graphContext.GetBuffer(phase2CandidateCount);
            if (phase2CountBuffer != null)
                graphContext.CommandList.UpdateBuffer(
                    phase2CountBuffer,
                    0,
                    zeroCount,
                    ResourceStateTransitionMode.Verify
                );
        }

        if (phase2IndirectDrawArgs.IsValid)
        {
            var phase2DrawArgsBuffer = graphContext.GetBuffer(phase2IndirectDrawArgs);
            if (phase2DrawArgsBuffer != null)
                graphContext.CommandList.UpdateBuffer(
                    phase2DrawArgsBuffer,
                    0,
                    resetDrawArgs,
                    ResourceStateTransitionMode.Verify
                );
        }

        if (phase2CandidateArgs.IsValid)
        {
            var phase2CandArgsBuf = graphContext.GetBuffer(phase2CandidateArgs);
            if (phase2CandArgsBuf != null)
                graphContext.CommandList.UpdateBuffer(
                    phase2CandArgsBuf,
                    0,
                    resetCandidateArgs,
                    ResourceStateTransitionMode.Verify
                );
        }

        if (zeroOffsetBuffer.IsValid)
        {
            var zeroBuf = graphContext.GetBuffer(zeroOffsetBuffer);
            if (zeroBuf != null)
            {
                Span<uint> zero4 = [0u, 0u, 0u, 0u];
                graphContext.CommandList.UpdateBuffer(
                    zeroBuf, 0, zero4, ResourceStateTransitionMode.Verify);
            }
        }

    }
}

internal sealed class ClusterBVHReadbackPass(ClusterBVHTraversePass bvhPass) : IRenderGraphPass
{
    public string Name => "BVH Readback";

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupReadbackPass(builder);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecuteReadbackPass(graphContext.RenderContext, graphContext);
    }
}

internal sealed class ClusterBVHClearArgsPass(
    ClusterBVHTraversePass bvhPass,
    bool clearArgsA,
    string name
) : IRenderGraphPass
{
    public string Name { get; } = name;

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupClearArgsPass(builder, clearArgsA);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecuteClearArgsPass(graphContext.RenderContext, graphContext, clearArgsA);
    }
}

internal sealed class ClusterBVHInitQueuePass(ClusterBVHTraversePass bvhPass) : IRenderGraphPass
{
    public string Name => "BVH Init Queue";

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupInitQueuePass(builder);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecuteInitQueuePass(graphContext.RenderContext, graphContext);
    }
}

internal sealed class ClusterBVHUpdateArgsPass(
    ClusterBVHTraversePass bvhPass,
    bool targetIsA,
    string name
) : IRenderGraphPass
{
    public string Name { get; } = name;

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupUpdateArgsPass(builder, targetIsA);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecuteUpdateArgsPass(graphContext.RenderContext, graphContext, targetIsA);
    }
}

internal sealed class ClusterBVHTraverseDepthPass(
    ClusterBVHTraversePass bvhPass,
    bool currentIsA,
    int depth,
    string name
) : IRenderGraphPass
{
    public string Name { get; } = name;

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupTraversePass(builder, currentIsA);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecuteTraversePass(graphContext.RenderContext, graphContext, currentIsA, depth);
    }
}

internal sealed class ClusterBVHPageFaultCopyPass(
    ClusterBVHTraversePass bvhPass,
    RenderGraphHandle hPageFaultReadback
) : IRenderGraphPass
{
    public string Name => "BVH Copy Page Faults";

    public void Setup(RenderGraphBuilder builder)
    {
        bvhPass.SetupPageFaultCopyPass(builder, hPageFaultReadback);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        bvhPass.ExecutePageFaultCopyPass(
            graphContext.RenderContext,
            graphContext,
            hPageFaultReadback
        );
    }
}

internal sealed class HiZMip0Pass(
    HiZBuildPass hizPass,
    RenderGraphHandle hDepth,
    RenderGraphHandle hHiZ
) : IRenderGraphPass
{
    public string Name => "HiZ Build Mip0";

    public void Setup(RenderGraphBuilder builder)
    {
        hizPass.SetupMip0(builder, hDepth, hHiZ);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        hizPass.ExecuteMip0(graphContext, hDepth, hHiZ);
    }
}

internal sealed class HiZDownsamplePass(HiZBuildPass hizPass, RenderGraphHandle hHiZ, uint mip)
    : IRenderGraphPass
{
    public string Name { get; } = $"HiZ Downsample Mip{mip}";

    public void Setup(RenderGraphBuilder builder)
    {
        hizPass.SetupDownsample(builder, hHiZ, mip);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        hizPass.ExecuteDownsample(graphContext, hHiZ, mip);
    }
}

internal sealed class ClusterDebugSphereCopyPass(
    ClusterDebugPass debugPass,
    RenderGraphHandle hIndirectDrawArgs,
    RenderGraphHandle hDebugIndirectArgs,
    RenderGraphHandle hCopyUB
) : IRenderGraphPass
{
    public string Name => "Debug Sphere Copy Args";

    public void Setup(RenderGraphBuilder builder)
    {
        debugPass.SetupSphereCopy(builder, hIndirectDrawArgs, hDebugIndirectArgs, hCopyUB);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        debugPass.ExecuteSphereCopy(
            graphContext.RenderContext,
            graphContext,
            hIndirectDrawArgs,
            hDebugIndirectArgs,
            hCopyUB
        );
    }
}

internal sealed class ClusterDebugSphereDrawPass(
    ClusterDebugPass debugPass,
    RenderGraphHandle hVisibleClusters,
    RenderGraphHandle hDebugIndirectArgs,
    RenderGraphHandle hPageHeap,
    RenderGraphHandle hColor,
    RenderGraphHandle hDepth,
    RenderGraphHandle hDrawUB
) : IRenderGraphPass
{
    public string Name => "Debug Sphere Draw";

    public void Setup(RenderGraphBuilder builder)
    {
        debugPass.SetupSphereDraw(
            builder,
            hVisibleClusters,
            hDebugIndirectArgs,
            hDrawUB,
            hPageHeap,
            hColor,
            hDepth
        );
    }

    public void Execute(RenderGraphContext graphContext)
    {
        debugPass.ExecuteSphereDraw(
            graphContext.RenderContext,
            graphContext,
            hVisibleClusters,
            hDebugIndirectArgs,
            hPageHeap,
            hDrawUB
        );
    }
}

internal sealed class ClusterCullUpdateArgsPass : IRenderGraphPass, IDisposable
{
    private readonly RenderContext _context;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private bool _initialized;

    public string Name { get; }

    public RenderGraphHandle HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCandidateArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugCullDrawArgs = RenderGraphHandle.Invalid;

    public ClusterCullUpdateArgsPass(RenderContext context, string passName = "Cull Update Args")
    {
        _context = context;
        Name = passName;
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
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                },
            },
            Cs = cs,
        };

        _pso = device.CreateComputePipelineState(ci);
        if (_pso != null)
            _srb = _pso.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HCandidateCount, ResourceState.UnorderedAccess);
        builder.Write(HCandidateArgs, ResourceState.UnorderedAccess);
        if (HDebugCullDrawArgs.IsValid)
            builder.Write(HDebugCullDrawArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (_pso == null || _srb == null)
            return;

        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null)
            return;

        var count = graphContext.GetBuffer(HCandidateCount);
        var args = graphContext.GetBuffer(HCandidateArgs);
        if (count == null || args == null)
            return;

        _srb.GetVariableByName(ShaderType.Compute, "CandidateCount")
            ?.Set(
                count.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _srb.GetVariableByName(ShaderType.Compute, "CandidateArgs")
            ?.Set(args.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        if (HDebugCullDrawArgs.IsValid)
        {
            var debugArgs = graphContext.GetBuffer(HDebugCullDrawArgs);
            if (debugArgs != null)
            {
                _srb.GetVariableByName(ShaderType.Compute, "DebugCullDrawArgs")
                    ?.Set(
                        debugArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                        SetShaderResourceFlags.None
                    );
            }
        }

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

internal sealed class ClusterDebugReadbackPass : IRenderGraphPass
{
    private readonly RenderContext _context;
    private uint[] _lastCandidateCount = new uint[1];
    private uint[] _lastDrawArgs = new uint[4];
    private uint[] _lastCandidateArgs = new uint[4];
    private uint[] _lastPhase2Count = new uint[1];
    private uint[] _lastPhase2DrawArgs = new uint[4];
    private byte[]? _lastDebugHiZData;

    public uint CandidateCount => _lastCandidateCount[0];
    public uint DrawVertexCount => _lastDrawArgs[0];
    public uint DrawInstanceCount => _lastDrawArgs[1];
    public uint DrawStartVertex => _lastDrawArgs[2];
    public uint DrawStartInstance => _lastDrawArgs[3];
    public uint[] CandidateArgs => _lastCandidateArgs;
    public uint Phase2CandidateCount => _lastPhase2Count[0];
    public uint Phase2DrawVertexCount => _lastPhase2DrawArgs[0];
    public uint Phase2DrawInstanceCount => _lastPhase2DrawArgs[1];
    public byte[]? DebugHiZData => _lastDebugHiZData;

    public RenderGraphHandle HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCandidateArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2CandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2IndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugReadbackBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZReadback = RenderGraphHandle.Invalid;

    public string Name => "Cluster Debug Readback";

    public ClusterDebugReadbackPass(RenderContext context)
    {
        _context = context;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HCandidateCount, ResourceState.CopySource);
        builder.Read(HIndirectDrawArgs, ResourceState.CopySource);
        builder.Read(HCandidateArgs, ResourceState.CopySource);
        if (HPhase2CandidateCount.IsValid)
            builder.Read(HPhase2CandidateCount, ResourceState.CopySource);
        if (HPhase2IndirectDrawArgs.IsValid)
            builder.Read(HPhase2IndirectDrawArgs, ResourceState.CopySource);
        builder.Write(HDebugReadbackBuffer, ResourceState.CopyDest);
        if (HDebugHiZOutput.IsValid)
            builder.Read(HDebugHiZOutput, ResourceState.CopySource);
        if (HDebugHiZReadback.IsValid)
            builder.Write(HDebugHiZReadback, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null)
            return;

        var readbackBuffer = graphContext.GetBuffer(HDebugReadbackBuffer);
        if (readbackBuffer == null)
            return;

        var candidateCount = graphContext.GetBuffer(HCandidateCount);
        var drawArgs = graphContext.GetBuffer(HIndirectDrawArgs);
        var candidateArgs = graphContext.GetBuffer(HCandidateArgs);
        var phase2Count = HPhase2CandidateCount.IsValid
            ? graphContext.GetBuffer(HPhase2CandidateCount)
            : null;
        var phase2DrawArgs = HPhase2IndirectDrawArgs.IsValid
            ? graphContext.GetBuffer(HPhase2IndirectDrawArgs)
            : null;

        if (candidateCount == null || drawArgs == null || candidateArgs == null)
            return;

        var map = ctx.MapBuffer<uint>(readbackBuffer, MapType.Read, MapFlags.DoNotWait);
        if (map.Length >= 10)
        {
            _lastCandidateCount[0] = map[0];
            _lastDrawArgs[0] = map[1];
            _lastDrawArgs[1] = map[2];
            _lastDrawArgs[2] = map[3];
            _lastDrawArgs[3] = map[4];
            _lastCandidateArgs[0] = map[5];
            _lastCandidateArgs[1] = map[6];
            _lastCandidateArgs[2] = map[7];
            _lastCandidateArgs[3] = map[8];
            _lastPhase2Count[0] = phase2Count != null ? map[9] : 0;
            if (map.Length >= 14 && phase2DrawArgs != null)
            {
                _lastPhase2DrawArgs[0] = map[10];
                _lastPhase2DrawArgs[1] = map[11];
                _lastPhase2DrawArgs[2] = map[12];
                _lastPhase2DrawArgs[3] = map[13];
            }
            else
            {
                _lastPhase2DrawArgs[0] = 0;
                _lastPhase2DrawArgs[1] = 0;
                _lastPhase2DrawArgs[2] = 0;
                _lastPhase2DrawArgs[3] = 0;
            }
        }
        ctx.UnmapBuffer(readbackBuffer, MapType.Read);

        ctx.CopyBuffer(
            candidateCount,
            0,
            ResourceStateTransitionMode.Verify,
            readbackBuffer,
            0,
            4,
            ResourceStateTransitionMode.Verify
        );
        ctx.CopyBuffer(
            drawArgs,
            0,
            ResourceStateTransitionMode.Verify,
            readbackBuffer,
            4,
            16,
            ResourceStateTransitionMode.Verify
        );
        ctx.CopyBuffer(
            candidateArgs,
            0,
            ResourceStateTransitionMode.Verify,
            readbackBuffer,
            20,
            16,
            ResourceStateTransitionMode.Verify
        );
        if (phase2Count != null)
        {
            ctx.CopyBuffer(
                phase2Count,
                0,
                ResourceStateTransitionMode.Verify,
                readbackBuffer,
                36,
                4,
                ResourceStateTransitionMode.Verify
            );
        }
        if (phase2DrawArgs != null)
        {
            ctx.CopyBuffer(
                phase2DrawArgs,
                0,
                ResourceStateTransitionMode.Verify,
                readbackBuffer,
                40,
                16,
                ResourceStateTransitionMode.Verify
            );
        }

        // Readback DebugHiZOutput
        if (HDebugHiZOutput.IsValid && HDebugHiZReadback.IsValid)
        {
            var debugSrc = graphContext.GetBuffer(HDebugHiZOutput);
            var debugDst = graphContext.GetBuffer(HDebugHiZReadback);
            if (debugSrc != null && debugDst != null)
            {
                // Read previous frame's data
                var debugMap = ctx.MapBuffer<uint>(debugDst, MapType.Read, MapFlags.DoNotWait);
                if (debugMap.Length > 0)
                {
                    // Convert uint span to byte array
                    var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(debugMap);
                    _lastDebugHiZData = byteSpan.ToArray();
                }
                else
                {
                    _lastDebugHiZData = null;
                }
                ctx.UnmapBuffer(debugDst, MapType.Read);

                // Copy current frame's data for next frame readback
                var srcDesc = debugSrc.GetDesc();
                ctx.CopyBuffer(
                    debugSrc,
                    0,
                    ResourceStateTransitionMode.Verify,
                    debugDst,
                    0,
                    srcDesc.Size,
                    ResourceStateTransitionMode.Verify
                );
            }
        }
    }
}

