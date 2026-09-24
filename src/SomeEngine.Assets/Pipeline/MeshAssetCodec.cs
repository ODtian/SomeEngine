using System;
using System.Collections.Generic;
using System.IO;
using FlatSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class MeshAssetCodec
{
    public static void Save(MeshAsset asset, string path)
    {
        // Auto-generate AssetGuid if missing
        if (string.IsNullOrWhiteSpace(asset.AssetGuid))
        {
            asset.AssetGuid = AssetGuid.New().ToFlatString();
        }

        int maxSize = MeshAsset.Serializer.GetMaxSize(asset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MeshAsset.Serializer.Write(buffer, asset);
        
        using var fs = File.Create(path);
        fs.Write(buffer, 0, bytesWritten);

        // Auto-create .meta file
        var fullPath = Path.GetFullPath(path);
        var existingMeta = AssetMetaFiles.TryLoad(fullPath);
        if (existingMeta == null)
        {
            var meta = new AssetMeta
            {
                AssetGuid = AssetGuid.Parse(asset.AssetGuid),
                SourceGuid = SourceGuid.Empty,
                SubAssetKey = string.Empty,
                ContentFingerprint = string.Empty,
                Dependencies = Array.Empty<DependencyEntryData>(),
                ImporterVersion = 0,
                AssetPath = fullPath,
            };
            AssetMetaFiles.Save(fullPath, meta);
        }
    }

    public static MeshAsset Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return MeshAsset.Serializer.Parse(bytes);
    }
}
