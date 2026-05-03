using Friflo.Engine.ECS;
using System.Buffers.Binary;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Assets;

public class GeneratedProjectAssetsTests
{
    [Fact]
    public void IcoSphereMaterial_BakesClusterFeaturePasses_FromGeneratedAssets()
    {
        string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
        using AssetDatabase assetDb = GeneratedAssetPipelineCatalog.CreateDatabase(projectRoot);
        EntityStore materialStore = new();

        Material material = MaterialAssetLoader.LoadFromFile(
            Path.Combine(projectRoot, "samples", "IcoSphere.material.0.Default.material.asset"),
            materialStore,
            textureLoader: null,
            shaderLoader: assetDb.Load<ShaderAsset>);

        Assert.Equal(3, material.PassEntities.Length);
        Assert.Contains(material.PassEntities, static entity =>
            entity.Tags.Has<Opaque>()
            && entity.TryGetComponent<ClusterShadeComponent>(out ClusterShadeComponent shade)
            && shade.Default.EntryPoint == "CSMaterialShadeCached");
        Assert.Contains(material.PassEntities, static entity =>
            entity.Tags.Has<Opaque>()
            && entity.TryGetComponent<ClusterRaster>(out ClusterRaster raster)
            && raster.SWInline.EntryPoint == "CSSWRaster"
            && raster.SWCached.EntryPoint == "CSSWRasterCached"
            && raster.HWVSInline.EntryPoint == "VSVisBuffer"
            && raster.HWVSCached.EntryPoint == "VSVisBufferCached"
            && raster.HWPS.EntryPoint == "PSVisBuffer");
        Assert.Contains(material.PassEntities, static entity =>
            entity.Tags.Has<Opaque>()
            && entity.TryGetComponent<ClusterDeform>(out ClusterDeform deform)
            && deform.Default.EntryPoint == "CSDeformWave"
            && deform.BoundsExpansion == 0.3f);

        Assert.Equal(1.0f, material.Params.GetScalar("BaseColorTint") is System.Numerics.Vector4 tint ? tint.X : 0.0f);
        byte[] scalarRegion = new byte[material.ScalarRegionByteSize];
        material.WriteScalarRegion(scalarRegion);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(scalarRegion) >= 16);
        Assert.Equal(1.0f, ReadFloat(scalarRegion, MaterialScalarRegionLayout.HeaderByteSize));
    }

    [Fact]
    public void DefaultArmTexture_IsNeutralForPbrScalarFactors()
    {
        string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
        TextureAsset arm = TextureAssetSerializer.Load(
            Path.Combine(projectRoot, "assets", "Textures", "default_arm.texture.asset"));

        Assert.NotNull(arm.Payload);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, arm.Payload.Value.ToArray());
    }

    private static string ResolveProjectRoot(string startPath)
    {
        string current = Path.GetFullPath(startPath);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SomeEngine.slnx")))
            {
                return current;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        throw new DirectoryNotFoundException($"Could not locate project root from '{startPath}'.");
    }

    private static float ReadFloat(byte[] data, int byteOffset)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(byteOffset, sizeof(uint))));
}
