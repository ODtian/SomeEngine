namespace SomeEngine.Assets;

/// <summary>Non-generic base interface for DI collection.</summary>
public interface IAssetProvider
{
    /// <summary>Asset type tag (e.g. "ShaderAsset", "TextureAsset").</summary>
    string AssetType { get; }

    /// <summary>The CLR type this provider produces.</summary>
    Type RuntimeType { get; }

    /// <summary>Returns true if <paramref name="assetPath"/> is handled by this provider.</summary>
    bool Matches(string assetPath);

    /// <summary>Create the runtime object from the asset file on disk.</summary>
    object Create(AssetGuid guid, string filePath);

    /// <summary>Destroy/dispose a previously created runtime object.</summary>
    void Destroy(object resource);

    /// <summary>Extract dependency GUIDs from the asset file (for manifest).</summary>
    IReadOnlyList<AssetGuid> GetDependencies(string filePath) => [];
}

/// <summary>
/// Typed provider: filePath → T.
/// Provider owns full IO strategy (ReadAllBytes, mmap, DirectStorage, etc.).
/// </summary>
public abstract class AssetProvider<T> : IAssetProvider where T : class
{
    public abstract string AssetType { get; }
    public Type RuntimeType => typeof(T);
    public abstract bool Matches(string assetPath);
    public abstract T Create(AssetGuid guid, string filePath);

    /// <summary>Override for custom cleanup. Default disposes if IDisposable.</summary>
    public virtual void Destroy(T resource) => (resource as IDisposable)?.Dispose();

    public virtual IReadOnlyList<AssetGuid> GetDependencies(string filePath) => [];

    // Explicit non-generic implementations
    object IAssetProvider.Create(AssetGuid guid, string filePath) => Create(guid, filePath)!;
    void IAssetProvider.Destroy(object resource) => Destroy((T)resource);
    IReadOnlyList<AssetGuid> IAssetProvider.GetDependencies(string filePath) => GetDependencies(filePath);
}
