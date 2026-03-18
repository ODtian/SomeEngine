using System.IO;
using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class MaterialAssetSerializer
{
    public static void Save(MaterialAsset asset, string path)
    {
        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MaterialAsset.Serializer.Write(buffer, asset);

        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static MaterialAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return MaterialAsset.Serializer.Parse(bytes);
    }

    public static MaterialAsset Parse(byte[] data)
    {
        return MaterialAsset.Serializer.Parse(data);
    }
}
