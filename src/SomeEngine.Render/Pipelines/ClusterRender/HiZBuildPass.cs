using System;
using System.Collections.Concurrent;
using System.IO;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Static PSO 缓存：HiZ 构建的 2 个 PSO + SRB pool。
/// </summary>
internal static class HiZBuildPSOs
{
    internal static IPipelineState? BuildMip0PSO;
    internal static IPipelineState? DownsamplePSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> BuildMip0SRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> DownsampleSRBPool = [];

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

            string shaderPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/hiz_build.slang"));
            var shaderAsset = SlangShaderImporter.Import(shaderPath);

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            using var csMip0 = shaderAsset.CreateShader(context, "BuildMip0");
            BuildMip0PSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "HiZ Build Mip0 PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layoutDesc,
                },
                Cs = csMip0,
            });

            using var csDown = shaderAsset.CreateShader(context, "DownsampleMip");
            DownsamplePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "HiZ Downsample PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layoutDesc,
                },
                Cs = csDown,
            });

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    internal static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);

    internal static uint DispatchCount(uint size) => (size + 7) / 8;
}

/// <summary>
/// RG Pass: HiZ Mip0 from depth target. Lightweight — PSO from static cache.
/// </summary>
internal sealed class HiZMip0Pass(RenderContext context, RenderGraphHandle hDepth, RenderGraphHandle hHiZ) : IRenderGraphPass
{
    public string Name => "HiZ Mip0";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(hDepth, ResourceState.ShaderResource);
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        HiZBuildPSOs.EnsureInitialized(context);
        if (HiZBuildPSOs.BuildMip0PSO == null) return;

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null) return;
        var hiZDesc = hiZTexture.GetDesc();

        var depthSRV = rgCtx.GetTextureView(hDepth, TextureViewType.ShaderResource);
        var hiZUAV0 = rgCtx.GetOrCreateView(hHiZ, new TextureViewDesc
        {
            Name = "MipView_UAV_0",
            ViewType = TextureViewType.UnorderedAccess,
            TextureDim = hiZDesc.Type,
            Format = hiZDesc.Format,
            MostDetailedMip = 0,
            NumMipLevels = 1,
            FirstSlice = 0,
            NumSlices = hiZDesc.ArraySizeOrDepth,
        });
        if (depthSRV == null || hiZUAV0 == null) return;

        var ctx = rgCtx.CommandList;
        var srb = HiZBuildPSOs.RentSRB(HiZBuildPSOs.BuildMip0PSO, HiZBuildPSOs.BuildMip0SRBPool);

        srb.GetVariableByName(ShaderType.Compute, "DepthTexture")
            ?.Set(depthSRV, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "HiZMip0")
            ?.Set(hiZUAV0, SetShaderResourceFlags.None);

        ctx.SetPipelineState(HiZBuildPSOs.BuildMip0PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = HiZBuildPSOs.DispatchCount(hiZDesc.Width),
            ThreadGroupCountY = HiZBuildPSOs.DispatchCount(hiZDesc.Height),
            ThreadGroupCountZ = 1,
        });

        HiZBuildPSOs.ReturnSRB(srb, HiZBuildPSOs.BuildMip0SRBPool);
    }
}

/// <summary>
/// RG Pass: HiZ Downsample one mip level. Lightweight — PSO from static cache.
/// </summary>
internal sealed class HiZDownsamplePass(RenderContext context, RenderGraphHandle hHiZ, uint mip) : IRenderGraphPass
{
    public string Name => $"HiZ Downsample Mip{mip}";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip - 1));
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip));
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        HiZBuildPSOs.EnsureInitialized(context);
        if (HiZBuildPSOs.DownsamplePSO == null) return;

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null) return;
        var hiZDesc = hiZTexture.GetDesc();

        var srcMipView = rgCtx.GetOrCreateView(hHiZ, new TextureViewDesc
        {
            Name = $"MipView_UAV_{mip - 1}",
            ViewType = TextureViewType.UnorderedAccess,
            TextureDim = hiZDesc.Type,
            Format = hiZDesc.Format,
            MostDetailedMip = mip - 1,
            NumMipLevels = 1,
            FirstSlice = 0,
            NumSlices = hiZDesc.ArraySizeOrDepth,
        });
        var dstMipView = rgCtx.GetOrCreateView(hHiZ, new TextureViewDesc
        {
            Name = $"MipView_UAV_{mip}",
            ViewType = TextureViewType.UnorderedAccess,
            TextureDim = hiZDesc.Type,
            Format = hiZDesc.Format,
            MostDetailedMip = mip,
            NumMipLevels = 1,
            FirstSlice = 0,
            NumSlices = hiZDesc.ArraySizeOrDepth,
        });
        if (srcMipView == null || dstMipView == null) return;

        var ctx = rgCtx.CommandList;
        uint mipWidth = Math.Max(1u, hiZDesc.Width >> (int)mip);
        uint mipHeight = Math.Max(1u, hiZDesc.Height >> (int)mip);

        var srb = HiZBuildPSOs.RentSRB(HiZBuildPSOs.DownsamplePSO, HiZBuildPSOs.DownsampleSRBPool);

        srb.GetVariableByName(ShaderType.Compute, "SrcMip")
            ?.Set(srcMipView, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "DstMip")
            ?.Set(dstMipView, SetShaderResourceFlags.None);

        ctx.SetPipelineState(HiZBuildPSOs.DownsamplePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = HiZBuildPSOs.DispatchCount(mipWidth),
            ThreadGroupCountY = HiZBuildPSOs.DispatchCount(mipHeight),
            ThreadGroupCountZ = 1,
        });

        HiZBuildPSOs.ReturnSRB(srb, HiZBuildPSOs.DownsampleSRBPool);
    }
}
