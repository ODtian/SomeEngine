using System.IO;
using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class MaterialInstanceAssetSerializer
{
    public static void Save(MaterialInstanceAsset asset, string path)
    {
        int maxSize = MaterialInstanceAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MaterialInstanceAsset.Serializer.Write(buffer, asset);

        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static MaterialInstanceAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return MaterialInstanceAsset.Serializer.Parse(bytes);
    }

    public static MaterialInstanceAsset Parse(byte[] data)
    {
        return MaterialInstanceAsset.Serializer.Parse(data);
    }
}
