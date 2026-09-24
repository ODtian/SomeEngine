using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Data;

string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
string assetsDir = Path.Combine(projectRoot, "assets");
if (!Directory.Exists(assetsDir))
{
    throw new DirectoryNotFoundException($"Assets directory not found at '{assetsDir}'.");
}

string shadersDir = Path.Combine(assetsDir, "Shaders");
string texturesDir = Path.Combine(assetsDir, "Textures");
string materialsDir = Path.Combine(assetsDir, "Materials");
Directory.CreateDirectory(texturesDir);
Directory.CreateDirectory(materialsDir);
WriteGeneratedFile(
    Path.Combine(shadersDir, "generated", "instance_header_layout.slang"),
    InstanceHeaderLayout.SlangSource);

using AssetDatabase assetDb = AssetCatalog.CreateDatabase(projectRoot);

if (args.Contains("--stamp-vertex-layout-abi", StringComparer.Ordinal))
{
    Console.WriteLine("Shader vertex layout protocol metadata is authored on shader entries.");
    return;
}

if (args.Contains("--reimport-icosphere", StringComparer.Ordinal))
{
    assetDb.Import("samples/IcoSphere.glb");
    Console.WriteLine("Reimported samples/IcoSphere.glb.");
    return;
}

AssetGuid defaultWhiteTextureGuid = GenerateDefault1x1Texture(assetDb, "default_white", PackRgba(255, 255, 255, 255));
AssetGuid defaultNormalTextureGuid = GenerateDefault1x1Texture(
    assetDb,
    "default_normal",
    PackRgba(128, 128, 255, 255)
);
AssetGuid defaultArmTextureGuid = GenerateDefault1x1Texture(assetDb, "default_arm", PackRgba(255, 255, 255, 255));

string[] shaderSources =
[
    "bvh_patch.slang",
    "cluster_binning.slang",
    "cluster_bvh_traverse.slang",
    "cluster_cull.slang",
    "cluster_deform.slang",
    "cluster_deform_binning.slang",
    "cluster_draw.slang",
    "cluster_motion_vectors.slang",
    "cluster_resolve.slang",
    "cluster_shade_binning.slang",
    "cluster_shade_material.slang",
    "cluster_shade_unlit.slang",
    "debug_args_copy.slang",
    "debug_sphere.slang",
    "depth_merge.slang",
    "hiz_build.slang",
    "imgui.slang",
    "post_tonemap.slang",
    "sw_raster.slang",
    "temporal_resolve.slang",
];

foreach (string shaderName in shaderSources)
{
    string shaderPath = Path.Combine(shadersDir, shaderName);
    if (File.Exists(shaderPath))
    {
        assetDb.Import(shaderPath);
    }
}

GenerateClusterPipeline(assetDb, projectRoot);
GenerateMaterialTemplate(
    assetDb,
    "DefaultPBR",
    shadeShaderGuid: ResolveRequired(assetDb, "assets/Shaders/cluster_shade_material.slang"),
    shadeEntryPoint: "CSMaterialShadeCached",
    defaultWhiteTextureGuid,
    defaultNormalTextureGuid,
    defaultArmTextureGuid,
    pbrScalars: true
);
GenerateMaterialTemplate(
    assetDb,
    "TestUnlit_1",
    shadeShaderGuid: ResolveRequired(assetDb, "assets/Shaders/cluster_shade_unlit.slang"),
    shadeEntryPoint: "CSUnlitShadeCached",
    defaultWhiteTextureGuid,
    defaultNormalTextureGuid,
    defaultArmTextureGuid,
    pbrScalars: false
);

string icoSpherePath = Path.Combine(projectRoot, "samples", "IcoSphere.glb");
if (File.Exists(icoSpherePath))
{
    assetDb.Import(icoSpherePath);
}

var diagnostics = assetDb.Validate();
foreach (var diag in diagnostics)
{
    Console.WriteLine($"  [{diag.Severity}] {diag.Kind}: {diag.Message}");
}

if (diagnostics.Any(static d => d.Severity == AssetDiagnosticSeverity.Error))
{
    Console.Error.WriteLine("Asset validation failed with errors.");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"Generated default texture assets in: {texturesDir}");
Console.WriteLine($"Generated material templates in: {materialsDir}");
Console.WriteLine($"Generated shader assets in: {shadersDir}");
Console.WriteLine("Generated default render asset.");
Console.WriteLine("Manifest updated successfully.");

static string ResolveProjectRoot(string startPath)
{
    string current = Path.GetFullPath(startPath);
    while (!string.IsNullOrEmpty(current))
    {
        if (
            File.Exists(Path.Combine(current, "SomeEngine.slnx"))
            || File.Exists(Path.Combine(current, "Directory.Build.props"))
        )
        {
            return current;
        }

        string? parent = Path.GetDirectoryName(current);
        if (
            string.IsNullOrEmpty(parent)
            || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)
        )
        {
            break;
        }

        current = parent;
    }

    throw new DirectoryNotFoundException($"Could not locate project root from '{startPath}'.");
}

static void WriteGeneratedFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    string normalizedContent = content.Replace("\r\n", "\n");
    if (File.Exists(path) && string.Equals(File.ReadAllText(path), normalizedContent, StringComparison.Ordinal))
    {
        return;
    }

    File.WriteAllText(path, normalizedContent);
}

static AssetGuid GenerateDefault1x1Texture(AssetDatabase assetDb, string name, uint rgba)
{
    byte[] pixels =
    [
        (byte)(rgba & 0xFF),
        (byte)((rgba >> 8) & 0xFF),
        (byte)((rgba >> 16) & 0xFF),
        (byte)((rgba >> 24) & 0xFF),
    ];

    string outputPath = $"assets/Textures/{name}.texture.asset";
    var textureAsset = new TextureAsset
    {
        Name = name,
        Width = 1,
        Height = 1,
        Format = "RGBA8_UNorm",
        Payload = pixels,
    };

    AssetGuid guid = assetDb.CreateAsset(outputPath, textureAsset);
    Console.WriteLine($"  Texture: {outputPath}");
    return guid;
}

static uint PackRgba(byte r, byte g, byte b, byte a)
    => (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

static void GenerateMaterialTemplate(
    AssetDatabase assetDb,
    string name,
    AssetGuid shadeShaderGuid,
    string shadeEntryPoint,
    AssetGuid defaultWhiteTextureGuid,
    AssetGuid defaultNormalTextureGuid,
    AssetGuid defaultArmTextureGuid,
    bool pbrScalars
)
{
    string outputPath = $"assets/Materials/{name}.material.asset";
    AssetGuid swRasterShaderGuid = ResolveRequired(assetDb, "assets/Shaders/sw_raster.slang");
    AssetGuid hwDrawShaderGuid = ResolveRequired(assetDb, "assets/Shaders/cluster_draw.slang");
    AssetGuid deformShaderGuid = ResolveRequired(assetDb, "assets/Shaders/cluster_deform.slang");

    assetDb.CreateAsset(
        outputPath,
        new MaterialAsset
        {
            Name = name,
            Passes =
            [
                new PassEntry
                {
                    ShaderGuid = shadeShaderGuid.ToFlatString(),
                    Tags = [new TagEntry { Name = "opaque" }],
                },
                new PassEntry
                {
                    ShaderGuid = swRasterShaderGuid.ToFlatString(),
                    Tags = [new TagEntry { Name = "opaque" }],
                },
                new PassEntry
                {
                    ShaderGuid = hwDrawShaderGuid.ToFlatString(),
                    Tags = [new TagEntry { Name = "opaque" }],
                },
                new PassEntry
                {
                    ShaderGuid = deformShaderGuid.ToFlatString(),
                    EntryPoint = "CSDeformWave",
                    Tags = [new TagEntry { Name = "opaque" }],
                    Components =
                    [
                        new ComponentEntry
                        {
                            TypeName = "ClusterDeform",
                            Json = "{\"BoundsExpansion\":0.3}",
                        },
                    ],
                },
                new PassEntry
                {
                    ShaderGuid = deformShaderGuid.ToFlatString(),
                    EntryPoint = "CSDeformCacheRequestWave",
                    Tags = [new TagEntry { Name = "opaque" }],
                },
            ],
            Textures = CreateDefaultTextureBindings(
                defaultWhiteTextureGuid,
                defaultNormalTextureGuid,
                defaultArmTextureGuid,
                pbrScalars
            ),
            Scalars = CreateDefaultScalars(pbrScalars),
        }
    );
    Console.WriteLine($"  Material: {outputPath}");
}

static void GenerateClusterPipeline(AssetDatabase assetDb, string projectRoot)
{
    const string outputPath = "assets/Pipelines/default_cluster.clusterrender.asset";
    ClusterRenderAsset asset = new()
    {
        AssetGuid = ClusterRenderAssets.DefaultGuid.ToFlatString(),
        Name = "DefaultCluster",
        TemporalResolve = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/temporal_resolve.slang")),
        ClusterBinning = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_binning.slang")),
        ClusterBvhTraverse = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_bvh_traverse.slang")),
        ClusterCull = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_cull.slang")),
        ClusterDeformBinning = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_deform_binning.slang")),
        ClusterDeform = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_deform.slang")),
        ClusterDraw = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_draw.slang")),
        ClusterMotionVectors = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_motion_vectors.slang")),
        ClusterResolve = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_resolve.slang")),
        ClusterShadeBinning = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/cluster_shade_binning.slang")),
        DepthMerge = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/depth_merge.slang")),
        HizBuild = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/hiz_build.slang")),
        BvhPatch = ShaderRef(ResolveRequired(assetDb, "assets/Shaders/bvh_patch.slang")),
    };

    string fullPath = Path.Combine(projectRoot, outputPath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    ClusterRenderCodec.Save(asset, fullPath);
    var provider = new ClusterRenderProvider();
    assetDb.Manifest.AddAsset(
        ClusterRenderAssets.DefaultGuid,
        asset.Name ?? "DefaultCluster",
        outputPath,
        nameof(ClusterRenderAsset),
        dependencies: provider.GetDependencies(fullPath));
    assetDb.Manifest.Save(Path.Combine(projectRoot, "Library", "AssetManifest"));
    Console.WriteLine($"  ClusterPipeline: {outputPath}");
}

static ShaderAssetRef ShaderRef(AssetGuid shaderGuid)
    => new()
    {
        ShaderGuid = shaderGuid.ToFlatString(),
    };

static AssetGuid ResolveRequired(AssetDatabase assetDb, string sourcePath) =>
    assetDb.Resolve(sourcePath)
    ?? throw new InvalidOperationException($"Required asset '{sourcePath}' was not imported.");

static List<TextureBinding> CreateDefaultTextureBindings(
    AssetGuid defaultWhiteTextureGuid,
    AssetGuid defaultNormalTextureGuid,
    AssetGuid defaultArmTextureGuid,
    bool pbr
)
{
    List<TextureBinding> textures =
    [
        new() { Name = "AlbedoMap", TextureGuid = defaultWhiteTextureGuid.ToFlatString() },
    ];

    if (pbr)
    {
        textures.Add(
            new TextureBinding
            {
                Name = "NormalMap",
                TextureGuid = defaultNormalTextureGuid.ToFlatString(),
            }
        );
        textures.Add(
            new TextureBinding { Name = "ARMMap", TextureGuid = defaultArmTextureGuid.ToFlatString() }
        );
    }

    return textures;
}

static List<ScalarParam> CreateDefaultScalars(bool pbr)
{
    List<ScalarParam> scalars =
    [
        new()
        {
            Name = "BaseColorTint",
            Value = new ParamValue(
                new Vec4Val
                {
                    X = 1.0f,
                    Y = 1.0f,
                    Z = 1.0f,
                    W = 1.0f,
                }
            ),
        },
    ];

    if (pbr)
    {
        scalars.Add(
            new ScalarParam
            {
                Name = "MetallicFactor",
                Value = new ParamValue(new FloatVal { V = 0.0f }),
            }
        );
        scalars.Add(
            new ScalarParam
            {
                Name = "Roughness",
                Value = new ParamValue(new FloatVal { V = 0.5f }),
            }
        );
        scalars.Add(
            new ScalarParam
            {
                Name = "EmissiveFactor",
                Value = new ParamValue(
                    new Vec3Val
                    {
                        X = 0.0f,
                        Y = 0.0f,
                        Z = 0.0f,
                    }
                ),
            }
        );
    }

    return scalars;
}
