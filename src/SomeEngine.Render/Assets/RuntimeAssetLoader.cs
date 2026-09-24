using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;
using System.Threading;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using RhiShaderStage = SomeEngine.Rhi.ShaderStage;

namespace SomeEngine.Render.Assets;

public static class RuntimeAssetLoader
{
    public static Mesh LoadMesh(MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new Mesh(
            asset.Name ?? string.Empty,
            asset.Payload.HasValue ? asset.Payload.Value : ReadOnlyMemory<byte>.Empty,
            asset.BvhOffset);
    }

    public static Shader LoadShader(ShaderAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new Shader(
            asset.Name ?? string.Empty,
            CopyVariants(asset),
            CopyAttributes(asset),
            CopyReflections(asset),
            CopyLayouts(asset));
    }

    public static Task<Handle<Mesh>> RequestMesh(
        AssetStore store,
        AssetDatabase database,
        AssetGuid guid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        return store.Request<Mesh>(
            guid,
            (assetGuid, token) =>
            {
                token.ThrowIfCancellationRequested();
                MeshAsset? asset = database.Load<MeshAsset>(assetGuid);
                return asset == null ? null : LoadMesh(asset);
            },
            cancellationToken);
    }

    public static Task<Handle<Shader>> RequestShader(
        AssetStore store,
        AssetDatabase database,
        AssetGuid guid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        return store.Request<Shader>(
            guid,
            (assetGuid, token) =>
            {
                token.ThrowIfCancellationRequested();
                ShaderAsset? asset = database.Load<ShaderAsset>(assetGuid);
                return asset == null ? null : LoadShader(asset);
            },
            cancellationToken);
    }

    public static Task<Handle<Material>> RequestMaterial(
        AssetStore store,
        AssetDatabase database,
        AssetGuid guid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        return store.Request<Material>(
            guid,
            async (assetGuid, token) =>
            {
                token.ThrowIfCancellationRequested();
                MaterialAsset? asset = database.Load<MaterialAsset>(assetGuid);
                if (asset == null)
                    return null;

                await RequestShaders(store, database, asset, token).ConfigureAwait(false);
                return MaterialAssetLoader.LoadFromAsset(
                    asset,
                    store,
                    textureLoader: null,
                    shaderLoader: shaderGuid => store.TryFind(shaderGuid, out Handle<Shader> handle) ? handle : default);
            },
            cancellationToken);
    }

    private static ShaderVariant[] CopyVariants(ShaderAsset asset)
    {
        if (asset.Variants == null || asset.Variants.Count == 0)
            return [];

        var variants = new ShaderVariant[asset.Variants.Count];
        for (int i = 0; i < variants.Length; i++)
        {
            ShaderBytecode source = asset.Variants[i];
            variants[i] = new ShaderVariant(
                source.Backend ?? string.Empty,
                source.Data.HasValue ? source.Data.Value.ToArray() : ReadOnlyMemory<byte>.Empty,
                source.EntryPoint ?? string.Empty,
                MapStage(source.Stage),
                source.ContentHash ?? string.Empty);
        }

        return variants;
    }

    private static async Task RequestShaders(
        AssetStore store,
        AssetDatabase database,
        MaterialAsset asset,
        CancellationToken cancellationToken)
    {
        List<Task<Handle<Shader>>> requests = [];
        foreach (AssetGuid shaderGuid in ShaderGuids(asset))
            requests.Add(RequestShader(store, database, shaderGuid, cancellationToken));

        if (requests.Count > 0)
            await Task.WhenAll(requests).ConfigureAwait(false);
    }

    private static IReadOnlyList<AssetGuid> ShaderGuids(MaterialAsset asset)
    {
        if (asset.Passes == null || asset.Passes.Count == 0)
            return [];

        var guids = new HashSet<AssetGuid>();
        foreach (PassEntry pass in asset.Passes)
        {
            if (AssetGuid.TryParse(pass.ShaderGuid, out AssetGuid shaderGuid) && !shaderGuid.IsEmpty)
                guids.Add(shaderGuid);
        }

        return guids.ToArray();
    }

    private static ShaderAttribute[] CopyAttributes(ShaderAsset asset)
    {
        if (asset.EntryPointAttributes == null || asset.EntryPointAttributes.Count == 0)
            return [];

        var attributes = new ShaderAttribute[asset.EntryPointAttributes.Count];
        for (int i = 0; i < attributes.Length; i++)
        {
            ShaderEntryPointAttribute source = asset.EntryPointAttributes[i];
            attributes[i] = new ShaderAttribute(
                source.VariantIndex,
                source.Name ?? string.Empty,
                source.Args == null
                    ? []
                    : [.. source.Args.Where(static arg => !string.IsNullOrWhiteSpace(arg))]);
        }

        return attributes;
    }

    private static ShaderReflection[] CopyReflections(ShaderAsset asset)
    {
        if (asset.EntryPointReflections == null || asset.EntryPointReflections.Count == 0)
            return [];

        var reflections = new ShaderReflection[asset.EntryPointReflections.Count];
        for (int i = 0; i < reflections.Length; i++)
        {
            ShaderEntryPointReflection source = asset.EntryPointReflections[i];
            reflections[i] = new ShaderReflection(
                source.Backend ?? string.Empty,
                source.EntryPoint ?? string.Empty,
                MapStage(source.Stage),
                CopyResources(source.Reflection?.Resources));
        }

        return reflections;
    }

    private static ShaderResource[] CopyResources(IList<ShaderResourceReflection>? resources)
    {
        if (resources == null || resources.Count == 0)
            return [];

        var result = new ShaderResource[resources.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ShaderResourceReflection source = resources[i];
            result[i] = new ShaderResource(
                source.Name ?? string.Empty,
                source.Stages,
                source.Binding,
                source.Space,
                (BindingType)(byte)source.BindingType);
        }

        return result;
    }

    private static ScalarLayout[] CopyLayouts(ShaderAsset asset)
    {
        if (asset.Metadata?.MaterialScalarLayouts == null || asset.Metadata.MaterialScalarLayouts.Count == 0)
            return [];

        var layouts = new List<ScalarLayout>(asset.Metadata.MaterialScalarLayouts.Count);
        foreach (ShaderMaterialScalarLayout layout in asset.Metadata.MaterialScalarLayouts)
        {
            if (!string.IsNullOrWhiteSpace(layout.Name))
                layouts.Add(LoadLayout(layout));
        }

        return [.. layouts];
    }

    private static ScalarLayout LoadLayout(ShaderMaterialScalarLayout layout)
    {
        if (layout.Fields == null || layout.Fields.Count == 0)
            return ScalarLayout.Empty;

        uint maxFieldEnd = 0;
        uint minFieldOffset = uint.MaxValue;
        foreach (ShaderMaterialScalarField field in layout.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || field.Size == 0)
                continue;

            minFieldOffset = Math.Min(minFieldOffset, field.Offset);
            maxFieldEnd = Math.Max(maxFieldEnd, field.Offset + field.Size);
        }

        if (minFieldOffset == uint.MaxValue)
            return ScalarLayout.Empty;

        if (minFieldOffset != 0)
        {
            throw new InvalidOperationException(
                $"Shader material scalar layout '{layout.Name}' uses non-zero payload base offset {minFieldOffset}. Reimport the shader asset.");
        }

        if (layout.Size < maxFieldEnd)
        {
            throw new InvalidOperationException(
                $"Shader material scalar layout '{layout.Name}' size {layout.Size} is smaller than its fields end {maxFieldEnd}. Reimport the shader asset.");
        }

        var fields = new List<ScalarFieldLayout>(layout.Fields.Count);
        foreach (ShaderMaterialScalarField field in layout.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                continue;

            fields.Add(new ScalarFieldLayout(
                field.Name,
                field.Offset,
                field.Size,
                field.RowCount,
                field.ColumnCount,
                field.ScalarType));
        }

        return ScalarLayout.FromFields(fields, layout.Size);
    }

    private static RhiShaderStage MapStage(AssetShaderStage stage)
        => stage switch
        {
            AssetShaderStage.Vertex => RhiShaderStage.Vertex,
            AssetShaderStage.Pixel => RhiShaderStage.Pixel,
            AssetShaderStage.Compute => RhiShaderStage.Compute,
            AssetShaderStage.Hull => RhiShaderStage.Hull,
            AssetShaderStage.Domain => RhiShaderStage.Domain,
            AssetShaderStage.Geometry => RhiShaderStage.Geometry,
            AssetShaderStage.Amplification => RhiShaderStage.Amplification,
            AssetShaderStage.Mesh => RhiShaderStage.Mesh,
            AssetShaderStage.RayGen => RhiShaderStage.RayGeneration,
            AssetShaderStage.RayMiss => RhiShaderStage.Miss,
            AssetShaderStage.RayClosestHit => RhiShaderStage.ClosestHit,
            AssetShaderStage.RayAnyHit => RhiShaderStage.AnyHit,
            AssetShaderStage.RayIntersection => RhiShaderStage.Intersection,
            AssetShaderStage.Callable => RhiShaderStage.Callable,
            _ => throw new NotSupportedException($"Shader stage '{stage}' is not supported by SomeEngine.Rhi."),
        };
}
