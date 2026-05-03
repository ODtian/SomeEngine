using System;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Assets;

public class LoaderDelegateIntegrationTests
{
    [Fact]
    public void MaterialAssetLoader_CanUseGuidDelegate()
    {
        EntityStore materialStore = new();
        AssetGuid shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(shaderGuid, "ShaderA", ("ClusterShade", Array.Empty<string>(), "CSMain"));

        MaterialAsset materialAsset = new()
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "Mat",
            Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString(), Tags = [new TagEntry { Name = "opaque" }] }],
        };

        Material material = MaterialAssetLoader.LoadFromAsset(materialAsset, materialStore, textureLoader: null, shaderLoader: guid => guid == shaderGuid ? shader : null);

        Assert.Equal(AssetGuid.Parse(materialAsset.AssetGuid!), material.AssetGuid);
        Assert.Equal(1, material.PassEntities.Length);
        Assert.True(material.PassEntities[0].Tags.Has<Opaque>());
        Assert.True(material.PassEntities[0].TryGetComponent<ClusterShadeComponent>(out _));
    }

    [Fact]
    public void MaterialInstanceLoader_CanUseParentDelegate()
    {
        EntityStore materialStore = new();
        Material parent = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "Parent", AssetGuid.New());
        parent.PassEntities[0].AddTag<Opaque>();

        MaterialInstanceAsset instanceAsset = new()
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            ParentGuid = parent.AssetGuid.ToFlatString(),
        };

        Material instance = MaterialInstanceLoader.LoadFromAsset(instanceAsset, materialStore, guid => guid == parent.AssetGuid ? parent : null, textureLoader: null);

        Assert.Equal(AssetGuid.Parse(instanceAsset.AssetGuid!), instance.AssetGuid);
        Assert.True(instance.PassEntities[0].Tags.Has<Opaque>());
    }

    [Fact]
    public void MeshMaterialBindings_Component_StoresMaterialGuidTable()
    {
        AssetGuid materialGuid = AssetGuid.New();
        MeshMaterialBindings bindings = new() { MaterialAssetGuids = [materialGuid] };

        Assert.Equal(materialGuid, bindings.MaterialAssetGuids[0]);
    }
}
