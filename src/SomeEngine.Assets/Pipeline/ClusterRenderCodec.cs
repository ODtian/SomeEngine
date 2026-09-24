using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class ClusterRenderCodec
{
    public static void Save(ClusterRenderAsset asset, string path)
    {
        int maxSize = ClusterRenderAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = ClusterRenderAsset.Serializer.Write(buffer, asset);

        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);
    }

    public static ClusterRenderAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return ClusterRenderAsset.Serializer.Parse(bytes);
    }

    public static ClusterRenderAsset Parse(byte[] data)
        => ClusterRenderAsset.Serializer.Parse(data);
}
