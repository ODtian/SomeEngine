using System;
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
public static partial class ClusterHiZ
{
internal sealed class HiZBuildResources : IDisposable
{
    internal const string ShaderFile = "hiz_build.slang";
    internal const string BuildMip0EntryPoint = "BuildMip0";
    internal const string DownsampleEntryPoint = "DownsampleMip";

    internal IPipelineState? BuildMip0PSO;
    internal IPipelineState? DownsamplePSO;
    internal ShaderAsset? ShaderAsset;

    internal readonly SRBPool BuildMip0Pool = new();
    internal readonly SRBPool DownsamplePool = new();

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

            string shaderPath = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(shaderPath);
            ShaderAsset = shaderAsset;

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            var csMip0 = shaderAsset.CreateShader(context, BuildMip0EntryPoint);
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

            var csDown = shaderAsset.CreateShader(context, DownsampleEntryPoint);
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
            _initialized = true;
        }
    }
internal static uint DispatchCount(uint size) => (size + 7) / 8;

    public void Dispose()
    {
        BuildMip0Pool.Dispose();
        DownsamplePool.Dispose();
        BuildMip0PSO?.Dispose();
        DownsamplePSO?.Dispose();
    }
}
}

/// <summary>
/// RG Pass: HiZ Mip0 from depth target. Lightweight — PSO from static cache.
/// </summary>
internal sealed class HiZMip0Pass(RenderContext context, ClusterHiZ.HiZBuildResources resources, RenderGraphHandle hDepth, RenderGraphHandle hHiZ) : IRenderGraphPass
{
    private static readonly bool LogMip0 = Environment.GetEnvironmentVariable("SOMEENGINE_HIZ_MIP0_LOG") == "1";
    private static int _debugFrameCount;

    public string Name => "HiZ Mip0";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(hDepth, ResourceState.ShaderResource);
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        if (resources.BuildMip0PSO == null) { Console.WriteLine("[HiZ Mip0] PSO is null!"); return; }

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null) { Console.WriteLine("[HiZ Mip0] HiZ texture is null!"); return; }
        var hiZDesc = hiZTexture.GetDesc();

        var depthTex = rgCtx.GetTexture(hDepth);
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

        if (LogMip0 && _debugFrameCount++ % 120 == 0)
        {
            var depthDesc = depthTex?.GetDesc();
            Console.WriteLine($"[HiZ Mip0] depthTex={depthTex != null} fmt={depthDesc?.Format} " +
                $"size={depthDesc?.Width}x{depthDesc?.Height} " +
                $"depthSRV={depthSRV != null} hiZUAV0={hiZUAV0 != null} " +
                $"hiZ={hiZDesc.Width}x{hiZDesc.Height} mips={hiZDesc.MipLevels} " +
                $"dispatch={ClusterHiZ.HiZBuildResources.DispatchCount(hiZDesc.Width)}x{ClusterHiZ.HiZBuildResources.DispatchCount(hiZDesc.Height)}");
        }

        if (depthSRV == null || hiZUAV0 == null) return;

        var ctx = rgCtx.CommandList;
        var srb = resources.BuildMip0Pool.Rent(resources.BuildMip0PSO);

        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DepthTexture")
            ?.Set(depthSRV, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "HiZMip0")
            ?.Set(hiZUAV0, SetShaderResourceFlags.None);

        ctx.SetPipelineState(resources.BuildMip0PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = ClusterHiZ.HiZBuildResources.DispatchCount(hiZDesc.Width),
            ThreadGroupCountY = ClusterHiZ.HiZBuildResources.DispatchCount(hiZDesc.Height),
            ThreadGroupCountZ = 1,
        });

        resources.BuildMip0Pool.Return(srb);
    }
}

/// <summary>
/// RG Pass: HiZ Downsample one mip level. Lightweight — PSO from static cache.
/// </summary>
internal sealed class HiZDownsamplePass(RenderContext context, ClusterHiZ.HiZBuildResources resources, RenderGraphHandle hHiZ, uint mip) : IRenderGraphPass
{
    public string Name => $"HiZ Downsample Mip{mip}";

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip - 1));
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip));
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        if (resources.DownsamplePSO == null) return;

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

        var srb = resources.DownsamplePool.Rent(resources.DownsamplePSO);

        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "SrcMip")
            ?.Set(srcMipView, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DstMip")
            ?.Set(dstMipView, SetShaderResourceFlags.None);

        ctx.SetPipelineState(resources.DownsamplePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = ClusterHiZ.HiZBuildResources.DispatchCount(mipWidth),
            ThreadGroupCountY = ClusterHiZ.HiZBuildResources.DispatchCount(mipHeight),
            ThreadGroupCountZ = 1,
        });

        resources.DownsamplePool.Return(srb);
    }
}
