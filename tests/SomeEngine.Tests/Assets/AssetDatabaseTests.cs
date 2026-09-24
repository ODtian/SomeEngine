using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetDatabaseTests
{
    [Fact]
    public void Import_SourceShader_ThenLoad_BySourcePath_ReusesGuid()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/simple.slang");

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Shaders/simple.slang");

            ShaderAsset? first = db.Load<ShaderAsset>("assets/Shaders/simple.slang", "shader:main");
            ShaderAsset? second = db.Load<ShaderAsset>("assets/Shaders/simple.slang", "shader:main");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
            Assert.True(File.Exists(SourceMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.slang"))));
            Assert.True(File.Exists(AssetMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "simple.shader.asset"))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_BySourcePath_DoesNotImportSource()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/not_imported.slang");

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);

            ShaderAsset? loaded = db.Load<ShaderAsset>("assets/Shaders/not_imported.slang", "shader:main");

            Assert.Null(loaded);
            Assert.Null(db.Resolve("assets/Shaders/not_imported.slang", "shader:main"));
            Assert.False(File.Exists(SourceMetaFiles.GetMetaPath(Path.Combine(dir, "assets", "Shaders", "not_imported.slang"))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_BySourcePath_DoesNotReimport_WhenSourceIsUpToDate()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/up_to_date.slang");

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Shaders/up_to_date.slang");

            ShaderAsset? first = db.Load<ShaderAsset>("assets/Shaders/up_to_date.slang", "shader:main");
            Assert.NotNull(first);

            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            ShaderAsset? second = db.Load<ShaderAsset>("assets/Shaders/up_to_date.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Import_BySourcePath_Reimports_WhenSourceChanges()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/reimport.slang");

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Shaders/reimport.slang");

            ShaderAsset? first = db.Load<ShaderAsset>("assets/Shaders/reimport.slang", "shader:main");
            Assert.NotNull(first);

            string sourcePath = Path.Combine(dir, "assets", "Shaders", "reimport.slang");
            string manifestPath = Path.Combine(dir, "Library", "AssetManifest", AssetManifest.AssetIndexFileName);
            DateTime before = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(manifestPath, before);
            before = File.GetLastWriteTimeUtc(manifestPath);

            File.AppendAllText(sourcePath, "\n// force reimport");
            db.Import("assets/Shaders/reimport.slang");

            ShaderAsset? second = db.Load<ShaderAsset>("assets/Shaders/reimport.slang", "shader:main");
            DateTime after = File.GetLastWriteTimeUtc(manifestPath);

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
            Assert.True(after > before);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Import_BySourcePath_PreservesDeterministicShaderGuid_WhenImportedMetaIsMissing()
    {
        string dir = CreateTempDir();
        WriteSimpleShader(dir, "assets/Shaders/deterministic.slang");

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Shaders/deterministic.slang");
            ShaderAsset? first = db.Load<ShaderAsset>("assets/Shaders/deterministic.slang", "shader:main");
            Assert.NotNull(first);

            string importedPath = Path.Combine(dir, "assets", "Shaders", "deterministic.shader.asset");
            File.Delete(importedPath);
            File.Delete(AssetMetaFiles.GetMetaPath(importedPath));

            db.Import("assets/Shaders/deterministic.slang");
            ShaderAsset? second = db.Load<ShaderAsset>("assets/Shaders/deterministic.slang", "shader:main");

            Assert.NotNull(second);
            Assert.Equal(first!.AssetGuid, second!.AssetGuid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_ByGuid_ReturnsMaterialAsset_AfterImport()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "test.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "TestMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Materials/test.material.asset");
            MaterialAsset? asset = db.Load<MaterialAsset>(materialGuid);

            Assert.NotNull(asset);
            Assert.Equal("TestMaterial", asset!.Name);
            Assert.Equal(materialGuid.ToFlatString(), asset.AssetGuid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_ByGuid_RejectsPayloadGuidMismatch()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid manifestGuid = AssetGuid.New();
            AssetGuid payloadGuid = AssetGuid.New();
            const string path = "assets/Materials/mismatch.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = payloadGuid.ToFlatString(),
                Name = "MismatchMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(manifestGuid, "MismatchMaterial", path, nameof(MaterialAsset));
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);

            Assert.Throws<InvalidOperationException>(() => db.Load<MaterialAsset>(manifestGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_ByGuid_RejectsShaderAssetWithoutSerializedEntryPointReflection()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid shaderGuid = AssetGuid.New();
            SourceGuid sourceGuid = SourceGuid.New();
            string sourcePath = Path.Combine(dir, "assets", "Shaders", "stale.slang");
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "stale.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            File.WriteAllText(sourcePath, "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() {}");
            ShaderAssetCodec.Save(new ShaderAsset
            {
                AssetGuid = shaderGuid.ToFlatString(),
                Name = "stale",
                ImportTrace = new ImportTrace
                {
                    SourceGuid = sourceGuid.ToFlatString(),
                    SourcePath = sourcePath,
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "old",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
                Variants =
                [
                    new ShaderBytecode
                    {
                        Backend = "dxil",
                        Stage = ShaderStage.Compute,
                        EntryPoint = "CSMain",
                        Data = Array.Empty<byte>(),
                        ContentHash = "stale-cs",
                    },
                ],
                EntryPointAttributes = [],
                Reflections = [],
                EntryPointReflections = [],
                Metadata = new ShaderMetadata
                {
                    Tags = [],
                    MaterialBindings = [],
                    MaterialScalarLayouts = [],
                },
            }, shaderAssetPath);

            AssetManifest manifest = new();
            manifest.AddAsset(
                shaderGuid,
                "stale",
                "assets/Shaders/stale.shader.asset",
                nameof(ShaderAsset),
                sourceGuid,
                "shader:main");
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => db.Load<ShaderAsset>(shaderGuid));
            Assert.Contains("serialized entry-point reflection", ex.Message);
            Assert.Contains("does not compile or reflect source files", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CreateAsset_AssignsGuid_RegistersManifest_AndReusesExistingGuid()
    {
        string dir = CreateTempDir();

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            const string path = "assets/Materials/created.material.asset";

            AssetGuid firstGuid = db.CreateAsset(path, new MaterialAsset
            {
                Name = "CreatedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.False(firstGuid.IsEmpty);
            Assert.Equal(firstGuid, db.Resolve(path));
            MaterialAsset? firstLoad = db.Load<MaterialAsset>(firstGuid);
            Assert.NotNull(firstLoad);
            Assert.Equal(firstGuid.ToFlatString(), firstLoad!.AssetGuid);
            Assert.Equal("CreatedMaterial", firstLoad.Name);

            AssetGuid secondGuid = db.CreateAsset(path, new MaterialAsset
            {
                Name = "UpdatedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.Equal(firstGuid, secondGuid);
            MaterialAsset? secondLoad = db.Load<MaterialAsset>(firstGuid);
            Assert.NotNull(secondLoad);
            Assert.Equal("UpdatedMaterial", secondLoad!.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CreateAsset_UsesRegisteredGuid_WhenExistingFileGuidDiffers()
    {
        string dir = CreateTempDir();

        try
        {
            const string path = "assets/Materials/reconcile.material.asset";
            string fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            AssetGuid staleGuid = AssetGuid.New();
            AssetGuid fileGuid = AssetGuid.New();
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = fileGuid.ToFlatString(),
                Name = "ExistingFile",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, fullPath);

            AssetManifest manifest = new();
            manifest.AddAsset(staleGuid, "Stale", path, nameof(MaterialAsset));
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            AssetGuid createdGuid = db.CreateAsset(path, new MaterialAsset
            {
                Name = "Reconciled",
                Passes = [],
                Textures = [],
                Scalars = [],
            });

            Assert.Equal(staleGuid, createdGuid);
            Assert.Equal(staleGuid, db.Resolve(path));
            Assert.DoesNotContain(db.List(nameof(MaterialAsset)), record => record.Guid == fileGuid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void List_Filter_And_DependencyQueries_Work()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();
            AssetGuid meshGuid = AssetGuid.New();

            AssetManifest manifest = new();
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/a.shader.asset", nameof(ShaderAsset));
            manifest.AddAsset(materialGuid, "MaterialA", "assets/Materials/a.material.asset", nameof(MaterialAsset), dependencies: [shaderGuid]);
            manifest.AddAsset(meshGuid, "MeshA", "assets/Meshes/a.mesh.asset", nameof(MeshAsset), dependencies: [materialGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            IReadOnlyList<AssetManifestRecord> materials = db.List(nameof(MaterialAsset));

            Assert.Single(materials);
            Assert.Equal(materialGuid, materials[0].Guid);
            Assert.Equal(new[] { shaderGuid }, db.GetDependencies(materialGuid));
            Assert.Equal(new[] { meshGuid }, db.GetReferencers(materialGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Validate_ReportsOrphanMetaAndDanglingReference()
    {
        string dir = CreateTempDir();

        try
        {
            SourceGuid orphanSourceGuid = SourceGuid.New();
            SourceGuid validSourceGuid = SourceGuid.New();
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid missingShaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();

            // 创建 valid shader .asset 文件和 material .asset 文件
            string shaderAssetPath = Path.Combine(dir, "assets", "Shaders", "valid.shader.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(shaderAssetPath)!);
            ShaderAssetCodec.Save(new ShaderAsset
            {
                AssetGuid = shaderGuid.ToFlatString(),
                Name = "ShaderA",
                Metadata = new ShaderMetadata { Tags = [], MaterialBindings = [] },
                Variants = [],
                Reflections = [],
                ImportTrace = new ImportTrace
                {
                    SourceGuid = validSourceGuid.ToFlatString(),
                    SourcePath = "assets/Shaders/valid.slang",
                    SubAssetKey = "shader:main",
                    ContentFingerprint = "fp",
                    Dependencies = [],
                    ImporterVersion = 1,
                },
            }, shaderAssetPath);

            string materialAssetPath = Path.Combine(dir, "assets", "Materials", "dangling.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialAssetPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "DanglingMaterial",
                Passes = [new PassEntry { ShaderGuid = missingShaderGuid.ToFlatString() }],
                Textures = [],
                Scalars = [],
            }, materialAssetPath);

            // valid source 实际存在
            string validSourceFile = Path.Combine(dir, "assets", "Shaders", "valid.slang");
            File.WriteAllText(validSourceFile, "// shader");

            // 构建 manifest：orphan source 指向不存在的文件，material 依赖不存在的 shader guid
            AssetManifest manifest = new();
            manifest.AddSource(orphanSourceGuid, "assets/Shaders/orphan.slang");
            manifest.AddSource(validSourceGuid, "assets/Shaders/valid.slang");
            manifest.AddAsset(shaderGuid, "ShaderA", "assets/Shaders/valid.shader.asset", nameof(ShaderAsset), validSourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "DanglingMaterial", "assets/Materials/dangling.material.asset", nameof(MaterialAsset), dependencies: [missingShaderGuid]);
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            IReadOnlyList<AssetDiagnostic> diagnostics = db.Validate();

            Assert.Contains(diagnostics, static diagnostic => diagnostic.Kind == AssetDiagnosticKind.OrphanSourceMeta);
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == AssetDiagnosticKind.DanglingReference && diagnostic.AssetGuid == materialGuid && diagnostic.RelatedAssetGuid == missingShaderGuid);
            Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Kind == AssetDiagnosticKind.MissingAssetFile);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Import_AssetFile_RebuildsIndexAndResolveWorks()
    {
        string dir = CreateTempDir();

        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);

            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "imported.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "ImportedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            IReadOnlyList<AssetGuid> imported = db.Import("assets/Materials/imported.material.asset");

            Assert.Equal(new[] { materialGuid }, imported);
            Assert.Equal(materialGuid, db.Resolve("assets/Materials/imported.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AssetDatabase_IsDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(AssetDatabase)));
    }

    [Fact]
    public void AssetDatabase_DoesNotLazyRegister_AssetFiles_OnLoad()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "lazy.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "LazyMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            MaterialAsset? loaded = db.Load<MaterialAsset>("assets/Materials/lazy.material.asset");

            Assert.Null(loaded);
            Assert.Null(db.Resolve("assets/Materials/lazy.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
    [Fact]
    public void Constructor_WithNoManifest_StartsEmpty()
    {
        string dir = CreateTempDir();
        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Empty(db.List(nameof(MaterialAsset)));
            Assert.Empty(db.List(nameof(ShaderAsset)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ByGuid_ReturnsNull_WhenNotRegistered()
    {
        string dir = CreateTempDir();
        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            MaterialAsset? result = db.Load<MaterialAsset>(AssetGuid.New());
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnknownPath()
    {
        string dir = CreateTempDir();
        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Null(db.Resolve("assets/Materials/nonexistent.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetDependencies_ReturnsEmpty_ForUnknownGuid()
    {
        string dir = CreateTempDir();
        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Empty(db.GetDependencies(AssetGuid.New()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetReferencers_ReturnsEmpty_ForUnknownGuid()
    {
        string dir = CreateTempDir();
        try
        {
            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Empty(db.GetReferencers(AssetGuid.New()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Import_Idempotent_DoesNotDuplicate()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            string path = Path.Combine(dir, "assets", "Materials", "dup.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Dup",
                Passes = [], Textures = [], Scalars = [],
            }, path);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Materials/dup.material.asset");
            db.Import("assets/Materials/dup.material.asset");

            Assert.Single(db.List(nameof(MaterialAsset)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Import_MultipleAssets_AllResolvable()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid g1 = AssetGuid.New(), g2 = AssetGuid.New();
            string p1 = Path.Combine(dir, "assets", "Materials", "a.material.asset");
            string p2 = Path.Combine(dir, "assets", "Materials", "b.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(p1)!);

            MaterialAssetCodec.Save(new MaterialAsset { AssetGuid = g1.ToFlatString(), Name = "A", Passes = [], Textures = [], Scalars = [] }, p1);
            MaterialAssetCodec.Save(new MaterialAsset { AssetGuid = g2.ToFlatString(), Name = "B", Passes = [], Textures = [], Scalars = [] }, p2);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Materials/a.material.asset");
            db.Import("assets/Materials/b.material.asset");

            Assert.Equal(2, db.List(nameof(MaterialAsset)).Count);
            Assert.Equal(g1, db.Resolve("assets/Materials/a.material.asset"));
            Assert.Equal(g2, db.Resolve("assets/Materials/b.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Validate_Reports_MissingAssetFile()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            AssetManifest manifest = new();
            manifest.AddAsset(guid, "Ghost", "assets/Materials/ghost.material.asset", nameof(MaterialAsset));
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            IReadOnlyList<AssetDiagnostic> diagnostics = db.Validate();

            Assert.Contains(diagnostics, d => d.Kind == AssetDiagnosticKind.MissingAssetFile && d.AssetGuid == guid);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Constructor_LoadsExistingManifest()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            AssetManifest manifest = new();
            manifest.AddAsset(guid, "Pre", "assets/Materials/pre.material.asset", nameof(MaterialAsset));
            manifest.Save(Path.Combine(dir, "Library", "AssetManifest"));

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Single(db.List(nameof(MaterialAsset)));
            Assert.Equal(guid, db.Resolve("assets/Materials/pre.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ByPath_RequiresManifestRegistration()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid guid = AssetGuid.New();
            string path = Path.Combine(dir, "assets", "Materials", "lazy2.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = guid.ToFlatString(),
                Name = "Lazy2",
                Passes = [], Textures = [], Scalars = [],
            }, path);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            Assert.Null(db.Resolve("assets/Materials/lazy2.material.asset"));

            MaterialAsset? loaded = db.Load<MaterialAsset>("assets/Materials/lazy2.material.asset");
            Assert.Null(loaded);
            Assert.Null(db.Resolve("assets/Materials/lazy2.material.asset"));

            db.Import("assets/Materials/lazy2.material.asset");

            loaded = db.Load<MaterialAsset>("assets/Materials/lazy2.material.asset");
            Assert.NotNull(loaded);
            Assert.Equal(guid, db.Resolve("assets/Materials/lazy2.material.asset"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void WriteSimpleShader(string dir, string relativePath)
    {
        string fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath,
            """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void CSMain()
            {
            }
            """);
    }
}
