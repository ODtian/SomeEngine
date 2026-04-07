using System;
using System.Collections.Generic;
using System.Linq;

namespace SomeEngine.Assets;

public interface IAssetTypeHandler
{
    string AssetType { get; }
    bool MatchesAssetPath(string assetPath);
    IAsset? Load(string assetPath);
    IReadOnlyList<AssetGuid> GetDependencies(IAsset asset);
}

public interface IAssetImporter
{
    string ImporterName { get; }
    IReadOnlyList<string> SourceExtensions { get; }
    bool MatchesSourcePath(string sourcePath);
    IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath);
}

public static class AssetTypeRegistry
{
    private static readonly object Sync = new();
    private static readonly List<IAssetTypeHandler> Handlers = [];
    private static readonly List<IAssetImporter> Importers = [];

    public static void Register(IAssetTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Sync)
        {
            if (Handlers.Any(existing => string.Equals(existing.AssetType, handler.AssetType, StringComparison.Ordinal)))
            {
                return;
            }

            Handlers.Add(handler);
        }
    }

    public static void RegisterImporter(IAssetImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        lock (Sync)
        {
            if (Importers.Any(existing => string.Equals(existing.ImporterName, importer.ImporterName, StringComparison.Ordinal)))
            {
                return;
            }

            Importers.Add(importer);
        }
    }

    public static IAssetTypeHandler? Match(string assetPath)
    {
        string normalized = AssetIoHelpers.NormalizePath(assetPath);
        lock (Sync)
        {
            return Handlers.FirstOrDefault(handler => handler.MatchesAssetPath(normalized));
        }
    }

    public static IAssetImporter? MatchImporter(string sourcePath)
    {
        string normalized = AssetIoHelpers.NormalizePath(sourcePath);
        lock (Sync)
        {
            return Importers.FirstOrDefault(importer => importer.MatchesSourcePath(normalized));
        }
    }

    public static IReadOnlyList<string> ListAssetTypes()
    {
        lock (Sync)
        {
            return Handlers.Select(static handler => handler.AssetType).ToArray();
        }
    }

    public static IReadOnlyList<string> ListImporters()
    {
        lock (Sync)
        {
            return Importers.Select(static importer => importer.ImporterName).ToArray();
        }
    }

    internal static IReadOnlyList<IAssetImporter> GetRegisteredImporters()
    {
        lock (Sync)
        {
            return Importers.ToArray();
        }
    }

    internal static void EnsureConfigured()
    {
        if (ListAssetTypes().Count == 0)
        {
            throw new InvalidOperationException("No asset handlers are registered. Call AssetTypeRegistration.RegisterBuiltIns() or register custom handlers.");
        }
    }
}
