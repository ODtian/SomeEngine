using System;
using System.Collections.Generic;
using System.Linq;
using Diligent;

namespace SomeEngine.Render.Pipelines;

public class GlobalPsoCache : IDisposable
{
    private class ComputeDescComparer : IEqualityComparer<ComputePipelineStateCreateInfo>
    {
        public bool Equals(ComputePipelineStateCreateInfo? x, ComputePipelineStateCreateInfo? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;

            if (x.PSODesc.PipelineType != y.PSODesc.PipelineType) return false;
            
            // Compare Shader (by reference identity since they are managed wrappers of native objects)
            if (!ReferenceEquals(x.Cs, y.Cs)) return false;

            return CompareResourceLayout(x.PSODesc.ResourceLayout, y.PSODesc.ResourceLayout);
        }

        public int GetHashCode(ComputePipelineStateCreateInfo obj)
        {
            return HashCode.Combine(
                obj.PSODesc.PipelineType,
                obj.Cs?.GetHashCode() ?? 0
            );
        }
    }

    private class GraphicsDescComparer : IEqualityComparer<GraphicsPipelineStateCreateInfo>
    {
        public bool Equals(GraphicsPipelineStateCreateInfo? x, GraphicsPipelineStateCreateInfo? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;

            if (x.PSODesc.PipelineType != y.PSODesc.PipelineType) return false;

            // Shaders
            if (!ReferenceEquals(x.Vs, y.Vs)) return false;
            if (!ReferenceEquals(x.Ps, y.Ps)) return false;
            if (!ReferenceEquals(x.Gs, y.Gs)) return false;
            if (!ReferenceEquals(x.Hs, y.Hs)) return false;
            if (!ReferenceEquals(x.Ds, y.Ds)) return false;
            if (!ReferenceEquals(x.As, y.As)) return false;
            if (!ReferenceEquals(x.Ms, y.Ms)) return false;

            ref var gx = ref x.GraphicsPipeline;
            ref var gy = ref y.GraphicsPipeline;

            if (gx.PrimitiveTopology != gy.PrimitiveTopology) return false;
            if (gx.NumRenderTargets != gy.NumRenderTargets) return false;
            for (int i = 0; i < gx.NumRenderTargets; i++)
            {
                if (gx.RTVFormats[i] != gy.RTVFormats[i]) return false;
            }
            if (gx.DSVFormat != gy.DSVFormat) return false;
            if (gx.NodeMask != gy.NodeMask) return false;
            if (gx.SmplDesc.Count != gy.SmplDesc.Count || gx.SmplDesc.Quality != gy.SmplDesc.Quality) return false;

            // Rasterizer
            if (gx.RasterizerDesc.FillMode != gy.RasterizerDesc.FillMode) return false;
            if (gx.RasterizerDesc.CullMode != gy.RasterizerDesc.CullMode) return false;
            if (gx.RasterizerDesc.FrontCounterClockwise != gy.RasterizerDesc.FrontCounterClockwise) return false;
            if (gx.RasterizerDesc.DepthClipEnable != gy.RasterizerDesc.DepthClipEnable) return false;
            if (gx.RasterizerDesc.ScissorEnable != gy.RasterizerDesc.ScissorEnable) return false;
            if (gx.RasterizerDesc.AntialiasedLineEnable != gy.RasterizerDesc.AntialiasedLineEnable) return false;
            if (gx.RasterizerDesc.DepthBias != gy.RasterizerDesc.DepthBias) return false;
            if (gx.RasterizerDesc.DepthBiasClamp != gy.RasterizerDesc.DepthBiasClamp) return false;
            if (gx.RasterizerDesc.SlopeScaledDepthBias != gy.RasterizerDesc.SlopeScaledDepthBias) return false;

            // DepthStencil
            if (gx.DepthStencilDesc.DepthEnable != gy.DepthStencilDesc.DepthEnable) return false;
            if (gx.DepthStencilDesc.DepthWriteEnable != gy.DepthStencilDesc.DepthWriteEnable) return false;
            if (gx.DepthStencilDesc.DepthFunc != gy.DepthStencilDesc.DepthFunc) return false;
            if (gx.DepthStencilDesc.StencilEnable != gy.DepthStencilDesc.StencilEnable) return false;
            if (gx.DepthStencilDesc.StencilReadMask != gy.DepthStencilDesc.StencilReadMask) return false;
            if (gx.DepthStencilDesc.StencilWriteMask != gy.DepthStencilDesc.StencilWriteMask) return false;
            // Simplified Stencil Op Compare
            if (gx.DepthStencilDesc.FrontFace.StencilFailOp != gy.DepthStencilDesc.FrontFace.StencilFailOp ||
                gx.DepthStencilDesc.FrontFace.StencilDepthFailOp != gy.DepthStencilDesc.FrontFace.StencilDepthFailOp ||
                gx.DepthStencilDesc.FrontFace.StencilPassOp != gy.DepthStencilDesc.FrontFace.StencilPassOp ||
                gx.DepthStencilDesc.FrontFace.StencilFunc != gy.DepthStencilDesc.FrontFace.StencilFunc) return false;
            if (gx.DepthStencilDesc.BackFace.StencilFailOp != gy.DepthStencilDesc.BackFace.StencilFailOp ||
                gx.DepthStencilDesc.BackFace.StencilDepthFailOp != gy.DepthStencilDesc.BackFace.StencilDepthFailOp ||
                gx.DepthStencilDesc.BackFace.StencilPassOp != gy.DepthStencilDesc.BackFace.StencilPassOp ||
                gx.DepthStencilDesc.BackFace.StencilFunc != gy.DepthStencilDesc.BackFace.StencilFunc) return false;

            // Blend
            if (gx.BlendDesc.AlphaToCoverageEnable != gy.BlendDesc.AlphaToCoverageEnable) return false;
            if (gx.BlendDesc.IndependentBlendEnable != gy.BlendDesc.IndependentBlendEnable) return false;
            for (int i = 0; i < (gx.BlendDesc.IndependentBlendEnable ? gx.NumRenderTargets : 1); i++)
            {
                var bx = gx.BlendDesc.RenderTargets[i];
                var by = gy.BlendDesc.RenderTargets[i];
                if (bx.BlendEnable != by.BlendEnable ||
                    bx.SrcBlend != by.SrcBlend || bx.DestBlend != by.DestBlend || bx.BlendOp != by.BlendOp ||
                    bx.SrcBlendAlpha != by.SrcBlendAlpha || bx.DestBlendAlpha != by.DestBlendAlpha || bx.BlendOpAlpha != by.BlendOpAlpha ||
                    bx.RenderTargetWriteMask != by.RenderTargetWriteMask)
                    return false;
            }

            // InputLayout
            if (gx.InputLayout.LayoutElements != null && gy.InputLayout.LayoutElements != null)
            {
                if (gx.InputLayout.LayoutElements.Length != gy.InputLayout.LayoutElements.Length) return false;
                for (int i = 0; i < gx.InputLayout.LayoutElements.Length; i++)
                {
                    var ex = gx.InputLayout.LayoutElements[i];
                    var ey = gy.InputLayout.LayoutElements[i];
                    if (ex.InputIndex != ey.InputIndex || ex.BufferSlot != ey.BufferSlot ||
                        ex.NumComponents != ey.NumComponents || ex.ValueType != ey.ValueType ||
                        ex.IsNormalized != ey.IsNormalized || ex.RelativeOffset != ey.RelativeOffset ||
                        ex.Stride != ey.Stride || ex.Frequency != ey.Frequency || ex.InstanceDataStepRate != ey.InstanceDataStepRate)
                        return false;
                }
            }
            else if (gx.InputLayout.LayoutElements != gy.InputLayout.LayoutElements)
            {
                return false;
            }

            return CompareResourceLayout(x.PSODesc.ResourceLayout, y.PSODesc.ResourceLayout);
        }

        public int GetHashCode(GraphicsPipelineStateCreateInfo obj)
        {
            var hash = new HashCode();
            hash.Add(obj.PSODesc.PipelineType);
            hash.Add(obj.Vs?.GetHashCode() ?? 0);
            hash.Add(obj.Ps?.GetHashCode() ?? 0);
            hash.Add(obj.GraphicsPipeline.NumRenderTargets);
            hash.Add(obj.GraphicsPipeline.DSVFormat);
            hash.Add(obj.GraphicsPipeline.PrimitiveTopology);
            
            // Only hash a few critical state fields to keep it fast. Equals will do deep check.
            hash.Add(obj.GraphicsPipeline.RasterizerDesc.FillMode);
            hash.Add(obj.GraphicsPipeline.DepthStencilDesc.DepthEnable);
            hash.Add(obj.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable);
            
            return hash.ToHashCode();
        }
    }

    private static bool CompareResourceLayout(PipelineResourceLayoutDesc xl, PipelineResourceLayoutDesc yl)
    {
        if (xl.DefaultVariableType != yl.DefaultVariableType) return false;
        
        var xVars = xl.Variables;
        var yVars = yl.Variables;
        
        if ((xVars == null) != (yVars == null)) return false;
        if (xVars != null && yVars != null)
        {
            if (xVars.Length != yVars.Length) return false;
            for (int i = 0; i < xVars.Length; i++)
            {
                if (xVars[i].Name != yVars[i].Name ||
                    xVars[i].ShaderStages != yVars[i].ShaderStages ||
                    xVars[i].Type != yVars[i].Type)
                    return false;
            }
        }

        var xImms = xl.ImmutableSamplers;
        var yImms = yl.ImmutableSamplers;
        if ((xImms == null) != (yImms == null)) return false;
        if (xImms != null && yImms != null)
        {
            if (xImms.Length != yImms.Length) return false;
            for (int i = 0; i < xImms.Length; i++)
            {
                if (xImms[i].SamplerOrTextureName != yImms[i].SamplerOrTextureName ||
                    xImms[i].ShaderStages != yImms[i].ShaderStages ||
                    xImms[i].Desc.MinFilter != yImms[i].Desc.MinFilter ||
                    xImms[i].Desc.MagFilter != yImms[i].Desc.MagFilter ||
                    xImms[i].Desc.MipFilter != yImms[i].Desc.MipFilter ||
                    xImms[i].Desc.AddressU != yImms[i].Desc.AddressU ||
                    xImms[i].Desc.AddressV != yImms[i].Desc.AddressV ||
                    xImms[i].Desc.AddressW != yImms[i].Desc.AddressW ||
                    xImms[i].Desc.ComparisonFunc != yImms[i].Desc.ComparisonFunc ||
                    xImms[i].Desc.MaxAnisotropy != yImms[i].Desc.MaxAnisotropy)
                    return false;
            }
        }

        return true;
    }

    private readonly Dictionary<GraphicsPipelineStateCreateInfo, IPipelineState> _graphicsCache = new(new GraphicsDescComparer());
    private readonly Dictionary<ComputePipelineStateCreateInfo, IPipelineState> _computeCache = new(new ComputeDescComparer());

    /// <summary>
    /// Gets or creates a Graphics Pipeline State Object.
    /// Deep compares the GraphicsPipelineStateCreateInfo.
    /// </summary>
    public IPipelineState GetOrCreateGraphicsPSO(IRenderDevice device, GraphicsPipelineStateCreateInfo ci)
    {
        if (_graphicsCache.TryGetValue(ci, out var pso))
            return pso;

        pso = device.CreateGraphicsPipelineState(ci);
        _graphicsCache.Add(ci, pso);
        return pso;
    }

    /// <summary>
    /// Gets or creates a Compute Pipeline State Object.
    /// Deep compares the ComputePipelineStateCreateInfo.
    /// </summary>
    public IPipelineState GetOrCreateComputePSO(IRenderDevice device, ComputePipelineStateCreateInfo ci)
    {
        if (_computeCache.TryGetValue(ci, out var pso))
            return pso;

        pso = device.CreateComputePipelineState(ci);
        _computeCache.Add(ci, pso);
        return pso;
    }

    public void Dispose()
    {
        foreach (var pso in _graphicsCache.Values) pso.Dispose();
        foreach (var pso in _computeCache.Values) pso.Dispose();
        _graphicsCache.Clear();
        _computeCache.Clear();
    }
}
