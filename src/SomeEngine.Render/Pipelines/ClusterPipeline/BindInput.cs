using SomeEngine.Render.Materials;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal static class BindInput
{
    internal static BindingSetPlan[] CreatePlan(ShaderBindingTable layout)
    {
        if (layout.SetCount == 0)
            return [];

        var sets = new List<BindingSetPlan>(layout.SetCount);
        for (int setIndex = 0; setIndex < layout.SetCount; setIndex++)
        {
            ReflectedBinding[] bindings = layout.GetSet(setIndex);
            if (bindings.Length == 0)
                continue;

            var slots = new List<BindingSlotPlan>();
            for (int start = 0; start < bindings.Length;)
            {
                ReflectedBinding binding = bindings[start];
                int end = start + 1;
                while (end < bindings.Length && SameSlot(binding, bindings[end]))
                    end++;

                ReflectedBinding[] grouped = bindings[start..end].ToArray();
                slots.Add(new BindingSlotPlan(grouped));
                start = end;
            }

            sets.Add(new BindingSetPlan(
                checked((uint)setIndex),
                layout.Layout(checked((uint)setIndex)),
                bindings.Length,
                [.. slots]));
        }

        return [.. sets];
    }

    public static void FillAll(
        ShaderBindingTable layout,
        RenderGraphContext context,
        BindingSource common,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks,
        IParameterSink sink,
        string owner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (layout.SetCount == 0)
            throw new InvalidOperationException($"{owner} state has no reflected binding layouts.");

        for (int set = 0; set < layout.SetCount; set++)
        {
            ReflectedBinding[] bindings = layout.GetSet(set);
            if (bindings.Length == 0)
                continue;

            uint setIndex = checked((uint)set);
            PassBindings resources = context.Bindings(layout.Layout(setIndex)).Reserve(bindings.Length);
            Fill(
                bindings,
                common,
                material,
                fallbacks,
                ref resources,
                owner);
            if (resources.Count != 0)
                sink.SetParameters(setIndex, resources);
        }
    }

    public static void Fill(
        ReadOnlySpan<ReflectedBinding> bindings,
        BindingSource common,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks,
        ref PassBindings resources,
        string owner)
    {
        ArgumentNullException.ThrowIfNull(common);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        for (int start = 0; start < bindings.Length;)
        {
            ReflectedBinding binding = bindings[start];
            int end = start + 1;
            while (end < bindings.Length && SameSlot(binding, bindings[end]))
                end++;

            if (Resolve(bindings[start..end], common, material, fallbacks, out BindingResourceDesc resource))
            {
                resources = resources.Resource(resource);
                start = end;
                continue;
            }

            throw new InvalidOperationException(
                $"{owner} could not bind reflected resource slot set {binding.Set} binding {binding.Binding}: {Names(bindings[start..end])}.");
        }
    }

    internal static void AddResource(
        RenderGraphContext context,
        ShaderBindingTable layout,
        ref PassBindings resources,
        ref uint? set,
        ReflectedBinding binding,
        BindingResourceDesc resource,
        RenderGraphAccess access,
        string owner,
        int reserveCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (set.HasValue && set.Value != binding.Set)
        {
            throw new InvalidOperationException(
                $"{owner} requires reflected resources in one binding set; '{binding.Name}' is set {binding.Set}, expected {set.Value}.");
        }

        if (!set.HasValue)
        {
            set = binding.Set;
            resources = context.Bindings(layout.Layout(binding.Set)).Reserve(reserveCount);
        }

        ReadOnlySpan<BindingResourceDesc> existingResources = resources.ResourceSpan;
        ReadOnlySpan<RenderGraphAccess> existingAccesses = resources.ResourceAccessSpan;
        for (int i = 0; i < existingResources.Length; i++)
        {
            BindingResourceDesc existing = existingResources[i];
            if (existing.Binding != binding.Binding || existing.ResourceType != binding.Type)
                continue;

            RenderGraphAccess existingAccess = i < existingAccesses.Length
                ? existingAccesses[i]
                : PassBindings.BindingAccess(existing.ResourceType);
            if (existing.Equals(resource) && existingAccess == access)
                return;

            throw new InvalidOperationException(
                $"{owner} reflected duplicate binding set {binding.Set} binding {binding.Binding} with incompatible resources or access override.");
        }

        resources = resources.Resource(resource, access);
    }

    internal static uint GetSetIndex(string owner, params ReflectedBinding[] bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (bindings.Length == 0)
            throw new ArgumentException("At least one binding is required.", nameof(bindings));

        uint set = bindings[0].Set;
        for (int i = 1; i < bindings.Length; i++)
        {
            if (bindings[i].Set != set)
            {
                throw new InvalidOperationException(
                    $"{owner} requires reflected resources in one binding set; '{bindings[0].Name}' is set {set}, '{bindings[i].Name}' is set {bindings[i].Set}.");
            }
        }

        return set;
    }

    internal static bool Resolve(
        ReadOnlySpan<ReflectedBinding> bindings,
        BindingSource common,
        MaterialBindings material,
        MaterialResourceFallbacks? fallbacks,
        out BindingResourceDesc resource)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            if (common(bindings[i], out resource))
                return true;
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            if (material.TryGet(bindings[i], out resource))
                return true;
        }

        if (fallbacks != null)
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                if (TryFallback(bindings[i], fallbacks, out resource))
                    return true;
            }
        }

        resource = default;
        return false;
    }

    internal static bool TryFallback(
        ReflectedBinding binding,
        MaterialResourceFallbacks fallbacks,
        out BindingResourceDesc resource)
    {
        resource = default;
        switch (binding.Type)
        {
            case BindingType.TextureRead:
            {
                TextureViewHandle view = fallbacks.ResolveTexture(binding.Name);
                if (!view.IsValid)
                    return false;
                resource = binding.Texture(view);
                return true;
            }
            case BindingType.Sampler:
                if (!fallbacks.DefaultSampler.IsValid)
                    return false;
                resource = binding.Sampler(fallbacks.DefaultSampler);
                return true;
            case BindingType.StorageBufferRead:
                if (!fallbacks.DefaultBufferView.IsValid)
                    return false;
                resource = binding.Buffer(fallbacks.DefaultBufferView);
                return true;
            case BindingType.RawBufferRead:
                if (!fallbacks.DefaultRawView.IsValid)
                    return false;
                resource = binding.Buffer(fallbacks.DefaultRawView);
                return true;
            case BindingType.ConstantBuffer:
                if (!fallbacks.DefaultConstantBufferView.IsValid)
                    return false;
                resource = binding.Buffer(fallbacks.DefaultConstantBufferView);
                return true;
            default:
                return false;
        }
    }

    private static bool SameSlot(ReflectedBinding left, ReflectedBinding right)
        => left.Binding == right.Binding
            && left.Type == right.Type;

    private static string Names(ReadOnlySpan<ReflectedBinding> bindings)
    {
        if (bindings.Length == 0)
            return "<empty>";

        if (bindings.Length == 1)
            return bindings[0].Name;

        string[] names = new string[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            names[i] = bindings[i].Name;
        return string.Join(", ", names);
    }
}

internal delegate bool BindingSource(ReflectedBinding binding, out BindingResourceDesc resource);

internal readonly record struct BindingSetPlan(
    uint SetIndex,
    BindingLayoutHandle Layout,
    int BindingCount,
    BindingSlotPlan[] Slots);

internal readonly record struct BindingSlotPlan(
    ReflectedBinding[] Bindings);
