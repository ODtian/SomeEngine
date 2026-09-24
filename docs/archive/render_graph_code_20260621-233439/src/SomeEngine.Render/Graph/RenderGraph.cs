using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using RhiComputePass = SomeEngine.Rhi.IComputePass;

namespace SomeEngine.Render.Graph;

public readonly record struct GraphQueues
{
    public IDevice Device { get; }
    public IQueue Graphics { get; }
    public IQueue? Compute { get; }
    public IQueue? Copy { get; }

    public GraphQueues(IDevice device, IQueue graphics, IQueue? compute, IQueue? copy)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Graphics = RequireGraphics(graphics);
        Compute = compute;
        Copy = copy;
    }

    public GraphQueues(IDevice device, IQueue graphics)
        : this(
            device,
            graphics,
            GetOptionalQueue(device, QueueType.Compute),
            GetOptionalQueue(device, QueueType.Copy))
    {
    }

    internal GraphQueues(IQueue graphics, IQueue? compute = null, IQueue? copy = null)
    {
        Graphics = RequireGraphics(graphics);
        Device = graphics.Device;
        Compute = compute;
        Copy = copy;
    }

    private static IQueue RequireGraphics(IQueue graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return graphics;
    }

    private static IQueue? GetOptionalQueue(IDevice device, QueueType type)
    {
        ArgumentNullException.ThrowIfNull(device);
        return type switch
        {
            QueueType.Compute => device.Features.ComputeQueue ? device.GetQueue(type) : null,
            QueueType.Copy => device.Features.CopyQueue ? device.GetQueue(type) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }

    public bool HasCompute => Compute != null;
    public bool HasCopy => Copy != null;

    public IQueue Get(QueueType type)
        => type switch
        {
            QueueType.Graphics => Graphics,
            QueueType.Compute when Compute != null => Compute,
            QueueType.Copy when Copy != null => Copy,
            _ => throw new InvalidOperationException($"RenderGraph queue set does not contain a {type} queue."),
        };

    public void Require()
    {
        ArgumentNullException.ThrowIfNull(Graphics);
        RequireQueueOwner(Graphics, Device, "graphics");
        if (Compute != null)
            RequireQueueOwner(Compute, Device, "compute");
        if (Copy != null)
            RequireQueueOwner(Copy, Device, "copy");
        if (Graphics.Type != QueueType.Graphics)
            throw new InvalidOperationException($"RenderGraph graphics queue slot received {Graphics.Type}.");
        if (Compute != null && Compute.Type != QueueType.Compute)
            throw new InvalidOperationException($"RenderGraph compute queue slot received {Compute.Type}.");
        if (Copy != null && Copy.Type != QueueType.Copy)
            throw new InvalidOperationException($"RenderGraph copy queue slot received {Copy.Type}.");
    }

    private static void RequireQueueOwner(IQueue queue, IDevice device, string slot)
    {
        if (!ReferenceEquals(queue.Device, device))
        {
            throw new InvalidOperationException(
                $"RenderGraph {slot} queue does not belong to the supplied device.");
        }
    }
}

public enum ResourceLifetime
{
    Pooled,
    Transient,
}

public sealed partial class RenderGraph : IDisposable
{
    private const int QueueCount = 3;
    private const int MaxPendingSubmittedFrames = 3;
    private const int StackBatchScratchLimit = 256;

    internal sealed class Resource(string name, ResourceKind kind)
    {
        public string Name = name;
        public ResourceKind Kind = kind;
        public TextureDesc? TextureDesc;
        public BufferDesc? BufferDesc;
        public byte[]? BufferInitialData;
        public bool BufferHasInitialData;
        public TextureHandle Texture;
        public BufferHandle Buffer;
        public ResourceState CurrentState;
        public ResourceState[]? TextureStates;
        public RenderGraphAccess[]? TextureUavAccesses;
        public RenderGraphAccess BufferUavAccess;
        public bool StateTrusted;
        public bool Imported;
        public bool AllowWrite;
        public bool AllowDuplicateImportHandle;
        public bool HasCallerOwnedImportedViews;
        public bool OwnsImportedViews = true;
        public ResourceLifetime Lifetime = ResourceLifetime.Pooled;
        public FlatDictionary<TextureViewKey, TextureViewHandle>? TextureViews;
        public FlatDictionary<BufferViewKey, BufferViewHandle>? BufferViews;
        public TextureViewHandle[]? PendingImportedTextureViews;
        public BufferViewHandle[]? PendingImportedBufferViews;

        public void Reset(string resourceName, ResourceKind resourceKind)
        {
            Name = resourceName;
            Kind = resourceKind;
            TextureDesc = null;
            BufferDesc = null;
            BufferInitialData = null;
            BufferHasInitialData = false;
            Texture = default;
            Buffer = default;
            CurrentState = ResourceState.Undefined;
            TextureStates = null;
            TextureUavAccesses = null;
            BufferUavAccess = RenderGraphAccess.None;
            StateTrusted = false;
            Imported = false;
            AllowWrite = false;
            AllowDuplicateImportHandle = false;
            HasCallerOwnedImportedViews = false;
            if (OwnsImportedViews)
            {
                TextureViews?.ClearNoResize();
                BufferViews?.ClearNoResize();
            }
            else
            {
                TextureViews = null;
                BufferViews = null;
            }

            OwnsImportedViews = true;
            Lifetime = ResourceLifetime.Pooled;
            PendingImportedTextureViews = null;
            PendingImportedBufferViews = null;
        }

        public FlatDictionary<TextureViewKey, TextureViewHandle> EnsureTextureViews()
            => TextureViews ??= new FlatDictionary<TextureViewKey, TextureViewHandle>();

        public FlatDictionary<BufferViewKey, BufferViewHandle> EnsureBufferViews()
            => BufferViews ??= new FlatDictionary<BufferViewKey, BufferViewHandle>();

        public FlatDictionary<TextureViewKey, TextureViewHandle> DetachTextureViews()
        {
            var views = EnsureTextureViews();
            TextureViews = null;
            return views;
        }

        public FlatDictionary<BufferViewKey, BufferViewHandle> DetachBufferViews()
        {
            var views = EnsureBufferViews();
            BufferViews = null;
            return views;
        }
    }

    private sealed class Pass(
        string name,
        PassMode mode,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext>? commandExecute,
        Action<RenderGraphContext, IComputeCommands>? computeExecute)
    {
        public string Name { get; } = name;
        public PassMode Mode { get; } = mode;
        public Action<RenderGraphBuilder> Setup { get; } = setup;
        public Action<RenderGraphContext>? CommandExecute { get; } = commandExecute;
        public Action<RenderGraphContext, IComputeCommands>? ComputeExecute { get; } = computeExecute;
        public bool SideEffect { get; set; }
    }

    private sealed class ComputeCommands(
        RenderGraphContext context,
        GraphFrameExecution execution,
        RhiComputePass inner,
        string passName) : IComputeCommands
    {
        private bool _pipelineSet;
        private bool _active = true;
        private List<TextureBarrier>? _textureBarriers;
        private List<BufferBarrier>? _bufferBarriers;

        public void SetPipeline(PipelineHandle pipeline)
        {
            RequireActive();
            execution.TrackPipeline(pipeline);
            inner.SetPipeline(pipeline);
            _pipelineSet = true;
        }

        public void SetParameters(uint setIndex, PassParameters parameters)
        {
            RequireActive();
            RequirePipeline();
            context.ValidateParameterLifetime(parameters, nameof(SetParameters));
            context.ValidateBindingResources(parameters.ResourceSpan, parameters.ResourceAccessSpan, nameof(SetParameters));
            inner.SetBindingSet(setIndex, execution.GetBindingSet(parameters));
        }

        public void SetParameters(uint setIndex, PassBindings bindings)
        {
            RequireActive();
            RequirePipeline();
            context.ValidateParameterLifetime(bindings, nameof(SetParameters));
            context.ValidateBindingResources(bindings.ResourceSpan, bindings.ResourceAccessSpan, nameof(SetParameters));
            inner.SetBindingSet(setIndex, execution.GetBindingSet(bindings));
        }

        public void Barrier(ReadOnlySpan<UavBarrier> barriers)
        {
            RequireActive();
            if (barriers.IsEmpty)
                return;

            _textureBarriers?.Clear();
            _bufferBarriers?.Clear();
            for (int i = 0; i < barriers.Length; i++)
            {
                var barrier = barriers[i];
                if (context.GetResourceKind(barrier.Handle, nameof(Barrier)) == ResourceKind.Texture)
                {
                    TextureHandle texture = context.GetTexture(
                        barrier.Handle,
                        ResourceState.UnorderedAccess,
                        RenderGraphAccess.WriteOnly,
                        barrier.Range,
                        nameof(Barrier));
                    var textureBarriers = _textureBarriers ??= [];
                    var range = barrier.Range.IsAll
                        ? SomeEngine.Rhi.SubresourceRange.All
                        : barrier.Range.ToRange();
                    textureBarriers.Add(new TextureBarrier(
                        texture,
                        ResourceState.UnorderedAccess,
                        ResourceState.UnorderedAccess,
                        range));
                }
                else
                {
                    BufferHandle buffer = context.GetBuffer(
                        barrier.Handle,
                        ResourceState.UnorderedAccess,
                        RenderGraphAccess.WriteOnly,
                        nameof(Barrier));
                    var bufferBarriers = _bufferBarriers ??= [];
                    bufferBarriers.Add(new BufferBarrier(
                        buffer,
                        ResourceState.UnorderedAccess,
                        ResourceState.UnorderedAccess));
                }
            }

            inner.Barrier(
                _textureBarriers == null ? [] : CollectionsMarshal.AsSpan(_textureBarriers),
                _bufferBarriers == null ? [] : CollectionsMarshal.AsSpan(_bufferBarriers));
        }

        public void BufferBarrier(RenderGraphHandle handle, ResourceState before, ResourceState after)
        {
            RequireActive();
            if (context.GetResourceKind(handle, nameof(BufferBarrier)) != ResourceKind.Buffer)
                throw new InvalidOperationException($"RenderGraph {nameof(BufferBarrier)} requires a buffer handle.");

            context.ValidateBufferTransition(handle, before, after, nameof(BufferBarrier));
            BufferHandle buffer = context.GetBuffer(handle);
            _bufferBarriers ??= [];
            _bufferBarriers.Clear();
            _bufferBarriers.Add(new BufferBarrier(buffer, before, after));
            inner.Barrier(
                [],
                CollectionsMarshal.AsSpan(_bufferBarriers));
        }

        public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
        {
            RequireActive();
            RequirePipeline();
            inner.SetPushConstants(stages, offset, data);
        }

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            RequireActive();
            RequirePipeline();
            inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        public void DispatchIndirect(
            RenderGraphHandle arguments,
            ulong argumentOffset = 0,
            uint dispatchCount = 1,
            uint strideInBytes = IndirectArgumentSize.Dispatch,
            RenderGraphHandle countBuffer = default,
            ulong countBufferOffset = 0)
        {
            RequireActive();
            RequirePipeline();
            var desc = new IndirectDispatchDesc
            {
                Arguments = context.GetBuffer(
                    arguments,
                    ResourceState.IndirectArgument,
                    RenderGraphAccess.ReadOnly,
                    nameof(DispatchIndirect)),
                ArgumentOffset = argumentOffset,
                DispatchCount = dispatchCount,
                StrideInBytes = strideInBytes,
                CountBuffer = context.GetIndirectCountBufferOrDefault(countBuffer, nameof(DispatchIndirect)),
                CountBufferOffset = countBufferOffset,
            };
            inner.DispatchIndirect(desc);
        }

        internal void Invalidate()
            => _active = false;

        private void RequireActive()
        {
            if (!_active)
                throw new InvalidOperationException("IComputeCommands cannot be used after the RenderGraph compute pass callback has returned.");
        }

        private void RequirePipeline()
        {
            if (!_pipelineSet)
                throw new InvalidOperationException($"RenderGraph compute pass '{passName}' must set a pipeline before recording compute commands.");
        }
    }

    private sealed class CommandBarriers
    {
        public readonly List<TextureBarrier> Textures = [];
        public readonly List<BufferBarrier> Buffers = [];
        public readonly List<AliasingBarrier> Aliases = [];
        public int BufferTransitions;
        public int BufferUavDependencies;

        public bool IsEmpty => Textures.Count == 0 && Buffers.Count == 0 && Aliases.Count == 0;

        public void Clear()
        {
            Textures.Clear();
            Buffers.Clear();
            Aliases.Clear();
            BufferTransitions = 0;
            BufferUavDependencies = 0;
        }

        public void AddBuffer(BufferHandle handle, ResourceState before, ResourceState after)
        {
            Buffers.Add(new BufferBarrier(handle, before, after));
            BufferTransitions++;
        }

        public void AddBufferUav(BufferHandle handle)
        {
            Buffers.Add(new BufferBarrier(handle, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess));
            BufferUavDependencies++;
        }
    }

    private sealed class ResourceStateTracker
    {
        public ResourceState[] States = [];
        public ResourceState[][] TextureStates = [];
        public RenderGraphAccess[][] TextureUavAccesses = [];
        public RenderGraphAccess[] BufferUavAccesses = [];
        public bool[] Trusted = [];
        public bool[] AliasReady = [];
        public readonly FlatDictionary<AliasSlotKey, AliasingResource> ActiveAliases = new();
        private GraphSchema? _aliasSchema;

        public void Begin(IReadOnlyList<Resource> resources, CompiledGraph compile)
        {
            int count = resources.Count;
            Ensure(count);
            for (int resourceIndex = 0; resourceIndex < count; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                if (!compile.Resources[resourceIndex].Live)
                {
                    States[resourceIndex] = ResourceState.Undefined;
                    BufferUavAccesses[resourceIndex] = RenderGraphAccess.None;
                    Trusted[resourceIndex] = false;
                    AliasReady[resourceIndex] = false;
                    TextureStates[resourceIndex] = [];
                    TextureUavAccesses[resourceIndex] = [];
                    continue;
                }

                States[resourceIndex] = resource.CurrentState;
                BufferUavAccesses[resourceIndex] = resource.BufferUavAccess;
                Trusted[resourceIndex] = resource.StateTrusted;
                AliasReady[resourceIndex] = false;
                if (resource.Kind == ResourceKind.Texture)
                    CopyTextureState(resourceIndex, resource);
                else
                {
                    TextureStates[resourceIndex] = [];
                    TextureUavAccesses[resourceIndex] = [];
                }
            }
        }

        public void Refresh(int resourceIndex, Resource resource)
        {
            States[resourceIndex] = resource.CurrentState;
            BufferUavAccesses[resourceIndex] = resource.BufferUavAccess;
            Trusted[resourceIndex] = resource.StateTrusted;
            AliasReady[resourceIndex] = false;
            if (resource.Kind == ResourceKind.Texture)
                CopyTextureState(resourceIndex, resource);
            else
            {
                TextureStates[resourceIndex] = [];
                TextureUavAccesses[resourceIndex] = [];
            }
        }

        public void BeginAliases(GraphSchema schema, ReadOnlySpan<ResourceRecord> resources)
        {
            bool hasAliases = false;
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                if (resources[resourceIndex].Aliased)
                {
                    hasAliases = true;
                    break;
                }
            }

            if (!hasAliases)
            {
                ClearAliases();
                return;
            }

            if (_aliasSchema != null && _aliasSchema.Equals(schema))
                return;

            ActiveAliases.Clear();
            _aliasSchema = schema;
        }

        public void ClearAliases()
        {
            ActiveAliases.Clear();
            _aliasSchema = null;
        }

        public void Commit(IReadOnlyList<Resource> resources, CompiledGraph compile)
        {
            int count = resources.Count;
            for (int resourceIndex = 0; resourceIndex < count; resourceIndex++)
            {
                if (!compile.Resources[resourceIndex].Live)
                    continue;

                var resource = resources[resourceIndex];
                resource.CurrentState = States[resourceIndex];
                resource.BufferUavAccess = BufferUavAccesses[resourceIndex];
                resource.StateTrusted = Trusted[resourceIndex];
                if (resource.Kind != ResourceKind.Texture)
                    continue;

                var sourceStates = TextureStates[resourceIndex];
                var targetStates = EnsureTextureStates(resource);
                if (targetStates.Length != sourceStates.Length)
                    throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' state tracker shape changed during execution.");
                Array.Copy(sourceStates, targetStates, sourceStates.Length);

                var sourceAccesses = TextureUavAccesses[resourceIndex];
                if (!HasAccess(sourceAccesses))
                {
                    resource.TextureUavAccesses = null;
                    continue;
                }

                var targetAccesses = EnsureUavAccesses(resource);
                if (targetAccesses.Length != sourceAccesses.Length)
                    throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' UAV access tracker shape changed during execution.");
                Array.Copy(sourceAccesses, targetAccesses, sourceAccesses.Length);
            }
        }

        private void CopyTextureState(int resourceIndex, Resource resource)
        {
            var sourceStates = EnsureTextureStates(resource);
            var targetStates = TextureStates[resourceIndex];
            if (targetStates == null || targetStates.Length != sourceStates.Length)
            {
                targetStates = new ResourceState[sourceStates.Length];
                TextureStates[resourceIndex] = targetStates;
            }

            Array.Copy(sourceStates, targetStates, sourceStates.Length);

            var targetAccesses = TextureUavAccesses[resourceIndex];
            if (targetAccesses == null || targetAccesses.Length != sourceStates.Length)
            {
                targetAccesses = new RenderGraphAccess[sourceStates.Length];
                TextureUavAccesses[resourceIndex] = targetAccesses;
            }

            if (resource.TextureUavAccesses is { } sourceAccesses)
            {
                if (sourceAccesses.Length != targetAccesses.Length)
                    throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' UAV access tracker shape changed before execution.");
                Array.Copy(sourceAccesses, targetAccesses, sourceAccesses.Length);
            }
            else
            {
                Array.Clear(targetAccesses);
            }
        }

        private void Ensure(int count)
        {
            if (States.Length < count)
                Array.Resize(ref States, count);
            if (TextureStates.Length < count)
                Array.Resize(ref TextureStates, count);
            if (TextureUavAccesses.Length < count)
                Array.Resize(ref TextureUavAccesses, count);
            if (BufferUavAccesses.Length < count)
                Array.Resize(ref BufferUavAccesses, count);
            if (Trusted.Length < count)
                Array.Resize(ref Trusted, count);
            if (AliasReady.Length < count)
                Array.Resize(ref AliasReady, count);
        }

        private static bool HasAccess(RenderGraphAccess[] accesses)
        {
            for (int i = 0; i < accesses.Length; i++)
            {
                if (accesses[i] != RenderGraphAccess.None)
                    return true;
            }

            return false;
        }
    }

    internal sealed class BindingSetEntry(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        BindingSetHandle handle)
    {
        public BindingLayoutHandle Layout { get; } = layout;
        public BindingResourceDesc[] Resources { get; } = resources;
        public BindingSetHandle Handle { get; } = handle;
    }

    internal sealed class BindingCache
    {
        // Size is used as a bitmask slot count; keep it a power of two.
        private const int Size = 256;

        private readonly BindingSetEntry?[] _entries = new BindingSetEntry?[Size];

        public bool TryGet(
            int hash,
            BindingLayoutHandle layout,
            ReadOnlySpan<BindingResourceDesc> resources,
            out BindingSetHandle handle)
        {
            BindingSetEntry? entry = _entries[Slot(hash)];
            if (entry != null
                && entry.Layout == layout
                && BindingHash.Equal(entry.Resources, resources))
            {
                handle = entry.Handle;
                return true;
            }

            handle = default;
            return false;
        }

        public void Store(int hash, BindingSetEntry entry)
            => _entries[Slot(hash)] = entry;

        public void Remove(BindingSetHandle handle)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i]?.Handle == handle)
                    _entries[i] = null;
            }
        }

        public void Clear()
            => Array.Clear(_entries, 0, _entries.Length);

        private static int Slot(int hash)
            => hash & (Size - 1);
    }

    internal sealed class FrameUploadBuffer
    {
        private readonly List<FrameUploadBufferLease> _idleBuffers = [];
        private readonly List<FrameUploadBufferLease> _frameBuffers = [];
        private IDevice? _device;
        private BufferHandle _buffer;
        private ulong _capacity;
        private ulong _offset;
        private ulong _lastFrameCapacity;
        private BindFlags _bindFlags;
        private bool _frameUsed;

        public bool HasFrameAllocations => _frameUsed;

        public FrameUploadAllocation Allocate(
            IDevice device,
            string name,
            ReadOnlySpan<byte> data,
            ulong logicalSize,
            BindFlags bindFlags = BindFlags.CopySource,
            ResourceState initialState = ResourceState.CopySource,
            ulong allocationAlignment = 1)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (data.IsEmpty)
                throw new ArgumentException("frame upload data must not be empty.", nameof(data));

            if (_device != null && !ReferenceEquals(_device, device))
                Destroy(_device);
            _device = device;

            ulong alignedOffset = AlignUp(
                _offset,
                CombineAlignment(device.Limits.BufferCopyOffsetAlignment, allocationAlignment));
            ulong end = checked(alignedOffset + (ulong)data.Length);
            EnsureCapacity(device, name, end, bindFlags);

            var mapped = device.MapBuffer(_buffer, MapMode.Write, alignedOffset, data.Length);
            data.CopyTo(mapped.Span);
            device.UnmapBuffer(_buffer);
            _offset = end;
            _frameUsed = true;

            return new FrameUploadAllocation(
                _buffer,
                alignedOffset,
                new BufferDesc
                {
                    Name = name,
                    SizeInBytes = Math.Max(logicalSize, end),
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = bindFlags,
                    InitialState = initialState,
                });
        }

        public FrameUploadAllocation Allocate<TState>(
            IDevice device,
            string name,
            int byteCount,
            ulong logicalSize,
            TState state,
            Action<Memory<byte>, TState> write,
            BindFlags bindFlags = BindFlags.CopySource,
            ResourceState initialState = ResourceState.CopySource,
            ulong allocationAlignment = 1)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(write);
            if (byteCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(byteCount), "frame upload byte count must be greater than zero.");

            if (_device != null && !ReferenceEquals(_device, device))
                Destroy(_device);
            _device = device;

            ulong alignedOffset = AlignUp(
                _offset,
                CombineAlignment(device.Limits.BufferCopyOffsetAlignment, allocationAlignment));
            ulong end = checked(alignedOffset + (ulong)byteCount);
            EnsureCapacity(device, name, end, bindFlags);

            var mapped = device.MapBuffer(_buffer, MapMode.Write, alignedOffset, byteCount);
            try
            {
                write(mapped, state);
            }
            finally
            {
                device.UnmapBuffer(_buffer);
            }

            _offset = end;
            _frameUsed = true;

            return new FrameUploadAllocation(
                _buffer,
                alignedOffset,
                new BufferDesc
                {
                    Name = name,
                    SizeInBytes = Math.Max(logicalSize, end),
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = bindFlags,
                    InitialState = initialState,
                });
        }

        public void QueueFrame(List<FrameUploadBufferLease> frameBuffers)
        {
            ArgumentNullException.ThrowIfNull(frameBuffers);
            if (!_frameUsed)
                return;

            frameBuffers.AddRange(_frameBuffers);
            _frameBuffers.Clear();
            if (_buffer.IsValid)
            {
                _lastFrameCapacity = Math.Max(_lastFrameCapacity, _capacity);
                frameBuffers.Add(CurrentLease());
            }
            ClearCurrent();
            _frameUsed = false;
        }

        public void DiscardFrame(IDevice? device)
        {
            _frameUsed = false;
            ReturnFrameBuffers(_frameBuffers);
            _frameBuffers.Clear();
            if (_buffer.IsValid)
            {
                ReturnBuffer(CurrentLease());
                ClearCurrent();
            }
        }

        public void RetireFrame(List<FrameUploadBufferLease> frameBuffers)
        {
            ArgumentNullException.ThrowIfNull(frameBuffers);
            ReturnFrameBuffers(frameBuffers);
            frameBuffers.Clear();
        }

        public void Destroy(IDevice device)
        {
            DestroyBuffers(device, _idleBuffers);
            DestroyBuffers(device, _frameBuffers);
            _idleBuffers.Clear();
            _frameBuffers.Clear();
            if (_buffer.IsValid)
            {
                DestroyBuffer(device, CurrentLease());
                ClearCurrent();
            }

            _frameUsed = false;
            _lastFrameCapacity = 0;
            _device = null;
        }

        public bool IsCurrentBuffer(BufferHandle buffer)
            => _buffer.IsValid && _buffer == buffer;

        private void EnsureCapacity(IDevice device, string name, ulong requiredBytes, BindFlags bindFlags)
        {
            BindFlags requiredBindFlags = bindFlags | BindFlags.CopySource;
            bool hasRequiredFlags = (_bindFlags & requiredBindFlags) == requiredBindFlags;
            if (_buffer.IsValid && requiredBytes <= _capacity && hasRequiredFlags)
                return;

            ulong targetCapacity = Math.Max(requiredBytes, _lastFrameCapacity);
            if (_buffer.IsValid)
            {
                _frameBuffers.Add(CurrentLease());
                ClearCurrent();
            }

            if (TryRentBuffer(targetCapacity, requiredBindFlags, out var rented))
            {
                UseLease(rented);
                return;
            }

            _bindFlags = requiredBindFlags;
            _capacity = targetCapacity;
            _buffer = device.CreateBuffer(new BufferDesc
            {
                Name = $"{name} FrameUploadBuffer",
                SizeInBytes = _capacity,
                Memory = MemoryClass.CpuUpload,
                BindFlags = _bindFlags,
                InitialState = ResourceState.GenericRead,
            });
        }

        private bool TryRentBuffer(ulong requiredBytes, BindFlags requiredBindFlags, out FrameUploadBufferLease lease)
        {
            for (int i = 0; i < _idleBuffers.Count; i++)
            {
                var candidate = _idleBuffers[i];
                if (candidate.Capacity < requiredBytes
                    || (candidate.BindFlags & requiredBindFlags) != requiredBindFlags)
                {
                    continue;
                }

                lease = candidate;
                int last = _idleBuffers.Count - 1;
                _idleBuffers[i] = _idleBuffers[last];
                _idleBuffers.RemoveAt(last);
                return true;
            }

            lease = default;
            return false;
        }

        private void UseLease(FrameUploadBufferLease lease)
        {
            _buffer = lease.Buffer;
            _capacity = lease.Capacity;
            _bindFlags = lease.BindFlags;
            _offset = 0;
        }

        private FrameUploadBufferLease CurrentLease()
            => new(_buffer, _capacity, _bindFlags);

        private void ClearCurrent()
        {
            _buffer = default;
            _capacity = 0;
            _offset = 0;
            _bindFlags = BindFlags.None;
        }

        private void ReturnFrameBuffers(List<FrameUploadBufferLease> frameBuffers)
        {
            for (int i = 0; i < frameBuffers.Count; i++)
                ReturnBuffer(frameBuffers[i]);
        }

        private void ReturnBuffer(FrameUploadBufferLease lease)
        {
            if (lease.Buffer.IsValid)
                _idleBuffers.Add(lease);
        }

        private void DestroyBuffers(IDevice device, List<FrameUploadBufferLease> buffers)
        {
            HashSet<BufferHandle>? destroyed = null;
            for (int i = buffers.Count - 1; i >= 0; i--)
            {
                var lease = buffers[i];
                BufferHandle buffer = lease.Buffer;
                if (!buffer.IsValid)
                    continue;

                destroyed ??= [];
                if (!destroyed.Add(buffer))
                    continue;

                DestroyBuffer(device, lease);
            }
        }

        private void DestroyBuffer(IDevice device, FrameUploadBufferLease lease)
            => device.Destroy(lease.Buffer);

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0)
                return value;
            ulong remainder = value % alignment;
            return remainder == 0 ? value : checked(value + alignment - remainder);
        }

        private static ulong CombineAlignment(ulong first, ulong second)
        {
            first = Math.Max(1ul, first);
            second = Math.Max(1ul, second);
            return checked(first / GreatestCommonDivisor(first, second) * second);
        }

        private static ulong GreatestCommonDivisor(ulong left, ulong right)
        {
            while (right != 0)
            {
                ulong remainder = left % right;
                left = right;
                right = remainder;
            }

            return Math.Max(1ul, left);
        }
    }

    internal readonly record struct FrameUploadBufferLease(
        BufferHandle Buffer,
        ulong Capacity,
        BindFlags BindFlags);

    internal readonly record struct FrameUploadAllocation(
        BufferHandle Buffer,
        ulong Offset,
        BufferDesc Desc);

    internal sealed class PendingFrameResources
    {
        public FenceHandle Fence;
        public ulong FenceValue;
        public readonly List<CommandBufferHandle> CommandBuffers = [];
        public readonly List<BindingSetHandle> BindingSets = [];
        public readonly List<TextureViewHandle> TextureViews = [];
        public readonly List<BufferViewHandle> BufferViews = [];
        public readonly List<FrameUploadBufferLease> FrameUploadBuffers = [];
        public readonly List<SubmittedTexture> Textures = [];
        public readonly List<SubmittedBuffer> Buffers = [];
        public readonly List<SubmittedAliasHeap> Heaps = [];
        public readonly List<SubmittedTextureExport> TextureExports = [];
        public readonly List<SubmittedBufferExport> BufferExports = [];
        public DeviceTimestamps? Timestamps;

        public void EnsureCapacity(
            int commandBufferCount,
            int bindingSetCount,
            int textureViewCount,
            int bufferViewCount,
            int aliasHeapCount,
            int textureCount,
            int bufferCount,
            int textureExportCount,
            int bufferExportCount)
        {
            CommandBuffers.EnsureCapacity(commandBufferCount);
            BindingSets.EnsureCapacity(bindingSetCount);
            TextureViews.EnsureCapacity(textureViewCount);
            BufferViews.EnsureCapacity(bufferViewCount);
            Textures.EnsureCapacity(textureCount);
            Buffers.EnsureCapacity(bufferCount);
            Heaps.EnsureCapacity(aliasHeapCount);
            TextureExports.EnsureCapacity(textureExportCount);
            BufferExports.EnsureCapacity(bufferExportCount);
        }

        public void ClearForReuse()
        {
            Fence = default;
            FenceValue = 0;
            CommandBuffers.Clear();
            BindingSets.Clear();
            TextureViews.Clear();
            BufferViews.Clear();
            FrameUploadBuffers.Clear();
            Textures.Clear();
            Buffers.Clear();
            Heaps.Clear();
            TextureExports.Clear();
            BufferExports.Clear();
            Timestamps = null;
        }
    }

    internal readonly record struct SubmittedTexture(
        TextureHandle Handle,
        TexturePoolKey Key,
        ResourceState[] States,
        FlatDictionary<TextureViewKey, TextureViewHandle> Views,
        bool Aliased,
        bool Reusable);

    internal readonly record struct SubmittedBuffer(
        BufferHandle Handle,
        BufferPoolKey Key,
        ResourceState State,
        FlatDictionary<BufferViewKey, BufferViewHandle> Views,
        bool Aliased,
        bool Reusable);

    internal readonly record struct SubmittedAliasHeap(
        MemoryHeapHandle Handle,
        AliasHeapPoolKey Key);

    internal readonly record struct SubmittedTextureExport(
        TextureHandle Handle,
        ResourceState State,
        Action<TextureHandle, ResourceState> Sink);

    internal readonly record struct SubmittedBufferExport(
        BufferHandle Handle,
        ResourceState State,
        Action<BufferHandle, ResourceState> Sink);

    internal sealed class AliasHeap(MemoryClass memory, MemoryHeapKind kind)
    {
        public MemoryClass Memory { get; } = memory;
        public MemoryHeapKind Kind { get; } = kind;
        public ulong Size;
        public MemoryHeapHandle Handle;
    }

    private sealed class AliasLayout(
        AliasHeapData[] heaps,
        AliasUse[] uses,
        ResourceTransition[][] checks,
        int[][] checkResources,
        int[][] aliasResources,
        ResourceTransition[] finalChecks,
        int[] finalCheckResources,
        int[] finalAliasResources)
    {
        public AliasHeapData[] Heaps { get; } = heaps;
        public AliasUse[] Uses { get; } = uses;
        public ResourceTransition[][] Checks { get; } = checks;
        public int[][] CheckResources { get; } = checkResources;
        public int[][] AliasResources { get; } = aliasResources;
        public ResourceTransition[] FinalChecks { get; } = finalChecks;
        public int[] FinalCheckResources { get; } = finalCheckResources;
        public int[] FinalAliasResources { get; } = finalAliasResources;
    }

    private readonly record struct AliasHeapData(
        MemoryClass Memory,
        MemoryHeapKind Kind,
        ulong Size);

    internal readonly record struct AliasHeapPoolKey(
        MemoryClass Memory,
        MemoryHeapKind Kind,
        ulong Size);

    private readonly record struct AliasSlotKey(
        MemoryHeapHandle Heap,
        ulong Offset);

    private readonly record struct AliasUse(
        int Resource,
        int FirstPass,
        int LastPass,
        bool Reusable,
        bool Aliased,
        int Heap,
        int Block,
        ulong Offset,
        ulong Size);

    private sealed class AliasBlock(int heapIndex)
    {
        public int HeapIndex { get; } = heapIndex;
        public int LastPass = -1;
        public ulong Size;
        public ulong Align;
        public int Count;
        public ulong Offset;
    }

    private readonly record struct AliasKey(
        MemoryClass Memory,
        MemoryHeapKind Kind);

    internal readonly record struct PooledTexture(
        TextureHandle Handle,
        ResourceState[] States,
        FlatDictionary<TextureViewKey, TextureViewHandle> Views);

    internal readonly record struct PooledBuffer(
        BufferHandle Handle,
        ResourceState State,
        FlatDictionary<BufferViewKey, BufferViewHandle> Views);

    internal readonly record struct RegisteredTextureView(
        int ResourceIndex,
        TextureViewDesc Desc);

    internal readonly record struct RegisteredBufferView(
        int ResourceIndex,
        BufferViewDesc Desc);

    internal readonly record struct PlacedTexturePoolKey(
        TexturePoolKey Texture,
        MemoryHeapHandle Heap,
        ulong Offset);

    internal readonly record struct PlacedBufferPoolKey(
        BufferPoolKey Buffer,
        MemoryHeapHandle Heap,
        ulong Offset);

    internal readonly record struct TexturePoolKey(
        ResourceDimension Dimension,
        uint Width,
        uint Height,
        uint Depth,
        uint MipLevels,
        uint ArraySize,
        uint SampleCount,
        Format Format,
        MemoryClass Memory,
        BindFlags BindFlags,
        ClearValue? OptimizedClearValue)
    {
        public static TexturePoolKey From(TextureDesc desc)
            => new(
                desc.Dimension,
                desc.Width,
                desc.Height,
                desc.Depth,
                desc.MipLevels,
                desc.ArraySize,
                desc.SampleCount,
                desc.Format,
                desc.Memory,
                desc.BindFlags,
                desc.OptimizedClearValue);
    }

    internal readonly record struct BufferPoolKey(
        ulong SizeInBytes,
        MemoryClass Memory,
        BindFlags BindFlags,
        uint StrideInBytes,
        bool Raw)
    {
        public static BufferPoolKey From(BufferDesc desc)
            => new(
                desc.SizeInBytes,
                desc.Memory,
                desc.BindFlags,
                desc.StrideInBytes,
                desc.Raw);
    }

    internal readonly record struct ResourceUse(
        int ResourceIndex,
        ResourceState EntryState,
        ResourceState ExitState,
        RenderGraphAccess Access,
        SubResourceRange Range,
        int InVersion = -1,
        int OutVersion = -1,
        int SourcePass = -1,
        ResourceState BarrierEntryState = ResourceState.Undefined,
        ResourceState BarrierExitState = ResourceState.Undefined)
    {
        public ResourceState EffectiveBarrierEntryState
            => BarrierEntryState == ResourceState.Undefined ? EntryState : BarrierEntryState;

        public ResourceState EffectiveBarrierExitState
            => BarrierExitState == ResourceState.Undefined ? ExitState : BarrierExitState;
    }

    internal readonly record struct PassEpilogueState(
        int ResourceIndex,
        ResourceState EntryState,
        ResourceState ExitState,
        RenderGraphAccess Access,
        SubResourceRange Range);

    [Flags]
    internal enum Hazards
    {
        None = 0,
        Raw = 1,
        Waw = 2,
        War = 4,
    }

    internal readonly record struct PassDependency(
        int SourcePass,
        int TargetPass,
        int ResourceIndex,
        int InVersion,
        int OutVersion,
        Hazards Hazards);

    internal enum QueueBatchKind
    {
        Command,
        Compute,
        Copy,
        Mixed,
    }

    [Flags]
    internal enum QueueMask
    {
        None = 0,
        Graphics = 1,
        Compute = 2,
        Copy = 4,
    }

    internal enum PassMode
    {
        Command,
        Compute,
        AsyncCompute,
        Copy,
        AsyncCopy,
    }

    internal readonly record struct QueueBatch(
        int StartSlot,
        int Count,
        QueueBatchKind Kind,
        QueueType Queue,
        int InputLinks,
        int OutputLinks,
        QueueMask WaitQueues,
        QueueMask SignalQueues);

    internal readonly record struct QueueLink(
        int SourceBatch,
        int TargetBatch,
        QueueType SourceQueue,
        QueueType TargetQueue,
        int DependencyCount,
        Hazards Hazards);

    private readonly record struct FencePoint(
        FenceHandle Fence,
        ulong Value);

    internal enum StateMatch
    {
        Compatible,
        Exact,
    }

    internal readonly record struct ResourceTransition(
        int ResourceIndex,
        ResourceState Before,
        ResourceState After,
        RenderGraphAccess Access,
        SomeEngine.Rhi.SubresourceRange Range,
        bool Required,
        StateMatch Match);

    private sealed class GraphSchemaState
    {
        private const int HeaderCount = 8;
        private const int ResourceToken = -1;
        private const int PassToken = -2;
        private const int UseToken = -3;

        private struct PassSchema
        {
            public int SideEffectIndex;
            public int UseCountIndex;

            public static PassSchema Empty => new()
            {
                SideEffectIndex = -1,
                UseCountIndex = -1,
            };
        }

        private struct ResourceSchema
        {
            public int StartIndex;
            public int Length;

            public static ResourceSchema Empty => new()
            {
                StartIndex = -1,
                Length = 0,
            };
        }

        private readonly record struct FinalStateSchema(int ResourceIndex, int State);

        private readonly List<int> _values = [];
        private readonly List<PassSchema> _passSchemas = [];
        private readonly List<ResourceSchema> _resources = [];
        private readonly List<int> _resourceScratch = [];
        private readonly List<int> _sortedOutputs = [];
        private readonly List<int> _textureExports = [];
        private readonly List<int> _bufferExports = [];
        private readonly List<int> _finalStates = [];
        private readonly List<FinalStateSchema> _sortedFinalStates = [];
        private int _bodyLength;

        public void Reset()
        {
            _values.Clear();
            EnsureHeader();
            _bodyLength = HeaderCount;
            _passSchemas.Clear();
            _resources.Clear();
            _resourceScratch.Clear();
            _sortedOutputs.Clear();
            _textureExports.Clear();
            _bufferExports.Clear();
            _finalStates.Clear();
            _sortedFinalStates.Clear();
        }

        public void AddPass(int passIndex, PassMode mode)
        {
            OpenBody();
            while (_passSchemas.Count <= passIndex)
                _passSchemas.Add(PassSchema.Empty);

            _values.Add(PassToken);
            _values.Add(passIndex);
            _values.Add((int)mode);
            int sideEffectIndex = _values.Count;
            _values.Add(0);
            int useCountIndex = _values.Count;
            _values.Add(0);
            _passSchemas[passIndex] = new PassSchema
            {
                SideEffectIndex = sideEffectIndex,
                UseCountIndex = useCountIndex,
            };
            _bodyLength = _values.Count;
        }

        public void SetSideEffect(int passIndex)
        {
            if ((uint)passIndex >= (uint)_passSchemas.Count)
                return;

            int index = _passSchemas[passIndex].SideEffectIndex;
            if ((uint)index < (uint)_values.Count)
                _values[index] = 1;
        }

        public void AddUse(int passIndex, ResourceUse use)
        {
            OpenBody();
            if ((uint)passIndex < (uint)_passSchemas.Count)
            {
                int countIndex = _passSchemas[passIndex].UseCountIndex;
                if ((uint)countIndex < (uint)_values.Count)
                    _values[countIndex]++;
            }

            _values.Add(UseToken);
            _values.Add(use.ResourceIndex);
            _values.Add((int)use.EntryState);
            _values.Add((int)use.ExitState);
            _values.Add((int)use.Access);
            _values.Add(unchecked((int)use.Range.FirstMipLevel));
            _values.Add(unchecked((int)use.Range.MipLevelCount));
            _values.Add(unchecked((int)use.Range.FirstArraySlice));
            _values.Add(unchecked((int)use.Range.ArraySliceCount));
            _bodyLength = _values.Count;
        }

        public void SetResource(int resourceIndex, Resource resource)
        {
            OpenBody();
            while (_resources.Count <= resourceIndex)
                _resources.Add(ResourceSchema.Empty);

            _resourceScratch.Clear();
            _resourceScratch.Add(ResourceToken);
            _resourceScratch.Add(resourceIndex);
            AddResourceSchema(_resourceScratch, resource);

            ResourceSchema schema = _resources[resourceIndex];
            if (schema.StartIndex >= 0)
            {
                ReplaceResource(schema.StartIndex, schema.Length, _resourceScratch);
                _resources[resourceIndex] = new ResourceSchema
                {
                    StartIndex = schema.StartIndex,
                    Length = _resourceScratch.Count,
                };
                _bodyLength = _values.Count;
                return;
            }

            int startIndex = _values.Count;
            _values.AddRange(_resourceScratch);
            _resources[resourceIndex] = new ResourceSchema
            {
                StartIndex = startIndex,
                Length = _resourceScratch.Count,
            };
            _bodyLength = _values.Count;
        }

        public void AddTextureExport(int resourceIndex)
            => _textureExports.Add(resourceIndex);

        public void AddBufferExport(int resourceIndex)
            => _bufferExports.Add(resourceIndex);

        public void AddFinalState(int resourceIndex, ResourceState state)
        {
            _finalStates.Add(resourceIndex);
            _finalStates.Add((int)state);
        }

        public GraphSchema Finish(
            int passCount,
            int resourceCount,
            bool asyncCompute,
            bool asyncCopy)
        {
            EnsureHeader();
            if (_values.Count > _bodyLength)
                _values.RemoveRange(_bodyLength, _values.Count - _bodyLength);

            int valueCapacity = _bodyLength
                + _textureExports.Count
                + _bufferExports.Count
                + _finalStates.Count;
            if (_values.Capacity < valueCapacity)
                _values.Capacity = valueCapacity;

            _values[0] = passCount;
            _values[1] = resourceCount;
            _values[2] = 0;
            _values[3] = _textureExports.Count;
            _values[4] = _bufferExports.Count;
            _values[5] = _finalStates.Count / 2;
            _values[6] = asyncCompute ? 1 : 0;
            _values[7] = asyncCopy ? 1 : 0;

            _sortedOutputs.Clear();
            _sortedOutputs.AddRange(_textureExports);
            _sortedOutputs.Sort();
            _values.AddRange(_sortedOutputs);

            _sortedOutputs.Clear();
            _sortedOutputs.AddRange(_bufferExports);
            _sortedOutputs.Sort();
            _values.AddRange(_sortedOutputs);

            _sortedFinalStates.Clear();
            for (int i = 0; i < _finalStates.Count; i += 2)
                _sortedFinalStates.Add(new FinalStateSchema(_finalStates[i], _finalStates[i + 1]));
            _sortedFinalStates.Sort(static (left, right) =>
            {
                int resource = left.ResourceIndex.CompareTo(right.ResourceIndex);
                return resource != 0 ? resource : left.State.CompareTo(right.State);
            });
            for (int i = 0; i < _sortedFinalStates.Count; i++)
            {
                _values.Add(_sortedFinalStates[i].ResourceIndex);
                _values.Add(_sortedFinalStates[i].State);
            }

            return new GraphSchema(CollectionsMarshal.AsSpan(_values));
        }

        public ReadOnlySpan<int> Values => CollectionsMarshal.AsSpan(_values);

        private void OpenBody()
        {
            EnsureHeader();
            if (_values.Count > _bodyLength)
                _values.RemoveRange(_bodyLength, _values.Count - _bodyLength);
        }

        private void EnsureHeader()
        {
            if (_values.Count >= HeaderCount)
                return;

            while (_values.Count < HeaderCount)
                _values.Add(0);
            _bodyLength = HeaderCount;
        }

        private void ReplaceResource(
            int startIndex,
            int oldLength,
            IReadOnlyList<int> values)
        {
            int delta = values.Count - oldLength;
            if (delta == 0)
            {
                for (int i = 0; i < values.Count; i++)
                    _values[startIndex + i] = values[i];
                return;
            }

            _values.RemoveRange(startIndex, oldLength);
            _values.InsertRange(startIndex, values);
            ShiftSchemaIndexes(startIndex, delta);
        }

        private void ShiftSchemaIndexes(int startIndex, int delta)
        {
            for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
            {
                ResourceSchema resource = _resources[resourceIndex];
                if (resource.StartIndex > startIndex)
                {
                    _resources[resourceIndex] = new ResourceSchema
                    {
                        StartIndex = resource.StartIndex + delta,
                        Length = resource.Length,
                    };
                }
            }

            for (int passIndex = 0; passIndex < _passSchemas.Count; passIndex++)
            {
                PassSchema pass = _passSchemas[passIndex];
                bool changed = false;
                if (pass.SideEffectIndex > startIndex)
                {
                    pass.SideEffectIndex += delta;
                    changed = true;
                }

                if (pass.UseCountIndex > startIndex)
                {
                    pass.UseCountIndex += delta;
                    changed = true;
                }

                if (changed)
                    _passSchemas[passIndex] = pass;
            }
        }
    }

    internal sealed class TransitionBatch
    {
        public readonly List<ResourceTransition> Transitions = [];
        public readonly List<ResourceTransition> RequiredTransitions = [];
        public readonly List<ResourceTransition> CheckTransitions = [];
        public readonly List<int> Resources = [];
        public readonly List<int> CheckResources = [];
        public readonly List<int> AliasResources = [];
        public IReadOnlyList<ResourceTransition>? SharedTransitions;
        public IReadOnlyList<ResourceTransition>? SharedRequiredTransitions;
        public IReadOnlyList<int>? SharedResources;
        public ResourceTransition[] SharedCheckTransitions = [];
        public int[] SharedCheckResources = [];
        public int[] SharedAliasResources = [];
        private readonly HashSet<int> _resourceSet = [];
        private readonly HashSet<int> _checkLookup = [];
        private int[] _wholeMarks = [];
        private int _wholeStamp;
        public bool HasAliases;

        public IReadOnlyList<ResourceTransition> TransitionSource => SharedTransitions ?? Transitions;
        public IReadOnlyList<ResourceTransition> RequiredTransitionSource => SharedRequiredTransitions ?? RequiredTransitions;
        public IReadOnlyList<int> ResourceSource => SharedResources ?? Resources;
        public int RequiredCount => RequiredTransitionSource.Count;

        public bool IsEmpty => !HasRequired && !HasChecks && !HasAliases;
        public bool HasRequired => RequiredCount != 0;
        public bool HasChecks => CheckTransitions.Count != 0 || SharedCheckTransitions.Length != 0;

        public void Clear()
        {
            Transitions.Clear();
            RequiredTransitions.Clear();
            CheckTransitions.Clear();
            Resources.Clear();
            CheckResources.Clear();
            AliasResources.Clear();
            SharedTransitions = null;
            SharedRequiredTransitions = null;
            SharedResources = null;
            SharedCheckTransitions = [];
            SharedCheckResources = [];
            SharedAliasResources = [];
            _resourceSet.Clear();
            _checkLookup.Clear();
            HasAliases = false;
        }

        public void Add(ResourceTransition transition)
        {
            Transitions.Add(transition);
            AddResource(transition.ResourceIndex);
            if (transition.Required)
                RequiredTransitions.Add(transition);
        }

        public void GatherChecks(ReadOnlySpan<ResourceRecord> resources)
        {
            CheckTransitions.Clear();
            CheckResources.Clear();
            AliasResources.Clear();
            SharedCheckTransitions = [];
            SharedCheckResources = [];
            SharedAliasResources = [];
            _checkLookup.Clear();
            StartCheck(resources.Length);
            HasAliases = false;
            IReadOnlyList<int> resourceSource = ResourceSource;
            for (int i = 0; i < resourceSource.Count; i++)
            {
                int resourceIndex = resourceSource[i];
                if (!resources[resourceIndex].Aliased)
                    continue;

                HasAliases = true;
                AliasResources.Add(resourceIndex);
            }

            IReadOnlyList<ResourceTransition> transitionSource = TransitionSource;
            for (int i = 0; i < transitionSource.Count; i++)
            {
                ResourceTransition transition = transitionSource[i];
                if (transition.Required || !resources[transition.ResourceIndex].Reusable)
                    continue;

                if (IsWholeRange(transition.Range))
                {
                    if (!MarkWhole(transition.ResourceIndex))
                        continue;
                }

                CheckTransitions.Add(transition);
                AddCheck(transition.ResourceIndex);
            }

        }

        public TransitionBatch Copy()
        {
            var copy = new TransitionBatch();
            copy.Transitions.AddRange(TransitionSource);
            copy.RequiredTransitions.AddRange(RequiredTransitionSource);
            copy.CheckTransitions.AddRange(CheckTransitions);
            copy.Resources.AddRange(ResourceSource);
            copy.CheckResources.AddRange(CheckResources);
            copy.AliasResources.AddRange(AliasResources);
            copy.SharedCheckTransitions = SharedCheckTransitions;
            copy.SharedCheckResources = SharedCheckResources;
            copy.SharedAliasResources = SharedAliasResources;
            copy._resourceSet.UnionWith(copy.Resources);
            copy._checkLookup.UnionWith(CheckResources);
            copy.HasAliases = HasAliases;
            return copy;
        }

        private void AddResource(int resourceIndex)
        {
            if (_resourceSet.Add(resourceIndex))
                Resources.Add(resourceIndex);
        }

        private void AddCheck(int resourceIndex)
        {
            if (_checkLookup.Add(resourceIndex))
                CheckResources.Add(resourceIndex);
        }

        private void StartCheck(int count)
        {
            if (_wholeMarks.Length < count)
                _wholeMarks = new int[count];

            _wholeStamp++;
            if (_wholeStamp != int.MaxValue)
                return;

            Array.Clear(_wholeMarks);
            _wholeStamp = 1;
        }

        private bool MarkWhole(int resourceIndex)
        {
            if (_wholeMarks[resourceIndex] == _wholeStamp)
                return false;

            _wholeMarks[resourceIndex] = _wholeStamp;
            return true;
        }

        private static bool IsWholeRange(SomeEngine.Rhi.SubresourceRange range)
            => range.FirstMip == 0
                && range.MipCount == uint.MaxValue
                && range.FirstSlice == 0
                && range.SliceCount == uint.MaxValue;
    }

    private sealed class BarrierState
    {
        public ResourceState[] Buffers = [];
        public RenderGraphAccess[] BufferAccesses = [];
        public ResourceState[][] Textures = [];
        public RenderGraphAccess[][] TextureAccesses = [];

        public void Reset(int resourceCount)
        {
            Ensure(ref Buffers, resourceCount);
            Ensure(ref BufferAccesses, resourceCount);
            Ensure(ref Textures, resourceCount);
            Ensure(ref TextureAccesses, resourceCount);
            Array.Clear(Buffers, 0, resourceCount);
            Array.Clear(BufferAccesses, 0, resourceCount);
        }

        public void ClearTexture(int resourceIndex)
        {
            Textures[resourceIndex] = [];
            TextureAccesses[resourceIndex] = [];
        }

        public void SetTexture(int resourceIndex, int count, ResourceState state)
        {
            ResourceState[] states = Textures[resourceIndex];
            if (states == null || states.Length != count)
            {
                states = new ResourceState[count];
                Textures[resourceIndex] = states;
            }

            Array.Fill(states, state);

            RenderGraphAccess[] accesses = TextureAccesses[resourceIndex];
            if (accesses == null || accesses.Length != count)
            {
                accesses = new RenderGraphAccess[count];
                TextureAccesses[resourceIndex] = accesses;
            }
            else
            {
                Array.Clear(accesses);
            }
        }

        private static void Ensure<T>(ref T[] values, int count)
        {
            if (values.Length < count)
                Array.Resize(ref values, count);
        }
    }

    internal struct ResourceRecord
    {
        public bool Live;
        public int FirstPass;
        public int LastPass;
        public int UseCount;
        public int Version;
        public int LastWriter;
        public TexturePoolKey TextureKey;
        public BufferPoolKey BufferKey;
        public bool Reusable;
        public bool Exported;
        public bool FinalState;
        public bool Retained;
        public bool Aliased;
        public int AliasHeap;
        public int AliasBlock;
        public ulong AliasOffset;
        public ulong AliasSize;

        public static ResourceRecord Empty => new()
        {
            FirstPass = -1,
            LastPass = -1,
            LastWriter = -1,
            AliasHeap = -1,
            AliasBlock = -1,
        };
    }

    internal sealed class CompiledGraph
    {
        public List<int> Passes = [];
        public List<QueueBatch> QueueBatches = [];
        public List<QueueLink> QueueLinks = [];
        public List<int>[] QueueWaits = [];
        public List<PassDependency> Dependencies = [];
        public QueueMask Queues;
        public QueueMask FrameWaits;
        public QueueType FrameQueue;
        public bool FinalWork;
        public bool BatchSignal;
        public List<int> Retains = [];
        public List<ResourceUse>[] Uses = [];
        public PassEpilogueState[][] PassEpilogues = [];
        public List<int>[] Resolves = [];
        public TransitionBatch[] Transitions = [];
        public TransitionBatch FinalTransitions = new();
        public List<int> RootResolves = [];
        public QueueType[] PassQueues = [];
        public int[] PassSlots = [];
        public bool[] LivePasses = [];
        public string?[] PassKeepReasons = [];
        public string?[] PassCullReasons = [];
        public ResourceRecord[] Resources = [];

        public int Count => Passes.Count;

        public CompiledGraph(int passCount, int resourceCount)
        {
            Reset(passCount, resourceCount);
        }

        private CompiledGraph(CompiledGraph source)
            => UseShared(source);

        public void SetOrder(ReadOnlySpan<int> passOrder)
        {
            Passes.Clear();
            for (int passSlot = 0; passSlot < passOrder.Length; passSlot++)
                Passes.Add(passOrder[passSlot]);
            FillPassSlots();
        }

        public void Reset(int passCount, int resourceCount)
        {
            Passes.Clear();
            QueueBatches.Clear();
            QueueLinks.Clear();
            Queues = QueueMask.None;
            FrameWaits = QueueMask.None;
            FrameQueue = QueueType.Graphics;
            FinalWork = false;
            BatchSignal = false;
            for (int i = 0; i < QueueWaits.Length; i++)
                QueueWaits[i].Clear();
            Dependencies.Clear();
            Retains.Clear();
            RootResolves.Clear();
            FinalTransitions.Clear();

            if (PassQueues.Length != passCount)
                PassQueues = new QueueType[passCount];
            else
                Array.Fill(PassQueues, QueueType.Graphics);

            if (PassSlots.Length != passCount)
                PassSlots = new int[passCount];
            Array.Fill(PassSlots, -1);

            if (LivePasses.Length != passCount)
                LivePasses = new bool[passCount];
            else
                Array.Clear(LivePasses);

            if (PassKeepReasons.Length != passCount)
                PassKeepReasons = new string?[passCount];
            else
                Array.Clear(PassKeepReasons);

            if (PassCullReasons.Length != passCount)
                PassCullReasons = new string?[passCount];
            else
                Array.Clear(PassCullReasons);

            if (Uses.Length != passCount)
            {
                Uses = new List<ResourceUse>[passCount];
                PassEpilogues = new PassEpilogueState[passCount][];
                Resolves = new List<int>[passCount];
                Transitions = new TransitionBatch[passCount];
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    Uses[passIndex] = [];
                    PassEpilogues[passIndex] = [];
                    Resolves[passIndex] = [];
                    Transitions[passIndex] = new TransitionBatch();
                }
            }
            else
            {
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    Uses[passIndex].Clear();
                    PassEpilogues[passIndex] = [];
                    Resolves[passIndex].Clear();
                    Transitions[passIndex].Clear();
                }
            }

            if (Resources.Length != resourceCount)
                Resources = new ResourceRecord[resourceCount];
            Array.Fill(Resources, ResourceRecord.Empty);
        }

        public void EnsureShape(int passCount, int resourceCount)
        {
            if (PassSlots.Length < passCount)
            {
                int oldCount = PassSlots.Length;
                int newCount = GrowCount(oldCount, passCount);
                Array.Resize(ref PassSlots, newCount);
                Array.Fill(PassSlots, -1, oldCount, newCount - oldCount);
            }

            if (LivePasses.Length < passCount)
                Array.Resize(ref LivePasses, GrowCount(LivePasses.Length, passCount));

            if (PassKeepReasons.Length < passCount)
                Array.Resize(ref PassKeepReasons, GrowCount(PassKeepReasons.Length, passCount));

            if (PassCullReasons.Length < passCount)
                Array.Resize(ref PassCullReasons, GrowCount(PassCullReasons.Length, passCount));

            if (PassQueues.Length < passCount)
                Array.Resize(ref PassQueues, GrowCount(PassQueues.Length, passCount));

            if (Uses.Length < passCount)
            {
                int oldCount = Uses.Length;
                int newCount = GrowCount(oldCount, passCount);
                Array.Resize(ref Uses, newCount);
                Array.Resize(ref PassEpilogues, newCount);
                Array.Resize(ref Resolves, newCount);
                Array.Resize(ref Transitions, newCount);
                for (int passIndex = oldCount; passIndex < newCount; passIndex++)
                {
                    Uses[passIndex] = [];
                    PassEpilogues[passIndex] = [];
                    Resolves[passIndex] = [];
                    Transitions[passIndex] = new TransitionBatch();
                }
            }

            if (Resources.Length < resourceCount)
            {
                int oldCount = Resources.Length;
                int newCount = GrowCount(oldCount, resourceCount);
                Array.Resize(ref Resources, newCount);
                Array.Fill(Resources, ResourceRecord.Empty, oldCount, newCount - oldCount);
            }
        }

        public void ClearDeclarations()
        {
            Passes.Clear();
            QueueBatches.Clear();
            QueueLinks.Clear();
            Queues = QueueMask.None;
            FrameWaits = QueueMask.None;
            FrameQueue = QueueType.Graphics;
            FinalWork = false;
            BatchSignal = false;
            for (int i = 0; i < QueueWaits.Length; i++)
                QueueWaits[i].Clear();
            Dependencies.Clear();
            Retains.Clear();
            RootResolves.Clear();
            FinalTransitions.Clear();
            Array.Fill(PassQueues, QueueType.Graphics);
            Array.Fill(PassSlots, -1);
            Array.Clear(LivePasses);
            Array.Clear(PassKeepReasons);
            Array.Clear(PassCullReasons);
            for (int passIndex = 0; passIndex < Uses.Length; passIndex++)
            {
                Uses[passIndex].Clear();
                PassEpilogues[passIndex] = [];
                Resolves[passIndex].Clear();
                Transitions[passIndex].Clear();
            }
            Array.Fill(Resources, ResourceRecord.Empty);
        }

        public void UseDeclarations(CompiledGraph source, int passCount, int resourceCount)
        {
            Reset(passCount, resourceCount);
            for (int passIndex = 0; passIndex < passCount; passIndex++)
                Uses[passIndex].AddRange(source.Uses[passIndex]);
        }

        private static int GrowCount(int current, int required)
        {
            int next = current == 0 ? 4 : current * 2;
            return next < required ? required : next;
        }

        public int SlotOf(int passIndex)
            => passIndex >= 0 && passIndex < PassSlots.Length
                ? PassSlots[passIndex]
                : -1;

        public void FillPassSlots()
        {
            Array.Fill(PassSlots, -1);
            for (int passSlot = 0; passSlot < Passes.Count; passSlot++)
                PassSlots[Passes[passSlot]] = passSlot;
        }

        public CompiledGraph Copy()
        {
            var copy = new CompiledGraph(PassSlots.Length, Resources.Length);
            copy.Passes.AddRange(Passes);
            copy.QueueBatches.AddRange(QueueBatches);
            copy.QueueLinks.AddRange(QueueLinks);
            copy.Queues = Queues;
            copy.FrameWaits = FrameWaits;
            copy.FrameQueue = FrameQueue;
            copy.FinalWork = FinalWork;
            copy.BatchSignal = BatchSignal;
            copy.PrepareQueueWaits();
            if (QueueWaits.Length != copy.QueueWaits.Length)
                throw new InvalidOperationException("RenderGraph compiled queue wait shape changed during copy.");
            for (int batchIndex = 0; batchIndex < QueueWaits.Length; batchIndex++)
                copy.QueueWaits[batchIndex].AddRange(QueueWaits[batchIndex]);
            copy.Dependencies.AddRange(Dependencies);
            copy.Retains.AddRange(Retains);
            for (int passIndex = 0; passIndex < Uses.Length; passIndex++)
            {
                copy.Uses[passIndex].AddRange(Uses[passIndex]);
                copy.PassEpilogues[passIndex] = PassEpilogues[passIndex];
                copy.Resolves[passIndex].AddRange(Resolves[passIndex]);
                copy.Transitions[passIndex] = Transitions[passIndex].Copy();
            }

            copy.FinalTransitions = FinalTransitions.Copy();
            copy.RootResolves.AddRange(RootResolves);
            Array.Copy(PassQueues, copy.PassQueues, PassQueues.Length);
            Array.Copy(PassSlots, copy.PassSlots, PassSlots.Length);
            Array.Copy(LivePasses, copy.LivePasses, LivePasses.Length);
            Array.Copy(PassKeepReasons, copy.PassKeepReasons, PassKeepReasons.Length);
            Array.Copy(PassCullReasons, copy.PassCullReasons, PassCullReasons.Length);
            Array.Copy(Resources, copy.Resources, Resources.Length);
            return copy;
        }

        public CompiledGraph Materialize()
            => new(this);

        public void UseShared(CompiledGraph source)
        {
            Passes = source.Passes;
            QueueBatches = source.QueueBatches;
            QueueLinks = source.QueueLinks;
            Queues = source.Queues;
            FrameWaits = source.FrameWaits;
            FrameQueue = source.FrameQueue;
            FinalWork = source.FinalWork;
            BatchSignal = source.BatchSignal;
            QueueWaits = source.QueueWaits;
            Dependencies = source.Dependencies;
            Retains = source.Retains;
            Uses = source.Uses;
            PassEpilogues = source.PassEpilogues;
            Resolves = source.Resolves;
            RootResolves = source.RootResolves;
            PassQueues = source.PassQueues;
            PassSlots = source.PassSlots;
            LivePasses = source.LivePasses;
            PassKeepReasons = source.PassKeepReasons;
            PassCullReasons = source.PassCullReasons;

            if (Transitions.Length != source.Transitions.Length)
            {
                Transitions = new TransitionBatch[source.Transitions.Length];
                for (int passIndex = 0; passIndex < Transitions.Length; passIndex++)
                    Transitions[passIndex] = new TransitionBatch();
            }

            for (int passIndex = 0; passIndex < source.Transitions.Length; passIndex++)
            {
                TransitionBatch target = Transitions[passIndex];
                TransitionBatch shared = source.Transitions[passIndex];
                target.Clear();
                target.SharedTransitions = shared.SharedTransitions ?? shared.Transitions;
                target.SharedRequiredTransitions = shared.SharedRequiredTransitions ?? shared.RequiredTransitions;
                target.SharedResources = shared.SharedResources ?? shared.Resources;
            }

            FinalTransitions.Clear();
            FinalTransitions.SharedTransitions = source.FinalTransitions.SharedTransitions ?? source.FinalTransitions.Transitions;
            FinalTransitions.SharedRequiredTransitions = source.FinalTransitions.SharedRequiredTransitions ?? source.FinalTransitions.RequiredTransitions;
            FinalTransitions.SharedResources = source.FinalTransitions.SharedResources ?? source.FinalTransitions.Resources;

            if (Resources.Length != source.Resources.Length)
                Resources = new ResourceRecord[source.Resources.Length];
            Array.Copy(source.Resources, Resources, source.Resources.Length);
        }

        public void PrepareQueueWaits()
        {
            if (QueueWaits.Length != QueueBatches.Count)
            {
                QueueWaits = new List<int>[QueueBatches.Count];
                for (int i = 0; i < QueueWaits.Length; i++)
                    QueueWaits[i] = [];
                return;
            }

            for (int i = 0; i < QueueWaits.Length; i++)
                QueueWaits[i].Clear();
        }
    }

    private readonly record struct TextureExport(
        int ResourceIndex,
        Action<TextureHandle, ResourceState> Sink);

    private readonly record struct BufferExport(
        int ResourceIndex,
        Action<BufferHandle, ResourceState> Sink);

    internal readonly record struct FinalState(
        int ResourceIndex,
        ResourceState State);

    internal readonly record struct PassSnapshot(
        string Name,
        PassMode Mode,
        bool SideEffect);

    internal readonly record struct ResourceSnapshot(
        string Name,
        ResourceKind Kind,
        TextureDesc? TextureDesc,
        BufferDesc? BufferDesc,
        bool BufferHasInitialData,
        ResourceState CurrentState,
        bool StateTrusted,
        bool Imported,
        bool AllowWrite,
        bool OwnsImportedViews,
        ResourceLifetime Lifetime)
    {
        public static ResourceSnapshot From(Resource resource)
            => new(
                resource.Name,
                resource.Kind,
                resource.TextureDesc,
                resource.BufferDesc,
                resource.BufferHasInitialData,
                resource.CurrentState,
                resource.StateTrusted,
                resource.Imported,
                resource.AllowWrite,
                resource.OwnsImportedViews,
                resource.Lifetime);

        public Resource CreateResource()
        {
            var resource = new Resource(Name, Kind)
            {
                TextureDesc = TextureDesc,
                BufferDesc = BufferDesc,
                BufferHasInitialData = BufferHasInitialData,
                CurrentState = CurrentState,
                StateTrusted = StateTrusted,
                Imported = Imported,
                AllowWrite = AllowWrite,
                OwnsImportedViews = OwnsImportedViews,
                Lifetime = Lifetime,
            };
            return resource;
        }

    }

    internal sealed class RenderGraphSnapshot(
        GraphSchema schema,
        CompiledGraph declarations,
        PassSnapshot[] passes,
        ResourceSnapshot[] resources,
        int[] textureExports,
        int[] bufferExports,
        FinalState[] finalStates)
    {
        public GraphSchema Schema { get; } = schema;

        public CompiledGraph Compile(bool asyncCompute, bool asyncCopy)
        {
            var graph = new RenderGraph
            {
                _schema = Schema,
                _declarations = declarations.Copy(),
                _featuresAppliedThisFrame = true,
            };

            for (int passIndex = 0; passIndex < passes.Length; passIndex++)
            {
                PassSnapshot pass = passes[passIndex];
                graph._passes.Add(new Pass(pass.Name, pass.Mode, static _ => { }, null, null)
                {
                    SideEffect = pass.SideEffect,
                });
            }

            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
                graph._resources.Add(resources[resourceIndex].CreateResource());

            for (int exportIndex = 0; exportIndex < textureExports.Length; exportIndex++)
                graph._textureExports.Add(new TextureExport(textureExports[exportIndex], static (_, _) => { }));
            for (int exportIndex = 0; exportIndex < bufferExports.Length; exportIndex++)
                graph._bufferExports.Add(new BufferExport(bufferExports[exportIndex], static (_, _) => { }));
            graph._finalStates.AddRange(finalStates);

            return graph.CompileUncached(asyncCompute, asyncCopy);
        }
    }

    private readonly List<Pass> _passes = [];
    private readonly List<Resource> _resources = [];
    private readonly Stack<Resource> _resourcePool = [];
    private readonly List<IRenderFeature> _features = [];
    private CompiledGraph? _activeCompile;
    private CompiledGraph? _declarations;
    private GraphSchema? _schema;
    private bool _declarationScratchDirty;
    private bool _declarationsFrozen;
    private readonly GraphScratch _scratch = new();
    private readonly ResourceStateTracker _resourceStates = new();
    private CommandBarriers[] _passBarriers = [];
    private readonly List<int> _dirtyBarrierSlots = [];
    private readonly CommandBarriers _finalBarriers = new();
    private readonly GraphSchemaState _schemaState = new();
    private readonly List<TextureExport> _textureExports = [];
    private readonly List<BufferExport> _bufferExports = [];
    private readonly List<FinalState> _finalStates = [];
    private readonly RenderGraphCompiler _compiler;
    private readonly RenderGraphExecutor _executor;
    private readonly GraphCache<AliasLayout> _aliasCache = new();
    private IDevice? _lastDevice;
    private int _generation = 1;
    private static int s_nextGraphId;
    private readonly int _graphId = Interlocked.Increment(ref s_nextGraphId);
    private int _graphRevision = 1;
    private int _setupPassIndex = -1;
    private int _nextSetupToken;
    private int _activeSetupToken = -1;
    private int _activeCallbackDepth;
    private int _activeCallbackPassIndex = -1;
    private int _activeCallbackIsSetup;
    [ThreadStatic]
    private static RenderGraph? threadExecuteGraph;
    [ThreadStatic]
    private static int threadExecutePassIndex;
    private bool _compileCompute;
    private bool _compileCopy;
    private bool _featuresAppliedThisFrame;
    private bool _disposed;
    private int _backgroundCompileActive;
    private int _backgroundCompilePending;

    internal int Generation => _generation;
    internal int GraphId => _graphId;
    public bool IsDisposed => _disposed;
    public bool EnableResourceAliasing { get; set; } = true;
    public RenderGraphBlackboard Blackboard { get; } = new();
    private System.Threading.Lock _viewCacheGate => _executor.ViewCacheGate;
    private System.Threading.Lock _bindingSetGate => _executor.BindingSetGate;
    private System.Threading.Lock _frameUseGate => _executor.FrameUseGate;
    private List<TextureViewHandle> _ownedTextureViews => _executor.OwnedTextureViews;
    private List<BufferViewHandle> _ownedBufferViews => _executor.OwnedBufferViews;
    private FlatDictionary<TexturePoolKey, Stack<PooledTexture>> _texturePool => _executor.TexturePool;
    private FlatDictionary<BufferPoolKey, Stack<PooledBuffer>> _bufferPool => _executor.BufferPool;
    private FlatDictionary<PlacedTexturePoolKey, Stack<PooledTexture>> _placedTexturePool => _executor.PlacedTexturePool;
    private FlatDictionary<PlacedBufferPoolKey, Stack<PooledBuffer>> _placedBufferPool => _executor.PlacedBufferPool;
    private FrameUploadBuffer _frameUploadBuffer => _executor.FrameUploadBuffer;
    private FlatDictionary<AliasHeapPoolKey, Stack<MemoryHeapHandle>> _aliasHeapPool => _executor.AliasHeapPool;
    private List<AliasHeap> _aliasHeaps => _executor.AliasHeaps;
    private FlatDictionary<int, List<BindingSetEntry>> _bindingSetOwner => _executor.BindingSetOwner;
    private BindingIndex _bindingIndex => _executor.BindingIndex;
    private BindingCache _bindingCache => _executor.BindingCache;
    private Dictionary<TextureViewHandle, RegisteredTextureView> _textureViewBindings => _executor.TextureViewBindings;
    private Dictionary<BufferViewHandle, RegisteredBufferView> _bufferViewBindings => _executor.BufferViewBindings;
    private HashSet<TextureViewHandle> _frameExternalTextureViews => _executor.FrameExternalTextureViews;
    private HashSet<BufferViewHandle> _frameExternalBufferViews => _executor.FrameExternalBufferViews;
    private HashSet<BindingSetHandle> _frameTransientBindingSets => _executor.FrameTransientBindingSets;
    private HashSet<BindingSetHandle> _frameBindingSets => _executor.FrameBindingSets;
    private HashSet<PipelineHandle> _framePipelines => _executor.FramePipelines;
    private FlatDictionary<BindingSetHandle, ulong> _bindingSetLastUse => _executor.BindingSetLastUse;
    private List<(BindingSetHandle Handle, ulong FenceValue)> _deferredBindingSetDestroys => _executor.DeferredBindingSetDestroys;
    private PipelineCache? _pipelineCache
    {
        get => _executor.PipelineCache;
        set => _executor.PipelineCache = value;
    }

    public RenderGraph()
    {
        _compiler = new RenderGraphCompiler(this);
        _executor = new RenderGraphExecutor(this);
    }

    public void BeginFrame(Action<RenderGraph> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        BeginFrameCore(static (graph, callback) => callback(graph), record);
    }

    public void BeginFrame<TState>(TState state, Action<RenderGraph, TState> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        BeginFrameCore(record, state);
    }

    private void BeginFrameCore<TState>(Action<RenderGraph, TState> record, TState state)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(BeginFrame));
        WaitForBackgroundCompile();
        ThrowIfBackgroundCompile(nameof(BeginFrame));

        BeginFrame();
        using (Profiler.BeginScope("RenderGraph.BeginFrame.Record"))
        {
            record(this, state);
        }
    }

    public void BeginFrame()
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(BeginFrame));
        using (Profiler.BeginScope("RenderGraph.BeginFrame.WaitForBackgroundCompile"))
        {
            WaitForBackgroundCompile();
        }
        ThrowIfBackgroundCompile(nameof(BeginFrame));
        using (Profiler.BeginScope("RenderGraph.BeginFrame.RetirePendingFrames"))
        {
            RetirePendingFrames(wait: false);
        }
        if (!_executor.CurrentFrameSubmitted)
        {
            using (Profiler.BeginScope("RenderGraph.BeginFrame.ReleaseFrameResources"))
            {
                ReleaseFrameResources();
            }
        }
        _passes.Clear();
        RecycleResources();
        _resources.Clear();
        _textureExports.Clear();
        _bufferExports.Clear();
        _finalStates.Clear();

        _activeCompile = null;
        _schema = null;
        _declarationScratchDirty = true;
        MarkGraphChanged();
        Blackboard.Clear();
        _featuresAppliedThisFrame = false;
        _executor.CurrentFrameSubmitted = false;
        ClearFrameUsage();
        _generation++;
        if (_generation <= 0)
            _generation = 1;

        if (_features.Count != 0)
        {
            foreach (IRenderFeature feature in _features)
                feature.AddPasses(this);
            _featuresAppliedThisFrame = true;
        }
    }

    internal void UsePipelineCache(PipelineCache? cache)
    {
        if (cache == null)
            return;
        ThrowIfBackgroundCompile(nameof(UsePipelineCache));
        if (_pipelineCache != null && !ReferenceEquals(_pipelineCache, cache))
            throw new InvalidOperationException("RenderGraph cannot use multiple pipeline caches.");

        _pipelineCache = cache;
    }

    internal void TrackPipeline(PipelineHandle pipeline)
    {
        if (_pipelineCache == null || !pipeline.IsValid)
            return;

        lock (_frameUseGate)
        {
            _framePipelines.Add(pipeline);
        }
    }

    private void ClearFrameUsage()
    {
        lock (_frameUseGate)
        {
            _frameBindingSets.Clear();
            _framePipelines.Clear();
            _textureViewBindings.Clear();
            _bufferViewBindings.Clear();
            _frameExternalTextureViews.Clear();
            _frameExternalBufferViews.Clear();
            _frameTransientBindingSets.Clear();
        }
    }

    private void BeginExecutePass(int passIndex)
    {
        threadExecuteGraph = this;
        threadExecutePassIndex = passIndex + 1;
        Volatile.Write(ref _activeCallbackPassIndex, passIndex);
        Volatile.Write(ref _activeCallbackIsSetup, 0);
        Interlocked.Increment(ref _activeCallbackDepth);
    }

    private void EndExecutePass()
    {
        if (Interlocked.Decrement(ref _activeCallbackDepth) == 0)
        {
            Volatile.Write(ref _activeCallbackPassIndex, -1);
            Volatile.Write(ref _activeCallbackIsSetup, 0);
        }
        threadExecuteGraph = null;
        threadExecutePassIndex = 0;
    }

    internal PipelineHandle GetPipeline(PipelineTicket ticket, PipelineNeed need, string site)
    {
        PipelineCache cache = _pipelineCache
            ?? throw new InvalidOperationException("RenderGraph requires a pipeline cache before resolving pipeline tickets.");
        PipelineHandle pipeline = cache.GetPipeline(ticket, need, site);
        TrackPipeline(pipeline);
        return pipeline;
    }

    internal string PassName(int passIndex)
        => passIndex >= 0 && passIndex < _passes.Count
            ? _passes[passIndex].Name
            : $"Pass {passIndex}";

    internal void AddFeature(IRenderFeature feature)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(AddFeature));
        ThrowIfBackgroundCompile(nameof(AddFeature));
        ArgumentNullException.ThrowIfNull(feature);
        _features.Add(feature);
        _compiler.GraphCache.Clear();
        _aliasCache.Clear();
        _compiler.CompileResult = null;
        _compiler.CompileSchema = null;
        MarkGraphChanged();
        _compiler.CompileScratch = null;
        _compiler.CompileWork = null;
    }

    internal void RemoveFeature(IRenderFeature feature)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(RemoveFeature));
        ThrowIfBackgroundCompile(nameof(RemoveFeature));
        ArgumentNullException.ThrowIfNull(feature);
        if (_features.Remove(feature))
        {
            _compiler.GraphCache.Clear();
            _aliasCache.Clear();
            _compiler.CompileResult = null;
            _compiler.CompileSchema = null;
            MarkGraphChanged();
            _compiler.CompileScratch = null;
            _compiler.CompileWork = null;
        }
    }

    public RenderGraphHandle ImportTexture(
        string name,
        TextureHandle texture,
        TextureDesc desc,
        ImportDesc import,
        ReadOnlySpan<TextureViewHandle> views = default)
        => ImportTextureCore(name, texture, desc, import, views, retainedViews: null, ownsImportedViews: true);

    internal RenderGraphHandle ImportTexture(
        string name,
        TextureHandle texture,
        TextureDesc desc,
        ImportDesc import,
        TextureViewHandle renderTargetView)
        => ImportTextureCore(
            name,
            texture,
            desc,
            import,
            renderTargetView.IsValid ? [renderTargetView] : default,
            retainedViews: null,
            ownsImportedViews: true);

    internal RenderGraphHandle ImportTexture(
        string name,
        TextureHandle texture,
        TextureDesc desc,
        ImportDesc import,
        FlatDictionary<TextureViewKey, TextureViewHandle> retainedViews)
    {
        ArgumentNullException.ThrowIfNull(retainedViews);
        return ImportTextureCore(name, texture, desc, import, default, retainedViews, ownsImportedViews: false);
    }

    private RenderGraphHandle ImportTextureCore(
        string name,
        TextureHandle texture,
        TextureDesc desc,
        ImportDesc import,
        ReadOnlySpan<TextureViewHandle> importedViews,
        FlatDictionary<TextureViewKey, TextureViewHandle>? retainedViews,
        bool ownsImportedViews)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ImportTexture));
        ThrowIfBackgroundCompile(nameof(ImportTexture));
        if (!texture.IsValid)
            throw new ArgumentException("Imported texture handle must be valid.", nameof(texture));
        for (int viewIndex = 0; viewIndex < importedViews.Length; viewIndex++)
        {
            if (!importedViews[viewIndex].IsValid)
                throw new ArgumentException("Imported texture view handles must be valid.", nameof(importedViews));
        }

        RejectDuplicateImport(texture, nameof(ImportTexture));
        ValidateImportedTextureState(desc, import.InitialState, nameof(ImportDesc.InitialState));
        if (import.FinalState.HasValue)
            ValidateImportedTextureState(desc, import.FinalState.Value, nameof(ImportDesc.FinalState));

        int index = AddResource(name, ResourceKind.Texture);
        var resource = _resources[index];
        resource.Texture = texture;
        resource.TextureDesc = desc;
        resource.CurrentState = import.InitialState;
        InitializeTextureStates(resource, desc, import.InitialState);
        resource.StateTrusted = false;
        resource.HasCallerOwnedImportedViews = importedViews.Length != 0;
        if (retainedViews != null)
            resource.TextureViews = retainedViews;
        ApplyImport(index, resource, import, ownsImportedViews);
        if (importedViews.Length != 0)
            resource.PendingImportedTextureViews = importedViews.ToArray();

        EnsureDeclarationScratch();
        _schemaState.SetResource(index, resource);
        MarkGraphChanged();
        return new RenderGraphHandle(index, _graphId, _generation);
    }

    private static void ValidateImportedTextureState(TextureDesc desc, ResourceState state, string propertyName)
        => ValidateImportedResourceState(
            "texture",
            state,
            ResourceStateValidation.TextureRequirement(state, allowPresent: true),
            desc.BindFlags,
            propertyName);

    public RenderGraphHandle ImportBuffer(
        string name,
        BufferHandle buffer,
        BufferDesc desc,
        ImportDesc import,
        ReadOnlySpan<BufferViewHandle> views = default)
        => ImportBufferCore(name, buffer, desc, import, views, retainedViews: null, ownsImportedViews: true);

    internal RenderGraphHandle ImportBuffer(
        string name,
        BufferHandle buffer,
        BufferDesc desc,
        ImportDesc import,
        FlatDictionary<BufferViewKey, BufferViewHandle> retainedViews)
    {
        ArgumentNullException.ThrowIfNull(retainedViews);
        return ImportBufferCore(name, buffer, desc, import, default, retainedViews, ownsImportedViews: false);
    }

    private RenderGraphHandle ImportBufferCore(
        string name,
        BufferHandle buffer,
        BufferDesc desc,
        ImportDesc import,
        ReadOnlySpan<BufferViewHandle> importedViews,
        FlatDictionary<BufferViewKey, BufferViewHandle>? retainedViews,
        bool ownsImportedViews,
        bool allowDuplicateImportHandle = false)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ImportBuffer));
        ThrowIfBackgroundCompile(nameof(ImportBuffer));
        if (!buffer.IsValid)
            throw new ArgumentException("Imported buffer handle must be valid.", nameof(buffer));
        for (int viewIndex = 0; viewIndex < importedViews.Length; viewIndex++)
        {
            if (!importedViews[viewIndex].IsValid)
                throw new ArgumentException("Imported buffer view handles must be valid.", nameof(importedViews));
        }

        RejectDuplicateImport(buffer, nameof(ImportBuffer), allowDuplicateImportHandle);
        ValidateImportedBufferState(desc, import.InitialState, nameof(ImportDesc.InitialState));
        if (import.FinalState.HasValue)
            ValidateImportedBufferState(desc, import.FinalState.Value, nameof(ImportDesc.FinalState));

        int index = AddResource(name, ResourceKind.Buffer);
        var resource = _resources[index];
        resource.Buffer = buffer;
        resource.BufferDesc = desc;
        resource.CurrentState = import.InitialState;
        resource.StateTrusted = false;
        resource.AllowDuplicateImportHandle = allowDuplicateImportHandle;
        resource.HasCallerOwnedImportedViews = importedViews.Length != 0;
        if (retainedViews != null)
            resource.BufferViews = retainedViews;
        if (importedViews.Length != 0)
            resource.PendingImportedBufferViews = importedViews.ToArray();
        ApplyImport(index, resource, import, ownsImportedViews);
        EnsureDeclarationScratch();
        _schemaState.SetResource(index, resource);
        MarkGraphChanged();
        return new RenderGraphHandle(index, _graphId, _generation);
    }

    private static void ValidateImportedBufferState(BufferDesc desc, ResourceState state, string propertyName)
        => ValidateImportedResourceState(
            "buffer",
            state,
            ResourceStateValidation.BufferRequirement(state),
            desc.BindFlags,
            propertyName);

    private static void ValidateImportedResourceState(
        string resourceKind,
        ResourceState state,
        ResourceStateValidationResult requirement,
        BindFlags bindFlags,
        string propertyName)
    {
        if (!requirement.IsValid)
        {
            throw new ArgumentException(
                $"Imported {resourceKind} {propertyName} {requirement.InvalidReason}.",
                propertyName);
        }

        if (requirement.RequiredBindFlags == BindFlags.None)
            return;

        if (requirement.AcceptsAnyBindFlag)
        {
            if ((bindFlags & requirement.RequiredBindFlags) != 0)
                return;

            throw new ArgumentException(
                $"Imported {resourceKind} {propertyName} {state} requires at least one read bind flag.",
                propertyName);
        }

        if ((bindFlags & requirement.RequiredBindFlags) == requirement.RequiredBindFlags)
            return;

        throw new ArgumentException(
            $"Imported {resourceKind} {propertyName} {state} requires bind flag {requirement.RequiredBindFlags}.",
            propertyName);
    }

    private void RejectDuplicateImport(TextureHandle texture, string operation)
    {
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.Imported && resource.Kind == ResourceKind.Texture && resource.Texture == texture)
            {
                throw new InvalidOperationException(
                    $"RenderGraph {operation} cannot import the same external texture handle twice in one frame ('{resource.Name}').");
            }
        }
    }

    private void RejectDuplicateImport(BufferHandle buffer, string operation, bool allowDuplicateImportHandle)
    {
        if (allowDuplicateImportHandle)
            return;

        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.Imported && resource.Kind == ResourceKind.Buffer && resource.Buffer == buffer)
            {
                throw new InvalidOperationException(
                    $"RenderGraph {operation} cannot import the same external buffer handle twice in one frame ('{resource.Name}').");
            }
        }
    }

    internal bool TryImportFrameUploadBuffer(
        string name,
        ReadOnlySpan<byte> data,
        ulong logicalSize,
        out RenderGraphHandle handle,
        out ulong sourceOffset)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ImportBuffer));
        ThrowIfBackgroundCompile(nameof(ImportBuffer));
        var device = _lastDevice;
        if (device == null)
        {
            handle = default;
            sourceOffset = 0;
            return false;
        }

        FrameUploadAllocation allocation = _frameUploadBuffer.Allocate(device, name, data, logicalSize);
        handle = ImportBuffer(
            name,
            allocation.Buffer,
            allocation.Desc,
            new ImportDesc(ResourceState.CopySource),
            allowDuplicateImportHandle: true);
        sourceOffset = allocation.Offset;
        return true;
    }

    internal bool TryImportFrameUploadBuffer<TState>(
        string name,
        int byteCount,
        ulong logicalSize,
        TState state,
        Action<Memory<byte>, TState> write,
        out RenderGraphHandle handle,
        out ulong sourceOffset)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ImportBuffer));
        ThrowIfBackgroundCompile(nameof(ImportBuffer));
        var device = _lastDevice;
        if (device == null)
        {
            handle = default;
            sourceOffset = 0;
            return false;
        }

        FrameUploadAllocation allocation = _frameUploadBuffer.Allocate(
            device,
            name,
            byteCount,
            logicalSize,
            state,
            write);
        handle = ImportBuffer(
            name,
            allocation.Buffer,
            allocation.Desc,
            new ImportDesc(ResourceState.CopySource),
            allowDuplicateImportHandle: true);
        sourceOffset = allocation.Offset;
        return true;
    }

    private RenderGraphHandle ImportBuffer(
        string name,
        BufferHandle buffer,
        BufferDesc desc,
        ImportDesc import,
        bool allowDuplicateImportHandle)
        => ImportBufferCore(name, buffer, desc, import, default, retainedViews: null, ownsImportedViews: true, allowDuplicateImportHandle);

    private void ApplyImport(int resourceIndex, Resource resource, ImportDesc import, bool ownsImportedViews)
    {
        resource.Imported = true;
        resource.AllowWrite = import.AllowWrite;
        resource.OwnsImportedViews = ownsImportedViews;
        if (import.FinalState.HasValue)
            AddFinalState(resourceIndex, import.FinalState.Value, "Import");
    }

    public RenderGraphHandle CreateTexture(string name, TextureDesc desc)
        => CreateTextureCore(name, desc, ResourceLifetime.Pooled);

    public RenderGraphHandle CreateTexture(string name, TextureDesc desc, ResourceLifetime lifetime)
        => CreateTextureCore(name, desc, lifetime);

    private RenderGraphHandle CreateTextureCore(string name, TextureDesc desc, ResourceLifetime lifetime)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(CreateTexture));
        ThrowIfBackgroundCompile(nameof(CreateTexture));
        if (lifetime is not ResourceLifetime.Pooled and not ResourceLifetime.Transient)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "RenderGraph resource lifetime is not defined.");
        int index = AddResource(name, ResourceKind.Texture);
        var resource = _resources[index];
        resource.TextureDesc = desc;
        resource.CurrentState = desc.InitialState;
        resource.Lifetime = lifetime;
        InitializeTextureStates(resource, desc, desc.InitialState);
        resource.StateTrusted = false;
        EnsureDeclarationScratch();
        _schemaState.SetResource(index, resource);
        MarkGraphChanged();
        return new RenderGraphHandle(index, _graphId, _generation);
    }

    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc)
        => CreateBufferCore(name, desc, ReadOnlySpan<byte>.Empty, ResourceLifetime.Pooled);

    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc, ResourceLifetime lifetime)
        => CreateBufferCore(name, desc, ReadOnlySpan<byte>.Empty, lifetime);

    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc, ReadOnlySpan<byte> initialData)
        => CreateBufferCore(name, desc, initialData, ResourceLifetime.Pooled);

    private RenderGraphHandle CreateBufferCore(
        string name,
        BufferDesc desc,
        ReadOnlySpan<byte> initialData,
        ResourceLifetime lifetime)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(CreateBuffer));
        ThrowIfBackgroundCompile(nameof(CreateBuffer));
        if (lifetime is not ResourceLifetime.Pooled and not ResourceLifetime.Transient)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "RenderGraph resource lifetime is not defined.");
        if (!initialData.IsEmpty && (ulong)initialData.Length > desc.SizeInBytes)
        {
            throw new ArgumentException(
                $"RenderGraph buffer '{name}' initial data ({initialData.Length} bytes) exceeds buffer size {desc.SizeInBytes} bytes.",
                nameof(initialData));
        }

        int index = AddResource(name, ResourceKind.Buffer);
        var resource = _resources[index];
        resource.BufferDesc = desc;
        resource.BufferInitialData = initialData.IsEmpty ? null : initialData.ToArray();
        resource.BufferHasInitialData = !initialData.IsEmpty;
        resource.CurrentState = desc.InitialState;
        resource.Lifetime = lifetime;
        resource.StateTrusted = false;
        EnsureDeclarationScratch();
        _schemaState.SetResource(index, resource);
        MarkGraphChanged();
        return new RenderGraphHandle(index, _graphId, _generation);
    }

    public void AddRasterPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute)
        where TData : class, new()
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(execute);
        var passData = PreparePassData(setup);
        AddPass(
            name,
            PassMode.Command,
            passData.Setup,
            context => execute(context, passData.Current()),
            computeExecute: null);
    }

    internal void AddRasterPass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        AddPass(name, PassMode.Command, setup, execute, computeExecute: null);
    }

    public void AddCopyPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute)
        where TData : class, new()
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(execute);
        var passData = PreparePassData(setup);
        AddPass(
            name,
            PassMode.Copy,
            passData.Setup,
            context => execute(context, passData.Current()),
            computeExecute: null);
    }

    internal void AddCopyPass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        AddPass(name, PassMode.Copy, setup, execute, computeExecute: null);
    }

    internal void AddAsyncCopyPass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        AddPass(name, PassMode.AsyncCopy, setup, execute, computeExecute: null);
    }

    public void AddComputePass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, IComputeCommands, TData> execute)
        where TData : class, new()
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(execute);
        var passData = PreparePassData(setup);
        AddPass(
            name,
            PassMode.Compute,
            passData.Setup,
            commandExecute: null,
            (context, pass) => execute(context, pass, passData.Current()));
    }

    private static (Action<RenderGraphBuilder> Setup, Func<TData> Current) PreparePassData<TData>(
        Action<RenderGraphBuilder, TData> setup)
        where TData : class, new()
    {
        TData data = new();
        return (
            builder =>
            {
                TData nextData = new();
                setup(builder, nextData);
                data = nextData;
            },
            () => data);
    }

    internal void AddComputePass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext, IComputeCommands> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        AddPass(name, PassMode.Compute, setup, commandExecute: null, execute);
    }

    internal void AddAsyncComputePass(
        string name,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext, IComputeCommands> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        AddPass(name, PassMode.AsyncCompute, setup, commandExecute: null, execute);
    }

    private void AddPass(
        string name,
        PassMode mode,
        Action<RenderGraphBuilder> setup,
        Action<RenderGraphContext>? commandExecute,
        Action<RenderGraphContext, IComputeCommands>? computeExecute)
    {
        string operation = mode switch
        {
            PassMode.Copy => nameof(AddCopyPass),
            PassMode.AsyncCopy => nameof(AddAsyncCopyPass),
            PassMode.Compute => nameof(AddComputePass),
            PassMode.AsyncCompute => nameof(AddAsyncComputePass),
            _ => nameof(AddRasterPass),
        };
        ThrowIfDisposed();
        ThrowIfActive(operation);
        ThrowIfBackgroundCompile(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(setup);
        EnsureDeclarationScratch();

        var pass = new Pass(name, mode, setup, commandExecute, computeExecute);
        int passIndex = _passes.Count;
        int resourceCountBeforeSetup = _resources.Count;
        TextureDesc?[]? textureDescsBeforeSetup = null;
        BufferDesc?[]? bufferDescsBeforeSetup = null;
        if (resourceCountBeforeSetup != 0)
        {
            textureDescsBeforeSetup = new TextureDesc?[resourceCountBeforeSetup];
            bufferDescsBeforeSetup = new BufferDesc?[resourceCountBeforeSetup];
            for (int resourceIndex = 0; resourceIndex < resourceCountBeforeSetup; resourceIndex++)
            {
                Resource resource = _resources[resourceIndex];
                textureDescsBeforeSetup[resourceIndex] = resource.TextureDesc;
                bufferDescsBeforeSetup[resourceIndex] = resource.BufferDesc;
            }
        }

        _passes.Add(pass);
        _schemaState.AddPass(passIndex, mode);
        try
        {
            SetupPass(passIndex, pass);
        }
        catch
        {
            if (textureDescsBeforeSetup != null && bufferDescsBeforeSetup != null)
            {
                for (int resourceIndex = 0; resourceIndex < resourceCountBeforeSetup; resourceIndex++)
                {
                    Resource resource = _resources[resourceIndex];
                    resource.TextureDesc = textureDescsBeforeSetup[resourceIndex];
                    resource.BufferDesc = bufferDescsBeforeSetup[resourceIndex];
                }
            }

            RemoveLastPass(passIndex);
            RebuildDeclarations();
            throw;
        }
        MarkGraphChanged();
    }

    public void ExtractTexture(RenderGraphHandle handle, ResourceState finalState, Action<TextureHandle, ResourceState> sink)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ExtractTexture));
        ThrowIfBackgroundCompile(nameof(ExtractTexture));
        ArgumentNullException.ThrowIfNull(sink);
        int resourceIndex = GetResourceIndex(handle, nameof(ExtractTexture));
        var resource = _resources[resourceIndex];
        if (resource.Kind != ResourceKind.Texture)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a texture.");
        AddFinalState(resourceIndex, finalState, nameof(ExtractTexture));

        _textureExports.Add(new TextureExport(resourceIndex, sink));
        EnsureDeclarationScratch();
        _schemaState.AddTextureExport(resourceIndex);
        MarkGraphChanged();
    }

    internal void SetFinalState(RenderGraphHandle handle, ResourceState finalState)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(SetFinalState));
        ThrowIfBackgroundCompile(nameof(SetFinalState));
        int resourceIndex = GetResourceIndex(handle, nameof(SetFinalState));
        AddFinalState(resourceIndex, finalState, nameof(SetFinalState));
        MarkGraphChanged();
    }

    public void ExtractBuffer(RenderGraphHandle handle, ResourceState finalState, Action<BufferHandle, ResourceState> sink)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(ExtractBuffer));
        ThrowIfBackgroundCompile(nameof(ExtractBuffer));
        ArgumentNullException.ThrowIfNull(sink);
        int resourceIndex = GetResourceIndex(handle, nameof(ExtractBuffer));
        var resource = _resources[resourceIndex];
        if (resource.Kind != ResourceKind.Buffer)
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a buffer.");
        AddFinalState(resourceIndex, finalState, nameof(ExtractBuffer));

        _bufferExports.Add(new BufferExport(resourceIndex, sink));
        EnsureDeclarationScratch();
        _schemaState.AddBufferExport(resourceIndex);
        MarkGraphChanged();
    }

    private void AddFinalState(int resourceIndex, ResourceState finalState, string operation)
    {
        Resource resource = _resources[resourceIndex];
        bool allowPresent = resource.Imported && resource.Kind == ResourceKind.Texture;
        if (EnsureState(resource, finalState, allowPresent))
        {
            EnsureDeclarationScratch();
            _schemaState.SetResource(resourceIndex, resource);
        }
        for (int i = 0; i < _finalStates.Count; i++)
        {
            var existing = _finalStates[i];
            if (existing.ResourceIndex != resourceIndex)
                continue;

            if (existing.State != finalState)
                RejectFinalState(resourceIndex, existing.State, finalState, operation);
            return;
        }

        _finalStates.Add(new FinalState(resourceIndex, finalState));
        EnsureDeclarationScratch();
        _schemaState.AddFinalState(resourceIndex, finalState);
    }

    private void RejectFinalState(
        int resourceIndex,
        ResourceState existingState,
        ResourceState requestedState,
        string operation)
    {
        var resource = _resources[resourceIndex];
        throw new InvalidOperationException(
            $"RenderGraph {operation} requested final state {requestedState} for '{resource.Name}', but it already has final state {existingState}.");
    }

    internal void RegisterResourceUse(
        RenderGraphHandle handle,
        int passIndex,
        int setupToken,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access,
        SubResourceRange range)
    {
        RequireActiveSetup(passIndex, setupToken, "pass setup");

        int resourceIndex = GetResourceIndex(handle, "RenderGraph pass setup");
        var resource = _resources[resourceIndex];
        if (resource.Imported
            && !resource.AllowWrite
            && Writes(access))
        {
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' writes imported resource '{resource.Name}' without {nameof(ImportDesc.AllowWrite)}.");
        }

        bool resourceChanged = EnsureState(resource, entryState);
        resourceChanged |= EnsureState(resource, exitState);
        EnsureDeclarationScratch();
        if (resourceChanged)
            _schemaState.SetResource(resourceIndex, resource);
        var use = new ResourceUse(resourceIndex, entryState, exitState, access, range);
        CurrentCompile().Uses[passIndex].Add(use);
        _schemaState.AddUse(passIndex, use);
    }

    internal void MarkSideEffect(int passIndex, int setupToken)
    {
        RequireActiveSetup(passIndex, setupToken, nameof(MarkSideEffect));

        EnsureDeclarationScratch();
        _passes[passIndex].SideEffect = true;
        _schemaState.SetSideEffect(passIndex);
    }

    private void RequireActiveSetup(int passIndex, int setupToken, string operation)
    {
        ThrowIfBackgroundCompile(operation);
        if (passIndex < 0 || passIndex >= _passes.Count)
            throw new InvalidOperationException("RenderGraph pass setup is outside a registered pass.");
        if (_setupPassIndex != passIndex || _activeSetupToken != setupToken || !ReferenceEquals(_activeCompile, _declarations))
        {
            throw new InvalidOperationException(
                $"RenderGraphBuilder is only valid during setup for its owning pass '{_passes[passIndex].Name}'.");
        }
    }

    internal void ValidatePassAccess(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        string operation)
    {
        List<ResourceUse> uses = RequireDeclaredResourceUses(handle, passIndex, kind, operation, out int resourceIndex, out Resource resource);

        foreach (var use in uses)
        {
            if (use.ResourceIndex == resourceIndex)
                return;
        }

        throw new InvalidOperationException(
            UndeclaredResourceUseMessage(passIndex, resource, operation));
    }

    internal void ValidatePassAccess(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation)
        => ValidatePassAccess(handle, passIndex, kind, requiredState, requiredAccess, SubResourceRange.All, operation, currentState: null);

    internal void ValidatePassAccess(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation,
        ResourceState? currentState)
        => ValidatePassAccess(handle, passIndex, kind, requiredState, requiredAccess, SubResourceRange.All, operation, currentState);

    internal void ValidatePassRange(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        SubResourceRange requiredRange,
        string operation)
    {
        List<ResourceUse> uses = RequireDeclaredResourceUses(handle, passIndex, kind, operation, out int resourceIndex, out Resource resource);

        bool declared = false;
        foreach (var use in uses)
        {
            if (use.ResourceIndex != resourceIndex)
                continue;

            declared = true;
            if (kind != ResourceKind.Texture || RangeSatisfies(resource, use.Range, requiredRange))
                return;
        }

        if (!declared)
            throw new InvalidOperationException(UndeclaredResourceUseMessage(passIndex, resource, operation));

        throw new InvalidOperationException(
            $"RenderGraph pass '{_passes[passIndex].Name}' used resource '{resource.Name}' during {operation} outside the declared subresource range.");
    }

    internal void ValidatePassAccess(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        SubResourceRange requiredRange,
        string operation,
        ResourceState? currentState = null)
    {
        List<ResourceUse> uses = RequireDeclaredResourceUses(handle, passIndex, kind, operation, out int resourceIndex, out Resource resource);

        bool declared = false;
        foreach (var use in uses)
        {
            if (use.ResourceIndex != resourceIndex)
                continue;

            declared = true;
            if (!AccessSatisfies(use.Access, requiredAccess))
                continue;
            if (kind == ResourceKind.Texture && !RangeSatisfies(resource, use.Range, requiredRange))
                continue;

            bool stateMatches = currentState.HasValue
                ? RuntimeStateMatches(currentState.Value, requiredState)
                    && (DeclaredStateMatches(use.EntryState, currentState.Value)
                        || DeclaredStateMatches(use.ExitState, currentState.Value))
                : DeclaredStateMatches(use.EntryState, requiredState);
            if (stateMatches)
            {
                return;
            }
        }

        if (!declared)
            throw new InvalidOperationException(UndeclaredResourceUseMessage(passIndex, resource, operation));

        throw new InvalidOperationException(
            $"RenderGraph pass '{_passes[passIndex].Name}' used resource '{resource.Name}' during {operation} with required access/state {requiredAccess} {requiredState}, but setup declared an incompatible contract.");
    }

    internal void ValidatePassTransition(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        ResourceState before,
        ResourceState after,
        RenderGraphAccess requiredAccess,
        string operation,
        ResourceState? currentState = null)
    {
        List<ResourceUse> uses = RequireDeclaredResourceUses(handle, passIndex, kind, operation, out int resourceIndex, out Resource resource);

        bool declared = false;
        foreach (var use in uses)
        {
            if (use.ResourceIndex != resourceIndex)
                continue;

            declared = true;
            if (currentState.HasValue && !RuntimeStateMatches(currentState.Value, before))
                continue;
            if (AccessSatisfies(use.Access, requiredAccess)
                && DeclaredStateMatches(use.EntryState, before)
                && DeclaredStateMatches(use.ExitState, after))
            {
                return;
            }
        }

        if (!declared)
            throw new InvalidOperationException(UndeclaredResourceUseMessage(passIndex, resource, operation));

        throw new InvalidOperationException(
            $"RenderGraph pass '{_passes[passIndex].Name}' used resource '{resource.Name}' during {operation} with undeclared transition {before}->{after}.");
    }

    private List<ResourceUse> RequireDeclaredResourceUses(
        RenderGraphHandle handle,
        int passIndex,
        ResourceKind kind,
        string operation,
        out int resourceIndex,
        out Resource resource)
    {
        CompiledGraph compile = CurrentCompile();
        if (passIndex < 0 || passIndex >= _passes.Count || passIndex >= compile.Uses.Length)
            throw new InvalidOperationException($"{operation} requires an active RenderGraph pass.");

        resourceIndex = GetResourceIndex(handle, operation);
        resource = _resources[resourceIndex];
        if (resource.Kind != kind)
        {
            string expected = kind == ResourceKind.Texture ? "texture" : "buffer";
            throw new InvalidOperationException($"RenderGraph resource '{resource.Name}' is not a {expected}.");
        }

        return compile.Uses[passIndex];
    }

    private string UndeclaredResourceUseMessage(int passIndex, Resource resource, string operation)
        => $"RenderGraph pass '{_passes[passIndex].Name}' accessed resource '{resource.Name}' during {operation}, but did not declare it in setup.";

    private static bool AccessSatisfies(RenderGraphAccess declaredAccess, RenderGraphAccess requiredAccess)
        => (!Reads(requiredAccess) || Reads(declaredAccess))
            && (!Writes(requiredAccess) || Writes(declaredAccess));

    private static bool DeclaredStateMatches(ResourceState declaredState, ResourceState requiredState)
        => declaredState == requiredState;

    private static bool RuntimeStateMatches(ResourceState actualState, ResourceState expectedState)
        => actualState == expectedState;

    private static bool RangeSatisfies(Resource resource, SubResourceRange declaredRange, SubResourceRange requiredRange)
    {
        TextureDesc desc = resource.TextureDesc
            ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
        SomeEngine.Rhi.SubresourceRange declared = ActualRange(desc, declaredRange);
        SomeEngine.Rhi.SubresourceRange required = ActualRange(desc, requiredRange);
        return declared.FirstMip <= required.FirstMip
            && declared.FirstMip + declared.MipCount >= required.FirstMip + required.MipCount
            && declared.FirstSlice <= required.FirstSlice
            && declared.FirstSlice + declared.SliceCount >= required.FirstSlice + required.SliceCount;
    }

    internal void ValidateTextureView(
        int passIndex,
        TextureViewHandle view,
        ViewKind requiredKind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation)
    {
        if (!_textureViewBindings.TryGetValue(view, out RegisteredTextureView registered)
            && !ResolvePendingImportedTextureView(view))
        {
            if (!TryValidateImportedTextureView(passIndex, view, requiredKind, requiredState, requiredAccess, operation))
            {
                throw new InvalidOperationException(
                    $"RenderGraph pass '{_passes[passIndex].Name}' used texture view '{view}' during {operation} that is not registered to the active graph.");
                }
            return;
        }

        if (!_textureViewBindings.TryGetValue(view, out registered))
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used texture view '{view}' during {operation} that could not be resolved after validation.");

        if (registered.Desc.Kind != requiredKind)
        {
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used texture view '{view}' during {operation} with kind {registered.Desc.Kind}, expected {requiredKind}.");
        }

        ValidatePassAccess(
            new RenderGraphHandle(registered.ResourceIndex, _graphId, _generation),
            passIndex,
            ResourceKind.Texture,
            requiredState,
            requiredAccess,
            new SubResourceRange(
                registered.Desc.FirstMip,
                registered.Desc.MipCount,
                registered.Desc.FirstSlice,
                registered.Desc.SliceCount),
            operation);
    }

    internal RenderGraphHandle ValidateBufferView(
        int passIndex,
        BufferViewHandle view,
        ViewKind requiredKind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation)
    {
        if (!_bufferViewBindings.TryGetValue(view, out RegisteredBufferView registered)
            && !ResolvePendingImportedBufferView(view))
        {
            if (!TryValidateImportedBufferView(passIndex, view, requiredKind, requiredState, requiredAccess, operation, out RenderGraphHandle importedHandle))
            {
                throw new InvalidOperationException(
                    $"RenderGraph pass '{_passes[passIndex].Name}' used buffer view '{view}' during {operation} that is not registered to the active graph.");
            }
            return importedHandle;
        }

        if (!_bufferViewBindings.TryGetValue(view, out registered))
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used buffer view '{view}' during {operation} that could not be resolved after validation.");

        if (registered.Desc.Kind != requiredKind)
        {
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used buffer view '{view}' during {operation} with kind {registered.Desc.Kind}, expected {requiredKind}.");
        }

        RenderGraphHandle handle = new(registered.ResourceIndex, _graphId, _generation);
        ValidatePassAccess(
            handle,
            passIndex,
            ResourceKind.Buffer,
            requiredState,
            requiredAccess,
            operation);
        return handle;
    }

    private bool TryValidateImportedTextureView(
        int passIndex,
        TextureViewHandle view,
        ViewKind requiredKind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation)
    {
        if (_lastDevice == null || !_lastDevice.TryGetTextureViewOwner(view, out TextureHandle texture, out TextureViewDesc desc))
            return false;
        if (desc.Kind != requiredKind)
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used texture view '{view}' during {operation} with kind {desc.Kind}, expected {requiredKind}.");

        if (!FindImportedTextureResource(texture, out int resourceIndex))
            return false;

        ValidatePassAccess(
            new RenderGraphHandle(resourceIndex, _graphId, _generation),
            passIndex,
            ResourceKind.Texture,
            requiredState,
            requiredAccess,
            new SubResourceRange(desc.FirstMip, desc.MipCount, desc.FirstSlice, desc.SliceCount),
            operation);
        TrackExternalTextureView(view);
        return true;
    }

    private bool TryValidateImportedBufferView(
        int passIndex,
        BufferViewHandle view,
        ViewKind requiredKind,
        ResourceState requiredState,
        RenderGraphAccess requiredAccess,
        string operation,
        out RenderGraphHandle handle)
    {
        handle = default;
        if (_lastDevice == null || !_lastDevice.TryGetBufferViewOwner(view, out BufferHandle buffer, out BufferViewDesc desc))
            return false;
        if (desc.Kind != requiredKind)
            throw new InvalidOperationException(
                $"RenderGraph pass '{_passes[passIndex].Name}' used buffer view '{view}' during {operation} with kind {desc.Kind}, expected {requiredKind}.");

        if (!FindImportedBufferResource(buffer, out int resourceIndex))
            return false;

        handle = new RenderGraphHandle(resourceIndex, _graphId, _generation);
        ValidatePassAccess(
            handle,
            passIndex,
            ResourceKind.Buffer,
            requiredState,
            requiredAccess,
            operation);
        TrackExternalBufferView(view);
        return true;
    }

    private bool FindImportedTextureResource(TextureHandle texture, out int resourceIndex)
    {
        for (resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.Imported && resource.Kind == ResourceKind.Texture && resource.Texture == texture)
                return true;
        }

        resourceIndex = -1;
        return false;
    }

    private bool FindImportedBufferResource(BufferHandle buffer, out int resourceIndex)
    {
        for (resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            Resource resource = _resources[resourceIndex];
            if (resource.Imported
                && !resource.AllowDuplicateImportHandle
                && resource.Kind == ResourceKind.Buffer
                && resource.Buffer == buffer)
            {
                return true;
            }
        }

        resourceIndex = -1;
        return false;
    }

    private CompiledGraph CurrentCompile()
        => _activeCompile ?? throw new InvalidOperationException("RenderGraph does not have an active compile.");

    private CompiledGraph Declarations()
    {
        if (_declarationScratchDirty)
            ResetDeclarationScratch();

        _declarations ??= new CompiledGraph(0, 0);
        _declarations.EnsureShape(_passes.Count, _resources.Count);
        return _declarations;
    }

    private void EnsureDeclarationScratch()
    {
        if (_activeCompile != null)
            return;

        if (_declarationsFrozen)
        {
            RebuildDeclarations();
            return;
        }

        if (_declarationScratchDirty)
            ResetDeclarationScratch();
    }

    private void ResetDeclarationScratch()
    {
        ResetDeclarations();
        _schemaState.Reset();
        _declarationScratchDirty = false;
    }

    private void ResetDeclarations()
    {
        _declarationsFrozen = false;
        if (_declarations == null)
            return;

        _declarations.ClearDeclarations();
    }

    private void SetupPass(int passIndex, Pass pass)
    {
        var declarations = Declarations();
        int setupToken = unchecked(++_nextSetupToken);
        if (setupToken == 0)
            setupToken = unchecked(++_nextSetupToken);
        _activeCompile = declarations;
        _setupPassIndex = passIndex;
        _activeSetupToken = setupToken;
        Volatile.Write(ref _activeCallbackPassIndex, passIndex);
        Volatile.Write(ref _activeCallbackIsSetup, 1);
        Interlocked.Increment(ref _activeCallbackDepth);
        try
        {
            pass.Setup(new RenderGraphBuilder(this, passIndex, setupToken));
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeCallbackDepth) == 0)
            {
                Volatile.Write(ref _activeCallbackPassIndex, -1);
                Volatile.Write(ref _activeCallbackIsSetup, 0);
            }
            _activeCompile = null;
            _setupPassIndex = -1;
            _activeSetupToken = -1;
        }
    }

    private void RemoveLastPass(int passIndex)
    {
        if (passIndex != _passes.Count - 1)
            throw new InvalidOperationException("RenderGraph can only roll back the pass currently being added.");

        _passes.RemoveAt(passIndex);
    }

    private void RebuildDeclarations()
    {
        ResetDeclarationScratch();
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
            _schemaState.SetResource(resourceIndex, _resources[resourceIndex]);

        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            Pass pass = _passes[passIndex];
            pass.SideEffect = false;
            _schemaState.AddPass(passIndex, pass.Mode);
            SetupPass(passIndex, pass);
        }

        for (int exportIndex = 0; exportIndex < _textureExports.Count; exportIndex++)
            _schemaState.AddTextureExport(_textureExports[exportIndex].ResourceIndex);
        for (int exportIndex = 0; exportIndex < _bufferExports.Count; exportIndex++)
            _schemaState.AddBufferExport(_bufferExports[exportIndex].ResourceIndex);
        for (int stateIndex = 0; stateIndex < _finalStates.Count; stateIndex++)
            _schemaState.AddFinalState(_finalStates[stateIndex].ResourceIndex, _finalStates[stateIndex].State);
    }
}
