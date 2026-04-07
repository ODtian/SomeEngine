using FlatSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Materials;

public class MaterialAssetRoundtripTests
{
    [Fact]
    public void Roundtrip_BasicMaterial()
    {
        var asset = new MaterialAsset
        {
            Name = "TestMat",
            Passes =
            [
                new PassEntry
                {
                    Shader = "pbr_cluster",
                    Tags = [new TagEntry { Name = "opaque", Value = 0 }],
                }
            ],
            Textures = [new TextureBinding { Name = "AlbedoMap", Path = "textures/white.dds" }],
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal("TestMat", parsed.Name);
        Assert.Equal(1, parsed.Passes!.Count);
        Assert.Equal("pbr_cluster", parsed.Passes[0].Shader);
        Assert.Equal(1, parsed.Passes[0].Tags!.Count);
        Assert.Equal("AlbedoMap", parsed.Textures![0].Name);
    }

    [Fact]
    public void Roundtrip_WithScalarParams()
    {
        var asset = new MaterialAsset
        {
            Name = "ScalarMat",
            Passes = [new PassEntry { Shader = "pbr" }],
            Scalars =
            [
                new ScalarParam { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.5f }) },
                new ScalarParam { Name = "MetallicFactor", Value = new ParamValue(new IntVal { V = 1 }) },
                new ScalarParam { Name = "UseNormalMap", Value = new ParamValue(new BoolVal { V = true }) },
                new ScalarParam
                {
                    Name = "TilingOffset",
                    Value = new ParamValue(new Vec4Val { X = 2.0f, Y = 2.0f, Z = 0.0f, W = 0.0f }),
                },
            ],
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal(4, parsed.Scalars!.Count);
        Assert.Equal(ParamValue.ItemKind.FloatVal, parsed.Scalars[0].Value!.Value.Kind);
        Assert.Equal(ParamValue.ItemKind.IntVal, parsed.Scalars[1].Value!.Value.Kind);
        Assert.Equal(ParamValue.ItemKind.BoolVal, parsed.Scalars[2].Value!.Value.Kind);
        Assert.Equal(ParamValue.ItemKind.Vec4Val, parsed.Scalars[3].Value!.Value.Kind);
    }

    [Fact]
    public void Roundtrip_Vec2_Vec3()
    {
        var asset = new MaterialAsset
        {
            Name = "VecMat",
            Passes = [new PassEntry { Shader = "s" }],
            Scalars =
            [
                new ScalarParam { Name = "Tiling", Value = new ParamValue(new Vec2Val { X = 4.0f, Y = 2.0f }) },
                new ScalarParam { Name = "Color", Value = new ParamValue(new Vec3Val { X = 1.0f, Y = 0.5f, Z = 0.0f }) },
            ],
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal(ParamValue.ItemKind.Vec2Val, parsed.Scalars![0].Value!.Value.Kind);
        Assert.Equal(ParamValue.ItemKind.Vec3Val, parsed.Scalars[1].Value!.Value.Kind);
    }

    [Fact]
    public void LoadFromAsset_UsesShaderGuidOnly()
    {
        var materialSystem = new MaterialSystem();
        var shaderGuid = AssetGuid.New();
        var guidShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "guid_shader", ("ClusterShade", Array.Empty<string>(), "CSMain"));

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "GuidMat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString(), Shader = "legacy_shader", Tags = [new TagEntry { Name = "opaque" }] }],
        };

        var material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialSystem,
            textureLoader: null,
            shaderLoader: guid => guid == shaderGuid ? guidShader : null);

        Assert.Equal(AssetGuid.Parse(asset.AssetGuid!), material.AssetGuid);
        Assert.True(material.Entity.Tags.Has<Opaque>());
        Assert.Equal("CSMain", material.Entity.GetComponent<ClusterShadeComponent>().Default.EntryPoint);
    }

    [Fact]
    public void LoadFromAsset_AppliesSerializedAuthoring_AndMaterialTags()
    {
        var materialSystem = new MaterialSystem();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "pbr",
            ("ClusterShade", Array.Empty<string>(), "CSMain"),
            ("StencilConfig", ["1", "always", "replace"], "CSMain"));

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mat",
            Passes = [new PassEntry { ShaderGuid = shader.AssetGuid, Tags = [new TagEntry { Name = "opaque" }] }],
        };

        var material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialSystem,
            textureLoader: null,
            shaderLoader: _ => shader);

        Assert.True(material.Entity.Tags.Has<Opaque>());
        Assert.Equal("CSMain", material.Entity.GetComponent<ClusterShadeComponent>().Default.EntryPoint);
        Assert.Equal((byte)1, material.Entity.GetComponent<StencilState>().Ref);
    }
}

public class MaterialInstanceRoundtripTests
{
    [Fact]
    public void Roundtrip_WithScalarOverrides()
    {
        var assetGuid = AssetGuid.New();
        var parentGuid = AssetGuid.New();
        var asset = new MaterialInstanceAsset
        {
            AssetGuid = assetGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            Parent = "materials/base.mat",
            Overrides = [new ParamOverride { Name = "AlbedoMap", Path = "textures/brick.dds" }],
            ScalarOverrides = [new ScalarOverride { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.8f }) }],
            TagOverrides =
            [
                new TagOverride { Name = "masked", Value = 0, Remove = false },
                new TagOverride { Name = "opaque", Value = 0, Remove = true },
            ],
        };

        int maxSize = MaterialInstanceAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialInstanceAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialInstanceAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal(assetGuid.ToFlatString(), parsed.AssetGuid);
        Assert.Equal(parentGuid.ToFlatString(), parsed.ParentGuid);
        Assert.Equal(2, parsed.TagOverrides!.Count);
        Assert.True(parsed.TagOverrides[1].Remove);
    }

    [Fact]
    public void LoadFromAsset_UsesParentGuidResolver()
    {
        var materialSystem = new MaterialSystem();
        var parentGuid = AssetGuid.New();
        var instanceGuid = AssetGuid.New();
        var parent = MaterialAssetPipelineTestHelpers.CreateMaterial(materialSystem, "Parent", parentGuid);
        parent.Params.SetScalar("Roughness", 0.2f);
        parent.Entity.AddTag<Opaque>();

        var asset = new MaterialInstanceAsset
        {
            AssetGuid = instanceGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            ScalarOverrides = [new ScalarOverride { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.7f }) }],
            TagOverrides =
            [
                new TagOverride { Name = "masked", Remove = false },
                new TagOverride { Name = "opaque", Remove = true },
            ],
        };

        var instance = MaterialInstanceLoader.LoadFromAsset(
            asset,
            materialSystem,
            guid => guid == parentGuid ? parent : null,
            textureLoader: null);

        Assert.Equal(instanceGuid, instance.AssetGuid);
        Assert.Equal(0.7f, instance.Params.GetScalar("Roughness"));
        Assert.True(instance.Entity.Tags.Has<Masked>());
        Assert.False(instance.Entity.Tags.Has<Opaque>());
    }
}

public class MeshMaterialResolverTests
{
    [Fact]
    public void Resolve_UsesMaterialGuidsOnly()
    {
        var guid = AssetGuid.New();
        var guidMaterial = new Material { Name = "GuidMat", AssetGuid = guid };

        var mesh = new MeshAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mesh",
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Attributes = [],
            DefaultMaterialGuids = [guid.ToFlatString(), string.Empty],
            DefaultMaterialSlots = ["legacy_a", "legacy_b"],
        };

        var resolved = MeshMaterialResolver.Resolve(mesh, assetGuid => assetGuid == guid ? guidMaterial : null);

        Assert.Equal(2, resolved.Length);
        Assert.Same(guidMaterial, resolved[0]);
        Assert.Null(resolved[1]);
    }
}

public class ShaderParamBagScalarTests
{
    [Fact]
    public void SetScalar_Float_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        bag.SetScalar("roughness", 0.7f);
        Assert.Equal(0.7f, bag.GetScalar("roughness"));
    }

    [Fact]
    public void SetScalar_Int_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        bag.SetScalar("mode", 3);
        Assert.Equal(3, bag.GetScalar("mode"));
    }

    [Fact]
    public void SetScalar_Vector4_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        var value = new System.Numerics.Vector4(1, 2, 3, 4);
        bag.SetScalar("tiling", value);
        Assert.Equal(value, bag.GetScalar("tiling"));
    }

    [Fact]
    public void GetScalar_NonExistent_ReturnsNull()
    {
        var bag = new ShaderParamBag();
        Assert.Null(bag.GetScalar("missing"));
    }

    [Fact]
    public void SetScalar_IncludesInSignature()
    {
        var bagA = new ShaderParamBag();
        var bagB = new ShaderParamBag();
        bagA.SetScalar("r", 0.5f);
        bagB.SetScalar("r", 0.8f);
        Assert.NotEqual(bagB.GetSignatureHash(), bagA.GetSignatureHash());
    }
}

internal static class MaterialAssetPipelineTestHelpers
{
    internal static Material CreateMaterial(MaterialSystem materialSystem, string name, AssetGuid guid)
    {
        var material = new Material
        {
            Name = name,
            AssetGuid = guid,
            System = materialSystem,
        };

        material.Entity = materialSystem.Store.CreateEntity();
        material.Entity.AddComponent(new MaterialRef { Owner = material });
        return material;
    }

    internal static ShaderAsset CreateShaderAsset(AssetGuid guid, string name, params (string AttributeName, IReadOnlyList<string> Args, string EntryPoint)[] authorings)
    {
        var attributes = new List<ShaderEntryPointAttribute>();
        var variants = new List<ShaderBytecode>();

        for (int i = 0; i < authorings.Length; i++)
        {
            variants.Add(new ShaderBytecode
            {
                Backend = "dxil",
                Stage = ShaderStage.Compute,
                EntryPoint = authorings[i].EntryPoint,
                Data = Array.Empty<byte>(),
                ContentHash = $"{name}-{i}",
            });

            attributes.Add(new ShaderEntryPointAttribute
            {
                VariantIndex = i,
                Name = authorings[i].AttributeName,
                Args = authorings[i].Args.Count == 0 ? [] : [.. authorings[i].Args],
            });
        }

        if (variants.Count == 0)
        {
            variants.Add(new ShaderBytecode
            {
                Backend = "dxil",
                Stage = ShaderStage.Compute,
                EntryPoint = "CSMain",
                Data = Array.Empty<byte>(),
                ContentHash = $"{name}-default",
            });
        }

        return new ShaderAsset
        {
            AssetGuid = guid.ToFlatString(),
            Name = name,
            EntryPointAttributes = attributes,
            Metadata = new ShaderMetadata
            {
                Tags = [],
                MaterialBindings = [],
            },
            Variants = variants,
            Reflections = [],
            ImportTrace = new ImportTrace
            {
                SourceGuid = SourceGuid.New().ToFlatString(),
                SourcePath = $"{name}.slang",
                SubAssetKey = "shader:main",
                ContentFingerprint = "fp",
                Dependencies = [],
                ImporterVersion = 1,
            },
        };
    }
}
