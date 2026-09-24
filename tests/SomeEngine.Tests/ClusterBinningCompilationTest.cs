using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests;

public class BinningCompileTests
{
    [Fact]
    public void RasterCompiles()
    {
        CompileOk(
            "cluster_binning.slang",
            [
                "CSBinningClear",
                "CSBinningPrepare",
                "CSBinningClearPrepare",
                "CSBinningCount",
                "CSBinningReserve",
                "CSBinningScatter",
                "CSRasterDeformBinClearPrepare",
                "CSRasterDeformBinCount",
                "CSRasterDeformBinReserve",
                "CSRasterDeformBinScatter",
            ]);
    }

    [Fact]
    public void PayloadStaysSplit()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_binning.slang"));
        string io = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_bin_io.slang"));

        Assert.Contains("#include \"cluster_bin_io.slang\"", source);
        Assert.Contains("uint RasterMaxBins;", source);
        Assert.Contains("uint DeformMaxBins;", source);
        Assert.Contains("uint RasterBinFieldIndex;", source);
        Assert.Contains("uint DeformBinFieldIndex;", source);
        Assert.Contains("RWStructuredBuffer<DeformBinEntry> DeformBinMeta;", source);
        Assert.Contains("RWStructuredBuffer<uint2> DeformBinnedClusterIndexBuffer;", source);
        Assert.Contains("RWByteAddressBuffer PreDeformDispatchArgs;", source);
        Assert.Contains("ReserveRaster(", source);
        Assert.Contains("ReserveDeform(", source);
        Assert.Contains("ScatterRaster(", source);
        Assert.Contains("ScatterDeform(", source);
        Assert.Contains("void CountComboDeform(", source);
        Assert.Contains("void ScatterComboDeform(", source);
        Assert.Contains("CountComboDeform(slotOffset, triangleCount, packedMaterials, packedRanges);", source);
        Assert.Contains("ScatterComboDeform(visibleIndex, slotOffset, triangleCount, packedMaterials, packedRanges);", source);

        Assert.Contains("void WriteDispatch(", io);
        Assert.Contains("StoreDispatchArgs(args, row * 12u, BinDispatch(count));", io);
        Assert.Contains("WriteDispatch(args, bin, count);", io);
        Assert.Contains("indices[offset + local] = uint2(visible, 0u);", io);
        Assert.Contains("indices[offset + local] = uint2(visible, (triStart << 16) | triEnd);", io);
        Assert.Contains("WriteDispatch(swArgs, maxBins + bin, swCount);", io);
    }

    [Fact]
    public void CpuBinAbiMatchesShader()
    {
        string io = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_bin_io.slang"));
        string structures = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_structures.slang"));

        Assert.Equal(32, ClusterBinGpu.RasterMetaStride);
        Assert.Contains("struct RasterBinEntry", structures);
        Assert.Contains("uint ClusterOffset;", structures);
        Assert.Contains("uint BinCapacity;", structures);
        Assert.Contains("uint SWOffset;", structures);

        Assert.Equal(16, ClusterBinGpu.DeformMetaStride);
        Assert.Contains("struct DeformBinEntry", structures);
        Assert.Contains("uint ClusterOffset;", structures);
        Assert.Contains("uint BinCount;", structures);
        Assert.Contains("uint Cursor;", structures);

        Assert.Equal(8, ClusterBinGpu.IndexStride);
        Assert.Contains("RWStructuredBuffer<uint2> indices", io);
        Assert.Contains("indices[offset + local] = uint2(visible, 0u);", io);
        Assert.Contains("indices[offset + local] = uint2(visible, (triStart << 16) | triEnd);", io);

        Assert.Equal(4, ClusterBinGpu.UintStride);
        Assert.Contains("RWStructuredBuffer<uint> offsets", io);

        Assert.Contains("StoreDispatchArgs(args, row * 12u, BinDispatch(count));", io);
        Assert.Contains("StoreDrawInstancedArgs(", io);
        Assert.Contains("DrawInstancedArgs(96, total, 0, totalOffset)", io);
        Assert.Contains("DrawInstancedArgs(96, hwCount, 0, totalOffset + swCount)", io);
    }

    [Fact]
    public void SlotAbiMatchesShader()
    {
        string binning = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_binning.slang"));
        string shade = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_shade_binning.slang"));
        string io = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_bin_io.slang"));
        string structures = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_structures.slang"));

        Assert.Contains("StructuredBuffer<uint> SlotBuffer", binning);
        Assert.Contains("StructuredBuffer<uint> SlotBuffer", shade);
        Assert.Contains("static const uint SLOT_INVALID = 0xFFFFu;", structures);
        Assert.Contains("static const uint BIN_INVALID = 0xFFFFFFFFu;", structures);
        Assert.Contains("Each pair of ushort slots packs into one uint.", structures);
        Assert.Contains("uint slotIndex = slotOffset + localMaterialIndex;", structures);
        Assert.Contains("if (slotIndex >= slotCapacity)", structures);
        Assert.Contains("return SLOT_INVALID;", structures);
        Assert.Contains("uint fieldBaseUint = fieldIndex * (slotCapacity / 2);", structures);
        Assert.Contains("uint wordIdx = fieldBaseUint + (slotIndex / 2);", structures);
        Assert.Contains("return (slotIndex % 2 == 0) ? (packed & 0xFFFF) : (packed >> 16);", structures);

        Assert.Contains("uint SlotBin(", io);
        Assert.Contains("uint localMaterialIndex,", io);
        Assert.Contains("GetSlotField(slots, slotOffset, localMaterialIndex, field, capacity)", io);
        Assert.Contains("return key != SLOT_INVALID && key < maxBins ? key : BIN_INVALID;", io);
        Assert.Contains("if (!IsBin(bin)) return;", io);
        Assert.Contains("void CountShade(", io);
        Assert.DoesNotContain("key < maxBins ? key : 0u", io);
        Assert.DoesNotContain("ClampBin", io);
    }

    [Fact]
    public void SlotBinUsesMaterialItem()
    {
        string binning = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_binning.slang"));
        string shade = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_shade_binning.slang"));
        string structures = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_structures.slang"));

        Assert.Contains("uint PackedMaterials;", structures);
        Assert.Contains("uint PackedRanges;", structures);
        Assert.Contains("uint LocalMaterial(uint packedMaterials, uint packedRanges, uint triStart)", structures);
        Assert.Contains("uint range0End = packedRanges & 0xFFu;", structures);
        Assert.Contains("uint range1End = (packedRanges >> 8) & 0xFFu;", structures);
        Assert.Contains("return (packedMaterials >> (lane * 8u)) & 0xFFu;", structures);
        Assert.Contains("uint MaterialEnd(uint packedRanges, uint triStart, uint triangleCount)", structures);

        Assert.Contains("void ReadClusterData(", binning);
        Assert.Contains("packedMaterials = PageHeap.Load(clusterOffset + 48);", binning);
        Assert.Contains("packedRanges = PageHeap.Load(clusterOffset + 52);", binning);
        Assert.Contains("uint slotOffset = GetInstanceSlotOffset(entry.z);", binning);
        Assert.Contains("LocalMaterial(packedMaterials, packedRanges, triStart)", binning);
        Assert.Contains("MaterialEnd(packedRanges, triStart, triangleCount)", binning);
        Assert.Contains("ComboDeformKey(slotOffset, localMaterialIndex)", binning);
        Assert.Contains("if (triStart >= triangleCount)", binning);
        Assert.Contains("uint triEnd = min(triStart + vrb.triCounts[b], triangleCount);", binning);

        Assert.Contains("uint localTri = DecodeVisBufferTriangleID(visData);", shade);
        Assert.Contains("uint packedMaterials = PageHeap.Load(clusterOffset + 48);", shade);
        Assert.Contains("uint packedRanges = PageHeap.Load(clusterOffset + 52);", shade);
        Assert.Contains("LocalMaterial(packedMaterials, packedRanges, localTri)", shade);
        Assert.Contains("CountShade(BinCounts, shadingBin);", shade);
    }

    [Fact]
    public void ShadeCompiles()
    {
        CompileOk(
            "cluster_shade_binning.slang",
            ["CSBinCount", "CSBinReserve", "CSBinScatter"]);
    }

    [Fact]
    public void DeformCompiles()
    {
        CompileOk(
            "cluster_deform_binning.slang",
            [
                "CSDeformBinClear",
                "CSDeformBinPrepare",
                "CSDeformBinClearPrepare",
                "CSDeformBinCount",
                "CSDeformBinReserve",
                "CSDeformBinReserveDispatchWrite",
                "CSDeformBinScatter",
            ]);
    }

    [Theory]
    [InlineData("cluster_binning.slang")]
    [InlineData("cluster_deform_binning.slang")]
    [InlineData("cluster_shade_binning.slang")]
    public void UsesBinIo(string shader)
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath(shader));

        Assert.Contains("#include \"cluster_bin_io.slang\"", source);
    }

    private static void CompileOk(string shaderFile, string[] entryPoints)
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(shaderFile));

        Assert.NotNull(asset);
        Assert.NotNull(asset.Variants);
        Assert.NotEmpty(asset.Variants!);

        foreach (string entryPoint in entryPoints)
        {
            var spirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == entryPoint && v.Backend == "spirv");
            Assert.NotNull(spirv);
            Assert.True(spirv!.Data.HasValue && spirv.Data.Value.Length > 0, $"SPIR-V bytecode should be non-empty for {entryPoint}");
        }
    }
}
