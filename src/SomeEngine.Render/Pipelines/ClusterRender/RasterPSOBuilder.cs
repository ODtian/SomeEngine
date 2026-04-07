using Friflo.Engine.ECS;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using Diligent;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Builds ShadePSOGroup arrays from BinSpace for Deform, SW Raster, and HW Draw.
/// Uses the same group shape as shade, but with a simple dynamic resource layout.
/// </summary>
public static class RasterPSOBuilder
{
    /// <summary>
    /// Build compute PSO groups for a given bin field.
    /// Each group shares one PSO (same shader + entry point). Each bin gets its own SRB.
    /// </summary>
    public static ShadePSOGroup[] BuildComputePSOGroups(
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

        var shaderGroups = ShadePSOGroup.ComputeShaderGroups(binSpace, fieldIndex, variantSelector);
        if (shaderGroups.Count == 0)
            return [];

        var deviceType = device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";

        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        var groups = new ShadePSOGroup[shaderGroups.Count];
        for (int g = 0; g < shaderGroups.Count; g++)
        {
            var sg = shaderGroups[g];
            var pso = FindOrCreateComputePSO(sg.VariantRef, psoCache, context, backend, layout, psoNamePrefix);

            var srbs = new IShaderResourceBinding[sg.BinCount];
            var argsBins = new int[sg.BinCount];
            for (int i = 0; i < sg.BinCount; i++)
            {
                srbs[i] = pso.CreateShaderResourceBinding(false);
                if (MaterialEntityUtility.TryGetMaterial(sg.Entities[i], out var material))
                {
                    material.Params.ApplyTo(srbs[i]);
                }

                argsBins[i] = sg.BinStart + i;
            }

            groups[g] = new ShadePSOGroup
            {
                PSO = pso,
                SRBs = srbs,
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
}
