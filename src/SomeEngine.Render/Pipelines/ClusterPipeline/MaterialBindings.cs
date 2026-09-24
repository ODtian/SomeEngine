using SomeEngine.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal readonly struct MaterialBindings
{
    public static readonly MaterialBindings Empty = new([]);

    private readonly Entry[]? _entries;

    private MaterialBindings(Entry[] entries)
    {
        _entries = entries;
    }

    public bool TryGet(ReflectedBinding binding, out BindingResourceDesc resource)
    {
        Entry[]? entries = _entries;
        if (entries == null)
        {
            resource = default;
            return false;
        }

        int lo = 0;
        int hi = entries.Length - 1;
        while (lo <= hi)
        {
            int index = lo + ((hi - lo) / 2);
            Entry entry = entries[index];
            int compare = Compare(entry, binding);
            if (compare == 0)
            {
                resource = entry.Resource;
                return true;
            }

            if (compare < 0)
                lo = index + 1;
            else
                hi = index - 1;
        }

        resource = default;
        return false;
    }

    public static MaterialBindings Create(
        ShaderBindingTable layout,
        AssetStore assets,
        Material? material)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (material == null)
            return Empty;

        var entries = new List<Entry>();
        for (int set = 0; set < layout.SetCount; set++)
            Add(entries, layout.GetSet(set), assets, material);

        return Create(entries);
    }

    public static MaterialBindings Create(
        ReadOnlySpan<ReflectedBinding> bindings,
        AssetStore assets,
        Material? material)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (material == null)
            return Empty;

        var entries = new List<Entry>();
        Add(entries, bindings, assets, material);
        return Create(entries);
    }

    private static void Add(
        List<Entry> entries,
        ReadOnlySpan<ReflectedBinding> bindings,
        AssetStore assets,
        Material material)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            ReflectedBinding binding = bindings[i];
            if (material.ToBindSet(binding, assets, out BindingResourceDesc resource))
            {
                entries.Add(new Entry(
                    binding.Set,
                    binding.Binding,
                    binding.Type,
                    resource));
            }
        }
    }

    private static MaterialBindings Create(List<Entry> entries)
    {
        if (entries.Count == 0)
            return Empty;

        Entry[] sorted = entries.ToArray();
        Array.Sort(sorted, Compare);
        return new MaterialBindings(sorted);
    }

    private static int Compare(Entry left, Entry right)
    {
        int set = left.Set.CompareTo(right.Set);
        if (set != 0)
            return set;

        int binding = left.Binding.CompareTo(right.Binding);
        return binding != 0 ? binding : left.Type.CompareTo(right.Type);
    }

    private static int Compare(Entry entry, ReflectedBinding binding)
    {
        int set = entry.Set.CompareTo(binding.Set);
        if (set != 0)
            return set;

        int slot = entry.Binding.CompareTo(binding.Binding);
        return slot != 0 ? slot : entry.Type.CompareTo(binding.Type);
    }

    private readonly record struct Entry(
        uint Set,
        uint Binding,
        BindingType Type,
        BindingResourceDesc Resource);
}
