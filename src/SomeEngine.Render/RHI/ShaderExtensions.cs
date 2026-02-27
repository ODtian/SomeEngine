using System;
using System.Collections.Generic;
using System.Linq;
using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.RHI;

public static class ShaderExtensions
{
    public static ShaderReflectionData? GetReflection(this ShaderAsset asset, RenderContext context)
    {
        if (context.Device == null)
            return null;
        var deviceType = context.Device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";
        return asset.Reflections?.FirstOrDefault(r => r.Backend == backend)?.Reflection;
    }

    public static ShaderResourceVariableDesc[] GetResourceVariables(
        this ShaderAsset asset,
        RenderContext context,
        Func<string, Diligent.ShaderResourceVariableType?>? typePolicy = null
    )
    {
        var reflection = asset.GetReflection(context);
        if (reflection?.Resources == null)
            return [];

        if (context.Device == null)
            return [];

        Console.WriteLine(
            $"--- Shader Asset Layout: {asset.Name} ({context.Device.GetDeviceInfo().Type}) ---"
        );

        // Use a map to merge variables that share the same name within overlapping stages.
        var mergedVariables = new Dictionary<string, ShaderResourceVariableDesc>();

        foreach (var r in reflection.Resources)
        {
            var resourceName = r.Name ?? string.Empty;
            var key = resourceName;

            var varType = typePolicy?.Invoke(r.Name ?? "");
            if (varType == null)
            {
                // Log skipped variable
                continue;
            }

            var desc = new ShaderResourceVariableDesc
            {
                Name = r.Name,
                Type = varType.Value,
                ShaderStages = (Diligent.ShaderType)r.Stages,
            };

            if (mergedVariables.TryGetValue(key, out var existing))
            {
                // If the same resource name is used, merge stage masks.
                existing.ShaderStages |= desc.ShaderStages;
                mergedVariables[key] = existing;
            }
            else
            {
                mergedVariables[key] = desc;
            }

            Console.WriteLine($"  Name={r.Name}, Stages={(Diligent.ShaderType)r.Stages}");
        }
        Console.WriteLine($"------------------------------------------");
        return [.. mergedVariables.Values];
    }

    public static IShaderResourceVariable? GetStaticVariable(
        this IPipelineState pso,
        RenderContext context,
        ShaderAsset? asset,
        ShaderType stage,
        string name
    )
    {
        if (asset == null)
            return null;
        return pso.GetStaticVariableByName(stage, name);
    }

    public static IShaderResourceVariable? GetVariable(
        this IShaderResourceBinding srb,
        RenderContext context,
        ShaderAsset? asset,
        ShaderType stage,
        string name
    )
    {
        if (asset == null)
            return null;
        return srb.GetVariableByName(stage, name);
    }

    public static IShader CreateShader(
        this ShaderAsset asset,
        RenderContext context,
        string entryPointName
    )
    {
        if (context.Device == null)
            throw new ArgumentNullException(nameof(context));
        // Determine backend
        var deviceType = context.Device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";

        // Find variant
        if (asset.Variants == null)
            throw new Exception("Asset has no variants");

        var variant =
            asset.Variants.FirstOrDefault(v =>
                v.Backend == backend && v.EntryPoint == entryPointName
            )
            ?? throw new Exception(
                $"Shader variant not found for backend {backend} and entry point {entryPointName} in asset {asset.Name}"
            );
        ShaderType type = MapType(variant.Stage);

        var shaderCI = new ShaderCreateInfo();
        shaderCI.Desc.ShaderType = type;
        shaderCI.Desc.Name = $"{asset.Name}_{entryPointName}";
        shaderCI.SourceLanguage = ShaderSourceLanguage.Bytecode;
        shaderCI.EntryPoint = entryPointName;

        // Variant Data is Memory<byte>?
        if (!variant.Data.HasValue)
            throw new Exception("Variant has no data");
        byte[] data = variant.Data.Value.ToArray();

        shaderCI.ByteCode = data;

        IShader shader;
        try
        {
            shader = context.Device.CreateShader(shaderCI, out var compilerOutput);
        }
        catch (Exception ex)
        {
            // Log error if possible, or rethrow with more info
            throw new Exception($"Failed to create shader {shaderCI.Desc.Name}: {ex.Message}", ex);
        }

        return shader;
    }

    private static ShaderType MapType(SomeEngine.Assets.Schema.ShaderStage stage)
    {
        return stage switch
        {
            SomeEngine.Assets.Schema.ShaderStage.Vertex => ShaderType.Vertex,
            SomeEngine.Assets.Schema.ShaderStage.Pixel => ShaderType.Pixel,
            SomeEngine.Assets.Schema.ShaderStage.Compute => ShaderType.Compute,
            SomeEngine.Assets.Schema.ShaderStage.Hull => ShaderType.Hull,
            SomeEngine.Assets.Schema.ShaderStage.Domain => ShaderType.Domain,
            SomeEngine.Assets.Schema.ShaderStage.Geometry => ShaderType.Geometry,
            SomeEngine.Assets.Schema.ShaderStage.Amplification => ShaderType.Amplification,
            SomeEngine.Assets.Schema.ShaderStage.Mesh => ShaderType.Mesh,
            SomeEngine.Assets.Schema.ShaderStage.RayGen => ShaderType.RayGen,
            SomeEngine.Assets.Schema.ShaderStage.RayMiss => ShaderType.RayMiss,
            SomeEngine.Assets.Schema.ShaderStage.RayClosestHit => ShaderType.RayClosestHit,
            SomeEngine.Assets.Schema.ShaderStage.RayAnyHit => ShaderType.RayAnyHit,
            SomeEngine.Assets.Schema.ShaderStage.RayIntersection => ShaderType.RayIntersection,
            SomeEngine.Assets.Schema.ShaderStage.Callable => ShaderType.Callable,
            _ => throw new NotImplementedException($"Stage {stage} not supported"),
        };
    }
}
