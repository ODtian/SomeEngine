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
    public uint MaxClustersPerBin;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>
/// Owns PSOs/SRBs for 3 binning kernels: CSBinningPrepare, CSBinningInit, CSBinning.
/// Must be split into 2 separate RG passes (via sub-pass wrappers) for proper UAV barriers:
///   InitPass: CSBinningPrepare + CSBinningInit
///   ScatterPass: CSBinning (DispatchComputeIndirect)
/// </summary>
public class ClusterBinningPass(
    RenderContext context,
    string passName = "ClusterBinning"
) : IDisposable
{
    public string PassName { get; } = passName;
    private ShaderAsset? _shaderAsset;
    internal IPipelineState? PreparePSO;
    internal IShaderResourceBinding? PrepareSRB;
    internal IPipelineState? InitPSO;
    internal IShaderResourceBinding? InitSRB;
    internal IPipelineState? BinPSO;
    internal IShaderResourceBinding? BinSRB;
    internal RenderContext Context => context;
    private bool _initialized;

    public void Init()
    {
        if (_initialized) return;
        var device = context.Device;
        if (device == null) return;

        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_binning.slang"
            )
        );
        _shaderAsset = SlangShaderImporter.Import(path);

        var layoutDesc = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        using var csPrepare = _shaderAsset.CreateShader(context, "CSBinningPrepare");
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
        if (PreparePSO != null) PrepareSRB = PreparePSO.CreateShaderResourceBinding(false);

        using var csInit = _shaderAsset.CreateShader(context, "CSBinningInit");
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
        if (InitPSO != null) InitSRB = InitPSO.CreateShaderResourceBinding(false);

        using var csBin = _shaderAsset.CreateShader(context, "CSBinning");
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
        if (BinPSO != null) BinSRB = BinPSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    internal void BindSRB(
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

    public void Dispose()
    {
        PrepareSRB?.Dispose();
        PreparePSO?.Dispose();
        InitSRB?.Dispose();
        InitPSO?.Dispose();
        BinSRB?.Dispose();
        BinPSO?.Dispose();
    }
}

/// <summary>
/// RG Pass 1: CSBinningPrepare (writes DispatchArgs) + CSBinningInit (clears bin metadata).
/// </summary>
internal sealed class ClusterBinningInitPass(ClusterBinningPass parent) : IRenderGraphPass
{
    public string Name { get; } = parent.PassName + " Init";

    // Input
    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;

    // Output (written by Prepare + Init)
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
        var ctx = parent.Context.ImmediateContext;
        if (ctx == null) return;

        var uniformBuf = rgCtx.GetBuffer(HBinningUniforms);
        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HBinningDispatchArgs);

        if (uniformBuf == null || drawArgsBuf == null || dispatchArgsBuf == null)
            return;

        // CSBinningPrepare: compute DispatchIndirect args (always runs)
        parent.BindSRB(parent.PrepareSRB!,
            uniformBuf, null, null, drawArgsBuf, null,
            null, null, null, dispatchArgsBuf);
        ctx.SetPipelineState(parent.PreparePSO!);
        ctx.CommitShaderResources(parent.PrepareSRB!, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1,
        });

        // CSBinningInit: clear per-bin metadata + draw args
        var metaBuf = rgCtx.GetBuffer(HRasterBinMeta);
        var binnedDrawBuf = rgCtx.GetBuffer(HBinnedDrawArgs);
        if (metaBuf == null || binnedDrawBuf == null) return;

        parent.BindSRB(parent.InitSRB!,
            uniformBuf, null, null, null, null,
            metaBuf, null, binnedDrawBuf, null);
        ctx.SetPipelineState(parent.InitPSO!);
        ctx.CommitShaderResources(parent.InitSRB!, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1,
        });
    }
}

/// <summary>
/// RG Pass 2: CSBinning (DispatchComputeIndirect).
/// RenderGraph inserts UAV barriers (BinningDispatchArgs→IndirectArg, BinMeta/BinnedDrawArgs UAV→UAV).
/// </summary>
internal sealed class ClusterBinningScatterPass(ClusterBinningPass parent) : IRenderGraphPass
{
    public string Name { get; } = parent.PassName + " Scatter";

    // Input
    public RenderGraphHandle HBinningUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HClusterReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinningDispatchArgs = RenderGraphHandle.Invalid;

    // Read+Write (atomics)
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedDrawArgs = RenderGraphHandle.Invalid;

    // Output
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
        var ctx = parent.Context.ImmediateContext;
        if (ctx == null || parent.BinPSO == null || parent.BinSRB == null) return;

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

        parent.BindSRB(parent.BinSRB,
            uniformBuf, visibleBuf, headerBuf, drawArgsBuf, offsetArgsBuf,
            metaBuf, binnedBuf, binnedDrawBuf, null);
        ctx.SetPipelineState(parent.BinPSO);
        ctx.CommitShaderResources(parent.BinSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
        });
    }
}
