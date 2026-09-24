using System.Text.Json;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public sealed class GltfSourceImporter : IAssetImporter
{
    private static readonly string[] Extensions = [".gltf", ".glb"];
    public const uint ImporterVersion = 3;

    public string ImporterName => nameof(GltfSourceImporter);
    public IReadOnlyList<string> SourceExtensions => Extensions;

    public bool MatchesSourcePath(string sourcePath) =>
        SourceExtensions.Any(extension =>
            sourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
        );

    public AssetImportFingerprint? GetFingerprint(
        string projectRoot,
        string sourcePath,
        SourceMeta sourceMeta
    )
    {
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        return GltfDeps.Fingerprint(projectRoot, fullPath, sourceMeta, settings);
    }

    public IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath)
    {
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(fullPath, ImporterName);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        AssetImportFingerprint fingerprint =
            GltfDeps.Fingerprint(projectRoot, fullPath, sourceMeta, settings)
            ?? throw new FileNotFoundException(
                $"One or more GLTF dependencies for '{fullPath}' could not be found."
            );
        ModelRoot model = ModelRoot.Load(fullPath);

        var importedTextures = new List<ImportedAsset>();
        List<ImportedMaterialInfo> importedMaterials = ImportMaterials(
            projectRoot,
            fullPath,
            sourceMeta,
            settings,
            fingerprint,
            model,
            importedTextures
        );
        List<ImportedAsset> importedMeshes = ImportMeshes(
            projectRoot,
            fullPath,
            sourceMeta,
            settings,
            fingerprint,
            model,
            importedMaterials
        );

        return importedTextures
            .Concat(importedMaterials.Select(static info => info.ImportedAsset))
            .Concat(importedMeshes)
            .OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static GltfImporterSettings LoadSettings(SourceMeta sourceMeta, string fullPath)
    {
        if (!sourceMeta.ImporterSettings.HasValue)
        {
            return GltfImporterSettings.Default();
        }

        GltfImporterSettings? settings =
            sourceMeta.ImporterSettings.Value.Deserialize<GltfImporterSettings>(
                AssetIoHelpers.JsonOptions
            );
        if (settings == null)
        {
            throw new InvalidOperationException(
                $"Source '{fullPath}' contains invalid importer settings for {nameof(GltfSourceImporter)}."
            );
        }

        if (string.IsNullOrWhiteSpace(settings.LitMaterialTemplate))
            settings.LitMaterialTemplate = GltfImporterSettings.DefaultLitMaterialTemplate;
        if (string.IsNullOrWhiteSpace(settings.UnlitMaterialTemplate))
            settings.UnlitMaterialTemplate = GltfImporterSettings.DefaultUnlitMaterialTemplate;

        return settings;
    }

    private static List<ImportedMaterialInfo> ImportMaterials(
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        GltfImporterSettings settings,
        AssetImportFingerprint fingerprint,
        ModelRoot model,
        List<ImportedAsset> importedTextures
    )
    {
        MaterialAsset litTemplate = MaterialAssetCodec.Load(
            GltfDeps.FullPath(projectRoot, settings.LitMaterialTemplate)
        );
        MaterialAsset unlitTemplate = MaterialAssetCodec.Load(
            GltfDeps.FullPath(projectRoot, settings.UnlitMaterialTemplate)
        );
        string sourceStem = Path.GetFileNameWithoutExtension(fullSourcePath);
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;

        var imported = new List<ImportedMaterialInfo>(model.LogicalMaterials.Count);
        for (int index = 0; index < model.LogicalMaterials.Count; index++)
        {
            Material material = model.LogicalMaterials[index];
            string materialName = string.IsNullOrWhiteSpace(material.Name)
                ? $"Material_{index}"
                : material.Name;
            string safeName = SanitizeSegment(materialName, $"Material_{index}");
            string outputPath = Path.Combine(
                sourceDirectory,
                $"{sourceStem}.material.{index}.{safeName}.material.asset"
            );
            string subAssetKey = $"material:{index}:{safeName}";

            MaterialAsset template = material.Unlit ? unlitTemplate : litTemplate;
            AssetGuid assetGuid = AssetGuid.FromSource(sourceMeta.SourceGuid, subAssetKey);
            MaterialAsset asset = CloneMaterial(template, materialName, assetGuid);

            ApplyMaterialSemantics(
                asset,
                material,
                projectRoot,
                fullSourcePath,
                sourceMeta,
                fingerprint,
                importedTextures
            );
            MaterialAssetCodec.Save(asset, outputPath);
            GltfDeps.SaveMeta(
                outputPath,
                asset.AssetGuid,
                sourceMeta.SourceGuid,
                subAssetKey,
                fingerprint
            );

            imported.Add(
                new ImportedMaterialInfo(
                    index,
                    new MeshMaterialSlot(AssetGuid.Parse(asset.AssetGuid!)),
                    new ImportedAsset(asset, subAssetKey, outputPath)
                )
            );
        }

        return imported;
    }

    private static List<ImportedAsset> ImportMeshes(
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        GltfImporterSettings settings,
        AssetImportFingerprint fingerprint,
        ModelRoot model,
        IReadOnlyList<ImportedMaterialInfo> importedMaterials
    )
    {
        string sourceStem = Path.GetFileNameWithoutExtension(fullSourcePath);
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;
        var imported = new List<ImportedAsset>(model.LogicalMeshes.Count);

        foreach (Mesh mesh in model.LogicalMeshes)
        {
            int meshIndex = mesh.LogicalIndex;
            string meshName = string.IsNullOrWhiteSpace(mesh.Name)
                ? $"Mesh_{meshIndex}"
                : mesh.Name;
            string safeName = SanitizeSegment(meshName, $"Mesh_{meshIndex}");
            string outputPath = Path.Combine(
                sourceDirectory,
                $"{sourceStem}.mesh.{meshIndex}.{safeName}.mesh.asset"
            );
            string subAssetKey = $"mesh:{meshIndex}:{safeName}";

            IReadOnlyList<MeshMaterialSlot> materialSlots = mesh
                .Primitives.Select(static primitive => primitive.Material?.LogicalIndex ?? -1)
                .Select(index =>
                    index >= 0 && index < importedMaterials.Count
                        ? importedMaterials[index].MeshMaterialSlot
                        : new MeshMaterialSlot(AssetGuid.Empty)
                )
                .ToArray();

            MeshAsset meshAsset = ClusterBuilder.ProcessMesh(
                mesh,
                materialSlots,
                meshName,
                new ClusterBuilderOptions
                {
                    GenerateMissingTangents = settings.GenerateTangents,
                });
            AssetGuid assetGuid = AssetGuid.FromSource(sourceMeta.SourceGuid, subAssetKey);
            meshAsset.AssetGuid = assetGuid.ToFlatString();

            MeshAssetCodec.Save(meshAsset, outputPath);
            GltfDeps.SaveMeta(
                outputPath,
                meshAsset.AssetGuid,
                sourceMeta.SourceGuid,
                subAssetKey,
                fingerprint
            );
            imported.Add(new ImportedAsset(meshAsset, subAssetKey, outputPath));
        }

        return imported;
    }

    private static MaterialAsset CloneMaterial(
        MaterialAsset template,
        string materialName,
        AssetGuid assetGuid
    )
    {
        return new MaterialAsset
        {
            AssetGuid = assetGuid.IsEmpty
                ? AssetGuid.New().ToFlatString()
                : assetGuid.ToFlatString(),
            Name = materialName,
            Passes = template.Passes?.Select(ClonePass).ToList() ?? [],
            Textures = template.Textures?.Select(CloneTexture).ToList() ?? [],
            Scalars = template.Scalars?.Select(CloneScalar).ToList() ?? [],
        };
    }

    private static void ApplyMaterialSemantics(
        MaterialAsset asset,
        Material material,
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        AssetImportFingerprint fingerprint,
        List<ImportedAsset> importedTextures
    )
    {
        ApplySurfaceTags(asset, material);
        ApplyPbrScalars(asset, material);
        ApplyTextureBindings(
            asset,
            material,
            projectRoot,
            fullSourcePath,
            sourceMeta,
            fingerprint,
            importedTextures
        );
    }

    private static void ApplySurfaceTags(MaterialAsset asset, Material material)
    {
        string[] semantics = material.Alpha switch
        {
            AlphaMode.MASK => material.DoubleSided ? ["masked", "two_sided"] : ["masked"],
            AlphaMode.BLEND => material.DoubleSided
                ? ["translucent", "two_sided"]
                : ["translucent"],
            _ => material.DoubleSided ? ["opaque", "two_sided"] : ["opaque"],
        };

        foreach (PassEntry pass in asset.Passes ?? [])
        {
            List<TagEntry> tags = pass.Tags?.ToList() ?? [];
            tags.RemoveAll(static tag =>
                string.Equals(tag.Name, "opaque", StringComparison.Ordinal)
                || string.Equals(tag.Name, "masked", StringComparison.Ordinal)
                || string.Equals(tag.Name, "translucent", StringComparison.Ordinal)
                || string.Equals(tag.Name, "two_sided", StringComparison.Ordinal)
            );
            foreach (string semantic in semantics)
            {
                tags.Add(new TagEntry { Name = semantic });
            }

            pass.Tags = tags;
        }
    }

    private static void ApplyPbrScalars(MaterialAsset asset, Material material)
    {
        var baseColor = material.FindChannel("BaseColor");
        var metallicRoughness = material.FindChannel("MetallicRoughness");
        var emissive = material.FindChannel("Emissive");

        SetScalar(
            asset,
            "BaseColorTint",
            new ParamValue(
                new Vec4Val
                {
                    X = baseColor?.Parameter.X ?? 1.0f,
                    Y = baseColor?.Parameter.Y ?? 1.0f,
                    Z = baseColor?.Parameter.Z ?? 1.0f,
                    W = baseColor?.Parameter.W ?? 1.0f,
                }
            )
        );
        SetScalar(
            asset,
            "MetallicFactor",
            new ParamValue(new FloatVal { V = metallicRoughness?.Parameter.Y ?? 1.0f })
        );
        SetScalar(
            asset,
            "Roughness",
            new ParamValue(new FloatVal { V = metallicRoughness?.Parameter.X ?? 1.0f })
        );

        if (material.Alpha == AlphaMode.MASK)
        {
            SetScalar(
                asset,
                "AlphaCutoff",
                new ParamValue(new FloatVal { V = material.AlphaCutoff })
            );
        }

        SetScalar(
            asset,
            "EmissiveFactor",
            new ParamValue(
                new Vec3Val
                {
                    X = emissive?.Parameter.X ?? 0.0f,
                    Y = emissive?.Parameter.Y ?? 0.0f,
                    Z = emissive?.Parameter.Z ?? 0.0f,
                }
            )
        );
    }

    private static void ApplyTextureBindings(
        MaterialAsset asset,
        Material material,
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        AssetImportFingerprint fingerprint,
        List<ImportedAsset> importedTextures
    )
    {
        var baseColor = material.FindChannel("BaseColor");
        var normal = material.FindChannel("Normal");
        var metallicRoughness = material.FindChannel("MetallicRoughness");
        var occlusion = material.FindChannel("Occlusion");
        var emissive = material.FindChannel("Emissive");

        string? albedoGuid = ResolveTextureAsset(
            baseColor?.Texture,
            projectRoot,
            fullSourcePath,
            sourceMeta,
            fingerprint,
            importedTextures
        );
        string? normalGuid = ResolveTextureAsset(
            normal?.Texture,
            projectRoot,
            fullSourcePath,
            sourceMeta,
            fingerprint,
            importedTextures
        );
        string? armGuid =
            ResolveTextureAsset(
                metallicRoughness?.Texture,
                projectRoot,
                fullSourcePath,
                sourceMeta,
                fingerprint,
                importedTextures
            )
            ?? ResolveTextureAsset(
                occlusion?.Texture,
                projectRoot,
                fullSourcePath,
                sourceMeta,
                fingerprint,
                importedTextures
            );
        string? emissiveGuid = ResolveTextureAsset(
            emissive?.Texture,
            projectRoot,
            fullSourcePath,
            sourceMeta,
            fingerprint,
            importedTextures
        );

        if (!string.IsNullOrWhiteSpace(albedoGuid))
        {
            SetTexture(asset, "AlbedoMap", albedoGuid);
        }

        if (!string.IsNullOrWhiteSpace(normalGuid))
        {
            SetTexture(asset, "NormalMap", normalGuid);
        }

        if (!string.IsNullOrWhiteSpace(armGuid))
        {
            SetTexture(asset, "ARMMap", armGuid);
        }

        if (!string.IsNullOrWhiteSpace(emissiveGuid))
        {
            SetTexture(asset, "EmissiveMap", emissiveGuid);
        }
    }

    /// <summary>
    /// Extract texture from GLTF, produce a .texture.asset file, return the AssetGuid string.
    /// </summary>
    private static string? ResolveTextureAsset(
        Texture? texture,
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        AssetImportFingerprint fingerprint,
        List<ImportedAsset> importedTextures
    )
    {
        if (texture?.PrimaryImage == null)
        {
            return null;
        }

        Image image = texture.PrimaryImage;
        var content = image.Content;
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;
        string sourceStem = Path.GetFileNameWithoutExtension(fullSourcePath);
        string imageName = SanitizeSegment(image.Name, $"image_{texture.LogicalIndex}");
        string outputPath = Path.Combine(
            sourceDirectory,
            $"{sourceStem}.texture.{texture.LogicalIndex}.{imageName}.texture.asset"
        );
        string subAssetKey = $"texture:{texture.LogicalIndex}:{imageName}";

        // Get raw image bytes (PNG/JPG/etc.)
        byte[] imageBytes;
        if (!string.IsNullOrWhiteSpace(content.SourcePath))
        {
            imageBytes = File.ReadAllBytes(content.SourcePath!);
        }
        else
        {
            imageBytes = content.Content.ToArray();
        }

        AssetGuid assetGuid = AssetGuid.FromSource(sourceMeta.SourceGuid, subAssetKey);

        // Width/Height are unknown at import time because the payload stays compressed.
        var textureAsset = new TextureAsset
        {
            AssetGuid = assetGuid.ToFlatString(),
            Name = imageName,
            Width = 0,
            Height = 0,
            Format = "RGBA8_UNorm",
            Payload = imageBytes,
        };

        TextureAssetCodec.Save(textureAsset, outputPath);
        GltfDeps.SaveMeta(
            outputPath,
            textureAsset.AssetGuid,
            sourceMeta.SourceGuid,
            subAssetKey,
            fingerprint
        );
        importedTextures.Add(new ImportedAsset(textureAsset, subAssetKey, outputPath));

        return assetGuid.ToFlatString();
    }

    private static void SetTexture(MaterialAsset asset, string name, string textureGuid)
    {
        List<TextureBinding> textures = asset.Textures?.ToList() ?? [];
        int existingIndex = textures.FindIndex(binding =>
            string.Equals(binding.Name, name, StringComparison.Ordinal)
        );
        if (existingIndex >= 0)
        {
            textures[existingIndex].TextureGuid = textureGuid;
        }
        else
        {
            textures.Add(new TextureBinding { Name = name, TextureGuid = textureGuid });
        }

        asset.Textures = textures;
    }

    private static void SetScalar(MaterialAsset asset, string name, ParamValue value)
    {
        List<ScalarParam> scalars = asset.Scalars?.ToList() ?? [];
        int existingIndex = scalars.FindIndex(scalar =>
            string.Equals(scalar.Name, name, StringComparison.Ordinal)
        );
        ScalarParam scalarParam = new() { Name = name, Value = value };
        if (existingIndex >= 0)
        {
            scalars[existingIndex] = scalarParam;
        }
        else
        {
            scalars.Add(scalarParam);
        }

        asset.Scalars = scalars;
    }

    private static PassEntry ClonePass(PassEntry pass)
    {
        return new PassEntry
        {
            ShaderGuid = pass.ShaderGuid,
            EntryPoint = pass.EntryPoint,
            Tags =
                pass.Tags?.Select(tag => new TagEntry { Name = tag.Name, Value = tag.Value })
                    .ToList()
                ?? [],
            Components =
                pass.Components?.Select(static component => new ComponentEntry
                    {
                        TypeName = component.TypeName,
                        Json = component.Json,
                    })
                    .ToList()
                ?? [],
        };
    }

    private static TextureBinding CloneTexture(TextureBinding binding)
    {
        return new TextureBinding { Name = binding.Name, TextureGuid = binding.TextureGuid };
    }

    private static ScalarParam CloneScalar(ScalarParam scalar)
    {
        return new ScalarParam { Name = scalar.Name, Value = scalar.Value };
    }

    private static string SanitizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        string sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private readonly record struct ImportedMaterialInfo(
        int MaterialIndex,
        MeshMaterialSlot MeshMaterialSlot,
        ImportedAsset ImportedAsset
    );
}
