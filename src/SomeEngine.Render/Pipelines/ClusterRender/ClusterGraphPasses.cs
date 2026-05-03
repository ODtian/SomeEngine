using System;
using System.IO;
using System.Text;
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
    private readonly RenderContext _context;
    private readonly ClusterUploadStage.Resources _resources;

    public IReadOnlyList<ClusterResourceManager.BVHPatchData>? Patches;
    public RenderGraphHandle HGlobalBVH;
    public RenderGraphHandle HPatchBuffer;
    public RenderGraphHandle HPatchUniforms;

    public string Name => "Cluster BVH Patch";

    public ClusterBVHPatchPass(RenderContext context, ClusterUploadStage.Resources resources)
    {
        _context = context;
        _resources = resources;
        _resources.EnsureInitialized(context);
    }

    public void Init() => _resources.EnsureInitialized(_context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Write(HGlobalBVH, ResourceState.UnorderedAccess);
        builder.Read(HPatchBuffer, ResourceState.ShaderResource);
        builder.Read(HPatchUniforms, ResourceState.ConstantBuffer);
    }

    public struct PatchUniforms
    {
        public uint PatchCount;
        public uint Pad0, Pad1, Pad2;
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (Patches == null || Patches.Count == 0) return;
        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null || _resources.PatchPSO == null) return;

        var bvhBuffer = graphContext.GetBuffer(HGlobalBVH);
        var patchBuffer = graphContext.GetBuffer(HPatchBuffer);
        var uniformsBuffer = graphContext.GetBuffer(HPatchUniforms);

        if (bvhBuffer == null || patchBuffer == null || uniformsBuffer == null) return;

        var uniforms = new PatchUniforms
        {
            PatchCount = (uint)Patches.Count,
            Pad0 = 0, Pad1 = 0, Pad2 = 0,
        };
        var uSpan = ctx.MapBuffer<PatchUniforms>(uniformsBuffer, MapType.Write, MapFlags.Discard);
        uSpan[0] = uniforms;
        ctx.UnmapBuffer(uniformsBuffer, MapType.Write);

        var pSpan = ctx.MapBuffer<ClusterResourceManager.BVHPatchData>(
            patchBuffer, MapType.Write, MapFlags.Discard);
        for (int i = 0; i < Patches.Count; i++)
            pSpan[i] = Patches[i];
        ctx.UnmapBuffer(patchBuffer, MapType.Write);

        var srb = _resources.PatchPool.Rent(_resources.PatchPSO);

        srb.GetVariableByReflectedBinding(_context, _resources.PatchShaderAsset, ShaderType.Compute, "GlobalBVH")
            ?.Set(bvhBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(_context, _resources.PatchShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformsBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(_context, _resources.PatchShaderAsset, ShaderType.Compute, "Patches")
            ?.Set(patchBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_resources.PatchPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        
        uint groups = ((uint)Patches.Count + 63) / 64;
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = groups,
            ThreadGroupCountY = 1,
            ThreadGroupCountZ = 1,
        });

        _resources.PatchPool.Return(srb);
    }

    public void Dispose() { }
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
            ResourceStateTransitionMode.None
        );

        graphContext.CommandList.UpdateBuffer(
            globalInstanceHeaderBuffer,
            0,
            (ReadOnlySpan<byte>)transformSystem.CpuHeaders,
            ResourceStateTransitionMode.None
        );

        if (transformSystem.MetadataByteCount > 0)
        {
            graphContext.CommandList.UpdateBuffer(
                globalInstanceDataHeapBuffer,
                0,
                (ReadOnlySpan<byte>)transformSystem.CpuMetadata,
                ResourceStateTransitionMode.None
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

        // CullDrawArgs raw layout: [0]=vertex count per cluster, [4]=SW count, [8]=HW count, [12]=total reservation counter.
        Span<uint> resetDrawArgs = [96, 0, 0, 0, 0];
        if (drawArgsBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                drawArgsBuffer,
                0,
                resetDrawArgs,
                ResourceStateTransitionMode.None
            );

        Span<uint> resetCandidateArgs = [1, 1, 1, 0];
        if (candidateArgsBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                candidateArgsBuffer,
                0,
                resetCandidateArgs,
                ResourceStateTransitionMode.None
            );

        Span<uint> zeroCount = [0u];
        if (candidateCountBuffer != null)
            graphContext.CommandList.UpdateBuffer(
                candidateCountBuffer,
                0,
                zeroCount,
                ResourceStateTransitionMode.None
            );
        if (pageFault != null)
            graphContext.CommandList.UpdateBuffer(
                pageFault,
                0,
                zeroCount,
                ResourceStateTransitionMode.None
            );

        if (phase2CandidateCount.IsValid)
        {
            var phase2CountBuffer = graphContext.GetBuffer(phase2CandidateCount);
            if (phase2CountBuffer != null)
                graphContext.CommandList.UpdateBuffer(
                    phase2CountBuffer,
                    0,
                    zeroCount,
                    ResourceStateTransitionMode.None
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
                    ResourceStateTransitionMode.None
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
                    ResourceStateTransitionMode.None
                );
        }

        if (zeroOffsetBuffer.IsValid)
        {
            var zeroBuf = graphContext.GetBuffer(zeroOffsetBuffer);
            if (zeroBuf != null)
            {
                Span<uint> zero4 = [0u, 0u, 0u, 0u];
                graphContext.CommandList.UpdateBuffer(
                    zeroBuf, 0, zero4, ResourceStateTransitionMode.None);
            }
        }

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




internal sealed class ClusterCullUpdateArgsPass : IRenderGraphPass, IDisposable
{
    private readonly RenderContext _context;
    private readonly ClusterCull.Resources _resources;

    public string Name { get; }

    public RenderGraphHandle HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCandidateArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCullingUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugCullDrawArgs = RenderGraphHandle.Invalid;

    public ClusterCullUpdateArgsPass(
        RenderContext context,
        ClusterCull.Resources resources,
        string passName = "Cull Update Args")
    {
        _context = context;
        _resources = resources;
        Name = passName;
        _resources.EnsureUpdateArgsInitialized(context);
    }

    public void Init() => _resources.EnsureUpdateArgsInitialized(_context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.ReadWrite(HCandidateCount, ResourceState.UnorderedAccess);
        builder.Write(HCandidateArgs, ResourceState.UnorderedAccess);
        if (HCullingUniforms.IsValid)
            builder.Read(HCullingUniforms, ResourceState.ConstantBuffer);
        if (HDebugCullDrawArgs.IsValid)
            builder.Write(HDebugCullDrawArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (_resources.UpdateArgsPSO == null) return;
        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null) return;

        var count = graphContext.GetBuffer(HCandidateCount);
        var args = graphContext.GetBuffer(HCandidateArgs);
        if (count == null || args == null) return;

        var srb = _resources.UpdateArgsPool.Rent(_resources.UpdateArgsPSO);

        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "CandidateCount")
            ?.Set(count.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "CandidateArgs")
            ?.Set(args.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (HCullingUniforms.IsValid)
        {
            var uniforms = graphContext.GetBuffer(HCullingUniforms);
            if (uniforms != null)
                srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "Uniforms")
                    ?.Set(uniforms, SetShaderResourceFlags.None);
        }

        if (HDebugCullDrawArgs.IsValid)
        {
            var debugArgs = graphContext.GetBuffer(HDebugCullDrawArgs);
            if (debugArgs != null)
                srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "DebugCullDrawArgs")
                    ?.Set(debugArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        ctx.SetPipelineState(_resources.UpdateArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1,
        });

        _resources.UpdateArgsPool.Return(srb);
    }

    public void Dispose() { }
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
    private bool _dumpHiZThisFrame;

    public uint CandidateCount => _lastCandidateCount[0];
    // DrawArgs layout = CullDrawArgs: [VertexCountPerCluster, SWCount, HWCount], with byte 12 used as a hidden total reservation counter.
    public uint DrawVertexCount => _lastDrawArgs[0];
    public uint DrawSWCount => _lastDrawArgs[1];
    public uint DrawHWCount => _lastDrawArgs[2];
    public uint DrawInstanceCount => _lastDrawArgs[1] + _lastDrawArgs[2]; // Total visible = SW + HW
    public uint[] CandidateArgs => _lastCandidateArgs;
    public uint Phase2CandidateCount => _lastPhase2Count[0];
    public uint Phase2DrawSWCount => _lastPhase2DrawArgs[1];
    public uint Phase2DrawHWCount => _lastPhase2DrawArgs[2];
    public uint Phase2DrawVertexCount => _lastPhase2DrawArgs[0];
    public uint Phase2DrawInstanceCount => _lastPhase2DrawArgs[1] + _lastPhase2DrawArgs[2];
    public byte[]? DebugHiZData => _lastDebugHiZData;
    public string? LastHiZDumpPath { get; private set; }

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

    /// <summary>
    /// 创建 readback buffer 并添加 debug readback pass 到 RenderGraph。
    /// </summary>
    public void AddPasses(
        RenderGraph graph,
        in ClusterTraverseOutput traverse,
        in ClusterCullOutput cull,
        bool dumpHiZData = false)
    {
        HCandidateCount = traverse.CandidateCount;
        HIndirectDrawArgs = cull.DrawArgs;
        HCandidateArgs = traverse.CandidateArgs;
        HPhase2CandidateCount = cull.Phase2CandidateCount;
        HPhase2IndirectDrawArgs = cull.Phase2DrawArgs;
        HDebugHiZOutput = dumpHiZData ? cull.DebugHiZOutput : RenderGraphHandle.Invalid;
        _dumpHiZThisFrame = dumpHiZData;

        var hDebugReadback = graph.CreateBuffer("DebugReadback", new BufferDesc
        {
            Size = 256,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        graph.MarkOutput(hDebugReadback);
        HDebugReadbackBuffer = hDebugReadback;

        if (dumpHiZData && HDebugHiZOutput.IsValid)
        {
            HDebugHiZReadback = graph.CreateBuffer("DebugHiZReadback", new BufferDesc
            {
                Size = ClusterHiZDebugLayout.BufferBytes,
                Usage = Usage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            graph.MarkOutput(HDebugHiZReadback);
        }
        else
        {
            HDebugHiZReadback = RenderGraphHandle.Invalid;
        }

        graph.AddPass(this);
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
        if (map.Length >= 14)
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
            if (phase2DrawArgs != null)
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

        ctx.CopyBuffer(candidateCount, 0, ResourceStateTransitionMode.None, readbackBuffer, 0, 4, ResourceStateTransitionMode.None);
        ctx.CopyBuffer(drawArgs, 0, ResourceStateTransitionMode.None, readbackBuffer, 4, 16, ResourceStateTransitionMode.None);
        ctx.CopyBuffer(candidateArgs, 0, ResourceStateTransitionMode.None, readbackBuffer, 20, 16, ResourceStateTransitionMode.None);
        if (phase2Count != null)
        {
            ctx.CopyBuffer(phase2Count, 0, ResourceStateTransitionMode.None, readbackBuffer, 36, 4, ResourceStateTransitionMode.None);
        }
        if (phase2DrawArgs != null)
        {
            ctx.CopyBuffer(phase2DrawArgs, 0, ResourceStateTransitionMode.None, readbackBuffer, 40, 16, ResourceStateTransitionMode.None);
        }

        // One-shot HiZ debug dump. This uses an RG staging buffer and waits only
        // on explicit F5 dump frames, so normal frames keep the async readback path.
        if (HDebugHiZOutput.IsValid && HDebugHiZReadback.IsValid)
        {
            var debugSrc = graphContext.GetBuffer(HDebugHiZOutput);
            var debugDst = graphContext.GetBuffer(HDebugHiZReadback);
            if (debugSrc != null && debugDst != null)
            {
                var srcDesc = debugSrc.GetDesc();
                ulong copyBytes = Math.Min(srcDesc.Size, ClusterHiZDebugLayout.BufferBytes);
                ctx.CopyBuffer(
                    debugSrc,
                    0,
                    ResourceStateTransitionMode.None,
                    debugDst,
                    0,
                    copyBytes,
                    ResourceStateTransitionMode.None
                );

                ctx.WaitForIdle();

                var debugMap = ctx.MapBuffer<uint>(debugDst, MapType.Read, MapFlags.None);
                if (debugMap.Length > 0)
                {
                    var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(debugMap);
                    _lastDebugHiZData = byteSpan.ToArray();
                    if (_dumpHiZThisFrame)
                        WriteHiZDump(debugMap);
                }
                else
                {
                    _lastDebugHiZData = null;
                }
                ctx.UnmapBuffer(debugDst, MapType.Read);
            }
        }
    }

    private void WriteHiZDump(ReadOnlySpan<uint> words)
    {
        Directory.CreateDirectory("dump");
        string path = Path.GetFullPath(Path.Combine(
            "dump",
            $"hiz_cull_dump_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt"));

        var text = BuildHiZDumpText(words, includeAllSamples: true);
        File.WriteAllText(path, text);
        LastHiZDumpPath = path;

        Console.Write(BuildHiZDumpText(words, includeAllSamples: false));
        Console.WriteLine($"[HiZ Dump] Full sample dump: {path}");
    }

    private static string BuildHiZDumpText(ReadOnlySpan<uint> words, bool includeAllSamples)
    {
        var sb = new StringBuilder(64 * 1024);

        uint sampleCount = ReadWord(words, ClusterHiZDebugLayout.SampleCountWord);
        uint captured = Math.Min(sampleCount, ClusterHiZDebugLayout.MaxSamples);
        uint overflow = ReadWord(words, ClusterHiZDebugLayout.SampleOverflowWord);

        sb.AppendLine("[HiZ Dump]");
        sb.AppendLine($"  samples: recorded={sampleCount} captured={captured} overflow={overflow}");
        sb.AppendLine($"  layout: headerBytes={ReadWord(words, ClusterHiZDebugLayout.HeaderBytesWord)} strideBytes={ReadWord(words, ClusterHiZDebugLayout.SampleStrideBytesWord)} capacity={ReadWord(words, ClusterHiZDebugLayout.SampleCapacityWord)}");
        sb.AppendLine(
            "  phase1: " +
            $"input={ReadWord(words, ClusterHiZDebugLayout.Phase1InputWord)} " +
            $"lodRejected={ReadWord(words, ClusterHiZDebugLayout.Phase1LodRejectedWord)} " +
            $"noPrevHistory={ReadWord(words, ClusterHiZDebugLayout.Phase1NoHistoryWord)} " +
            $"tested={ReadWord(words, ClusterHiZDebugLayout.Phase1TestedWord)} " +
            $"deferredToP2={ReadWord(words, ClusterHiZDebugLayout.Phase1DeferredWord)} " +
            $"drawn={ReadWord(words, ClusterHiZDebugLayout.Phase1DrawnWord)} " +
            $"depthVisible={ReadWord(words, ClusterHiZDebugLayout.Phase1DepthVisibleWord)}");
        sb.AppendLine(
            "  phase2: " +
            $"input={ReadWord(words, ClusterHiZDebugLayout.Phase2InputWord)} " +
            $"lodRejected={ReadWord(words, ClusterHiZDebugLayout.Phase2LodRejectedWord)} " +
            $"tested={ReadWord(words, ClusterHiZDebugLayout.Phase2TestedWord)} " +
            $"culled={ReadWord(words, ClusterHiZDebugLayout.Phase2CulledWord)} " +
            $"drawn={ReadWord(words, ClusterHiZDebugLayout.Phase2DrawnWord)} " +
            $"depthVisible={ReadWord(words, ClusterHiZDebugLayout.Phase2DepthVisibleWord)}");
        sb.AppendLine($"  hizDisabledTests={ReadWord(words, ClusterHiZDebugLayout.HiZDisabledWord)}");

        uint sampleLimit = includeAllSamples ? captured : Math.Min(captured, 32u);
        if (sampleLimit > 0)
            sb.AppendLine("  samples:");

        for (uint i = 0; i < sampleLimit; i++)
        {
            int baseWord = (int)(ClusterHiZDebugLayout.HeaderWords + i * (ClusterHiZDebugLayout.SampleStrideBytes / sizeof(uint)));
            if (baseWord + 19 >= words.Length)
                break;

            float minU = UIntToFloat(words[baseWord + 0]);
            float minV = UIntToFloat(words[baseWord + 1]);
            float maxU = UIntToFloat(words[baseWord + 2]);
            float maxV = UIntToFloat(words[baseWord + 3]);
            float nearDepth = UIntToFloat(words[baseWord + 4]);
            float hizDepth = UIntToFloat(words[baseWord + 5]);
            uint mip = words[baseWord + 6];
            uint flags = words[baseWord + 7];
            float centerX = UIntToFloat(words[baseWord + 8]);
            float centerY = UIntToFloat(words[baseWord + 9]);
            float centerZ = UIntToFloat(words[baseWord + 10]);
            float radius = UIntToFloat(words[baseWord + 11]);
            uint pageOffset = words[baseWord + 12];
            uint clusterId = words[baseWord + 13];
            uint instanceId = words[baseWord + 14];
            float boundsExpansion = UIntToFloat(words[baseWord + 15]);
            float pixelW = UIntToFloat(words[baseWord + 16]);
            float pixelH = UIntToFloat(words[baseWord + 17]);
            float depthDelta = UIntToFloat(words[baseWord + 18]);
            float epsilon = UIntToFloat(words[baseWord + 19]);
            bool occluded = (flags & 1u) != 0;
            bool usePrev = (flags & 2u) != 0;
            bool expanded = (flags & 4u) != 0;
            bool fullScreen = (flags & 8u) != 0;
            bool hizDisabled = (flags & 16u) != 0;

            sb.Append("    [").Append(i).Append("] ")
                .Append(usePrev ? "P1(prev)" : "P2(curr)")
                .Append(occluded ? " occluded" : " visible")
                .Append(" mip=").Append(mip)
                .Append(" uv=(").Append(minU.ToString("F4")).Append(',').Append(minV.ToString("F4"))
                .Append(")-(").Append(maxU.ToString("F4")).Append(',').Append(maxV.ToString("F4")).Append(')')
                .Append(" px=").Append(pixelW.ToString("F1")).Append('x').Append(pixelH.ToString("F1"))
                .Append(" near=").Append(nearDepth.ToString("F6"))
                .Append(" hiz=").Append(hizDepth.ToString("F6"))
                .Append(" delta=").Append(depthDelta.ToString("F6"))
                .Append(" eps=").Append(epsilon.ToString("F6"))
                .Append(" inst=").Append(instanceId)
                .Append(" cluster=").Append(clusterId)
                .Append(" page=").Append(pageOffset)
                .Append(" center=(").Append(centerX.ToString("F2")).Append(',').Append(centerY.ToString("F2")).Append(',').Append(centerZ.ToString("F2")).Append(')')
                .Append(" radius=").Append(radius.ToString("F3"));

            if (boundsExpansion > 0.0f || expanded)
                sb.Append(" expand=").Append(boundsExpansion.ToString("F3"));
            if (fullScreen)
                sb.Append(" fullscreenBounds");
            if (hizDisabled)
                sb.Append(" hizDisabled");
            sb.AppendLine();
        }

        if (!includeAllSamples && captured > sampleLimit)
            sb.AppendLine($"  ... {captured - sampleLimit} more samples written to file");

        return sb.ToString();
    }

    private static uint ReadWord(ReadOnlySpan<uint> words, int index)
        => index >= 0 && index < words.Length ? words[index] : 0;

    private static float UIntToFloat(uint value)
        => BitConverter.UInt32BitsToSingle(value);
}

