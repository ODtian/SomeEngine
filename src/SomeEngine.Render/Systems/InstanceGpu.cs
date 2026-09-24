using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using SomeECS.Core;
using SomeECS.Core.Components;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;

namespace SomeEngine.Render.Systems;

internal readonly record struct InstanceFrame(
    RenderGraphHandle Transform,
    RenderGraphHandle PrevTransform,
    RenderGraphHandle Header,
    RenderGraphHandle Data);

public readonly record struct InstanceUploadState(
    bool Ready,
    bool Transform,
    bool Header,
    bool Data);

internal readonly record struct InstanceScan(int Count, int Overrides, int Active);
internal readonly record struct PreparedUploadCopy(
    UploadTarget Target,
    ulong DestinationOffset,
    ulong SourceOffset,
    ulong ByteCount);

internal enum UploadTarget
{
    Transform,
    PrevTransform,
    Header,
    Data,
}

internal enum UploadSource
{
    Transform,
    PrevTransform,
    Header,
    Data,
}

internal sealed class InstanceGpu : IDisposable
{
    private readonly IDevice _device;
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _transformViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _prevTransformViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _headerViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _dataViews = new();
    private BufferHandle _transform;
    private BufferHandle _prevTransform;
    private BufferHandle _header;
    private BufferHandle _data;
    private RenderGraph? _frameGraph;
    private GpuTransform[] _transforms = [];
    private GpuTransform[] _prevTransforms = [];
    private RenderInstance[] _instances = [];
    private MaterialOverride[] _overrides = [];
    private EntityId[] _entities = [];
    private byte[] _headers = [];
    private InstanceDirtyFlags[] _dirtyFlags = [];
    private int[] _activeIndices = [];
    private bool[] _active = [];
    private bool[] _hasOverride = [];
    private readonly InstanceMeta _meta = new();
    private ResourceState _transformState = ResourceState.Common;
    private ResourceState _prevTransformState = ResourceState.Common;
    private ResourceState _headerState = ResourceState.Common;
    private ResourceState _dataState = ResourceState.Common;
    private BufferHandle _uploadSource;
    private int _uploadSourceBytes;
    private int _count;
    private int _capacity;
    private int _dataBytes;
    private int _activeCount;
    private bool _activeIndicesSorted = true;
    private bool _activeFullRange;
    private bool _activeFullRangeDataHasOverride;
    private InstanceDirtyFlags _activeFullRangeFlags;
    private RenderWorld? _activeFullRangeWorld;
    private int _lastUploadCount;
    private int _lastUploadBytes;
    private uint _patchVersion = uint.MaxValue;
    private uint _lastWorldVersion;
    private uint _lastShapeVersion;
    private uint _lastHeaderVersion;
    private PreparedUploadCopy[] _preparedUploads = [];
    private int _preparedUploadCount;
    private ulong _preparedUploadLogicalSize;
    private bool _framePrepared;
    private bool _preparedVersioned;
    private uint _preparedWorldVersion;
    private uint _preparedShapeVersion;
    private uint _preparedHeaderVersion;
    private bool _hasVersionedFrame;
    private bool _ready;
    private bool _disposed;

    public InstanceGpu(IDevice device)
        => _device = device ?? throw new ArgumentNullException(nameof(device));

    public BufferHandle Transform => _transform;
    public BufferHandle PrevTransform => _prevTransform;
    public BufferHandle Header => _header;
    public BufferHandle Data => _data;
    public int Count => _count;
    public int MetadataByteCount => _meta.ByteCount;
    public int LastUploadCount => _lastUploadCount;
    public int LastUploadBytes => _lastUploadBytes;

    public InstanceUploadState GetUploadState(
        RenderWorld renderWorld,
        InstanceHeaderData? headerData = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderWorld);

        uint headerVersion = headerData?.Version ?? 0;
        if (CanReuseVersionedFrame(renderWorld.Version, renderWorld.InstanceShapeVersion, headerVersion))
        {
            _lastUploadCount = 0;
            _lastUploadBytes = 0;
            return new InstanceUploadState(true, Transform: false, Header: false, Data: false);
        }

        if (CanPatchVersionedFrame(renderWorld.InstanceShapeVersion, headerVersion))
        {
            DirtyUploadState dirty = ScanInstanceUpdates(renderWorld);
            dirty |= ScanDirty(renderWorld.World, DirtyQuery(renderWorld.World));
            return new InstanceUploadState(
                _ready,
                dirty.Transform,
                dirty.Header,
                dirty.Data);
        }

        InstanceScan scan = Scan(renderWorld);
        return new InstanceUploadState(
            _ready,
            scan.Active > 0,
            scan.Active > 0,
            scan.Overrides > 0);
    }

    public InstanceUploadState GetUploadState(
        World world,
        InstanceHeaderData? headerData = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(world);

        QueryHandle query = Query(world);
        InstanceScan scan = Scan(world, query);
        int capacity = Math.Max(scan.Count, 1);
        int metadataBytes = Math.Max(scan.Overrides > 0 ? checked(scan.Count * InstanceMeta.SlotSize) : 0, 16);
        bool grewInstances = _capacity < capacity;
        bool grewData = _dataBytes < metadataBytes;
        bool allInstances = !_ready || grewInstances;
        bool allHeaders = !_ready || grewInstances;
        bool allData = !_ready || grewData;
        DirtyUploadState dirty = ScanDirty(world, query);

        return new InstanceUploadState(
            _ready,
            scan.Count > 0 && (allInstances || dirty.Transform),
            scan.Count > 0 && (allHeaders || dirty.Header),
            scan.Overrides > 0 && (allData || dirty.Data));
    }

    public void PrepareFrame(
        RenderWorld renderWorld,
        InstanceHeaderData? headerData = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderWorld);
        PrepareCore(
            renderWorld.World,
            headerData,
            renderWorld.Version,
            renderWorld.InstanceShapeVersion,
            versioned: true,
            renderWorld);
    }

    public void PrepareFrame(
        World world,
        InstanceHeaderData? headerData = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(world);
        PrepareCore(
            world,
            headerData,
            worldVersion: 0,
            shapeVersion: 0,
            versioned: false,
            renderWorld: null);
    }

    public InstanceFrame RecordFrame(
        RenderGraph graph,
        RenderWorld renderWorld,
        InstanceHeaderData? headerData = null,
        List<BufferCopyRequest>? copyRequests = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderWorld);
        ValidatePreparedFrame(
            versioned: true,
            renderWorld.Version,
            renderWorld.InstanceShapeVersion,
            headerData?.Version ?? 0);
        return RecordFrameCore(graph, renderWorld.World, renderWorld, copyRequests);
    }

    public InstanceFrame RecordFrame(
        RenderGraph graph,
        World world,
        InstanceHeaderData? headerData = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(world);
        ValidatePreparedFrame(
            versioned: false,
            worldVersion: 0,
            shapeVersion: 0,
            headerData?.Version ?? 0);
        return RecordFrameCore(graph, world, renderWorld: null, copyRequests: null);
    }

    public InstanceFrame Add(
        RenderGraph graph,
        RenderWorld renderWorld,
        InstanceHeaderData? headerData = null,
        List<BufferCopyRequest>? copyRequests = null)
    {
        PrepareFrame(renderWorld, headerData);
        return RecordFrame(graph, renderWorld, headerData, copyRequests);
    }

    public InstanceFrame Add(
        RenderGraph graph,
        World world,
        InstanceHeaderData? headerData = null)
    {
        PrepareFrame(world, headerData);
        return RecordFrame(graph, world, headerData);
    }

    private void PrepareCore(
        World world,
        InstanceHeaderData? headerData,
        uint worldVersion,
        uint shapeVersion,
        bool versioned,
        RenderWorld? renderWorld)
    {
        ArgumentNullException.ThrowIfNull(world);
        _framePrepared = false;
        _preparedUploadCount = 0;
        _preparedUploadLogicalSize = 0;

        uint headerVersion = headerData?.Version ?? 0;
        if (versioned && CanReuseVersionedFrame(worldVersion, shapeVersion, headerVersion))
        {
            _lastUploadCount = 0;
            _lastUploadBytes = 0;
            MarkPrepared(versioned, worldVersion, shapeVersion, headerVersion);
            return;
        }

        if (versioned && CanPatchVersionedFrame(shapeVersion, headerVersion))
        {
            using (Profiler.BeginScope("InstanceGpu.UpdateDirtyInstances"))
            {
                UpdateDirtyInstances(renderWorld, world, DirtyQuery(world), headerData);
            }

            using (Profiler.BeginScope("InstanceGpu.AddUploads"))
            {
                PrepareUploads(
                    allInstances: false,
                    allHeaders: false,
                    allData: false);
            }

            MarkPrepared(versioned, worldVersion, shapeVersion, headerVersion);
            return;
        }

        QueryHandle query = Query(world);
        InstanceScan scan;
        using (Profiler.BeginScope("InstanceGpu.Scan"))
        {
            scan = renderWorld != null ? Scan(renderWorld) : Scan(world, query);
        }

        bool patchChanged = headerData != null && _patchVersion != headerVersion;
        using (Profiler.BeginScope("InstanceGpu.WriteScratch"))
        {
            if (renderWorld != null)
                WriteScratch(renderWorld, world, scan, headerData);
            else
                WriteScratch(world, query, scan, headerData);
        }

        _patchVersion = headerVersion;
        int capacity = Math.Max(_count, 1);
        int dataBytes = Math.Max(_meta.ByteCount, 16);
        bool grewInstances;
        bool grewData;
        using (Profiler.BeginScope("InstanceGpu.EnsureCapacity"))
        {
            EnsureCapacity(capacity, dataBytes, out grewInstances, out grewData);
            EnsureValid();
        }

        using (Profiler.BeginScope("InstanceGpu.AddUploads"))
        {
            PrepareUploads(
                allInstances: !_ready || grewInstances,
                allHeaders: !_ready || grewInstances || patchChanged,
                allData: !_ready || grewData);
        }

        MarkPrepared(versioned, worldVersion, shapeVersion, headerVersion);
    }

    private InstanceFrame RecordFrameCore(
        RenderGraph graph,
        World world,
        RenderWorld? renderWorld,
        List<BufferCopyRequest>? copyRequests)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(world);

        InstanceFrame frame;
        using (Profiler.BeginScope("InstanceGpu.ImportFrame"))
        {
            frame = ImportFrame(graph);
        }

        using (Profiler.BeginScope("InstanceGpu.EmitUploads"))
        {
            EmitPreparedUploads(graph, frame, copyRequests);
        }

        using (Profiler.BeginScope("InstanceGpu.ClearDirty"))
        {
            ClearDirty(world);
        }

        renderWorld?.ClearInstanceUpdates();
        ClearPatchActive();
        _ready = true;
        if (_preparedVersioned)
        {
            _lastWorldVersion = _preparedWorldVersion;
            _lastShapeVersion = _preparedShapeVersion;
            _lastHeaderVersion = _preparedHeaderVersion;
            _hasVersionedFrame = true;
        }
        else
        {
            _hasVersionedFrame = false;
        }

        _framePrepared = false;
        return frame;
    }

    private bool CanReuseVersionedFrame(uint worldVersion, uint shapeVersion, uint headerVersion)
        => _ready
            && _hasVersionedFrame
            && _lastWorldVersion == worldVersion
            && _lastShapeVersion == shapeVersion
            && _lastHeaderVersion == headerVersion
            && _transform.IsValid
            && _prevTransform.IsValid
            && _header.IsValid
            && _data.IsValid;

    private bool CanPatchVersionedFrame(uint shapeVersion, uint headerVersion)
        => _ready
            && _hasVersionedFrame
            && _lastShapeVersion == shapeVersion
            && _lastHeaderVersion == headerVersion
            && _transform.IsValid
            && _prevTransform.IsValid
            && _header.IsValid
            && _data.IsValid;

    private void MarkPrepared(
        bool versioned,
        uint worldVersion,
        uint shapeVersion,
        uint headerVersion)
    {
        _preparedVersioned = versioned;
        _preparedWorldVersion = worldVersion;
        _preparedShapeVersion = shapeVersion;
        _preparedHeaderVersion = headerVersion;
        _framePrepared = true;
    }

    private void ValidatePreparedFrame(
        bool versioned,
        uint worldVersion,
        uint shapeVersion,
        uint headerVersion)
    {
        if (!_framePrepared)
            throw new InvalidOperationException("InstanceGpu.PrepareFrame must be called before RecordFrame.");

        if (_preparedVersioned != versioned
            || _preparedWorldVersion != worldVersion
            || _preparedShapeVersion != shapeVersion
            || _preparedHeaderVersion != headerVersion)
        {
            throw new InvalidOperationException(
                "InstanceGpu.RecordFrame must use the same world/version/header inputs that were used for PrepareFrame.");
        }
    }

    private InstanceFrame ImportFrame(RenderGraph graph)
    {
        EnsureValid();
        var transform = graph.ImportBuffer(
            "Instance Transform",
            _transform,
            TransformDesc(_capacity, "Instance Transform"),
            new ImportDesc(_transformState)
            {
                AllowWrite = true,
            },
            _transformViews);
        var prevTransform = graph.ImportBuffer(
            "Instance PrevTransform",
            _prevTransform,
            TransformDesc(_capacity, "Instance PrevTransform"),
            new ImportDesc(_prevTransformState)
            {
                AllowWrite = true,
            },
            _prevTransformViews);
        var header = graph.ImportBuffer(
            "Instance Header",
            _header,
            HeaderDesc(_capacity),
            new ImportDesc(_headerState)
            {
                AllowWrite = true,
            },
            _headerViews);
        var heap = graph.ImportBuffer(
            "Instance Data",
            _data,
            DataDesc(_dataBytes),
            new ImportDesc(_dataState)
            {
                AllowWrite = true,
            },
            _dataViews);
        graph.ExtractBuffer(
            transform,
            ResourceState.ShaderResource,
            (_, state) => _transformState = state);
        graph.ExtractBuffer(
            prevTransform,
            ResourceState.ShaderResource,
            (_, state) => _prevTransformState = state);
        graph.ExtractBuffer(
            header,
            ResourceState.ShaderResource,
            (_, state) => _headerState = state);
        graph.ExtractBuffer(
            heap,
            ResourceState.ShaderResource,
            (_, state) => _dataState = state);
        _frameGraph = graph;
        return new InstanceFrame(transform, prevTransform, header, heap);
    }

    public ReadOnlySpan<byte> GetHeader(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index), "instance index is outside the active instance range.");

        return InstanceHeaderLayout.Slice(_headers.AsSpan(), index);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _device.WaitIdle();
        ClearBindings();
        DestroyBuffer(ref _transform, _transformViews);
        DestroyBuffer(ref _prevTransform, _prevTransformViews);
        DestroyBuffer(ref _header, _headerViews);
        DestroyBuffer(ref _data, _dataViews);
        DestroyUploadBuffer(ref _uploadSource, ref _uploadSourceBytes);
        _frameGraph = null;
        _disposed = true;
    }

    private void EnsureCapacity(
        int capacity,
        int dataBytes,
        out bool grewInstances,
        out bool grewData)
    {
        grewInstances = _capacity < capacity;
        grewData = _dataBytes < dataBytes;
        if (!grewInstances && !grewData)
            return;

        if (_transform.IsValid || _prevTransform.IsValid || _header.IsValid || _data.IsValid)
        {
            _device.WaitIdle();
            ClearBindings();
        }

        if (grewInstances)
        {
            DestroyBuffer(ref _transform, _transformViews);
            DestroyBuffer(ref _prevTransform, _prevTransformViews);
            DestroyBuffer(ref _header, _headerViews);
            _capacity = capacity;
            _transformState = ResourceState.Common;
            _prevTransformState = ResourceState.Common;
            _headerState = ResourceState.Common;
        }

        if (grewData)
        {
            DestroyBuffer(ref _data, _dataViews);
            _dataBytes = dataBytes;
            _dataState = ResourceState.Common;
        }

        EnsureCreated();
    }

    private void EnsurePreparedUploadCapacity(int count)
    {
        if (_preparedUploads.Length < count)
            Array.Resize(ref _preparedUploads, count);
    }

    private void EnsureUploadSource(string name, int requiredBytes)
    {
        if (_uploadSource.IsValid && _uploadSourceBytes >= requiredBytes)
            return;

        if (_uploadSource.IsValid)
        {
            _device.WaitIdle();
            _device.Destroy(_uploadSource);
        }

        _uploadSourceBytes = Math.Max(requiredBytes, 1);
        _uploadSource = _device.CreateBuffer(
            new BufferDesc
            {
                Name = name,
                SizeInBytes = checked((ulong)_uploadSourceBytes),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
    }

    private void EnsureCreated()
    {
        if (!_transform.IsValid)
            _transform = _device.CreateBuffer(TransformDesc(_capacity, "Instance Transform"));
        if (!_prevTransform.IsValid)
            _prevTransform = _device.CreateBuffer(TransformDesc(_capacity, "Instance PrevTransform"));
        if (!_header.IsValid)
            _header = _device.CreateBuffer(HeaderDesc(_capacity));
        if (!_data.IsValid)
            _data = _device.CreateBuffer(DataDesc(_dataBytes));
    }

    private void EnsureValid()
    {
        if (!_transform.IsValid || !_prevTransform.IsValid || !_header.IsValid || !_data.IsValid)
            throw new InvalidOperationException("instance gpu resources failed to create required buffers.");
    }

    private static QueryHandle Query(World world)
        => world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>()
                .Optional<InstanceDirty>(QueryAccess.Read)
                .Optional<MaterialOverride>(QueryAccess.Read));

    private static QueryHandle DirtyQuery(World world)
        => world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>()
                .ReadWrite<InstanceDirty>()
                .Enabled<InstanceDirty>()
                .Optional<MaterialOverride>(QueryAccess.Read));

    private static InstanceScan Scan(World world, QueryHandle query)
    {
        int count = 0;
        int overrides = 0;
        int active = 0;
        foreach (QueryChunkView chunk in world.RunQuery(query).Chunks)
        {
            ReadOnlySpan<RenderInstance> instances = chunk.Read<RenderInstance>();
            bool hasOverrides = chunk.TryRead<MaterialOverride>(out _);
            for (int i = 0; i < instances.Length; i++)
            {
                int index = instances[i].InstanceIndex;
                if (index < 0)
                    throw new InvalidOperationException("render instance index must be non-negative.");

                count = Math.Max(count, index + 1);
                active++;
                if (hasOverrides)
                    overrides++;
            }
        }

        return new InstanceScan(count, overrides, active);
    }

    private static InstanceScan Scan(RenderWorld renderWorld)
    {
        int count = renderWorld.InstanceSlotCount;
        int overrides = 0;
        int active = 0;
        ReadOnlySpan<bool> activeSlots = renderWorld.InstanceSlotActive;
        ReadOnlySpan<bool> hasOverrides = renderWorld.InstanceSlotHasOverride;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            if (!activeSlots[i])
                continue;

            active++;
            if (hasOverrides[i])
                overrides++;
        }

        return new InstanceScan(count, overrides, active);
    }

    private static DirtyUploadState ScanDirty(World world, QueryHandle query)
    {
        bool transform = false;
        bool header = false;
        bool data = false;
        foreach (QueryChunkView chunk in world.RunQuery(query).Chunks)
        {
            bool hasOverride = chunk.Has<MaterialOverride>();
            foreach (int row in chunk.RowIndices)
            {
                InstanceDirtyFlags flags = chunk.Read<InstanceDirty>(row).Flags;
                transform |= (flags & InstanceDirtyFlags.Transform) != 0;
                header |= (flags & InstanceDirtyFlags.Header) != 0;
                data |= hasOverride && (flags & InstanceDirtyFlags.Data) != 0;
            }
        }

        return new DirtyUploadState(transform, header, data);
    }

    private static DirtyUploadState ScanInstanceUpdates(RenderWorld renderWorld)
    {
        InstanceDirtyFlags dirty = renderWorld.InstanceUpdateFlagsUnion;
        return new DirtyUploadState(
            (dirty & InstanceDirtyFlags.Transform) != 0,
            (dirty & InstanceDirtyFlags.Header) != 0,
            renderWorld.InstanceUpdateHasData);
    }

    private void WriteScratch(World world, QueryHandle query, InstanceScan scan, InstanceHeaderData? headerData)
    {
        ClearActive();
        _count = scan.Count;

        if (scan.Count == 0)
            return;

        EnsureScratch(scan.Count, scan.Active);
        _meta.Begin(scan.Count, scan.Overrides > 0);

        foreach (QueryChunkView chunk in world.RunQuery(query).Chunks)
        {
            ReadOnlySpan<RenderInstance> instances = chunk.Read<RenderInstance>();
            ReadOnlySpan<InstanceDirty> dirtyItems = default;
            bool hasDirty = chunk.TryRead<InstanceDirty>(out dirtyItems);
            ReadOnlySpan<MaterialOverride> overrides = default;
            bool hasOverrides = chunk.TryRead<MaterialOverride>(out overrides);
            ReadOnlySpan<EntityId> entities = chunk.Entities;
            for (int i = 0; i < instances.Length; i++)
            {
                RenderInstance instance = instances[i];
                int index = instance.InstanceIndex;
                if (_active[index])
                    throw new InvalidOperationException("render instance index must be unique.");

                InstanceDirtyFlags dirty = hasDirty ? dirtyItems[i].Flags : InstanceDirtyFlags.None;
                _dirtyFlags[index] |= dirty;

                _active[index] = true;
                _activeIndices[_activeCount++] = index;
                _entities[index] = entities[i];
                _instances[index] = instance;
                _hasOverride[index] = hasOverrides;
                if (hasOverrides)
                    _overrides[index] = overrides[i];
                _transforms[index] = instance.Transform;
                _prevTransforms[index] = instance.PrevTransform;
            }
        }

        Array.Sort(_activeIndices, 0, _activeCount);
        for (int activeIndex = 0; activeIndex < _activeCount; activeIndex++)
        {
            int index = _activeIndices[activeIndex];

            RenderInstance instance = _instances[index];
            uint dataOffset = 0;
            InstanceFlags dataFlags = instance.DataFlags;
            if (_hasOverride[index])
            {
                MaterialOverride overrideData = _overrides[index];
                dataOffset = _meta.Write(index, in overrideData);
                dataFlags |= InstanceFlags.MaterialOverride;
            }
            else
            {
                dataFlags &= ~InstanceFlags.MaterialOverride;
            }

            if (instance.DataOffset != dataOffset || instance.DataFlags != dataFlags)
            {
                instance.DataOffset = dataOffset;
                instance.DataFlags = dataFlags;
                _dirtyFlags[index] |= InstanceDirtyFlags.Header;
                if (_hasOverride[index])
                    _dirtyFlags[index] |= InstanceDirtyFlags.Data;
                InstanceMarks.Store(world, _entities[index], instance);
            }

            Span<byte> header = InstanceHeaderLayout.Slice(_headers.AsSpan(), index);
            InstanceHeaderLayout.Write(header, in instance);
            headerData?.Write(index, header);
        }
    }

    private void WriteScratch(RenderWorld renderWorld, World world, InstanceScan scan, InstanceHeaderData? headerData)
    {
        ClearActive();
        _count = scan.Count;

        if (scan.Count == 0)
            return;

        EnsureScratch(scan.Count, scan.Active);
        _meta.Begin(scan.Count, scan.Overrides > 0);

        ReadOnlySpan<EntityId> entities = renderWorld.InstanceSlotEntities;
        ReadOnlySpan<RenderInstance> instances = renderWorld.InstanceSlots;
        ReadOnlySpan<MaterialOverride> overrides = renderWorld.InstanceSlotOverrides;
        ReadOnlySpan<bool> hasOverrides = renderWorld.InstanceSlotHasOverride;
        ReadOnlySpan<bool> activeSlots = renderWorld.InstanceSlotActive;
        for (int index = 0; index < activeSlots.Length; index++)
        {
            if (!activeSlots[index])
                continue;

            RenderInstance instance = instances[index];
            if (instance.InstanceIndex != index)
                throw new InvalidOperationException("render instance mirror index must match its retained slot.");

            _dirtyFlags[index] = InstanceDirtyFlags.All;
            _active[index] = true;
            _activeIndices[_activeCount++] = index;
            _entities[index] = entities[index];
            _instances[index] = instance;
            _hasOverride[index] = hasOverrides[index];
            if (hasOverrides[index])
                _overrides[index] = overrides[index];
            _transforms[index] = instance.Transform;
            _prevTransforms[index] = instance.PrevTransform;
        }

        for (int activeIndex = 0; activeIndex < _activeCount; activeIndex++)
        {
            int index = _activeIndices[activeIndex];
            RenderInstance instance = _instances[index];
            uint dataOffset = 0;
            InstanceFlags dataFlags = instance.DataFlags;
            if (_hasOverride[index])
            {
                MaterialOverride overrideData = _overrides[index];
                dataOffset = _meta.Write(index, in overrideData);
                dataFlags |= InstanceFlags.MaterialOverride;
            }
            else
            {
                dataFlags &= ~InstanceFlags.MaterialOverride;
            }

            if (instance.DataOffset != dataOffset || instance.DataFlags != dataFlags)
            {
                instance.DataOffset = dataOffset;
                instance.DataFlags = dataFlags;
                _instances[index] = instance;
                InstanceMarks.Store(world, _entities[index], instance);
                renderWorld.StoreInstance(
                    _entities[index],
                    in instance,
                    _hasOverride[index],
                    in _overrides[index]);
            }

            Span<byte> header = InstanceHeaderLayout.Slice(_headers.AsSpan(), index);
            InstanceHeaderLayout.Write(header, in instance);
            headerData?.Write(index, header);
        }
    }

    private void UpdateDirtyInstances(RenderWorld? renderWorld, World world, QueryHandle query, InstanceHeaderData? headerData)
    {
        ClearPatchActive();
        if (_count == 0)
            return;

        EnsureDirtyInstanceScratch(_count);
        if (renderWorld != null)
        {
            bool renderWorldUniform = renderWorld.InstanceUpdateUniform;
            ReadOnlySpan<EntityId> updateEntities = renderWorldUniform
                ? default
                : renderWorld.InstanceUpdateEntities;
            ReadOnlySpan<int> updateIndices = renderWorldUniform
                ? default
                : renderWorld.InstanceUpdateIndices;
            ReadOnlySpan<InstanceDirtyFlags> updateFlags = renderWorldUniform
                ? default
                : renderWorld.InstanceUpdateFlags;
            ReadOnlySpan<EntityId> slotEntities = renderWorld.InstanceSlotEntities;
            ReadOnlySpan<RenderInstance> slotInstances = renderWorld.InstanceSlots;
            ReadOnlySpan<MaterialOverride> slotOverrides = renderWorld.InstanceSlotOverrides;
            ReadOnlySpan<bool> slotHasOverrides = renderWorld.InstanceSlotHasOverride;
            ReadOnlySpan<bool> slotActive = renderWorld.InstanceSlotActive;
            InstanceDirtyFlags updateUnion = renderWorld.InstanceUpdateFlagsUnion;
            bool uniformFullRange = renderWorldUniform
                && renderWorld.InstanceUpdatesCoverAllSlots
                && (updateUnion & InstanceDirtyFlags.Header) == 0
                && ((updateUnion & InstanceDirtyFlags.Data) == 0 || renderWorld.InstanceUpdatesAllDataHaveOverride)
                && slotInstances.Length >= _count
                && slotEntities.Length >= _count
                && slotHasOverrides.Length >= _count
                && slotActive.Length >= _count;
            bool canUseFullRange = uniformFullRange
                || (renderWorld.InstanceUpdatesCoverAllSlots
                    && (updateUnion & InstanceDirtyFlags.Header) == 0
                    && ((updateUnion & InstanceDirtyFlags.Data) == 0 || renderWorld.InstanceUpdatesAllDataHaveOverride)
                    && updateFlags.Length >= _count
                    && updateIndices.Length >= _count
                    && updateEntities.Length >= _count
                    && slotInstances.Length >= _count
                    && slotEntities.Length >= _count
                    && slotHasOverrides.Length >= _count
                    && slotActive.Length >= _count);
            if (!canUseFullRange)
            {
                canUseFullRange = updateFlags.Length == _count
                && _count > 0
                && updateIndices.Length >= _count
                && updateEntities.Length >= _count
                && slotInstances.Length >= _count
                && slotEntities.Length >= _count
                && slotHasOverrides.Length >= _count
                && slotActive.Length >= _count;
                if (canUseFullRange)
                {
                    for (int index = 0; index < _count; index++)
                    {
                        InstanceDirtyFlags dirty = updateFlags[index];
                        RenderInstance instance = slotInstances[index];
                        bool hasOverride = slotHasOverrides[index];
                        if (updateIndices[index] != index
                            || dirty == InstanceDirtyFlags.None
                            || (dirty & InstanceDirtyFlags.Header) != 0
                            || !slotActive[index]
                            || slotEntities[index] != updateEntities[index]
                            || instance.InstanceIndex != index)
                        {
                            canUseFullRange = false;
                            break;
                        }

                        if ((dirty & InstanceDirtyFlags.Data) != 0)
                        {
                            InstanceFlags dataFlags = hasOverride
                                ? instance.DataFlags | InstanceFlags.MaterialOverride
                                : instance.DataFlags & ~InstanceFlags.MaterialOverride;
                            uint dataOffset = hasOverride ? _meta.Offset(index) : 0;
                            if (instance.DataOffset != dataOffset || instance.DataFlags != dataFlags)
                            {
                                canUseFullRange = false;
                                break;
                            }
                        }
                    }
                }
            }

            if (canUseFullRange)
            {
                if (uniformFullRange)
                {
                    _activeFullRange = true;
                    _activeFullRangeFlags = updateUnion;
                    _activeFullRangeDataHasOverride = renderWorld.InstanceUpdatesAllDataHaveOverride;
                    _activeFullRangeWorld = renderWorld;
                    _activeCount = 0;
                    _activeIndicesSorted = true;
                }
                else
                {
                    _activeCount = _count;
                    _activeIndicesSorted = true;
                    for (int index = 0; index < _count; index++)
                    {
                        InstanceDirtyFlags dirty = updateFlags[index];
                        EntityId entity = updateEntities[index];
                        bool hasOverride = slotHasOverrides[index];
                        RenderInstance instance = slotInstances[index];
                        _activeIndices[index] = index;
                        _active[index] = true;
                        _dirtyFlags[index] = dirty;
                        _entities[index] = entity;
                        _instances[index] = instance;
                        _hasOverride[index] = hasOverride;
                        if (hasOverride)
                            _overrides[index] = slotOverrides[index];
                        if ((dirty & InstanceDirtyFlags.Transform) != 0)
                        {
                            _transforms[index] = instance.Transform;
                            _prevTransforms[index] = instance.PrevTransform;
                        }
                        if ((dirty & InstanceDirtyFlags.Data) != 0 && hasOverride)
                            _meta.Write(index, in slotOverrides[index]);
                    }
                }
            }
            else
            {
                for (int i = 0; i < updateFlags.Length; i++)
                {
                    InstanceDirtyFlags dirty = updateFlags[i];
                    if (dirty == InstanceDirtyFlags.None)
                        continue;

                    int index = updateIndices[i];
                    if ((uint)index >= (uint)_count)
                        throw new InvalidOperationException("direct render instance update index is outside the retained instance shape.");
                    if ((uint)index >= (uint)slotInstances.Length
                        || (uint)index >= (uint)slotEntities.Length
                        || (uint)index >= (uint)slotHasOverrides.Length
                        || (uint)index >= (uint)slotActive.Length
                        || !slotActive[index]
                        || slotEntities[index] != updateEntities[i])
                    {
                        throw new InvalidOperationException("direct render instance update does not match a live render world slot.");
                    }

                    RecordDirtyInstance(
                        renderWorld,
                        world,
                        updateEntities[i],
                        slotInstances[index],
                        index,
                        dirty,
                        slotHasOverrides[index],
                        slotOverrides[index],
                        headerData,
                        updateRenderWorldSlot: false);
                }
            }
        }

        foreach (QueryChunkView chunk in world.RunQuery(query).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                RenderInstance instance = chunk.Read<RenderInstance>(row);
                int index = instance.InstanceIndex;
                if ((uint)index >= (uint)_count)
                    throw new InvalidOperationException("dirty render instance index is outside the retained instance shape.");

                ref InstanceDirty dirtyComponent = ref chunk.ReadWrite<InstanceDirty>(row);
                InstanceDirtyFlags dirty = dirtyComponent.Flags;
                if (dirty == InstanceDirtyFlags.None)
                    continue;

                bool hasOverride = chunk.TryRead(row, out MaterialOverride materialOverride);
                RecordDirtyInstance(
                    renderWorld,
                    world,
                    chunk.GetEntity(row),
                    instance,
                    index,
                    dirty,
                    hasOverride,
                    materialOverride,
                    headerData,
                    updateRenderWorldSlot: true);
                dirtyComponent.Flags = InstanceDirtyFlags.None;
                chunk.SetComponentEnabled<InstanceDirty>(row, false);
            }
        }

        if (!_activeIndicesSorted)
            Array.Sort(_activeIndices, 0, _activeCount);
    }

    private void RecordDirtyInstance(
        RenderWorld? renderWorld,
        World world,
        EntityId entity,
        RenderInstance instance,
        int index,
        InstanceDirtyFlags dirty,
        bool hasOverride,
        MaterialOverride materialOverride,
        InstanceHeaderData? headerData,
        bool updateRenderWorldSlot)
    {
        if (!_active[index])
        {
            _active[index] = true;
            _dirtyFlags[index] = InstanceDirtyFlags.None;
            if (_activeCount > 0 && index < _activeIndices[_activeCount - 1])
                _activeIndicesSorted = false;
            _activeIndices[_activeCount++] = index;
        }

        _dirtyFlags[index] |= dirty;
        _entities[index] = entity;
        _instances[index] = instance;
        _hasOverride[index] = hasOverride;
        if (hasOverride)
            _overrides[index] = materialOverride;
        if (updateRenderWorldSlot)
            renderWorld?.StoreInstance(entity, in instance, hasOverride, in materialOverride);

        if ((dirty & InstanceDirtyFlags.Transform) != 0)
        {
            _transforms[index] = instance.Transform;
            _prevTransforms[index] = instance.PrevTransform;
        }

        if ((dirty & InstanceDirtyFlags.Header) != 0)
            UpdateDirtyHeaderAndData(renderWorld, world, index, instance, hasOverride, headerData);
        else if ((dirty & InstanceDirtyFlags.Data) != 0)
            UpdateDirtyData(renderWorld, world, index, instance, hasOverride, headerData);
    }

    private void UpdateDirtyData(
        RenderWorld? renderWorld,
        World world,
        int index,
        RenderInstance instance,
        bool hasOverride,
        InstanceHeaderData? headerData)
    {
        uint dataOffset = 0;
        InstanceFlags dataFlags = instance.DataFlags;
        if (hasOverride)
        {
            MaterialOverride overrideData = _overrides[index];
            dataOffset = _meta.Write(index, in overrideData);
            dataFlags |= InstanceFlags.MaterialOverride;
        }
        else
        {
            dataFlags &= ~InstanceFlags.MaterialOverride;
        }

        if (instance.DataOffset == dataOffset && instance.DataFlags == dataFlags)
            return;

        instance.DataOffset = dataOffset;
        instance.DataFlags = dataFlags;
        _dirtyFlags[index] |= InstanceDirtyFlags.Header;
        if (hasOverride)
            _dirtyFlags[index] |= InstanceDirtyFlags.Data;
        InstanceMarks.Store(world, _entities[index], instance);
        _instances[index] = instance;
        renderWorld?.StoreInstance(
            _entities[index],
            in instance,
            hasOverride,
            in _overrides[index]);

        Span<byte> header = InstanceHeaderLayout.Slice(_headers.AsSpan(), index);
        InstanceHeaderLayout.Write(header, in instance);
        headerData?.Write(index, header);
    }

    private void UpdateDirtyHeaderAndData(
        RenderWorld? renderWorld,
        World world,
        int index,
        RenderInstance instance,
        bool hasOverride,
        InstanceHeaderData? headerData)
    {
        Span<byte> header = InstanceHeaderLayout.Slice(_headers.AsSpan(), index);
        Span<byte> previousHeader = stackalloc byte[InstanceHeaderLayout.StrideBytes];
        header.CopyTo(previousHeader);

        uint dataOffset = 0;
        InstanceFlags dataFlags = instance.DataFlags;
        if (hasOverride)
        {
            MaterialOverride overrideData = _overrides[index];
            dataOffset = _meta.Write(index, in overrideData);
            dataFlags |= InstanceFlags.MaterialOverride;
        }
        else
        {
            dataFlags &= ~InstanceFlags.MaterialOverride;
        }

        if (instance.DataOffset != dataOffset || instance.DataFlags != dataFlags)
        {
            instance.DataOffset = dataOffset;
            instance.DataFlags = dataFlags;
            _dirtyFlags[index] |= InstanceDirtyFlags.Header;
            if (hasOverride)
                _dirtyFlags[index] |= InstanceDirtyFlags.Data;
            InstanceMarks.Store(world, _entities[index], instance);
            _instances[index] = instance;
            renderWorld?.StoreInstance(
                _entities[index],
                in instance,
                hasOverride,
                in _overrides[index]);
        }

        InstanceHeaderLayout.Write(header, in instance);
        headerData?.Write(index, header);
        if (header.SequenceEqual(previousHeader))
            _dirtyFlags[index] &= ~InstanceDirtyFlags.Header;
    }

    private void ClearActive()
    {
        for (int i = 0; i < _activeCount; i++)
        {
            int index = _activeIndices[i];
            if ((uint)index >= (uint)_active.Length)
                continue;

            _active[index] = false;
            _hasOverride[index] = false;
            _dirtyFlags[index] = InstanceDirtyFlags.None;
            if (checked((index + 1) * InstanceHeaderLayout.StrideBytes) <= _headers.Length)
                InstanceHeaderLayout.Clear(InstanceHeaderLayout.Slice(_headers.AsSpan(), index));
        }

        _activeCount = 0;
        _activeIndicesSorted = true;
        _activeFullRange = false;
        _activeFullRangeDataHasOverride = false;
        _activeFullRangeFlags = InstanceDirtyFlags.None;
        _activeFullRangeWorld = null;
    }

    private void EnsureScratch(int count, int active)
    {
        if (_transforms.Length < count)
            Array.Resize(ref _transforms, count);
        if (_prevTransforms.Length < count)
            Array.Resize(ref _prevTransforms, count);
        if (_instances.Length < count)
            Array.Resize(ref _instances, count);
        if (_entities.Length < count)
            Array.Resize(ref _entities, count);
        if (_active.Length < count)
            Array.Resize(ref _active, count);
        if (_hasOverride.Length < count)
            Array.Resize(ref _hasOverride, count);
        if (_dirtyFlags.Length < count)
            Array.Resize(ref _dirtyFlags, count);
        if (_activeIndices.Length < active)
            Array.Resize(ref _activeIndices, active);

        int headerBytes = checked(count * InstanceHeaderLayout.StrideBytes);
        if (_headers.Length < headerBytes)
            Array.Resize(ref _headers, headerBytes);

        if (_overrides.Length < count)
            Array.Resize(ref _overrides, count);
    }

    private void EnsureDirtyInstanceScratch(int count)
    {
        if (_activeIndices.Length < count)
            Array.Resize(ref _activeIndices, count);
        if (_dirtyFlags.Length < count)
            Array.Resize(ref _dirtyFlags, count);
        if (_active.Length < count)
            Array.Resize(ref _active, count);
        if (_entities.Length < count)
            Array.Resize(ref _entities, count);
        if (_instances.Length < count)
            Array.Resize(ref _instances, count);
        if (_hasOverride.Length < count)
            Array.Resize(ref _hasOverride, count);
        if (_overrides.Length < count)
            Array.Resize(ref _overrides, count);
    }

    private void ClearPatchActive()
    {
        for (int i = 0; i < _activeCount; i++)
        {
            int index = _activeIndices[i];
            if ((uint)index >= (uint)_active.Length)
                continue;

            _active[index] = false;
            _hasOverride[index] = false;
            _dirtyFlags[index] = InstanceDirtyFlags.None;
        }

        _activeCount = 0;
        _activeIndicesSorted = true;
        _activeFullRange = false;
        _activeFullRangeDataHasOverride = false;
        _activeFullRangeFlags = InstanceDirtyFlags.None;
        _activeFullRangeWorld = null;
    }

    private void ClearDirty(World world)
    {
        foreach (QueryChunkView chunk in world.RunQuery(DirtyQuery(world)).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                ref InstanceDirty dirty = ref chunk.ReadWrite<InstanceDirty>(row);
                dirty.Flags = InstanceDirtyFlags.None;
                chunk.SetComponentEnabled<InstanceDirty>(row, false);
            }
        }
    }

    private void PrepareUploads(
        bool allInstances,
        bool allHeaders,
        bool allData)
    {
        _lastUploadCount = 0;
        _lastUploadBytes = 0;
        _preparedUploadCount = 0;
        _preparedUploadLogicalSize = 0;

        bool fullTransformRange = false;
        bool fullHeaderRange = false;
        bool fullDataRange = false;
        bool hasTransformUpload = false;
        bool hasHeaderUpload = false;
        bool hasDataUpload = false;
        if (_activeFullRange && _count > 0)
        {
            fullTransformRange = (_activeFullRangeFlags & InstanceDirtyFlags.Transform) != 0;
            fullHeaderRange = (_activeFullRangeFlags & InstanceDirtyFlags.Header) != 0;
            fullDataRange = (_activeFullRangeFlags & InstanceDirtyFlags.Data) != 0
                && _activeFullRangeDataHasOverride
                && _meta.ByteCount > 0;
            hasTransformUpload = fullTransformRange;
            hasHeaderUpload = fullHeaderRange;
            hasDataUpload = fullDataRange;
            for (int activeIndex = 0; activeIndex < _activeCount; activeIndex++)
            {
                int index = _activeIndices[activeIndex];
                InstanceDirtyFlags dirty = _dirtyFlags[index];
                hasTransformUpload |= (dirty & InstanceDirtyFlags.Transform) != 0;
                hasHeaderUpload |= (dirty & InstanceDirtyFlags.Header) != 0;
                hasDataUpload |= (dirty & InstanceDirtyFlags.Data) != 0 && _hasOverride[index];
            }
        }
        else if (_count > 0
            && _activeCount == _count
            && _activeIndicesSorted
            && _activeIndices[0] == 0
            && _activeIndices[_activeCount - 1] == _count - 1)
        {
            fullTransformRange = true;
            fullHeaderRange = true;
            fullDataRange = _meta.ByteCount > 0;
            for (int activeIndex = 0; activeIndex < _activeCount; activeIndex++)
            {
                int index = _activeIndices[activeIndex];
                InstanceDirtyFlags dirty = _dirtyFlags[index];
                hasTransformUpload |= (dirty & InstanceDirtyFlags.Transform) != 0;
                hasHeaderUpload |= (dirty & InstanceDirtyFlags.Header) != 0;
                hasDataUpload |= (dirty & InstanceDirtyFlags.Data) != 0 && _hasOverride[index];
                fullTransformRange &= (dirty & InstanceDirtyFlags.Transform) != 0;
                fullHeaderRange &= (dirty & InstanceDirtyFlags.Header) != 0;
                fullDataRange &= (dirty & InstanceDirtyFlags.Data) != 0 && _hasOverride[index];
            }
        }
        else
        {
            for (int activeIndex = 0; activeIndex < _activeCount; activeIndex++)
            {
                int index = _activeIndices[activeIndex];
                InstanceDirtyFlags dirty = _dirtyFlags[index];
                hasTransformUpload |= (dirty & InstanceDirtyFlags.Transform) != 0;
                hasHeaderUpload |= (dirty & InstanceDirtyFlags.Header) != 0;
                hasDataUpload |= (dirty & InstanceDirtyFlags.Data) != 0 && _hasOverride[index];
            }
        }

        bool useFullTransformUpload = _count > 0 && (allInstances || fullTransformRange);
        bool useFullHeaderUpload = _count > 0 && (allHeaders || fullHeaderRange);
        bool useFullDataUpload = (allData || fullDataRange) && _meta.ByteCount > 0;
        var segments = new List<(UploadTarget Target, UploadSource Source, int StartByte, int ByteCount, ulong DestinationOffset)>();

        if (_count > 0)
        {
            using (Profiler.BeginScope("InstanceGpu.AddUploads.Transforms"))
            {
                if (useFullTransformUpload)
                {
                    int transformBytes = checked(_count * GpuTransform.SizeInBytes);
                    segments.Add((UploadTarget.Transform, UploadSource.Transform, 0, transformBytes, 0));
                    segments.Add((UploadTarget.PrevTransform, UploadSource.PrevTransform, 0, transformBytes, 0));
                }
                else if (hasTransformUpload)
                {
                    AddRangeSegments(
                        segments,
                        UploadTarget.Transform,
                        UploadSource.Transform,
                        GpuTransform.SizeInBytes,
                        InstanceDirtyFlags.Transform);
                    AddRangeSegments(
                        segments,
                        UploadTarget.PrevTransform,
                        UploadSource.PrevTransform,
                        GpuTransform.SizeInBytes,
                        InstanceDirtyFlags.Transform);
                }
            }

            using (Profiler.BeginScope("InstanceGpu.AddUploads.Headers"))
            {
                if (useFullHeaderUpload)
                {
                    segments.Add((UploadTarget.Header, UploadSource.Header, 0, checked(_count * InstanceHeaderLayout.StrideBytes), 0));
                }
                else if (hasHeaderUpload)
                {
                    AddRangeSegments(
                        segments,
                        UploadTarget.Header,
                        UploadSource.Header,
                        InstanceHeaderLayout.StrideBytes,
                        InstanceDirtyFlags.Header);
                }
            }
        }

        using (Profiler.BeginScope("InstanceGpu.AddUploads.Data"))
        {
            if (useFullDataUpload)
            {
                if (_meta.ByteCount > 0)
                    segments.Add((UploadTarget.Data, UploadSource.Data, 0, _meta.ByteCount, 0));
            }
            else if (hasDataUpload)
            {
                AddMetaSegments(segments);
            }
        }

        if (segments.Count == 0)
            return;

        int totalBytes = 0;
        ulong logicalSize = 0;
        for (int index = 0; index < segments.Count; index++)
        {
            totalBytes = checked(totalBytes + segments[index].ByteCount);
            logicalSize = Math.Max(logicalSize, LogicalSizeFor(segments[index].Target));
        }

        EnsureUploadSource("Instance Uploads Source", totalBytes);
        using (Profiler.BeginScope("InstanceGpu.AddUploads.PackedCopySource"))
        {
            Memory<byte> mapped = _device.MapBuffer(_uploadSource, MapMode.Write, 0, totalBytes);
            try
            {
                ReadOnlySpan<byte> transforms = MemoryMarshal.AsBytes<GpuTransform>(_transforms.AsSpan(0, _count));
                ReadOnlySpan<byte> prevTransforms = MemoryMarshal.AsBytes<GpuTransform>(_prevTransforms.AsSpan(0, _count));
                ReadOnlySpan<byte> headers = _headers.AsSpan(0, checked(_count * InstanceHeaderLayout.StrideBytes));
                ReadOnlySpan<byte> data = _meta.Data;

                EnsurePreparedUploadCapacity(segments.Count);
                int sourceOffset = 0;
                for (int index = 0; index < segments.Count; index++)
                {
                    var segment = segments[index];
                    ReadOnlySpan<byte> source = segment.Source switch
                    {
                        UploadSource.Transform => transforms.Slice(segment.StartByte, segment.ByteCount),
                        UploadSource.PrevTransform => prevTransforms.Slice(segment.StartByte, segment.ByteCount),
                        UploadSource.Header => headers.Slice(segment.StartByte, segment.ByteCount),
                        UploadSource.Data => data.Slice(segment.StartByte, segment.ByteCount),
                        _ => throw new InvalidOperationException("Unknown instance upload source."),
                    };
                    source.CopyTo(mapped.Span.Slice(sourceOffset, segment.ByteCount));
                    _preparedUploads[index] = new PreparedUploadCopy(
                        segment.Target,
                        segment.DestinationOffset,
                        checked((ulong)sourceOffset),
                        checked((ulong)segment.ByteCount));
                    sourceOffset = checked(sourceOffset + segment.ByteCount);
                    _lastUploadCount++;
                    _lastUploadBytes = checked(_lastUploadBytes + segment.ByteCount);
                }

                _preparedUploadCount = segments.Count;
                _preparedUploadLogicalSize = Math.Max(logicalSize, checked((ulong)totalBytes));
            }
            finally
            {
                _device.UnmapBuffer(_uploadSource);
            }
        }
    }

    private void EmitPreparedUploads(
        RenderGraph graph,
        InstanceFrame frame,
        List<BufferCopyRequest>? copyRequests)
    {
        if (_preparedUploadCount == 0)
            return;

        RenderGraphHandle source = graph.ImportBuffer(
            "Instance Uploads Source",
            _uploadSource,
            new BufferDesc
            {
                Name = "Instance Uploads Source",
                SizeInBytes = _preparedUploadLogicalSize,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            new ImportDesc(ResourceState.CopySource));
        var requests = new BufferCopyRequest[_preparedUploadCount];
        for (int index = 0; index < _preparedUploadCount; index++)
        {
            PreparedUploadCopy upload = _preparedUploads[index];
            requests[index] = new BufferCopyRequest(
                source,
                TargetHandle(frame, upload.Target),
                upload.SourceOffset,
                upload.DestinationOffset,
                upload.ByteCount);
        }

        if (copyRequests == null)
            BufferCopyPasses.AddCopyBatch(graph, "Instance Uploads", requests);
        else
            copyRequests.AddRange(requests);
    }

    private void AddRangeSegments(
        List<(UploadTarget Target, UploadSource Source, int StartByte, int ByteCount, ulong DestinationOffset)> segments,
        UploadTarget target,
        UploadSource source,
        int stride,
        InstanceDirtyFlags flag)
    {
        int cursor = 0;
        while (cursor < _activeCount)
        {
            int index = _activeIndices[cursor];
            if ((_dirtyFlags[index] & flag) == 0)
            {
                cursor++;
                continue;
            }

            int start = index;
            int end = index;
            cursor++;
            while (cursor < _activeCount)
            {
                int next = _activeIndices[cursor];
                if (next != end + 1 || (_dirtyFlags[next] & flag) == 0)
                    break;

                end = next;
                cursor++;
            }

            int byteOffset = checked(start * stride);
            int rangeCount = end - start + 1;
            int byteCount = checked(rangeCount * stride);
            segments.Add((target, source, byteOffset, byteCount, checked((ulong)byteOffset)));
        }
    }

    private void AddMetaSegments(
        List<(UploadTarget Target, UploadSource Source, int StartByte, int ByteCount, ulong DestinationOffset)> segments)
    {
        if (_meta.Data.IsEmpty)
            return;

        int stride = InstanceMeta.SlotSize;
        int cursor = 0;
        while (cursor < _activeCount)
        {
            int index = _activeIndices[cursor];
            if ((_dirtyFlags[index] & InstanceDirtyFlags.Data) == 0 || !_hasOverride[index])
            {
                cursor++;
                continue;
            }

            int start = index;
            int end = index;
            cursor++;
            while (cursor < _activeCount)
            {
                int next = _activeIndices[cursor];
                if (next != end + 1
                    || (_dirtyFlags[next] & InstanceDirtyFlags.Data) == 0
                    || !_hasOverride[next])
                {
                    break;
                }

                end = next;
                cursor++;
            }

            int byteOffset = checked(start * stride);
            int rangeCount = end - start + 1;
            int byteCount = checked(rangeCount * stride);
            segments.Add((UploadTarget.Data, UploadSource.Data, byteOffset, byteCount, checked((ulong)byteOffset)));
        }
    }

    private ulong LogicalSizeFor(UploadTarget target)
        => target switch
        {
            UploadTarget.Transform or UploadTarget.PrevTransform => TransformDesc(_capacity, "Instance Uploads Source").SizeInBytes,
            UploadTarget.Header => HeaderDesc(_capacity).SizeInBytes,
            UploadTarget.Data => DataDesc(_dataBytes).SizeInBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

    private static RenderGraphHandle TargetHandle(InstanceFrame frame, UploadTarget target)
        => target switch
        {
            UploadTarget.Transform => frame.Transform,
            UploadTarget.PrevTransform => frame.PrevTransform,
            UploadTarget.Header => frame.Header,
            UploadTarget.Data => frame.Data,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

    private static BufferDesc TransformDesc(int capacity, string name)
        => new()
        {
            Name = name,
            SizeInBytes = checked((ulong)capacity * GpuTransform.SizeInBytes),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = GpuTransform.SizeInBytes,
        };

    private static BufferDesc HeaderDesc(int capacity)
        => new()
        {
            Name = "Instance Header",
            SizeInBytes = checked((ulong)capacity * InstanceHeaderLayout.StrideBytes),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            Raw = true,
        };

    private static BufferDesc DataDesc(int dataBytes)
        => new()
        {
            Name = "Instance Data",
            SizeInBytes = checked((ulong)dataBytes),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            Raw = true,
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InstanceGpu));
    }

    private void ClearBindings()
    {
        if (_transformViews.Count == 0
            && _prevTransformViews.Count == 0
            && _headerViews.Count == 0
            && _dataViews.Count == 0)
        {
            return;
        }

        RenderGraph? graph = _frameGraph;
        if (graph is { IsDisposed: false })
            graph.ClearBindSets(waitForGpu: true);
    }

    private void DestroyBuffer(
        ref BufferHandle buffer,
        FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        DestroyViews(views);

        if (buffer.IsValid)
        {
            _device.Destroy(buffer);
            buffer = default;
        }
    }

    private void DestroyUploadBuffer(
        ref BufferHandle buffer,
        ref int capacityBytes)
    {
        if (buffer.IsValid)
        {
            _device.Destroy(buffer);
            buffer = default;
        }

        capacityBytes = 0;
    }

    private void DestroyViews(FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
            _device.Destroy(pair.Value);
        views.Clear();
    }

    private readonly record struct DirtyUploadState(
        bool Transform,
        bool Header,
        bool Data)
    {
        public static DirtyUploadState operator |(DirtyUploadState left, DirtyUploadState right)
            => new(
                left.Transform || right.Transform,
                left.Header || right.Header,
                left.Data || right.Data);
    }
}
