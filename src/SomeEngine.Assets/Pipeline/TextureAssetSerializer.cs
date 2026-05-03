using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class TextureAssetSerializer
{
    public static void Save(TextureAsset asset, string path)
    {
        int maxSize = TextureAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = TextureAsset.Serializer.Write(buffer, asset);

        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static TextureAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return TextureAsset.Serializer.Parse(bytes);
    }

    public static TextureAsset Parse(byte[] data)
        => TextureAsset.Serializer.Parse(data);
}
