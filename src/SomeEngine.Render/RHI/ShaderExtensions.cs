using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.RHI;

public static class ShaderExtensions
{
    // Attach cached shader instances to the ShaderAsset lifetime
    private static readonly ConditionalWeakTable<ShaderAsset, Dictionary<ShaderCacheKey, IShader>> _shaderCache = new();
    private static readonly ConditionalWeakTable<ShaderAsset, Dictionary<ShaderBindingLookupKey, ShaderBindingInfo>> _bindingCache = new();

    private readonly record struct ShaderCacheKey(string Backend, string EntryPoint);
    private readonly record struct ShaderBindingLookupKey(string Backend, ShaderType Stage, string Name);

    private readonly record struct ShaderBindingInfo(
        ShaderResourceType ResourceType,
        uint Binding,
        uint Space);

    public static ShaderReflectionData? GetReflection(this ShaderAsset asset, RenderContext context)
    {
        if (context.Device == null)
            return null;
        string backend = GetBackendName(context);
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

    private static string GetBackendName(RenderContext context)
    {
        var deviceType = context.Device?.GetDeviceInfo().Type;
        return deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";
    }

    private static Dictionary<ShaderBindingLookupKey, ShaderBindingInfo> GetBindingCache(ShaderAsset asset)
    {
        return _bindingCache.GetValue(asset, static asset =>
        {
            var cache = new Dictionary<ShaderBindingLookupKey, ShaderBindingInfo>();
            if (asset.Reflections == null)
                return cache;

            foreach (var backendReflection in asset.Reflections)
            {
                string backend = backendReflection.Backend ?? string.Empty;
                var resources = backendReflection.Reflection?.Resources;
                if (string.IsNullOrEmpty(backend) || resources == null)
                    continue;

                foreach (var resource in resources)
                {
                    string name = resource.Name ?? string.Empty;
                    if (string.IsNullOrEmpty(name) || resource.ResourceType == 0)
                        continue;

                    var stages = (ShaderType)resource.Stages;
                    var info = new ShaderBindingInfo(
                        (ShaderResourceType)resource.ResourceType,
                        resource.Binding,
                        resource.Space);

                    for (uint stageBit = (uint)ShaderType.Vertex; stageBit <= (uint)ShaderType.Callable; stageBit <<= 1)
                    {
                        var stage = (ShaderType)stageBit;
                        if ((stages & stage) == 0)
                            continue;

                        cache[new ShaderBindingLookupKey(backend, stage, name)] = info;
                    }
                }
            }

            return cache;
        });
    }

    public static bool TryGetReflectedBinding(
        this ShaderAsset asset,
        RenderContext context,
        ShaderType stage,
        string name,
        out ShaderResourceType resourceType,
        out uint binding,
        out uint space)
    {
        resourceType = ShaderResourceType.Unknown;
        binding = 0;
        space = 0;

        string backend = GetBackendName(context);
        var cache = GetBindingCache(asset);
        if (!cache.TryGetValue(new ShaderBindingLookupKey(backend, stage, name), out var info))
            return false;

        resourceType = info.ResourceType;
        binding = info.Binding;
        space = info.Space;
        return true;
    }

    public static IShaderResourceVariable? GetStaticVariableByReflectedBinding(
        this IPipelineState pso,
        RenderContext context,
        ShaderAsset? asset,
        ShaderType stage,
        string name
    )
    {
        if (asset == null)
            return null;
        if (!asset.TryGetReflectedBinding(context, stage, name, out var resourceType, out var binding, out var space))
            return null;

        return pso.GetStaticVariableByBinding(stage, resourceType, binding, space);
    }

    public static IShaderResourceVariable? GetVariableByReflectedBinding(
        this IShaderResourceBinding srb,
        RenderContext context,
        ShaderAsset? asset,
        ShaderType stage,
        string name
    )
    {
        if (asset == null)
            return null;
        if (!asset.TryGetReflectedBinding(context, stage, name, out var resourceType, out var binding, out var space))
            return null;

        return srb.GetVariableByBinding(stage, resourceType, binding, space);
    }

    public static IShader CreateShader(
        this ShaderAsset asset,
        RenderContext context,
        string entryPointName
    )
    {
        if (context.Device == null)
            throw new ArgumentNullException(nameof(context));

        string backend = GetBackendName(context);
        var cacheKey = new ShaderCacheKey(backend, entryPointName);
        var cacheDict = _shaderCache.GetOrCreateValue(asset);
        lock (cacheDict)
        {
            if (cacheDict.TryGetValue(cacheKey, out var cachedShader))
            {
                return cachedShader;
            }

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

            cacheDict[cacheKey] = shader;
            return shader;
        }
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
