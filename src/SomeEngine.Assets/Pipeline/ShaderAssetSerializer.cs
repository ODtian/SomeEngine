using System.IO;
using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class ShaderAssetSerializer
{
    public static void Save(ShaderAsset asset, string path)
    {
        int maxSize = ShaderAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = ShaderAsset.Serializer.Write(buffer, asset);
        
        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static ShaderAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return ShaderAsset.Serializer.Parse(bytes);
    }
}
