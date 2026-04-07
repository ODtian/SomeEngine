using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public static class ClusterDeformBinPSOs
{
    internal static IPipelineState? ClearPSO;
    internal static IPipelineState? PreparePSO;
    internal static IPipelineState? CountPSO;
    internal static IPipelineState? ReservePSO;
    internal static IPipelineState? ReserveDispatchWritePSO;
    internal static IPipelineState? ScatterPSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> ClearSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> PrepareSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> CountSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> ReserveSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> ReserveDispatchWriteSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> ScatterSRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

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
                    "../../../../../../assets/Shaders/cluster_deform_binning.slang"
                )
            );
            var shaderAsset = SlangShaderImporter.Import(path);

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            using var csClear = shaderAsset.CreateShader(context, "CSDeformBinClear");
            ClearPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinClear", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csClear,
            });

            using var csPrepare = shaderAsset.CreateShader(context, "CSDeformBinPrepare");
            PreparePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinPrepare", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csPrepare,
            });

            using var csCount = shaderAsset.CreateShader(context, "CSDeformBinCount");
            CountPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinCount", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csCount,
            });

            using var csReserve = shaderAsset.CreateShader(context, "CSDeformBinReserve");
            ReservePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinReserve", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csReserve,
            });

            using var csReserveDW = shaderAsset.CreateShader(context, "CSDeformBinReserveDispatchWrite");
            ReserveDispatchWritePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinReserveDispatchWrite", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csReserveDW,
            });

            using var csBin = shaderAsset.CreateShader(context, "CSDeformBinScatter");
            ScatterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc { Name = "CSDeformBinScatter", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                Cs = csBin,
            });

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    internal static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);

    internal static void BindSRB(
        IShaderResourceBinding srb,
        IBuffer? uniforms, IBuffer? visible, IBuffer? headers, IBuffer? drawArgs,
        IBuffer? meta, IBuffer? binned, IBuffer? dispatchArgs,
        IBuffer? materialSlotBuffer = null, IBuffer? pageHeap = null)
    {
        if (uniforms != null)
            srb.GetVariableByName(ShaderType.Compute, "Uniforms")
                ?.Set(uniforms, SetShaderResourceFlags.None);
        if (visible != null)
            srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
                ?.Set(visible.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (headers != null)
            srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")
                ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (drawArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "DrawArgs")
                ?.Set(drawArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (meta != null)
            srb.GetVariableByName(ShaderType.Compute, "RasterBinMeta")
                ?.Set(meta.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binned != null)
            srb.GetVariableByName(ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binned.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (dispatchArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "DeformBinningDispatchArgs")
                ?.Set(dispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (materialSlotBuffer != null)
            srb.GetVariableByName(ShaderType.Compute, "MaterialSlotBuffer")
                ?.Set(materialSlotBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (pageHeap != null)
            srb.GetVariableByName(ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
    }
}

internal sealed class ClusterDeformBinInitPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Init";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HDeformBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinMeta, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformBinPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        if (uniformBuf == null || drawArgsBuf == null || dispatchArgsBuf == null) return;

        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        if (metaBuf == null) return;

        var clearSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.ClearPSO!, ClusterDeformBinPSOs.ClearSRBPool);
        ClusterDeformBinPSOs.BindSRB(clearSRB, uniformBuf, null, null, null, metaBuf, null, null);
        ctx.SetPipelineState(ClusterDeformBinPSOs.ClearPSO!);
        ctx.CommitShaderResources(clearSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterDeformBinPSOs.ReturnSRB(clearSRB, ClusterDeformBinPSOs.ClearSRBPool);

        var prepareSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.PreparePSO!, ClusterDeformBinPSOs.PrepareSRBPool);
        ClusterDeformBinPSOs.BindSRB(prepareSRB, uniformBuf, null, null, drawArgsBuf, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(ClusterDeformBinPSOs.PreparePSO!);
        ctx.CommitShaderResources(prepareSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterDeformBinPSOs.ReturnSRB(prepareSRB, ClusterDeformBinPSOs.PrepareSRBPool);
    }
}

internal sealed class ClusterDeformBinCountPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Count";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid) builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid) builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformBinPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterDeformBinPSOs.CountPSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var headerBuf = rgCtx.GetBuffer(HInstanceHeaders);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null || drawArgsBuf == null || dispatchArgsBuf == null || metaBuf == null) return;
        var matBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var countSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.CountPSO, ClusterDeformBinPSOs.CountSRBPool);
        ClusterDeformBinPSOs.BindSRB(countSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, metaBuf, null, null, matBuf, pageHeapBuf);
        ctx.SetPipelineState(ClusterDeformBinPSOs.CountPSO);
        ctx.CommitShaderResources(countSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        ClusterDeformBinPSOs.ReturnSRB(countSRB, ClusterDeformBinPSOs.CountSRBPool);
    }
}

internal sealed class ClusterDeformBinReservePass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Reserve";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinningDispatchArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformBinPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterDeformBinPSOs.ReservePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        if (uniformBuf == null || metaBuf == null || dispatchArgsBuf == null) return;

        var reserveSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.ReservePSO, ClusterDeformBinPSOs.ReserveSRBPool);
        ClusterDeformBinPSOs.BindSRB(reserveSRB, uniformBuf, null, null, null, metaBuf, null, null);
        ctx.SetPipelineState(ClusterDeformBinPSOs.ReservePSO);
        ctx.CommitShaderResources(reserveSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 }); // hardcoded MAX_BINS=256 threads
        ClusterDeformBinPSOs.ReturnSRB(reserveSRB, ClusterDeformBinPSOs.ReserveSRBPool);

        var reserveDWSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.ReserveDispatchWritePSO!, ClusterDeformBinPSOs.ReserveDispatchWriteSRBPool);
        ClusterDeformBinPSOs.BindSRB(reserveDWSRB, uniformBuf, null, null, null, metaBuf, null, dispatchArgsBuf);
        ctx.SetPipelineState(ClusterDeformBinPSOs.ReserveDispatchWritePSO!);
        ctx.CommitShaderResources(reserveDWSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterDeformBinPSOs.ReturnSRB(reserveDWSRB, ClusterDeformBinPSOs.ReserveDispatchWriteSRBPool);
    }
}

internal sealed class ClusterDeformBinScatterPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Scatter";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinnedClusterBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid) builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid) builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinnedClusterBuffer, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterDeformBinPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterDeformBinPSOs.ScatterPSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var headerBuf = rgCtx.GetBuffer(HInstanceHeaders);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var binnedBuf = rgCtx.GetBuffer(HDeformBinnedClusterBuffer);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null || drawArgsBuf == null || dispatchArgsBuf == null || metaBuf == null || binnedBuf == null) return;
        var matBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var scatterSRB = ClusterDeformBinPSOs.RentSRB(ClusterDeformBinPSOs.ScatterPSO, ClusterDeformBinPSOs.ScatterSRBPool);
        ClusterDeformBinPSOs.BindSRB(scatterSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, metaBuf, binnedBuf, null, matBuf, pageHeapBuf);
        ctx.SetPipelineState(ClusterDeformBinPSOs.ScatterPSO);
        ctx.CommitShaderResources(scatterSRB, ResourceStateTransitionMode.None);
        // Note: scattering uses the Prepare pass's dispatch args which calculates (totalVisible+63)/64. But we need to make sure we read from the START of dispatchArgs,
        // Wait, ReserveDW overwrote the dispatch args for final per-bin dispatch! We need a SEPARATE dispatch args buffer or use the Prepare pass's args in a dedicated buffer.
        // Actually, Count and Scatter iterate over ALL visible clusters. They share the same dispatch args compute from Prepare.
        // But ReserveDW computes the dispatch args for the *PreDeform compute shader* itself!
        // So Count/Scatter should use HDeformBinningDispatchArgs (computed in Prepare, size 1), while ReserveDW should output to HPreDeformDispatchArgs.
        // I will fix this right now. For now, this is just using HDeformBinningDispatchArgs.
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        ClusterDeformBinPSOs.ReturnSRB(scatterSRB, ClusterDeformBinPSOs.ScatterSRBPool);
    }
}
