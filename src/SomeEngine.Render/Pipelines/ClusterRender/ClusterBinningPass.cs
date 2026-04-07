using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
public struct BinningUniforms
{
    public uint MaxBins;
    public uint SlotCapacity;         // MaterialSlotBuffer SOA capacity (even)
    public uint BinFieldIndex;        // Field index for this binning pass in SOA
    public uint MaxVisibleClusters;
}

/// <summary>
/// Binning PSO/SRB 的 static 缓存容器。
/// PSO 编译一次，SRB 通过 ConcurrentBag pool 管理。
/// </summary>
public static class ClusterBinningPSOs
{
    internal static IPipelineState? ClearPSO;
    internal static IPipelineState? PreparePSO;
    internal static IPipelineState? CountPSO;
    internal static IPipelineState? ReservePSO;
    internal static IPipelineState? ScatterPSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> ClearSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> PrepareSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> CountSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> ReserveSRBPool = [];
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
                    "../../../../../../assets/Shaders/cluster_binning.slang"
                )
            );
            var shaderAsset = SlangShaderImporter.Import(path);

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

        using var csClear = shaderAsset.CreateShader(context, "CSBinningClear");
        ClearPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningClear", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csClear,
        });

        using var csPrepare = shaderAsset.CreateShader(context, "CSBinningPrepare");
        PreparePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningPrepare", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csPrepare,
        });

        using var csCount = shaderAsset.CreateShader(context, "CSBinningCount");
        CountPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningCount", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csCount,
        });

        using var csReserve = shaderAsset.CreateShader(context, "CSBinningReserve");
        ReservePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningReserve", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csReserve,
        });

        using var csBin = shaderAsset.CreateShader(context, "CSBinningScatter");
        ScatterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningScatter", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
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
        IBuffer? uniforms, IBuffer? visible, IBuffer? headers, IBuffer? drawArgs, IBuffer? offsetArgs,
        IBuffer? meta, IBuffer? binned, IBuffer? binnedDraw, IBuffer? binnedHWDraw, IBuffer? dispatchArgs,
        IBuffer? materialSlotBuffer = null, IBuffer? pageHeap = null, IBuffer? swDispatchArgs = null)
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
        if (offsetArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "ClusterReadOffsetArgs")
                ?.Set(offsetArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (meta != null)
            srb.GetVariableByName(ShaderType.Compute, "RasterBinMeta")
                ?.Set(meta.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binned != null)
            srb.GetVariableByName(ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binned.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binnedDraw != null)
            srb.GetVariableByName(ShaderType.Compute, "BinnedDrawArgs")
                ?.Set(binnedDraw.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binnedHWDraw != null)
            srb.GetVariableByName(ShaderType.Compute, "BinnedHWDrawArgs")
                ?.Set(binnedHWDraw.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (dispatchArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "BinningDispatchArgs")
                ?.Set(dispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (swDispatchArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "BinnedSWDispatchArgs")
                ?.Set(swDispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (materialSlotBuffer != null)
            srb.GetVariableByName(ShaderType.Compute, "MaterialSlotBuffer")
                ?.Set(materialSlotBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (pageHeap != null)
            srb.GetVariableByName(ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
    }
}

/// <summary>
/// RG Pass 1: CSBinningPrepare (writes DispatchArgs) + CSBinningInit (clears bin metadata).
/// Lightweight — PSO/SRB from static cache.
/// </summary>
internal sealed class ClusterBinningInitPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterBinning Init";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.Write(HRasterBinMeta, ResourceState.UnorderedAccess); // Written by CSBinningClear
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterBinningPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);
        if (uniformBuf == null || drawArgsBuf == null || dispatchArgsBuf == null) return;

        // CSBinningClear
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        if (metaBuf == null) return;
        var clearSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.ClearPSO!, ClusterBinningPSOs.ClearSRBPool);
        ClusterBinningPSOs.BindSRB(clearSRB, uniformBuf, null, null, null, null, metaBuf, null, null, null, null);
        ctx.SetPipelineState(ClusterBinningPSOs.ClearPSO!);
        ctx.CommitShaderResources(clearSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterBinningPSOs.ReturnSRB(clearSRB, ClusterBinningPSOs.ClearSRBPool);

        // CSBinningPrepare
        var prepareSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.PreparePSO!, ClusterBinningPSOs.PrepareSRBPool);
        ClusterBinningPSOs.BindSRB(prepareSRB, uniformBuf, null, null, drawArgsBuf, null, null, null, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(ClusterBinningPSOs.PreparePSO!);
        ctx.CommitShaderResources(prepareSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterBinningPSOs.ReturnSRB(prepareSRB, ClusterBinningPSOs.PrepareSRBPool);
    }
}

/// <summary>
/// RG Pass 2: CSBinningCount — count clusters per bin (DispatchComputeIndirect).
/// </summary>
internal sealed class ClusterBinningCountPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterBinning Count";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HClusterReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        if (HClusterReadOffsetArgs.IsValid)
            builder.Read(HClusterReadOffsetArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid)
            builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid)
            builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HRasterBinMeta, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterBinningPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterBinningPSOs.CountPSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var headerBuf = rgCtx.GetBuffer(HInstanceHeaders);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var offsetArgsBuf = HClusterReadOffsetArgs.IsValid ? rgCtx.GetBuffer(HClusterReadOffsetArgs) : null;
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null
            || drawArgsBuf == null || dispatchArgsBuf == null || metaBuf == null)
            return;

        var materialSlotBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var countSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.CountPSO, ClusterBinningPSOs.CountSRBPool);
        ClusterBinningPSOs.BindSRB(countSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf, metaBuf, null, null, null, null, materialSlotBuf, pageHeapBuf);
        ctx.SetPipelineState(ClusterBinningPSOs.CountPSO);
        ctx.CommitShaderResources(countSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        ClusterBinningPSOs.ReturnSRB(countSRB, ClusterBinningPSOs.CountSRBPool);
    }
}

/// <summary>
/// RG Pass 3: CSBinningReserve — prefix sum to compute bin offsets + write BinnedDrawArgs.
/// </summary>
internal sealed class ClusterBinningReservePass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterBinning Reserve";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedHWDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedSWDispatchArgs = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HRasterBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HBinnedDrawArgs, ResourceState.UnorderedAccess);
        builder.Write(HBinnedHWDrawArgs, ResourceState.UnorderedAccess);
        if (HBinnedSWDispatchArgs.IsValid)
            builder.Write(HBinnedSWDispatchArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterBinningPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterBinningPSOs.ReservePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedDrawBuf = rgCtx.GetBuffer(HBinnedDrawArgs);
        var binnedHWDrawBuf = rgCtx.GetBuffer(HBinnedHWDrawArgs);
        var swDispatchArgsBuf = HBinnedSWDispatchArgs.IsValid ? rgCtx.GetBuffer(HBinnedSWDispatchArgs) : null;
        if (uniformBuf == null || metaBuf == null || binnedDrawBuf == null || binnedHWDrawBuf == null) return;

        var reserveSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.ReservePSO, ClusterBinningPSOs.ReserveSRBPool);
        ClusterBinningPSOs.BindSRB(reserveSRB, uniformBuf, null, null, null, null, metaBuf, null, binnedDrawBuf, binnedHWDrawBuf, null, swDispatchArgs: swDispatchArgsBuf);
        ctx.SetPipelineState(ClusterBinningPSOs.ReservePSO);
        ctx.CommitShaderResources(reserveSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterBinningPSOs.ReturnSRB(reserveSRB, ClusterBinningPSOs.ReserveSRBPool);
    }
}

/// <summary>
/// RG Pass 4: CSBinningScatter — scatter visible clusters into binned buffer.
/// </summary>
internal sealed class ClusterBinningScatterPass(RenderContext context) : IRenderGraphPass
{
    public string Name => "ClusterBinning Scatter";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HClusterReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedClusterBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        if (HClusterReadOffsetArgs.IsValid)
            builder.Read(HClusterReadOffsetArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid)
            builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid)
            builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HRasterBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HBinnedClusterBuffer, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterBinningPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterBinningPSOs.ScatterPSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var headerBuf = rgCtx.GetBuffer(HInstanceHeaders);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var offsetArgsBuf = HClusterReadOffsetArgs.IsValid ? rgCtx.GetBuffer(HClusterReadOffsetArgs) : null;
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedBuf = rgCtx.GetBuffer(HBinnedClusterBuffer);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null
            || drawArgsBuf == null || dispatchArgsBuf == null
            || metaBuf == null || binnedBuf == null)
            return;

        var materialSlotBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var scatterSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.ScatterPSO, ClusterBinningPSOs.ScatterSRBPool);
        ClusterBinningPSOs.BindSRB(scatterSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf, metaBuf, binnedBuf, null, null, null, materialSlotBuf, pageHeapBuf);
        ctx.SetPipelineState(ClusterBinningPSOs.ScatterPSO);
        ctx.CommitShaderResources(scatterSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        ClusterBinningPSOs.ReturnSRB(scatterSRB, ClusterBinningPSOs.ScatterSRBPool);
    }
}

