using System.IO;
using FlatSharp;
using NUnit.Framework;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Linq;

namespace SomeEngine.Tests;

[TestFixture]
public class IcoSphereTest
{
    [Test]
    public void TestIcoSphereGeneration()
    {
        // Level 3 subdivision -> 1280 triangles, 642 vertices
        // Level 4 subdivision -> 5120 triangles, 2562 vertices
        // Level 5 subdivision -> 20480 triangles, 10242 vertices
        var (vertices, indices, attributes) =
            PrimitiveMeshGenerator.CreateIcoSphere(5);

        Assert.That(indices.Length, Is.EqualTo(20480 * 3));
        Assert.That(vertices.Length, Is.EqualTo(10242));

        // Process through ClusterBuilder
        var meshAsset = ClusterBuilder.ProcessRaw(
            vertices, attributes, indices, "IcoSphere_LOD5"
        );

        Assert.That(meshAsset.Payload, Is.Not.Null);
        Assert.That(meshAsset.Payload.Value.Length, Is.GreaterThan(0));

        // Save to disk for engine to use
        string outputPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "../../../../../../samples/IcoSphere.mesh"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var fs = File.Create(outputPath);

        // Serialize using FlatSharp generated Serializer
        int maxSize = MeshAsset.Serializer.GetMaxSize(meshAsset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MeshAsset.Serializer.Write(buffer, meshAsset);

        fs.Write(buffer, 0, bytesWritten);
    }

    [Test]
    public void GenerateIcoSphereGltf()
    {
        var (vertices, indices, attributes) =
            PrimitiveMeshGenerator.CreateIcoSphere(5);

        var mesh =
            new MeshBuilder<VertexPositionNormal, VertexTexture1>("IcoSphere");
        var primitive = mesh.UsePrimitive(MaterialBuilder.CreateDefault());

        var normalsAttr = attributes.Find(a => a.Name == "NORMAL");
        var uvsAttr = attributes.Find(a => a.Name == "TEXCOORD_0");

        float[] normals = normalsAttr?.Data ?? new float[vertices.Length * 3];
        float[] uvs = uvsAttr?.Data ?? new float[vertices.Length * 2];

        for (int i = 0; i < indices.Length; i += 3)
        {
            uint i1 = indices[i];
            uint i2 = indices[i + 1];
            uint i3 = indices[i + 2];

            var v1 = vertices[i1];
            var v2 = vertices[i2];
            var v3 = vertices[i3];

            var n1 = new System.Numerics.Vector3(
                normals[i1 * 3], normals[i1 * 3 + 1], normals[i1 * 3 + 2]
            );
            var n2 = new System.Numerics.Vector3(
                normals[i2 * 3], normals[i2 * 3 + 1], normals[i2 * 3 + 2]
            );
            var n3 = new System.Numerics.Vector3(
                normals[i3 * 3], normals[i3 * 3 + 1], normals[i3 * 3 + 2]
            );

            var uv1 = new System.Numerics.Vector2(uvs[i1 * 2], uvs[i1 * 2 + 1]);
            var uv2 = new System.Numerics.Vector2(uvs[i2 * 2], uvs[i2 * 2 + 1]);
            var uv3 = new System.Numerics.Vector2(uvs[i3 * 2], uvs[i3 * 2 + 1]);

            primitive.AddTriangle(
                (new VertexPositionNormal(v1, n1), new VertexTexture1(uv1)),
                (new VertexPositionNormal(v2, n2), new VertexTexture1(uv2)),
                (new VertexPositionNormal(v3, n3), new VertexTexture1(uv3))
            );
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, System.Numerics.Matrix4x4.Identity);

        string outputPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "../../../../../../samples/IcoSphere.glb"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        scene.ToGltf2().SaveGLB(outputPath);

        TestContext.Out.WriteLine($"Exported GLTF to {outputPath}");
    }
}
