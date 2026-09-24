using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;

namespace SomeEngine.Tests.Materials;

public class MaterialAssetPipelineTests
{
    private const string ClusterShade = "cluster.shade";
    private const string ClusterRasterVs = "cluster.raster.vs";
    private const string ClusterRasterPs = "cluster.raster.ps";
    private const string ClusterDeform = "cluster.deform.eval";

    [Fact]
    public void LoadFromAsset_BuildsRuntimePasses()
    {
        using var store = new AssetStore();
        AssetGuid shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialTestAssets.Shader(
            shaderGuid,
            "cluster_shader",
            ("MaterialTarget", [ClusterShade], "Shade"),
            ("MaterialTarget", [ClusterRasterVs], "Vs"),
            ("MaterialTarget", [ClusterRasterPs], "Ps"),
            ("MaterialTarget", [ClusterDeform], "Deform"));
        Handle<Shader> shaderHandle = store.Add(shaderGuid, RuntimeAssetLoader.LoadShader(shader));
        AssetGuid materialGuid = AssetGuid.New();
        MaterialAsset asset = new()
        {
            AssetGuid = materialGuid.ToFlatString(),
            Name = "RuntimeMat",
            Passes =
            [
                new PassEntry
                {
                    ShaderGuid = shaderGuid.ToFlatString(),
                    Tags = [new TagEntry { Name = "masked" }, new TagEntry { Name = "twosided" }],
                    Components =
                    [
                        new ComponentEntry { TypeName = "ClusterDeform", Json = "{\"BoundsExpansion\":2.5}" },
                        new ComponentEntry { TypeName = "StencilState", Json = "{\"Ref\":3,\"Compare\":\"LessOrEqual\",\"PassOp\":\"Replace\"}" },
                    ],
                },
            ],
        };

        Material material = MaterialAssetLoader.LoadFromAsset(
            asset,
            store,
            shaderLoader: guid => guid == shaderGuid ? shaderHandle : default);

        Assert.Equal("RuntimeMat", material.Name);
        Assert.All(material.Passes, pass => Assert.Equal(shaderHandle, pass.Shader));
        Assert.Contains(material.Passes, pass => pass.Target == ClusterShade && pass.EntryPoint == "Shade");
        Assert.Contains(material.Passes, pass => pass.Target == ClusterRasterVs && pass.EntryPoint == "Vs");
        Assert.Contains(material.Passes, pass => pass.Target == ClusterRasterPs && pass.EntryPoint == "Ps");
        Assert.Contains(material.Passes, pass => pass.Target == ClusterDeform && pass.EntryPoint == "Deform");
        Assert.All(material.Passes, pass =>
        {
            Assert.Equal(SurfaceMode.Masked, pass.State.Surface);
            Assert.True(pass.State.TwoSided);
            Assert.Equal(2.5f, pass.State.BoundsExpansion);
            Assert.Equal(3, pass.State.StencilRef);
            Assert.Equal(CompareOp.LessOrEqual, pass.State.StencilCompare);
            Assert.Equal(SomeEngine.Render.Materials.StencilOp.Replace, pass.State.StencilPass);
        });
    }

    [Fact]
    public void LoadFromAsset_KeepsGuidOut()
    {
        using var store = new AssetStore();
        AssetGuid materialGuid = AssetGuid.New();

        Material material = MaterialAssetLoader.LoadFromAsset(
            new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "RuntimeOnly",
            },
            store);

        Assert.Equal("RuntimeOnly", material.Name);
        Assert.DoesNotContain(
            typeof(Material).GetMembers(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic),
            member => member.Name.Contains("Guid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadFromAsset_UsesHandleStore_ForShaders()
    {
        using var store = new AssetStore();
        AssetGuid shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialTestAssets.Shader(
            shaderGuid,
            "shade_shader",
            ("MaterialTarget", [ClusterShade], "Shade"));
        Handle<Shader> shaderHandle = store.Add(shaderGuid, RuntimeAssetLoader.LoadShader(shader));

        Material material = MaterialAssetLoader.LoadFromAsset(
            new MaterialAsset
            {
                Name = "HandleMat",
                Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString() }],
            },
            store,
            shaderLoader: guid => guid == shaderGuid ? shaderHandle : default);

        MaterialPass pass = Assert.Single(material.Passes);
        Assert.Equal(ClusterShade, pass.Target);
        Assert.Equal(shaderHandle, pass.Shader);
    }

    [Fact]
    public void LoadFromAsset_KeepsRendererTarget()
    {
        using var store = new AssetStore();
        AssetGuid shaderGuid = AssetGuid.New();
        ShaderAsset shader = MaterialTestAssets.Shader(
            shaderGuid,
            "forward_shader",
            ("MaterialTarget", ["forward.color"], "Forward"));
        Handle<Shader> shaderHandle = store.Add(shaderGuid, RuntimeAssetLoader.LoadShader(shader));

        Material material = MaterialAssetLoader.LoadFromAsset(
            new MaterialAsset
            {
                Name = "ForwardMat",
                Passes = [new PassEntry { ShaderGuid = shaderGuid.ToFlatString() }],
            },
            store,
            shaderLoader: guid => guid == shaderGuid ? shaderHandle : default);

        MaterialPass pass = Assert.Single(material.Passes);
        Assert.Equal("forward.color", pass.Target);
        Assert.Equal("Forward", pass.EntryPoint);
    }

    [Fact]
    public void InstanceLoader_ClonesParentAsset()
    {
        AssetGuid parentGuid = AssetGuid.New();
        Material parent = new()
        {
            Name = "Parent",
        };
        parent.SetPasses(
        [
            new MaterialPass(ClusterShade, new Handle<Shader>(4, 1), "Shade", MaterialState.Default),
        ]);
        using var textures = new AssetStore();
        parent.AlbedoMap = textures.Add(AssetGuid.New(), new Texture { View = new TextureViewHandle(1, 1) });

        TextureViewHandle overrideView = new(2, 1);
        Handle<Texture> overrideTexture = textures.Add(AssetGuid.New(), new Texture { View = overrideView });
        Material instance = MaterialInstanceLoader.LoadFromAsset(
            new MaterialInstanceAsset
            {
                ParentGuid = parentGuid.ToFlatString(),
                Overrides =
                [
                    new ParamOverride { Name = "AlbedoMap", TextureGuid = AssetGuid.New().ToFlatString() },
                ],
            },
            guid => guid == parentGuid ? parent : null,
            _ => overrideTexture);

        Assert.NotSame(parent, instance);
        Assert.Equal(parent.Passes, instance.Passes);
        ReflectedBinding binding = new(
            "AlbedoMap",
            Set: 0,
            Binding: 0,
            BindingType.TextureRead,
            ShaderStageFlags.Compute);
        Assert.True(instance.ToBindSet(binding, textures, out BindingResourceDesc resource));
        Assert.Equal(overrideView, resource.TextureView);
    }

}

internal static class MaterialTestAssets
{
    public static ShaderAsset Shader(
        AssetGuid guid,
        string name,
        params (string Attribute, IReadOnlyList<string> Args, string Entry)[] entries)
    {
        List<ShaderBytecode> variants = [];
        List<ShaderEntryPointAttribute> attributes = [];
        for (int i = 0; i < entries.Length; i++)
        {
            variants.Add(new ShaderBytecode
            {
                Backend = "dxil",
                Stage = AssetShaderStage.Compute,
                EntryPoint = entries[i].Entry,
                Data = Array.Empty<byte>(),
                ContentHash = $"{name}-{i}",
            });
            attributes.Add(new ShaderEntryPointAttribute
            {
                VariantIndex = i,
                Name = entries[i].Attribute,
                Args = entries[i].Args.Count == 0 ? [] : [.. entries[i].Args],
            });
        }

        return new ShaderAsset
        {
            AssetGuid = guid.ToFlatString(),
            Name = name,
            Variants = variants,
            EntryPointAttributes = attributes,
            Metadata = new ShaderMetadata
            {
                Tags = [],
                MaterialBindings = [],
                MaterialScalarLayouts = [],
            },
            Reflections = [],
        };
    }
}
