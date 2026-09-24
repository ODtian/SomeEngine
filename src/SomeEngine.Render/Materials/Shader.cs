using SomeEngine.Rhi;
using RhiShaderStage = SomeEngine.Rhi.ShaderStage;

namespace SomeEngine.Render.Materials;

public sealed class Shader
{
    private readonly ShaderVariant[] _variants;
    private readonly ShaderAttribute[] _attributes;
    private readonly ShaderReflection[] _reflections;
    private readonly ScalarLayout[] _scalarLayouts;

    public Shader(
        string name,
        IReadOnlyList<ShaderVariant>? variants,
        IReadOnlyList<ShaderAttribute>? attributes,
        IReadOnlyList<ShaderReflection>? reflections,
        IReadOnlyList<ScalarLayout>? scalarLayouts)
    {
        Name = string.IsNullOrWhiteSpace(name) ? nameof(Shader) : name;
        _variants = Copy(variants);
        _attributes = Copy(attributes);
        _reflections = Copy(reflections);
        _scalarLayouts = Copy(scalarLayouts);
    }

    public string Name { get; }

    public IReadOnlyList<ShaderVariant> Variants => _variants;

    public IReadOnlyList<ShaderAttribute> Attributes => _attributes;

    public IReadOnlyList<ShaderReflection> Reflections => _reflections;

    public IReadOnlyList<ScalarLayout> ScalarLayouts => _scalarLayouts;

    public bool TryVariant(
        string backend,
        string entryPoint,
        out ShaderVariant variant)
    {
        for (int i = 0; i < _variants.Length; i++)
        {
            variant = _variants[i];
            if (string.Equals(variant.Backend, backend, StringComparison.Ordinal)
                && string.Equals(variant.EntryPoint, entryPoint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        variant = default;
        return false;
    }

    public bool TryVariant(
        string backend,
        string entryPoint,
        RhiShaderStage stage,
        out ShaderVariant variant)
    {
        for (int i = 0; i < _variants.Length; i++)
        {
            variant = _variants[i];
            if (string.Equals(variant.Backend, backend, StringComparison.Ordinal)
                && string.Equals(variant.EntryPoint, entryPoint, StringComparison.Ordinal)
                && variant.Stage == stage)
            {
                return true;
            }
        }

        variant = default;
        return false;
    }

    public bool TryReflection(
        string backend,
        string entryPoint,
        out ShaderReflection reflection)
    {
        for (int i = 0; i < _reflections.Length; i++)
        {
            reflection = _reflections[i];
            if (string.Equals(reflection.Backend, backend, StringComparison.Ordinal)
                && string.Equals(reflection.EntryPoint, entryPoint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        reflection = default;
        return false;
    }

    public bool TryEntry(
        ShaderAttribute attribute,
        string? preferred,
        out string entry)
    {
        entry = string.Empty;
        if (attribute.VariantIndex < 0 || attribute.VariantIndex >= _variants.Length)
            return false;

        entry = _variants[attribute.VariantIndex].EntryPoint;
        return !string.IsNullOrWhiteSpace(entry)
            && (string.IsNullOrWhiteSpace(preferred)
                || string.Equals(entry, preferred, StringComparison.Ordinal));
    }

    private static T[] Copy<T>(IReadOnlyList<T>? source)
    {
        if (source == null || source.Count == 0)
            return [];

        var copy = new T[source.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = source[i];
        return copy;
    }
}

public readonly record struct ShaderVariant(
    string Backend,
    ReadOnlyMemory<byte> Bytecode,
    string EntryPoint,
    RhiShaderStage Stage,
    string ContentHash);

public readonly record struct ShaderAttribute(
    int VariantIndex,
    string Name,
    IReadOnlyList<string> Args);

public readonly record struct ShaderReflection(
    string Backend,
    string EntryPoint,
    RhiShaderStage Stage,
    IReadOnlyList<ShaderResource> Resources);

public readonly record struct ShaderResource(
    string Name,
    uint Stages,
    uint Binding,
    uint Set,
    BindingType Type);
