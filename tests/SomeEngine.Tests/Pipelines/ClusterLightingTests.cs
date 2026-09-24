using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;

namespace SomeEngine.Tests.Pipelines;

public sealed class ClusterLightingTests
{
    [Fact]
    public void ClusterLightingContractsExposeClusterLightGridResources()
    {
        Type[] renderTypes = typeof(LightBuffer).Assembly.GetTypes();
        string[] typeNames = [.. renderTypes.Select(static type => type.Name)];

        Assert.Contains("ClusterLightGrid", typeNames);
        Assert.Contains("LightGridUniforms", typeNames);

        Type lightGridType = Assert.Single(renderTypes, static type => type.Name == "LightGrid");
        string[] lightGridPropertyNames = [.. lightGridType.GetProperties().Select(static property => property.Name)];
        Assert.Contains("ClusterLightGrid", lightGridPropertyNames);
        Assert.Contains("LightIndexList", lightGridPropertyNames);
    }

    [Fact]
    public void MaterialShadePassBindsClusterLightGridResources()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "MaterialShadePass.cs"));

        Assert.Contains("UniformGpu<LightGridUniforms>", source);
        Assert.Contains("_lightBuffer.AddFrame(graph, sceneLights, lightVersion, shadeData, screenWidth, screenHeight, depthSliceCount)", source);
        Assert.Contains("builder.Read(lights.ClusterLightGrid", source);
        Assert.Contains("builder.Read(lights.LightIndexList", source);
        Assert.Contains("builder.Read(lightGridUniforms.Buffer", source);
        Assert.Contains("\"LightGridUniforms\" => CommonBindingKind.LightGridUniforms", source);
        Assert.Contains("\"ClusterLightGrid\" => CommonBindingKind.ClusterLightGrid", source);
        Assert.Contains("\"LightIndexList\" => CommonBindingKind.LightIndexList", source);
        Assert.Contains("\"LightCookieAtlas\" => CommonBindingKind.LightCookieAtlas", source);
        Assert.Contains("\"LightCookieSampler\" => CommonBindingKind.LightCookieSampler", source);
        Assert.Contains("GetLightCookieTexture(sceneLights, materialFallbacks, assets)", source);
        Assert.Contains("materialFallbacks.LightCookieSampler", source);
    }

    [Fact]
    public void ClusterShadePipelineDefines3DClusterLightGrid()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "assets",
            "Shaders",
            "cluster_shade_pipeline.slang"));

        Assert.Contains("StructuredBuffer<uint2> ClusterLightGrid", source);
        Assert.Contains("float4 LightGridZParams", source);
        Assert.Contains("uint DepthSliceCount", source);
        Assert.Contains("uint GetClusterIndex(uint2 pixelCoord, float3 surfacePosition)", source);
        Assert.Contains("mul(float4(surfacePosition, 1.0), Uniforms.View).z", source);
        Assert.Contains("sliceIndex * LightGridTileCount.y", source);
        Assert.Contains("uint LightLayerMask;", source);
        Assert.Contains("uint LayerMask;", source);
        Assert.Contains("int CookieIndex;", source);
        Assert.Contains("float CookieStrength;", source);
        Assert.Contains("float4x4 WorldToLightCookie;", source);
        Assert.Contains("float4 CookieScaleOffset;", source);
        Assert.Contains("Texture2DArray LightCookieAtlas", source);
        Assert.Contains("SamplerState LightCookieSampler", source);
    }

    [Fact]
    public void EmptySceneLightsUploadHasZeroCounts()
    {
        using var graph = new RenderGraph();
        graph.BeginFrame();

        uint depthSliceCount = new ClusterConfig().DepthSliceCount;
        object grid = AddLightGrid(graph, default, default, 32, 16, depthSliceCount);
        LightCounts counts = GetProperty<LightCounts>(grid, "Counts");
        RenderGraphHandle buffer = GetProperty<RenderGraphHandle>(grid, "Buffer");
        RenderGraphHandle clusterLightGrid = GetProperty<RenderGraphHandle>(grid, "ClusterLightGrid");
        RenderGraphHandle lightIndexList = GetProperty<RenderGraphHandle>(grid, "LightIndexList");
        object uniforms = GetRequiredPropertyValue(grid, "Uniforms");

        BufferDesc desc = graph.GetBufferDesc(buffer);
        BufferDesc clusterGridDesc = graph.GetBufferDesc(clusterLightGrid);
        BufferDesc indexListDesc = graph.GetBufferDesc(lightIndexList);
        int clusterLightGridSize = Marshal.SizeOf(RequiredRenderType("ClusterLightGrid"));

        Assert.Equal(0u, counts.DirectionalCount);
        Assert.Equal(0u, counts.PointCount);
        Assert.Equal(0u, counts.SpotCount);
        Assert.Equal(16u, GetUIntField(uniforms, "LightGridTileSizeX"));
        Assert.Equal(16u, GetUIntField(uniforms, "LightGridTileSizeY"));
        Assert.Equal(2u, GetUIntField(uniforms, "LightGridTileCountX"));
        Assert.Equal(1u, GetUIntField(uniforms, "LightGridTileCountY"));
        Assert.Equal(depthSliceCount, GetUIntField(uniforms, "DepthSliceCount"));
        Assert.Equal(new Vector4(0.0f, 1.0f, 1.0f, depthSliceCount), GetVector4Field(uniforms, "LightGridZParams"));
        Assert.Equal((ulong)GPULight.SizeInBytes, desc.SizeInBytes);
        Assert.Equal((uint)GPULight.SizeInBytes, desc.StrideInBytes);
        Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
        Assert.Equal((ulong)clusterLightGridSize * 2u * depthSliceCount, clusterGridDesc.SizeInBytes);
        Assert.Equal((uint)clusterLightGridSize, clusterGridDesc.StrideInBytes);
        Assert.True(clusterGridDesc.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(clusterGridDesc.BindFlags.HasFlag(BindFlags.CopyDestination));
        Assert.Equal((ulong)sizeof(uint), indexListDesc.SizeInBytes);
        Assert.Equal((uint)sizeof(uint), indexListDesc.StrideInBytes);
        Assert.True(indexListDesc.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(indexListDesc.BindFlags.HasFlag(BindFlags.CopyDestination));
    }

    [Fact]
    public void PacksSceneLightsInDirectionalPointSpotOrder()
    {
        Vector4 directionalCookieScaleOffset = new(0.5f, 0.5f, 0.0f, 0.0f);
        Matrix4x4 directionalWorldToCookie = Matrix4x4.CreateTranslation(1, 2, 3);
        Vector4 pointCookieScaleOffset = new(0.25f, 0.25f, 0.5f, 0.0f);
        Matrix4x4 pointWorldToCookie = Matrix4x4.CreateScale(2.0f);
        Vector4 spotCookieScaleOffset = new(0.125f, 0.125f, 0.75f, 0.0f);
        Matrix4x4 spotWorldToCookie = Matrix4x4.CreateRotationY(0.25f);
        DirectionalLight[] directional =
        [
            new(
                new Vector3(0, -1, 0),
                new Vector3(1, 0.9f, 0.8f),
                2.0f,
                0x00000002u,
                1,
                0.5f,
                directionalCookieScaleOffset,
                directionalWorldToCookie),
        ];
        PointLight[] point =
        [
            new(
                new Vector3(1, 2, 3),
                12.0f,
                new Vector3(0.4f, 0.5f, 0.6f),
                3.0f,
                0x00000004u,
                2,
                0.75f,
                pointCookieScaleOffset,
                pointWorldToCookie),
        ];
        SpotLight[] spot =
        [
            new(
                new Vector3(4, 5, 6),
                20.0f,
                new Vector3(0, -1, 1),
                0.95f,
                0.75f,
                new Vector3(0.7f, 0.8f, 0.9f),
                4.0f,
                0x00000008u,
                3,
                1.0f,
                spotCookieScaleOffset,
                spotWorldToCookie),
        ];
        var lights = new SceneLights(directional, point, spot);

        GPULight[] packed = LightBuffer.Pack(lights, out uint directionalCount, out uint pointCount, out uint spotCount);

        Assert.Equal(144, Marshal.SizeOf<GPULight>());
        Assert.Equal(1u, directionalCount);
        Assert.Equal(1u, pointCount);
        Assert.Equal(1u, spotCount);
        Assert.Equal(directional[0].Direction, packed[0].Direction);
        Assert.Equal(directional[0].Intensity, packed[0].Intensity);
        Assert.Equal(directional[0].LayerMask, packed[0].LayerMask);
        Assert.Equal(directional[0].CookieIndex, packed[0].CookieIndex);
        Assert.Equal(directional[0].CookieStrength, packed[0].CookieStrength);
        Assert.Equal(directional[0].WorldToLightCookie, packed[0].WorldToLightCookie);
        Assert.Equal(directional[0].CookieScaleOffset, packed[0].CookieScaleOffset);
        Assert.Equal(point[0].Position, packed[1].Position);
        Assert.Equal(point[0].Range, packed[1].Range);
        Assert.Equal(point[0].LayerMask, packed[1].LayerMask);
        Assert.Equal(point[0].CookieIndex, packed[1].CookieIndex);
        Assert.Equal(point[0].CookieStrength, packed[1].CookieStrength);
        Assert.Equal(point[0].WorldToLightCookie, packed[1].WorldToLightCookie);
        Assert.Equal(point[0].CookieScaleOffset, packed[1].CookieScaleOffset);
        Assert.Equal(spot[0].Direction, packed[2].Direction);
        Assert.Equal(spot[0].InnerConeCos, packed[2].InnerConeCos);
        Assert.Equal(spot[0].OuterConeCos, packed[2].OuterConeCos);
        Assert.Equal(spot[0].LayerMask, packed[2].LayerMask);
        Assert.Equal(spot[0].CookieIndex, packed[2].CookieIndex);
        Assert.Equal(spot[0].CookieStrength, packed[2].CookieStrength);
        Assert.Equal(spot[0].WorldToLightCookie, packed[2].WorldToLightCookie);
        Assert.Equal(spot[0].CookieScaleOffset, packed[2].CookieScaleOffset);
    }

    [Fact]
    public void ScratchLightGridMatchesReferenceGrid_ForLocalLights()
    {
        DirectionalLight[] directional =
        [
            new(new Vector3(0, -1, 0), Vector3.One, 1.0f),
        ];
        PointLight[] points =
        [
            new(new Vector3(0.0f, 0.0f, 0.5f), 0.75f, new Vector3(1, 0, 0), 2.0f),
            new(new Vector3(0.65f, -0.4f, 1.0f), 0.45f, new Vector3(0, 1, 0), 3.0f),
        ];
        SpotLight[] spots =
        [
            new(new Vector3(-0.5f, 0.25f, 1.5f), 0.8f, new Vector3(0, 0, -1), 0.9f, 0.7f, new Vector3(0, 0, 1), 4.0f),
        ];
        var lights = new SceneLights(directional, points, spots);
        var shadeData = new ShadeUniforms
        {
            View = Matrix4x4.Transpose(Matrix4x4.Identity),
            ViewProj = Matrix4x4.Transpose(Matrix4x4.Identity),
            CameraPos = new Vector3(0, 0, -3),
        };
        LightBuffer.Pack(lights, out uint directionalCount, out uint pointCount, out _);
        const uint width = 64;
        const uint height = 48;
        const uint depthSliceCount = 4;

        LightBuffer.BuildLightGrid(
            lights,
            shadeData,
            width,
            height,
            depthSliceCount,
            directionalCount,
            pointCount,
            out ClusterLightGrid[] expectedGrid,
            out uint[] expectedIndices,
            out LightGridUniforms expectedUniforms);

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var buffer = new LightBuffer(device);

        buffer.BuildLightGrid(
            lights,
            shadeData,
            width,
            height,
            depthSliceCount,
            directionalCount,
            pointCount,
            out ReadOnlySpan<ClusterLightGrid> actualGrid,
            out ReadOnlySpan<uint> actualIndices,
            out LightGridUniforms actualUniforms);

        Assert.Equal(expectedGrid, actualGrid.ToArray());
        Assert.Equal(expectedIndices, actualIndices.ToArray());
        Assert.Contains(actualGrid.ToArray(), static cell => cell.Count > 0);
        foreach (ClusterLightGrid cell in actualGrid)
        {
            for (uint i = 0; i < cell.Count; i++)
                Assert.NotEqual(0u, actualIndices[checked((int)(cell.Offset + i))]);
        }

        Assert.Equal(expectedUniforms.LightGridTileSizeX, actualUniforms.LightGridTileSizeX);
        Assert.Equal(expectedUniforms.LightGridTileSizeY, actualUniforms.LightGridTileSizeY);
        Assert.Equal(expectedUniforms.LightGridTileCountX, actualUniforms.LightGridTileCountX);
        Assert.Equal(expectedUniforms.LightGridTileCountY, actualUniforms.LightGridTileCountY);
        Assert.Equal(expectedUniforms.LightGridZParams, actualUniforms.LightGridZParams);
        Assert.Equal(expectedUniforms.DepthSliceCount, actualUniforms.DepthSliceCount);
    }

    [Fact]
    public void SceneLightConstructorsDefaultToAllLayersAndNoCookie()
    {
        DirectionalLight directional = new(new Vector3(0, -1, 0), Vector3.One, 1.0f);
        PointLight point = new(Vector3.Zero, 8.0f, Vector3.One, 1.0f);
        SpotLight spot = new(Vector3.Zero, 8.0f, new Vector3(0, -1, 0), 0.9f, 0.7f, Vector3.One, 1.0f);

        Assert.Equal(SceneLights.DefaultLightLayerMask, directional.LayerMask);
        Assert.Equal(SceneLights.NoCookie, directional.CookieIndex);
        Assert.Equal(1.0f, directional.CookieStrength);
        Assert.Equal(default(Vector4), directional.CookieScaleOffset);
        Assert.Equal(default(Matrix4x4), directional.WorldToLightCookie);
        Assert.Equal(SceneLights.DefaultLightLayerMask, point.LayerMask);
        Assert.Equal(SceneLights.NoCookie, point.CookieIndex);
        Assert.Equal(1.0f, point.CookieStrength);
        Assert.Equal(default(Vector4), point.CookieScaleOffset);
        Assert.Equal(default(Matrix4x4), point.WorldToLightCookie);
        Assert.Equal(SceneLights.DefaultLightLayerMask, spot.LayerMask);
        Assert.Equal(SceneLights.NoCookie, spot.CookieIndex);
        Assert.Equal(1.0f, spot.CookieStrength);
        Assert.Equal(default(Vector4), spot.CookieScaleOffset);
        Assert.Equal(default(Matrix4x4), spot.WorldToLightCookie);
    }

    [Fact]
    public void GPULightLayoutMatchesShaderBuffer()
    {
        Assert.Equal(144, Marshal.SizeOf<GPULight>());
        Assert.Equal(0, Marshal.OffsetOf<GPULight>(nameof(GPULight.Position)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<GPULight>(nameof(GPULight.Range)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<GPULight>(nameof(GPULight.Direction)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<GPULight>(nameof(GPULight.InnerConeCos)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<GPULight>(nameof(GPULight.Color)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<GPULight>(nameof(GPULight.Intensity)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<GPULight>(nameof(GPULight.OuterConeCos)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<GPULight>(nameof(GPULight.LayerMask)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<GPULight>(nameof(GPULight.CookieIndex)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<GPULight>(nameof(GPULight.CookieStrength)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<GPULight>(nameof(GPULight.WorldToLightCookie)).ToInt32());
        Assert.Equal(128, Marshal.OffsetOf<GPULight>(nameof(GPULight.CookieScaleOffset)).ToInt32());
    }

    [Fact]
    public void LightCountsLayoutMatchesShaderCBuffer()
    {
        Assert.Equal(16, Marshal.SizeOf<LightCounts>());
        Assert.Equal(0, Marshal.OffsetOf<LightCounts>(nameof(LightCounts.DirectionalCount)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<LightCounts>(nameof(LightCounts.PointCount)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<LightCounts>(nameof(LightCounts.SpotCount)).ToInt32());
    }

    [Fact]
    public void ClusterLightGridLayoutMatchesShaderBuffer()
    {
        Type clusterLightGridType = RequiredRenderType("ClusterLightGrid");

        Assert.Equal(8, Marshal.SizeOf(clusterLightGridType));
        Assert.Equal(0, Marshal.OffsetOf(clusterLightGridType, "Offset").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf(clusterLightGridType, "Count").ToInt32());
    }

    [Fact]
    public void LightGridUniformLayoutMatchesShaderCBuffer()
    {
        Type uniformsType = RequiredRenderType("LightGridUniforms");

        Assert.Equal(48, Marshal.SizeOf(uniformsType));
        Assert.Equal(0, Marshal.OffsetOf(uniformsType, "LightGridTileSizeX").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf(uniformsType, "LightGridTileSizeY").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(uniformsType, "LightGridTileCountX").ToInt32());
        Assert.Equal(12, Marshal.OffsetOf(uniformsType, "LightGridTileCountY").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(uniformsType, "LightGridZParams").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(uniformsType, "DepthSliceCount").ToInt32());
    }

    [Fact]
    public void ShadeUniformLayoutKeepsCameraAndMotionOffsets()
    {
        Assert.Equal(352, Marshal.OffsetOf<ShadeUniforms>(nameof(ShadeUniforms.CameraPos)).ToInt32());
        Assert.Equal(344, Marshal.OffsetOf<ShadeUniforms>(nameof(ShadeUniforms.LightLayerMask)).ToInt32());
        Assert.Equal(368, Marshal.OffsetOf<ShadeUniforms>(nameof(ShadeUniforms.HasPreviousFrame)).ToInt32());
        Assert.Equal(372, Marshal.OffsetOf<ShadeUniforms>(nameof(ShadeUniforms.WriteMotionVectors)).ToInt32());
        Assert.Equal(384, Marshal.SizeOf<ShadeUniforms>());
    }

    [Fact]
    public void MakeShadeDoesNotInjectDefaultLighting()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "ClusterPipeline.Runtime.cs"));

        Assert.Contains("SceneLights? sceneLights = null", source);
        Assert.Contains("SceneLights currentLights = sceneLights ?? renderWorld.SceneLights", source);
        Assert.Contains("MakeShade(camera, width, height, slots.ShadingBinCount, cameraHistory)", source);
        Assert.Contains("currentLights,", source);
        Assert.Contains("LightLayerMask = SceneLights.DefaultLightLayerMask", source);
        Assert.DoesNotContain("LightBuffer.Set", source);
        Assert.DoesNotContain("LightDir", source);
        Assert.DoesNotContain("AmbientColor", source);
        Assert.DoesNotContain("LightIntensity = 1.5f", source);
        Assert.DoesNotContain("new Vector3(0.3f, -1.0f, 0.5f)", source);
        Assert.DoesNotContain("new Vector3(0.15f, 0.15f, 0.15f)", source);
    }

    private static object AddLightGrid(
        RenderGraph graph,
        in SceneLights lights,
        in ShadeUniforms shadeData,
        uint screenWidth,
        uint screenHeight,
        uint depthSliceCount)
    {
        MethodInfo? addMethod = typeof(LightBuffer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SingleOrDefault(static method => method.Name == "Add" && method.GetParameters().Length == 6);
        Assert.True(
            addMethod != null,
            "LightBuffer.Add must accept RenderGraph, SceneLights, ShadeUniforms, screen width, screen height, and depth slice count.");

        object? result = addMethod!.Invoke(null, [graph, lights, shadeData, screenWidth, screenHeight, depthSliceCount]);
        Assert.NotNull(result);
        return result!;
    }

    private static T GetProperty<T>(object instance, string propertyName)
    {
        object value = GetRequiredPropertyValue(instance, propertyName);
        Assert.IsType<T>(value);
        return (T)value;
    }

    private static object GetRequiredPropertyValue(object instance, string propertyName)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName);
        Assert.True(property != null, $"{instance.GetType().Name} must expose '{propertyName}'.");

        object? value = property!.GetValue(instance);
        Assert.NotNull(value);
        return value!;
    }

    private static uint GetUIntField(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName);
        Assert.True(field != null, $"{instance.GetType().Name} must expose '{fieldName}'.");

        object? value = field!.GetValue(instance);
        Assert.True(value is uint, $"{instance.GetType().Name}.{fieldName} must be a uint field.");
        return (uint)value!;
    }

    private static Vector4 GetVector4Field(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName);
        Assert.True(field != null, $"{instance.GetType().Name} must expose '{fieldName}'.");

        object? value = field!.GetValue(instance);
        Assert.True(value is Vector4, $"{instance.GetType().Name}.{fieldName} must be a Vector4 field.");
        return (Vector4)value!;
    }

    private static Type RequiredRenderType(string typeName)
    {
        Type? type = typeof(LightBuffer).Assembly.GetTypes().SingleOrDefault(type => type.Name == typeName);
        Assert.True(type != null, $"SomeEngine.Render.Pipelines must define '{typeName}'.");
        return type!;
    }
}
