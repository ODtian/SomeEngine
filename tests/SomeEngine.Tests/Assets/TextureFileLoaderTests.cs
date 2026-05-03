using SomeEngine.Render.Utils;

namespace SomeEngine.Tests.Assets;

public class TextureFileLoaderTests
{
    [Fact]
    public void LoadImage_ResolvesRelativePathAndDecodesPixels()
    {
        string dir = CreateTempDir();
        string texturePath = Path.Combine(dir, "assets", "Textures", "test.png");
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        File.WriteAllBytes(texturePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Z0ioAAAAASUVORK5CYII="));

        TextureFileImage image = TextureFileLoader.LoadImage(dir, "assets/Textures/test.png");

        Assert.Equal(Path.GetFullPath(texturePath), image.FullPath);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(4, image.Pixels.Length);

        Directory.Delete(dir, true);
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
