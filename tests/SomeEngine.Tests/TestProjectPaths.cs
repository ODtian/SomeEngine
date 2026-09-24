namespace SomeEngine.Tests;

internal static class TestProjectPaths
{
    public static string ProjectRoot(string? startPath = null)
    {
        foreach (string candidate in StartPaths(startPath))
        {
            string? current = Path.GetFullPath(candidate);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "SomeEngine.slnx")))
                    return current;

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;

                current = parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SomeEngine project root.");
    }

    public static string ShaderDirectory()
        => Path.Combine(ProjectRoot(), "assets", "Shaders");

    public static string ShaderPath(string fileName)
        => Path.Combine(ShaderDirectory(), fileName);

    public static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IEnumerable<string> StartPaths(string? explicitStart)
    {
        if (!string.IsNullOrWhiteSpace(explicitStart))
            yield return explicitStart;

        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}
