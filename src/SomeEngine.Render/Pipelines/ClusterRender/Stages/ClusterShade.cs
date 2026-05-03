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
/// Owns PSO build logic and pass composition. Uses all-Dynamic resource binding.
/// </summary>
public static partial class ClusterShade
{
    // ─── Binning / Resolve resources ───
    public sealed class Resources : IDisposable
    {
        internal readonly ClusterShadeBinningResources ShadeBin = new();
        internal readonly ResolveResources Resolve = new();
        internal readonly MaterialFallbackResources MaterialFallbacks = new();
        private bool _initialized;

        internal void EnsureInitialized(RenderContext context)
        {
            if (_initialized) return;
            ShadeBin.Init(context);
            Resolve.EnsureInitialized(context);
            MaterialFallbacks.EnsureInitialized(context);
            _initialized = true;
        }

        public void Dispose()
        {
            ShadeBin.Dispose();
            Resolve.Dispose();
            MaterialFallbacks.Dispose();
        }
    }

    /// <summary>Standard linear-wrap sampler desc used as immutable sampler for MaterialSampler.</summary>
    private static readonly SamplerDesc s_linearWrapSamplerDesc = new()
    {
        MinFilter = FilterType.Linear,
        MagFilter = FilterType.Linear,
        MipFilter = FilterType.Linear,
        AddressU = TextureAddressMode.Wrap,
        AddressV = TextureAddressMode.Wrap,
        AddressW = TextureAddressMode.Wrap,
    };

    // ════════════════════════════════════════════════
    //  PSO Group Build (replaces ShadePSOBuilder)
    // ════════════════════════════════════════════════

    internal static MaterialPSOGroup[] BuildPSOGroups(
        BinSpace binSpace, int fieldIndex,
        GlobalPsoCache psoCache, RenderContext context)
    {
        var device = context.Device;
        if (device == null) return [];

        var shaderGroups = MaterialPSOGroup.ComputeShaderGroups(
            binSpace,
            fieldIndex,
            static entity => entity.GetComponent<ClusterShadeComponent>().Default);
        if (shaderGroups.Count == 0)
            return [];

        var deviceType = device.GetDeviceInfo().Type;
        string backend = deviceType == RenderDeviceType.D3D12 ? "dxil" : "spirv";
        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
            ImmutableSamplers =
            [
                new ImmutableSamplerDesc
                {
                    ShaderStages = ShaderType.Compute,
                    SamplerOrTextureName = "MaterialSampler",
                    Desc = s_linearWrapSamplerDesc,
                },
            ],
        };

        var groups = new MaterialPSOGroup[shaderGroups.Count];
        for (int g = 0; g < shaderGroups.Count; g++)
        {
            var sg = shaderGroups[g];
            var pso = FindOrCreateComputePSO(sg.VariantRef, psoCache, context, backend, layout);
            var srb = pso.CreateShaderResourceBinding(false);
            var argsBins = new int[sg.BinCount];
            for (int i = 0; i < sg.BinCount; i++)
                argsBins[i] = binSpace.GetArgsBin(fieldIndex, sg.BinStart + i);

            groups[g] = new MaterialPSOGroup
            {
                PSO = pso,
                SRB = srb,
                ComputeVariant = sg.VariantRef,
                Entities = sg.Entities,
                ArgsBins = argsBins,
                BinStart = sg.BinStart,
                BinCount = sg.BinCount,
            };
        }

        return groups;
    }

    private static IPipelineState FindOrCreateComputePSO(
        ShaderVariantRef variantRef,
        GlobalPsoCache psoCache,
        RenderContext context,
        string backend,
        PipelineResourceLayoutDesc layout)
    {
        var shader = variantRef.Shader;
        if (shader == null || context.Device == null)
            throw new InvalidOperationException("ShaderVariantRef must have a non-null ShaderAsset.");

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
                ResourceLayout = layout,
            },
            Cs = cs,
        };

        return psoCache.GetOrCreateComputePSO(context.Device, ci);
    }

    // ════════════════════════════════════════════════
    //  Pass Composition
    // ════════════════════════════════════════════════

    public static (ClusterShadeBinOutput ShadeBin, ClusterShadeOutput Shade) AddPasses(
        RenderGraph graph,
        RenderContext context,
        Resources resources,
        in ClusterRasterOutput raster,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        RenderGraphHandle hDrawUniforms,
        RenderGraphHandle hMaterialSlotBuffer,
        MaterialPSOGroup[] psoGroups,
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
        resources.EnsureInitialized(context);
        var res = resources.ShadeBin;

        uint drawDebugMode = (uint)debugMode;
        bool isResolveOnlyDebug = debugMode is ClusterDebugMode.ClusterID
            or ClusterDebugMode.LODLevel
            or ClusterDebugMode.SWHWView;
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
        var hBinReserveCounters = graph.CreateBuffer("BinReserveCounters", new BufferDesc
        {
            Size = 4,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
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
            builder =>
            {
                builder.Write(hBinCounts, ResourceState.CopyDest);
                builder.Write(hBinReserveCounters, ResourceState.CopyDest);
            },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinCounts);
                var counterBuf = rgCtx.GetBuffer(hBinReserveCounters);
                if (ctx2 != null && buf != null && counterBuf != null)
                {
                    byte[] zeros = new byte[maxMaterials * 4];
                    Array.Clear(zeros);
                    ctx2.UpdateBuffer(buf, 0, zeros, ResourceStateTransitionMode.None);
                    byte[] counterZeros = new byte[4];
                    ctx2.UpdateBuffer(counterBuf, 0, counterZeros, ResourceStateTransitionMode.None);
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
            HReserveCounters = hBinReserveCounters,
            MaterialCount = clampedMaterialCount,
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
            var resolve = new ClusterResolvePass(context, resources.Resolve);
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
        var hMaterialScalarRegion = CreateDynamicByteAddressBuffer(
            graph,
            "MaterialScalarRegion",
            ClusterMaterialShadePass.GetMaxScalarRegionByteSize(psoGroups));

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
            HMaterialScalarRegion = hMaterialScalarRegion,
            HPixelCoordBuffer = shadeBinOut.PixelCoordBuffer,
            HBinOffsets = shadeBinOut.BinOffsets,
            HBinCounts = shadeBinOut.BinCounts,
            HBinIndirectArgs = shadeBinOut.BinIndirectArgs,
            HOutputColor = hShadeOutput,
            HDeformCache = hDeformCache,
            HCacheOffsets = hCacheOffsets,
            ShadeUniformData = shadeUniformData,
            PSOGroups = psoGroups,
            MaterialFallbacks = resources.MaterialFallbacks.Fallbacks,
        };
        graph.AddPass(shadePass);

        // Copy shade result to final color target if provided
        if (colorTarget.IsValid)
            AddCopyPass(graph, hShadeOutput, colorTarget, screenWidth, screenHeight);

        return (shadeBinOut, new ClusterShadeOutput(colorTarget.IsValid ? colorTarget : hShadeOutput));
    }

    // ─── Helpers ───



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

    private static RenderGraphHandle CreateDynamicByteAddressBuffer(RenderGraph graph, string name, int byteSize)
    {
        int alignedByteSize = Math.Max(16, ((byteSize + 15) / 16) * 16);
        return graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)alignedByteSize,
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
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
