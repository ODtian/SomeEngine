using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public static partial class ClusterDeformBinStage
{
    public sealed class Resources : IDisposable
    {
        internal const string ShaderFile = "cluster_deform_binning.slang";
        internal const string ClearEntryPoint = "CSDeformBinClear";
        internal const string PrepareEntryPoint = "CSDeformBinPrepare";
        internal const string CountEntryPoint = "CSDeformBinCount";
        internal const string ReserveEntryPoint = "CSDeformBinReserve";
        internal const string ReserveDispatchWriteEntryPoint = "CSDeformBinReserveDispatchWrite";
        internal const string ScatterEntryPoint = "CSDeformBinScatter";

        internal IPipelineState? ClearPSO;
        internal IPipelineState? PreparePSO;
        internal IPipelineState? CountPSO;
        internal IPipelineState? ReservePSO;
        internal IPipelineState? ReserveDispatchWritePSO;
        internal IPipelineState? ScatterPSO;
        internal ShaderAsset? ShaderAsset;

        internal readonly SRBPool ClearPool = new();
        internal readonly SRBPool PreparePool = new();
        internal readonly SRBPool CountPool = new();
        internal readonly SRBPool ReservePool = new();
        internal readonly SRBPool ReserveDispatchWritePool = new();
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
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinClear", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csClear,
                });

                var csPrepare = shaderAsset.CreateShader(context, PrepareEntryPoint);
                PreparePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinPrepare", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csPrepare,
                });

                var csCount = shaderAsset.CreateShader(context, CountEntryPoint);
                CountPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinCount", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csCount,
                });

                var csReserve = shaderAsset.CreateShader(context, ReserveEntryPoint);
                ReservePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinReserve", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csReserve,
                });

                var csReserveDW = shaderAsset.CreateShader(context, ReserveDispatchWriteEntryPoint);
                ReserveDispatchWritePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinReserveDispatchWrite", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csReserveDW,
                });

                var csBin = shaderAsset.CreateShader(context, ScatterEntryPoint);
                ScatterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc { Name = "CSDeformBinScatter", PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc },
                    Cs = csBin,
                });

                _initialized = true;
            }
        }

        internal void BindSRB(
            RenderContext context,
            IShaderResourceBinding srb,
            IBuffer? uniforms, IBuffer? visible, IBuffer? headers, IBuffer? drawArgs, IBuffer? readOffsetArgs,
            IBuffer? meta, IBuffer? binned, IBuffer? dispatchArgs,
            IBuffer? materialSlotBuffer = null, IBuffer? pageHeap = null,
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
            if (readOffsetArgs != null)
                srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "ReadOffsetArgs")
                    ?.Set(readOffsetArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            if (meta != null)
                srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "DeformBinMeta")
                    ?.Set(meta.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            if (binned != null)
                srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "BinnedClusterIndexBuffer")
                    ?.Set(binned.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            if (dispatchArgs != null)
                srb.GetVariableByReflectedBinding(context, ShaderAsset, ShaderType.Compute, "DeformBinningDispatchArgs")
                    ?.Set(dispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
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

        public void Dispose()
        {
            ClearPool.Dispose();
            PreparePool.Dispose();
            CountPool.Dispose();
            ReservePool.Dispose();
            ReserveDispatchWritePool.Dispose();
            ScatterPool.Dispose();
            ClearPSO?.Dispose();
            PreparePSO?.Dispose();
            CountPSO?.Dispose();
            ReservePSO?.Dispose();
            ReserveDispatchWritePSO?.Dispose();
            ScatterPSO?.Dispose();
        }
    }
}

internal sealed class ClusterDeformBinInitPass(
    RenderContext context,
    ClusterDeformBinStage.Resources resources
) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Init";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReserveCounters = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HDeformBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HReserveCounters, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ClearPSO == null || resources.PreparePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var reserveCounters = rgCtx.GetBuffer(HReserveCounters);
        if (uniformBuf == null || drawArgsBuf == null || dispatchArgsBuf == null || metaBuf == null || reserveCounters == null) return;

        byte[] counterZeros = new byte[4];
        ctx.UpdateBuffer(reserveCounters, 0, counterZeros, ResourceStateTransitionMode.None);

        var clearSRB = resources.ClearPool.Rent(resources.ClearPSO);
        resources.BindSRB(context, clearSRB, uniformBuf, null, null, null, null, metaBuf, null, null);
        ctx.SetPipelineState(resources.ClearPSO);
        ctx.CommitShaderResources(clearSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.ClearPool.Return(clearSRB);

        var prepareSRB = resources.PreparePool.Rent(resources.PreparePSO);
        resources.BindSRB(context, prepareSRB, uniformBuf, null, null, drawArgsBuf, null, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(resources.PreparePSO);
        ctx.CommitShaderResources(prepareSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.PreparePool.Return(prepareSRB);
    }
}

internal sealed class ClusterDeformBinCountPass(
    RenderContext context,
    ClusterDeformBinStage.Resources resources
) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Count";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
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
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid) builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid) builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
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
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null || drawArgsBuf == null || readOffsetBuf == null || dispatchArgsBuf == null || metaBuf == null) return;
        var matBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var countSRB = resources.CountPool.Rent(resources.CountPSO);
        resources.BindSRB(context, countSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, readOffsetBuf, metaBuf, null, null, matBuf, pageHeapBuf);
        ctx.SetPipelineState(resources.CountPSO);
        ctx.CommitShaderResources(countSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        resources.CountPool.Return(countSRB);
    }
}

internal sealed class ClusterDeformBinReservePass(
    RenderContext context,
    ClusterDeformBinStage.Resources resources
) : IRenderGraphPass
{
    private const uint ReserveBlockSize = 128;

    public string Name => "ClusterDeformBin Reserve";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinningDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReserveCounters = RenderGraphHandle.Invalid;
    public uint MaxBins;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.ReadWrite(HReserveCounters, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ReservePSO == null || resources.ReserveDispatchWritePSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var reserveCounters = rgCtx.GetBuffer(HReserveCounters);
        if (uniformBuf == null || metaBuf == null || dispatchArgsBuf == null || reserveCounters == null) return;

        var reserveSRB = resources.ReservePool.Rent(resources.ReservePSO);
        resources.BindSRB(context, reserveSRB, uniformBuf, null, null, null, null, metaBuf, null, null, reserveCounters: reserveCounters);
        ctx.SetPipelineState(resources.ReservePSO);
        ctx.CommitShaderResources(reserveSRB, ResourceStateTransitionMode.None);
        uint maxBins = MaxBins == 0 ? 1u : MaxBins;
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (maxBins + ReserveBlockSize - 1) / ReserveBlockSize, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.ReservePool.Return(reserveSRB);

        var reserveDWSRB = resources.ReserveDispatchWritePool.Rent(resources.ReserveDispatchWritePSO);
        resources.BindSRB(context, reserveDWSRB, uniformBuf, null, null, null, null, metaBuf, null, dispatchArgsBuf);
        ctx.SetPipelineState(resources.ReserveDispatchWritePSO);
        ctx.CommitShaderResources(reserveDWSRB, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (MaxBins + 63) / 64, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.ReserveDispatchWritePool.Return(reserveDWSRB);
    }
}

internal sealed class ClusterDeformBinScatterPass(
    RenderContext context,
    ClusterDeformBinStage.Resources resources
) : IRenderGraphPass
{
    public string Name => "ClusterDeformBin Scatter";

    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
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
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        if (HMaterialSlotBuffer.IsValid) builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        if (HPageHeap.IsValid) builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HDeformBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HDeformBinnedClusterBuffer, ResourceState.UnorderedAccess);
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
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var binnedBuf = rgCtx.GetBuffer(HDeformBinnedClusterBuffer);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null || drawArgsBuf == null || readOffsetBuf == null || dispatchArgsBuf == null || metaBuf == null || binnedBuf == null) return;
        var matBuf = HMaterialSlotBuffer.IsValid ? rgCtx.GetBuffer(HMaterialSlotBuffer) : null;
        var pageHeapBuf = HPageHeap.IsValid ? rgCtx.GetBuffer(HPageHeap) : null;

        var scatterSRB = resources.ScatterPool.Rent(resources.ScatterPSO);
        resources.BindSRB(context, scatterSRB, uniformBuf, visibleBuf, headerBuf, drawArgsBuf, readOffsetBuf, metaBuf, binnedBuf, null, matBuf, pageHeapBuf);
        ctx.SetPipelineState(resources.ScatterPSO);
        ctx.CommitShaderResources(scatterSRB, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs { AttribsBuffer = dispatchArgsBuf, AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None });
        resources.ScatterPool.Return(scatterSRB);
    }
}
