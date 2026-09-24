using System.Text.RegularExpressions;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Tests;

namespace SomeEngine.Tests.Materials;

public class MaterialShaderCompilationTests
{
    [Theory]
    [InlineData("cluster_shade_material.slang")]
    [InlineData("cluster_shade_unlit.slang")]
    public void MaterialShadeShader_ImportsSuccessfully(string shaderName)
    {
        string path = TestProjectPaths.ShaderPath(shaderName);

        var asset = SlangShaderImporter.ImportTransient(path);

        Assert.NotNull(asset);
        Assert.NotEmpty(asset.Variants!);
        string[] expectedMaterialResources = shaderName == "cluster_shade_material.slang"
            ? ["AlbedoMap", "NormalMap", "ARMMap", "MaterialSampler"]
            : ["AlbedoMap", "MaterialSampler"];
        foreach (string resourceName in expectedMaterialResources)
        {
            Assert.Contains(asset.Metadata!.MaterialBindings!, r => r.Name == resourceName);
        }

        string[] expectedShaderResources = shaderName == "cluster_shade_material.slang"
            ? [.. expectedMaterialResources, "LightBuffer", "LightCounts", "ClusterLightGrid", "LightIndexList", "LightGridUniforms", "LightCookieAtlas", "LightCookieSampler"]
            : expectedMaterialResources;
        foreach (var backend in asset.Reflections!)
        {
            foreach (string resourceName in expectedShaderResources)
            {
                Assert.Contains(backend.Reflection!.Resources!, r => r.Name == resourceName);
            }

        }

        if (shaderName == "cluster_shade_material.slang")
        {
            var dxil = Assert.Single(asset.Reflections!, r => r.Backend == "dxil");
            var resources = dxil.Reflection!.Resources!;
            Assert.Contains(resources, r => r.Name == "AlbedoMap");
            Assert.Contains(resources, r => r.Name == "NormalMap");
            Assert.Contains(resources, r => r.Name == "ARMMap");
            Assert.Contains(resources, r => r.Name == "ClusterLightGrid");
            Assert.Contains(resources, r => r.Name == "LightIndexList");
            Assert.Contains(resources, r => r.Name == "LightGridUniforms" && r.BindingType == ShaderBindingType.ConstantBuffer);
            Assert.Contains(resources, r => r.Name == "LightCounts" && r.BindingType == ShaderBindingType.ConstantBuffer);
            Assert.Contains(resources, r => r.Name == "LightBuffer" && r.BindingType == ShaderBindingType.StorageBufferRead);
            Assert.Contains(resources, r => r.Name == "LightCookieAtlas" && r.BindingType == ShaderBindingType.TextureRead);
            Assert.Contains(resources, r => r.Name == "LightCookieSampler" && r.BindingType == ShaderBindingType.Sampler);
        }
        else
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("GetDefaultUnlitScalars", source);
            Assert.Contains("LoadMaterialScalars<UnlitScalars>()", source);
        }
    }

    [Fact]
    public void MaterialShadeShader_ReflectsMaterialScalarLayout()
    {
        string path = TestProjectPaths.ShaderPath("cluster_shade_material.slang");

        var asset = SlangShaderImporter.ImportTransient(path);

        var layout = Assert.Single(asset.Metadata!.MaterialScalarLayouts!);
        Assert.Equal("StandardPBRScalars", layout.Name);
        Assert.Contains(layout.Fields!, field => field.Name == "BaseColorTint" && field.Offset == 0 && field.Size == 16);
        Assert.Contains(layout.Fields!, field => field.Name == "MetallicFactor");
        Assert.Contains(layout.Fields!, field => field.Name == "Roughness");
        Assert.Contains(layout.Fields!, field => field.Name == "EmissiveFactor");
        Assert.True(layout.Size >= layout.Fields!.Max(field => field.Offset + field.Size));
    }

    [Fact]
    public void Brdf_DoesNotAmplifyBackFacingViewVectors()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("brdf.slang"));

        Assert.Contains("float rawNdotV = dot(N, V);", source);
        Assert.Contains("if (rawNdotV <= 0.0)", source);
        Assert.Contains("return float3(0.0);", source);
        Assert.Contains("static const float MIN_N_DOT_V = 0.05;", source);
        Assert.Contains("float NdotV = max(rawNdotV, MIN_N_DOT_V);", source);
        Assert.Contains("float3 EvaluatePointLight(", source);
        Assert.Contains("float3 EvaluateSpotLight(", source);
        Assert.Contains("float EvaluateSpotCone(", source);
    }

    [Fact]
    public void StandardPBR_UsesClusterLightIndicesWithoutDefaultAmbient()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("standard_pbr.slang"));
        string materialInterfaces = File.ReadAllText(TestProjectPaths.ShaderPath("material_interfaces.slang"));

        Assert.Contains("ClusterLightGrid", source);
        Assert.Contains("LightIndexList", source);
        Assert.Contains("GetClusterIndex(", source);
        Assert.Contains("LIGHT_GRID_INVALID_CLUSTER", source);
        Assert.Contains("LightIndexList[", source);
        Assert.Contains("static const uint DEFAULT_LIGHT_LAYER_MASK = 0xFFFFFFFFu;", source);
        Assert.Contains("static const int NO_COOKIE = -1;", source);
        Assert.Contains("uint surfaceLightLayerMask = Uniforms.LightLayerMask != 0u ? Uniforms.LightLayerMask : DEFAULT_LIGHT_LAYER_MASK;", source);
        Assert.Contains("if ((light.LayerMask & surfaceLightLayerMask) == 0u)", source);
        Assert.Contains("light.CookieIndex", source);
        Assert.Contains("light.CookieStrength", source);
        Assert.Contains("float SampleLightCookie(GPULight light, float3 surfacePosition)", source);
        Assert.Contains("LightCookieAtlas.SampleLevel", source);
        Assert.Contains("LightCookieSampler", source);
        Assert.Contains("light.WorldToLightCookie", source);
        Assert.Contains("light.CookieScaleOffset", source);
        Assert.Contains("float cookieAttenuation = SampleLightCookie(light, ctx.surfacePosition);", source);
        Assert.DoesNotContain("light.CookieIndex == NO_COOKIE ? 1.0 : saturate(light.CookieStrength)", source);
        Assert.DoesNotContain("TileLightGrid", source);
        Assert.Contains("for (uint lightIndex = 0u; lightIndex < directionalEnd; lightIndex++)", source);
        Assert.Contains("if (lightIndex < directionalEnd || lightIndex >= spotEnd)", source);
        Assert.False(
            Regex.IsMatch(source, @"for\s*\([^)]*LightCounts\.(PointCount|SpotCount)", RegexOptions.Multiline),
            "Standard PBR final shading must use cluster light indices for point and spot lights.");
        Assert.Contains("EvaluateDirectionalLight(", source);
        Assert.Contains("-normalize(light.Direction)", source);
        Assert.Contains("EvaluatePointLight(", source);
        Assert.Contains("EvaluateSpotLight(", source);
        Assert.Contains("light.Color * light.Intensity * cookieAttenuation", source);
        Assert.DoesNotContain("light.Kind", source);
        Assert.Contains("float3 finalColor = directColor + emissive;", source);
        Assert.DoesNotContain("Uniforms.LightDir", source);
        Assert.DoesNotContain("Uniforms.LightIntensity", source);
        Assert.DoesNotContain("Uniforms.AmbientColor", source);
        Assert.DoesNotContain("groundColor", source);
        Assert.DoesNotContain("skyColor", source);
        Assert.DoesNotContain("GetDefaultStandardPBRScalars", source);
        Assert.DoesNotMatch(
            @"LoadMaterialScalars<StandardPBRScalars>\s*\(\s*[^)]",
            source);
        Assert.Contains("LoadMaterialScalars<StandardPBRScalars>()", source);
        Assert.Contains("T LoadMaterialScalars<T>()", materialInterfaces);
        Assert.DoesNotContain("GetMaterialScalarPayloadByteSize", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarLayoutHash", materialInterfaces);
        Assert.DoesNotContain("T LoadMaterialScalars<T>(T defaultValue)", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarBytesRaw", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarBytes<T>", materialInterfaces);
    }

}
