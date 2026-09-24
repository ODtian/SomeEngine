using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

internal static class BindingHash
{
    public static HashCode Begin(BindingLayoutHandle layout)
    {
        var hash = new HashCode();
        hash.Add(layout);
        return hash;
    }

    public static int Finish(HashCode hash, int count)
    {
        hash.Add(count);
        return hash.ToHashCode();
    }

    public static int Compute(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        HashCode hash = Begin(layout);
        for (int i = 0; i < resources.Length; i++)
            hash.Add(resources[i]);
        return Finish(hash, resources.Length);
    }

    public static bool Equal(BindingResourceDesc[] left, ReadOnlySpan<BindingResourceDesc> right)
        => left.AsSpan().SequenceEqual(right);
}
