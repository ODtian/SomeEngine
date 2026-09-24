using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

/// <summary>
/// Provider for TextureAsset data (FlatBuffer object).
/// Returns the CPU-side TextureAsset (metadata + compressed payload).
/// </summary>
public sealed class TextureAssetProvider : AssetProvider<TextureAsset>
{
    public override string AssetType => nameof(TextureAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".texture.asset", StringComparison.OrdinalIgnoreCase);

    public override TextureAsset Create(AssetGuid guid, string filePath)
        => TextureAssetCodec.Load(filePath);
}
