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
}
