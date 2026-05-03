using System;
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
/// PSO 编译一次，SRB 通过 SRBPool 管理。
/// </summary>
internal static partial class ClusterRasterBin
{
internal sealed class Resources : IDisposable
{
    internal const string ShaderFile = "cluster_binning.slang";
    internal const string ClearEntryPoint = "CSBinningClear";
    internal const string PrepareEntryPoint = "CSBinningPrepare";
    internal const string CountEntryPoint = "CSBinningCount";
    internal const string ReserveEntryPoint = "CSBinningReserve";
    internal const string ScatterEntryPoint = "CSBinningScatter";

    internal IPipelineState? ClearPSO;
    internal IPipelineState? PreparePSO;
    internal IPipelineState? CountPSO;
    internal IPipelineState? ReservePSO;
    internal IPipelineState? ScatterPSO;
    internal ShaderAsset? ShaderAsset;

    internal readonly SRBPool ClearPool = new();
    internal readonly SRBPool PreparePool = new();
    internal readonly SRBPool CountPool = new();
    internal readonly SRBPool ReservePool = new();
    internal readonly SRBPool ScatterPool = new();

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

            string path = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(path);
            ShaderAsset = shaderAsset;

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

        var csClear = shaderAsset.CreateShader(context, ClearEntryPoint);
        ClearPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningClear", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csClear,
        });

        var csPrepare = shaderAsset.CreateShader(context, PrepareEntryPoint);
        PreparePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningPrepare", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csPrepare,
        });

        var csCount = shaderAsset.CreateShader(context, CountEntryPoint);
        CountPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningCount", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csCount,
        });

        var csReserve = shaderAsset.CreateShader(context, ReserveEntryPoint);
        ReservePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningReserve", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csReserve,
        });

        var csBin = shaderAsset.CreateShader(context, ScatterEntryPoint);
        ScatterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc { Name = "CSBinningScatter", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
            Cs = csBin,
        });
            _initialized = true;
        }
    }
    public void Dispose()
    {
        ClearPool.Dispose();
        PreparePool.Dispose();
        CountPool.Dispose();
        ReservePool.Dispose();
        ScatterPool.Dispose();
        ClearPSO?.Dispose();
        PreparePSO?.Dispose();
        CountPSO?.Dispose();
        ReservePSO?.Dispose();
        ScatterPSO?.Dispose();
    }
internal void BindSRB(
        RenderContext context,
        IShaderResourceBinding srb,
        IBuffer? uniforms, IBuffer? visible, IBuffer? headers, IBuffer? drawArgs, IBuffer? offsetArgs,
        IBuffer? meta, IBuffer? binned, IBuffer? binnedDraw, IBuffer? binnedHWDraw, IBuffer? dispatchArgs,
        IBuffer? materialSlotBuffer = null, IBuffer? pageHeap = null, IBuffer? swDispatchArgs = null,
        IBuffer? reserveCounters = null)
    {
        if (uniforms != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "Uniforms")
                ?.Set(uniforms, SetShaderResourceFlags.None);
        if (visible != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "VisibleClusters")
                ?.Set(visible.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (headers != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "InstanceHeaders")
                ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (drawArgs != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "DrawArgs")
                ?.Set(drawArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (offsetArgs != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "ClusterReadOffsetArgs")
                ?.Set(offsetArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (meta != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "RasterBinMeta")
                ?.Set(meta.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binned != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binned.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binnedDraw != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinnedDrawArgs")
                ?.Set(binnedDraw.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (binnedHWDraw != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinnedHWDrawArgs")
                ?.Set(binnedHWDraw.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (dispatchArgs != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinningDispatchArgs")
                ?.Set(dispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (swDispatchArgs != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinnedSWDispatchArgs")
                ?.Set(swDispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        if (materialSlotBuffer != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "MaterialSlotBuffer")
                ?.Set(materialSlotBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (pageHeap != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (reserveCounters != null)
            srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "ReserveCounters")
                ?.Set(reserveCounters.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
    }
}
}

/// <summary>
/// RG Pass 1: CSBinningPrepare (writes DispatchArgs) + CSBinningInit (clears bin metadata).
/// Lightweight — PSO/SRB from static cache.
/// </summary>
internal sealed class ClusterBinningInitPass(RenderContext context, ClusterRasterBin.Resources resources) : IRenderGraphPass
{
    public string Name => "ClusterBinning Init";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReserveCounters = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.Write(HRasterBinMeta, ResourceState.UnorderedAccess); // Written by CSBinningClear
        builder.Write(HReserveCounters, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ClearPSO == null || resources.PreparePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);
        var reserveCounters = rgCtx.GetBuffer(HReserveCounters);
        if (uniformBuf == null || drawArgsBuf == null || dispatchArgsBuf == null || reserveCounters == null) return;

        byte[] counterZeros = new byte[8];
        ctx.UpdateBuffer(reserveCounters, 0, counterZeros, ResourceStateTransitionMode.None);

        // CSBinningClear
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        if (metaBuf == null) return;
        var clearSRB = resources.ClearPool.Rent(resources.ClearPSO);
        resources.BindSRB(context, clearSRB, uniformBuf, null, null, null, null, metaBuf, null, null, null, null);
        ctx.SetPipelineState(resources.ClearPSO);
        ctx.CommitShaderResources(clearSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.ClearPool.Return(clearSRB);

        // CSBinningPrepare
        var prepareSRB = resources.PreparePool.Rent(resources.PreparePSO);
        resources.BindSRB(context, prepareSRB, uniformBuf, null, null, drawArgsBuf, null, null, null, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(resources.PreparePSO);
        ctx.CommitShaderResources(prepareSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.PreparePool.Return(prepareSRB);
    }
}

/// <summary>
/// RG Pass 2: CSBinningCount — count clusters per bin (DispatchComputeIndirect).
/// </summary>
internal sealed class ClusterBinningCountPass(RenderContext context, ClusterRasterBin.Resources resources) : IRenderGraphPass
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
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.CountPSO == null) return;

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

        var countSRB = resources.CountPool.Rent(resources.CountPSO);
        resources.BindSRB(context, countSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf, metaBuf, null, null, null, null, materialSlotBuf, pageHeapBuf);
        ctx.SetPipelineState(resources.CountPSO);
        ctx.CommitShaderResources(countSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        resources.CountPool.Return(countSRB);
    }
}

/// <summary>
/// RG Pass 3: CSBinningReserve — prefix sum to compute bin offsets + write BinnedDrawArgs.
/// </summary>
internal sealed class ClusterBinningReservePass(RenderContext context, ClusterRasterBin.Resources resources) : IRenderGraphPass
{
    private const uint ReserveBlockSize = 128;

    public string Name => "ClusterBinning Reserve";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedHWDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedSWDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReserveCounters = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HRasterBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HBinnedDrawArgs, ResourceState.UnorderedAccess);
        builder.Write(HBinnedHWDrawArgs, ResourceState.UnorderedAccess);
        if (HBinnedSWDispatchArgs.IsValid)
            builder.Write(HBinnedSWDispatchArgs, ResourceState.UnorderedAccess);
        builder.ReadWrite(HReserveCounters, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ReservePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedDrawBuf = rgCtx.GetBuffer(HBinnedDrawArgs);
        var binnedHWDrawBuf = rgCtx.GetBuffer(HBinnedHWDrawArgs);
        var swDispatchArgsBuf = HBinnedSWDispatchArgs.IsValid ? rgCtx.GetBuffer(HBinnedSWDispatchArgs) : null;
        var reserveCounters = rgCtx.GetBuffer(HReserveCounters);
        if (uniformBuf == null || metaBuf == null || binnedDrawBuf == null || binnedHWDrawBuf == null || reserveCounters == null) return;

        var reserveSRB = resources.ReservePool.Rent(resources.ReservePSO);
        resources.BindSRB(context, reserveSRB, uniformBuf, null, null, null, null, metaBuf, null, binnedDrawBuf, binnedHWDrawBuf, null, swDispatchArgs: swDispatchArgsBuf, reserveCounters: reserveCounters);
        ctx.SetPipelineState(resources.ReservePSO);
        ctx.CommitShaderResources(reserveSRB, ResourceStateTransitionMode.None);
        uint maxBins = MaxBins == 0 ? 1u : MaxBins;
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (maxBins + ReserveBlockSize - 1) / ReserveBlockSize, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.ReservePool.Return(reserveSRB);
    }
}

/// <summary>
/// RG Pass 4: CSBinningScatter — scatter visible clusters into binned buffer.
/// </summary>
internal sealed class ClusterBinningScatterPass(RenderContext context, ClusterRasterBin.Resources resources) : IRenderGraphPass
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
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ScatterPSO == null) return;

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

        var scatterSRB = resources.ScatterPool.Rent(resources.ScatterPSO);
        resources.BindSRB(context, scatterSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf, metaBuf, binnedBuf, null, null, null, materialSlotBuf, pageHeapBuf);
        ctx.SetPipelineState(resources.ScatterPSO);
        ctx.CommitShaderResources(scatterSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        resources.ScatterPool.Return(scatterSRB);
    }
}

