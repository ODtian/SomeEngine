using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: Shade — stateless static orchestrator.
/// Owns Sig0/PerPassSRB statics, PSO build logic, and pass composition.
/// </summary>
public static class ClusterShade
{
    // ─── Binning / Resolve resources ───
    private static ClusterShadeBinningResources? s_shadeBinRes;
    private static ClusterResolvePass? s_resolveProto;

    // ─── Dual-Signature statics ───
    private static IPipelineResourceSignature? s_sig0;
    private static IShaderResourceBinding? s_perPassSRB;

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    /// <summary>Per-pass SRB (Sig0). Used by ClusterMaterialShadePass.Execute().</summary>
    internal static IShaderResourceBinding? PerPassSRB => s_perPassSRB;

    /// <summary>Sig0 accessor for PSO creation.</summary>
    internal static IPipelineResourceSignature? Sig0 => s_sig0;

    private static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            s_shadeBinRes = new ClusterShadeBinningResources();
            s_shadeBinRes.Init(context);
            s_resolveProto = new ClusterResolvePass(context);
            s_resolveProto.Init();

            // Create Sig0 + PerPassSRB
            var device = context.Device!;
            s_sig0 = device.CreatePipelineResourceSignature(new PipelineResourceSignatureDesc
            {
                Name = "ShadeSignature_PerPass",
                BindingIndex = 0,
                Resources =
                [
                    Res("VisBuffer",        ShaderResourceType.TextureSrv),
                    Res("VisibleClusters",  ShaderResourceType.BufferSrv),
                    Res("PageHeap",         ShaderResourceType.BufferSrv),
                    Res("Instances",        ShaderResourceType.BufferSrv),
                    Res("InstanceHeaders",  ShaderResourceType.BufferSrv),
                    Res("InstanceDataHeap", ShaderResourceType.BufferSrv),
                    Res("PixelCoordBuffer", ShaderResourceType.BufferSrv),
                    Res("BinOffsets",       ShaderResourceType.BufferSrv),
                    Res("BinCounts",        ShaderResourceType.BufferSrv),
                    Res("OutputColor",      ShaderResourceType.TextureUav),
                    Res("DeformCache",      ShaderResourceType.BufferSrv),
                    Res("CacheOffsets",     ShaderResourceType.BufferSrv),
                ],
            });
            s_perPassSRB = s_sig0.CreateShaderResourceBinding(false);

            s_initialized = true;
        }
    }

    // ════════════════════════════════════════════════
    //  PSO Group Build (replaces ShadePSOBuilder)
    // ════════════════════════════════════════════════

    internal static ShadePSOGroup[] BuildPSOGroups(
        BinSpace binSpace, int fieldIndex,
        GlobalPsoCache psoCache, RenderContext context)
    {
        EnsureInitialized(context);

        var shaderGroups = ShadePSOGroup.ComputeShaderGroups(
            binSpace,
            fieldIndex,
            static entity => entity.GetComponent<ClusterShadeComponent>().Default);
        if (shaderGroups.Count == 0)
            return [];

        var groups = new ShadePSOGroup[shaderGroups.Count];
        for (int g = 0; g < shaderGroups.Count; g++)
        {
            var sg = shaderGroups[g];
            var representativeEntity = sg.Entities[0];
            var sig1 = CreateSig1(context.Device!, representativeEntity, sg.VariantRef);
            var pso = FindOrCreatePSO(sg.VariantRef, psoCache, context, sig1);
            var srbs = new IShaderResourceBinding[sg.BinCount];
            var argsBins = new int[sg.BinCount];

            for (int i = 0; i < sg.BinCount; i++)
            {
                srbs[i] = sig1.CreateShaderResourceBinding(false);
                if (MaterialEntityUtility.TryGetMaterial(sg.Entities[i], out var material))
                {
                    material.Params.ApplyTo(srbs[i]);
                }

                argsBins[i] = binSpace.GetArgsBin(fieldIndex, sg.BinStart + i);
            }

            groups[g] = new ShadePSOGroup
            {
                PSO = pso,
                SRBs = srbs,
                Entities = sg.Entities,
                ArgsBins = argsBins,
                BinStart = sg.BinStart,
                BinCount = sg.BinCount,
            };
        }

        return groups;
    }

    internal static ulong ComputeSig1CacheKey(Entity entity, ShaderVariantRef variantRef)
    {
        return MaterialEntityUtility.ComputeResolvedResourceLayoutHash(entity, variantRef);
    }

    internal static PipelineResourceDesc[] BuildSig1Resources(Entity entity, ShaderVariantRef variantRef)
    {
        var resources = new List<PipelineResourceDesc>
        {
            new()
            {
                Name = "Uniforms",
                ShaderStages = ShaderType.Compute,
                ResourceType = ShaderResourceType.ConstantBuffer,
                VarType = ShaderResourceVariableType.Dynamic,
            },
        };

        foreach (var (name, type) in MaterialEntityUtility.EnumerateResolvedResources(entity, variantRef))
        {
            resources.Add(new PipelineResourceDesc
            {
                Name = name,
                ShaderStages = ShaderType.Compute,
                ResourceType = type,
                VarType = ShaderResourceVariableType.Mutable,
            });
        }

        return resources.ToArray();
    }

    private static IPipelineResourceSignature CreateSig1(IRenderDevice device, Entity entity, ShaderVariantRef variantRef)
    {
        ulong sigHash = ComputeSig1CacheKey(entity, variantRef);
        return device.CreatePipelineResourceSignature(new PipelineResourceSignatureDesc
        {
            Name = $"ShadeSignature_Material_{sigHash:X16}",
            BindingIndex = 1,
            Resources = BuildSig1Resources(entity, variantRef),
        });
    }

    private static IPipelineState FindOrCreatePSO(
        ShaderVariantRef variantRef,
        GlobalPsoCache psoCache,
        RenderContext context,
        IPipelineResourceSignature sig1)
    {
        var shader = variantRef.Shader;
        if (shader == null || context.Device == null)
            throw new InvalidOperationException("ShaderVariantRef must have a non-null ShaderAsset.");

        var deviceType = context.Device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";
        var computeVariant = shader.Variants?.FirstOrDefault(v =>
            v.Backend == backend
            && v.Stage == SomeEngine.Assets.Schema.ShaderStage.Compute
            && string.Equals(v.EntryPoint, variantRef.EntryPoint, StringComparison.Ordinal));
        string entryPoint = computeVariant?.EntryPoint
            ?? throw new InvalidOperationException(
                $"No compute entry point '{variantRef.EntryPoint}' found for backend {backend} in shader {shader.Name}");

        var cs = shader.CreateShader(context, entryPoint);
        var ci = new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = $"Shade PSO ({shader.Name})",
                PipelineType = PipelineType.Compute,
            },
            Cs = cs,
            ResourceSignatures = [s_sig0!, sig1],
        };

        return psoCache.GetOrCreateComputePSO(context.Device, ci);
    }

    // ════════════════════════════════════════════════
    //  Pass Composition
    // ════════════════════════════════════════════════

    public static (ClusterShadeBinOutput ShadeBin, ClusterShadeOutput Shade) AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterRasterOutput raster,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        RenderGraphHandle hDrawUniforms,
        RenderGraphHandle hMaterialSlotBuffer,
        ShadePSOGroup[] psoGroups,
        RenderGraphHandle colorTarget,
        RenderGraphHandle depthTarget,
        BinSpace binSpace,
        int shadingBinFieldIndex,
        uint materialCount,
        in Matrix4x4 view,
        in Matrix4x4 proj,
        Vector3 cameraPos,
        uint pageTableSize,
        Vector3 quantOrigin,
        float quantStep,
        ClusterDebugMode debugMode,
        uint screenWidth,
        uint screenHeight,
        RenderGraphHandle hDeformCache = default,
        RenderGraphHandle hCacheOffsets = default
    )
    {
        EnsureInitialized(context);
        var res = s_shadeBinRes!;

        uint drawDebugMode = (uint)debugMode;
        bool isResolveOnlyDebug = drawDebugMode is 1 or 2 or 3;
        uint clampedMaterialCount = Math.Max(materialCount, 1u);

        // ════════════════════════════════════════════════
        //  ShadeBin (Count + Reserve + Scatter)
        // ════════════════════════════════════════════════
        int maxMaterials = Math.Max((int)clampedMaterialCount, 256);
        var hBinUniforms = CreateDynamicUniform(graph, "ShadeBinUniforms", new ShadeBinUniforms
        {
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            MaterialCount = clampedMaterialCount,
            SlotCapacity = (uint)binSpace.SlotCapacity,
            BinFieldIndex = (uint)shadingBinFieldIndex,
        });

        var hBinCounts = graph.CreateBuffer("BinCounts", new BufferDesc
        {
            Size = (ulong)(maxMaterials * 4), BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured, ElementByteStride = 4,
        });
        var hBinOffsets = graph.CreateBuffer("BinOffsets", new BufferDesc
        {
            Size = (ulong)(maxMaterials * 4), BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured, ElementByteStride = 4,
        });
        var hBinScatterCount = graph.CreateBuffer("BinScatterCount", new BufferDesc
        {
            Size = (ulong)(maxMaterials * 4), BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured, ElementByteStride = 4,
        });
        var hPixelCoordBuffer = graph.CreateBuffer("PixelCoordBuffer", new BufferDesc
        {
            Size = (ulong)(screenWidth * screenHeight * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured, ElementByteStride = 4,
        });
        var hBinIndirectArgs = graph.CreateBuffer("BinIndirectArgs", new BufferDesc
        {
            Size = (ulong)(maxMaterials * 12),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw, ElementByteStride = 4,
        });

        // Clear bin counts
        graph.AddPass(
            "ClearBinCounts",
            builder => { builder.Write(hBinCounts, ResourceState.CopyDest); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinCounts);
                if (ctx2 != null && buf != null)
                {
                    byte[] zeros = new byte[maxMaterials * 4];
                    Array.Clear(zeros);
                    ctx2.UpdateBuffer(buf, 0, zeros, ResourceStateTransitionMode.None);
                }
            }
        );

        // Count → Reserve → Scatter
        graph.AddPass(new ClusterShadeBinCountPass(context, res)
        {
            HVisBuffer = raster.VisBuffer, HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = globals.GlobalInstanceHeader, HShadeBinUniforms = hBinUniforms,
            HBinCounts = hBinCounts, HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = globals.PageHeap,
        });
        graph.AddPass(new ClusterShadeBinReservePass(context, res)
        {
            HShadeBinUniforms = hBinUniforms, HBinCounts = hBinCounts,
            HBinOffsets = hBinOffsets, HBinScatterCount = hBinScatterCount,
            HBinIndirectArgs = hBinIndirectArgs,
        });
        graph.AddPass(new ClusterShadeBinScatterPass(context, res)
        {
            HVisBuffer = raster.VisBuffer, HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = globals.GlobalInstanceHeader, HShadeBinUniforms = hBinUniforms,
            HBinOffsets = hBinOffsets, HBinScatterCount = hBinScatterCount,
            HPixelCoordBuffer = hPixelCoordBuffer, HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = globals.PageHeap,
        });

        var shadeBinOut = new ClusterShadeBinOutput(hPixelCoordBuffer, hBinOffsets, hBinCounts, hBinIndirectArgs);

        // ════════════════════════════════════════════════
        //  Shade Target
        // ════════════════════════════════════════════════
        // Shade output always goes to an intermediate UAV-capable texture
        // (swapchain typically does not support UAV).
        var hShadeOutput = graph.CreateTexture("ShadeOutput", new TextureDesc
        {
            Type = ResourceDimension.Tex2d, Width = screenWidth, Height = screenHeight,
            MipLevels = 1, Format = TextureFormat.RGBA8_UNorm, Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget,
        });

        // ════════════════════════════════════════════════
        //  Resolve-only debug path (ClusterID/LOD/etc.)
        // ════════════════════════════════════════════════
        if (isResolveOnlyDebug)
        {
            var resolve = new ClusterResolvePass(context);
            resolve.Init();
            resolve.HVisBuffer = raster.VisBuffer;
            resolve.HDepthTarget = depthTarget;
            resolve.HVisibleClusters = cull.VisibleClusters;
            resolve.HPageHeap = globals.PageHeap;
            resolve.HGlobalTransformBuffer = globals.GlobalTransform;
            resolve.HDrawUniforms = hDrawUniforms;
            resolve.HColorTarget = hShadeOutput;
            graph.AddPass(resolve);

            // Copy to final color target if provided
            if (colorTarget.IsValid)
                AddCopyPass(graph, hShadeOutput, colorTarget, screenWidth, screenHeight);

            return (shadeBinOut, new ClusterShadeOutput(colorTarget.IsValid ? colorTarget : hShadeOutput));
        }

        // ════════════════════════════════════════════════
        //  Shade Uniforms + Clear + Material Shade
        // ════════════════════════════════════════════════
        var shadeUniformData = new ShadeUniforms
        {
            ViewProj = Matrix4x4.Transpose(view * proj),
            View = Matrix4x4.Transpose(view),
            PageTableSize = pageTableSize,
            DebugMode = drawDebugMode,
            ScreenWidth = screenWidth, ScreenHeight = screenHeight,
            QuantOrigin = quantOrigin, QuantStep = quantStep,
            ShadingBin = 0,
            MaterialCount = clampedMaterialCount,
            LightDir = new Vector3(0.3f, -1.0f, 0.5f),
            LightIntensity = 1.5f,
            AmbientColor = new Vector3(0.15f),
            CameraPos = cameraPos,
        };
        var hShadeUniforms = CreateDynamicUniform(graph, "ShadeUniforms", shadeUniformData);

        // Clear shade output
        graph.AddPass(
            "ClearResolveTarget",
            builder => { builder.Write(hShadeOutput, ResourceState.RenderTarget); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var tex = rgCtx.GetTexture(hShadeOutput);
                if (ctx2 != null && tex != null)
                {
                    var rtv = tex.GetDefaultView(TextureViewType.RenderTarget);
                    if (rtv != null)
                    {
                        ctx2.SetRenderTargets([rtv], null, ResourceStateTransitionMode.None);
                        ctx2.ClearRenderTarget(rtv, new Vector4(0, 0, 0, 0), ResourceStateTransitionMode.None);
                    }
                }
            }
        );

        // Material Shade
        var shadePass = new ClusterMaterialShadePass(context)
        {
            HVisBuffer = raster.VisBuffer,
            HVisibleClusters = cull.VisibleClusters,
            HPageHeap = globals.PageHeap,
            HInstances = globals.GlobalTransform,
            HInstanceHeaders = globals.GlobalInstanceHeader,
            HInstanceDataHeap = globals.InstanceDataHeap,
            HShadeUniforms = hShadeUniforms,
            HPixelCoordBuffer = shadeBinOut.PixelCoordBuffer,
            HBinOffsets = shadeBinOut.BinOffsets,
            HBinCounts = shadeBinOut.BinCounts,
            HBinIndirectArgs = shadeBinOut.BinIndirectArgs,
            HOutputColor = hShadeOutput,
            HDeformCache = hDeformCache,
            HCacheOffsets = hCacheOffsets,
            ShadeUniformData = shadeUniformData,
            PSOGroups = psoGroups,
        };
        graph.AddPass(shadePass);

        // Copy shade result to final color target if provided
        if (colorTarget.IsValid)
            AddCopyPass(graph, hShadeOutput, colorTarget, screenWidth, screenHeight);

        return (shadeBinOut, new ClusterShadeOutput(colorTarget.IsValid ? colorTarget : hShadeOutput));
    }

    // ─── Helpers ───

    private static PipelineResourceDesc Res(string name, ShaderResourceType type) => new()
    {
        Name = name,
        ShaderStages = ShaderType.Compute,
        ResourceType = type,
        VarType = ShaderResourceVariableType.Dynamic,
    };

    private static RenderGraphHandle CreateDynamicUniform<T>(RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic, BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass(
            $"Upload{name}",
            builder => { builder.Write(handle, ResourceState.ConstantBuffer); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<T>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = data;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );
        return handle;
    }

    private static void AddCopyPass(
        RenderGraph graph,
        RenderGraphHandle src,
        RenderGraphHandle dst,
        uint width,
        uint height)
    {
        graph.AddPass(
            "CopyShadeToColor",
            builder =>
            {
                builder.Read(src, ResourceState.CopySource);
                builder.Write(dst, ResourceState.CopyDest);
            },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var srcTex = rgCtx.GetTexture(src);
                var dstTex = rgCtx.GetTexture(dst);
                if (ctx2 != null && srcTex != null && dstTex != null)
                {
                    ctx2.CopyTexture(new CopyTextureAttribs
                    {
                        SrcTexture = srcTex,
                        DstTexture = dstTex,
                        SrcTextureTransitionMode = ResourceStateTransitionMode.None,
                        DstTextureTransitionMode = ResourceStateTransitionMode.None,
                    });
                }
            }
        );
    }
}
