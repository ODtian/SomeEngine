using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

// ─── GPU uniform struct matching sw_raster.slang SWRasterUniforms ───
[StructLayout(LayoutKind.Sequential)]
public struct SWRasterUniforms
{
    // float4x4 ViewProj (row-major → column-major transpose before upload)
    public System.Numerics.Matrix4x4 ViewProj;
    public System.Numerics.Vector3 QuantOrigin;
    public float QuantStep;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaxBins;
    public uint DebugDump;
    public uint CurrentBin;
    public uint _pad0;
    public uint _pad1;
    public uint _pad2;
}

// ─── Static PSO/SRB cache ───
public static class ClusterSWRasterPSOs
{
    internal static IPipelineState? SWRasterPSO;
    internal static IPipelineState? SWRasterCachedPSO;
    internal static readonly ConcurrentBag<IShaderResourceBinding> SRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> CachedSRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static IShaderResourceBinding RentSRB(bool cached)
    {
        var pool = cached ? CachedSRBPool : SRBPool;
        var pso = cached ? SWRasterCachedPSO! : SWRasterPSO!;
        return pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);
    }

    internal static void ReturnSRB(IShaderResourceBinding srb, bool cached)
        => (cached ? CachedSRBPool : SRBPool).Add(srb);

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string path = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/sw_raster.slang"
                )
            );
            var shaderAsset = SlangShaderImporter.Import(path);

            var psoLayout = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            using var cs = shaderAsset.CreateShader(context, "CSSWRaster");
            SWRasterPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSSWRaster",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = psoLayout,
                },
                Cs = cs,
            });

            using var csCached = shaderAsset.CreateShader(context, "CSSWRasterCached");
            SWRasterCachedPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSSWRasterCached",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = psoLayout,
                },
                Cs = csCached,
            });

            s_initialized = true;
        }
    }
}

// ─── RenderGraph Pass (per-frame lightweight instance) ───
public class ClusterSWRasterPass(
    RenderContext context,
    string passName = "ClusterSWRaster"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    // ─── Input handles (set by caller) ───
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedClusterIndex = RenderGraphHandle.Invalid;
    public RenderGraphHandle HRasterBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HSWRasterUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinnedSWDispatchArgs = RenderGraphHandle.Invalid;

    // ─── DeformCache handles (optional — validity = enabled) ───
    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    // ─── Output handles (set by caller) ───
    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthUAV = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugSWOutput = RenderGraphHandle.Invalid;

    // ─── Uniform data (set by caller, modified per-bin in Execute) ───
    public SWRasterUniforms SWRasterUniformData;

    // ─── PSO Groups (from material pipeline) ───
    public ShadePSOGroup[]? PSOGroups;
    public uint TotalBinCount { get; set; } = 1;

    public void Init() { }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HBinnedClusterIndex, ResourceState.ShaderResource);
        builder.Read(HRasterBinMeta, ResourceState.ShaderResource);
        builder.Read(HSWRasterUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HBinnedSWDispatchArgs, ResourceState.IndirectArgument);
        if (HDeformCache.IsValid)
        {
            builder.Read(HDeformCache, ResourceState.ShaderResource);
            builder.Read(HCacheOffsets, ResourceState.ShaderResource);
        }
        builder.Write(HVisBuffer, ResourceState.UnorderedAccess);
        builder.Write(HDepthUAV, ResourceState.UnorderedAccess);
        if (HDebugSWOutput.IsValid)
            builder.Write(HDebugSWOutput, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || PSOGroups == null || PSOGroups.Length == 0) return;

        var uniformBuf = rgCtx.GetBuffer(HSWRasterUniforms);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var binnedBuf  = rgCtx.GetBuffer(HBinnedClusterIndex);
        var metaBuf    = rgCtx.GetBuffer(HRasterBinMeta);
        var pageHeap   = rgCtx.GetBuffer(HPageHeap);
        var swDispatchArgsBuf = rgCtx.GetBuffer(HBinnedSWDispatchArgs);
        if (uniformBuf == null || visibleBuf == null || binnedBuf == null || metaBuf == null || pageHeap == null || swDispatchArgsBuf == null)
            return;

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);

        var visBufferUAV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.UnorderedAccess);
        var depthUAV     = rgCtx.GetTextureView(HDepthUAV, TextureViewType.UnorderedAccess);
        if (visBufferUAV == null || depthUAV == null) return;

        bool cached = HDeformCache.IsValid;
        var uniformData = SWRasterUniformData;

        foreach (var group in PSOGroups)
        {
            if (group.PSO == null) continue;

            var srb = group.PSO.CreateShaderResourceBinding(false);

            srb.GetVariableByName(ShaderType.Compute, "Uniforms")
                ?.Set(uniformBuf, SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
                ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binnedBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "RasterBinMeta")
                ?.Set(metaBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            if (globalTransformView != null)
            {
                srb.GetVariableByName(ShaderType.Compute, "Instances")
                    ?.Set(globalTransformView, SetShaderResourceFlags.None);
            }
            srb.GetVariableByName(ShaderType.Compute, "VisBuffer")
                ?.Set(visBufferUAV, SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "DepthUAV")
                ?.Set(depthUAV, SetShaderResourceFlags.None);

            if (cached)
            {
                var cacheBuf = rgCtx.GetBuffer(HDeformCache);
                var offsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
                if (cacheBuf != null)
                    srb.GetVariableByName(ShaderType.Compute, "DeformCache")
                        ?.Set(cacheBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
                if (offsetsBuf != null)
                    srb.GetVariableByName(ShaderType.Compute, "CacheOffsets")
                        ?.Set(offsetsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            }

            if (HDebugSWOutput.IsValid)
            {
                var debugBuf = rgCtx.GetBuffer(HDebugSWOutput);
                var debugVar = srb.GetVariableByName(ShaderType.Compute, "DebugSWOutput");
                if (debugBuf != null && debugVar != null)
                    debugVar.Set(debugBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            }

            ctx.SetPipelineState(group.PSO);
            ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);

            for (int i = 0; i < group.BinCount; i++)
            {
                int bin = group.BinStart + i;
                uniformData.CurrentBin = (uint)bin;
                var mapped = ctx.MapBuffer<SWRasterUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx.UnmapBuffer(uniformBuf, MapType.Write);

                ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = swDispatchArgsBuf,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                    DispatchArgsByteOffset = (ulong)((TotalBinCount + (uint)bin) * 12),
                });
            }
        }
    }

    public void Dispose() { }
}
