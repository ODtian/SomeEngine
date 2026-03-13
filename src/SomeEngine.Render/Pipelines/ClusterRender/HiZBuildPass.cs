using System;
using System.IO;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public class HiZBuildPass(RenderContext context) : IDisposable
{
    private readonly RenderContext _context = context;

    private ShaderAsset? _shaderAsset;
    private IPipelineState? _buildMip0PSO;
    private IShaderResourceBinding? _buildMip0SRB;
    private IPipelineState? _downsamplePSO;
    private IShaderResourceBinding? _downsampleSRB;
    private bool _initialized;

    public void Init()
    {
        if (_initialized)
            return;

        var device = _context.Device;
        if (device == null)
            return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/hiz_build.slang"
            )
        );

        _shaderAsset = SlangShaderImporter.Import(shaderPath);

        using (var cs = _shaderAsset.CreateShader(_context, "BuildMip0"))
        {
            var ci = new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "HiZ Build Mip0 PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                Cs = cs,
            };

            _buildMip0PSO = device.CreateComputePipelineState(ci);
            if (_buildMip0PSO != null)
                _buildMip0SRB = _buildMip0PSO.CreateShaderResourceBinding(false);
        }

        using (var cs = _shaderAsset.CreateShader(_context, "DownsampleMip"))
        {
            var ci = new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "HiZ Downsample PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                Cs = cs,
            };

            _downsamplePSO = device.CreateComputePipelineState(ci);
            if (_downsamplePSO != null)
                _downsampleSRB = _downsamplePSO.CreateShaderResourceBinding(false);
        }

        _initialized = true;
    }

    public void SetupMip0(
        RenderGraphBuilder builder,
        RenderGraphHandle hDepth,
        RenderGraphHandle hHiZ
    )
    {
        builder.Read(hDepth, ResourceState.ShaderResource);
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
    }

    public void ExecuteMip0(
        RenderGraphContext rgCtx,
        RenderGraphHandle hDepth,
        RenderGraphHandle hHiZ
    )
    {
        if (_buildMip0PSO == null || _buildMip0SRB == null)
            return;

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null)
            return;

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
        if (depthSRV == null || hiZUAV0 == null)
            return;

        var ctx = rgCtx.CommandList;

        var desc = hiZDesc;

        _buildMip0SRB
            .GetVariableByName(ShaderType.Compute, "DepthTexture")
            ?.Set(depthSRV, SetShaderResourceFlags.None);
        _buildMip0SRB
            .GetVariableByName(ShaderType.Compute, "HiZMip0")
            ?.Set(hiZUAV0, SetShaderResourceFlags.None);

        ctx.SetPipelineState(_buildMip0PSO);
        ctx.CommitShaderResources(_buildMip0SRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = DispatchCount(desc.Width),
                ThreadGroupCountY = DispatchCount(desc.Height),
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void SetupDownsample(RenderGraphBuilder builder, RenderGraphHandle hHiZ, uint mip)
    {
        builder.Read(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip - 1));
        builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(mip));
    }

    public void ExecuteDownsample(RenderGraphContext rgCtx, RenderGraphHandle hHiZ, uint mip)
    {
        if (_downsamplePSO == null || _downsampleSRB == null)
            return;

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null)
            return;

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
        if (srcMipView == null || dstMipView == null)
            return;

        var ctx = rgCtx.CommandList;

        uint mipWidth = Math.Max(1u, hiZDesc.Width >> (int)mip);
        uint mipHeight = Math.Max(1u, hiZDesc.Height >> (int)mip);

        _downsampleSRB
            .GetVariableByName(ShaderType.Compute, "SrcMip")
            ?.Set(srcMipView, SetShaderResourceFlags.None);
        _downsampleSRB
            .GetVariableByName(ShaderType.Compute, "DstMip")
            ?.Set(dstMipView, SetShaderResourceFlags.None);

        ctx.SetPipelineState(_downsamplePSO);
        ctx.CommitShaderResources(_downsampleSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = DispatchCount(mipWidth),
                ThreadGroupCountY = DispatchCount(mipHeight),
                ThreadGroupCountZ = 1,
            }
        );
    }

    private static uint DispatchCount(uint size)
    {
        return (size + 7) / 8;
    }

    public void Dispose()
    {
        _buildMip0SRB?.Dispose();
        _buildMip0PSO?.Dispose();
        _downsampleSRB?.Dispose();
        _downsamplePSO?.Dispose();
    }
}
