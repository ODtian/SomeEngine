using Diligent;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Pipelines;

public class ClusterShadeSig1Tests
{
    [Fact]
    public void ComputeSig1CacheKey_IgnoresScalarValues_And_InsertionOrder()
    {
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "Shared", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        var matA = CreateShadeMaterial(shader, "A");
        matA.Params.Set("AlbedoTex", (ITextureView?)null);
        matA.Params.Set("SamplerLinear", (ISampler?)null);
        matA.Params.SetScalar("Roughness", 0.25f);

        var matB = CreateShadeMaterial(shader, "B");
        matB.Params.SetScalar("Roughness", 0.75f);
        matB.Params.Set("SamplerLinear", (ISampler?)null);
        matB.Params.Set("AlbedoTex", (ITextureView?)null);

        var variantA = matA.Entity.GetComponent<ClusterShadeComponent>().Default;
        var variantB = matB.Entity.GetComponent<ClusterShadeComponent>().Default;

        Assert.Equal(
            ClusterShade.ComputeSig1CacheKey(matA.Entity, variantA),
            ClusterShade.ComputeSig1CacheKey(matB.Entity, variantB));
    }

    [Fact]
    public void ComputeSig1CacheKey_Differs_ForDifferentResourceLayout()
    {
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "Shared", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        var matA = CreateShadeMaterial(shader, "A");
        matA.Params.Set("Shared", (ITextureView?)null);

        var matB = CreateShadeMaterial(shader, "B");
        matB.Params.SetBuffer("Shared", null);

        Assert.NotEqual(
            ClusterShade.ComputeSig1CacheKey(matA.Entity, matA.Entity.GetComponent<ClusterShadeComponent>().Default),
            ClusterShade.ComputeSig1CacheKey(matB.Entity, matB.Entity.GetComponent<ClusterShadeComponent>().Default));
    }

    [Fact]
    public void BuildSig1Resources_AddsUniforms_First_And_SortsRemainingResources()
    {
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "Shared", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        var material = CreateShadeMaterial(shader, "M");
        material.Params.Set("ZTex", (ITextureView?)null);
        material.Params.SetBuffer("BufferA", null);
        material.Params.Set("ASampler", (ISampler?)null);

        var resources = ClusterShade.BuildSig1Resources(material.Entity, material.Entity.GetComponent<ClusterShadeComponent>().Default);

        Assert.Equal(4, resources.Length);
        Assert.Equal("Uniforms", resources[0].Name);
        Assert.Equal("ASampler", resources[1].Name);
        Assert.Equal("BufferA", resources[2].Name);
        Assert.Equal("ZTex", resources[3].Name);
    }

    [Fact]
    public void BuildSig1Resources_UsesShaderMetadataBindings_WhenPresent()
    {
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "MetaShader", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        shader.Metadata = new ShaderMetadata
        {
            MaterialBindings =
            [
                new ShaderMaterialBinding { Name = "NeededTex", ResourceType = 0 },
                new ShaderMaterialBinding { Name = "NeededSampler", ResourceType = 1 },
            ],
        };

        var material = CreateShadeMaterial(shader, "M");
        material.Params.Set("NeededTex", (ITextureView?)null);
        material.Params.Set("NeededSampler", (ISampler?)null);
        material.Params.SetBuffer("UnusedBuffer", null);

        var resources = ClusterShade.BuildSig1Resources(material.Entity, material.Entity.GetComponent<ClusterShadeComponent>().Default);

        Assert.Equal(new[] { "Uniforms", "NeededSampler", "NeededTex" }, resources.Select(resource => resource.Name).ToArray());
    }

    [Fact]
    public void ComputeSig1CacheKey_IgnoresUnusedRuntimeResources_WhenMetadataPresent()
    {
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "MetaShader", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        shader.Metadata = new ShaderMetadata
        {
            MaterialBindings = [new ShaderMaterialBinding { Name = "AlbedoMap", ResourceType = 0 }],
        };

        var matA = CreateShadeMaterial(shader, "A");
        matA.Params.Set("AlbedoMap", (ITextureView?)null);
        matA.Params.SetBuffer("UnusedBuffer", null);

        var matB = CreateShadeMaterial(shader, "B");
        matB.Params.Set("AlbedoMap", (ITextureView?)null);

        Assert.Equal(
            ClusterShade.ComputeSig1CacheKey(matA.Entity, matA.Entity.GetComponent<ClusterShadeComponent>().Default),
            ClusterShade.ComputeSig1CacheKey(matB.Entity, matB.Entity.GetComponent<ClusterShadeComponent>().Default));
    }

    private static Material CreateShadeMaterial(ShaderAsset shader, string name)
    {
        var materialSystem = new MaterialSystem();
        var material = MaterialAssetPipelineTestHelpers.CreateMaterial(materialSystem, name, AssetGuid.New());
        material.Entity.AddComponent(new ClusterShadeComponent { Default = new ShaderVariantRef(shader, "CSMain") });
        return material;
    }
}
