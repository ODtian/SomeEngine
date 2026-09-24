namespace SomeEngine.Render.Graph;

/// <summary>
/// Type-keyed same-frame exchange surface for render features.
/// Values are scoped to the current RenderGraph frame and are cleared by BeginFrame.
/// </summary>
public sealed class RenderGraphBlackboard
{
    private readonly Dictionary<Type, object?> _values = new();

    /// <summary>
    /// Stores same-frame typed graph data. Values are cleared by RenderGraph.BeginFrame.
    /// </summary>
    public void Set<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _values[typeof(T)] = value;
    }

    /// <summary>
    /// Reads a value published earlier in the current frame by its exact CLR type.
    /// </summary>
    public bool TryGet<T>(out T value)
    {
        if (_values.TryGetValue(typeof(T), out object? boxed)
            && boxed is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Reads a required value published earlier in the current frame by its exact CLR type.
    /// </summary>
    public T Get<T>()
        => TryGet<T>(out T value)
            ? value
            : throw new InvalidOperationException($"RenderGraph blackboard does not contain a value for '{typeof(T).FullName}'.");

    /// <summary>
    /// Removes a value from the current frame exchange surface.
    /// </summary>
    public bool Remove<T>()
        => _values.Remove(typeof(T));

    internal void Clear()
        => _values.Clear();
}
