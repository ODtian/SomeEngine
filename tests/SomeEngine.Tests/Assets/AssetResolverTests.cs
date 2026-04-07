using System;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Assets;

public class LoaderDelegateIntegrationTests
{
    [Fact]
    public void MaterialAssetLoader_CanUseGuidDelegate()
    {
        MaterialSystem materialSystem = new();
        AssetGuid shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "ShaderA", ("ClusterShade", Array.Empty<string>(), "CSMain"));

        MaterialAsset materialAsset = new()
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString(), Tags = [new TagEntry { Name = "opaque" }] }],
        };

        Material material = MaterialAssetLoader.LoadFromAsset(materialAsset, materialSystem, textureLoader: null, shaderLoader: guid => guid == shaderGuid ? shader : null);

        Assert.Equal(AssetGuid.Parse(materialAsset.AssetGuid!), material.AssetGuid);
        Assert.Equal("CSMain", material.Entity.GetComponent<ClusterShadeComponent>().Default.EntryPoint);
        Assert.True(material.Entity.Tags.Has<Opaque>());
    }

    [Fact]
    public void MaterialInstanceLoader_CanUseParentDelegate()
    {
        MaterialSystem materialSystem = new();
        Material parent = MaterialAssetPipelineTestHelpers.CreateMaterial(materialSystem, "Parent", AssetGuid.New());
        parent.Entity.AddTag<Opaque>();

        MaterialInstanceAsset instanceAsset = new()
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            ParentGuid = parent.AssetGuid.ToFlatString(),
            TagOverrides = [new TagOverride { Name = "masked", Remove = false }],
        };

        Material instance = MaterialInstanceLoader.LoadFromAsset(instanceAsset, materialSystem, guid => guid == parent.AssetGuid ? parent : null, textureLoader: null);

        Assert.Equal(AssetGuid.Parse(instanceAsset.AssetGuid!), instance.AssetGuid);
        Assert.True(instance.Entity.Tags.Has<Masked>());
    }

    [Fact]
    public void MeshMaterialResolver_CanUseGuidDelegate()
    {
        Material material = new() { Name = "Mat", AssetGuid = AssetGuid.New() };
        MeshAsset mesh = new()
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mesh",
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Attributes = [],
            DefaultMaterialGuids = [material.AssetGuid.ToFlatString()],
        };

        Material?[] resolved = MeshMaterialResolver.Resolve(mesh, guid => guid == material.AssetGuid ? material : null);

        Assert.Single(resolved);
        Assert.Same(material, resolved[0]);
    }
}
