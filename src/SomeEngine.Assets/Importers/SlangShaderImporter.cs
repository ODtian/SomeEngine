using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = 15;

    [ThreadStatic]
    private static IGlobalSession? t_globalSession;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (Schema.ShaderAsset Asset, DateTime LastModified)
    > _cache = new();

    public static IGlobalSession GlobalSession
    {
        get
        {
            if (t_globalSession == null)
            {
                Slang.CreateGlobalSession(Slang.ApiVersion, out t_globalSession);
            }

            return t_globalSession;
        }
    }

    public static Schema.ShaderAsset Import(string filePath, string? source = null)
    {
        var sourceMeta = SourceMetaFiles.GetOrCreate(filePath);
        var existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(Path.GetFullPath(filePath), ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, source);
    }

    public static Schema.ShaderAsset ImportTransient(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        var sourceMeta = File.Exists(SourceMetaFiles.GetMetaPath(filePath))
            ? SourceMetaFiles.Load(filePath)
            : new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
            };

        var existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(filePath, ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, source: null, writeCache: false);
    }

    public static Schema.ShaderAsset Import(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        string? source = null,
        bool writeCache = true
    )
    {
        filePath = Path.GetFullPath(filePath);
        string cachePath = Path.ChangeExtension(filePath, ".shader.asset");
        string projectRoot = SlangDeps.ProjectRoot(filePath);
        string subAssetKey = "shader:main";
        AssetGuid assetGuid = SlangDeps.GuidFor(sourceMeta.SourceGuid, subAssetKey);
        if (existingAsset != null && existingAsset.AssetGuid != assetGuid)
        {
            existingAsset = null;
        }

        if (existingAsset != null && File.Exists(cachePath))
        {
            AssetImportFingerprint? historicalFingerprint = SlangDeps.Refresh(
                existingAsset.Dependencies,
                projectRoot,
                ImporterVersion
            );
            if (historicalFingerprint?.ContentFingerprint == existingAsset.ContentFingerprint)
            {
                if (
                    _cache.TryGetValue(filePath, out var memoryCached)
                    && SlangDeps.Matches(memoryCached.Asset, existingAsset)
                )
                {
                    return memoryCached.Asset;
                }

                var cachedAsset = ShaderAssetCodec.Load(cachePath);
                if (SlangDeps.Matches(cachedAsset, existingAsset))
                {
                    DateTime cacheTime = File.Exists(filePath)
                        ? File.GetLastWriteTime(filePath)
                        : File.GetLastWriteTime(cachePath);
                    _cache[filePath] = (cachedAsset, cacheTime);
                    return cachedAsset;
                }
            }
        }

        if (source == null)
        {
            source = File.ReadAllText(filePath);
        }

        string name = Path.GetFileNameWithoutExtension(filePath);
        // Slang documents a single global session as non-thread-safe.
        // We avoid a process-wide lock by pinning one long-lived global
        // session to each thread that touches Slang.
        var globalSession = GlobalSession;
        var dxilProfile = globalSession.FindProfile("sm_6_5");
        var spirvProfile = globalSession.FindProfile("glsl_460");

        var targets = new TargetDesc[]
        {
            new() { Format = SlangCompileTarget.Dxil, Profile = dxilProfile },
            new() { Format = SlangCompileTarget.Spirv, Profile = spirvProfile },
        };

        var options = new[]
        {
            new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
            new CompilerOptionEntry(
                CompilerOptionName.VulkanEmitReflection,
                CompilerOptionValue.FromInt(1, 0)
            ),
            new CompilerOptionEntry(
                CompilerOptionName.DebugInformation,
                CompilerOptionValue.FromInt(0, 0)
            ),
        };

        var sessionDesc = new SessionDesc
        {
            Targets = targets,
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
            SearchPaths = [Path.GetDirectoryName(filePath) ?? ""],
            CompilerOptionEntries = options,
        };

        globalSession.CreateSession(sessionDesc, out var session);

        // Load Module
        var sourceBlob = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        var module = session.LoadModuleFromSource(name, filePath, sourceBlob, out var diagnostics);

        if (module == null)
        {
            throw new Exception($"Failed to load module {name}: {GetString(diagnostics)}");
        }

        var dependencies = SlangDeps.Collect(module, filePath, projectRoot);
        string fingerprint = SlangDeps.Fingerprint(dependencies, ImporterVersion);

        int entryPointCount = module.GetDefinedEntryPointCount();

        var metadata = new ShaderMetadata
        {
            Tags = [],
            MaterialBindings = [],
            MaterialScalarLayouts = [],
        };

        var asset = new Schema.ShaderAsset
        {
            AssetGuid = assetGuid.ToFlatString(),
            Name = name,
            ImportTrace = new Schema.ImportTrace
            {
                SourceGuid = sourceMeta.SourceGuid.ToFlatString(),
                SourcePath = filePath,
                SubAssetKey = subAssetKey,
                ContentFingerprint = fingerprint,
                Dependencies = dependencies
                    .Select(static d => new Schema.DependencyEntry
                    {
                        Path = d.RelativePath,
                        ContentHash = d.ContentHash,
                    })
                    .ToList(),
                ImporterVersion = ImporterVersion,
            },
            Variants = [],
            EntryPointAttributes = [],
            Reflections = [],
            EntryPointReflections = [],
            EntryPointMetadata = [],
            Metadata = metadata,
        };

        var moduleRefl = module.GetModuleReflection();
        var materialScalarTypes = SlangMaterialMeta.ScalarTypes(
            moduleRefl,
            source,
            dependencies,
            projectRoot
        );
        for (uint i = 0; i < moduleRefl.Count; i++)
        {
            var decl = moduleRefl[(int)i];
            if (decl.Kind != DeclReflectionKind.Variable)
                continue;

            var v = decl.AsVariable();
            if (v == VariableReflection.Null)
                continue;

            if (v.Type.Kind == SlangTypeKind.ParameterBlock)
            {
                var elementType = v.Type.ElementType;

                for (uint a = 0; a < elementType.AttributeCount; a++)
                {
                    var attr = elementType.GetAttribute(a);
                    if (attr.Name == "PipelineTag" && attr.ArgumentCount > 0)
                    {
                        metadata.Tags.Add(attr.GetArgumentValueString(0));
                    }
                }

                for (uint f = 0; f < elementType.FieldCount; f++)
                {
                    var field = elementType.GetFieldByIndex(f);
                    SlangMaterialMeta.AddBinding(
                        metadata,
                        field.Name,
                        SlangMaterialMeta.ResourceType(field.Type));
                }
            }
        }

        var backendResourceMaps =
            new Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>>();
        for (int t = 0; t < targets.Length; t++)
        {
            string backendName = targets[t].Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
            backendResourceMaps[backendName] = new Dictionary<SlangBindingMap.ResourceKey, uint>();
        }

        for (int i = 0; i < entryPointCount; i++)
        {
            IEntryPoint? entryPoint = null;
            IComponentType? composedProgram = null;
            ISlangBlob? diagnostics2 = null;
            IComponentType? linkedProgram = null;
            ISlangBlob? linkDiagnostics = null;

            module.GetDefinedEntryPoint(i, out entryPoint);
            var reflectedAttributes = SlangEntryMeta.Read(
                entryPoint.GetFunctionReflection()
            );

            // Compose (Module + EntryPoint)
            session.CreateCompositeComponentType(
                [module, entryPoint],
                out composedProgram,
                out diagnostics2
            );

            if (composedProgram == null)
            {
                Console.WriteLine(
                    $"Warning: Failed to compose entry point {i}: {GetString(diagnostics2)}"
                );
                continue;
            }

            // Link
            composedProgram.Link(out linkedProgram, out linkDiagnostics);

            if (linkedProgram == null)
            {
                Console.WriteLine(
                    $"Warning: Failed to link entry point {i}: {GetString(linkDiagnostics)}"
                );
                continue;
            }

            // Get Layout from linked program for each target
            for (int t = 0; t < targets.Length; t++)
            {
                string backendName =
                    targets[t].Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
                var reflection = linkedProgram.GetLayout((nint)t, out _);
                if (reflection != ShaderReflection.Null)
                {
                    var epReflection = reflection.GetEntryPointByIndex(0);
                    ShaderStage epStage =
                        epReflection.Stage != SlangStage.None
                            ? MapStage(epReflection.Stage)
                            : ShaderStage.Vertex;

                    var entryPointMaterialResourceNames = new HashSet<string>(StringComparer.Ordinal);
                    uint entryParamCount = epReflection.ParameterCount;
                    for (uint pi = 0; pi < entryParamCount; pi++)
                    {
                        var param = epReflection.GetParameterByIndex(pi);
                        SlangMaterialMeta.BindEntry(
                            param,
                            metadata,
                            entryPointMaterialResourceNames);
                    }

                    var entryPointResources =
                        new Dictionary<SlangBindingMap.ResourceKey, uint>();

                    // Global parameters (cbuffer, StructuredBuffer, etc.)
                    uint globalParamCount = reflection.ParameterCount;
                    for (uint pi = 0; pi < globalParamCount; pi++)
                    {
                        var param = reflection.GetParameterByIndex(pi);
                        SlangBindingMap.Collect(
                            param,
                            epStage,
                            entryPointResources,
                            backendName,
                            skipNames: entryPointMaterialResourceNames);
                    }

                    var entryPointBindingBases = SlangBindingMap.Next(
                        entryPointResources,
                        backendName);
                    for (uint pi = 0; pi < entryParamCount; pi++)
                    {
                        var param = epReflection.GetParameterByIndex(pi);
                        SlangBindingMap.Collect(
                            param,
                            epStage,
                            entryPointResources,
                            backendName,
                            entryBases: entryPointBindingBases);
                    }

                    SlangBindingMap.Merge(backendResourceMaps[backendName], entryPointResources);
                    asset.EntryPointReflections.Add(
                        new Schema.ShaderEntryPointReflection
                        {
                            Backend = backendName,
                            EntryPoint = epReflection.Name,
                            Stage = epStage,
                            Reflection = SlangBindingMap.Create(entryPointResources),
                        });
                }
            }

            var baseReflection = linkedProgram.GetLayout(0, out _);
            if (baseReflection == ShaderReflection.Null)
                baseReflection = linkedProgram.GetLayout(1, out _);
            SlangMaterialMeta.ScalarLayouts(baseReflection, materialScalarTypes, metadata);

            var entryPointReflection = baseReflection.GetEntryPointByIndex(0);
            string epName = entryPointReflection.Name;
            var finalStage = MapStage(entryPointReflection.Stage);

            // Get compiled code for each target
            for (int t = 0; t < targets.Length; t++)
            {
                linkedProgram.GetEntryPointCode(0, t, out var codeBlob, out var diag);

                if (codeBlob != null)
                {
                    int variantIndex = asset.Variants.Count;
                    var rawBytes = GetBytes(codeBlob);
                    bool isSpirvBackend = targets[t].Format == SlangCompileTarget.Spirv;
                    // Normalize bytecode before hashing:
                    // SPIR-V: strip OpName/OpMemberName (debug names that vary with variable names)
                    var bytesToHash = isSpirvBackend ? StripSpirvNames(rawBytes) : rawBytes;
                    var hashBytes = SHA256.HashData(bytesToHash);
                    var contentHash = Convert.ToHexString(hashBytes);

                    asset.Variants.Add(
                        new ShaderBytecode
                        {
                            Backend =
                                targets[t].Format == SlangCompileTarget.Dxil ? "dxil" : "spirv",
                            Stage = finalStage,
                            EntryPoint = epName,
                            Data = rawBytes,
                            ContentHash = contentHash,
                        }
                    );

                    for (int attrIndex = 0; attrIndex < reflectedAttributes.Count; attrIndex++)
                    {
                        asset.EntryPointAttributes.Add(
                            new Schema.ShaderEntryPointAttribute
                            {
                                VariantIndex = variantIndex,
                                Name = reflectedAttributes[attrIndex].Name,
                                Args =
                                    reflectedAttributes[attrIndex].Args.Count == 0
                                        ? []
                                        : [.. reflectedAttributes[attrIndex].Args],
                            }
                        );
                    }

                    ShaderEntryPointMetadata? entryPointMetadata =
                        SlangEntryMeta.Create(variantIndex, reflectedAttributes);
                    if (entryPointMetadata != null)
                    {
                        asset.EntryPointMetadata.Add(entryPointMetadata);
                    }
                }
                else
                {
                    Console.WriteLine(
                        $"Warning: Failed to get code for target {t}: {GetString(diag)}"
                    );
                }
            }
        }

        // Finalize Reflection Data
        foreach (var kvp in backendResourceMaps)
        {
            var reflectionData = new Schema.ShaderReflectionData { Resources = [] };
            var backendRef = new Schema.BackendReflection
            {
                Backend = kvp.Key,
                Reflection = reflectionData,
            };
            SlangBindingMap.Fill(kvp.Value, reflectionData);
            asset.Reflections!.Add(backendRef);
        }

        if (writeCache && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            _cache[filePath] = (asset, File.GetLastWriteTime(filePath));

            try
            {
                ShaderAssetCodec.Save(asset, cachePath);
                AssetMetaFiles.Save(
                    cachePath,
                    new AssetMeta
                    {
                        AssetGuid = AssetGuid.Parse(
                            asset.AssetGuid ?? AssetGuid.Empty.ToFlatString()
                        ),
                        SourceGuid = sourceMeta.SourceGuid,
                        SubAssetKey = subAssetKey,
                        ContentFingerprint = fingerprint,
                        Dependencies = dependencies,
                        ImporterVersion = ImporterVersion,
                        AssetPath = cachePath,
                    }
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Warning: Failed to save shader asset cache to {cachePath}: {ex.Message}"
                );
            }
        }

        return asset;
    }

    private static Schema.ShaderStage MapStage(SlangStage stage)
    {
        switch (stage)
        {
            case SlangStage.None:
                Console.WriteLine(
                    "Warning: Slang reported ShaderStage.None. Falling back to Vertex."
                );
                return Schema.ShaderStage.Vertex;
            case SlangStage.Vertex:
                return Schema.ShaderStage.Vertex;
            case SlangStage.Fragment:
                return Schema.ShaderStage.Pixel;
            case SlangStage.Compute:
                return Schema.ShaderStage.Compute;
            case SlangStage.Hull:
                return Schema.ShaderStage.Hull;
            case SlangStage.Domain:
                return Schema.ShaderStage.Domain;
            case SlangStage.Geometry:
                return Schema.ShaderStage.Geometry;
            case SlangStage.Amplification:
                return Schema.ShaderStage.Amplification;
            case SlangStage.Mesh:
                return Schema.ShaderStage.Mesh;
            case SlangStage.RayGeneration:
                return Schema.ShaderStage.RayGen;
            case SlangStage.Miss:
                return Schema.ShaderStage.RayMiss;
            case SlangStage.ClosestHit:
                return Schema.ShaderStage.RayClosestHit;
            case SlangStage.AnyHit:
                return Schema.ShaderStage.RayAnyHit;
            case SlangStage.Intersection:
                return Schema.ShaderStage.RayIntersection;
            case SlangStage.Callable:
                return Schema.ShaderStage.Callable;
            default:
                throw new NotImplementedException($"Stage {stage} not supported");
        }
    }

    private static string? GetString(ISlangBlob? blob)
    {
        if (blob == null)
            return null;
        unsafe
        {
            return Encoding.UTF8.GetString(
                (byte*)blob.GetBufferPointer(),
                (int)blob.GetBufferSize()
            );
        }
    }

    private static byte[] GetBytes(ISlangBlob blob)
    {
        unsafe
        {
            var span = new ReadOnlySpan<byte>(blob.GetBufferPointer(), (int)blob.GetBufferSize());
            return span.ToArray();
        }
    }

    /// <summary>
    /// Strip OpName (5) and OpMemberName (6) instructions from SPIR-V bytecode.
    /// These instructions encode debug variable/member names but don't affect
    /// execution semantics. Removing them ensures identical logic with different
    /// variable names produces the same content hash.
    /// SPIR-V binary format: header (5 words) + instructions.
    /// Each instruction: word0 = (wordCount &lt;&lt; 16) | opcode, followed by (wordCount-1) words.
    /// </summary>
    private static byte[] StripSpirvNames(byte[] spirv)
    {
        if (spirv.Length < 20)
            return spirv; // Too small for valid SPIR-V

        const ushort OpName = 5;
        const ushort OpMemberName = 6;
        const int HeaderWords = 5;

        var words = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        // Validate SPIR-V magic number
        if (words[0] != 0x07230203)
            return spirv;

        using var ms = new MemoryStream(spirv.Length);
        using var bw = new BinaryWriter(ms);

        // Copy header (5 words = 20 bytes)
        for (int i = 0; i < HeaderWords && i < words.Length; i++)
            bw.Write(words[i]);

        // Process instructions, skip OpName and OpMemberName
        int pos = HeaderWords;
        while (pos < words.Length)
        {
            uint instrWord = words[pos];
            ushort opcode = (ushort)(instrWord & 0xFFFF);
            ushort wordCount = (ushort)(instrWord >> 16);

            if (wordCount == 0)
                break; // Malformed
            if (pos + wordCount > words.Length)
                break;

            if (opcode != OpName && opcode != OpMemberName)
            {
                for (int w = 0; w < wordCount; w++)
                    bw.Write(words[pos + w]);
            }

            pos += wordCount;
        }

        return ms.ToArray();
    }

}
