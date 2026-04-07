using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
string assetsDir = Path.Combine(projectRoot, "assets");
if (!Directory.Exists(assetsDir))
{
    throw new DirectoryNotFoundException($"Assets directory not found at '{assetsDir}'.");
}

string shadersDir = Path.Combine(assetsDir, "Shaders");
string materialsDir = Path.Combine(assetsDir, "Materials");
Directory.CreateDirectory(materialsDir);

AssetGuid pbrShaderGuid = EnsureShaderAsset(shadersDir, "cluster_shade_material");
AssetGuid unlitShaderGuid = EnsureShaderAsset(shadersDir, "cluster_shade_unlit");
_ = EnsureShaderAsset(shadersDir, "sw_raster");
_ = EnsureShaderAsset(shadersDir, "cluster_deform");

string pbrPath = SaveMaterial(CreatePbrMaterial(pbrShaderGuid), Path.Combine(materialsDir, "DefaultPBR.material.asset"));
string unlitPath = SaveMaterial(CreateUnlitMaterial(unlitShaderGuid), Path.Combine(materialsDir, "TestUnlit_1.material.asset"));

Console.WriteLine($"Generated shader assets in: {shadersDir}");
Console.WriteLine($"Generated material asset: {pbrPath}");
Console.WriteLine($"Generated material asset: {unlitPath}");

static string ResolveProjectRoot(string startPath)
{
    string current = Path.GetFullPath(startPath);
    while (!string.IsNullOrEmpty(current))
    {
        if (File.Exists(Path.Combine(current, "SomeEngine.slnx")) ||
            File.Exists(Path.Combine(current, "Directory.Build.props")))
        {
            return current;
        }

        string? parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        current = parent;
    }

    throw new DirectoryNotFoundException($"Could not locate project root from '{startPath}'.");
}

static AssetGuid EnsureShaderAsset(string shadersDir, string shaderName)
{
    string shaderSourcePath = Path.Combine(shadersDir, $"{shaderName}.slang");
    if (!File.Exists(shaderSourcePath))
    {
        throw new FileNotFoundException($"Shader source not found: {shaderSourcePath}", shaderSourcePath);
    }

    ShaderAsset shaderAsset = SlangShaderImporter.Import(shaderSourcePath);
    if (!AssetGuid.TryParse(shaderAsset.AssetGuid, out AssetGuid shaderGuid) || shaderGuid.IsEmpty)
    {
        throw new InvalidOperationException($"Shader '{shaderName}' did not produce a valid asset GUID.");
    }

    return shaderGuid;
}

static MaterialAsset CreatePbrMaterial(AssetGuid shaderGuid)
{
    return new MaterialAsset
    {
        AssetGuid = AssetGuid.New().ToFlatString(),
        Name = "DefaultPBR",
        Passes =
        [
            CreateClusterShaderPass(shaderGuid, "cluster_shade_material"),
            CreateTagOnlyPass("sw_raster", "ClusterRaster"),
            CreateTagOnlyPass("cluster_deform", "VertexDeform"),
        ],
        Textures = CreateDefaultTextureBindings(),
    };
}

static MaterialAsset CreateUnlitMaterial(AssetGuid shaderGuid)
{
    return new MaterialAsset
    {
        AssetGuid = AssetGuid.New().ToFlatString(),
        Name = "TestUnlit_1",
        Passes =
        [
            CreateClusterShaderPass(shaderGuid, "cluster_shade_unlit"),
            CreateTagOnlyPass("sw_raster", "ClusterRaster"),
            CreateTagOnlyPass("cluster_deform", "VertexDeform"),
        ],
        Textures = CreateDefaultTextureBindings(),
    };
}

static PassEntry CreateClusterShaderPass(AssetGuid shaderGuid, string shaderName)
{
    return new PassEntry
    {
        ShaderGuid = shaderGuid.ToFlatString(),
        Shader = shaderName,
        Tags =
        [
            new TagEntry { Name = "opaque" },
            new TagEntry { Name = "ClusterShader" },
        ],
    };
}

static PassEntry CreateTagOnlyPass(string shaderName, string tagName)
{
    return new PassEntry
    {
        Shader = shaderName,
        Tags =
        [
            new TagEntry { Name = tagName },
        ],
    };
}

static List<TextureBinding> CreateDefaultTextureBindings()
{
    return
    [
        new TextureBinding { Name = "AlbedoMap", Path = "default:white" },
        new TextureBinding { Name = "NormalMap", Path = "default:normal" },
        new TextureBinding { Name = "ARMMap", Path = "default:arm" },
    ];
}

static string SaveMaterial(MaterialAsset asset, string path)
{
    if (File.Exists(path))
    {
        MaterialAsset existing = MaterialAssetSerializer.Load(path);
        if (!string.IsNullOrWhiteSpace(existing.AssetGuid))
        {
            asset.AssetGuid = existing.AssetGuid;
        }
    }

    MaterialAssetSerializer.Save(asset, path);
    return path;
}
