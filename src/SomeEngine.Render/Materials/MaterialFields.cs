namespace SomeEngine.Render.Materials;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class BindFieldAttribute : Attribute
{
    public BindFieldAttribute(string name)
        => Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("material binding field name must not be empty.", nameof(name))
            : name;

    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ScalarFieldAttribute : Attribute
{
    public ScalarFieldAttribute(string name)
        => Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("material scalar field name must not be empty.", nameof(name))
            : name;

    public string Name { get; }
}
