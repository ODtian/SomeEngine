using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.RHI;
using System;
using System.Linq;

using System.Collections.Generic;

namespace SomeEngine.Render.RHI;

public static class ShaderExtensions
{
    public static ShaderReflectionData? GetReflection(this ShaderAsset asset, RenderContext context)
    {
        if (context.Device == null) return null;
        var deviceType = context.Device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";
        return asset.Reflections?.FirstOrDefault(r => r.Backend == backend)?.Reflection;
    }

    public static ShaderResourceVariableDesc[] GetResourceVariables(this ShaderAsset asset, RenderContext context)
    {
        var reflection = asset.GetReflection(context);
        if (reflection?.Resources == null)
            return Array.Empty<ShaderResourceVariableDesc>();

        if (context.Device == null) return Array.Empty<ShaderResourceVariableDesc>();

        Console.WriteLine($"--- Shader Asset Layout: {asset.Name} ({context.Device.GetDeviceInfo().Type}) ---");
        
        // Use a map to merge variables that share the same slot and registration class within overlapping stages.
        // Key: (Set, Binding, RegisterClass)
        var mergedVariables = new Dictionary<(uint Set, uint Binding, int Class), ShaderResourceVariableDesc>();
        
        foreach (var r in reflection.Resources)
        {
            var type = MapResourceType(r.Category);
            var regClass = (int)GetShaderResourceRegisterClass(type);
            var key = (r.Set, r.Binding, regClass);

            var desc = new ShaderResourceVariableDesc
            {
                Name = r.Name,
                Type = MapVariableType(r.Type),
                ShaderStages = (Diligent.ShaderType)r.Stages,
                Binding = r.Binding,
                Set = r.Set,
                ResourceType = type
            };

            if (mergedVariables.TryGetValue(key, out var existing))
            {
                // If the same slot is used, we must merge the stages and potentially names.
                // Diligent will handle the binding as long as the Stage mask is correct.
                existing.ShaderStages |= desc.ShaderStages;
                // Keep the first name found or combine them? Diligent prefers the name in the shader.
                // If they have different names, we just print a warning but merge them since they are physically the same slot.
                if (existing.Name != desc.Name)
                {
                    Console.WriteLine($"  [INFO] Merging resources '{existing.Name}' and '{desc.Name}' sharing slot Set={r.Set} Bind={r.Binding} Class={regClass}");
                }
                mergedVariables[key] = existing;
            }
            else
            {
                mergedVariables[key] = desc;
            }

            string categoryChar = r.Category switch
            {
                ShaderResourceCategory.ConstantBuffer => "b",
                ShaderResourceCategory.ShaderResource => "t",
                ShaderResourceCategory.UnorderedAccess => "u",
                ShaderResourceCategory.SamplerState => "s",
                _ => "?"
            };
            Console.WriteLine($"  [{categoryChar}] Set={r.Set}, Binding={r.Binding}, Name={r.Name}, Stages={(Diligent.ShaderType)r.Stages}");
        }
        Console.WriteLine($"------------------------------------------");
        return mergedVariables.Values.ToArray();
    }

    private enum ShaderResourceRegisterClass
    {
        Srv, Uav, Cbv, Sampler, Unknown
    }

    private static ShaderResourceRegisterClass GetShaderResourceRegisterClass(Diligent.ShaderResourceType type)
    {
        return type switch
        {
            Diligent.ShaderResourceType.ConstantBuffer => ShaderResourceRegisterClass.Cbv,
            Diligent.ShaderResourceType.TextureSrv => ShaderResourceRegisterClass.Srv,
            Diligent.ShaderResourceType.BufferSrv => ShaderResourceRegisterClass.Srv,
            Diligent.ShaderResourceType.InputAttachment => ShaderResourceRegisterClass.Srv,
            Diligent.ShaderResourceType.AccelStruct => ShaderResourceRegisterClass.Srv,
            Diligent.ShaderResourceType.TextureUav => ShaderResourceRegisterClass.Uav,
            Diligent.ShaderResourceType.BufferUav => ShaderResourceRegisterClass.Uav,
            Diligent.ShaderResourceType.Sampler => ShaderResourceRegisterClass.Sampler,
            _ => ShaderResourceRegisterClass.Unknown
        };
    }

    private static Diligent.ShaderResourceType MapResourceType(ShaderResourceCategory category)
    {
        return category switch
        {
            SomeEngine.Assets.Schema.ShaderResourceCategory.ConstantBuffer => Diligent.ShaderResourceType.ConstantBuffer,
            SomeEngine.Assets.Schema.ShaderResourceCategory.ShaderResource => Diligent.ShaderResourceType.TextureSrv,
            SomeEngine.Assets.Schema.ShaderResourceCategory.UnorderedAccess => Diligent.ShaderResourceType.TextureUav,
            SomeEngine.Assets.Schema.ShaderResourceCategory.SamplerState => Diligent.ShaderResourceType.Sampler,
            _ => Diligent.ShaderResourceType.Unknown
        };
    }

    public static (uint Binding, uint Set, Diligent.ShaderResourceType Type) GetResourceBinding(this ShaderAsset asset, RenderContext context, string name)
    {
        var reflection = asset.GetReflection(context);
        var res = reflection?.Resources?.FirstOrDefault(r => r.Name == name);
        if (res == null) return (uint.MaxValue, uint.MaxValue, Diligent.ShaderResourceType.Unknown);
        
        return (res.Binding, res.Set, MapResourceType(res.Category));
    }

    public static IShaderResourceVariable? GetStaticVariable(this IPipelineState pso, RenderContext context, ShaderAsset? asset, ShaderType stage, string name)
    {
        if (asset == null) return null;
        var (binding, set, type) = asset.GetResourceBinding(context, name);
        if (binding == uint.MaxValue) return null;
        return pso.GetStaticVariableByBinding(stage, binding, set, type);
    }

    public static IShaderResourceVariable? GetVariable(this IShaderResourceBinding srb, RenderContext context, ShaderAsset? asset, ShaderType stage, string name)
    {
        if (asset == null) return null;
        var (binding, set, type) = asset.GetResourceBinding(context, name);
        if (binding == uint.MaxValue) return null;
        return srb.GetVariableByBinding(stage, binding, set, type);
    }

    private static Diligent.ShaderResourceVariableType MapVariableType(SomeEngine.Assets.Schema.ShaderResourceVariableType type)
    {
        return type switch
        {
            SomeEngine.Assets.Schema.ShaderResourceVariableType.Static => Diligent.ShaderResourceVariableType.Static,
            SomeEngine.Assets.Schema.ShaderResourceVariableType.Mutable => Diligent.ShaderResourceVariableType.Mutable,
            SomeEngine.Assets.Schema.ShaderResourceVariableType.Dynamic => Diligent.ShaderResourceVariableType.Dynamic,
            _ => Diligent.ShaderResourceVariableType.Static
        };
    }

    public static IShader CreateShader(
        this ShaderAsset asset, RenderContext context, string entryPointName
    )
    {
        if (context.Device == null) throw new ArgumentNullException(nameof(context.Device));
        // Determine backend
        var deviceType = context.Device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";

        // Find variant
        if (asset.Variants == null)
            throw new Exception("Asset has no variants");

        var variant = asset.Variants.FirstOrDefault(
            v => v.Backend == backend && v.EntryPoint == entryPointName
        );
        if (variant == null)
        {
            throw new Exception(
                $"Shader variant not found for backend {backend} and entry point {entryPointName} in asset {asset.Name}"
            );
        }

        ShaderType type = MapType(variant.Stage);

        ShaderCreateInfo shaderCI = new ShaderCreateInfo();
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
        IDataBlob? compilerOutput;
        try
        {
            shader = context.Device.CreateShader(shaderCI, out compilerOutput);
        }
        catch (Exception ex)
        {
            // Log error if possible, or rethrow with more info
            throw new Exception(
                $"Failed to create shader {shaderCI.Desc.Name}: {ex.Message}", ex
            );
        }

        return shader;
    }

    private static ShaderType MapType(SomeEngine.Assets.Schema.ShaderStage stage)
    {
        return stage switch {
            SomeEngine.Assets.Schema.ShaderStage.Vertex => ShaderType.Vertex,
            SomeEngine.Assets.Schema.ShaderStage.Pixel => ShaderType.Pixel,
            SomeEngine.Assets.Schema.ShaderStage.Compute => ShaderType.Compute,
            SomeEngine.Assets.Schema.ShaderStage.Hull => ShaderType.Hull,
            SomeEngine.Assets.Schema.ShaderStage.Domain => ShaderType.Domain,
            SomeEngine.Assets.Schema.ShaderStage.Geometry => ShaderType.Geometry,
            SomeEngine.Assets.Schema.ShaderStage.Amplification =>
                ShaderType.Amplification,
            SomeEngine.Assets.Schema.ShaderStage.Mesh => ShaderType.Mesh,
            SomeEngine.Assets.Schema.ShaderStage.RayGen => ShaderType.RayGen,
            SomeEngine.Assets.Schema.ShaderStage.RayMiss => ShaderType.RayMiss,
            SomeEngine.Assets.Schema.ShaderStage.RayClosestHit =>
                ShaderType.RayClosestHit,
            SomeEngine.Assets.Schema.ShaderStage.RayAnyHit => ShaderType.RayAnyHit,
            SomeEngine.Assets.Schema.ShaderStage.RayIntersection =>
                ShaderType.RayIntersection,
            SomeEngine.Assets.Schema.ShaderStage.Callable => ShaderType.Callable,
            _ => throw new NotImplementedException($"Stage {stage} not supported")
        };
    }
}
