using System;
using System.Collections.Generic;
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

    private readonly List<ITextureView> _srvMipViews = [];
    private readonly List<ITextureView> _uavMipViews = [];
    private ITexture? _cachedHiZTexture;
    private uint _cachedMipCount;

    public void Init()
    {
        if (_initialized)
            return;

        var device = _context.Device;
        if (device == null)
            return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/hiz_build.slang")
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
                        DefaultVariableType = ShaderResourceVariableType.Mutable,
                        Variables = _shaderAsset.GetResourceVariables(
                            _context,
                            name =>
                                (name == "DepthTexture" || name == "HiZMip0")
                                    ? ShaderResourceVariableType.Dynamic
                                    : null
                        ),
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
                        DefaultVariableType = ShaderResourceVariableType.Mutable,
                        Variables = _shaderAsset.GetResourceVariables(
                            _context,
                            name =>
                                (name == "SrcMip" || name == "DstMip")
                                    ? ShaderResourceVariableType.Dynamic
                                    : null
                        ),
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

    public void SetupMip0(RenderGraphBuilder builder, RGResourceHandle hDepth, RGResourceHandle hHiZ)
    {
        builder.ReadTexture(hDepth, ResourceState.ShaderResource);
        builder.WriteTexture(hHiZ, ResourceState.UnorderedAccess);
    }

    public void ExecuteMip0(RenderContext context, RenderGraphContext rgCtx, RGResourceHandle hDepth, RGResourceHandle hHiZ)
    {
        if (_buildMip0PSO == null || _buildMip0SRB == null)
            return;

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var depthSRV = rgCtx.GetTextureView(hDepth, TextureViewType.ShaderResource);
        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (depthSRV == null || hiZTexture == null)
            return;

        EnsureMipViews(hiZTexture);
        if (_uavMipViews.Count == 0)
            return;

        var desc = hiZTexture.GetDesc();

        _buildMip0SRB.GetVariableByName(ShaderType.Compute, "DepthTexture")?.Set(depthSRV, SetShaderResourceFlags.None);
        _buildMip0SRB.GetVariableByName(ShaderType.Compute, "HiZMip0")?.Set(_uavMipViews[0], SetShaderResourceFlags.None);

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

    public void SetupDownsample(RenderGraphBuilder builder, RGResourceHandle hHiZ, uint mip)
    {
        builder.ReadTexture(hHiZ, ResourceState.ShaderResource);
        builder.WriteTexture(hHiZ, ResourceState.UnorderedAccess);
    }

    public void ExecuteDownsample(RenderContext context, RenderGraphContext rgCtx, RGResourceHandle hHiZ, uint mip)
    {
        if (_downsamplePSO == null || _downsampleSRB == null)
            return;

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var hiZTexture = rgCtx.GetTexture(hHiZ);
        if (hiZTexture == null)
            return;

        EnsureMipViews(hiZTexture);
        if (mip >= _cachedMipCount || _srvMipViews.Count <= mip - 1 || _uavMipViews.Count <= mip)
            return;

        var desc = hiZTexture.GetDesc();
        uint mipWidth = Math.Max(1u, desc.Width >> (int)mip);
        uint mipHeight = Math.Max(1u, desc.Height >> (int)mip);

        _downsampleSRB.GetVariableByName(ShaderType.Compute, "SrcMip")?.Set(_srvMipViews[(int)mip - 1], SetShaderResourceFlags.None);
        _downsampleSRB.GetVariableByName(ShaderType.Compute, "DstMip")?.Set(_uavMipViews[(int)mip], SetShaderResourceFlags.None);

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


    private void EnsureMipViews(ITexture texture)
    {
        var desc = texture.GetDesc();
        uint mipCount = Math.Max(1u, desc.MipLevels);

        if (_cachedHiZTexture == texture && _cachedMipCount == mipCount)
            return;

        ClearMipViews();

        _cachedHiZTexture = texture;
        _cachedMipCount = mipCount;

        for (uint mip = 0; mip < mipCount; mip++)
        {
            var srvDesc = new TextureViewDesc
            {
                Name = $"HiZ SRV Mip {mip}",
                ViewType = TextureViewType.ShaderResource,
                TextureDim = desc.Type,
                Format = desc.Format,
                MostDetailedMip = mip,
                NumMipLevels = 1,
                FirstSlice = 0,
                NumSlices = desc.ArraySizeOrDepth,
            };

            var uavDesc = new TextureViewDesc
            {
                Name = $"HiZ UAV Mip {mip}",
                ViewType = TextureViewType.UnorderedAccess,
                TextureDim = desc.Type,
                Format = desc.Format,
                MostDetailedMip = mip,
                NumMipLevels = 1,
                FirstSlice = 0,
                NumSlices = desc.ArraySizeOrDepth,
            };

            var srv = texture.CreateView(srvDesc);
            var uav = texture.CreateView(uavDesc);

            if (srv != null)
                _srvMipViews.Add(srv);
            if (uav != null)
                _uavMipViews.Add(uav);
        }
    }

    private static uint DispatchCount(uint size)
    {
        return (size + 7) / 8;
    }

    private void ClearMipViews()
    {
        foreach (var view in _srvMipViews)
            view.Dispose();
        foreach (var view in _uavMipViews)
            view.Dispose();

        _srvMipViews.Clear();
        _uavMipViews.Clear();

        _cachedHiZTexture = null;
        _cachedMipCount = 0;
    }

    public void Dispose()
    {
        ClearMipViews();

        _buildMip0SRB?.Dispose();
        _buildMip0PSO?.Dispose();
        _downsampleSRB?.Dispose();
        _downsamplePSO?.Dispose();
    }
}
