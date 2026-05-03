using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SlangShaderSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = 6;
    private const byte DiligentShaderResourceTypeUnknown = 0;
    private const byte DiligentShaderResourceTypeConstantBuffer = 1;
    private const byte DiligentShaderResourceTypeTextureSrv = 2;
    private const byte DiligentShaderResourceTypeBufferSrv = 3;
    private const byte DiligentShaderResourceTypeTextureUav = 4;
    private const byte DiligentShaderResourceTypeBufferUav = 5;
    private const byte DiligentShaderResourceTypeSampler = 6;
    private const byte DiligentShaderResourceTypeInputAttachment = 7;
    private const byte DiligentShaderResourceTypeAccelStruct = 8;

    private readonly record struct ShaderResourceReflectionKey(
        string Name,
        uint Binding,
        uint Space,
        byte ResourceType
    );

    private static readonly bool LogReflection =
        Environment.GetEnvironmentVariable("SOMEENGINE_SLANG_REFLECTION_LOG") == "1";

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
        var sourceMeta = SourceMetaManager.GetOrCreate(filePath);
        var existingAsset = AssetMetaManager.TryLoad(
            Path.ChangeExtension(Path.GetFullPath(filePath), ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, source);
    }

    public static Schema.ShaderAsset Import(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        string? source = null
    )
    {
        filePath = Path.GetFullPath(filePath);
        string cachePath = Path.ChangeExtension(filePath, ".shader.asset");
        string projectRoot = ResolveProjectRoot(filePath);
        string subAssetKey = "shader:main";
        AssetGuid assetGuid = ResolveImportedAssetGuid(sourceMeta.SourceGuid, subAssetKey);
        if (existingAsset != null && existingAsset.AssetGuid != assetGuid)
        {
            existingAsset = null;
        }

        if (existingAsset != null && File.Exists(cachePath))
        {
            AssetImportFingerprint? historicalFingerprint = TryComputeCurrentFingerprint(
                existingAsset.Dependencies,
                projectRoot,
                ImporterVersion
            );
            if (historicalFingerprint?.ContentFingerprint == existingAsset.ContentFingerprint)
            {
                if (
                    _cache.TryGetValue(filePath, out var memoryCached)
                    && IsAssetMetaMatching(memoryCached.Asset, existingAsset)
                )
                {
                    return memoryCached.Asset;
                }

                var cachedAsset = ShaderAssetSerializer.Load(cachePath);
                if (IsAssetMetaMatching(cachedAsset, existingAsset))
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

        var dependencies = CollectDependenciesFromModule(module, filePath, projectRoot);
        string fingerprint = ComputeFingerprint(dependencies, ImporterVersion);

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
            Metadata = metadata,
        };

        var moduleRefl = module.GetModuleReflection();
        var materialScalarTypes = CollectMaterialScalarTypeNames(
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
                    metadata.MaterialBindings.Add(
                        new Schema.ShaderMaterialBinding
                        {
                            Name = field.Name,
                            ResourceType = InferResourceType(field.Type.Kind),
                        }
                    );
                }
            }
        }

        var backendResourceMaps =
            new Dictionary<string, Dictionary<ShaderResourceReflectionKey, uint>>();
        for (int t = 0; t < targets.Length; t++)
        {
            string backendName = targets[t].Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
            backendResourceMaps[backendName] = new Dictionary<ShaderResourceReflectionKey, uint>();
        }

        for (int i = 0; i < entryPointCount; i++)
        {
            IEntryPoint? entryPoint = null;
            IComponentType? composedProgram = null;
            ISlangBlob? diagnostics2 = null;
            IComponentType? linkedProgram = null;
            ISlangBlob? linkDiagnostics = null;

            module.GetDefinedEntryPoint(i, out entryPoint);
            var reflectedAttributes = CollectEntryPointAttributes(
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

                    // Global parameters (cbuffer, StructuredBuffer, etc.)
                    uint globalParamCount = reflection.ParameterCount;
                    if (LogReflection)
                    {
                        Console.WriteLine(
                            $"[Slang Reflection] Backend={backendName} EP={epReflection.Name} Stage={epStage} GlobalParams={globalParamCount}"
                        );
                    }
                    for (uint pi = 0; pi < globalParamCount; pi++)
                    {
                        var param = reflection.GetParameterByIndex(pi);
                        CollectResourceFromVar(param, epStage, backendResourceMaps[backendName]);
                    }

                    uint entryParamCount = epReflection.ParameterCount;
                    for (uint pi = 0; pi < entryParamCount; pi++)
                    {
                        var param = epReflection.GetParameterByIndex(pi);
                        CollectResourceFromVar(param, epStage, backendResourceMaps[backendName]);
                    }
                }
            }

            var baseReflection = linkedProgram.GetLayout(0, out _);
            if (baseReflection == ShaderReflection.Null)
                baseReflection = linkedProgram.GetLayout(1, out _);
            CollectMaterialScalarLayouts(baseReflection, materialScalarTypes, metadata);

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
            FinalizeReflection(kvp.Value, reflectionData);
            asset.Reflections!.Add(backendRef);
        }

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            _cache[filePath] = (asset, File.GetLastWriteTime(filePath));

            try
            {
                ShaderAssetSerializer.Save(asset, cachePath);
                AssetMetaManager.Save(
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

    private static List<EntryPointAttributeData> CollectEntryPointAttributes(
        FunctionReflection functionReflection
    )
    {
        var attributes = new List<EntryPointAttributeData>();
        if (functionReflection == FunctionReflection.Null)
        {
            return attributes;
        }

        for (
            uint attributeIndex = 0;
            attributeIndex < functionReflection.AttributeCount;
            attributeIndex++
        )
        {
            var attribute = functionReflection.GetAttribute(attributeIndex);
            if (attribute == AttributeReflection.Null || string.IsNullOrEmpty(attribute.Name))
            {
                continue;
            }

            var args = new List<string>((int)attribute.ArgumentCount);
            for (uint argIndex = 0; argIndex < attribute.ArgumentCount; argIndex++)
            {
                args.Add(attribute.GetArgumentValueString(argIndex));
            }

            attributes.Add(new EntryPointAttributeData(attribute.Name, args));
        }

        return attributes;
    }

    private static List<string> CollectMaterialScalarTypeNames(
        DeclReflection root,
        string source,
        IReadOnlyList<DependencyEntryData> dependencies,
        string projectRoot
    )
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTypeName(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        void Visit(DeclReflection decl)
        {
            if (decl == DeclReflection.Null)
            {
                return;
            }

            if (decl.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = decl.Type;
                if (type != TypeReflection.Null && HasAttribute(type, "MaterialScalars"))
                {
                    AddTypeName(type.Name);
                }
            }

            for (int i = 0; i < decl.ChildrenCount; i++)
            {
                Visit(decl.GetChild((uint)i));
            }
        }

        Visit(root);
        CollectMaterialScalarTypeNamesFromSource(source, AddTypeName);
        foreach (DependencyEntryData dependency in dependencies)
        {
            string dependencyPath = GetAbsoluteDependencyPath(projectRoot, dependency.RelativePath);
            if (!File.Exists(dependencyPath))
            {
                continue;
            }

            CollectMaterialScalarTypeNamesFromSource(File.ReadAllText(dependencyPath), AddTypeName);
        }

        return result;
    }

    private static void CollectMaterialScalarTypeNamesFromSource(
        string source,
        Action<string> addTypeName
    )
    {
        foreach (Match match in MaterialScalarsAttributeRegex().Matches(source))
        {
            if (match.Groups.Count > 1)
            {
                addTypeName(match.Groups[1].Value);
            }
        }
    }

    private static bool HasAttribute(TypeReflection type, string name)
    {
        for (uint i = 0; i < type.AttributeCount; i++)
        {
            AttributeReflection attribute = type.GetAttribute(i);
            if (
                attribute != AttributeReflection.Null
                && string.Equals(attribute.Name, name, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectMaterialScalarLayouts(
        ShaderReflection reflection,
        IReadOnlyList<string> materialScalarTypes,
        Schema.ShaderMetadata metadata
    )
    {
        if (reflection == ShaderReflection.Null || materialScalarTypes.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            metadata.MaterialScalarLayouts?.Select(static layout => layout.Name ?? string.Empty)
                ?? [],
            StringComparer.Ordinal
        );

        metadata.MaterialScalarLayouts ??= [];

        foreach (string typeName in materialScalarTypes)
        {
            if (string.IsNullOrWhiteSpace(typeName) || !existing.Add(typeName))
            {
                continue;
            }

            TypeReflection? maybeLayoutType = reflection.FindTypeByName(typeName);
            if (maybeLayoutType == null)
            {
                continue;
            }

            TypeReflection layoutType = maybeLayoutType.Value;
            TypeLayoutReflection? maybeLayout = reflection.GetTypeLayout(
                layoutType,
                LayoutRules.Default
            );
            if (maybeLayout == null)
            {
                continue;
            }

            TypeLayoutReflection typeLayout = maybeLayout.Value;
            uint payloadSize = checked((uint)typeLayout.GetSize(SlangParameterCategory.Uniform));
            var fields = new List<Schema.ShaderMaterialScalarField>((int)typeLayout.FieldCount);
            uint baseOffset = uint.MaxValue;
            uint maxFieldEnd = 0;

            for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
            {
                VariableLayoutReflection fieldLayout = typeLayout.GetFieldByIndex(fieldIndex);
                if (
                    fieldLayout == VariableLayoutReflection.Null
                    || string.IsNullOrWhiteSpace(fieldLayout.Name)
                )
                {
                    continue;
                }

                TypeLayoutReflection fieldTypeLayout = fieldLayout.TypeLayout;
                uint fieldOffset = checked(
                    (uint)fieldLayout.GetOffset(SlangParameterCategory.Uniform)
                );
                uint fieldSize = checked(
                    (uint)fieldTypeLayout.GetSize(SlangParameterCategory.Uniform)
                );
                if (fieldSize == 0)
                {
                    fieldSize = checked(
                        (uint)fieldTypeLayout.GetStride(SlangParameterCategory.Uniform)
                    );
                }

                uint fieldEnd = fieldOffset + fieldSize;
                baseOffset = Math.Min(baseOffset, fieldOffset);
                maxFieldEnd = Math.Max(maxFieldEnd, fieldEnd);

                fields.Add(
                    new Schema.ShaderMaterialScalarField
                    {
                        Name = fieldLayout.Name,
                        Offset = fieldOffset,
                        Size = fieldSize,
                        RowCount = fieldTypeLayout.RowCount,
                        ColumnCount = fieldTypeLayout.ColumnCount,
                        ScalarType = checked((byte)fieldTypeLayout.ScalarType),
                    }
                );
            }

            if (payloadSize == 0)
            {
                payloadSize = maxFieldEnd;
            }

            if (baseOffset != uint.MaxValue && baseOffset > 0)
            {
                if (payloadSize >= maxFieldEnd)
                {
                    payloadSize -= baseOffset;
                }

                payloadSize = Math.Max(payloadSize, maxFieldEnd - baseOffset);
                for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                {
                    Schema.ShaderMaterialScalarField field = fields[fieldIndex];
                    field.Offset -= baseOffset;
                    fields[fieldIndex] = field;
                }
            }

            metadata.MaterialScalarLayouts.Add(
                new Schema.ShaderMaterialScalarLayout
                {
                    Name = typeName,
                    Size = payloadSize,
                    Fields = fields,
                }
            );
        }
    }

    [GeneratedRegex(
        @"\[\s*MaterialScalars(?:\s*\([^\)]*\))?\s*\]\s*struct\s+([A-Za-z_][A-Za-z0-9_]*)"
    )]
    private static partial Regex MaterialScalarsAttributeRegex();

    private static bool IsAssetMetaMatching(Schema.ShaderAsset asset, AssetMeta existingAsset)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetGuid))
        {
            return false;
        }

        return AssetGuid.TryParse(asset.AssetGuid, out var assetGuid)
            && assetGuid == existingAsset.AssetGuid;
    }

    private static AssetGuid ResolveImportedAssetGuid(SourceGuid sourceGuid, string subAssetKey)
    {
        return AssetGuid.FromSource(sourceGuid, subAssetKey);
    }

    private static DependencyEntryData[] CollectDependenciesFromModule(
        IModule module,
        string sourcePath,
        string projectRoot
    )
    {
        var dependencies = new Dictionary<string, DependencyEntryData>(
            StringComparer.OrdinalIgnoreCase
        );

        void AddDependency(string dependencyPath)
        {
            string fullPath = Path.GetFullPath(dependencyPath);
            if (!File.Exists(fullPath))
            {
                return;
            }

            string relativePath = MakeRelativePath(projectRoot, fullPath);
            dependencies[relativePath] = new DependencyEntryData
            {
                RelativePath = relativePath,
                ContentHash = ComputeFileSha256(fullPath),
            };
        }

        AddDependency(sourcePath);
        int dependencyCount = module.GetDependencyFileCount();
        for (int i = 0; i < dependencyCount; i++)
        {
            AddDependency(module.GetDependencyFilePath(i));
        }

        return dependencies
            .Values.OrderBy(static x => x.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static AssetImportFingerprint? TryComputeCurrentFingerprint(
        IReadOnlyList<DependencyEntryData> dependencies,
        string projectRoot,
        uint importerVersion
    )
    {
        if (dependencies.Count == 0)
        {
            return null;
        }

        DependencyEntryData[] currentDependencies = new DependencyEntryData[dependencies.Count];
        for (int i = 0; i < dependencies.Count; i++)
        {
            string fullPath = GetAbsoluteDependencyPath(projectRoot, dependencies[i].RelativePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            currentDependencies[i] = new DependencyEntryData
            {
                RelativePath = MakeRelativePath(projectRoot, fullPath),
                ContentHash = ComputeFileSha256(fullPath),
            };
        }

        return new AssetImportFingerprint
        {
            ContentFingerprint = ComputeFingerprint(currentDependencies, importerVersion),
            Dependencies = currentDependencies,
            ImporterVersion = importerVersion,
        };
    }

    private static string ComputeFingerprint(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion
    )
    {
        var builder = new StringBuilder();
        foreach (
            var dependency in dependencies.OrderBy(
                static x => x.RelativePath,
                StringComparer.Ordinal
            )
        )
        {
            builder.Append(dependency.RelativePath);
            builder.Append(':');
            builder.Append(dependency.ContentHash);
            builder.Append('\n');
        }

        builder.Append("||");
        builder.Append(importerVersion);
        return ComputeSha256(builder.ToString());
    }

    private static string ComputeSha256(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ComputeFileSha256(string filePath)
    {
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(filePath)));
    }

    private static string ResolveProjectRoot(string sourcePath)
    {
        string? current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        while (!string.IsNullOrEmpty(current))
        {
            if (
                File.Exists(Path.Combine(current, "SomeEngine.slnx"))
                || File.Exists(Path.Combine(current, "Directory.Build.props"))
            )
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        string cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (Path.Exists(cwd) && sourcePath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
        {
            return cwd;
        }

        return Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? cwd;
    }

    private static string MakeRelativePath(string projectRoot, string fullPath)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        string normalizedPath = Path.GetFullPath(fullPath);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(normalizedPath);
        }

        return Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
    }

    private static string GetAbsoluteDependencyPath(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }

    private static byte InferResourceType(SlangTypeKind kind)
    {
        return kind switch
        {
            SlangTypeKind.Resource => 0,
            SlangTypeKind.SamplerState => 1,
            SlangTypeKind.ConstantBuffer => 2,
            _ => 255,
        };
    }

    private static void FinalizeReflection(
        Dictionary<ShaderResourceReflectionKey, uint> resourceMap,
        Schema.ShaderReflectionData dest
    )
    {
        if (dest.Resources == null)
        {
            dest.Resources = new List<Schema.ShaderResourceReflection>();
        }

        var resources = dest.Resources;
        foreach (
            var kvp in resourceMap
                .OrderBy(static kvp => kvp.Key.Space)
                .ThenBy(static kvp => kvp.Key.Binding)
                .ThenBy(static kvp => kvp.Key.ResourceType)
                .ThenBy(static kvp => kvp.Key.Name, StringComparer.Ordinal)
        )
        {
            resources.Add(
                new Schema.ShaderResourceReflection
                {
                    Name = kvp.Key.Name,
                    Stages = kvp.Value,
                    Binding = kvp.Key.Binding,
                    Space = kvp.Key.Space,
                    ResourceType = kvp.Key.ResourceType,
                }
            );
        }
    }

    private static void CollectResourceFromVar(
        VariableLayoutReflection varLayout,
        ShaderStage stage,
        Dictionary<ShaderResourceReflectionKey, uint> resources
    )
    {
        Visit(varLayout, 0, 0, false);

        void Visit(
            VariableLayoutReflection layout,
            uint baseBinding,
            uint baseSpace,
            bool fieldBinding
        )
        {
            if (
                layout == VariableLayoutReflection.Null
                || TryAdd(layout, baseBinding, baseSpace, fieldBinding)
            )
                return;

            TypeLayoutReflection typeLayout = layout.TypeLayout.UnwrapArray();
            if (typeLayout == TypeLayoutReflection.Null)
                return;

            uint childBaseBinding = fieldBinding ? baseBinding : layout.BindingIndex;
            uint childBaseSpace = fieldBinding
                ? baseSpace
                : GetSpace(layout, SlangParameterCategory.DescriptorTableSlot, 0);
            for (uint i = 0; i < typeLayout.FieldCount; i++)
                Visit(typeLayout.GetFieldByIndex(i), childBaseBinding, childBaseSpace, true);
        }

        bool TryAdd(
            VariableLayoutReflection layout,
            uint baseBinding,
            uint baseSpace,
            bool fieldBinding
        )
        {
            byte resourceType = GetResourceType(layout, out var category);
            if (
                resourceType == DiligentShaderResourceTypeUnknown
                || string.IsNullOrEmpty(layout.Name)
            )
                return resourceType != DiligentShaderResourceTypeUnknown;

            var bindingCategory = fieldBinding
                ? SlangParameterCategory.DescriptorTableSlot
                : category;
            uint binding = fieldBinding
                ? baseBinding + checked((uint)layout.GetOffset(bindingCategory))
                : layout.BindingIndex;
            uint space = GetSpace(layout, bindingCategory, baseSpace);
            var key = new ShaderResourceReflectionKey(layout.Name, binding, space, resourceType);
            resources[key] = resources.GetValueOrDefault(key) | GetDiligentStageFlags(stage);
            return true;
        }

        static byte GetResourceType(
            VariableLayoutReflection layout,
            out SlangParameterCategory category
        )
        {
            category = layout.Category;
            TypeReflection type = layout.Type.UnwrapArray();
            SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;

            return category switch
            {
                SlangParameterCategory.ConstantBuffer
                or SlangParameterCategory.PushConstantBuffer =>
                    DiligentShaderResourceTypeConstantBuffer,
                SlangParameterCategory.SamplerState => DiligentShaderResourceTypeSampler,
                SlangParameterCategory.Subpass => DiligentShaderResourceTypeInputAttachment,
                SlangParameterCategory.ShaderResource => MapReadOnly(shape),
                SlangParameterCategory.UnorderedAccess => MapReadWrite(shape),
                _ => type.Kind switch
                {
                    SlangTypeKind.ConstantBuffer => DiligentShaderResourceTypeConstantBuffer,
                    SlangTypeKind.SamplerState => DiligentShaderResourceTypeSampler,
                    SlangTypeKind.Resource
                    or SlangTypeKind.TextureBuffer
                    or SlangTypeKind.ShaderStorageBuffer => IsWrite(type.ResourceAccess)
                        ? MapReadWrite(shape)
                        : MapReadOnly(shape),
                    _ => DiligentShaderResourceTypeUnknown,
                },
            };
        }

        static uint GetSpace(
            VariableLayoutReflection layout,
            SlangParameterCategory category,
            uint fallback
        )
        {
            uint space = checked((uint)layout.GetBindingSpace(category));
            if (space == 0 && layout.BindingSpace != 0)
                space = layout.BindingSpace;
            return space == 0 ? fallback : space;
        }

        static bool IsWrite(SlangResourceAccess access) =>
            access
                is SlangResourceAccess.ReadWrite
                    or SlangResourceAccess.RasterOrdered
                    or SlangResourceAccess.Append
                    or SlangResourceAccess.Consume
                    or SlangResourceAccess.Write;

        static byte MapReadOnly(SlangResourceShape shape) =>
            shape switch
            {
                SlangResourceShape.StructuredBuffer
                or SlangResourceShape.ByteAddressBuffer
                or SlangResourceShape.TextureBuffer => DiligentShaderResourceTypeBufferSrv,
                SlangResourceShape.AccelerationStructure => DiligentShaderResourceTypeAccelStruct,
                SlangResourceShape.TextureSubpass => DiligentShaderResourceTypeInputAttachment,
                _ => DiligentShaderResourceTypeTextureSrv,
            };

        static byte MapReadWrite(SlangResourceShape shape) =>
            shape
                is SlangResourceShape.StructuredBuffer
                    or SlangResourceShape.ByteAddressBuffer
                    or SlangResourceShape.TextureBuffer
                ? DiligentShaderResourceTypeBufferUav
                : DiligentShaderResourceTypeTextureUav;
    }

    private static uint GetDiligentStageFlags(ShaderStage stage)
    {
        return stage switch
        {
            ShaderStage.Vertex => 0x01,
            ShaderStage.Pixel => 0x02,
            ShaderStage.Geometry => 0x04,
            ShaderStage.Hull => 0x08,
            ShaderStage.Domain => 0x10,
            ShaderStage.Compute => 0x20,
            ShaderStage.Amplification => 0x40,
            ShaderStage.Mesh => 0x80,
            ShaderStage.RayGen => 0x100,
            ShaderStage.RayMiss => 0x200,
            ShaderStage.RayClosestHit => 0x400,
            ShaderStage.RayAnyHit => 0x800,
            ShaderStage.RayIntersection => 0x1000,
            ShaderStage.Callable => 0x2000,
            _ => 0,
        };
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

    private sealed record EntryPointAttributeData(string Name, List<string> Args);
}
