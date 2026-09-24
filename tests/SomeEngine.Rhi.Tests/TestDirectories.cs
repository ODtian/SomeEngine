namespace SomeEngine.Rhi.Tests;

internal static class TestDirectories
{
    public static string CreateTempDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine.Rhi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
