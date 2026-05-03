using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Materials;

public class BinSpaceSlotBindingTests
{
    [Fact]
    public void AllocateSlots_CanBindDifferentEntitiesPerField()
    {
        EntityStore materialStore = new();
        ShaderAsset shadeShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "Shade",
            ("ClusterShade", Array.Empty<string>(), "CSShade"));
        ShaderAsset rasterShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "Raster",
            ("ClusterRaster", ["sw_inline"], "CSSWRaster"));

        Entity shadeEntity = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "ShadeMat", AssetGuid.New()).PassEntities[0];
        shadeEntity.AddComponent(new ClusterShadeComponent { Default = new ShaderVariantRef(shadeShader, "CSShade") });
        shadeEntity.AddTag<Opaque>();

        Entity rasterEntity = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "RasterMat", AssetGuid.New()).PassEntities[0];
        rasterEntity.AddComponent(new ClusterRaster { SWInline = new ShaderVariantRef(rasterShader, "CSSWRaster") });

        using BinSpace binSpace = new();
        int rasterField = binSpace.RegisterField("RasterBin");
        int shadeField = binSpace.RegisterField("ShadingBin");
        Entity[] rasterEntities = [rasterEntity];
        Entity[] shadeEntities = [shadeEntity];

        binSpace.RegisterGroup(rasterField, new BinQueue.BinGroup
        {
            Query = () => rasterEntities,
            OrderKey = static _ => 0,
            SignatureFunc = entity => (ulong)entity.Id,
        });
        binSpace.RegisterGroup(shadeField, new BinQueue.BinGroup
        {
            Query = () => shadeEntities,
            OrderKey = static _ => 0,
            SignatureFunc = entity => (ulong)entity.Id,
        });

        binSpace.FreezeLayout();
        int slotOffset = binSpace.AllocateSlots(
        [
            new MaterialSlotBinding(
                shadeEntity,
                [rasterEntity, shadeEntity]),
        ]);

        binSpace.RebuildIfDirty();

        Assert.Equal(0, slotOffset);
        Assert.Equal((ushort)0, binSpace.SlotBuffer!.GetField(0, 0, rasterField));
        Assert.Equal((ushort)0, binSpace.SlotBuffer!.GetField(0, 0, shadeField));
    }
}
