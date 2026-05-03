using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using Diligent;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Builds MaterialPSOGroup arrays from BinSpace for Deform, SW Raster, and HW Draw.
/// Uses the same group shape as shade, but with a simple dynamic resource layout.
/// </summary>
public static class RasterPSOBuilder
{
    /// <summary>
    /// Build compute PSO groups for a given bin field.
    /// Each group shares one PSO (same shader + entry point) and one SRB (Dynamic variables).
    /// </summary>
    public static MaterialPSOGroup[] BuildComputePSOGroups(
        BinSpace binSpace,
        int fieldIndex,
        GlobalPsoCache psoCache,
        RenderContext context,
        string psoNamePrefix,
        Func<Entity, ShaderVariantRef> variantSelector)
    {
        var device = context.Device;
        if (device == null)
            return [];

        var shaderGroups = MaterialPSOGroup.ComputeShaderGroups(binSpace, fieldIndex, variantSelector);
        if (shaderGroups.Count == 0)
            return [];

        var deviceType = device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";

        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        var groups = new MaterialPSOGroup[shaderGroups.Count];
        for (int g = 0; g < shaderGroups.Count; g++)
        {
            var sg = shaderGroups[g];
            var pso = FindOrCreateComputePSO(sg.VariantRef, psoCache, context, backend, layout, psoNamePrefix);

            // Single SRB per layout �?Dynamic variables allow re-binding per dispatch
            var srb = pso.CreateShaderResourceBinding(false);

            var argsBins = new int[sg.BinCount];
            for (int i = 0; i < sg.BinCount; i++)
            {
                argsBins[i] = sg.BinStart + i;
            }

            groups[g] = new MaterialPSOGroup
            {
                PSO = pso,
                SRB = srb,
                ComputeVariant = sg.VariantRef,
                Entities = sg.Entities,
                ArgsBins = argsBins,
                BinStart = sg.BinStart,
                BinCount = sg.BinCount,
            };
        }

        return groups;
    }

    public static MaterialPSOGroup[] BuildGraphicsPSOGroups(
        BinSpace binSpace,
        int fieldIndex,
        GlobalPsoCache psoCache,
        RenderContext context,
        string psoNamePrefix,
        Func<Entity, ShaderVariantRef> vertexVariantSelector,
        Func<Entity, ShaderVariantRef> pixelVariantSelector,
        bool useVisBuffer,
        bool depthWrite)
    {
        var device = context.Device;
        if (device == null)
            return [];

        var shaderGroups = ComputeGraphicsShaderGroups(
            binSpace,
            fieldIndex,
            vertexVariantSelector,
            pixelVariantSelector);
        if (shaderGroups.Count == 0)
            return [];

        var deviceType = device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";

        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        var groups = new MaterialPSOGroup[shaderGroups.Count];
        for (int g = 0; g < shaderGroups.Count; g++)
        {
            var sg = shaderGroups[g];
            var pso = FindOrCreateGraphicsPSO(
                sg.VertexVariant,
                sg.PixelVariant,
                psoCache,
                context,
                backend,
                layout,
                psoNamePrefix,
                useVisBuffer,
                depthWrite);

            var argsBins = new int[sg.BinCount];
            for (int i = 0; i < sg.BinCount; i++)
            {
                argsBins[i] = sg.BinStart + i;
            }

            groups[g] = new MaterialPSOGroup
            {
                PSO = pso,
                SRB = pso.CreateShaderResourceBinding(false),
                VertexVariant = sg.VertexVariant,
                PixelVariant = sg.PixelVariant,
                Entities = sg.Entities,
                ArgsBins = argsBins,
                BinStart = sg.BinStart,
                BinCount = sg.BinCount,
            };
        }

        return groups;
    }

    private static IPipelineState FindOrCreateComputePSO(
        ShaderVariantRef variantRef,
        GlobalPsoCache psoCache,
        RenderContext context,
        string backend,
        PipelineResourceLayoutDesc layout,
        string namePrefix)
    {
        var shader = variantRef.Shader;
        if (shader == null || context.Device == null)
            throw new InvalidOperationException("ShaderVariantRef must reference a shader asset.");

        var computeVariant = shader.Variants?.FirstOrDefault(v =>
            v.Backend == backend
            && v.Stage == SomeEngine.Assets.Schema.ShaderStage.Compute
            && string.Equals(v.EntryPoint, variantRef.EntryPoint, StringComparison.Ordinal));
        string entryPoint = computeVariant?.EntryPoint
            ?? throw new InvalidOperationException(
                $"No compute entry point '{variantRef.EntryPoint}' found for backend {backend} in shader {shader.Name}");

        var cs = shader.CreateShader(context, entryPoint);
        var ci = new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = $"{namePrefix} PSO ({shader.Name})",
                PipelineType = PipelineType.Compute,
                ResourceLayout = layout,
            },
            Cs = cs,
        };

        return psoCache.GetOrCreateComputePSO(context.Device, ci);
    }

    private static IPipelineState FindOrCreateGraphicsPSO(
        ShaderVariantRef vertexVariantRef,
        ShaderVariantRef pixelVariantRef,
        GlobalPsoCache psoCache,
        RenderContext context,
        string backend,
        PipelineResourceLayoutDesc layout,
        string namePrefix,
        bool useVisBuffer,
        bool depthWrite)
    {
        if (context.Device == null)
            throw new InvalidOperationException("Render device is required to build graphics PSOs.");

        string vsEntryPoint = FindEntryPoint(vertexVariantRef, ShaderStage.Vertex, backend);
        string psEntryPoint = FindEntryPoint(pixelVariantRef, ShaderStage.Pixel, backend);
        var vertexShader = vertexVariantRef.Shader!;
        var pixelShader = pixelVariantRef.Shader!;
        var vs = vertexShader.CreateShader(context, vsEntryPoint);
        var ps = pixelShader.CreateShader(context, psEntryPoint);

        var ci = new GraphicsPipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = $"{namePrefix} PSO ({vertexShader.Name}/{pixelShader.Name})",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = layout,
            },
            GraphicsPipeline = new GraphicsPipelineDesc
            {
                NumRenderTargets = 1,
                RTVFormats = [useVisBuffer ? TextureFormat.R32_UInt : TextureFormat.RGBA8_UNorm],
                DSVFormat = TextureFormat.D32_Float,
                InputLayout = new InputLayoutDesc { LayoutElements = [] },
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc
                {
                    CullMode = CullMode.Back,
                    FrontCounterClockwise = true,
                },
                DepthStencilDesc = new DepthStencilStateDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = depthWrite,
                },
            },
            Vs = vs,
            Ps = ps,
        };

        return psoCache.GetOrCreateGraphicsPSO(context.Device, ci);
    }

    private static string FindEntryPoint(
        ShaderVariantRef variantRef,
        ShaderStage stage,
        string backend)
    {
        var shader = variantRef.Shader;
        if (shader == null || string.IsNullOrEmpty(variantRef.EntryPoint))
            throw new InvalidOperationException("ShaderVariantRef must reference a shader asset and entry point.");

        var variant = shader.Variants?.FirstOrDefault(v =>
            v.Backend == backend
            && v.Stage == stage
            && string.Equals(v.EntryPoint, variantRef.EntryPoint, StringComparison.Ordinal));

        return variant?.EntryPoint
            ?? throw new InvalidOperationException(
                $"No {stage} entry point '{variantRef.EntryPoint}' found for backend {backend} in shader {shader.Name}");
    }

    private static List<GraphicsShaderGroup> ComputeGraphicsShaderGroups(
        BinSpace binSpace,
        int fieldIndex,
        Func<Entity, ShaderVariantRef> vertexVariantSelector,
        Func<Entity, ShaderVariantRef> pixelVariantSelector)
    {
        int totalBins = binSpace.GetTotalBinCount(fieldIndex);
        var groups = new List<GraphicsShaderGroup>();
        if (totalBins == 0)
            return groups;

        int groupStart = 0;
        Entity firstEntity = binSpace.GetEntity(fieldIndex, 0);
        var currentVS = vertexVariantSelector(firstEntity);
        var currentPS = pixelVariantSelector(firstEntity);
        if (currentVS.IsEmpty || currentPS.IsEmpty)
            return [];

        for (int bin = 1; bin <= totalBins; bin++)
        {
            ShaderVariantRef nextVS = default;
            ShaderVariantRef nextPS = default;
            if (bin < totalBins)
            {
                Entity entity = binSpace.GetEntity(fieldIndex, bin);
                nextVS = vertexVariantSelector(entity);
                nextPS = pixelVariantSelector(entity);
                if (nextVS.IsEmpty || nextPS.IsEmpty)
                    return [];
            }

            bool isBreak = bin == totalBins
                || !SameVariant(nextVS, currentVS)
                || !SameVariant(nextPS, currentPS);

            if (!isBreak)
                continue;

            int count = bin - groupStart;
            var entities = new Entity[count];
            for (int i = 0; i < count; i++)
            {
                entities[i] = binSpace.GetEntity(fieldIndex, groupStart + i);
            }

            groups.Add(new GraphicsShaderGroup(groupStart, count, entities, currentVS, currentPS));

            if (bin < totalBins)
            {
                groupStart = bin;
                currentVS = nextVS;
                currentPS = nextPS;
            }
        }

        return groups;
    }

    private readonly record struct GraphicsShaderGroup(
        int BinStart,
        int BinCount,
        Entity[] Entities,
        ShaderVariantRef VertexVariant,
        ShaderVariantRef PixelVariant);

    private static bool SameVariant(ShaderVariantRef left, ShaderVariantRef right)
    {
        return ReferenceEquals(left.Shader, right.Shader)
            && string.Equals(left.EntryPoint, right.EntryPoint, StringComparison.Ordinal);
    }
}

