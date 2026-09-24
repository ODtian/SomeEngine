using SomeEngine.Assets;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeECS.Core;
using SomeECS.Core.Components;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;
using SomeEngine.Core.Diagnostics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct SlotFrame(
    RenderGraphHandle Buffer,
    uint SlotCapacity,
    uint RasterBinFieldIndex,
    uint ShadingBinFieldIndex,
    uint VertexEvalFieldIndex,
    uint RasterBinCount,
    uint ShadingBinCount,
    uint VertexEvalBinCount);

internal readonly record struct MaterialQueries(
    QueryHandle Instances,
    QueryHandle AddedMaterials,
    QueryHandle ChangedMaterials,
    QueryHandle ChangedInstances,
    QueryHandle RemovedInstances)
{
    public static MaterialQueries From(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new MaterialQueries(
            world.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderInstance>()
                    .Read<RenderMaterials>()),
            world.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderInstance>()
                    .Read<RenderMaterials>()
                    .Added<RenderMaterials>()),
            world.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderInstance>()
                    .Read<RenderMaterials>()
                    .Changed<RenderMaterials>()),
            world.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderInstance>()
                    .Read<RenderMaterials>()
                    .Read<InstanceDirty>()
                    .Enabled<InstanceDirty>()),
            world.Query(
                new QueryDefinitionBuilder()
                    .Removed<RenderInstance>()));
    }
}

internal sealed class MaterialItems : IPipelineSource, IDisposable
{
    private const int RasterField = 0;
    private const int ShadeField = 1;
    private const int DeformField = 2;
    private const int FieldCount = 3;
    private const int InvalidSlotCount = 256;
    private const string ShadeTarget = "cluster.shade";
    private const string CachedShadeTarget = "cluster.shade.cached";
    private const string SoftwareTarget = "cluster.raster.sw";
    private const string CachedSoftwareTarget = "cluster.raster.sw.cached";
    private const string VertexTarget = "cluster.raster.vs";
    private const string CachedVertexTarget = "cluster.raster.vs.cached";
    private const string PixelTarget = "cluster.raster.ps";
    private const string DeformTarget = "cluster.deform.eval";
    private static readonly string[] ShadeResources =
    [
        "Uniforms",
        "PixelCoordBuffer",
        "BinOffsets",
        "BinCounts",
        "VisBuffer",
        "VisibleClusters",
        "PageHeap",
        "OutputColor",
        "OutputMotionVectors",
        "PreviousInstances",
        "Instances",
        "InstanceHeaders",
        "InstanceDataHeap",
        "MaterialScalarRegion",
        "AlbedoMap",
        "NormalMap",
        "ARMMap",
        "EmissiveMap",
        "MaterialSampler",
        "LightBuffer",
        "LightCounts",
        "LightGridUniforms",
        "ClusterLightGrid",
        "LightIndexList",
        "LightCookieAtlas",
        "LightCookieSampler",
    ];
    private static readonly string[] CachedShadeResources =
    [
        .. ShadeResources,
        "DeformCache",
        "CacheOffsets",
    ];
    private static readonly string[] SoftwareResources =
    [
        "PageHeap",
        "VisibleClusters",
        "BinnedClusterIndexBuffer",
        "RasterBinMeta",
        "DepthTarget",
        "VisBuffer",
        "DepthUAV",
        "DebugSWOutput",
        "Instances",
        "InstanceHeaders",
        "InstanceDataHeap",
    ];
    private static readonly string[] CachedSoftwareResources =
    [
        .. SoftwareResources,
        "DeformCache",
        "CacheOffsets",
    ];
    private static readonly string[] DrawResources =
    [
        "Uniforms",
        "DispatchUniforms",
        "PageHeap",
        "BinnedClusterIndexBuffer",
        "VisibleClusters",
        "VisibleClusterMeta",
        "Instances",
        "InstanceHeaders",
        "InstanceDataHeap",
    ];
    private static readonly string[] CachedDrawResources =
    [
        .. DrawResources,
        "DeformCache",
        "CacheOffsets",
    ];
    private static readonly string[] DeformResources =
    [
        "PageHeap",
        "VisibleClusters",
        "BinnedClusterIndexBuffer",
        "DeformBinMeta",
        "PreviousInstances",
        "Instances",
        "InstanceHeaders",
        "InstanceDataHeap",
        "MaterialScalarRegion",
        "DeformCache",
        "CacheOffsetsWrite",
        "CacheAllocationCounter",
    ];

    private readonly RenderContext _context;
    private readonly AssetStore _assets;
    private readonly MaterialGpu _materialGpu;
    private readonly PipelineCollector _pipelineCollector = new();
    private readonly List<BindingLayoutHandle> _layouts = [];
    private readonly List<MaterialBin[]> _retired = [];
    private readonly List<EntityId> _markEntities = [];
    private readonly List<MaterialBin> _pipelineBins = [];
    private uint _pipelineVersion = 1;
    private readonly MaterialVariantState _uncached;
    private readonly MaterialVariantState _cached;
    private MaterialVariantState? _active;
    private RenderGraph? _lastGraph;
    private bool _framePrepared;

    public MaterialItems(
        RenderContext context,
        AssetStore assets,
        IDevice device)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _uncached = new MaterialVariantState(device, FieldCount);
        _cached = new MaterialVariantState(device, FieldCount);
        _materialGpu = new MaterialGpu(device);
    }

    public IReadOnlyList<MaterialBin> Shade => Active.Shade;
    public IReadOnlyList<MaterialBin> Sw => Active.Sw;
    public IReadOnlyList<MaterialBin> Draw => Active.Draw;
    public IReadOnlyList<MaterialBin> Deform => Active.Deform;
    public int RasterCount => Active.RasterCount;
    public int SlotCapacity => Active.Slots.Capacity;
    public uint Version => _pipelineVersion;
    public InstanceHeaderData Headers { get; } = new();

    private MaterialVariantState Active => _active ?? _uncached;

    public void PrepareFrame(
        RenderWorld world,
        bool useCache)
    {
        ArgumentNullException.ThrowIfNull(world);
        _framePrepared = false;
        World source = world.World;
        MaterialQueries queries = MaterialQueries.From(source);
        uint current = source.AcquireSystemTick();

        MaterialVariantState state = useCache ? _cached : _uncached;
        _active = state;
        uint worldMaterialVersion = world.MaterialVersion;
        ulong materialStoreVersion = _assets.GetVersion<Material>();
        ulong shaderStoreVersion = _assets.GetVersion<Shader>();

        bool rebuild = NeedsBuild(state, useCache, materialStoreVersion, shaderStoreVersion);
        if (rebuild)
        {
            using (Profiler.BeginScope("MaterialItems.Build"))
            {
                Build(state, source, queries, useCache, current, worldMaterialVersion);
            }
        }
        else
        {
            using (Profiler.BeginScope("MaterialItems.UpdateSlots"))
            {
                bool bindingsChanged = UpdateBindings(state, materialStoreVersion);
                bool slotsChanged = UpdateSlots(state, source, queries, useCache, current, worldMaterialVersion);
                if (bindingsChanged || slotsChanged)
                    CollectPipelines();
            }
        }

        state.MaterialStoreVersion = materialStoreVersion;
        state.ShaderStoreVersion = shaderStoreVersion;
        _framePrepared = true;
    }

    public SlotFrame RecordFrame(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!_framePrepared)
            throw new InvalidOperationException("MaterialItems.PrepareFrame must be called before RecordFrame.");

        MaterialVariantState state = Active;
        bool useCache = ReferenceEquals(state, _cached);
        if (_lastGraph != null && !ReferenceEquals(_lastGraph, graph))
            Clear(_lastGraph);
        _lastGraph = graph;
        Retire(graph);
        graph.UsePipelineCache(_context.PipelineCache);

        UpdateScalars(graph, state.Shade, "Cluster Shade");
        UpdateScalars(graph, state.Sw, "Cluster SW Raster");
        UpdateScalars(graph, state.Draw, "Cluster Draw");
        UpdateScalars(graph, state.Deform, "Cluster Deform");

        RenderGraphHandle slots = state.SlotGpu.Add(graph, state.Slots);
        return new SlotFrame(
            slots,
            checked((uint)Math.Max(state.Slots.Capacity, 1)),
            RasterField,
            ShadeField,
            useCache && state.Deform.Length > 0 ? DeformField : uint.MaxValue,
            checked((uint)Math.Max(state.RasterCount, 1)),
            checked((uint)Math.Max(state.ShadeCount, 1)),
            checked((uint)Math.Max(state.DeformCount, 1)));
    }

    public void Dispose()
    {
        if (_lastGraph != null && !_lastGraph.IsDisposed)
            Clear(_lastGraph);
        _lastGraph = null;

        Drop();
        Dispose(_uncached);
        Dispose(_cached);
        DisposeRetired();
        _materialGpu.Dispose();
    }

    private void UpdateScalars(RenderGraph graph, MaterialBin[] states, string prefix)
    {
        for (int i = 0; i < states.Length; i++)
        {
            Material? material = states[i].Material;
            if (states[i].MaterialHandle.IsValid && material == null)
                throw new InvalidOperationException($"{prefix} material bin {i} is missing material state.");

            states[i].MaterialScalarRegion = _materialGpu.Add(
                graph,
                material,
                states[i].ScalarLayout,
                $"{prefix} MaterialScalarRegion {i}");
        }
    }

    private void Build(
        MaterialVariantState state,
        World world,
        MaterialQueries queries,
        bool useCache,
        uint current,
        uint worldMaterialVersion)
    {
        ClearPending();
        try
        {
            if (_lastGraph != null)
                Drop(_lastGraph, waitForGpu: true);
            Retire(ref state.Shade);
            Retire(ref state.Sw);
            Retire(ref state.Draw);
            Retire(ref state.Deform);

            state.Reset(FieldCount);
            Headers.Clear();
            int active = 0;
            AddInvalidSlots(state);

            foreach (QueryChunkView chunk in world.RunQuery(queries.Instances, 0, current).Chunks)
            {
                ReadOnlySpan<RenderInstance> instances = chunk.Read<RenderInstance>();
                ReadOnlySpan<RenderMaterials> materials = chunk.Read<RenderMaterials>();
                for (int i = 0; i < instances.Length; i++)
                {
                    RenderInstance instance = instances[i];
                    active++;
                    WriteSlots(state, instance, materials[i].Materials.Span, useCache);
                }
            }

            if (state.RasterCount == 0 && active > 0)
            {
                throw new InvalidOperationException(
                    "cluster pipeline produced zero raster bins for active mesh instances. Attach MeshMaterialBindings with a material that exposes cluster raster MaterialPass entries.");
            }

            CollectPipelines();

            state.Built = true;
            state.LastTick = current;
            state.WorldMaterialVersion = worldMaterialVersion;
        }
        catch
        {
            ClearPending();
            Dispose(state.Shade);
            Dispose(state.Sw);
            Dispose(state.Draw);
            Dispose(state.Deform);
            state.Shade = [];
            state.Sw = [];
            state.Draw = [];
            state.Deform = [];
            TouchPipelines();
            throw;
        }
    }

    private bool UpdateSlots(
        MaterialVariantState state,
        World world,
        MaterialQueries queries,
        bool useCache,
        uint current,
        uint worldMaterialVersion)
    {
        if (state.LastTick == current && state.WorldMaterialVersion == worldMaterialVersion)
            return false;

        _markEntities.Clear();
        bool slotsChanged = ClearRemoved(state, world, queries, current);
        UpdateRows(state, world, queries.AddedMaterials, useCache, current);
        UpdateRows(state, world, queries.ChangedMaterials, useCache, current);
        UpdateBounds(state, world, queries, useCache, current);
        slotsChanged |= _markEntities.Count != 0;

        for (int i = 0; i < _markEntities.Count; i++)
            InstanceMarks.Mark(world, _markEntities[i], InstanceDirtyFlags.Header);

        state.LastTick = current;
        state.WorldMaterialVersion = worldMaterialVersion;
        return slotsChanged;
    }

    private void UpdateRows(
        MaterialVariantState state,
        World world,
        QueryHandle query,
        bool useCache,
        uint current)
    {
        foreach (QueryChunkView chunk in world.RunQuery(query, state.LastTick, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                RenderInstance instance = chunk.Read<RenderInstance>(row);
                RenderMaterials materials = chunk.Read<RenderMaterials>(row);
                WriteSlots(state, instance, materials.Materials.Span, useCache);
                _markEntities.Add(chunk.GetEntity(row));
            }
        }
    }

    private void UpdateBounds(
        MaterialVariantState state,
        World world,
        MaterialQueries queries,
        bool useCache,
        uint current)
    {
        foreach (QueryChunkView chunk in world.RunQuery(queries.ChangedInstances).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                InstanceDirty dirty = chunk.Read<InstanceDirty>(row);
                if ((dirty.Flags & InstanceDirtyFlags.MaterialHeader) == 0)
                    continue;

                RenderInstance instance = chunk.Read<RenderInstance>(row);
                RenderMaterials materials = chunk.Read<RenderMaterials>(row);
                WriteHeader(state, instance, materials.Materials.Span, useCache);
                _markEntities.Add(chunk.GetEntity(row));
            }
        }
    }

    private bool ClearRemoved(
        MaterialVariantState state,
        World world,
        MaterialQueries queries,
        uint current)
    {
        bool changed = false;
        foreach (QueryChunkView chunk in world.RunQuery(queries.RemovedInstances, state.LastTick, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                Removed<RenderInstance> removed = chunk.Read<Removed<RenderInstance>>(row);
                int index = removed.Value.InstanceIndex;
                if ((uint)index >= (uint)state.SlotCounts.Length)
                    continue;

                ClearSlots(state, index);
                Headers.SetU32(index, InstanceHeaderLayout.SlotOffset, 0);
                Headers.SetFloat32(index, InstanceHeaderLayout.BoundsExpansionWorld, 0);
                changed = true;
            }
        }

        world.ClearRemoved<RenderInstance>(current);
        return changed;
    }

    private void WriteSlots(
        MaterialVariantState state,
        RenderInstance instance,
        ReadOnlySpan<Handle<Material>> materials,
        bool useCache)
    {
        int index = instance.InstanceIndex;
        if (index < 0)
            throw new InvalidOperationException("render instance index must be non-negative.");

        EnsureSlots(state, index);
        float bounds = MathF.Max(0f, instance.BoundsExpansion);
        if (materials.IsEmpty)
        {
            ClearSlots(state, index);
            Headers.SetU32(index, InstanceHeaderLayout.SlotOffset, 0);
            Headers.SetFloat32(index, InstanceHeaderLayout.BoundsExpansionWorld, bounds);
            return;
        }

        int offset = SlotRange(state, index, materials.Length);
        int deformRef = int.MinValue;
        for (int slot = 0; slot < materials.Length; slot++)
        {
            int itemIndex = ItemIndex(state, materials[slot], useCache);
            int raster = itemIndex >= 0 ? state.RasterBins[itemIndex] : -1;
            int shade = itemIndex >= 0 ? state.ShadeBins[itemIndex] : -1;
            int deform = itemIndex >= 0 ? state.DeformBins[itemIndex] : -1;
            CheckDeform(useCache, ref deformRef, deform);

            state.Slots.SetField(offset, slot, RasterField, ToSlot(raster));
            state.Slots.SetField(offset, slot, ShadeField, ToSlot(shade));
            state.Slots.SetField(offset, slot, DeformField, ToSlot(deform));

            if (itemIndex >= 0)
                bounds = MathF.Max(bounds, state.Items[itemIndex].BoundsExpansion());
        }

        Headers.SetU32(index, InstanceHeaderLayout.SlotOffset, checked((uint)offset));
        Headers.SetFloat32(index, InstanceHeaderLayout.BoundsExpansionWorld, bounds);
    }

    private void WriteHeader(
        MaterialVariantState state,
        RenderInstance instance,
        ReadOnlySpan<Handle<Material>> materials,
        bool useCache)
    {
        int index = instance.InstanceIndex;
        if ((uint)index >= (uint)state.SlotCounts.Length)
            return;

        float bounds = MathF.Max(0f, instance.BoundsExpansion);
        for (int slot = 0; slot < materials.Length; slot++)
        {
            int itemIndex = ItemIndex(state, materials[slot], useCache);
            if (itemIndex >= 0)
                bounds = MathF.Max(bounds, state.Items[itemIndex].BoundsExpansion());
        }

        uint offset = state.SlotCounts[index] > 0
            ? checked((uint)state.SlotOffsets[index])
            : 0u;
        Headers.SetU32(index, InstanceHeaderLayout.SlotOffset, offset);
        Headers.SetFloat32(index, InstanceHeaderLayout.BoundsExpansionWorld, bounds);
    }

    private void AddInvalidSlots(MaterialVariantState state)
    {
        int offset = state.Slots.AllocateRange(InvalidSlotCount);
        if (offset != 0)
            throw new InvalidOperationException("cluster invalid material slots must start at zero.");

        for (int slot = 0; slot < InvalidSlotCount; slot++)
        {
            state.Slots.SetField(0, slot, RasterField, ushort.MaxValue);
            state.Slots.SetField(0, slot, ShadeField, ushort.MaxValue);
            state.Slots.SetField(0, slot, DeformField, ushort.MaxValue);
        }
    }

    private int SlotRange(MaterialVariantState state, int index, int count)
    {
        if (state.SlotCounts[index] == count)
            return state.SlotOffsets[index];

        ClearSlots(state, index);
        int offset = state.Slots.AllocateRange(count);
        state.SlotOffsets[index] = offset;
        state.SlotCounts[index] = count;
        return offset;
    }

    private void ClearSlots(MaterialVariantState state, int index)
    {
        int count = state.SlotCounts[index];
        if (count > 0)
            state.Slots.FreeRange(state.SlotOffsets[index], count);

        state.SlotOffsets[index] = -1;
        state.SlotCounts[index] = 0;
    }

    private static void EnsureSlots(MaterialVariantState state, int index)
    {
        if (state.SlotOffsets.Length > index)
            return;

        int old = state.SlotOffsets.Length;
        int size = Math.Max(index + 1, old == 0 ? 16 : old * 2);
        Array.Resize(ref state.SlotOffsets, size);
        Array.Resize(ref state.SlotCounts, size);
        Array.Fill(state.SlotOffsets, -1, old, size - old);
    }

    private static void CheckDeform(bool useCache, ref int first, int current)
    {
        if (!useCache)
            return;

        if (first == int.MinValue)
        {
            first = current;
            return;
        }

        if (first != current)
        {
            throw new InvalidOperationException(
                "cluster deform cache requires all local material slots on one instance to resolve to the same deform pass state.");
        }
    }

    private int ItemIndex(MaterialVariantState state, Handle<Material> handle, bool useCache)
    {
        if (!handle.IsValid || !_assets.TryGet(handle, out Material? material) || material == null)
            return -1;

        for (int i = 0; i < state.ItemCount; i++)
        {
            if (state.Items[i].Handle == handle)
                return i;
        }

        MaterialItem item = MakeItem(handle, material);
        CheckCache(item, useCache);
        if (!item.HasRaster(useCache) && !item.HasShade(useCache) && !(useCache && item.HasDeform()))
            return -1;

        int index = AddItem(state, item);
        Add(ref state.RasterBins, index, item.HasRaster(useCache) ? AddRaster(state, item, useCache) : -1);
        Add(ref state.ShadeBins, index, item.HasShade(useCache) ? AddShade(state, item, useCache) : -1);
        Add(ref state.DeformBins, index, useCache && item.HasDeform() ? AddDeform(state, item) : -1);
        return index;
    }

    private MaterialItem MakeItem(Handle<Material> handle, Material material)
    {
        MaterialItem item = new()
        {
            Handle = handle,
            Material = material,
            PassVersion = material.PassVersion,
            BindingVersion = material.BindingVersion,
            State = MaterialState.Default,
        };

        foreach (MaterialPass pass in material.Passes)
        {
            if (!_assets.TryGet(pass.Shader, out Shader? shader) || shader == null)
                continue;

            if (item.State != MaterialState.Default && item.State != pass.State)
                throw new InvalidOperationException("cluster material passes for one material must use the same MaterialState.");

            if (!ParseTarget(pass.Target, out ClusterPass target))
                continue;

            PassShader variant = new(pass.Shader, pass.EntryPoint);
            item = target switch
            {
                ClusterPass.Shade => item with { Shade = variant, State = pass.State },
                ClusterPass.CachedShade => item with { ShadeCache = variant, State = pass.State },
                ClusterPass.SwRaster => item with { Sw = variant, State = pass.State },
                ClusterPass.CachedSwRaster => item with { SwCache = variant, State = pass.State },
                ClusterPass.VsRaster => item with { Vs = variant, State = pass.State },
                ClusterPass.CachedVsRaster => item with { VsCache = variant, State = pass.State },
                ClusterPass.PsRaster => item with { Ps = variant, State = pass.State },
                ClusterPass.DeformEval => item with { Deform = variant, State = pass.State },
                _ => item,
            };
        }

        return item;
    }

    private static bool ParseTarget(string? target, out ClusterPass pass)
    {
        pass = target switch
        {
            ShadeTarget => ClusterPass.Shade,
            CachedShadeTarget => ClusterPass.CachedShade,
            SoftwareTarget => ClusterPass.SwRaster,
            CachedSoftwareTarget => ClusterPass.CachedSwRaster,
            VertexTarget => ClusterPass.VsRaster,
            CachedVertexTarget => ClusterPass.CachedVsRaster,
            PixelTarget => ClusterPass.PsRaster,
            DeformTarget => ClusterPass.DeformEval,
            _ => default,
        };

        return target is ShadeTarget
            or CachedShadeTarget
            or SoftwareTarget
            or CachedSoftwareTarget
            or VertexTarget
            or CachedVertexTarget
            or PixelTarget
            or DeformTarget;
    }

    private static void CheckCache(MaterialItem item, bool useCache)
    {
        if (!useCache)
            return;

        bool raster = item.HasRaster(false);
        bool shade = item.HasShade(false);
        bool cachedRaster = item.HasRaster(true);
        bool cachedShade = item.HasShade(true);
        bool needsCache = raster || shade || item.HasCache();
        if (!needsCache)
            return;

        if (raster && !cachedRaster)
            throw new InvalidOperationException("cluster deform cache requires a cached raster MaterialPass target for every raster material.");
        if (shade && !cachedShade)
            throw new InvalidOperationException("cluster deform cache requires a cached shade MaterialPass target for every shaded material.");
        if (!item.HasDeform())
            throw new InvalidOperationException("cluster deform cache requires a cluster deform MaterialPass target.");
    }

    private int AddRaster(MaterialVariantState state, MaterialItem item, bool useCache)
    {
        int bin = state.RasterCount++;
        PassShader sw = ClusterVariants.Sw(item, useCache);
        if (!sw.IsEmpty)
            AddCompute(
                ref state.Sw,
                sw,
                item.RasterState(useCache),
                item.Handle,
                bin,
                "Cluster SW Raster",
                useCache ? CachedSoftwareResources : SoftwareResources,
                [
                    new PushRangeDesc
                    {
                        Stages = ShaderStageFlags.Compute,
                        Offset = 0,
                        SizeInBytes = checked((uint)Marshal.SizeOf<SwRasterUniforms>()),
                    }
                ]);

        PassShader vs = ClusterVariants.Vs(item, useCache);
        if (!vs.IsEmpty && !item.Ps.IsEmpty)
            AddGraphics(
                ref state.Draw,
                vs,
                item.Ps,
                item.DrawState(useCache),
                item.Handle,
                bin,
                "Cluster Draw",
                useCache ? CachedDrawResources : DrawResources);

        return bin;
    }

    private int AddShade(MaterialVariantState state, MaterialItem item, bool useCache)
    {
        int bin = state.ShadeCount++;
        AddCompute(
            ref state.Shade,
            ClusterVariants.Shade(item, useCache),
            item.ShadePassState(useCache),
            item.Handle,
            bin,
            "Cluster Shade",
            useCache ? CachedShadeResources : ShadeResources);
        return bin;
    }

    private int AddDeform(MaterialVariantState state, MaterialItem item)
    {
        int bin = state.DeformCount++;
        AddCompute(
            ref state.Deform,
            item.Deform,
            item.State,
            item.Handle,
            bin,
            "Cluster Deform",
            DeformResources,
            [
                new PushRangeDesc
                {
                    Stages = ShaderStageFlags.Compute,
                    Offset = 0,
                    SizeInBytes = checked((uint)Marshal.SizeOf<ClusterDeformUniforms>()),
                }
            ]);
        return bin;
    }

    private void AddCompute(
        ref MaterialBin[] states,
        PassShader variant,
        MaterialState state,
        Handle<Material> material,
        int bin,
        string name,
        IReadOnlyCollection<string> resources,
        IReadOnlyList<PushRangeDesc>? pushConstants = null)
    {
        if (variant.IsEmpty)
            return;

        var device = _context.GraphicsDevice;
        if (device == null)
            return;

        string backend = PipelineCache.BackendName(device.AdapterInfo.Backend);
        CheckState(state, name, graphics: false);
        var created = MakeCompute(variant, state, device, backend, name, resources, pushConstants);
        MaterialBindings bindings;
        Material? materialValue = Resolve(material);
        try
        {
            bindings = MakeBindings(created.Bindings, materialValue);
        }
        catch
        {
            ShaderBindings.Destroy(device, created.Bindings);
            throw;
        }

        PipelineRequest request = PipelineRequest.ForCompute(
            created.State,
            Owner(name, material, bin),
            PipelineNeed.Optional,
            Source(name, material, materialValue));
        var binState = new MaterialBin
        {
            Device = device,
            Bindings = created.Bindings,
            MaterialBindings = bindings,
            Compute = variant,
            MaterialHandle = material,
            Material = materialValue,
            ScalarLayout = materialValue?.ScalarRegionLayout ?? ScalarLayout.Empty,
            State = state,
            PipelineRequest = request,
            BinIndex = bin,
            ArgsIndex = bin,
        };
        Add(ref states, binState);
        _pipelineCollector.Add(request);
        _pipelineBins.Add(binState);
        TouchPipelines();
    }

    private void AddGraphics(
        ref MaterialBin[] states,
        PassShader vertex,
        PassShader pixel,
        MaterialState state,
        Handle<Material> material,
        int bin,
        string name,
        IReadOnlyCollection<string> resources)
    {
        var device = _context.GraphicsDevice;
        if (device == null)
            return;

        string backend = PipelineCache.BackendName(device.AdapterInfo.Backend);
        CheckState(state, name, graphics: true);
        var created = MakeGraphics(vertex, pixel, state, device, backend, name, resources);
        MaterialBindings bindings;
        Material? materialValue = Resolve(material);
        try
        {
            bindings = MakeBindings(created.Bindings, materialValue);
        }
        catch
        {
            ShaderBindings.Destroy(device, created.Bindings);
            throw;
        }

        PipelineRequest request = PipelineRequest.ForGraphics(
            created.State,
            Owner(name, material, bin),
            PipelineNeed.Optional,
            Source(name, material, materialValue));
        var binState = new MaterialBin
        {
            Device = device,
            Bindings = created.Bindings,
            MaterialBindings = bindings,
            Vertex = vertex,
            Pixel = pixel,
            MaterialHandle = material,
            Material = materialValue,
            ScalarLayout = materialValue?.ScalarRegionLayout ?? ScalarLayout.Empty,
            State = state,
            PipelineRequest = request,
            BinIndex = bin,
            ArgsIndex = bin,
        };
        Add(ref states, binState);
        _pipelineCollector.Add(request);
        _pipelineBins.Add(binState);
        TouchPipelines();
    }

    private ComputeCreate MakeCompute(
        PassShader variant,
        MaterialState state,
        IDevice device,
        string backend,
        string name,
        IReadOnlyCollection<string> resources,
        IReadOnlyList<PushRangeDesc>? pushConstants = null)
    {
        Shader shader = ResolveShader(variant);
        if (string.IsNullOrWhiteSpace(variant.EntryPoint))
            throw new InvalidOperationException("cluster material pass must reference a shader asset and entry point.");

        string entry = FindEntry(shader, variant.EntryPoint, ShaderStage.Compute, backend);
        var layout = ShaderBindings.Create(
            device,
            $"{name} ({ShaderKeys.Name(shader)})",
            resources,
            pushConstants,
            new (Shader Shader, string EntryPoint)[] { (shader, entry) });

        try
        {
            return new ComputeCreate(new ComputeState
            {
                Name = $"{name} ({ShaderKeys.Name(shader)})",
                Shader = shader,
                EntryPoint = entry,
                Layout = layout.PipelineLayout,
                Bindings = layout.Key,
                Material = state,
            }, layout);
        }
        catch
        {
            ShaderBindings.Destroy(device, layout);
            throw;
        }
    }

    private GraphicsCreate MakeGraphics(
        PassShader vertex,
        PassShader pixel,
        MaterialState state,
        IDevice device,
        string backend,
        string name,
        IReadOnlyCollection<string> resources)
    {
        Shader vertexShader = ResolveShader(vertex);
        Shader pixelShader = ResolveShader(pixel);
        string vsEntry = FindEntry(vertexShader, vertex.EntryPoint, ShaderStage.Vertex, backend);
        string psEntry = FindEntry(pixelShader, pixel.EntryPoint, ShaderStage.Pixel, backend);
        const PrimitiveTopology topology = PrimitiveTopology.TriangleList;
        const Format colorFormat = Format.R32UInt;
        const Format depthFormat = Format.D32Float;
        const uint sampleCount = 1;
        var layout = ShaderBindings.Create(
            device,
            $"{name} ({ShaderKeys.Name(vertexShader)}/{ShaderKeys.Name(pixelShader)})",
            resources,
            new (Shader Shader, string EntryPoint)[] { (vertexShader, vsEntry), (pixelShader, psEntry) });

        try
        {
            var pipeline = new GraphicsState
            {
                Name = $"{name} ({ShaderKeys.Name(vertexShader)}/{ShaderKeys.Name(pixelShader)})",
                VertexShader = vertexShader,
                VertexEntry = vsEntry,
                PixelShader = pixelShader,
                PixelEntry = psEntry,
                Layout = layout.PipelineLayout,
                Bindings = layout.Key,
                Material = state,
                Topology = topology,
                ColorFormats = [colorFormat],
                DepthStencilFormat = depthFormat,
                SampleCount = sampleCount,
                Rasterizer = new RasterizerDesc
                {
                    CullMode = state.TwoSided ? CullMode.None : CullMode.Back,
                    FrontCounterClockwise = true,
                },
                DepthStencil = new DepthStencilDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthCompare = CompareOp.LessOrEqual,
                },
            };
            return new GraphicsCreate(pipeline, layout);
        }
        catch
        {
            ShaderBindings.Destroy(device, layout);
            throw;
        }
    }

    private void CollectPipelines()
    {
        if (_pipelineCollector.Count == 0)
            return;

        using PipelineLease lease = _context.LeasePipelines(_pipelineCollector);
        if (lease.Tickets.Count != _pipelineBins.Count)
            throw new InvalidOperationException("cluster material pipeline lease returned an unexpected ticket count.");

        PipelineTicket[] tickets = lease.Detach();
        for (int i = 0; i < tickets.Length; i++)
            _pipelineBins[i].PipelineState = tickets[i];

        if (!_context.HasPipelineSource(this))
            _context.WarmupPipelines(tickets, int.MaxValue);

        ClearPending();
    }

    string IPipelineSource.Name => "Cluster Materials";

    uint IPipelineSource.Version => _pipelineVersion;

    void IPipelineSource.Collect(PipelineCollector collector)
    {
        CollectBins(collector, _uncached.Shade);
        CollectBins(collector, _uncached.Sw);
        CollectBins(collector, _uncached.Draw);
        CollectBins(collector, _uncached.Deform);
        CollectBins(collector, _cached.Shade);
        CollectBins(collector, _cached.Sw);
        CollectBins(collector, _cached.Draw);
        CollectBins(collector, _cached.Deform);
    }

    private static void CollectBins(PipelineCollector collector, MaterialBin[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            PipelineRequest request = states[i].PipelineRequest;
            if (!string.IsNullOrWhiteSpace(request.Owner))
                collector.Add(request);
        }
    }

    private void ClearPending()
    {
        if (_pipelineCollector.Count == 0 && _pipelineBins.Count == 0)
            return;

        _pipelineCollector.Clear();
        _pipelineBins.Clear();
    }

    private void TouchPipelines()
    {
        _pipelineVersion++;
        if (_pipelineVersion == 0)
            _pipelineVersion = 1;
    }

    private static string Owner(string name, Handle<Material> material, int bin)
        => $"{name} {material} Bin {bin}";

    private static string Source(string name, Handle<Material> handle, Material? material)
    {
        string? materialName = material?.Name;
        if (string.IsNullOrWhiteSpace(materialName))
            materialName = handle.ToString();
        return $"Cluster Materials/{name}/{materialName}";
    }

    private MaterialBindings MakeBindings(ShaderBindingTable layout, Handle<Material> handle)
        => MaterialBindings.Create(layout, _assets, Resolve(handle));

    private MaterialBindings MakeBindings(ShaderBindingTable layout, Material? material)
        => MaterialBindings.Create(layout, _assets, material);

    private bool UpdateBindings(MaterialVariantState state, ulong materialStoreVersion)
    {
        bool changed = false;
        bool materialStoreChanged = state.MaterialStoreVersion != materialStoreVersion;
        for (int i = 0; i < state.ItemCount; i++)
        {
            MaterialItem item = state.Items[i];
            if (!item.Handle.IsValid)
                continue;

            Material? material = item.Material;
            bool materialResolved = false;
            if (materialStoreChanged || material == null)
            {
                if (!_assets.TryGet(item.Handle, out material) || material == null)
                    continue;

                item = item with { Material = material };
                state.Items[i] = item;
                materialResolved = true;
            }

            bool bindingChanged = material.BindingVersion != item.BindingVersion;
            if (materialStoreChanged || materialResolved || bindingChanged)
            {
                RefreshBindings(state.Shade, item.Handle, material, bindingChanged);
                RefreshBindings(state.Sw, item.Handle, material, bindingChanged);
                RefreshBindings(state.Draw, item.Handle, material, bindingChanged);
                RefreshBindings(state.Deform, item.Handle, material, bindingChanged);
            }

            if (bindingChanged)
            {
                state.Items[i] = item with { Material = material, BindingVersion = material.BindingVersion };
                changed = true;
            }
        }

        return changed;
    }

    private void RefreshBindings(MaterialBin[] states, Handle<Material> handle, Material material, bool rebuildBindings)
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].MaterialHandle != handle)
                continue;

            states[i].Material = material;
            if (rebuildBindings && states[i].Bindings is ShaderBindingTable table)
                states[i].MaterialBindings = MakeBindings(table, material);
        }
    }

    private bool NeedsBuild(MaterialVariantState state, bool useCache, ulong materialStoreVersion, ulong shaderStoreVersion)
    {
        if (!state.Built)
            return true;

        bool materialStoreChanged = state.MaterialStoreVersion != materialStoreVersion;
        bool shaderStoreChanged = state.ShaderStoreVersion != shaderStoreVersion;
        for (int i = 0; i < state.ItemCount; i++)
        {
            MaterialItem item = state.Items[i];
            if (!item.Handle.IsValid)
            {
                return true;
            }

            Material? material = item.Material;
            if (materialStoreChanged || material == null)
            {
                if (!_assets.TryGet(item.Handle, out material) || material == null)
                    return true;

                item = item with { Material = material };
                state.Items[i] = item;
            }

            if (material.PassVersion != item.PassVersion
                || (shaderStoreChanged && ShaderMissing(item)))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShaderMissing(MaterialItem item)
        => ShaderMissing(item.Shade)
            || ShaderMissing(item.ShadeCache)
            || ShaderMissing(item.Sw)
            || ShaderMissing(item.SwCache)
            || ShaderMissing(item.Vs)
            || ShaderMissing(item.VsCache)
            || ShaderMissing(item.Ps)
            || ShaderMissing(item.Deform);

    private bool ShaderMissing(PassShader shader)
    {
        if (shader.IsEmpty)
            return false;

        return !_assets.TryGet(shader.Shader, out Shader? value) || value == null;
    }

    private Material? Resolve(Handle<Material> handle)
        => handle.IsValid && _assets.TryGet(handle, out Material? material) ? material : null;

    private Shader ResolveShader(PassShader shader)
    {
        if (!shader.Shader.IsValid || !_assets.TryGet(shader.Shader, out Shader? value) || value == null)
            throw new InvalidOperationException("cluster material pass must reference a runtime shader handle.");
        return value;
    }

    private static void CheckState(MaterialState state, string owner, bool graphics)
    {
        if (state.Surface != SurfaceMode.Opaque)
            throw new InvalidOperationException($"{owner} currently supports only opaque cluster material state.");
        if (state.OverlayLayer != 0)
            throw new InvalidOperationException($"{owner} currently does not support overlay material ordering.");
        if (state.StencilRef != 0
            || state.StencilCompare != CompareOp.Always
            || state.StencilPass != StencilOp.Keep)
        {
            string path = graphics ? "graphics" : "compute";
            throw new InvalidOperationException($"{owner} cannot consume stencil state in a cluster {path} pass.");
        }
    }

    private void Clear(RenderGraph graph)
    {
        Drop(graph, waitForGpu: true);
        DisposeRetired();
    }

    private void Drop(RenderGraph? graph = null, bool waitForGpu = false)
    {
        if (graph != null)
        {
            _layouts.Clear();
            AddLayouts(_uncached);
            AddLayouts(_cached);
            for (int i = 0; i < _retired.Count; i++)
                AddLayouts(_retired[i]);

            graph.ClearBindings(CollectionsMarshal.AsSpan(_layouts), waitForGpu);
        }
    }

    private void Retire(RenderGraph graph)
    {
        if (_retired.Count != 0 && graph.RetireBindingSets())
            DisposeRetired();
    }

    private void Retire(ref MaterialBin[] states)
    {
        if (states.Length != 0)
        {
            _retired.Add(states);
            TouchPipelines();
        }
        states = [];
    }

    private void DisposeRetired()
    {
        for (int i = 0; i < _retired.Count; i++)
            Dispose(_retired[i]);
        _retired.Clear();
    }

    private void Dispose(MaterialBin[] states)
    {
        PipelineCache store = _context.PipelineCache
            ?? throw new InvalidOperationException("cluster materials require a PipelineCache to release material pipelines.");
        for (int i = 0; i < states.Length; i++)
            store.Release(ref states[i].PipelineState);

        IDevice? device = _context.GraphicsDevice ?? FindDevice(states);
        if (device != null)
        {
            device.WaitIdle();
            store.Retire(ulong.MaxValue);
        }

        for (int i = 0; i < states.Length; i++)
            states[i].Dispose(store);
    }

    private void Dispose(MaterialVariantState state)
    {
        Dispose(state.Shade);
        Dispose(state.Sw);
        Dispose(state.Draw);
        Dispose(state.Deform);
        state.Dispose();
    }

    private static IDevice? FindDevice(MaterialBin[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].Device != null)
                return states[i].Device;
        }

        return null;
    }

    private void AddLayouts(MaterialBin[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            var table = states[i].Bindings;
            if (!table.HasValue)
                continue;

            var layouts = table.GetValueOrDefault().Layouts;
            for (int layoutIndex = 0; layoutIndex < layouts.Length; layoutIndex++)
            {
                if (layouts[layoutIndex].IsValid)
                    _layouts.Add(layouts[layoutIndex]);
            }
        }
    }

    private void AddLayouts(MaterialVariantState state)
    {
        AddLayouts(state.Shade);
        AddLayouts(state.Sw);
        AddLayouts(state.Draw);
        AddLayouts(state.Deform);
    }

    private static string FindEntry(
        Shader shader,
        string? entryPoint,
        ShaderStage stage,
        string backend)
    {
        if (shader == null || string.IsNullOrEmpty(entryPoint))
            throw new InvalidOperationException("cluster material pass must reference a shader asset and entry point.");

        if (shader.TryVariant(backend, entryPoint, stage, out ShaderVariant match))
            return match.EntryPoint;

        throw new InvalidOperationException(
            $"No {stage} entry point '{entryPoint}' found for backend {backend} in shader {shader.Name}");
    }

    private static int AddItem(MaterialVariantState state, MaterialItem item)
    {
        if (state.ItemCount == state.Items.Length)
            Array.Resize(ref state.Items, Math.Max(state.ItemCount + 1, state.Items.Length == 0 ? 16 : state.Items.Length * 2));
        state.Items[state.ItemCount] = item;
        return state.ItemCount++;
    }

    private static void Add(ref int[] values, int index, int value)
    {
        if (values.Length <= index)
            Array.Resize(ref values, index + 1);
        values[index] = value;
    }

    private static void Add(ref MaterialBin[] states, MaterialBin state)
    {
        int index = states.Length;
        Array.Resize(ref states, index + 1);
        states[index] = state;
    }

    private static ushort ToSlot(int value)
    {
        if (value < 0)
            return ushort.MaxValue;
        if (value > ushort.MaxValue)
            throw new InvalidOperationException("cluster material bin exceeds SlotBuffer index range.");
        return checked((ushort)value);
    }

    private readonly record struct ComputeCreate(
        ComputeState State,
        ShaderBindingTable Bindings);

    private readonly record struct GraphicsCreate(
        GraphicsState State,
        ShaderBindingTable Bindings);

    private sealed class MaterialVariantState
    {
        public MaterialVariantState(IDevice device, int fields)
        {
            Slots = new ClusterSlotBuffer(fields);
            SlotGpu = new ClusterSlotGpu(device);
        }

        public ClusterSlotGpu SlotGpu { get; }
        public ClusterSlotBuffer Slots;
        public MaterialItem[] Items = [];
        public int[] SlotOffsets = [];
        public int[] SlotCounts = [];
        public int ItemCount;
        public int[] RasterBins = [];
        public int[] ShadeBins = [];
        public int[] DeformBins = [];
        public MaterialBin[] Shade = [];
        public MaterialBin[] Sw = [];
        public MaterialBin[] Draw = [];
        public MaterialBin[] Deform = [];
        public int RasterCount;
        public int ShadeCount;
        public int DeformCount;
        public uint LastTick;
        public uint WorldMaterialVersion;
        public ulong MaterialStoreVersion;
        public ulong ShaderStoreVersion;
        public bool Built;

        public void Reset(int fields)
        {
            Slots.Dispose();
            Slots = new ClusterSlotBuffer(fields);
            Items = [];
            SlotOffsets = [];
            SlotCounts = [];
            ItemCount = 0;
            RasterBins = [];
            ShadeBins = [];
            DeformBins = [];
            RasterCount = 0;
            ShadeCount = 0;
            DeformCount = 0;
            LastTick = 0;
            WorldMaterialVersion = 0;
            MaterialStoreVersion = 0;
            ShaderStoreVersion = 0;
            Built = false;
        }

        public void Dispose()
        {
            Slots.Dispose();
            SlotGpu.Dispose();
        }
    }
}
