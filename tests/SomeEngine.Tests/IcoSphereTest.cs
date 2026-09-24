using System.IO;
using FlatSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Linq;

namespace SomeEngine.Tests;

public class IcoSphereTest
{
    [Fact]
    public void TestIcoSphereGeneration()
    {
        // Level 3 subdivision -> 1280 triangles, 642 vertices
        // Level 4 subdivision -> 5120 triangles, 2562 vertices
        // Level 5 subdivision -> 20480 triangles, 10242 vertices
        var (vertices, indices, attributes) =
            PrimitiveMeshGenerator.CreateIcoSphere(5);

        Assert.Equal(20480 * 3, indices.Length);
        Assert.Equal(10242, vertices.Length);

        // Process through ClusterBuilder
        var meshAsset = ClusterBuilder.ProcessRaw(
            vertices, attributes, indices, new System.Collections.Generic.List<string>(), "IcoSphere_LOD5"
        );

        Assert.NotNull(meshAsset.Payload);
        Assert.True(meshAsset.Payload.Value.Length > 0);

        // Verify attribute streams
        Assert.NotNull(meshAsset.Attributes);
        Assert.Equal(3, meshAsset.Attributes!.Count);
        for (int i = 0; i < meshAsset.Attributes.Count; i++)
        {
            var a = meshAsset.Attributes[i];
            Console.WriteLine($"  Attr[{i}] name={a.Name} type={a.Type} comp={a.Components} norm={a.Normalized} streamIdx={a.Offset}");
        }
        Assert.Equal("NORMAL", meshAsset.Attributes[0].Name);
        Assert.Equal("TANGENT", meshAsset.Attributes[1].Name);
        Assert.Equal("TEXCOORD_0", meshAsset.Attributes[2].Name);

        // Save to disk for engine to use
        string outputPath = Path.Combine(TestProjectPaths.ProjectRoot(), "samples", "IcoSphere.mesh");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var fs = File.Create(outputPath);

        // Serialize using FlatSharp generated Serializer
        int maxSize = MeshAsset.Serializer.GetMaxSize(meshAsset);
        byte[] buffer = new byte[maxSize];
        int bytesWritten = MeshAsset.Serializer.Write(buffer, meshAsset);

        fs.Write(buffer, 0, bytesWritten);
    }

    [Fact]
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

        string outputPath = Path.Combine(TestProjectPaths.ProjectRoot(), "samples", "IcoSphere.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        scene.ToGltf2().SaveGLB(outputPath);

        Console.WriteLine($"Exported GLTF to {outputPath}");
    }
}
