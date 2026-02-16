using System.IO;
using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class MeshAssetSerializer
{
    public static void Save(MeshAsset asset, string path)
    {
        int maxSize = MeshAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MeshAsset.Serializer.Write(buffer, asset);
        
        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static MeshAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return MeshAsset.Serializer.Parse(bytes);
    }
}
