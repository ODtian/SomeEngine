using System;
using System.Collections.Generic;
using System.Linq;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets;

public static class AssetTypeRegistration
{
    public static void RegisterBuiltIns()
    {
        AssetTypeRegistry.Register(new ShaderAssetTypeHandler());
        AssetTypeRegistry.Register(new MaterialAssetTypeHandler());
        AssetTypeRegistry.Register(new MaterialInstanceAssetTypeHandler());
        AssetTypeRegistry.Register(new MeshAssetTypeHandler());
        AssetTypeRegistry.RegisterImporter(new SlangSourceImporter());
    }

    private sealed class ShaderAssetTypeHandler : IAssetTypeHandler
    {
        public string AssetType => nameof(ShaderAsset);
        public bool MatchesAssetPath(string assetPath) => assetPath.EndsWith(".shader.asset", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".slang.asset", StringComparison.OrdinalIgnoreCase);
        public IAsset? Load(string assetPath) => ShaderAssetSerializer.Load(assetPath);
        public IReadOnlyList<AssetGuid> GetDependencies(IAsset asset) => [];
    }

    private sealed class MaterialAssetTypeHandler : IAssetTypeHandler
    {
        public string AssetType => nameof(MaterialAsset);
        public bool MatchesAssetPath(string assetPath) => assetPath.EndsWith(".material.asset", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".mat.asset", StringComparison.OrdinalIgnoreCase);
        public IAsset? Load(string assetPath) => MaterialAssetSerializer.Load(assetPath);

        public IReadOnlyList<AssetGuid> GetDependencies(IAsset asset)
            => asset is MaterialAsset material && material.Passes != null
                ? material.Passes
                    .Select(static pass => AssetGuid.TryParse(pass.ShaderGuid, out AssetGuid guid) ? guid : AssetGuid.Empty)
                    .Where(static guid => !guid.IsEmpty)
                    .Distinct()
                    .ToArray()
                : [];
    }

    private sealed class MaterialInstanceAssetTypeHandler : IAssetTypeHandler
    {
        public string AssetType => nameof(MaterialInstanceAsset);
        public bool MatchesAssetPath(string assetPath) => assetPath.EndsWith(".materialinstance.asset", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".matinst.asset", StringComparison.OrdinalIgnoreCase);
        public IAsset? Load(string assetPath) => MaterialInstanceAssetSerializer.Load(assetPath);

        public IReadOnlyList<AssetGuid> GetDependencies(IAsset asset)
            => asset is MaterialInstanceAsset instance && AssetGuid.TryParse(instance.ParentGuid, out AssetGuid guid) && !guid.IsEmpty
                ? [guid]
                : [];
    }

    private sealed class MeshAssetTypeHandler : IAssetTypeHandler
    {
        public string AssetType => nameof(MeshAsset);
        public bool MatchesAssetPath(string assetPath) => assetPath.EndsWith(".mesh.asset", StringComparison.OrdinalIgnoreCase);
        public IAsset? Load(string assetPath) => MeshAssetSerializer.Load(assetPath);

        public IReadOnlyList<AssetGuid> GetDependencies(IAsset asset)
            => asset is MeshAsset mesh && mesh.DefaultMaterialGuids != null
                ? mesh.DefaultMaterialGuids
                    .Select(static value => AssetGuid.TryParse(value, out AssetGuid guid) ? guid : AssetGuid.Empty)
                    .Where(static guid => !guid.IsEmpty)
                    .Distinct()
                    .ToArray()
                : [];
    }
}
