using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
public struct DeformUniforms
{
    public Vector3 QuantOrigin;
    public float QuantStep;
    public uint MaxVisibleClusters;
    public uint MaxDeformCacheBytes;
    public uint MaxClusterVertices;
    public uint CurrentBin;
    public uint CacheStrideBytes;
    public uint CacheScanBlockCount0;
    public uint CacheScanBlockCount1;
    public uint ResetCacheAllocationState;
}

public static partial class ClusterHiZ
{
internal sealed class DeformResources : IDisposable
{
    internal const string ShaderFile = "cluster_deform.slang";
    internal const string PrepareVisibleArgsEntryPoint = "CSDeformPrepareVisibleArgs";
    internal const string InitVisibleEntryPoint = "CSDeformInitVisible";
    internal const string CacheRequestEntryPoint = "CSDeformCacheRequest";
    internal const string CacheScanVisibleEntryPoint = "CSDeformCacheScanVisible";
    internal const string CacheScanBlocks0EntryPoint = "CSDeformCacheScanBlocks0";
    internal const string CacheScanBlocks1EntryPoint = "CSDeformCacheScanBlocks1";
    internal const string CacheApplyBlockOffsetsEntryPoint = "CSDeformCacheApplyBlockOffsets";
    internal const string CacheCommitAllocationEntryPoint = "CSDeformCacheCommitAllocation";
    internal const uint CacheScanBlockSize = 256;

    internal IPipelineState? PrepareVisibleArgsPSO;
    internal IPipelineState? InitVisiblePSO;
    internal IPipelineState? CacheRequestPSO;
    internal IPipelineState? CacheScanVisiblePSO;
    internal IPipelineState? CacheScanBlocks0PSO;
    internal IPipelineState? CacheScanBlocks1PSO;
    internal IPipelineState? CacheApplyBlockOffsetsPSO;
    internal IPipelineState? CacheCommitAllocationPSO;
    internal ShaderAsset? ShaderAsset;
    internal readonly SRBPool PrepareVisibleArgsPool = new();
    internal readonly SRBPool InitVisiblePool = new();
    internal readonly SRBPool CacheRequestPool = new();
    internal readonly SRBPool CacheScanVisiblePool = new();
    internal readonly SRBPool CacheScanBlocks0Pool = new();
    internal readonly SRBPool CacheScanBlocks1Pool = new();
    internal readonly SRBPool CacheApplyBlockOffsetsPool = new();
    internal readonly SRBPool CacheCommitAllocationPool = new();

    private bool _initialized;
    private readonly Lock _initLock = new();

    internal void EnsureInitialized(RenderContext context)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string path = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(path);
            ShaderAsset = shaderAsset;

            var layout = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            // DeformWavePSO/DeformStaticPSO removed �?now built from material pipeline PSO groups



            var csPrepareVisible = shaderAsset.CreateShader(context, PrepareVisibleArgsEntryPoint);
            PrepareVisibleArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformPrepareVisibleArgs",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csPrepareVisible,
            });

            var csInitVisible = shaderAsset.CreateShader(context, InitVisibleEntryPoint);
            InitVisiblePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformInitVisible",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csInitVisible,
            });

            var csCacheRequest = shaderAsset.CreateShader(context, CacheRequestEntryPoint);
            CacheRequestPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheRequest",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheRequest,
            });

            var csCacheScanVisible = shaderAsset.CreateShader(context, CacheScanVisibleEntryPoint);
            CacheScanVisiblePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheScanVisible",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheScanVisible,
            });

            var csCacheScanBlocks0 = shaderAsset.CreateShader(context, CacheScanBlocks0EntryPoint);
            CacheScanBlocks0PSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheScanBlocks0",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheScanBlocks0,
            });

            var csCacheScanBlocks1 = shaderAsset.CreateShader(context, CacheScanBlocks1EntryPoint);
            CacheScanBlocks1PSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheScanBlocks1",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheScanBlocks1,
            });

            var csCacheApplyBlockOffsets = shaderAsset.CreateShader(context, CacheApplyBlockOffsetsEntryPoint);
            CacheApplyBlockOffsetsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheApplyBlockOffsets",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheApplyBlockOffsets,
            });

            var csCacheCommitAllocation = shaderAsset.CreateShader(context, CacheCommitAllocationEntryPoint);
            CacheCommitAllocationPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "CSDeformCacheCommitAllocation",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = layout,
                },
                Cs = csCacheCommitAllocation,
            });
            _initialized = true;
        }
    }

    public void Dispose()
    {
        PrepareVisibleArgsPool.Dispose();
        InitVisiblePool.Dispose();
        CacheRequestPool.Dispose();
        CacheScanVisiblePool.Dispose();
        CacheScanBlocks0Pool.Dispose();
        CacheScanBlocks1Pool.Dispose();
        CacheApplyBlockOffsetsPool.Dispose();
        CacheCommitAllocationPool.Dispose();
        PrepareVisibleArgsPSO?.Dispose();
        InitVisiblePSO?.Dispose();
        CacheRequestPSO?.Dispose();
        CacheScanVisiblePSO?.Dispose();
        CacheScanBlocks0PSO?.Dispose();
        CacheScanBlocks1PSO?.Dispose();
        CacheApplyBlockOffsetsPSO?.Dispose();
        CacheCommitAllocationPSO?.Dispose();
    }
}
}

public class ClusterDeformPass(
    RenderContext context,
    string passName = "ClusterDeform"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    public RenderGraphHandle HBinnedClusterIndex = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformBinMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;

    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public MaterialPSOGroup[]? PSOGroups;
    public DeformUniforms DeformUniformData;

    public void Init() { }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HBinnedClusterIndex, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HDeformBinMeta, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDeformDispatchArgs, ResourceState.IndirectArgument);
        builder.Write(HDeformCache, ResourceState.UnorderedAccess);
        builder.Read(HCacheOffsets, ResourceState.ShaderResource);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx2 = context.ImmediateContext;
        if (ctx2 == null || PSOGroups == null || PSOGroups.Length == 0) return;

        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var binnedBuf = rgCtx.GetBuffer(HBinnedClusterIndex);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var deformBinMetaBuf = rgCtx.GetBuffer(HDeformBinMeta);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var deformCacheBuf = rgCtx.GetBuffer(HDeformCache);
        var cacheOffsetBuf = rgCtx.GetBuffer(HCacheOffsets);
        var deformDispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        if (uniformBuf == null || binnedBuf == null || visibleBuf == null ||
            deformBinMetaBuf == null || pageHeap == null ||
            deformCacheBuf == null || cacheOffsetBuf == null ||
            deformDispatchArgsBuf == null)
            return;

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);

        var uniformData = DeformUniformData;

        foreach (var group in PSOGroups)
        {
            if (group.PSO == null || group.SRB == null) continue;
            var shaderAsset = group.ComputeVariant.Shader;
            if (shaderAsset == null) continue;

            var srb = group.SRB;
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "Uniforms")
                ?.Set(uniformBuf, SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "BinnedClusterIndexBuffer")
                ?.Set(binnedBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "VisibleClusters")
                ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "DeformBinMeta")
                ?.Set(deformBinMetaBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            if (globalTransformView != null)
            {
                srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "Instances")
                    ?.Set(globalTransformView, SetShaderResourceFlags.None);
            }
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "DeformCache")
                ?.Set(deformCacheBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, shaderAsset, ShaderType.Compute, "CacheOffsets")
                ?.Set(cacheOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

            ctx2.SetPipelineState(group.PSO);
            ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);

            for (int i = 0; i < group.BinCount; i++)
            {
                int bin = group.BinStart + i;
                uniformData.CurrentBin = (uint)bin;
                var mapped = ctx2.MapBuffer<DeformUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx2.UnmapBuffer(uniformBuf, MapType.Write);

                ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = deformDispatchArgsBuf,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                    DispatchArgsByteOffset = (ulong)(bin * 12),
                });
            }
        }
    }

    public void Dispose() { }
}

internal class ClusterDeformPrepareVisibleArgsPass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformPrepareVisibleArgs"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Write(HDeformDispatchArgs, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.PrepareVisibleArgsPSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        if (drawArgsBuf == null || dispatchArgsBuf == null) return;

        var srb = resources.PrepareVisibleArgsPool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DeformDispatchArgs")
            ?.Set(dispatchArgsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.PrepareVisibleArgsPool.Return(srb);
    }
}

internal class ClusterDeformInitVisiblePass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformInitVisible"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDeformDispatchArgs, ResourceState.IndirectArgument);
        builder.Write(HCacheOffsets, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.InitVisiblePSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        var cacheOffsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
        if (drawArgsBuf == null || readOffsetBuf == null || visibleBuf == null ||
            uniformBuf == null || dispatchArgsBuf == null || cacheOffsetsBuf == null)
            return;

        var srb = resources.InitVisiblePool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "ReadOffsetArgs")
            ?.Set(readOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheOffsetsWrite")
            ?.Set(cacheOffsetsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None
        });
        resources.InitVisiblePool.Return(srb);
    }
}

internal class ClusterDeformCacheRequestPass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheRequest"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheRequestBytes = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDeformDispatchArgs, ResourceState.IndirectArgument);
        builder.Write(HCacheRequestBytes, ResourceState.UnorderedAccess);
        builder.Write(HCacheOffsets, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheRequestPSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var visibleBuf = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeapBuf = rgCtx.GetBuffer(HPageHeap);
        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        var requestBuf = rgCtx.GetBuffer(HCacheRequestBytes);
        var cacheOffsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
        if (drawArgsBuf == null || readOffsetBuf == null || visibleBuf == null ||
            pageHeapBuf == null || uniformBuf == null || dispatchArgsBuf == null ||
            requestBuf == null || cacheOffsetsBuf == null)
            return;

        var srb = resources.CacheRequestPool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "ReadOffsetArgs")
            ?.Set(readOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "PageHeap")
            ?.Set(pageHeapBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheRequestBytes")
            ?.Set(requestBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheOffsetsWrite")
            ?.Set(cacheOffsetsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None
        });
        resources.CacheRequestPool.Return(srb);
    }
}

internal class ClusterDeformCacheScanVisiblePass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheScanVisible"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheRequestBytes = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockSums0 = RenderGraphHandle.Invalid;
    public uint ThreadGroupCountX;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HCacheRequestBytes, ResourceState.UnorderedAccess);
        builder.Write(HCacheOffsets, ResourceState.UnorderedAccess);
        builder.Write(HCacheBlockSums0, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheScanVisiblePSO;
        if (pso == null || ThreadGroupCountX == 0) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var requestBuf = rgCtx.GetBuffer(HCacheRequestBytes);
        var cacheOffsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
        var blockSums0Buf = rgCtx.GetBuffer(HCacheBlockSums0);
        if (drawArgsBuf == null || readOffsetBuf == null || uniformBuf == null ||
            requestBuf == null || cacheOffsetsBuf == null || blockSums0Buf == null)
            return;

        var srb = resources.CacheScanVisiblePool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "ReadOffsetArgs")
            ?.Set(readOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheRequestBytes")
            ?.Set(requestBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheOffsetsWrite")
            ?.Set(cacheOffsetsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockSums0")
            ?.Set(blockSums0Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = ThreadGroupCountX, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.CacheScanVisiblePool.Return(srb);
    }
}

internal class ClusterDeformCacheScanBlocks0Pass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheScanBlocks0"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockSums0 = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockOffsets0 = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockSums1 = RenderGraphHandle.Invalid;
    public uint ThreadGroupCountX;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HCacheBlockSums0, ResourceState.UnorderedAccess);
        builder.Write(HCacheBlockOffsets0, ResourceState.UnorderedAccess);
        builder.Write(HCacheBlockSums1, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheScanBlocks0PSO;
        if (pso == null || ThreadGroupCountX == 0) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var sums0Buf = rgCtx.GetBuffer(HCacheBlockSums0);
        var offsets0Buf = rgCtx.GetBuffer(HCacheBlockOffsets0);
        var sums1Buf = rgCtx.GetBuffer(HCacheBlockSums1);
        if (uniformBuf == null || sums0Buf == null || offsets0Buf == null || sums1Buf == null)
            return;

        var srb = resources.CacheScanBlocks0Pool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockSums0")
            ?.Set(sums0Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockOffsets0")
            ?.Set(offsets0Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockSums1")
            ?.Set(sums1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = ThreadGroupCountX, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.CacheScanBlocks0Pool.Return(srb);
    }
}

internal class ClusterDeformCacheScanBlocks1Pass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheScanBlocks1"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockSums1 = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockOffsets1 = RenderGraphHandle.Invalid;
    public uint ThreadGroupCountX;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HCacheBlockSums1, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheBlockOffsets1, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheScanBlocks1PSO;
        if (pso == null || ThreadGroupCountX == 0) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var sums1Buf = rgCtx.GetBuffer(HCacheBlockSums1);
        var offsets1Buf = rgCtx.GetBuffer(HCacheBlockOffsets1);
        if (uniformBuf == null || sums1Buf == null || offsets1Buf == null)
            return;

        var srb = resources.CacheScanBlocks1Pool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockSums1")
            ?.Set(sums1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockOffsets1")
            ?.Set(offsets1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = ThreadGroupCountX, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.CacheScanBlocks1Pool.Return(srb);
    }
}

internal class ClusterDeformCacheApplyBlockOffsetsPass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheApplyBlockOffsets"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HReadOffsetArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformDispatchArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheRequestBytes = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockOffsets0 = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockOffsets1 = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDrawArgs, ResourceState.ShaderResource);
        builder.Read(HReadOffsetArgs, ResourceState.ShaderResource);
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDeformDispatchArgs, ResourceState.IndirectArgument);
        builder.ReadWrite(HCacheRequestBytes, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheOffsets, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheBlockOffsets0, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheBlockOffsets1, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheApplyBlockOffsetsPSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var drawArgsBuf = rgCtx.GetBuffer(HDrawArgs);
        var readOffsetBuf = rgCtx.GetBuffer(HReadOffsetArgs);
        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var dispatchArgsBuf = rgCtx.GetBuffer(HDeformDispatchArgs);
        var requestBuf = rgCtx.GetBuffer(HCacheRequestBytes);
        var cacheOffsetsBuf = rgCtx.GetBuffer(HCacheOffsets);
        var blockOffsets0Buf = rgCtx.GetBuffer(HCacheBlockOffsets0);
        var blockOffsets1Buf = rgCtx.GetBuffer(HCacheBlockOffsets1);
        if (drawArgsBuf == null || readOffsetBuf == null || uniformBuf == null ||
            dispatchArgsBuf == null || requestBuf == null || cacheOffsetsBuf == null ||
            blockOffsets0Buf == null || blockOffsets1Buf == null)
            return;

        var srb = resources.CacheApplyBlockOffsetsPool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgsBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "ReadOffsetArgs")
            ?.Set(readOffsetBuf.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheRequestBytes")
            ?.Set(requestBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheOffsetsWrite")
            ?.Set(cacheOffsetsBuf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockOffsets0")
            ?.Set(blockOffsets0Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockOffsets1")
            ?.Set(blockOffsets1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = dispatchArgsBuf,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None
        });
        resources.CacheApplyBlockOffsetsPool.Return(srb);
    }
}

internal class ClusterDeformCacheCommitAllocationPass(
    RenderContext context,
    ClusterHiZ.DeformResources resources,
    string passName = "DeformCacheCommitAllocation"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HDeformUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockSums1 = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheBlockOffsets1 = RenderGraphHandle.Invalid;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDeformUniforms, ResourceState.ConstantBuffer);
        builder.ReadWrite(HCacheBlockSums1, ResourceState.UnorderedAccess);
        builder.ReadWrite(HCacheBlockOffsets1, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.CacheCommitAllocationPSO;
        if (pso == null) return;

        var ctx2 = context.ImmediateContext;
        if (ctx2 == null) return;

        var uniformBuf = rgCtx.GetBuffer(HDeformUniforms);
        var sums1Buf = rgCtx.GetBuffer(HCacheBlockSums1);
        var offsets1Buf = rgCtx.GetBuffer(HCacheBlockOffsets1);
        if (uniformBuf == null || sums1Buf == null || offsets1Buf == null)
            return;

        var srb = resources.CacheCommitAllocationPool.Rent(pso);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockSums1")
            ?.Set(sums1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CacheBlockOffsets1")
            ?.Set(offsets1Buf.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx2.SetPipelineState(pso);
        ctx2.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx2.DispatchCompute(new DispatchComputeAttribs
            { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
        resources.CacheCommitAllocationPool.Return(srb);
    }
}
