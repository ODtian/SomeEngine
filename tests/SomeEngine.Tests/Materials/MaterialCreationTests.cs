using System;
using System.Collections.Generic;
using System.IO;
using FlatSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Assets;

namespace SomeEngine.Tests.Materials;

public class MaterialCreationTests
{
    [Fact(Skip = "Run this manually to generate default material assets into the project")]
    public void GenerateDefaultMaterials()
    {
        // 1. Resolve 'assets' directory
        string baseDir = AppContext.BaseDirectory;
        string assetsDir = Path.GetFullPath(Path.Combine(baseDir, "../../../../../../assets"));
        Assert.True(Directory.Exists(assetsDir), $"Assets directory not found at {assetsDir}");

        string shadersDir = Path.Combine(assetsDir, "Shaders");
        string materialsDir = Path.Combine(assetsDir, "Materials");
        Directory.CreateDirectory(materialsDir);

        // 2. Load or define GUIDs
        // We ensure we keep the same GUID if they exist, to avoid breaking links.
        string pbrShaderPath = Path.Combine(shadersDir, "cluster_shade_material.shader.asset");
        string unlitShaderPath = Path.Combine(shadersDir, "cluster_shade_unlit.shader.asset");

        AssetGuid pbrShaderGuid = LoadOrGenerateShaderAsset(pbrShaderPath, "cluster_shade_material");
        AssetGuid unlitShaderGuid = LoadOrGenerateShaderAsset(unlitShaderPath, "cluster_shade_unlit");

        // The default material assets now write ShaderGuid for every pass, so
        // all referenced shader assets need to exist on disk up front.
        AssetGuid swRasterShaderGuid = LoadOrGenerateShaderAsset(Path.Combine(shadersDir, "sw_raster.shader.asset"), "sw_raster");
        AssetGuid deformShaderGuid = LoadOrGenerateShaderAsset(Path.Combine(shadersDir, "cluster_deform.shader.asset"), "cluster_deform");

        // 3. Create DefaultPBR.mat
        var pbrAsset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(), // Ideally you would reuse an existing GUID if it existed
            Name = "DefaultPBR",
            Passes = new List<PassEntry>
            {
                new()
                {
                    ShaderGuid = pbrShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "opaque" },
                        new() { Name = "ClusterShader" },
                    }
                },
                new()
                {
                    ShaderGuid = swRasterShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "ClusterRaster" },
                    }
                },
                new()
                {
                    ShaderGuid = deformShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "VertexDeform" },
                    }
                },
            },
            Textures = new List<TextureBinding>
            {
                new() { Name = "AlbedoMap", Path = "default:white" },
                new() { Name = "NormalMap", Path = "default:normal" },
                new() { Name = "ARMMap", Path = "default:arm" },
            },
        };

        // 4. Create TestUnlit_1.mat
        var unlitAsset = new MaterialAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "TestUnlit_1",
            Passes = new List<PassEntry>
            {
                new()
                {
                    ShaderGuid = unlitShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "opaque" },
                        new() { Name = "ClusterShader" },
                    }
                },
                new()
                {
                    ShaderGuid = swRasterShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "ClusterRaster" },
                    }
                },
                new()
                {
                    ShaderGuid = deformShaderGuid.ToFlatString(),
                    Tags = new List<TagEntry>
                    {
                        new() { Name = "VertexDeform" },
                    }
                },
            },
            Textures = new List<TextureBinding>
            {
                new() { Name = "AlbedoMap", Path = "default:white" },
                new() { Name = "NormalMap", Path = "default:normal" },
                new() { Name = "ARMMap", Path = "default:arm" },
            },
        };

        // Function to preserve GUID if file exists
        string SaveMaterial(MaterialAsset asset, string filename)
        {
            string outPath = Path.Combine(materialsDir, filename);
            if (File.Exists(outPath))
            {
                var existing = MaterialAsset.Serializer.Parse(File.ReadAllBytes(outPath));
                if (!string.IsNullOrEmpty(existing.AssetGuid))
                {
                    asset.AssetGuid = existing.AssetGuid;
                }
            }

            int size = MaterialAsset.Serializer.GetMaxSize(asset);
            byte[] buf = new byte[size];
            int written = MaterialAsset.Serializer.Write(buf, asset);
            File.WriteAllBytes(outPath, buf.AsSpan(0, written).ToArray());
            return outPath;
        }

        string pbrPath = SaveMaterial(pbrAsset, "DefaultPBR.material.asset");
        string unlitPath = SaveMaterial(unlitAsset, "TestUnlit_1.material.asset");

        Console.WriteLine($"Generated PBR material at: {pbrPath}");
        Console.WriteLine($"Generated Unlit material at: {unlitPath}");
    }

    private AssetGuid LoadOrGenerateShaderAsset(string assetFilePath, string shaderName)
    {
        // Compile from source to ensure shader asset has all backend variants (DXIL + SPIR-V).
        // The importer will reuse existing GUID if the .shader.asset already exists.
        string shadersDir = Path.GetDirectoryName(assetFilePath)!;
        string slangPath = Path.Combine(shadersDir, shaderName + ".slang");

        if (!File.Exists(slangPath))
        {
            throw new FileNotFoundException($"Shader source not found: {slangPath}");
        }

        var shaderAsset = SomeEngine.Assets.Importers.SlangShaderImporter.Import(slangPath);
        return AssetGuid.Parse(shaderAsset.AssetGuid);
    }
}
