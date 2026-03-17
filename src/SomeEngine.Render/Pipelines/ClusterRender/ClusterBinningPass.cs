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
    public uint MaxClustersPerBin;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>
/// Binning PSO/SRB 的 static 缓存容器。
/// PSO 编译一次，SRB 通过 ConcurrentBag pool 管理。
/// </summary>
public static class ClusterBinningPSOs
{
    internal static IPipelineState? PreparePSO;
    internal static IPipelineState? InitPSO;
    internal static IPipelineState? BinPSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> PrepareSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> InitSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> BinSRBPool = [];

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

            using var csPrepare = shaderAsset.CreateShader(context, "CSBinningPrepare");
            PreparePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Binning Prepare PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layoutDesc,
                },
                Cs = csPrepare,
            });

            using var csInit = shaderAsset.CreateShader(context, "CSBinningInit");
            InitPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Binning Init PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layoutDesc,
                },
                Cs = csInit,
            });

            using var csBin = shaderAsset.CreateShader(context, "CSBinning");
            BinPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Binning Scatter PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layoutDesc,
                },
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
        IBuffer? meta, IBuffer? binned, IBuffer? binnedDraw, IBuffer? dispatchArgs)
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
        if (dispatchArgs != null)
            srb.GetVariableByName(ShaderType.Compute, "BinningDispatchArgs")
                ?.Set(dispatchArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
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

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HBinningDispatchArgs, ResourceState.UnorderedAccess);
        builder.Write(HRasterBinMeta, ResourceState.UnorderedAccess);
        builder.Write(HBinnedDrawArgs, ResourceState.UnorderedAccess);
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

        // CSBinningPrepare
        var prepareSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.PreparePSO!, ClusterBinningPSOs.PrepareSRBPool);
        ClusterBinningPSOs.BindSRB(prepareSRB,
            uniformBuf, null, null, drawArgsBuf, null,
            null, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(ClusterBinningPSOs.PreparePSO!);
        ctx.CommitShaderResources(prepareSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterBinningPSOs.ReturnSRB(prepareSRB, ClusterBinningPSOs.PrepareSRBPool);

        // CSBinningInit
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedDrawBuf = rgCtx.GetBuffer(HBinnedDrawArgs);
        if (metaBuf == null || binnedDrawBuf == null) return;

        var initSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.InitPSO!, ClusterBinningPSOs.InitSRBPool);
        ClusterBinningPSOs.BindSRB(initSRB,
            uniformBuf, null, null, null, null,
            metaBuf, null, binnedDrawBuf, null);
        ctx.SetPipelineState(ClusterBinningPSOs.InitPSO!);
        ctx.CommitShaderResources(initSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        ClusterBinningPSOs.ReturnSRB(initSRB, ClusterBinningPSOs.InitSRBPool);
    }
}

/// <summary>
/// RG Pass 2: CSBinning (DispatchComputeIndirect).
/// Lightweight — PSO/SRB from static cache.
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
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedClusterBuffer = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinningUniforms, ResourceState.ConstantBuffer);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        if (HClusterReadOffsetArgs.IsValid)
            builder.Read(HClusterReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HBinningDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HRasterBinMeta, ResourceState.UnorderedAccess);
        builder.ReadWrite(HBinnedDrawArgs, ResourceState.UnorderedAccess);
        builder.Write(HBinnedClusterBuffer, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterBinningPSOs.EnsureInitialized(context);
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterBinningPSOs.BinPSO == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var headerBuf = rgCtx.GetBuffer(HInstanceHeaders);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var offsetArgsBuf = HClusterReadOffsetArgs.IsValid ? rgCtx.GetBuffer(HClusterReadOffsetArgs) : null;
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedDrawBuf = rgCtx.GetBuffer(HBinnedDrawArgs);
        var binnedBuf = rgCtx.GetBuffer(HBinnedClusterBuffer);

        if (uniformBuf == null || visibleBuf == null || headerBuf == null
            || drawArgsBuf == null || dispatchArgsBuf == null
            || metaBuf == null || binnedDrawBuf == null || binnedBuf == null)
            return;

        var binSRB = ClusterBinningPSOs.RentSRB(ClusterBinningPSOs.BinPSO, ClusterBinningPSOs.BinSRBPool);
        ClusterBinningPSOs.BindSRB(binSRB,
            uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf,
            metaBuf, binnedBuf, binnedDrawBuf, null);
        ctx.SetPipelineState(ClusterBinningPSOs.BinPSO);
        ctx.CommitShaderResources(binSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
        });
        ClusterBinningPSOs.ReturnSRB(binSRB, ClusterBinningPSOs.BinSRBPool);
    }
}
