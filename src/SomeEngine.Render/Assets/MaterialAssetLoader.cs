using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Assets;

public static class MaterialAssetLoader
{
    public delegate Handle<Texture> TextureLoadFunc(AssetGuid textureGuid);
    public delegate Handle<Shader> ShaderLoadFunc(AssetGuid guid);

    public static Material Load(
        byte[] data,
        AssetStore store,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetCodec.Parse(data);
        return LoadFromAsset(asset, store, textureLoader, shaderLoader);
    }

    public static Material LoadFromFile(
        string path,
        AssetStore store,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetCodec.Load(path);
        return LoadFromAsset(asset, store, textureLoader, shaderLoader);
    }

    public static Material LoadFromAsset(
        MaterialAsset asset,
        AssetStore store,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var material = new Material
        {
            Name = asset.Name ?? string.Empty,
        };

        ScalarLayout? scalarLayout = null;
        var passes = new List<MaterialPass>();
        if (asset.Passes is { Count: > 0 } sourcePasses)
        {
            foreach (PassEntry source in sourcePasses)
                AddPasses(source, store, shaderLoader, passes, ref scalarLayout);
        }

        material.SetPasses(CollectionsMarshal.AsSpan(passes));
        ApplyTextures(asset, material, textureLoader);
        ApplyScalars(asset, material, scalarLayout);
        return material;
    }

    public static void ApplyTexture(Material material, string name, Handle<Texture> handle)
    {
        ArgumentNullException.ThrowIfNull(material);
        switch (name)
        {
            case "AlbedoMap":
                material.AlbedoMap = handle;
                break;
            case "NormalMap":
                material.NormalMap = handle;
                break;
            case "ARMMap":
                material.ArmMap = handle;
                break;
            case "EmissiveMap":
                material.EmissiveMap = handle;
                break;
            default:
                throw new InvalidOperationException($"Material '{material.Name}' does not expose texture field '{name}'.");
        }

        material.TouchBindings();
    }

    public static void ApplyScalar(Material material, string name, ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(material);
        switch (name)
        {
            case "BaseColorTint":
                material.BaseColorTint = ToVector(value);
                break;
            case "MetallicFactor":
                material.MetallicFactor = ToFloat(value);
                break;
            case "Roughness":
                material.Roughness = ToFloat(value);
                break;
            case "EmissiveFactor":
                material.EmissiveFactor = ToVector(value);
                break;
            default:
                throw new InvalidOperationException($"Material '{material.Name}' does not expose scalar field '{name}'.");
        }

        material.TouchScalars();
    }

    private static void AddPasses(
        PassEntry source,
        AssetStore store,
        ShaderLoadFunc? shaderLoader,
        List<MaterialPass> passes,
        ref ScalarLayout? scalarLayout)
    {
        MaterialState state = ReadState(source);
        if (shaderLoader == null
            || !AssetGuid.TryParse(source.ShaderGuid, out AssetGuid shaderGuid)
            || shaderGuid.IsEmpty)
        {
            return;
        }

        Handle<Shader> shaderHandle = shaderLoader(shaderGuid);
        if (!shaderHandle.IsValid || !store.TryGet(shaderHandle, out Shader? shader) || shader == null)
            return;

        scalarLayout ??= PickScalarLayout(shader);
        if (shader.Attributes.Count == 0)
            return;

        foreach (ShaderAttribute attribute in shader.Attributes)
        {
            if (!shader.TryEntry(attribute, source.EntryPoint, out string entry))
                continue;
            if (!TryTarget(attribute.Name, attribute.Args, out string target))
                continue;

            passes.Add(new MaterialPass(target, shaderHandle, entry, state));
        }
    }

    private static MaterialState ReadState(PassEntry source)
    {
        MaterialState state = MaterialState.Default;
        if (source.Tags != null)
        {
            foreach (TagEntry tag in source.Tags)
            {
                switch (Key(tag.Name))
                {
                    case "opaque":
                        state = state with { Surface = SurfaceMode.Opaque };
                        break;
                    case "masked":
                        state = state with { Surface = SurfaceMode.Masked };
                        break;
                    case "translucent":
                        state = state with { Surface = SurfaceMode.Translucent };
                        break;
                    case "twosided":
                        state = state with { TwoSided = true };
                        break;
                }
            }
        }

        if (source.Components != null)
        {
            foreach (ComponentEntry component in source.Components)
                state = ReadComponent(component, state);
        }

        return state;
    }

    private static MaterialState ReadComponent(ComponentEntry component, MaterialState state)
    {
        if (string.IsNullOrWhiteSpace(component.Json))
            return state;

        using JsonDocument doc = JsonDocument.Parse(component.Json);
        JsonElement root = doc.RootElement;
        switch (Key(component.TypeName))
        {
            case "overlayshade":
                return state with { OverlayLayer = ReadInt(root, "Layer") };
            case "clusterdeform":
                return state with { BoundsExpansion = MathF.Max(0f, ReadFloat(root, "BoundsExpansion")) };
            case "stencilstate":
                return state with
                {
                    StencilRef = (byte)Math.Clamp(ReadInt(root, "Ref"), byte.MinValue, byte.MaxValue),
                    StencilCompare = ReadCompare(root, "Compare"),
                    StencilPass = ReadStencil(root, "PassOp"),
                };
            default:
                return state;
        }
    }

    private static bool TryTarget(string? attribute, IEnumerable<string>? args, out string target)
    {
        target = string.Empty;
        if (!string.Equals(attribute, "MaterialTarget", StringComparison.Ordinal))
            return false;

        string value = args?.FirstOrDefault(static arg => !string.IsNullOrWhiteSpace(arg))?.Trim()
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        target = value;
        return true;
    }

    private static void ApplyTextures(
        MaterialAsset asset,
        Material material,
        TextureLoadFunc? textureLoader)
    {
        if (asset.Textures == null)
            return;

        foreach (TextureBinding binding in asset.Textures)
        {
            if (binding.Name == null
                || binding.TextureGuid == null
                || !AssetGuid.TryParse(binding.TextureGuid, out AssetGuid textureGuid)
                || textureGuid.IsEmpty)
            {
                continue;
            }

            ApplyTexture(material, binding.Name, textureLoader?.Invoke(textureGuid) ?? default);
        }
    }

    private static void ApplyScalars(
        MaterialAsset asset,
        Material material,
        ScalarLayout? shaderLayout)
    {
        ScalarLayout scalarLayout = shaderLayout ?? ScalarLayout.Empty;
        if (asset.Scalars is { Count: > 0 } && scalarLayout.PayloadByteSize == 0)
        {
            throw new InvalidOperationException(
                $"Material '{material.Name}' declares scalar parameters, but no shader material scalar layout was found.");
        }

        material.SetScalarLayout(scalarLayout);
        if (asset.Scalars == null)
            return;

        foreach (ScalarParam scalar in asset.Scalars)
        {
            if (scalar.Name != null && scalar.Value != null)
                ApplyScalar(material, scalar.Name, scalar.Value.Value);
        }
    }

    private static float ToFloat(ParamValue value)
        => value.Kind switch
        {
            ParamValue.ItemKind.FloatVal => value.FloatVal!.V,
            ParamValue.ItemKind.IntVal => value.IntVal!.V,
            ParamValue.ItemKind.BoolVal => value.BoolVal!.V ? 1.0f : 0.0f,
            ParamValue.ItemKind.Vec2Val => value.Vec2Val!.X,
            ParamValue.ItemKind.Vec3Val => value.Vec3Val!.X,
            ParamValue.ItemKind.Vec4Val => value.Vec4Val!.X,
            _ => 0.0f,
        };

    private static Vector4 ToVector(ParamValue value)
        => value.Kind switch
        {
            ParamValue.ItemKind.FloatVal => new Vector4(value.FloatVal!.V),
            ParamValue.ItemKind.IntVal => new Vector4(value.IntVal!.V),
            ParamValue.ItemKind.BoolVal => new Vector4(value.BoolVal!.V ? 1.0f : 0.0f),
            ParamValue.ItemKind.Vec2Val => new Vector4(value.Vec2Val!.X, value.Vec2Val!.Y, 0, 0),
            ParamValue.ItemKind.Vec3Val => new Vector4(value.Vec3Val!.X, value.Vec3Val!.Y, value.Vec3Val!.Z, 0),
            ParamValue.ItemKind.Vec4Val => new Vector4(value.Vec4Val!.X, value.Vec4Val!.Y, value.Vec4Val!.Z, value.Vec4Val!.W),
            _ => default,
        };

    private static ScalarLayout? PickScalarLayout(Shader shader)
    {
        foreach (ScalarLayout layout in shader.ScalarLayouts)
        {
            if (layout.PayloadByteSize > 0)
                return layout;
        }

        return null;
    }

    private static string Key(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static float ReadFloat(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetSingle(out float result)
            ? result
            : 0f;

    private static CompareOp ReadCompare(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value)
            && Enum.TryParse(value.ToString(), ignoreCase: true, out CompareOp result)
                ? result
                : CompareOp.Always;

    private static Materials.StencilOp ReadStencil(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value)
            && Enum.TryParse(value.ToString(), ignoreCase: true, out Materials.StencilOp result)
                ? result
                : Materials.StencilOp.Keep;
}
