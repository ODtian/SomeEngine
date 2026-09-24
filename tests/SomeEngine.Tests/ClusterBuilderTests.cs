using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Tests;

public class ClusterBuilderTests
{
    [Fact]
    public void TestClusterGeneration()
    {
        // 1. Create a 32x32 plane
        int w = 32;
        int h = 32;
        var positions = new Vector3[w * h];
        var normals = new float[w * h * 3];
        var uvs = new float[w * h * 2];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                positions[y * w + x] = new Vector3(x, 0, y);
                normals[(y * w + x) * 3 + 0] = 0;
                normals[(y * w + x) * 3 + 1] = 1;
                normals[(y * w + x) * 3 + 2] = 0;
                uvs[(y * w + x) * 2 + 0] = x / (float)w;
                uvs[(y * w + x) * 2 + 1] = y / (float)h;
            }
        }

        var indicesList = new List<uint>();
        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                uint i0 = (uint)(y * w + x);
                uint i1 = (uint)(y * w + x + 1);
                uint i2 = (uint)((y + 1) * w + x);
                uint i3 = (uint)((y + 1) * w + x + 1);

                indicesList.Add(i0);
                indicesList.Add(i2);
                indicesList.Add(i1);

                indicesList.Add(i1);
                indicesList.Add(i2);
                indicesList.Add(i3);
            }
        }

        var indices = indicesList.ToArray();

        var rawAttributes = new List<RawAttribute>
        {
            new RawAttribute("NORMAL", normals, 3, ValueType.Int8, 3, true),
            new RawAttribute("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false)
        };

        // 2. Run Builder
        var asset = ClusterBuilder.ProcessRaw(positions, rawAttributes, indices, new List<string>(), "TestPlane");

        // 3. Assertions
        Assert.NotNull(asset.Payload);
        Assert.True(asset.Payload.Value.Length > 0);

        // Check if we have at least one page
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        Assert.True(header.ClusterCount > 0);
        Assert.True(header.TotalVertexCount > 0);
    }

    [Fact]
    public void TestSoAStreamLayout()
    {
        // Create a minimal triangle with known attribute values
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 }; // All pointing up
        var uvs = new float[] { 0, 0, 1, 0, 0, 1 }; // Standard triangle UVs
        var indices = new uint[] { 0, 1, 2 };

        var rawAttributes = new List<RawAttribute>
        {
            new("NORMAL", normals, 3, ValueType.Int8, 3, true),       // 3 bytes/vertex
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),   // 4 bytes/vertex
        };

        var asset = ClusterBuilder.ProcessRaw(positions, rawAttributes, indices, new List<string>(), "TestSoA");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));

        Assert.True(header.ClusterCount > 0);
        uint totalVerts = header.TotalVertexCount;
        Assert.Equal(3u, totalVerts);

        // --- Verify SoA layout ---
        // Stream 0: NORMAL (Int8x3, 3 bytes per vertex)
        // Stream 1: TEXCOORD_0 (Float16x2, 4 bytes per vertex)
        uint attrBase = header.AttributesOffset;

        // Normal stream: starts at attrBase, size = 3 * 3 = 9 bytes
        int normalStreamSize = 3 * 3; // 3 verts * 3 bytes
        for (int v = 0; v < 3; v++)
        {
            int offset = (int)attrBase + v * 3;
            // Normal = (0, 1, 0) packed as Int8 SNORM: x=0, y=127, z=0
            sbyte nx = (sbyte)span[offset + 0];
            sbyte ny = (sbyte)span[offset + 1];
            sbyte nz = (sbyte)span[offset + 2];

            Assert.Equal(0, nx);
            Assert.Equal(127, ny);
            Assert.Equal(0, nz);
        }

        // UV stream: starts right after normal stream
        uint uvBase = attrBase + (uint)normalStreamSize;
        for (int v = 0; v < 3; v++)
        {
            int offset = (int)uvBase + v * 4;
            ushort rawU = BitConverter.ToUInt16(span.Slice(offset, 2));
            ushort rawV = BitConverter.ToUInt16(span.Slice(offset + 2, 2));
            float u = (float)BitConverter.UInt16BitsToHalf(rawU);
            float uExpected = uvs[v * 2 + 0];
            float vExpected = uvs[v * 2 + 1];

            Assert.InRange(u, uExpected - 0.01f, uExpected + 0.01f);
            Assert.InRange((float)BitConverter.UInt16BitsToHalf(rawV), vExpected - 0.01f, vExpected + 0.01f);
        }

        // Verify indices start after UV stream (no interleaving gap)
        uint expectedIndicesOffset = uvBase + (uint)(3 * 4); // 3 verts * 4 bytes
        Assert.Equal(expectedIndicesOffset, header.IndicesOffset);
    }

    [Fact]
    public void TestSoAStreamLayout_WithTangent_PlacesUvAfterTangent()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 };
        var tangents = new float[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1 };
        var uvs = new float[] { 0, 0, 1, 0, 0, 1 };
        var indices = new uint[] { 0, 1, 2 };

        var rawAttributes = new List<RawAttribute>
        {
            new("NORMAL", normals, 3, ValueType.Int8, 3, true),
            new("TANGENT", tangents, 4, ValueType.Int8, 4, true),
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),
        };

        var asset = ClusterBuilder.ProcessRaw(positions, rawAttributes, indices, new List<string>(), "TestSoAWithTangent");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));

        uint attrBase = header.AttributesOffset;
        uint tangentBase = attrBase + (uint)(3 * 3);
        uint uvBase = tangentBase + (uint)(3 * 4);

        for (int v = 0; v < 3; v++)
        {
            int uvOffset = (int)uvBase + v * 4;
            ushort rawU = BitConverter.ToUInt16(span.Slice(uvOffset, 2));
            ushort rawV = BitConverter.ToUInt16(span.Slice(uvOffset + 2, 2));
            Assert.InRange((float)BitConverter.UInt16BitsToHalf(rawU), uvs[v * 2 + 0] - 0.01f, uvs[v * 2 + 0] + 0.01f);
            Assert.InRange((float)BitConverter.UInt16BitsToHalf(rawV), uvs[v * 2 + 1] - 0.01f, uvs[v * 2 + 1] + 0.01f);
        }

        uint expectedIndicesOffset = uvBase + (uint)(3 * 4);
        Assert.Equal(expectedIndicesOffset, header.IndicesOffset);
    }

    [Fact]
    public void ProcessRaw_DoesNotMergeVerticesWithDifferentAttributes()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 0),
        };
        var uvs = new float[]
        {
            0, 0,
            1, 0,
            0, 1,
            0.5f, 0.5f,
        };
        var indices = new uint[] { 0, 1, 2, 3, 2, 1 };
        var rawAttributes = new List<RawAttribute>
        {
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),
        };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            rawAttributes,
            indices,
            new List<string>(),
            "UvSeam");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        Assert.Equal(4u, header.TotalVertexCount);
        Assert.Contains(
            ReadHalf2Stream(span, header.AttributesOffset, header.TotalVertexCount),
            static uv => Math.Abs(uv.X - 0.5f) < 0.01f && Math.Abs(uv.Y - 0.5f) < 0.01f);
    }

    [Fact]
    public void ProcessRaw_ClusterBoundsContainDecodedQuantizedPositions()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1.00002f, 0, 0),
            new(0, 1.00002f, 0),
        };
        var indices = new uint[] { 0, 1, 2 };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            new List<RawAttribute>(),
            indices,
            new List<string>(),
            "QuantizedBounds");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        var clusters = MemoryMarshal.Cast<byte, GPUCluster>(
            span.Slice(
                (int)header.ClustersOffset,
                checked((int)header.ClusterCount * GPUCluster.SizeInBytes)));
        var quantizedPositions = MemoryMarshal.Cast<byte, ushort>(
            span.Slice(
                (int)header.PositionsOffset,
                checked((int)header.TotalVertexCount * 3 * sizeof(ushort))));
        var origin = new Vector3(header.QuantOriginX, header.QuantOriginY, header.QuantOriginZ);

        foreach (ref readonly var cluster in clusters)
        {
            uint vertexCount = cluster.PackedCounts & 0xFF;
            for (uint local = 0; local < vertexCount; local++)
            {
                int wordOffset = checked(((int)cluster.VertexStart + (int)local) * 3);
                var decoded = new Vector3(
                    (cluster.IntBaseX + quantizedPositions[wordOffset + 0]) * header.QuantStep + origin.X,
                    (cluster.IntBaseY + quantizedPositions[wordOffset + 1]) * header.QuantStep + origin.Y,
                    (cluster.IntBaseZ + quantizedPositions[wordOffset + 2]) * header.QuantStep + origin.Z);

                Assert.InRange(decoded.X, cluster.BoundMin.X, cluster.BoundMax.X);
                Assert.InRange(decoded.Y, cluster.BoundMin.Y, cluster.BoundMax.Y);
                Assert.InRange(decoded.Z, cluster.BoundMin.Z, cluster.BoundMax.Z);
            }
        }
    }

    [Fact]
    public void ProcessRaw_WritesMeshRegions_FromSourceSlots()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var indices = new uint[] { 0, 1, 2 };
        var materialGuid = SomeEngine.Assets.AssetGuid.New();
        var materialSlot = new MeshMaterialSlot(materialGuid);

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            new List<RawAttribute>(),
            indices,
            new List<MeshMaterialSlot> { materialSlot },
            "GuidMesh");

        Assert.NotNull(asset.Regions);
        Assert.Single(asset.Regions);
        Assert.Equal("region_0", asset.Regions[0].Name);
    }

    private static List<Vector2> ReadHalf2Stream(
        ReadOnlySpan<byte> payload,
        uint streamOffset,
        uint vertexCount)
    {
        var values = new List<Vector2>(checked((int)vertexCount));
        for (uint vertex = 0; vertex < vertexCount; vertex++)
        {
            int offset = checked((int)streamOffset + (int)vertex * 4);
            values.Add(new Vector2(
                (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload.Slice(offset, 2))),
                (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload.Slice(offset + 2, 2)))));
        }
        return values;
    }

}
