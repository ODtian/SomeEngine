using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
public struct DeformUniforms
{
    public Vector3 QuantOrigin;
    public float QuantStep;
    public uint MaxVisibleClusters;
    public uint MaxDeformVertices;
    public uint MaxRasterBins;
    public uint MaxClusterVertices;
    public uint CurrentBin;
    public uint _pad0;
    public uint _pad1;
    public uint _pad2;
}

public static class ClusterDeformPSOs
{
    internal static IPipelineState? PrepareVisibleArgsPSO;
    internal static IPipelineState? InitVisiblePSO;
    internal static readonly ConcurrentBag<IShaderResourceBinding> PrepareVisibleArgsSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> InitVisibleSRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static IShaderResourceBinding RentPrepareVisibleArgsSRB()
        => PrepareVisibleArgsSRBPool.TryTake(out var srb) ? srb : PrepareVisibleArgsPSO!.CreateShaderResourceBinding(false);

    internal static IShaderResourceBinding RentInitVisibleSRB()
        => InitVisibleSRBPool.TryTake(out var srb) ? srb : InitVisiblePSO!.CreateShaderResourceBinding(false);

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string path = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/cluster_deform.slang"
                )
            );
            var shaderAsset = SlangShaderImporter.Import(path);

            var layout = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            // DeformWavePSO/DeformStaticPSO removed — now built from material pipeline PSO groups



            using var csPrepareVisible = shaderAsset.CreateShader(context, "CSDeformPrepareVisibleArgs");
            PrepareVisibleArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformPrepareVisibleArgs",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csPrepareVisible,
            });

            using var csInitVisible = shaderAsset.CreateShader(context, "CSDeformInitVisible");
            InitVisiblePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformInitVisible",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csInitVisible,
            });


            s_initialized = true;
        }
    }
}

public class ClusterDeformPass(
    RenderContext context,
    string passName = "ClusterDeform"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    public RenderGraphHandle HBinnedClusterIndex = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedSWDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;

    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public ShadePSOGroup[]? PSOGroups;
    public DeformUniforms DeformUniformData;

    public void Init() { }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinnedClusterIndex, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HRasterBinMeta, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HBinnedSWDispatchArgs, ResourceState.IndirectArgument);
        builder.Write(HDeformCache, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheOffsets, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx2 = context.ImmediateContext;
        if (ctx2 == null || PSOGroups == null || PSOGroups.Length == 0) return;

        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var binnedBuf = rgCtx.GetBuffer(HBinnedClusterIndex);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var rasterBinMetaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var deformCacheBuf = rgCtx.GetBuffer(HDeformCache);
        var cacheOffsetBuf = rgCtx.GetBuffer(HCacheOffsets);
        var swDispatchArgsBuf = rgCtx.GetBuffer(HBinnedSWDispatchArgs);
        if (uniformBuf == null || binnedBuf == null || visibleBuf == null ||
            rasterBinMetaBuf == null || pageHeap == null ||
            deformCacheBuf == null || cacheOffsetBuf == null || swDispatchArgsBuf == null)
            return;

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);

        var uniformData = DeformUniformData;

        foreach (var group in PSOGroups)
        {
            if (group.PSO == null) continue;

            var srb = group.PSO.CreateShaderResourceBinding(false);
            srb.GetVariableByName(ShaderType.Compute, "Uniforms")
                ?.Set(uniformBuf, SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binnedBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
                ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "RasterBinMeta")
                ?.Set(rasterBinMetaBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            if (globalTransformView != null)
            {
                srb.GetVariableByName(ShaderType.Compute, "Instances")
                    ?.Set(globalTransformView, SetShaderResourceFlags.None);
            }
            srb.GetVariableByName(ShaderType.Compute, "DeformCache")
                ?.Set(deformCacheBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "CacheOffsets")
                ?.Set(cacheOffsetBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

            ctx2.SetPipelineState(group.PSO);
            ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);

            for (int i = 0; i < group.BinCount; i++)
            {
                int bin = group.BinStart + i;
                uniformData.CurrentBin = (uint)bin;
                var mapped = ctx2.MapBuffer<DeformUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx2.UnmapBuffer(uniformBuf, MapType.Write);

                ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = swDispatchArgsBuf,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                    DispatchArgsByteOffset = (ulong)(bin * 12),
                });
            }
        }
    }

    public void Dispose() { }
}

public class ClusterDeformPrepareVisibleArgsPass(
    RenderContext context,
    string passName = "DeformPrepareVisibleArgs"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HDeformDispatchArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformPSOs.EnsureInitialized(context);
        var pso = ClusterDeformPSOs.PrepareVisibleArgsPSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        if (drawArgsBuf == null || dispatchArgsBuf == null) return;

        var srb = ClusterDeformPSOs.RentPrepareVisibleArgsSRB();
        srb.GetVariableByName(ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "DeformDispatchArgs")
            ?.Set(dispatchArgsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        ClusterDeformPSOs.PrepareVisibleArgsSRBPool.Add(srb);
    }
}

public class ClusterDeformInitVisiblePass(
    RenderContext context,
    string passName = "DeformInitVisible"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheAllocCounter = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDeformDispatchArgs, ResourceState.IndirectArgument);
        builder.Write(HCacheOffsets, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheAllocCounter, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformPSOs.EnsureInitialized(context);
        var pso = ClusterDeformPSOs.InitVisiblePSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeapBuf = rgCtx.GetBuffer(HPageHeap);
        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        var cacheOffsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
        var cacheAllocBuf = rgCtx.GetBuffer(HCacheAllocCounter);
        if (drawArgsBuf == null || readOffsetBuf == null || visibleBuf == null || pageHeapBuf == null ||
            uniformBuf == null || dispatchArgsBuf == null || cacheOffsetsBuf == null ||
            cacheAllocBuf == null)
            return;

        var srb = ClusterDeformPSOs.RentInitVisibleSRB();
        srb.GetVariableByName(ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "ReadOffsetArgs")
            ?.Set(readOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeapBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CacheOffsets")
            ?.Set(cacheOffsetsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CacheAllocCounter")
            ?.Set(cacheAllocBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None
        });

        ClusterDeformPSOs.InitVisibleSRBPool.Add(srb);
    }
}
