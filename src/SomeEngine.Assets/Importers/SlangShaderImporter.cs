using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Linq;
using SomeEngine.Assets.Schema;
using SlangShaderSharp;

namespace SomeEngine.Assets.Importers;

public static class SlangShaderImporter
{
    private static IGlobalSession? _globalSession;

    public static IGlobalSession GlobalSession
    {
        get {
            if (_globalSession == null)
            {
                Slang.CreateGlobalSession(Slang.ApiVersion, out _globalSession);
            }
            return _globalSession;
        }
    }

    public static ShaderAsset Import(string filePath, string? source = null)
    {
        if (source == null)
        {
            source = File.ReadAllText(filePath);
        }

        string name = Path.GetFileNameWithoutExtension(filePath);

        // Find profiles using GlobalSession
        var dxilProfile = GlobalSession.FindProfile("sm_6_5");
        var spirvProfile = GlobalSession.FindProfile("glsl_460");

        // Define targets
        var targets = new TargetDesc[] {
            new TargetDesc {
                Format = SlangCompileTarget.Dxil, Profile = dxilProfile
            },
            new TargetDesc {
                Format = SlangCompileTarget.Spirv, Profile = spirvProfile
            }
        };

        var sessionDesc = new SessionDesc {
            Targets = targets,
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
            SearchPaths = new[] { Path.GetDirectoryName(filePath) ?? "" }
        };

        GlobalSession.CreateSession(sessionDesc, out var session);

        // Load Module
        var blob = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        var module =
            session.LoadModuleFromSource(name, filePath, blob, out var diagnostics);

        if (module == null)
        {
            throw new Exception(
                $"Failed to load module {name}: {GetString(diagnostics)}"
            );
        }

        int entryPointCount = module.GetDefinedEntryPointCount();

        var asset = new ShaderAsset {
            Name = name,
            Variants = new List<ShaderBytecode>(),
            Reflections = new List<BackendReflection>()
        };

        var backendResourceMaps = new Dictionary<
            string,
            Dictionary<
                (string Name, uint Set, uint Binding),
                (HashSet<ShaderStage> Stages,
                 ShaderResourceCategory Category)>>();
        for (int t = 0; t < targets.Length; t++)
        {
            string backendName =
                targets[t].Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
            backendResourceMaps[backendName] = new Dictionary<
                (string Name, uint Set, uint Binding),
                (HashSet<ShaderStage> Stages,
                 ShaderResourceCategory Category)>();
        }

        for (int i = 0; i < entryPointCount; i++)
        {
            module.GetDefinedEntryPoint(i, out var entryPoint);

            // Compose (Module + EntryPoint)
            IComponentType composedProgram;
            ISlangBlob ? diagnostics2;

            session.CreateCompositeComponentType(
                [module, entryPoint], out composedProgram, out diagnostics2
            );

            if (composedProgram == null)
            {
                Console.WriteLine(
                    $"Warning: Failed to compose entry point {i}: {GetString(diagnostics2)}"
                );
                continue;
            }

            // Link
            IComponentType linkedProgram;
            ISlangBlob ? linkDiagnostics;
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
                    ShaderStage epStage = epReflection.Stage != SlangStage.None
                                              ? MapStage(epReflection.Stage)
                                              : ShaderStage.Vertex;

                    // Global parameters (cbuffer, StructuredBuffer, etc.)
                    uint globalParamCount = reflection.ParameterCount;
                    Console.WriteLine(
                        $"[Slang Reflection] Backend={backendName} EP={epReflection.Name} Stage={epStage} GlobalParams={globalParamCount}"
                    );
                    for (uint pi = 0; pi < globalParamCount; pi++)
                    {
                        var param = reflection.GetParameterByIndex(pi);
                        CollectResourceFromVar(
                            param, epStage, backendResourceMaps[backendName]
                        );
                    }
                }
            }

            var baseReflection = linkedProgram.GetLayout(0, out _);
            if (baseReflection == ShaderReflection.Null)
                baseReflection = linkedProgram.GetLayout(1, out _);

            var entryPointReflection = baseReflection.GetEntryPointByIndex(0);
            string epName = entryPointReflection.Name;
            var finalStage = MapStage(entryPointReflection.Stage);

            // Get compiled code for each target
            for (int t = 0; t < targets.Length; t++)
            {
                linkedProgram.GetEntryPointCode(
                    0, t, out var codeBlob, out var diag
                );

                if (codeBlob != null)
                {
                    asset.Variants.Add(new ShaderBytecode {
                        Backend = targets[t].Format == SlangCompileTarget.Dxil
                                      ? "dxil"
                                      : "spirv",
                        Stage = finalStage,
                        EntryPoint = epName,
                        Data = GetBytes(codeBlob)
                    });
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
            var backendRef = new BackendReflection {
                Backend = kvp.Key,
                Reflection = new ShaderReflectionData {
                    Resources = new List<ShaderResourceReflection>()
                }
            };
            FinalizeReflection(kvp.Value, backendRef.Reflection);
            asset.Reflections.Add(backendRef);
        }

        return asset;
    }

    private static void FinalizeReflection(
        Dictionary<
            (string Name, uint Set, uint Binding),
            (HashSet<ShaderStage> Stages,
             ShaderResourceCategory Category)> resourceMap,
        ShaderReflectionData dest
    )
    {
        dest.Resources ??= new List<ShaderResourceReflection>();
        foreach (var kvp in resourceMap)
        {
            uint stageMask = 0;
            foreach (var s in kvp.Value.Stages)
            {
                stageMask |= GetDiligentStageFlags(s);
            }

            dest.Resources.Add(new ShaderResourceReflection {
                Name = kvp.Key.Name,
                Set = kvp.Key.Set,
                Binding = kvp.Key.Binding,
                Category = kvp.Value.Category,
                Stages = stageMask
            });
        }
    }

    private static void CollectResourceFromVar(
        VariableLayoutReflection varLayout,
        ShaderStage stage,
        Dictionary<
            (string Name, uint Set, uint Binding),
            (HashSet<ShaderStage> Stages,
             ShaderResourceCategory Category)> resources
    )
    {
        // Only collect top-level resources
        var category = varLayout.Category;
        if (category != SlangParameterCategory.ConstantBuffer &&
            category != SlangParameterCategory.ShaderResource &&
            category != SlangParameterCategory.UnorderedAccess &&
            category != SlangParameterCategory.SamplerState)
        {
            return;
        }

        string name = varLayout.Name;
        if (string.IsNullOrEmpty(name))
            return;

        // BindingIndex = register number (b#/t#/u#/s#), BindingSpace = register
        // space
        uint binding = varLayout.BindingIndex;
        uint set = varLayout.BindingSpace;

        Console.WriteLine(
            $"[Slang Reflection] Detected resource: {name} Stage: {stage} Set: {set} Binding: {binding} Category: {category}"
        );

        if (!resources.TryGetValue((name, set, binding), out var entry))
        {
            var mappedCategory = MapCategory(varLayout);

            entry =
                (new HashSet<ShaderStage>(),
                 mappedCategory);
            resources[(name, set, binding)] = entry;
        }
        entry.Stages.Add(stage);
    }

    private static ShaderResourceCategory MapCategory(
        VariableLayoutReflection varLayout
    )
    {
        var category = varLayout.Category;
        var typeLayout = varLayout.TypeLayout;
        var typeReflection = typeLayout.Type;
        var shape = typeReflection.ResourceShape;
        var baseShape = shape & SlangResourceShape.BaseShapeMask;

        if (category == SlangParameterCategory.ConstantBuffer)
            return ShaderResourceCategory.ConstantBuffer;

        if (category == SlangParameterCategory.SamplerState)
            return ShaderResourceCategory.Sampler;

        if (category == SlangParameterCategory.ShaderResource)
        {
            if (baseShape == SlangResourceShape.AccelerationStructure)
                return ShaderResourceCategory.AccelStruct;

            if (baseShape == SlangResourceShape.StructuredBuffer ||
                baseShape == SlangResourceShape.ByteAddressBuffer ||
                baseShape == SlangResourceShape.TextureBuffer)
                return ShaderResourceCategory.BufferSRV;

            if (baseShape == SlangResourceShape.TextureSubpass)
                return ShaderResourceCategory.InputAttachment;

            return ShaderResourceCategory.TextureSRV;
        }

        if (category == SlangParameterCategory.UnorderedAccess)
        {
            if (baseShape == SlangResourceShape.StructuredBuffer ||
                baseShape == SlangResourceShape.ByteAddressBuffer ||
                baseShape == SlangResourceShape.TextureBuffer)
                return ShaderResourceCategory.BufferUAV;

            return ShaderResourceCategory.TextureUAV;
        }

        return ShaderResourceCategory.Unknown;
    }

    private static uint GetDiligentStageFlags(ShaderStage stage)
    {
        return stage switch { ShaderStage.Vertex => 0x01,
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
                              _ => 0 };
    }

    private static SomeEngine.Assets.Schema.ShaderStage MapStage(SlangStage stage)
    {
        switch (stage)
        {
        case SlangStage.None:
            Console.WriteLine(
                "Warning: Slang reported ShaderStage.None. Falling back to Vertex."
            );
            return SomeEngine.Assets.Schema.ShaderStage.Vertex;
        case SlangStage.Vertex:
            return SomeEngine.Assets.Schema.ShaderStage.Vertex;
        case SlangStage.Fragment:
            return SomeEngine.Assets.Schema.ShaderStage.Pixel;
        case SlangStage.Compute:
            return SomeEngine.Assets.Schema.ShaderStage.Compute;
        case SlangStage.Hull:
            return SomeEngine.Assets.Schema.ShaderStage.Hull;
        case SlangStage.Domain:
            return SomeEngine.Assets.Schema.ShaderStage.Domain;
        case SlangStage.Geometry:
            return SomeEngine.Assets.Schema.ShaderStage.Geometry;
        case SlangStage.Amplification:
            return SomeEngine.Assets.Schema.ShaderStage.Amplification;
        case SlangStage.Mesh:
            return SomeEngine.Assets.Schema.ShaderStage.Mesh;
        case SlangStage.RayGeneration:
            return SomeEngine.Assets.Schema.ShaderStage.RayGen;
        case SlangStage.Miss:
            return SomeEngine.Assets.Schema.ShaderStage.RayMiss;
        case SlangStage.ClosestHit:
            return SomeEngine.Assets.Schema.ShaderStage.RayClosestHit;
        case SlangStage.AnyHit:
            return SomeEngine.Assets.Schema.ShaderStage.RayAnyHit;
        case SlangStage.Intersection:
            return SomeEngine.Assets.Schema.ShaderStage.RayIntersection;
        case SlangStage.Callable:
            return SomeEngine.Assets.Schema.ShaderStage.Callable;
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
                (byte *)blob.GetBufferPointer(), (int)blob.GetBufferSize()
            );
        }
    }

    private static byte[] GetBytes(ISlangBlob blob)
    {
        unsafe
        {
            var span = new ReadOnlySpan<byte>(
                blob.GetBufferPointer(), (int)blob.GetBufferSize()
            );
            return span.ToArray();
        }
    }
}
