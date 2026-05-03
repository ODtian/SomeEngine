using FlatSharp;
using Friflo.Engine.ECS;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Diligent;
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
                    ShaderGuid = "pbr-cluster-guid",
                    Tags = [new TagEntry { Name = "opaque", Value = 0 }],
                }
            ],
            Textures = [new TextureBinding { Name = "AlbedoMap", TextureGuid = AssetGuid.New().ToFlatString() }],
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal("TestMat", parsed.Name);
        Assert.Equal(1, parsed.Passes!.Count);
        Assert.Equal("pbr-cluster-guid", parsed.Passes[0].ShaderGuid);
        Assert.Equal(1, parsed.Passes[0].Tags!.Count);
        Assert.Equal("AlbedoMap", parsed.Textures![0].Name);
    }

    [Fact]
    public void Roundtrip_WithScalarParams()
    {
        var asset = new MaterialAsset
        {
            Name = "ScalarMat",
            Passes = [new PassEntry { ShaderGuid = "pbr-guid" }],
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
            Passes = [new PassEntry { ShaderGuid = "shader-guid" }],
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
    public void LoadFromAsset_AppliesShaderAuthoring()
    {
        EntityStore materialStore = new();
        var shaderGuid = AssetGuid.New();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "guid_shader", ("ClusterShade", Array.Empty<string>(), "CSMain"));

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "GuidMat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString(), Tags = [new TagEntry { Name = "opaque" }] }],
        };

        var material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialStore,
            textureLoader: null,
            shaderLoader: guid => guid == shaderGuid ? shader : null);

        Assert.Equal(AssetGuid.Parse(asset.AssetGuid!), material.AssetGuid);
        Assert.True(material.PassEntities[0].Tags.Has<Opaque>());
        Assert.True(material.PassEntities[0].TryGetComponent<ClusterShadeComponent>(out _));
    }

    [Fact]
    public void LoadFromAsset_AppliesSerializedPassAuthoring()
    {
        EntityStore materialStore = new();

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mat",
            Passes =
            [
                new PassEntry
                {
                    Tags = [new TagEntry { Name = "opaque" }],
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = nameof(StencilState),
                            Json = "{\"Ref\":1,\"Compare\":7,\"PassOp\":2}",
                        },
                    ],
                },
            ],
        };

        var material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialStore,
            textureLoader: null);

        Assert.True(material.PassEntities[0].Tags.Has<Opaque>());
        Assert.True(material.PassEntities[0].TryGetComponent<StencilState>(out _));
    }

    [Fact]
    public void LoadFromAsset_AppliesClusterDeformBoundsExpansionComponent()
    {
        EntityStore materialStore = new();

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "DeformingMat",
            Passes =
            [
                new PassEntry
                {
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = nameof(ClusterDeform),
                            Json = "{\"BoundsExpansion\":0.375}",
                        },
                    ],
                },
            ],
        };

        var material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialStore,
            textureLoader: null);

        Assert.True(material.PassEntities[0].TryGetComponent<ClusterDeform>(out ClusterDeform deform));
        Assert.Equal(0.375f, deform.BoundsExpansion);
    }

    [Fact]
    public void Roundtrip_PassEntitySnapshots_PreserveTagsAndComponentPayload()
    {
        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "MultiPass",
            Passes =
            [
                new PassEntry
                {
                    Tags = [new TagEntry { Name = "opaque" }],
                },
                new PassEntry
                {
                    Tags = [new TagEntry { Name = "masked" }],
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = nameof(OverlayShade),
                            Json = "{\"Layer\":2}",
                        },
                    ],
                },
            ],
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buffer, asset);
        MaterialAsset parsed = MaterialAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal(2, parsed.Passes!.Count);
        Assert.Equal("opaque", parsed.Passes[0].Tags![0].Name);
        Assert.Equal(nameof(OverlayShade), parsed.Passes[1].Components![0].TypeName);
        Assert.Equal("{\"Layer\":2}", parsed.Passes[1].Components[0].Json);
    }

    [Fact]
    public void LoadFromAsset_CreatesOneEntityPerPassSnapshot()
    {
        EntityStore materialStore = new();

        var asset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "MultiPass",
            Passes =
            [
                new PassEntry
                {
                    Tags = [new TagEntry { Name = "opaque" }],
                },
                new PassEntry
                {
                    Tags = [new TagEntry { Name = "masked" }],
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = nameof(OverlayShade),
                            Json = "{\"Layer\":2}",
                        },
                    ],
                },
            ],
        };

        Material material = MaterialAssetLoader.LoadFromAsset(
            asset,
            materialStore,
            textureLoader: null);

        Assert.Equal(2, material.PassEntities.Length);
        Assert.True(material.PassEntities[0].Tags.Has<Opaque>());
        Assert.True(material.PassEntities[1].Tags.Has<Masked>());
        Assert.True(material.PassEntities[1].TryGetComponent<OverlayShade>(out _));
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
            Overrides = [new ParamOverride { Name = "AlbedoMap", TextureGuid = AssetGuid.New().ToFlatString() }],
            ScalarOverrides = [new ScalarOverride { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.8f }) }],
        };

        int maxSize = MaterialInstanceAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int written = MaterialInstanceAsset.Serializer.Write(buffer, asset);
        var parsed = MaterialInstanceAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.Equal(assetGuid.ToFlatString(), parsed.AssetGuid);
        Assert.Equal(parentGuid.ToFlatString(), parsed.ParentGuid);
        Assert.Single(parsed.Overrides!);
        Assert.Single(parsed.ScalarOverrides!);
    }

    [Fact]
    public void LoadFromAsset_UsesParentGuidResolver()
    {
        EntityStore materialStore = new();
        var parentGuid = AssetGuid.New();
        var instanceGuid = AssetGuid.New();
        var parent = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "Parent", parentGuid);
        parent.Params.SetScalar("Roughness", 0.2f);
        parent.PassEntities[0].AddTag<Opaque>();

        var asset = new MaterialInstanceAsset
        {
            AssetGuid = instanceGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            ScalarOverrides = [new ScalarOverride { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.7f }) }],
        };

        var instance = MaterialInstanceLoader.LoadFromAsset(
            asset,
            materialStore,
            guid => guid == parentGuid ? parent : null,
            textureLoader: null);

        Assert.Equal(instanceGuid, instance.AssetGuid);
        Assert.Equal(0.7f, instance.Params.GetScalar("Roughness"));
        Assert.True(instance.PassEntities[0].Tags.Has<Opaque>());
    }

    [Fact]
    public void LoadFromAsset_PreservesPassEntities_WhenInstantiatingParent()
    {
        EntityStore materialStore = new();
        var parentGuid = AssetGuid.New();
        var instanceGuid = AssetGuid.New();
        Material parent = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "Parent", parentGuid);
        Entity secondaryPass = MaterialAssetPipelineTestHelpers.CreatePass(parent);
        parent.PassEntities[0].AddTag<Opaque>();
        secondaryPass.AddComponent(new OverlayShade { Layer = 4 });
        MaterialAssetPipelineTestHelpers.SetPassEntities(parent, parent.PassEntities[0], secondaryPass);
        parent.Params.SetScalar("Roughness", 0.2f);

        MaterialInstanceAsset instanceAsset = new()
        {
            AssetGuid = instanceGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            ScalarOverrides =
            [
                new ScalarOverride
                {
                    Name = "Roughness",
                    Value = new ParamValue(new FloatVal { V = 0.7f }),
                },
            ],
        };

        Material instance = MaterialInstanceLoader.LoadFromAsset(
            instanceAsset,
            materialStore,
            guid => guid == parentGuid ? parent : null,
            textureLoader: null);

        Assert.Equal(2, instance.PassEntities.Length);
        Assert.True(instance.PassEntities[0].Tags.Has<Opaque>());
        Assert.True(instance.PassEntities[1].TryGetComponent<OverlayShade>(out OverlayShade overlay));
        Assert.Equal((byte)4, overlay.Layer);
        Assert.Equal(0.7f, instance.Params.GetScalar("Roughness"));
    }

    [Fact]
    public void ProviderDependencies_IncludeParentAndTextureOverrides()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "instance.materialinstance.asset");
        var parentGuid = AssetGuid.New();
        var textureGuid = AssetGuid.New();

        try
        {
            MaterialInstanceAssetSerializer.Save(new MaterialInstanceAsset
            {
                AssetGuid = AssetGuid.New().ToFlatString(),
                ParentGuid = parentGuid.ToFlatString(),
                Overrides =
                [
                    new ParamOverride { Name = "AlbedoMap", TextureGuid = textureGuid.ToFlatString() },
                ],
            }, path);

            MaterialInstanceAssetProvider provider = new();
            IReadOnlyList<AssetGuid> dependencies = provider.GetDependencies(path);

            Assert.Contains(parentGuid, dependencies);
            Assert.Contains(textureGuid, dependencies);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class MeshRegionMetadataTests
{
    [Fact]
    public void MeshAsset_Roundtrip_UsesExplicitRegions_InsteadOfDefaultMaterialReferences()
    {
        var mesh = new MeshAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mesh",
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Attributes = [],
            Regions =
            [
                new MeshRegion { Name = "body" },
                new MeshRegion { Name = "eyes" },
            ],
        };

        int maxSize = MeshAsset.Serializer.GetMaxSize(mesh);
        byte[] buffer = new byte[maxSize];
        int written = MeshAsset.Serializer.Write(buffer, mesh);
        MeshAsset parsed = MeshAsset.Serializer.Parse(buffer.AsSpan(0, written).ToArray());

        Assert.NotNull(parsed.Regions);
        Assert.Equal(2, parsed.Regions.Count);
        Assert.Equal("body", parsed.Regions[0].Name);
        Assert.Equal("eyes", parsed.Regions[1].Name);
    }
}

public class ShaderParamBagScalarTests
{
    private const byte Float32Scalar = 8;

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

    [Fact]
    public void MaterialScalarRegionLayout_PacksScalarsBySlangOffsets()
    {
        var material = new Material();
        material.SetScalarRegionLayout(MaterialScalarRegionLayout.FromFields(
            [
                new MaterialScalarFieldLayout("BaseColorTint", 0, 16, 1, 4, Float32Scalar),
                new MaterialScalarFieldLayout("MetallicFactor", 16, 4, 1, 1, Float32Scalar),
                new MaterialScalarFieldLayout("Roughness", 20, 4, 1, 1, Float32Scalar),
                new MaterialScalarFieldLayout("EmissiveFactor", 24, 12, 1, 3, Float32Scalar),
            ],
            payloadByteSize: 36));
        material.Params.SetScalar("BaseColorTint", new Vector4(0.2f, 0.3f, 0.4f, 0.5f));
        material.Params.SetScalar("MetallicFactor", 0.75f);
        material.Params.SetScalar("Roughness", 0.35f);
        material.Params.SetScalar("EmissiveFactor", new Vector4(0.1f, 0.2f, 0.3f, 0.0f));

        byte[] region = new byte[material.ScalarRegionByteSize];
        material.WriteScalarRegion(region);

        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(0, sizeof(uint))));
        Assert.Equal(0.2f, ReadFloat(region, 16));
        Assert.Equal(0.3f, ReadFloat(region, 20));
        Assert.Equal(0.75f, ReadFloat(region, 32));
        Assert.Equal(0.35f, ReadFloat(region, 36));
        Assert.Equal(0.1f, ReadFloat(region, 40));
        Assert.Equal(0.2f, ReadFloat(region, 44));
        Assert.Equal(0.3f, ReadFloat(region, 48));
    }

    [Fact]
    public void MaterialScalarRegionLayout_RejectsNonZeroReflectedBaseOffset()
    {
        var layout = new ShaderMaterialScalarLayout
        {
            Name = "OffsetScalars",
            Size = 64,
            Fields =
            [
                new ShaderMaterialScalarField { Name = "BaseColorTint", Offset = 16, Size = 16, RowCount = 1, ColumnCount = 4, ScalarType = Float32Scalar },
                new ShaderMaterialScalarField { Name = "MetallicFactor", Offset = 32, Size = 4, RowCount = 1, ColumnCount = 1, ScalarType = Float32Scalar },
                new ShaderMaterialScalarField { Name = "Roughness", Offset = 36, Size = 4, RowCount = 1, ColumnCount = 1, ScalarType = Float32Scalar },
                new ShaderMaterialScalarField { Name = "EmissiveFactor", Offset = 48, Size = 12, RowCount = 1, ColumnCount = 3, ScalarType = Float32Scalar },
            ],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MaterialScalarRegionLayout.FromShaderLayout(layout));
        Assert.Contains("non-zero payload base offset", ex.Message);
    }

    [Fact]
    public void MaterialAssetLoader_BuildsScalarRegionLayoutFromShaderMetadata()
    {
        var store = new EntityStore();
        var shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "scalar_shader");
        shader.Metadata!.MaterialScalarLayouts =
        [
            new ShaderMaterialScalarLayout
            {
                Name = "TestScalars",
                Size = 36,
                Fields =
                [
                    new ShaderMaterialScalarField { Name = "BaseColorTint", Offset = 0, Size = 16, RowCount = 1, ColumnCount = 4, ScalarType = Float32Scalar },
                    new ShaderMaterialScalarField { Name = "MetallicFactor", Offset = 16, Size = 4, RowCount = 1, ColumnCount = 1, ScalarType = Float32Scalar },
                    new ShaderMaterialScalarField { Name = "Roughness", Offset = 20, Size = 4, RowCount = 1, ColumnCount = 1, ScalarType = Float32Scalar },
                    new ShaderMaterialScalarField { Name = "EmissiveFactor", Offset = 24, Size = 12, RowCount = 1, ColumnCount = 3, ScalarType = Float32Scalar },
                ],
            },
        ];

        var asset = new MaterialAsset
        {
            Name = "ScalarRegionMat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString() }],
            Textures = [],
            Scalars =
            [
                new ScalarParam { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.5f }) },
                new ScalarParam { Name = "BaseColorTint", Value = new ParamValue(new Vec4Val { X = 1, Y = 1, Z = 1, W = 1 }) },
                new ScalarParam { Name = "MetallicFactor", Value = new ParamValue(new FloatVal { V = 0.25f }) },
            ],
        };

        Material material = MaterialAssetLoader.LoadFromAsset(
            asset,
            store,
            shaderLoader: guid => guid == shaderGuid ? shader : null);

        Assert.Equal(
            ["BaseColorTint", "MetallicFactor", "Roughness", "EmissiveFactor"],
            material.ScalarRegionLayout.Fields.Select(static field => field.Name));
        Assert.Equal(36u, material.ScalarRegionLayout.PayloadByteSize);
    }

    [Fact]
    public void MaterialAssetLoader_RejectsScalarsWithoutShaderMetadataLayout()
    {
        var store = new EntityStore();
        var shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "shader_without_scalars");

        var asset = new MaterialAsset
        {
            Name = "ScalarRegionMat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString() }],
            Scalars =
            [
                new ScalarParam { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.5f }) },
            ],
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            MaterialAssetLoader.LoadFromAsset(
                asset,
                store,
                shaderLoader: guid => guid == shaderGuid ? shader : null));

        Assert.Contains("shader material scalar layout", ex.Message);
    }

    [Fact]
    public void ShaderParamBag_AppliesDeterministicFallbacksForMissingMaterialResources()
    {
        var bag = new ShaderParamBag();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "fallback_shader");
        shader.Metadata!.MaterialBindings =
        [
            new ShaderMaterialBinding { Name = "AlbedoMap", ResourceType = 0 },
            new ShaderMaterialBinding { Name = "NormalMap", ResourceType = 0 },
            new ShaderMaterialBinding { Name = "ExtraMaterialData", ResourceType = 2 },
            new ShaderMaterialBinding { Name = "MaterialSampler", ResourceType = 1 },
        ];

        var fallbacks = new MaterialResourceFallbacks
        {
            WhiteTexture = UninitializedDiligentObject<ITextureView>(),
            FlatNormalTexture = UninitializedDiligentObject<ITextureView>(),
            DefaultBufferView = UninitializedDiligentObject<IBufferView>(),
            DefaultSampler = UninitializedDiligentObject<ISampler>(),
        };
        List<(string Name, ShaderResourceType Type)> applied = [];

        int count = bag.ApplyFallbacks(shader, fallbacks, (name, type) => applied.Add((name, type)));

        Assert.Equal(4, count);
        Assert.Contains(("AlbedoMap", ShaderResourceType.TextureSrv), applied);
        Assert.Contains(("NormalMap", ShaderResourceType.TextureSrv), applied);
        Assert.Contains(("ExtraMaterialData", ShaderResourceType.BufferSrv), applied);
        Assert.Contains(("MaterialSampler", ShaderResourceType.Sampler), applied);
        Assert.Contains(bag.EnumerateResources(), static resource =>
            resource is { Name: "AlbedoMap", Type: ShaderResourceType.TextureSrv });
        Assert.Contains(bag.EnumerateResources(), static resource =>
            resource is { Name: "NormalMap", Type: ShaderResourceType.TextureSrv });
        Assert.Contains(bag.EnumerateResources(), static resource =>
            resource is { Name: "ExtraMaterialData", Type: ShaderResourceType.BufferSrv });
        Assert.Contains(bag.EnumerateResources(), static resource =>
            resource is { Name: "MaterialSampler", Type: ShaderResourceType.Sampler });
    }

    private static float ReadFloat(byte[] data, int byteOffset)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(byteOffset, sizeof(uint))));

    private static T UninitializedDiligentObject<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}

internal static class MaterialAssetPipelineTestHelpers
{
    internal static Material CreateMaterial(EntityStore materialStore, string name, AssetGuid guid)
    {
        var material = new Material
        {
            Name = name,
            AssetGuid = guid,
            PassStore = materialStore,
        };

        Entity passEntity = materialStore.CreateEntity();
        passEntity.AddComponent(new MaterialRef { Owner = material });
        material.PassEntities = [passEntity];
        return material;
    }

    internal static Entity CreatePass(Material material)
    {
        EntityStore store = material.PassStore ?? throw new InvalidOperationException("Material is not attached to a pass store.");
        Entity entity = store.CreateEntity();
        entity.AddComponent(new MaterialRef { Owner = material });
        return entity;
    }

    internal static void SetPassEntities(Material material, params Entity[] passEntities)
    {
        material.PassEntities = passEntities;
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
                MaterialScalarLayouts = [],
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
