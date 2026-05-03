using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Assets;

public class GltfSourceImporterTests
{
    [Fact]
    public void Import_UsesDefaultTemplates_WhenSourceMetaLacksImporterSettings()
    {
        string dir = CreateTempDir();

        try
        {
            string gltfPath = WriteTestProject(dir, writeImporterSettings: false);
            var importer = new GltfSourceImporter();

            IReadOnlyList<ImportedAsset> imported = importer.Import(dir, gltfPath);

            Assert.Contains(imported, asset => asset.SubAssetKey.StartsWith("material:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Import_ProducesMaterialAndMeshSubAssets_WithStableKeys_AndMeshRegionsWithoutMaterialDependencies()
    {
        string dir = CreateTempDir();

        try
        {
            string gltfPath = WriteTestProject(dir, writeImporterSettings: true);
            var importer = new GltfSourceImporter();

            IReadOnlyList<ImportedAsset> first = importer.Import(dir, gltfPath);
            IReadOnlyList<ImportedAsset> second = importer.Import(dir, gltfPath);

            Assert.Equal(9, first.Count); // 5 textures + 2 materials + 2 meshes
            Assert.Equal(
                first.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.SubAssetKey),
                second.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.SubAssetKey));
            Assert.Equal(
                first.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.Asset.AssetGuid),
                second.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.Asset.AssetGuid));

            ImportedAsset maskedMaterial = Assert.Single(first, asset => asset.SubAssetKey == "material:0:MaskedLit");
            ImportedAsset transparentMaterial = Assert.Single(first, asset => asset.SubAssetKey == "material:1:TransparentUnlit");
            ImportedAsset bodyMesh = Assert.Single(first, asset => asset.SubAssetKey == "mesh:0:Body");
            ImportedAsset eyesMesh = Assert.Single(first, asset => asset.SubAssetKey == "mesh:1:Eyes");

            MaterialAsset maskedAsset = MaterialAssetSerializer.Load(maskedMaterial.OutputPath);
            MaterialAsset transparentAsset = MaterialAssetSerializer.Load(transparentMaterial.OutputPath);
            MeshAsset bodyAsset = MeshAssetSerializer.Load(bodyMesh.OutputPath);
            MeshAsset eyesAsset = MeshAssetSerializer.Load(eyesMesh.OutputPath);
            AssetMeta? bodyMeshMeta = AssetMetaManager.TryLoad(bodyMesh.OutputPath);

            Assert.Equal("lit-shader-guid", maskedAsset.Passes![0].ShaderGuid);
            Assert.Contains(maskedAsset.Passes[0].Tags!, tag => tag.Name == "masked");
            Assert.Contains(maskedAsset.Passes[0].Tags!, tag => tag.Name == "two_sided");
            Assert.DoesNotContain(maskedAsset.Passes[0].Tags!, tag => tag.Name == "opaque");
            Assert.Contains(maskedAsset.Passes[0].Components!, component =>
                component.TypeName == nameof(OverlayShade)
                && component.Json == "{\"Layer\":5}");
            Assert.Contains(maskedAsset.Textures!, binding => binding.Name == "AlbedoMap");
            Assert.Contains(maskedAsset.Textures!, binding => binding.Name == "NormalMap");
            Assert.Contains(maskedAsset.Textures!, binding => binding.Name == "ARMMap");
            Assert.Contains(maskedAsset.Scalars!, scalar => scalar.Name == "BaseColorTint");
            Assert.Contains(maskedAsset.Scalars!, scalar => scalar.Name == "MetallicFactor");
            Assert.Contains(maskedAsset.Scalars!, scalar => scalar.Name == "Roughness");
            Assert.Contains(maskedAsset.Scalars!, scalar => scalar.Name == "AlphaCutoff");
            Assert.Contains(maskedAsset.Scalars!, scalar => scalar.Name == "EmissiveFactor");

            Assert.Equal("unlit-shader-guid", transparentAsset.Passes![0].ShaderGuid);
            Assert.Contains(transparentAsset.Passes[0].Tags!, tag => tag.Name == "translucent");
            Assert.DoesNotContain(transparentAsset.Passes[0].Tags!, tag => tag.Name == "opaque");

            Assert.NotNull(bodyAsset.Regions);
            Assert.Single(bodyAsset.Regions);
            Assert.Equal("region_0", bodyAsset.Regions[0].Name);
            Assert.NotNull(bodyMeshMeta);
            Assert.Equal(GltfSourceImporter.ImporterVersion, bodyMeshMeta!.ImporterVersion);

            Assert.NotNull(eyesAsset.Regions);
            Assert.Single(eyesAsset.Regions);
            Assert.Equal("region_0", eyesAsset.Regions[0].Name);

            // Verify texture sub-assets are produced and texture bindings use GUIDs
            var textureKeys = first.Where(asset => asset.SubAssetKey.StartsWith("texture:")).ToArray();
            Assert.Equal(5, textureKeys.Length);

            // Verify masked material texture bindings contain parseable GUIDs (not file paths)
            foreach (TextureBinding binding in maskedAsset.Textures!)
            {
                Assert.True(AssetGuid.TryParse(binding.TextureGuid, out AssetGuid parsedGuid), $"TextureBinding '{binding.Name}' should contain a valid GUID, got: '{binding.TextureGuid}'");
                Assert.False(parsedGuid.IsEmpty, $"TextureBinding '{binding.Name}' GUID should not be empty");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string WriteTestProject(string dir, bool writeImporterSettings)
    {
        string templateDir = Path.Combine(dir, "assets", "Materials", "Templates");
        Directory.CreateDirectory(templateDir);
        WriteTemplate(Path.Combine(templateDir, "PbrTemplate.material.asset"), "LitTemplate", "lit-shader-guid");
        WriteTemplate(Path.Combine(templateDir, "UnlitTemplate.material.asset"), "UnlitTemplate", "unlit-shader-guid");
        WriteTemplate(Path.Combine(dir, GltfImporterSettings.DefaultLitMaterialTemplate), "DefaultPBR", "lit-shader-guid");
        WriteTemplate(Path.Combine(dir, GltfImporterSettings.DefaultUnlitMaterialTemplate), "TestUnlit_1", "unlit-shader-guid");

        string modelDir = Path.Combine(dir, "assets", "Models");
        string textureDir = Path.Combine(modelDir, "textures");
        Directory.CreateDirectory(textureDir);
        WriteTinyPng(Path.Combine(textureDir, "albedo.png"));
        WriteTinyPng(Path.Combine(textureDir, "metallic_roughness.png"));
        WriteTinyPng(Path.Combine(textureDir, "normal.png"));
        WriteTinyPng(Path.Combine(textureDir, "occlusion.png"));
        WriteTinyPng(Path.Combine(textureDir, "emissive.png"));

        string gltfPath = Path.Combine(modelDir, "character.gltf");
        string binPath = Path.Combine(modelDir, "character.bin");
        File.WriteAllBytes(binPath, BuildTriangleBuffer());
        File.WriteAllText(gltfPath, BuildGltfJson(), new UTF8Encoding(false));

        if (writeImporterSettings)
        {
            JsonElement settings = JsonDocument.Parse(
                """
                {
                  "lit_material_template": "assets/Materials/Templates/PbrTemplate.material.asset",
                  "unlit_material_template": "assets/Materials/Templates/UnlitTemplate.material.asset"
                }
                """).RootElement.Clone();

            SourceMetaManager.Save(gltfPath, new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = "GltfSourceImporter",
                ImporterSettings = settings,
            });
        }

        return gltfPath;
    }

    private static void WriteTemplate(string path, string name, string shaderGuid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MaterialAssetSerializer.Save(new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = name,
            Passes =
            [
                new PassEntry
                {
                    ShaderGuid = shaderGuid,
                    Tags =
                    [
                        new TagEntry { Name = "opaque" },
                    ],
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = nameof(OverlayShade),
                            Json = "{\"Layer\":5}",
                        },
                    ],
                },
            ],
            Textures = [],
            Scalars = [],
        }, path);
    }

    private static byte[] BuildTriangleBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WritePositions(writer, new[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f,
        });
        WriteIndices(writer, new ushort[] { 0, 1, 2 });
        writer.Write((ushort)0); // pad to 4-byte alignment

        WritePositions(writer, new[]
        {
            0f, 0f, 1f,
            1f, 0f, 1f,
            0f, 1f, 1f,
        });
        WriteIndices(writer, new ushort[] { 0, 1, 2 });

        writer.Flush();
        return stream.ToArray();
    }

    private static void WritePositions(BinaryWriter writer, float[] values)
    {
        foreach (float value in values)
        {
            writer.Write(value);
        }
    }

    private static void WriteIndices(BinaryWriter writer, ushort[] values)
    {
        foreach (ushort value in values)
        {
            writer.Write(value);
        }
    }

    private static string BuildGltfJson()
    {
        return
            """
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [
                { "nodes": [0, 1] }
              ],
              "nodes": [
                { "mesh": 0 },
                { "mesh": 1 }
              ],
              "buffers": [
                { "uri": "character.bin", "byteLength": 86 }
              ],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
                { "buffer": 0, "byteOffset": 36, "byteLength": 6, "target": 34963 },
                { "buffer": 0, "byteOffset": 44, "byteLength": 36, "target": 34962 },
                { "buffer": 0, "byteOffset": 80, "byteLength": 6, "target": 34963 }
              ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" },
                { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 1], "max": [1, 1, 1] },
                { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ],
              "images": [
                { "uri": "textures/albedo.png" },
                { "uri": "textures/metallic_roughness.png" },
                { "uri": "textures/normal.png" },
                { "uri": "textures/occlusion.png" },
                { "uri": "textures/emissive.png" }
              ],
              "textures": [
                { "source": 0 },
                { "source": 1 },
                { "source": 2 },
                { "source": 3 },
                { "source": 4 }
              ],
              "materials": [
                {
                  "name": "MaskedLit",
                  "pbrMetallicRoughness": {
                    "baseColorFactor": [0.5, 0.25, 1.0, 1.0],
                    "baseColorTexture": { "index": 0 },
                    "metallicFactor": 0.75,
                    "roughnessFactor": 0.35,
                    "metallicRoughnessTexture": { "index": 1 }
                  },
                  "normalTexture": { "index": 2 },
                  "occlusionTexture": { "index": 3 },
                  "emissiveFactor": [0.2, 0.4, 0.6],
                  "emissiveTexture": { "index": 4 },
                  "alphaMode": "MASK",
                  "alphaCutoff": 0.33,
                  "doubleSided": true
                },
                {
                  "name": "TransparentUnlit",
                  "extensions": {
                    "KHR_materials_unlit": {}
                  },
                  "pbrMetallicRoughness": {
                    "baseColorFactor": [1.0, 1.0, 1.0, 0.5],
                    "baseColorTexture": { "index": 0 }
                  },
                  "alphaMode": "BLEND"
                }
              ],
              "meshes": [
                {
                  "name": "Body",
                  "primitives": [
                    {
                      "attributes": { "POSITION": 0 },
                      "indices": 1,
                      "material": 0
                    }
                  ]
                },
                {
                  "name": "Eyes",
                  "primitives": [
                    {
                      "attributes": { "POSITION": 2 },
                      "indices": 3,
                      "material": 1
                    }
                  ]
                }
              ],
              "extensionsUsed": [ "KHR_materials_unlit" ]
            }
            """;
    }

    private static void WriteTinyPng(string path)
    {
        const string tinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Z0ioAAAAASUVORK5CYII=";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(tinyPngBase64));
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
