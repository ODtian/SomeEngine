using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Manages PSO/SRB creation for the shade binning shader.
/// Actual dispatch is performed by three separate RenderGraph passes:
/// ClusterShadeBinCountPass, ClusterShadeBinReservePass, ClusterShadeBinScatterPass.
/// </summary>
public class ClusterShadeBinningResources : IDisposable
{
    public IPipelineState? CountPSO { get; private set; }
    public IPipelineState? ReservePSO { get; private set; }
    public IPipelineState? ScatterPSO { get; private set; }
    public IShaderResourceBinding? CountSRB { get; private set; }
    public IShaderResourceBinding? ReserveSRB { get; private set; }
    public IShaderResourceBinding? ScatterSRB { get; private set; }

    public bool Initialized { get; private set; }

    public void Init(RenderContext context)
    {
        if (Initialized) return;
        var device = context.Device;
        if (device == null) return;

        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_shade_binning.slang"
            )
        );
        var shaderAsset = SlangShaderImporter.Import(path);

        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        using var csCount = shaderAsset.CreateShader(context, "CSBinCount");
        CountPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Shade Bin Count PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = layout,
            },
            Cs = csCount,
        });
        if (CountPSO != null) CountSRB = CountPSO.CreateShaderResourceBinding(false);

        using var csReserve = shaderAsset.CreateShader(context, "CSBinReserve");
        ReservePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Shade Bin Reserve PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = layout,
            },
            Cs = csReserve,
        });
        if (ReservePSO != null) ReserveSRB = ReservePSO.CreateShaderResourceBinding(false);

        using var csScatter = shaderAsset.CreateShader(context, "CSBinScatter");
        ScatterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Shade Bin Scatter PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = layout,
            },
            Cs = csScatter,
        });
        if (ScatterPSO != null) ScatterSRB = ScatterPSO.CreateShaderResourceBinding(false);

        Initialized = true;
    }

    public void Dispose()
    {
        CountSRB?.Dispose();
        CountPSO?.Dispose();
        ReserveSRB?.Dispose();
        ReservePSO?.Dispose();
        ScatterSRB?.Dispose();
        ScatterPSO?.Dispose();
    }
}

/// <summary>
/// Pass 1: Count pixels per material.
/// </summary>
public class ClusterShadeBinCountPass(
    RenderContext context,
    ClusterShadeBinningResources resources
) : IRenderGraphPass
{
    public string Name => "Shade Bin Count";

    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HShadeBinUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinCounts = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HShadeBinUniforms, ResourceState.ConstantBuffer);
        builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Write(HBinCounts, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.CountPSO == null || resources.CountSRB == null)
            return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var instanceHeaders = rgCtx.GetBuffer(HInstanceHeaders);
        var uniformBuf = rgCtx.GetBuffer(HShadeBinUniforms);
        var binCounts = rgCtx.GetBuffer(HBinCounts);
        var materialSlotBuffer = rgCtx.GetBuffer(HMaterialSlotBuffer);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        if (visBufferSRV == null || visibleClusters == null || instanceHeaders == null
            || uniformBuf == null || binCounts == null || materialSlotBuffer == null || pageHeap == null)
            return;

        var srb = resources.CountSRB;
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisBuffer")
            ?.Set(visBufferSRV, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleClusters.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")
            ?.Set(instanceHeaders.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "MaterialSlotBuffer")
            ?.Set(materialSlotBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinCounts")
            ?.Set(binCounts.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        var texDesc = rgCtx.GetTexture(HVisBuffer)?.GetDesc();
        uint w = texDesc?.Width ?? 1;
        uint h = texDesc?.Height ?? 1;

        ctx.SetPipelineState(resources.CountPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = (w + 7) / 8,
            ThreadGroupCountY = (h + 7) / 8,
            ThreadGroupCountZ = 1,
        });
    }
}

/// <summary>
/// Pass 2: Prefix sum → offsets + indirect args.
/// </summary>
public class ClusterShadeBinReservePass(
    RenderContext context,
    ClusterShadeBinningResources resources
) : IRenderGraphPass
{
    public string Name => "Shade Bin Reserve";

    public RenderGraphHandle HShadeBinUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinCounts = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinScatterCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinIndirectArgs = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HShadeBinUniforms, ResourceState.ConstantBuffer);
        // BinCounts is RWStructuredBuffer in shader, so must be UAV even though we only read
        builder.Read(HBinCounts, ResourceState.UnorderedAccess);
        builder.Write(HBinOffsets, ResourceState.UnorderedAccess);
        builder.Write(HBinScatterCount, ResourceState.UnorderedAccess);
        builder.Write(HBinIndirectArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ReservePSO == null || resources.ReserveSRB == null)
            return;

        var uniformBuf = rgCtx.GetBuffer(HShadeBinUniforms);
        var binCounts = rgCtx.GetBuffer(HBinCounts);
        var binOffsets = rgCtx.GetBuffer(HBinOffsets);
        var binScatterCount = rgCtx.GetBuffer(HBinScatterCount);
        var binIndirectArgs = rgCtx.GetBuffer(HBinIndirectArgs);
        if (uniformBuf == null || binCounts == null || binOffsets == null
            || binScatterCount == null || binIndirectArgs == null)
            return;

        var srb = resources.ReserveSRB;
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinCounts")
            ?.Set(binCounts.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinOffsets")
            ?.Set(binOffsets.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinScatterCount")
            ?.Set(binScatterCount.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinIndirectArgs")
            ?.Set(binIndirectArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(resources.ReservePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = 1,
            ThreadGroupCountY = 1,
            ThreadGroupCountZ = 1,
        });
    }
}

/// <summary>
/// Pass 3: Scatter pixel coords into per-material segments.
/// </summary>
public class ClusterShadeBinScatterPass(
    RenderContext context,
    ClusterShadeBinningResources resources
) : IRenderGraphPass
{
    public string Name => "Shade Bin Scatter";

    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HShadeBinUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinScatterCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPixelCoordBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HMaterialSlotBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HShadeBinUniforms, ResourceState.ConstantBuffer);
        builder.Read(HMaterialSlotBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        // BinOffsets is RWStructuredBuffer in shader, so must be UAV even though we only read
        builder.Read(HBinOffsets, ResourceState.UnorderedAccess);
        builder.Write(HBinScatterCount, ResourceState.UnorderedAccess);
        builder.Write(HPixelCoordBuffer, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || resources.ScatterPSO == null || resources.ScatterSRB == null)
            return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var instanceHeaders = rgCtx.GetBuffer(HInstanceHeaders);
        var uniformBuf = rgCtx.GetBuffer(HShadeBinUniforms);
        var binOffsets = rgCtx.GetBuffer(HBinOffsets);
        var binScatterCount = rgCtx.GetBuffer(HBinScatterCount);
        var pixelCoordBuffer = rgCtx.GetBuffer(HPixelCoordBuffer);
        var materialSlotBuffer = rgCtx.GetBuffer(HMaterialSlotBuffer);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        if (visBufferSRV == null || visibleClusters == null || instanceHeaders == null
            || uniformBuf == null || binOffsets == null || binScatterCount == null
            || pixelCoordBuffer == null || materialSlotBuffer == null || pageHeap == null)
            return;

        var srb = resources.ScatterSRB;
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisBuffer")
            ?.Set(visBufferSRV, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleClusters.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")
            ?.Set(instanceHeaders.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "MaterialSlotBuffer")
            ?.Set(materialSlotBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinOffsets")
            ?.Set(binOffsets.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinScatterCount")
            ?.Set(binScatterCount.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PixelCoordBuffer")
            ?.Set(pixelCoordBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        var texDesc = rgCtx.GetTexture(HVisBuffer)?.GetDesc();
        uint w = texDesc?.Width ?? 1;
        uint h = texDesc?.Height ?? 1;

        ctx.SetPipelineState(resources.ScatterPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = (w + 7) / 8,
            ThreadGroupCountY = (h + 7) / 8,
            ThreadGroupCountZ = 1,
        });
    }
}
