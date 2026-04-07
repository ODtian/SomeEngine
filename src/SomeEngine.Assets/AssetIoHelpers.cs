using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SomeEngine.Assets;

internal static class AssetIoHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/');

    internal static string ToManifestPath(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string fullRoot = Path.GetFullPath(projectRoot);
        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(fullRoot, path));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/')
            : fullPath.Replace('\\', '/');
    }
}
