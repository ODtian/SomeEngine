using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;
using Xunit;

namespace SomeEngine.Rhi.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D12FactAttribute : FactAttribute
{
    public D3D12FactAttribute()
    {
        Skip = D3D12TestEnvironment.BackendSkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class D3D12DxcFactAttribute : FactAttribute
{
    public D3D12DxcFactAttribute()
    {
        Skip = D3D12TestEnvironment.DxcSkipReason;
    }
}

internal static class D3D12TestEnvironment
{
    private static readonly Lazy<string?> BackendSkipReasonValue = new(GetBackendSkipReason);
    private static readonly Lazy<string?> DxcPathValue = new(FindDxcPath);

    public static string? BackendSkipReason => BackendSkipReasonValue.Value;

    public static string? DxcSkipReason
        => BackendSkipReason ?? (DxcPathValue.Value == null ? "D3D12 DXC tests require dxc.exe in PATH or Windows Kits." : null);

    public static string? FindDxc() => DxcPathValue.Value;

    private static string? GetBackendSkipReason()
    {
        if (!OperatingSystem.IsWindows())
            return "D3D12 backend tests require Windows.";

        using var instance = SomeEngine.Rhi.Instance.Create(D3D12Backend.Factory);
        return instance.EnumerateAdapters().Any(adapter => adapter.Backend == Backend.D3D12)
            ? null
            : "D3D12 backend tests require an available D3D12 adapter.";
    }

    private static string? FindDxcPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, "dxc.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var root = Environment.GetFolderPath(programFiles);
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var kitBin = Path.Combine(root, "Windows Kits", "10", "bin");
            if (!Directory.Exists(kitBin))
                continue;
            var directCandidate = Path.Combine(kitBin, "x64", "dxc.exe");
            if (File.Exists(directCandidate))
                return directCandidate;
            foreach (var version in Directory.EnumerateDirectories(kitBin).OrderByDescending(static directory => Path.GetFileName(directory)))
            {
                var candidate = Path.Combine(version, "x64", "dxc.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
