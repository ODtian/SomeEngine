using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Tests;

public class ClusterBuilderTests
{
    [Test]
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
        var asset = ClusterBuilder.ProcessRaw(positions, rawAttributes, indices, "TestPlane");
        
        // 3. Assertions
        Assert.That(asset.Payload, Is.Not.Null);
        Assert.That(asset.Payload.Value.Length, Is.GreaterThan(0));

        // Check if we have at least one page
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        Assert.That(header.ClusterCount, Is.GreaterThan(0));
        Assert.That(header.TotalVertexCount, Is.GreaterThan(0));
    }

    [Test]
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

        var asset = ClusterBuilder.ProcessRaw(positions, rawAttributes, indices, "TestSoA");

        Assert.That(asset.Payload, Is.Not.Null);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));

        Assert.That(header.ClusterCount, Is.GreaterThan(0));
        uint totalVerts = header.TotalVertexCount;
        Assert.That(totalVerts, Is.EqualTo(3));

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

            Assert.That(nx, Is.EqualTo(0), $"Normal[{v}].x should be 0");
            Assert.That(ny, Is.EqualTo(127), $"Normal[{v}].y should be 127 (SNORM for 1.0)");
            Assert.That(nz, Is.EqualTo(0), $"Normal[{v}].z should be 0");
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

            Assert.That(u, Is.EqualTo(uExpected).Within(0.01f), $"UV[{v}].u");
            Assert.That((float)BitConverter.UInt16BitsToHalf(rawV),
                Is.EqualTo(vExpected).Within(0.01f), $"UV[{v}].v");
        }

        // Verify indices start after UV stream (no interleaving gap)
        uint expectedIndicesOffset = uvBase + (uint)(3 * 4); // 3 verts * 4 bytes
        Assert.That(header.IndicesOffset, Is.EqualTo(expectedIndicesOffset),
            "Indices should start right after the last attribute stream (SoA contiguous)");
    }
}
