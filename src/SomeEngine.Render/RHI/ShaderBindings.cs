using SomeEngine.Core.Collections;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal readonly record struct ShaderBindingTable
{
    private readonly BindingLayoutHandle[] _layouts;
    private readonly ReflectedBinding[][] _sets;
    private readonly FlatDictionary<string, ReflectedBinding> _byName;

    public ShaderBindingTable(
        BindingLayoutHandle[] layouts,
        PipelineLayoutHandle pipelineLayout,
        ReflectedBinding[][] sets,
        FlatDictionary<string, ReflectedBinding> byName,
        ReflectedBinding[] bindings)
    {
        _layouts = layouts;
        PipelineLayout = pipelineLayout;
        _sets = sets;
        _byName = byName;
        Bindings = bindings;
        Key = BindingKey.From(bindings);
    }

    public PipelineLayoutHandle PipelineLayout { get; }
    public ReflectedBinding[] Bindings { get; }
    public BindingKey Key { get; }
    public int SetCount => _layouts?.Length ?? 0;
    public ReadOnlySpan<BindingLayoutHandle> Layouts => _layouts ?? [];

    public BindingLayoutHandle Layout(uint set)
    {
        if (_layouts == null || set >= _layouts.Length)
            throw new InvalidOperationException($"Shader binding set {set} is not reflected.");
        return _layouts[checked((int)set)];
    }

    public ReflectedBinding[] GetSet(int set)
    {
        if (_sets == null || (uint)set >= (uint)_sets.Length)
            return [];
        return _sets[set];
    }

    internal bool TryGet(string name, out ReflectedBinding binding)
    {
        if (_byName != null && _byName.TryGetValue(name, out binding))
            return true;

        binding = default;
        return false;
    }

    internal string Candidates()
    {
        if (_byName == null || _byName.Count == 0)
            return "none";
        return string.Join(", ", _byName.Select(static pair => pair.Key).OrderBy(static n => n, StringComparer.Ordinal));
    }
}

internal static class ShaderBindings
{
    public static ShaderBindingTable Create(
        IDevice device,
        string name,
        Shader shader,
        params string[] entryPoints)
        => Create(device, name, shader, reflectedResourceNames: null, entryPoints: entryPoints);

    public static ShaderBindingTable Create(
        IDevice device,
        string name,
        Shader shader,
        IReadOnlyCollection<string>? reflectedResourceNames,
        params string[] entryPoints)
    {
        var shaders = new (Shader Shader, string EntryPoint)[entryPoints.Length];
        for (int i = 0; i < entryPoints.Length; i++)
            shaders[i] = (shader, entryPoints[i]);

        return Create(device, name, reflectedResourceNames, shaders);
    }

    public static ShaderBindingTable Create(
        IDevice device,
        string name,
        params (Shader Shader, string EntryPoint)[] shaders)
        => Create(device, name, reflectedResourceNames: null, shaders);

    public static ShaderBindingTable Create(
        IDevice device,
        string name,
        IReadOnlyCollection<string>? reflectedResourceNames,
        ReadOnlySpan<(Shader Shader, string EntryPoint)> shaders)
        => CreateCore(device, name, reflectedResourceNames, pushConstants: null, shaders);

    internal static ShaderBindingTable Create(
        IDevice device,
        string name,
        IReadOnlyCollection<string>? reflectedResourceNames,
        IReadOnlyList<PushRangeDesc>? pushConstants,
        ReadOnlySpan<(Shader Shader, string EntryPoint)> shaders)
        => CreateCore(device, name, reflectedResourceNames, pushConstants, shaders);

    private static ShaderBindingTable CreateCore(
        IDevice device,
        string name,
        IReadOnlyCollection<string>? reflectedResourceNames,
        IReadOnlyList<PushRangeDesc>? pushConstants,
        ReadOnlySpan<(Shader Shader, string EntryPoint)> shaders)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (shaders.Length == 0)
            throw new ArgumentException("At least one shader entry point is required.", nameof(shaders));

        HashSet<string>? reflectedResourceSet = reflectedResourceNames == null
            ? null
            : new HashSet<string>(reflectedResourceNames, StringComparer.Ordinal);

        var slotsBySet = new FlatDictionary<uint, FlatDictionary<BindingSlotKey, BindingSlotDesc>>();
        var bindings = new FlatDictionary<string, ReflectedBinding>(StringComparer.Ordinal);
        string backend = PipelineCache.BackendName(device.AdapterInfo.Backend);

        for (int shaderIndex = 0; shaderIndex < shaders.Length; shaderIndex++)
        {
            Shader shader = shaders[shaderIndex].Shader;
            string entryPoint = shaders[shaderIndex].EntryPoint;
            ArgumentNullException.ThrowIfNull(shader);
            ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

            bool foundVariant = false;
            ShaderStageFlags variantStages = ShaderStageFlags.None;
            foreach (ShaderVariant variant in shader.Variants)
            {
                if (!string.Equals(variant.Backend, backend, StringComparison.Ordinal)
                    || !string.Equals(variant.EntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    continue;
                }

                foundVariant = true;
                variantStages |= variant.Stage switch
                {
                    ShaderStage.Vertex => ShaderStageFlags.Vertex,
                    ShaderStage.Pixel => ShaderStageFlags.Pixel,
                    ShaderStage.Compute => ShaderStageFlags.Compute,
                    ShaderStage.Hull => ShaderStageFlags.Hull,
                    ShaderStage.Domain => ShaderStageFlags.Domain,
                    ShaderStage.Geometry => ShaderStageFlags.Geometry,
                    ShaderStage.Amplification => ShaderStageFlags.Amplification,
                    ShaderStage.Mesh => ShaderStageFlags.Mesh,
                    ShaderStage.RayGeneration => ShaderStageFlags.RayGeneration,
                    ShaderStage.AnyHit => ShaderStageFlags.AnyHit,
                    ShaderStage.ClosestHit => ShaderStageFlags.ClosestHit,
                    ShaderStage.Miss => ShaderStageFlags.Miss,
                    ShaderStage.Intersection => ShaderStageFlags.Intersection,
                    ShaderStage.Callable => ShaderStageFlags.Callable,
                    _ => ShaderStageFlags.None,
                };
            }

            if (!foundVariant)
                throw new InvalidOperationException(
                    $"Shader '{shader.Name}' has no {backend} variant for entry '{entryPoint}'.");

            foreach (var resource in GetResources(
                shader,
                backend,
                entryPoint))
            {
                string resourceName = resource.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resourceName))
                    continue;
                if (reflectedResourceSet != null && !reflectedResourceSet.Contains(resourceName))
                    continue;

                BindingType bindingType = ReadBindingType(resource.Type, resourceName);
                ShaderStageFlags stages = MapStages(resource.Stages);
                if (stages == ShaderStageFlags.None)
                    stages = variantStages;
                if (stages == ShaderStageFlags.None)
                    stages = ShaderStageFlags.All;

                var binding = new ReflectedBinding(
                    resourceName,
                    resource.Set,
                    resource.Binding,
                    bindingType,
                    stages);

                if (bindings.TryGetValue(resourceName, out var existing))
                {
                    if (existing.Set != binding.Set
                        || existing.Binding != binding.Binding
                        || existing.Type != binding.Type)
                    {
                        throw new InvalidOperationException(
                            $"Shader '{shader.Name}' reflects incompatible duplicate resource '{resourceName}'.");
                    }

                    bindings[resourceName] = existing with { Stages = existing.Stages | binding.Stages };
                }
                else
                {
                    bindings.Add(resourceName, binding);
                }

                if (!slotsBySet.TryGetValue(resource.Set, out var slots))
                {
                    slots = new FlatDictionary<BindingSlotKey, BindingSlotDesc>();
                    slotsBySet.Add(resource.Set, slots);
                }

                var slotKey = new BindingSlotKey(resource.Binding, RegisterClass(bindingType));
                if (slots.TryGetValue(slotKey, out var existingSlot))
                {
                    if (existingSlot.Type != bindingType)
                    {
                        throw new InvalidOperationException(
                            $"Shader '{shader.Name}' reflects binding {resource.Binding} in set {resource.Set} with incompatible types.");
                    }

                    slots[slotKey] = existingSlot with
                    {
                        Stages = existingSlot.Stages | stages,
                    };
                }
                else
                {
                    slots.Add(
                        slotKey,
                        new BindingSlotDesc
                        {
                            Binding = resource.Binding,
                            Type = bindingType,
                            Stages = stages,
                        });
                }
            }
        }

        if (slotsBySet.Count == 0)
        {
            var emptyPipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc
            {
                Name = $"{name} Pipeline Layout",
            });
            return new ShaderBindingTable([], emptyPipelineLayout, [], bindings, ToBindingArray(bindings));
        }

        uint maxSet = MaxSet(slotsBySet);
        var layouts = new BindingLayoutHandle[maxSet + 1];
        var bindingsBySet = new ReflectedBinding[maxSet + 1][];
        for (uint set = 0; set <= maxSet; set++)
        {
            BindingSlotDesc[] slots = slotsBySet.TryGetValue(set, out var setSlots)
                ? [.. setSlots.Select(static pair => pair.Value)
                    .OrderBy(static slot => RegisterClass(slot.Type))
                    .ThenBy(static slot => slot.Binding)]
                : [];
            layouts[set] = device.CreateBindingLayout(new BindingLayoutDesc
            {
                Name = $"{name} Set {set}",
                Slots = slots,
            });
            bindingsBySet[set] =
            [
                .. bindings.Select(static pair => pair.Value)
                    .Where(binding => binding.Set == set)
                    .OrderBy(static binding => RegisterClass(binding.Type))
                    .ThenBy(static binding => binding.Binding)
                    .ThenBy(static binding => binding.Name, StringComparer.Ordinal),
            ];
        }

        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc
        {
            Name = $"{name} Pipeline Layout",
            BindingLayouts = layouts,
            PushConstants = pushConstants ?? Array.Empty<PushRangeDesc>(),
        });

        return new ShaderBindingTable(
            layouts,
            pipelineLayout,
            bindingsBySet,
            bindings,
            [.. bindings.Select(static pair => pair.Value)
                .OrderBy(static binding => binding.Set)
                .ThenBy(static binding => RegisterClass(binding.Type))
                .ThenBy(static binding => binding.Binding)
                .ThenBy(static binding => binding.Name, StringComparer.Ordinal)]);
    }

    public static ReflectedBinding GetRequired(this ShaderBindingTable table, string name)
    {
        if (table.TryGet(name, out var binding))
            return binding;

        throw new InvalidOperationException(
            $"shader binding '{name}' was not reflected. Candidates: {table.Candidates()}.");
    }

    public static void Destroy(IDevice? device, ShaderBindingTable table)
    {
        if (device == null)
            return;

        if (table.PipelineLayout.IsValid)
            device.Destroy(table.PipelineLayout);

        var layouts = table.Layouts;
        for (int index = layouts.Length - 1; index >= 0; index--)
        {
            if (layouts[index].IsValid)
                device.Destroy(layouts[index]);
        }
    }

    private static IReadOnlyList<ShaderResource> GetResources(
        Shader shader,
        string backend,
        string entryPoint)
    {
        if (!shader.TryReflection(backend, entryPoint, out ShaderReflection reflection))
        {
            throw new InvalidOperationException(
                $"Shader '{shader.Name}' has no {backend} entry-point reflection for '{entryPoint}'. Reimport the shader asset with the current Slang importer.");
        }

        return reflection.Resources;
    }

    private static BindingType ReadBindingType(BindingType bindingType, string resourceName)
    {
        if (bindingType == BindingType.None || !Enum.IsDefined(bindingType))
            throw new NotSupportedException(
                $"Shader resource '{resourceName}' does not provide a supported reflected binding type.");
        return bindingType;
    }

    private static BindingRegisterClass RegisterClass(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => BindingRegisterClass.ConstantBuffer,
            BindingType.StorageBufferRead
                or BindingType.RawBufferRead
                or BindingType.TextureRead
                or BindingType.AccelerationStructure => BindingRegisterClass.ShaderResource,
            BindingType.StorageBufferReadWrite
                or BindingType.RawBufferReadWrite
                or BindingType.TextureReadWrite => BindingRegisterClass.UnorderedAccess,
            BindingType.Sampler => BindingRegisterClass.Sampler,
            _ => throw new InvalidOperationException($"Binding type {type} has no shader register class."),
        };

    private static ShaderStageFlags MapStages(uint stages)
    {
        ShaderStageFlags result = ShaderStageFlags.None;
        if ((stages & (1u << 0)) != 0)
            result |= ShaderStageFlags.Vertex;
        if ((stages & (1u << 1)) != 0)
            result |= ShaderStageFlags.Pixel;
        if ((stages & (1u << 2)) != 0)
            result |= ShaderStageFlags.Geometry;
        if ((stages & (1u << 3)) != 0)
            result |= ShaderStageFlags.Hull;
        if ((stages & (1u << 4)) != 0)
            result |= ShaderStageFlags.Domain;
        if ((stages & (1u << 5)) != 0)
            result |= ShaderStageFlags.Compute;
        if ((stages & (1u << 6)) != 0)
            result |= ShaderStageFlags.Amplification;
        if ((stages & (1u << 7)) != 0)
            result |= ShaderStageFlags.Mesh;
        return result;
    }

    private static uint MaxSet(FlatDictionary<uint, FlatDictionary<BindingSlotKey, BindingSlotDesc>> slotsBySet)
    {
        uint maxSet = 0;
        foreach (var pair in slotsBySet)
            maxSet = Math.Max(maxSet, pair.Key);
        return maxSet;
    }

    private static ReflectedBinding[] ToBindingArray(FlatDictionary<string, ReflectedBinding> bindings)
    {
        var result = new ReflectedBinding[bindings.Count];
        int index = 0;
        foreach (var pair in bindings)
            result[index++] = pair.Value;
        return result;
    }

    private enum BindingRegisterClass
    {
        ConstantBuffer,
        ShaderResource,
        UnorderedAccess,
        Sampler,
    }

    private readonly record struct BindingSlotKey(uint Binding, BindingRegisterClass RegisterClass);
}

internal readonly record struct ReflectedBinding(
    string Name,
    uint Set,
    uint Binding,
    BindingType Type,
    ShaderStageFlags Stages)
{
    public BindingResourceDesc Texture(TextureViewHandle view)
    {
        if (Type is not BindingType.TextureRead and not BindingType.TextureReadWrite)
            throw new InvalidOperationException($"binding '{Name}' expects {Type}, not a texture.");
        return new BindingResourceDesc
        {
            Binding = Binding,
            ResourceType = Type,
            TextureView = view,
        };
    }

    public BindingResourceDesc Buffer(BufferViewHandle view)
    {
        if (Type is not BindingType.ConstantBuffer
            and not BindingType.StorageBufferRead
            and not BindingType.StorageBufferReadWrite
            and not BindingType.RawBufferRead
            and not BindingType.RawBufferReadWrite)
        {
            throw new InvalidOperationException($"binding '{Name}' expects {Type}, not a buffer.");
        }

        return new BindingResourceDesc
        {
            Binding = Binding,
            ResourceType = Type,
            BufferView = view,
        };
    }

    public BindingResourceDesc Sampler(SamplerHandle sampler)
    {
        if (Type != BindingType.Sampler)
            throw new InvalidOperationException($"binding '{Name}' expects {Type}, not a sampler.");
        return new BindingResourceDesc
        {
            Binding = Binding,
            ResourceType = Type,
            SamplerHandle = sampler,
        };
    }
}

public readonly struct BindingKey : IEquatable<BindingKey>
{
    private static readonly BindingSlot[] EmptySlots = [];

    private readonly BindingSlot[] _slots;
    private readonly int _hash;

    private BindingKey(BindingSlot[] slots)
    {
        _slots = slots.Length == 0 ? EmptySlots : slots;
        var hash = new HashCode();
        for (int i = 0; i < _slots.Length; i++)
            hash.Add(_slots[i]);
        _hash = hash.ToHashCode();
    }

    public static BindingKey Empty { get; } = new(EmptySlots);

    internal static BindingKey From(ReadOnlySpan<ReflectedBinding> bindings)
    {
        if (bindings.IsEmpty)
            return Empty;

        var slots = new BindingSlot[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            ReflectedBinding binding = bindings[i];
            slots[i] = new BindingSlot(
                binding.Set,
                binding.Binding,
                binding.Type,
                binding.Stages);
        }

        return new BindingKey(slots);
    }

    public bool Equals(BindingKey other)
    {
        if (_hash != other._hash)
            return false;

        ReadOnlySpan<BindingSlot> left = _slots ?? EmptySlots;
        ReadOnlySpan<BindingSlot> right = other._slots ?? EmptySlots;
        return left.SequenceEqual(right);
    }

    internal void AddTo(PipelineStateWriter writer)
    {
        ReadOnlySpan<BindingSlot> slots = _slots ?? EmptySlots;
        writer.AddValue(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            BindingSlot slot = slots[i];
            writer.AddValue(slot.Set);
            writer.AddValue(slot.Binding);
            writer.AddEnum(slot.Type);
            writer.AddEnum(slot.Stages);
        }
    }

    public override bool Equals(object? obj)
        => obj is BindingKey other && Equals(other);

    public override int GetHashCode()
        => _hash;

    private readonly record struct BindingSlot(
        uint Set,
        uint Binding,
        BindingType Type,
        ShaderStageFlags Stages);
}


