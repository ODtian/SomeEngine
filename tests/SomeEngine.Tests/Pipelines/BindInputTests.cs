using SomeEngine.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Tests.Pipelines;

public class BindInputTests
{
    [Fact]
    public void Fill_UsesCommonMaterialThenFallbacks()
    {
        var material = new Material();
        TextureViewHandle materialView = new(7, 1);
        using var assets = new AssetStore();
        var texture = assets.Add(AssetGuid.New(), new Texture { View = materialView });
        material.AlbedoMap = texture;
        MaterialResourceFallbacks fallbacks = Fallbacks();
        ReflectedBinding[] table = Table();
        MaterialBindings bindings = MaterialBindings.Create(table, assets, material);

        BindingResourceDesc[] resources = Collect(table, Common, bindings, fallbacks);

        Assert.Collection(
            resources,
            resource => Assert.Equal(new BufferViewHandle(2, 1), resource.BufferView),
            resource => Assert.Equal(materialView, resource.TextureView),
            resource => Assert.Equal(fallbacks.DefaultSampler, resource.SamplerHandle));
    }

    [Fact]
    public void Fill_UsesFallback_WhenMaterialMissing()
    {
        MaterialResourceFallbacks fallbacks = Fallbacks();
        ReflectedBinding[] table = Table();

        BindingResourceDesc[] resources = Collect(table, Common, MaterialBindings.Empty, fallbacks);

        Assert.Collection(
            resources,
            resource => Assert.Equal(new BufferViewHandle(2, 1), resource.BufferView),
            resource => Assert.Equal(fallbacks.WhiteTexture, resource.TextureView),
            resource => Assert.Equal(fallbacks.DefaultSampler, resource.SamplerHandle));
    }

    [Fact]
    public void Fill_SelectsBufferFallbackByType()
    {
        MaterialResourceFallbacks fallbacks = Fallbacks();
        ReflectedBinding[] table =
        [
            new ReflectedBinding("Structured", 0, 0, BindingType.StorageBufferRead, ShaderStageFlags.Compute),
            new ReflectedBinding("Raw", 0, 1, BindingType.RawBufferRead, ShaderStageFlags.Compute),
        ];

        BindingResourceDesc[] resources = Collect(table, None, MaterialBindings.Empty, fallbacks);

        Assert.Collection(
            resources,
            resource => Assert.Equal(fallbacks.DefaultBufferView, resource.BufferView),
            resource => Assert.Equal(fallbacks.DefaultRawView, resource.BufferView));
    }

    private static BindingResourceDesc[] Collect(
        ReadOnlySpan<ReflectedBinding> table,
        BindingSource common,
        MaterialBindings bindings,
        MaterialResourceFallbacks fallbacks)
    {
        var resources = new List<BindingResourceDesc>();
        for (int i = 0; i < table.Length; i++)
        {
            Assert.True(BindInput.Resolve(table[i..(i + 1)], common, bindings, fallbacks, out BindingResourceDesc resource));
            resources.Add(resource);
        }

        return resources.ToArray();
    }

    private static bool Common(ReflectedBinding binding, out BindingResourceDesc resource)
    {
        if (binding.Name == "Uniforms")
        {
            resource = binding.Buffer(new BufferViewHandle(2, 1));
            return true;
        }

        resource = default;
        return false;
    }

    private static bool None(ReflectedBinding binding, out BindingResourceDesc resource)
    {
        resource = default;
        return false;
    }

    private static ReflectedBinding[] Table()
        =>
        [
            new ReflectedBinding("Uniforms", 0, 0, BindingType.ConstantBuffer, ShaderStageFlags.Compute),
            new ReflectedBinding("AlbedoMap", 0, 1, BindingType.TextureRead, ShaderStageFlags.Compute),
            new ReflectedBinding("LinearSampler", 0, 2, BindingType.Sampler, ShaderStageFlags.Compute),
        ];

    private static MaterialResourceFallbacks Fallbacks()
        => new()
        {
            WhiteTexture = new TextureViewHandle(3, 1),
            BlackTexture = new TextureViewHandle(4, 1),
            FlatNormalTexture = new TextureViewHandle(5, 1),
            DefaultBufferView = new BufferViewHandle(6, 1),
            DefaultRawView = new BufferViewHandle(7, 1),
            DefaultConstantBufferView = new BufferViewHandle(8, 1),
            DefaultSampler = new SamplerHandle(9, 1),
        };
}
