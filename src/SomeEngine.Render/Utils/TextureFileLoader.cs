using StbImageSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Utils;

public sealed class TextureFileImage
{
    public required string FullPath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }
}

public static class TextureFileLoader
{
    public static TextureFileImage LoadImage(string projectRoot, string path)
    {
        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));
        using FileStream stream = File.OpenRead(fullPath);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return new TextureFileImage
        {
            FullPath = fullPath,
            Width = image.Width,
            Height = image.Height,
            Pixels = image.Data,
        };
    }

    public static TextureFileImage Decode(TextureAsset asset, string name)
    {
        Memory<byte> payload = asset.Payload ?? Memory<byte>.Empty;
        if (payload.Length == 4 && asset.Width == 1 && asset.Height == 1)
        {
            return new TextureFileImage
            {
                FullPath = name,
                Width = 1,
                Height = 1,
                Pixels = payload.ToArray(),
            };
        }

        ImageResult image = ImageResult.FromMemory(payload.ToArray(), ColorComponents.RedGreenBlueAlpha);
        return new TextureFileImage
        {
            FullPath = name,
            Width = image.Width,
            Height = image.Height,
            Pixels = image.Data,
        };
    }
}
