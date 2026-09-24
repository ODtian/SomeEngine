using System.Runtime.InteropServices;
using SharpGen.Runtime;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Rhi;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SomeEngine.Rhi.D3D12;

internal sealed partial class D3D12Device : IDevice, IMemoryDevice, ICacheDevice, IRtDevice, IQueryDevice, ID3D12DeviceInterop
{
    private readonly IDXGIFactory6 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D12Device _device;
    private readonly D3D12Queue _graphicsQueue;
    private readonly D3D12Queue _computeQueue;
    private readonly D3D12Queue _copyQueue;
    private readonly Vortice.Direct3D12.Debug.ID3D12InfoQueue? _infoQueue;
    private readonly ID3D12Fence _uploadFence;
    private readonly List<CommandBufferRecord> _pendingCommandBufferRetirements = [];
    private readonly System.Threading.Lock _activeListGate = new();
    private readonly FlatDictionary<BufferHandle, int> _activeListBuffers = new();
    private readonly FlatDictionary<TextureHandle, int> _activeListTextures = new();
    private readonly FlatDictionary<QueryPoolHandle, int> _activeListQueryPools = new();
    private readonly FlatDictionary<TextureViewHandle, int> _activeListTextureViews = new();
    private readonly FlatDictionary<BufferViewHandle, int> _activeListBufferViews = new();
    private readonly FlatDictionary<SamplerHandle, int> _activeListSamplers = new();
    private readonly FlatDictionary<AccelerationStructureHandle, int> _activeListAccelerationStructures = new();
    private readonly FlatDictionary<BindingLayoutHandle, int> _activeListBindingLayouts = new();
    private readonly FlatDictionary<BindingSetHandle, int> _activeListBindingSets = new();
    private readonly FlatDictionary<PipelineLayoutHandle, int> _activeListPipelineLayouts = new();
    private readonly FlatDictionary<PipelineHandle, int> _activeListPipelines = new();
    private readonly Dictionary<ulong, List<RootSignatureRecord>> _rootSignatures = [];
    private int _aliasingHeapResourceCount;
    private ulong _uploadFenceValue;
    private bool _disposed;

    internal readonly HandleStore<BufferHandle, BufferRecord> Buffers = new((id, gen) => new BufferHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<TextureHandle, TextureRecord> Textures = new((id, gen) => new TextureHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<TextureViewHandle, TextureViewRecord> TextureViews = new((id, gen) => new TextureViewHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BufferViewHandle, BufferViewRecord> BufferViews = new((id, gen) => new BufferViewHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<SamplerHandle, SamplerRecord> Samplers = new((id, gen) => new SamplerHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<ShaderModuleHandle, ShaderRecord> Shaders = new((id, gen) => new ShaderModuleHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BindingLayoutHandle, LayoutRecord> BindingLayouts = new((id, gen) => new BindingLayoutHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineLayoutHandle, PipeLayoutRecord> PipelineLayouts = new((id, gen) => new PipelineLayoutHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BindingSetHandle, BindingSetRecord> BindingSets = new((id, gen) => new BindingSetHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineHandle, PipelineRecord> Pipelines = new((id, gen) => new PipelineHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineCacheHandle, PipelineCacheRecord> PipelineCaches = new((id, gen) => new PipelineCacheHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<AccelerationStructureHandle, AccelRecord> AccelerationStructures = new((id, gen) => new AccelerationStructureHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<CommandBufferHandle, CommandBufferRecord> CommandBuffers = new((id, gen) => new CommandBufferHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<FenceHandle, FenceRecord> Fences = new((id, gen) => new FenceHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<QueryPoolHandle, QueryPoolRecord> QueryPools = new((id, gen) => new QueryPoolHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<SwapchainHandle, SwapchainRecord> Swapchains = new((id, gen) => new SwapchainHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<MemoryHeapHandle, HeapRecord> MemoryHeaps = new((id, gen) => new MemoryHeapHandle(id, gen), h => h.Id, h => h.Generation);

    internal readonly CpuDescriptorPool CbvSrvUavDescriptors;
    internal readonly CpuDescriptorPool SamplerDescriptors;
    internal readonly CpuDescriptorPool RtvDescriptors;
    internal readonly CpuDescriptorPool DsvDescriptors;
    internal readonly ShaderDescriptorPool ShaderResourceDescriptors;
    internal readonly ShaderDescriptorPool ShaderSamplerDescriptors;
    internal readonly ID3D12DescriptorHeap[] ShaderResourceHeapList;
    internal readonly ID3D12DescriptorHeap[] ShaderSamplerHeapList;
    internal readonly ID3D12DescriptorHeap[] ShaderResourceAndSamplerHeapList;
    private ID3D12CommandSignature? _drawIndirectSignature;
    private ID3D12CommandSignature? _drawIndexedIndirectSignature;
    private ID3D12CommandSignature? _dispatchIndirectSignature;
    private ID3D12CommandSignature? _meshDispatchIndirectSignature;

    public D3D12Device(
        IDXGIFactory6 factory,
        IDXGIAdapter1 adapter,
        ID3D12Device device,
        AdapterInfo adapterInfo,
        bool validationEnabled,
        D3D12Policy policy)
    {
        _factory = factory;
        _adapter = adapter;
        _device = device;
        Policy = policy;
        AdapterInfo = adapterInfo;
        ValidationEnabled = validationEnabled;
        CbvSrvUavDescriptors = new CpuDescriptorPool(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, policy.CpuDescriptorCapacity);
        SamplerDescriptors = new CpuDescriptorPool(device, DescriptorHeapType.Sampler, policy.CpuDescriptorCapacity);
        RtvDescriptors = new CpuDescriptorPool(device, DescriptorHeapType.RenderTargetView, policy.CpuDescriptorCapacity);
        DsvDescriptors = new CpuDescriptorPool(device, DescriptorHeapType.DepthStencilView, policy.CpuDescriptorCapacity);
        ShaderResourceDescriptors = new ShaderDescriptorPool(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, policy.ShaderResourceDescriptorCapacity);
        ShaderSamplerDescriptors = new ShaderDescriptorPool(device, DescriptorHeapType.Sampler, policy.ShaderSamplerDescriptorCapacity);
        ShaderResourceHeapList = [ShaderResourceDescriptors.Heap];
        ShaderSamplerHeapList = [ShaderSamplerDescriptors.Heap];
        ShaderResourceAndSamplerHeapList = [ShaderResourceDescriptors.Heap, ShaderSamplerDescriptors.Heap];
        _infoQueue = device.QueryInterfaceOrNull<Vortice.Direct3D12.Debug.ID3D12InfoQueue>();
        if (policy.PrecreateIndirectCommandSignatures)
        {
            _drawIndirectSignature = CreateCommandSignature(IndirectArgumentType.Draw, IndirectArgumentSize.Draw);
            _drawIndexedIndirectSignature = CreateCommandSignature(IndirectArgumentType.DrawIndexed, IndirectArgumentSize.DrawIndexed);
            _dispatchIndirectSignature = CreateCommandSignature(IndirectArgumentType.Dispatch, IndirectArgumentSize.Dispatch);
            _meshDispatchIndirectSignature = CreateCommandSignature(IndirectArgumentType.DispatchMesh, IndirectArgumentSize.Dispatch);
        }

        _graphicsQueue = new D3D12Queue(this, QueueType.Graphics, CommandQueueKind.Direct, device.CreateCommandQueue(CommandListType.Direct, CommandQueuePriority.Normal, CommandQueueFlags.None, 0));
        _computeQueue = new D3D12Queue(this, QueueType.Compute, CommandQueueKind.Compute, device.CreateCommandQueue(CommandListType.Compute, CommandQueuePriority.Normal, CommandQueueFlags.None, 0));
        _copyQueue = new D3D12Queue(this, QueueType.Copy, CommandQueueKind.Copy, device.CreateCommandQueue(CommandListType.Copy, CommandQueuePriority.Normal, CommandQueueFlags.None, 0));
        _uploadFence = device.CreateFence(0, FenceFlags.None);

        Features = CreateFeatures();
        Limits = CreateLimits();
        QueueFamilies =
        [
            new QueueFamilyInfo { Type = QueueType.Graphics, Count = 1, SupportsGraphics = true, SupportsCompute = true, SupportsCopy = true, SupportsTimestamps = true },
            new QueueFamilyInfo { Type = QueueType.Compute, Count = 1, SupportsCompute = true, SupportsCopy = true, SupportsTimestamps = true },
            new QueueFamilyInfo { Type = QueueType.Copy, Count = 1, SupportsCopy = true, SupportsTimestamps = false },
        ];
    }

    public AdapterInfo AdapterInfo { get; }
    public DeviceFeatures Features { get; }
    public DeviceLimits Limits { get; }
    public IReadOnlyList<QueueFamilyInfo> QueueFamilies { get; }

    public ID3D12Device NativeDevice => _device;
    public IDXGIFactory6 Factory => _factory;
    public IDXGIAdapter1 Adapter => _adapter;
    internal D3D12Policy Policy { get; }
    internal bool ValidationEnabled { get; }

    internal ID3D12CommandSignature DrawIndirectSignature
        => _drawIndirectSignature ??= CreateCommandSignature(IndirectArgumentType.Draw, IndirectArgumentSize.Draw);

    internal ID3D12CommandSignature DrawIndexedIndirectSignature
        => _drawIndexedIndirectSignature ??= CreateCommandSignature(IndirectArgumentType.DrawIndexed, IndirectArgumentSize.DrawIndexed);

    internal ID3D12CommandSignature DispatchIndirectSignature
        => _dispatchIndirectSignature ??= CreateCommandSignature(IndirectArgumentType.Dispatch, IndirectArgumentSize.Dispatch);

    internal ID3D12CommandSignature MeshDispatchIndirectSignature
        => _meshDispatchIndirectSignature ??= CreateCommandSignature(IndirectArgumentType.DispatchMesh, IndirectArgumentSize.Dispatch);

    public T? Get<T>() where T : class
    {
        if (this is T self)
            return self;
        if (_device is T nativeDevice)
            return nativeDevice;
        if (_factory is T factory)
            return factory;
        if (_adapter is T adapter)
            return adapter;
        return null;
    }

    public IQueue GetQueue(QueueType type, uint index = 0)
    {
        ThrowIfDisposed();
        if (index != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 backend exposes one queue per queue type in v0.");
        return type switch
        {
            QueueType.Graphics => _graphicsQueue,
            QueueType.Compute => _computeQueue,
            QueueType.Copy => _copyQueue,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Queue type {type} is not defined."),
        };
    }

    internal D3D12Queue GetQueue(CommandQueueKind kind)
        => kind switch
        {
            CommandQueueKind.Direct => _graphicsQueue,
            CommandQueueKind.Compute => _computeQueue,
            CommandQueueKind.Copy => _copyQueue,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Queue kind {kind} is not defined."),
        };

    public ResourceMemoryRequirements GetBufferReqs(BufferDesc desc)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        var info = _device.GetResourceAllocationInfo([D3D12Mappings.BufferResourceDesc(desc)]);
        return new ResourceMemoryRequirements(info.SizeInBytes, info.Alignment, MemoryHeapKind.Buffer, false, false);
    }

    public ResourceMemoryRequirements GetTextureReqs(TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTextureFormat(desc);
        var info = _device.GetResourceAllocationInfo([D3D12Mappings.TextureResourceDesc(desc)]);
        var kind = desc.BindFlags.HasFlag(BindFlags.RenderTarget) || desc.BindFlags.HasFlag(BindFlags.DepthStencil)
            ? MemoryHeapKind.RenderTargetOrDepthStencil
            : MemoryHeapKind.Texture;
        return new ResourceMemoryRequirements(info.SizeInBytes, info.Alignment, kind, false, false);
    }

    public MemoryHeapHandle CreateMemoryHeap(MemoryHeapDesc desc)
    {
        ThrowIfDisposed();
        Validation.MemoryHeapDesc(desc, Features);
        var heapDesc = new HeapDescription(desc.SizeInBytes, D3D12Mappings.ToHeapType(desc.Memory), Limits.MinMemoryHeapAlignment, D3D12Mappings.ToHeapFlags(desc.Kind));
        var heap = _device.CreateHeap<ID3D12Heap>(heapDesc);
        return MemoryHeaps.Add(new HeapRecord(desc with { }, heap));
    }

    public MemoryHeapDesc GetHeapDesc(MemoryHeapHandle heap)
    {
        ThrowIfDisposed();
        return MemoryHeaps.Get(heap, "MemoryHeap").Desc;
    }

    public MemoryBudget GetMemoryBudget(MemoryClass memory)
    {
        ThrowIfDisposed();
        using var adapter3 = _adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
        if (adapter3 == null)
            return new MemoryBudget(memory, AdapterInfo.DedicatedVideoMemory + AdapterInfo.SharedSystemMemory, 0, IsExact: false);
        var group = memory == MemoryClass.DeviceLocal ? MemorySegmentGroup.Local : MemorySegmentGroup.NonLocal;
        var info = adapter3.QueryVideoMemoryInfo(0, group);
        return new MemoryBudget(memory, info.Budget, info.CurrentUsage, IsExact: true);
    }

    public BufferHandle CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        if (!initialData.IsEmpty && (ulong)initialData.Length > desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Initial buffer data exceeds buffer size.");
        ResourceState creationState = !initialData.IsEmpty && desc.Memory == MemoryClass.DeviceLocal
            ? ResourceState.CopyDestination
            : desc.InitialState;
        var resource = _device.CreateCommittedResource(
            new HeapProperties(D3D12Mappings.ToHeapType(desc.Memory)),
            HeapFlags.None,
            D3D12Mappings.BufferResourceDesc(desc),
            InitialBufferState(desc, creationState),
            null);
        var requirements = GetBufferReqs(desc);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Committed, desc.Memory, MemoryHeapHandle.Invalid, 0, requirements.SizeInBytes);
        var storedDesc = desc with { };
        var record = new BufferRecord(storedDesc, resource, allocation) { State = creationState };
        if (storedDesc.Memory == MemoryClass.CpuUpload)
            EnsureUploadMap(record);
        var handle = Buffers.Add(record);
        if (!initialData.IsEmpty)
            InitializeBuffer(handle, initialData, desc.InitialState);
        return handle;
    }

    public TextureHandle CreateTexture(TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTextureFormat(desc);
        var resource = _device.CreateCommittedResource(
            new HeapProperties(D3D12Mappings.ToHeapType(desc.Memory)),
            HeapFlags.None,
            D3D12Mappings.TextureResourceDesc(desc),
            D3D12Mappings.ToState(desc.InitialState),
            D3D12Mappings.ToClearValue(desc.OptimizedClearValue));
        var requirements = GetTextureReqs(desc);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Committed, desc.Memory, MemoryHeapHandle.Invalid, 0, requirements.SizeInBytes);
        var handle = Textures.Add(new TextureRecord(desc with { }, resource, allocation));
        return handle;
    }

    public BufferHandle ImportBuffer(ExternalBufferDesc desc)
    {
        ThrowIfDisposed();
        if (desc.Resource == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external buffer import requires a native resource.");
        var storedDesc = desc.Desc with { Name = string.IsNullOrWhiteSpace(desc.Name) ? desc.Desc.Name : desc.Name };
        Validation.BufferDesc(storedDesc);
        var nativeDesc = desc.Resource.Description;
        if (nativeDesc.Dimension != Vortice.Direct3D12.ResourceDimension.Buffer)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external buffer import requires a buffer resource.");
        if (nativeDesc.Width != storedDesc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external buffer width must match BufferDesc.SizeInBytes.");
        if (nativeDesc.Layout != TextureLayout.RowMajor)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external buffer layout must be RowMajor.");
        ValidateExternalHeap(desc.Resource, storedDesc.Memory, "D3D12 external buffer");
        ValidateExternalFlags(nativeDesc.Flags, storedDesc.BindFlags, "D3D12 external buffer");
        var allocation = new ResourceAllocationInfo(ResourceOwnership.External, storedDesc.Memory, MemoryHeapHandle.Invalid, 0, storedDesc.SizeInBytes);
        return Buffers.Add(new BufferRecord(storedDesc, desc.Resource, allocation));
    }

    public TextureHandle ImportTexture(ExternalTextureDesc desc)
    {
        ThrowIfDisposed();
        if (desc.Resource == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture import requires a native resource.");
        var storedDesc = desc.Desc with { Name = string.IsNullOrWhiteSpace(desc.Name) ? desc.Desc.Name : desc.Name };
        Validation.TextureDesc(storedDesc);
        ValidateTextureFormat(storedDesc);
        var nativeDesc = desc.Resource.Description;
        if (nativeDesc.Dimension == Vortice.Direct3D12.ResourceDimension.Buffer)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture import requires a texture resource.");
        if (nativeDesc.Width != storedDesc.Width ||
            nativeDesc.Height != storedDesc.Height ||
            nativeDesc.MipLevels != storedDesc.MipLevels ||
            nativeDesc.Format != D3D12Mappings.ToDxgi(storedDesc.Format))
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture resource description does not match TextureDesc.");
        }
        if (nativeDesc.SampleDescription.Count != storedDesc.SampleCount || nativeDesc.SampleDescription.Quality != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture sample description does not match TextureDesc.");
        if (nativeDesc.Layout != TextureLayout.Unknown)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture layout must be Unknown.");
        ValidateExternalHeap(desc.Resource, storedDesc.Memory, "D3D12 external texture");
        ValidateExternalFlags(nativeDesc.Flags, storedDesc.BindFlags, "D3D12 external texture");

        var expectedDepthOrArray = storedDesc.Dimension == SomeEngine.Rhi.ResourceDimension.Texture3D
            ? storedDesc.Depth
            : storedDesc.ArraySize;
        if (nativeDesc.DepthOrArraySize != expectedDepthOrArray)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 external texture depth/array size does not match TextureDesc.");
        var requirements = GetTextureReqs(storedDesc);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.External, storedDesc.Memory, MemoryHeapHandle.Invalid, 0, requirements.SizeInBytes);
        return Textures.Add(new TextureRecord(storedDesc, desc.Resource, allocation));
    }

    public ID3D12Resource GetNativeBuffer(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").Resource;
    }

    public ID3D12Resource GetNativeTexture(TextureHandle texture)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").Resource;
    }

    public ID3D12Resource GetNativeAcceleration(AccelerationStructureHandle accelerationStructure)
    {
        ThrowIfDisposed();
        return AccelerationStructures.Get(accelerationStructure, "AccelerationStructure").Resource;
    }

    public BufferHandle CreatePlacedBuffer(MemoryHeapHandle heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        if (!initialData.IsEmpty && (ulong)initialData.Length > desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Initial buffer data exceeds buffer size.");
        var requirements = GetBufferReqs(desc);
        ValidatePlacement(heap, offset, requirements, desc.Memory);
        ResourceState creationState = !initialData.IsEmpty && desc.Memory == MemoryClass.DeviceLocal
            ? ResourceState.CopyDestination
            : desc.InitialState;
        var heapRecord = MemoryHeaps.Get(heap, "MemoryHeap");
        if (!initialData.IsEmpty && !WouldBeActive(heapRecord, offset, requirements.SizeInBytes))
            throw new RhiException(ErrorCode.ValidationFailure, "Initial data cannot be uploaded to an inactive placed buffer; issue an aliasing barrier before copying data.");
        var resource = _device.CreatePlacedResource<ID3D12Resource>(
            heapRecord.Heap,
            offset,
            D3D12Mappings.BufferResourceDesc(desc),
            InitialBufferState(desc, creationState),
            null);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Placed, desc.Memory, heap, offset, requirements.SizeInBytes);
        var handle = Buffers.Add(new BufferRecord(desc with { }, resource, allocation) { State = creationState });
        RegisterPlacedAllocation(AliasingResource.BufferResource(handle), allocation);
        if (!initialData.IsEmpty)
            InitializeBuffer(handle, initialData, desc.InitialState);
        return handle;
    }

    public TextureHandle CreatePlacedTexture(MemoryHeapHandle heap, ulong offset, TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTextureFormat(desc);
        var requirements = GetTextureReqs(desc);
        ValidatePlacement(heap, offset, requirements, desc.Memory);
        var heapRecord = MemoryHeaps.Get(heap, "MemoryHeap");
        var resource = _device.CreatePlacedResource<ID3D12Resource>(
            heapRecord.Heap,
            offset,
            D3D12Mappings.TextureResourceDesc(desc),
            D3D12Mappings.ToState(desc.InitialState),
            D3D12Mappings.ToClearValue(desc.OptimizedClearValue));
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Placed, desc.Memory, heap, offset, requirements.SizeInBytes);
        var handle = Textures.Add(new TextureRecord(desc with { }, resource, allocation));
        RegisterPlacedAllocation(AliasingResource.TextureResource(handle), allocation);
        return handle;
    }

    public TextureViewHandle CreateTextureView(TextureHandle texture, TextureViewDesc desc)
    {
        ThrowIfDisposed();
        var textureRecord = Textures.Get(texture, "Texture");
        Validation.TextureViewDesc(textureRecord.Desc, desc);
        var stored = desc with { Format = desc.Format == Format.Unknown ? textureRecord.Desc.Format : desc.Format };
        var allocation = stored.Kind switch
        {
            ViewKind.RenderTarget => RtvDescriptors.Allocate(),
            ViewKind.DepthStencil => DsvDescriptors.Allocate(),
            ViewKind.ShaderResource or ViewKind.UnorderedAccess => CbvSrvUavDescriptors.Allocate(),
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view kind {stored.Kind} is not valid for textures."),
        };
        DescriptorAllocation? readOnlyDepthAllocation = null;
        try
        {
            CreateTextureDescriptor(textureRecord, stored, allocation.Cpu);
            CpuDescriptorHandle readOnlyDepthDescriptor = default;
            if (stored.Kind == ViewKind.DepthStencil)
            {
                var readOnly = DsvDescriptors.Allocate();
                readOnlyDepthAllocation = readOnly;
                readOnlyDepthDescriptor = readOnly.Cpu;
                CreateTextureDescriptor(textureRecord, stored, readOnly.Cpu, DepthStencilViewFlags.ReadOnlyDepth);
            }

            var handle = TextureViews.Add(new TextureViewRecord(texture, stored, allocation.Cpu, allocation, stored.Kind, textureRecord.Allocation.Ownership, readOnlyDepthDescriptor, readOnlyDepthAllocation));
            return handle;
        }
        catch
        {
            if (readOnlyDepthAllocation is { } readOnly)
                DsvDescriptors.Free(readOnly);
            FreeTextureDescriptor(stored.Kind, allocation);
            throw;
        }
    }

    public BufferViewHandle CreateBufferView(BufferHandle buffer, BufferViewDesc desc)
    {
        ThrowIfDisposed();
        var bufferRecord = Buffers.Get(buffer, "Buffer");
        Validation.BufferViewDesc(bufferRecord.Desc, desc);
        var allocation = CbvSrvUavDescriptors.Allocate();
        try
        {
            CreateBufferDescriptor(bufferRecord, desc, allocation.Cpu);
            var handle = BufferViews.Add(new BufferViewRecord(buffer, desc with { }, allocation.Cpu, allocation, desc.Kind));
            return handle;
        }
        catch
        {
            CbvSrvUavDescriptors.Free(allocation);
            throw;
        }
    }

    public SamplerHandle CreateSampler(SamplerDesc desc)
    {
        ThrowIfDisposed();
        Validation.SamplerDesc(desc);
        var allocation = SamplerDescriptors.Allocate();
        var native = CreateSamplerDescription(desc);
        _device.CreateSampler(ref native, allocation.Cpu);
        var handle = Samplers.Add(new SamplerRecord(desc with { }, allocation.Cpu, allocation));
        return handle;
    }

    private static SamplerDescription CreateSamplerDescription(SamplerDesc desc)
    {
        var border = desc.BorderColor switch
        {
            BorderColor.TransparentBlack => new Color4(0, 0, 0, 0),
            BorderColor.OpaqueBlack => new Color4(0, 0, 0, 1),
            BorderColor.OpaqueWhite => new Color4(1, 1, 1, 1),
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Border color {desc.BorderColor} is not defined."),
        };
        return new SamplerDescription(
            D3D12Mappings.ToFilter(desc),
            D3D12Mappings.ToAddressMode(desc.AddressU),
            D3D12Mappings.ToAddressMode(desc.AddressV),
            D3D12Mappings.ToAddressMode(desc.AddressW),
            desc.MipLodBias,
            desc.MaxAnisotropy,
            D3D12Mappings.ToComparison(desc.Compare),
            in border,
            desc.MinLod,
            desc.MaxLod);
    }

    public ShaderModuleHandle CreateShaderModule(ShaderModuleDesc desc)
    {
        ThrowIfDisposed();
        Validation.ShaderModuleDesc(desc);
        if (desc.Backend != Backend.D3D12 || desc.BytecodeFormat != ShaderBytecodeFormat.Dxil)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 shader modules require Backend=D3D12 and BytecodeFormat=Dxil.");
        var handle = Shaders.Add(new ShaderRecord(desc with { Bytecode = desc.Bytecode.ToArray() }));
        return handle;
    }

    public BindingLayoutHandle CreateBindingLayout(BindingLayoutDesc desc)
    {
        ThrowIfDisposed();
        Validation.BindingLayoutDesc(desc, Limits, Features);
        var stored = desc with { Slots = desc.Slots.ToArray() };
        BuildBindingSlots(
            stored,
            out var validationSlots,
            out var validationSlotLookup,
            out var requiredBindingResourceCount,
            out var supportsTransientBindings,
            out var resourceSlots,
            out var resourceSlotLookup,
            out var resourceDescriptorCount,
            out var resourceSlotsNeedNullDescriptors,
            out var dynamicResourceSlots,
            out var dynamicResourceSlotLookup,
            out var dynamicResourceDescriptorCount,
            out var dynamicResourceSlotsNeedNullDescriptors,
            out var samplerSlots,
            out var samplerSlotLookup,
            out var samplerDescriptorCount,
            out var samplerSlotsNeedNullDescriptors,
            out var signature);
        var handle = BindingLayouts.Add(new LayoutRecord(
            stored,
            signature,
            validationSlots,
            validationSlotLookup,
            requiredBindingResourceCount,
            resourceSlots,
            resourceSlotLookup,
            resourceDescriptorCount,
            resourceSlotsNeedNullDescriptors,
            dynamicResourceSlots,
            dynamicResourceSlotLookup,
            dynamicResourceDescriptorCount,
            dynamicResourceSlotsNeedNullDescriptors,
            samplerSlots,
            samplerSlotLookup,
            samplerDescriptorCount,
            samplerSlotsNeedNullDescriptors,
            supportsTransientBindings,
            dynamicResourceSlots.Length != 0));
        return handle;
    }

    public BindingLayoutHandle CreateBindingLayout(ReadOnlySpan<BindingSlotDesc> slots, string name = "")
        => CreateBindingLayout(new BindingLayoutDesc { Name = name, Slots = slots.ToArray() });

    public PipelineLayoutHandle CreatePipelineLayout(PipelineLayoutDesc desc)
    {
        ThrowIfDisposed();
        Validation.PipelineLayoutDesc(desc, Limits);
        var stored = desc with
        {
            BindingLayouts = desc.BindingLayouts.ToArray(),
            PushConstants = desc.PushConstants.ToArray(),
            StaticSamplers = desc.StaticSamplers.ToArray(),
        };
        var fingerprint = ComputeRootHash(stored);
        var root = CreateRootSignature(stored, out var setRootBindings, out var setSignatures, out var pushConstantRootBindings);
        var sharedRoot = RetainRootSignature(stored, root, fingerprint);
        var handle = PipelineLayouts.Add(new PipeLayoutRecord(
            stored,
            sharedRoot.RootSignature,
            sharedRoot,
            fingerprint,
            setRootBindings,
            setSignatures,
            pushConstantRootBindings));
        return handle;
    }

    public PipelineLayoutHandle CreatePipelineLayout(
        ReadOnlySpan<BindingLayoutHandle> bindingLayouts,
        ReadOnlySpan<PushRangeDesc> pushConstants = default,
        ReadOnlySpan<StaticSamplerDesc> staticSamplers = default,
        string name = "")
        => CreatePipelineLayout(
            new PipelineLayoutDesc
            {
                Name = name,
                BindingLayouts = bindingLayouts.ToArray(),
                PushConstants = pushConstants.ToArray(),
                StaticSamplers = staticSamplers.ToArray(),
            });

    private RootSignatureRecord RetainRootSignature(
        PipelineLayoutDesc desc,
        ID3D12RootSignature rootSignature,
        ulong fingerprint)
    {
        BuildRootSignatureSlots(
            desc,
            out var resourceSlots,
            out var dynamicResourceSlots,
            out var samplerSlots);

        var pushConstants = desc.PushConstants.ToArray();
        var staticSamplers = desc.StaticSamplers.ToArray();
        if (_rootSignatures.TryGetValue(fingerprint, out var candidates))
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!RootSignatureMatches(candidate, resourceSlots, dynamicResourceSlots, samplerSlots, pushConstants, staticSamplers))
                    continue;

                candidate.RefCount++;
                rootSignature.Dispose();
                return candidate;
            }
        }
        else
        {
            candidates = [];
            _rootSignatures.Add(fingerprint, candidates);
        }

        var record = new RootSignatureRecord(
            rootSignature,
            fingerprint,
            resourceSlots,
            dynamicResourceSlots,
            samplerSlots,
            pushConstants,
            staticSamplers);
        candidates.Add(record);
        return record;
    }

    private void ReleaseRootSignature(RootSignatureRecord record)
    {
        record.RefCount--;
        if (record.RefCount > 0)
            return;

        if (_rootSignatures.TryGetValue(record.Fingerprint, out var candidates))
        {
            candidates.Remove(record);
            if (candidates.Count == 0)
                _rootSignatures.Remove(record.Fingerprint);
        }

        record.RootSignature.Dispose();
    }

    private void BuildRootSignatureSlots(
        PipelineLayoutDesc desc,
        out SlotLayout[][] resourceSlots,
        out SlotLayout[][] dynamicResourceSlots,
        out SlotLayout[][] samplerSlots)
    {
        int setCount = desc.BindingLayouts.Count;
        resourceSlots = new SlotLayout[setCount][];
        dynamicResourceSlots = new SlotLayout[setCount][];
        samplerSlots = new SlotLayout[setCount][];
        for (int set = 0; set < setCount; set++)
        {
            var layout = BindingLayouts.Get(desc.BindingLayouts[set], "PipelineBindingLayout");
            resourceSlots[set] = layout.ResourceSlots;
            dynamicResourceSlots[set] = layout.DynamicResourceSlots;
            samplerSlots[set] = layout.SamplerSlots;
        }
    }

    private static bool RootSignatureMatches(
        RootSignatureRecord record,
        SlotLayout[][] resourceSlots,
        SlotLayout[][] dynamicResourceSlots,
        SlotLayout[][] samplerSlots,
        ReadOnlySpan<PushRangeDesc> pushConstants,
        ReadOnlySpan<StaticSamplerDesc> staticSamplers)
        => SlotLayoutGroupsMatch(record.ResourceSlots, resourceSlots)
            && SlotLayoutGroupsMatch(record.DynamicResourceSlots, dynamicResourceSlots)
            && SlotLayoutGroupsMatch(record.SamplerSlots, samplerSlots)
            && PushConstantsMatch(record.PushConstants, pushConstants)
            && StaticSamplersMatch(record.StaticSamplers, staticSamplers);

    private static bool SlotLayoutGroupsMatch(SlotLayout[][] left, SlotLayout[][] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int set = 0; set < left.Length; set++)
        {
            if (!SlotLayoutsMatch(left[set], right[set]))
                return false;
        }

        return true;
    }

    private static bool SlotLayoutsMatch(ReadOnlySpan<SlotLayout> left, ReadOnlySpan<SlotLayout> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; index++)
        {
            var leftSlot = left[index];
            var rightSlot = right[index];
            if (leftSlot.Binding != rightSlot.Binding
                || leftSlot.Type != rightSlot.Type
                || leftSlot.Count != rightSlot.Count
                || leftSlot.BaseDescriptor != rightSlot.BaseDescriptor)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PushConstantsMatch(ReadOnlySpan<PushRangeDesc> left, ReadOnlySpan<PushRangeDesc> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; index++)
        {
            if (left[index].Stages != right[index].Stages
                || left[index].Offset != right[index].Offset
                || left[index].SizeInBytes != right[index].SizeInBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static bool StaticSamplersMatch(ReadOnlySpan<StaticSamplerDesc> left, ReadOnlySpan<StaticSamplerDesc> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; index++)
        {
            var leftSampler = left[index];
            var rightSampler = right[index];
            if (leftSampler.Set != rightSampler.Set
                || leftSampler.Binding != rightSampler.Binding
                || leftSampler.Stages != rightSampler.Stages
                || !SamplerDescMatches(leftSampler.Sampler, rightSampler.Sampler))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SamplerDescMatches(SamplerDesc left, SamplerDesc right)
        => left.MinFilter == right.MinFilter
            && left.MagFilter == right.MagFilter
            && left.MipmapMode == right.MipmapMode
            && left.AddressU == right.AddressU
            && left.AddressV == right.AddressV
            && left.AddressW == right.AddressW
            && left.MipLodBias.Equals(right.MipLodBias)
            && left.MinLod.Equals(right.MinLod)
            && left.MaxLod.Equals(right.MaxLod)
            && left.MaxAnisotropy == right.MaxAnisotropy
            && left.Compare == right.Compare
            && left.BorderColor == right.BorderColor;

    public BindingSetHandle CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc)
        => CreateBindingSet(
            layout,
            desc.Resources,
            desc.Name,
            desc.Flags);

    public BindingSetHandle CreateBindingSet(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        string name = "",
        BindingSetFlags flags = BindingSetFlags.None)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resources);
        var layoutRecord = BindingLayouts.Get(layout, "BindingLayout");
        var storedDesc = new BindingSetDesc
        {
            Name = name,
            Flags = flags,
            Resources = resources,
        };
        Validation.BindingSetDesc(storedDesc);
        ValidateBindingResources(layoutRecord, resources);
        ShaderDescriptorAllocation? resourceDescriptors = null;
        ShaderDescriptorAllocation? dynamicResourceDescriptors = null;
        ShaderDescriptorAllocation? samplerDescriptors = null;
        try
        {
            resourceDescriptors = CreateBindingDescriptors(layoutRecord.ResourceSlots, layoutRecord.ResourceSlotLookup, layoutRecord.ResourceDescriptorCount, layoutRecord.ResourceSlotsNeedNullDescriptors, resources, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            dynamicResourceDescriptors = CreateBindingDescriptors(layoutRecord.DynamicResourceSlots, layoutRecord.DynamicResourceSlotLookup, layoutRecord.DynamicResourceDescriptorCount, layoutRecord.DynamicResourceSlotsNeedNullDescriptors, resources, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            samplerDescriptors = CreateBindingDescriptors(layoutRecord.SamplerSlots, layoutRecord.SamplerSlotLookup, layoutRecord.SamplerDescriptorCount, layoutRecord.SamplerSlotsNeedNullDescriptors, resources, DescriptorHeapType.Sampler);
            CollectBindMeta(
                resources,
                out var bindStates,
                out var buffers,
                out var textures,
                out var accelerationStructures);
            var handle = BindingSets.Add(new BindingSetRecord(
                layout,
                layoutRecord.Signature,
                layoutRecord.HasDynamicResourceSlots,
                storedDesc.Flags,
                resourceDescriptors,
                dynamicResourceDescriptors,
                samplerDescriptors,
                resources,
                bindStates,
                buffers,
                textures,
                accelerationStructures));
            return handle;
        }
        catch
        {
            FreeShaderDescriptor(resourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            FreeShaderDescriptor(dynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            FreeShaderDescriptor(samplerDescriptors, DescriptorHeapType.Sampler);
            throw;
        }
    }

    public void UpdateBindingSet(BindingSetHandle bindingSet, ReadOnlySpan<BindingResourceDesc> resources)
    {
        ThrowIfDisposed();
        var record = BindingSets.Get(bindingSet, "BindingSet");
        if (!record.Flags.HasFlag(BindingSetFlags.Mutable))
            throw new RhiException(ErrorCode.ValidationFailure, "UpdateBindingSet requires the binding set to be created with BindingSetFlags.Mutable.");
        CheckSetFree(bindingSet);
        var layoutRecord = BindingLayouts.Get(record.Layout, "BindingLayout");
        RhiBindingValidation.ValidateUpdates(layoutRecord.Desc, resources);
        var merged = RhiBindingValidation.MergeResources(record.Resources, resources);
        ValidateBindingResources(layoutRecord, merged);
        CollectBindMeta(
            merged,
            out var bindStates,
            out var buffers,
            out var textures,
            out var accelerationStructures);
        WriteDescUpdates(layoutRecord.ResourceSlots, resources, record.ResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        WriteDescUpdates(layoutRecord.DynamicResourceSlots, resources, record.DynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        WriteDescUpdates(layoutRecord.SamplerSlots, resources, record.SamplerDescriptors, DescriptorHeapType.Sampler);
        BindingSets.Set(
            bindingSet,
            new BindingSetRecord(
                record.Layout,
                record.LayoutSignature,
                record.HasDynamicResourceSlots,
                record.Flags,
                record.ResourceDescriptors,
                record.DynamicResourceDescriptors,
                record.SamplerDescriptors,
                merged,
                bindStates,
                buffers,
                textures,
                accelerationStructures),
            "BindingSet");
    }

    public PipelineCacheHandle CreatePipelineCache(PipelineCacheDesc desc)
    {
        ThrowIfDisposed();
        Validation.PipelineCacheDesc(desc);
        var initialData = desc.InitialData.ToArray();
        var storedDesc = desc with { InitialData = initialData };
        var handle = PipelineCaches.Add(CreatePipelineLib(storedDesc, initialData));
        return handle;
    }

    public byte[] GetPipelineData(PipelineCacheHandle cache)
    {
        ThrowIfDisposed();
        var record = PipelineCaches.Get(cache, "PipelineCache");
        return SerializePipelineLibrary(record.Library);
    }

    public PipelineHandle CreateComputePipeline(ComputePipelineDesc desc)
    {
        ThrowIfDisposed();
        var layout = PipelineLayouts.Get(desc.Layout, "PipelineLayout");
        var shader = Shaders.Get(desc.ComputeShader, "ShaderModule");
        if (shader.Desc.Stage != ShaderStage.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pipeline requires a compute shader.");
        var nativeDesc = new ComputePipelineStateDescription
        {
            RootSignature = layout.RootSignature,
            ComputeShader = shader.Desc.Bytecode,
        };
        var state = CreateComputeState(desc, nativeDesc, shader);
        var handle = Pipelines.Add(new PipelineRecord(PipelineKind.Compute, desc.Layout, layout, state));
        return handle;
    }

    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDesc desc)
    {
        ThrowIfDisposed();
        var storedDesc = Snapshot(desc);
        ValidateGraphicsDesc(storedDesc);
        var layout = PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        var vertex = Shaders.Get(storedDesc.VertexShader, "VertexShader");
        var pixel = Shaders.Get(storedDesc.PixelShader, "PixelShader");
        var hull = storedDesc.HullShader.IsValid ? Shaders.Get(storedDesc.HullShader, "HullShader") : null;
        var domain = storedDesc.DomainShader.IsValid ? Shaders.Get(storedDesc.DomainShader, "DomainShader") : null;
        var geometry = storedDesc.GeometryShader.IsValid ? Shaders.Get(storedDesc.GeometryShader, "GeometryShader") : null;
        if (vertex.Desc.Stage != ShaderStage.Vertex || pixel.Desc.Stage != ShaderStage.Pixel)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline requires vertex and pixel shaders.");
        var nativeDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = layout.RootSignature,
            VertexShader = vertex.Desc.Bytecode,
            PixelShader = pixel.Desc.Bytecode,
            HullShader = hull?.Desc.Bytecode ?? default,
            DomainShader = domain?.Desc.Bytecode ?? default,
            GeometryShader = geometry?.Desc.Bytecode ?? default,
            SampleMask = storedDesc.Multisample.SampleMask,
            RasterizerState = CreateRasterizer(storedDesc.Rasterizer),
            DepthStencilState = CreateDepthStencil(storedDesc.DepthStencil),
            BlendState = CreateBlend(storedDesc.Blend),
            PrimitiveTopologyType = D3D12Mappings.ToTopologyType(storedDesc.Topology),
            RenderTargetFormats = storedDesc.ColorFormats.Select(D3D12Mappings.ToDxgi).ToArray(),
            DepthStencilFormat = D3D12Mappings.ToDxgi(storedDesc.DepthStencilFormat),
            SampleDescription = new SampleDescription(storedDesc.SampleCount, 0),
            InputLayout = new InputLayoutDescription(CreateInputElements(storedDesc)),
        };
        var state = CreateGraphicsState(storedDesc, nativeDesc, vertex, pixel, hull, domain, geometry);
        var handle = Pipelines.Add(new PipelineRecord(
            PipelineKind.Graphics,
            storedDesc.Layout,
            layout,
            state,
            storedDesc.Topology,
            storedDesc,
            RenderPassCompatibility: CreatePassCompatibility(storedDesc)));
        return handle;
    }

    public PipelineHandle CreateGraphicsPipeline(
        GraphicsPipelineDesc desc,
        ReadOnlySpan<VertexLayoutDesc> vertexBuffers,
        ReadOnlySpan<VertexAttributeDesc> vertexAttributes,
        ReadOnlySpan<Format> colorFormats,
        ReadOnlySpan<BlendTargetDesc> blendTargets)
        => CreateGraphicsPipeline(
            desc with
            {
                VertexBuffers = vertexBuffers.ToArray(),
                VertexAttributes = vertexAttributes.ToArray(),
                ColorFormats = colorFormats.ToArray(),
                Blend = desc.Blend with { Targets = blendTargets.ToArray() },
            });

    public PipelineHandle CreateMeshPipeline(MeshPipelineDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.MeshShader)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Mesh pipelines require mesh shader support.");
        var storedDesc = Snapshot(desc);
        ValidateMeshDesc(storedDesc);
        var layout = PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        var mesh = Shaders.Get(storedDesc.MeshShader, "MeshShader");
        var amplification = storedDesc.AmplificationShader.IsValid ? Shaders.Get(storedDesc.AmplificationShader, "AmplificationShader") : null;
        var pixel = storedDesc.PixelShader.IsValid ? Shaders.Get(storedDesc.PixelShader, "PixelShader") : null;
        if (mesh.Desc.Stage != ShaderStage.Mesh)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mesh pipeline mesh shader handle does not reference a mesh shader.");
        if (amplification != null && amplification.Desc.Stage != ShaderStage.Amplification)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mesh pipeline amplification shader handle does not reference an amplification shader.");
        if (pixel != null && pixel.Desc.Stage != ShaderStage.Pixel)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mesh pipeline pixel shader handle does not reference a pixel shader.");
        var state = CreateMeshState(storedDesc, layout, amplification, mesh, pixel);
        var handle = Pipelines.Add(new PipelineRecord(
            PipelineKind.Mesh,
            storedDesc.Layout,
            layout,
            state,
            storedDesc.Topology,
            MeshDesc: storedDesc,
            RenderPassCompatibility: CreatePassCompatibility(storedDesc)));
        return handle;
    }

    public PipelineHandle CreateRtPipeline(RtPipelineDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing pipelines require ray tracing support.");
        var storedDesc = Snapshot(desc);
        Validation.RtPipelineDesc(storedDesc, Limits);
        var layout = PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        ValidateRtShaders(storedDesc);
        using var device5 = RequireRtDevice();
        var state = CreateRtState(device5, layout, storedDesc);
        ID3D12StateObjectProperties properties;
        try
        {
            properties = state.QueryInterface<ID3D12StateObjectProperties>();
        }
        catch
        {
            state.Dispose();
            throw;
        }

        var handle = Pipelines.Add(new PipelineRecord(PipelineKind.RayTracing, storedDesc.Layout, layout, StateObject: state, StateObjectProperties: properties, RayTracingDesc: storedDesc));
        return handle;
    }

    private static PipelinePassCompatibility CreatePassCompatibility(GraphicsPipelineDesc desc)
        => new(
            ColorFormatAt(desc.ColorFormats, 0),
            ColorFormatAt(desc.ColorFormats, 1),
            ColorFormatAt(desc.ColorFormats, 2),
            ColorFormatAt(desc.ColorFormats, 3),
            ColorFormatAt(desc.ColorFormats, 4),
            ColorFormatAt(desc.ColorFormats, 5),
            ColorFormatAt(desc.ColorFormats, 6),
            ColorFormatAt(desc.ColorFormats, 7),
            desc.ColorFormats.Count,
            desc.DepthStencilFormat,
            desc.SampleCount,
            desc.DepthStencil.DepthWriteEnable);

    private static PipelinePassCompatibility CreatePassCompatibility(MeshPipelineDesc desc)
        => new(
            ColorFormatAt(desc.ColorFormats, 0),
            ColorFormatAt(desc.ColorFormats, 1),
            ColorFormatAt(desc.ColorFormats, 2),
            ColorFormatAt(desc.ColorFormats, 3),
            ColorFormatAt(desc.ColorFormats, 4),
            ColorFormatAt(desc.ColorFormats, 5),
            ColorFormatAt(desc.ColorFormats, 6),
            ColorFormatAt(desc.ColorFormats, 7),
            desc.ColorFormats.Count,
            desc.DepthStencilFormat,
            desc.SampleCount,
            desc.DepthStencil.DepthWriteEnable);

    private static Format ColorFormatAt(IReadOnlyList<Format> formats, int index)
        => index < formats.Count ? formats[index] : Format.Unknown;

    public int GetRtSize(PipelineHandle pipeline)
    {
        ThrowIfDisposed();
        var record = Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader identifiers require a ray tracing pipeline.");
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing shader identifiers require ray tracing support.");
        return checked((int)Limits.RayTracingShaderIdentifierSizeInBytes);
    }

    public unsafe void GetRtId(PipelineHandle pipeline, string shaderGroupName, Span<byte> destination)
    {
        ThrowIfDisposed();
        var record = Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing || record.StateObjectProperties == null || record.RayTracingDesc == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader identifiers require a ray tracing pipeline.");
        if (string.IsNullOrWhiteSpace(shaderGroupName))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader group name must be specified.");
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing shader identifiers require ray tracing support.");
        int size = checked((int)Limits.RayTracingShaderIdentifierSizeInBytes);
        if (destination.Length < size)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader identifier destination must be at least {size} bytes.");
        bool found = false;
        foreach (var group in record.RayTracingDesc.ShaderGroups)
        {
            if (group.Name == shaderGroupName)
            {
                found = true;
                break;
            }
        }
        if (!found)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group '{shaderGroupName}' does not exist in the pipeline.");

        var identifier = record.StateObjectProperties.GetShaderIdentifier(shaderGroupName);
        if (identifier == IntPtr.Zero)
            throw new RhiException(ErrorCode.BackendFailure, $"D3D12 returned a null shader identifier for group '{shaderGroupName}'.");
        new ReadOnlySpan<byte>((void*)identifier, size).CopyTo(destination);
    }

    public AccelBuildSizes GetAccelSizes(AccelBuildDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure builds require ray tracing support.");
        Validation.AccelBuildDesc(desc, requireResourceHandles: false);
        var inputs = CreateRtInputs(desc, requireResourceHandles: false);
        using var device5 = RequireRtDevice();
        var prebuild = device5.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        return new AccelBuildSizes(
            AlignUp(prebuild.ResultDataMaxSizeInBytes, Limits.AccelerationStructureAlignment),
            AlignUp(prebuild.ScratchDataSizeInBytes, Limits.AccelerationStructureAlignment),
            AlignUp(prebuild.UpdateScratchDataSizeInBytes, Limits.AccelerationStructureAlignment));
    }

    public AccelerationStructureHandle CreateAccelerationStructure(AccelerationStructureDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structures require ray tracing support.");
        Validation.AccelerationStructureDesc(desc);
        if (desc.SizeInBytes % Limits.AccelerationStructureAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure size must be aligned to {Limits.AccelerationStructureAlignment} bytes on D3D12.");

        var storedDesc = desc with { };
        var resource = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            D3D12Mappings.BufferResourceDesc(new BufferDesc
            {
                SizeInBytes = storedDesc.SizeInBytes,
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
            }),
            ResourceStates.RaytracingAccelerationStructure,
            null);
        var allocation = CbvSrvUavDescriptors.Allocate();
        _device.CreateShaderResourceView(null, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = 5768,
            RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = resource.GPUVirtualAddress },
        }, allocation.Cpu);
        return AccelerationStructures.Add(new AccelRecord(storedDesc, resource, allocation.Cpu, allocation));
    }

    public ICommandList CreateCommandList(CommandListDesc desc)
    {
        ThrowIfDisposed();
        Validation.CommandListDesc(desc);
        var list = new D3D12CommandList(this, desc);
        return list;
    }

    public FenceHandle CreateFence(string name, ulong initialValue = 0)
    {
        ThrowIfDisposed();
        return Fences.Add(new FenceRecord(_device.CreateFence(initialValue, FenceFlags.None), initialValue));
    }

    public QueryPoolHandle CreateQueryPool(QueryPoolDesc desc)
    {
        ThrowIfDisposed();
        Validation.QueryPoolDesc(desc);
        QueryHeapType heapType = ToQueryHeap(desc);
        var heap = _device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(heapType, desc.Count));
        return QueryPools.Add(new QueryPoolRecord(desc with { }, heap));
    }

    public SwapchainHandle CreateSwapchain(SwapchainDesc desc)
    {
        ThrowIfDisposed();
        Validation.SwapchainDesc(desc);
        if (desc.NativeWindowHandle == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 swapchain requires a valid HWND.");
        var swapchain = D3D12Swapchain.Create(this, desc);
        return Swapchains.Add(swapchain);
    }

    public ISwapchain GetSwapchain(SwapchainHandle handle)
    {
        ThrowIfDisposed();
        Swapchains.Get(handle, "Swapchain");
        return new D3D12Swapchain(this, handle);
    }

    public BufferDesc GetBufferDesc(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").Desc;
    }

    public TextureDesc GetTextureDesc(TextureHandle texture)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").Desc;
    }

    public TextureViewDesc GetTextureViewDesc(TextureViewHandle view)
    {
        ThrowIfDisposed();
        return TextureViews.Get(view, "TextureView").Desc;
    }

    public BufferViewDesc GetBufferViewDesc(BufferViewHandle view)
    {
        ThrowIfDisposed();
        return BufferViews.Get(view, "BufferView").Desc;
    }

    public bool TryGetTextureViewOwner(TextureViewHandle view, out TextureHandle texture, out TextureViewDesc desc)
    {
        ThrowIfDisposed();
        if (!TextureViews.IsAlive(view))
        {
            texture = default;
            desc = default!;
            return false;
        }

        TextureViewRecord record = TextureViews.Get(view, "TextureView");
        texture = record.Texture;
        desc = record.Desc;
        return true;
    }

    public bool TryGetBufferViewOwner(BufferViewHandle view, out BufferHandle buffer, out BufferViewDesc desc)
    {
        ThrowIfDisposed();
        if (!BufferViews.IsAlive(view))
        {
            buffer = default;
            desc = default!;
            return false;
        }

        BufferViewRecord record = BufferViews.Get(view, "BufferView");
        buffer = record.Buffer;
        desc = record.Desc;
        return true;
    }

    public ResourceAllocationInfo GetBufferAlloc(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").Allocation;
    }

    public ResourceAllocationInfo GetTextureAlloc(TextureHandle texture)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").Allocation;
    }

    public ResourceState GetBufferState(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").State;
    }

    public ResourceState GetTextureState(TextureHandle texture, uint mipLevel = 0, uint arraySlice = 0)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").GetState(mipLevel, arraySlice);
    }

    public FormatSupport GetFormatSupport(Format format) => GetFormatCapabilities(format).Support;

    public FormatCapabilities GetFormatCapabilities(Format format)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(format) || format == Format.Unknown)
            return default;
        var data = new FeatureDataFormatSupport { Format = D3D12Mappings.ToDxgi(format) };
        if (!_device.CheckFeatureSupport(Vortice.Direct3D12.Feature.FormatSupport, ref data))
            return default;
        FormatSupport support = FormatSupport.None;
        if (data.Support1 != FormatSupport1.None)
            support |= FormatSupport.CopySource | FormatSupport.CopyDestination;
        if (data.Support1.HasFlag(FormatSupport1.InputAssemblerVertexBuffer))
            support |= FormatSupport.VertexAttribute;
        if (data.Support1.HasFlag(FormatSupport1.RenderTarget))
            support |= FormatSupport.RenderTarget;
        if (data.Support1.HasFlag(FormatSupport1.DepthStencil))
            support |= FormatSupport.DepthStencil;
        if (data.Support1.HasFlag(FormatSupport1.TypedUnorderedAccessView) ||
            data.Support2.HasFlag(FormatSupport2.UnorderedAccessViewTypedLoad) ||
            data.Support2.HasFlag(FormatSupport2.UnorderedAccessViewTypedStore))
            support |= FormatSupport.UnorderedAccess;
        if (data.Support1.HasFlag(FormatSupport1.ShaderLoad) || data.Support1.HasFlag(FormatSupport1.ShaderSample))
            support |= FormatSupport.ShaderSample;
        if (Validation.IsDepthFormat(format) && SupportsDepthSrv(format))
            support |= FormatSupport.ShaderSample;
        if (data.Support1.HasFlag(FormatSupport1.Display))
            support |= FormatSupport.Present;
        return new FormatCapabilities(
            support,
            GetSampleCounts(D3D12Mappings.ToDxgi(format), support),
            data.Support2.HasFlag(FormatSupport2.UnorderedAccessViewTypedLoad),
            data.Support2.HasFlag(FormatSupport2.UnorderedAccessViewTypedStore),
            data.Support1.HasFlag(FormatSupport1.ShaderSample),
            data.Support1.HasFlag(FormatSupport1.Blendable),
            data.Support1.HasFlag(FormatSupport1.DepthStencil));
    }

    private bool SupportsDepthSrv(Format format)
    {
        var data = new FeatureDataFormatSupport { Format = D3D12Mappings.ToSrvDxgi(format) };
        return _device.CheckFeatureSupport(Vortice.Direct3D12.Feature.FormatSupport, ref data)
            && (data.Support1.HasFlag(FormatSupport1.ShaderLoad) || data.Support1.HasFlag(FormatSupport1.ShaderSample));
    }

    public ulong GetFenceValue(FenceHandle fence)
    {
        ThrowIfDisposed();
        return Fences.Get(fence, "Fence").Fence.CompletedValue;
    }

    public void WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue)
    {
        ThrowIfDisposed();
        var record = Fences.Get(fence, "Fence");
        if (value > record.LastSignaledValue)
            throw new RhiException(ErrorCode.ValidationFailure, $"Fence wait value {value} has not been signaled. Last signaled value is {record.LastSignaledValue}.");
        WaitNativeFence(record.Fence, value, timeoutNanoseconds);
        RetireCommandBuffers();
    }

    public unsafe Memory<byte> MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(buffer, "Buffer");
        if (record.MappedMemory != null || record.PersistentMapActive)
            throw new RhiException(ErrorCode.ValidationFailure, "Buffer is already mapped.");
        if (mode != MapMode.Write && mode != MapMode.Read)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Map mode value {mode} is not defined.");
        if (mode == MapMode.Write && record.Desc.Memory != MemoryClass.CpuUpload)
            throw new RhiException(ErrorCode.ValidationFailure, "Write mapping requires CpuUpload memory.");
        if (mode == MapMode.Read && record.Desc.Memory != MemoryClass.CpuReadback)
            throw new RhiException(ErrorCode.ValidationFailure, "Read mapping requires CpuReadback memory.");
        ulong mappedSize = sizeInBytes < 0 ? record.Desc.SizeInBytes - offset : checked((ulong)sizeInBytes);
        if (offset > record.Desc.SizeInBytes || mappedSize == 0 || mappedSize > record.Desc.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mapped range is outside the buffer.");
        if (mappedSize > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 mapped ranges are limited to 2GB.");
        if (record.PersistentMappedMemory != null)
        {
            record.PersistentMapActive = true;
            record.MappedMode = mode;
            record.MappedOffset = offset;
            record.MappedSize = mappedSize;
            return record.PersistentMappedMemory.Memory.Slice(checked((int)offset), checked((int)mappedSize));
        }

        byte* pointer = record.Resource.Map<byte>(0) + offset;
        record.MappedMemory = new MappedMemory(pointer, checked((int)mappedSize));
        record.MappedMode = mode;
        record.MappedOffset = offset;
        record.MappedSize = mappedSize;
        return record.MappedMemory.Memory;
    }

    public void FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)
    {
        ThrowIfDisposed();
        ValidateMappedRange(buffer, offset, sizeInBytes, "FlushBufferRange");
    }

    public void InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)
    {
        ThrowIfDisposed();
        ValidateMappedRange(buffer, offset, sizeInBytes, "InvalidateBufferRange");
    }

    public void UnmapBuffer(BufferHandle buffer)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(buffer, "Buffer");
        if (record.MappedMemory == null && !record.PersistentMapActive)
            throw new RhiException(ErrorCode.ValidationFailure, "Buffer is not mapped.");
        if (record.PersistentMapActive)
        {
            record.PersistentMapActive = false;
            record.MappedOffset = 0;
            record.MappedSize = 0;
            return;
        }

        Vortice.Direct3D12.Range? writtenRange = record.MappedMode == MapMode.Write
            ? new Vortice.Direct3D12.Range((nuint)record.MappedOffset, (nuint)(record.MappedOffset + record.MappedSize))
            : new Vortice.Direct3D12.Range(0, 0);
        MappedMemory mapped = record.MappedMemory
            ?? throw new RhiException(ErrorCode.ValidationFailure, "Buffer is not mapped.");
        mapped.Close();
        record.MappedMemory = null;
        if (record.PersistentMappedMemory == null)
            record.Resource.Unmap(0, writtenRange);
    }

    public void Destroy(BufferHandle handle) => DestroyBuffer(handle);
    public void Destroy(TextureHandle handle) => DestroyTexture(handle);
    public void Destroy(TextureViewHandle handle) => DestroyTextureView(handle);
    public void Destroy(BufferViewHandle handle) => DestroyBufferView(handle);
    public void Destroy(SamplerHandle handle) => DestroySampler(handle);
    public void Destroy(ShaderModuleHandle handle) => DestroyShaderModule(handle);
    public void Destroy(BindingLayoutHandle handle) => DestroyBindingLayout(handle);
    public void Destroy(PipelineLayoutHandle handle) => DestroyPipelineLayout(handle);
    public void Destroy(BindingSetHandle handle) => DestroyBindingSet(handle);
    public void Destroy(PipelineHandle handle) => DestroyPipeline(handle);
    public void Destroy(PipelineCacheHandle handle) => DestroyPipelineCache(handle);
    public void Destroy(AccelerationStructureHandle handle) => DestroyAccelerationStructure(handle);
    public void Destroy(CommandBufferHandle handle) => DestroyCommandBuffer(handle);
    public void Destroy(FenceHandle handle) => DestroyFence(handle);
    public void Destroy(QueryPoolHandle handle) => DestroyQueryPool(handle);
    public void Destroy(SwapchainHandle handle) => DestroySwapchain(handle);
    public void Destroy(MemoryHeapHandle handle) => DestroyMemoryHeap(handle);

    private void DestroyBuffer(BufferHandle handle)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(handle, "Buffer");
        if (record.Allocation.Ownership == ResourceOwnership.Swapchain)
            throw new RhiException(ErrorCode.ValidationFailure, "Swapchain buffers cannot be destroyed through DestroyBuffer.");
        CheckBufferViews(handle);
        CheckBufferFree(handle);
        ReleaseAllocation(record.Allocation, AliasingResource.BufferResource(handle));
        if (record.MappedMemory != null)
        {
            record.MappedMemory.Close();
            record.MappedMemory = null;
        }
        record.PersistentMapActive = false;

        if (record.PersistentMappedMemory != null)
        {
            record.PersistentMappedMemory.Close();
            record.PersistentMappedMemory = null;
            record.Resource.Unmap(0, null);
        }

        if (record.Allocation.Ownership != ResourceOwnership.External)
            record.Resource.Dispose();
        Buffers.Destroy(handle, "Buffer");
    }

    private void DestroyTexture(TextureHandle handle)
    {
        ThrowIfDisposed();
        var record = Textures.Get(handle, "Texture");
        if (record.Allocation.Ownership == ResourceOwnership.Swapchain)
            throw new RhiException(ErrorCode.ValidationFailure, "Swapchain textures cannot be destroyed through DestroyTexture.");
        CheckTextureViews(handle);
        CheckTextureFree(handle);
        ReleaseAllocation(record.Allocation, AliasingResource.TextureResource(handle));
        if (record.Allocation.Ownership != ResourceOwnership.External)
            record.Resource.Dispose();
        Textures.Destroy(handle, "Texture");
    }

    private void DestroyTextureView(TextureViewHandle handle)
    {
        ThrowIfDisposed();
        var record = TextureViews.Get(handle, "TextureView");
        if (record.Ownership == ResourceOwnership.Swapchain)
            throw new RhiException(ErrorCode.ValidationFailure, "Swapchain render-target views cannot be destroyed through DestroyTextureView.");
        CheckTvRefs(handle);
        CheckTvFree(handle);
        FreeTextureView(record);
        TextureViews.Destroy(handle, "TextureView");
    }

    private void DestroyBufferView(BufferViewHandle handle)
    {
        ThrowIfDisposed();
        var record = BufferViews.Get(handle, "BufferView");
        CheckBvRefs(handle);
        CheckBvFree(handle);
        CbvSrvUavDescriptors.Free(record.Allocation);
        BufferViews.Destroy(handle, "BufferView");
    }

    private void DestroySampler(SamplerHandle handle)
    {
        ThrowIfDisposed();
        var record = Samplers.Get(handle, "Sampler");
        CheckSamplerRefs(handle);
        CheckSamplerFree(handle);
        SamplerDescriptors.Free(record.Allocation);
        Samplers.Destroy(handle, "Sampler");
    }

    private void DestroyShaderModule(ShaderModuleHandle handle)
    {
        ThrowIfDisposed();
        Shaders.Destroy(handle, "ShaderModule");
    }

    private void DestroyBindingLayout(BindingLayoutHandle handle)
    {
        ThrowIfDisposed();
        BindingLayouts.Get(handle, "BindingLayout");
        CheckLayoutFree(handle);
        BindingLayouts.Destroy(handle, "BindingLayout");
    }

    private void DestroyPipelineLayout(PipelineLayoutHandle handle)
    {
        ThrowIfDisposed();
        var record = PipelineLayouts.Get(handle, "PipelineLayout");
        CheckPlFree(handle);
        ReleaseRootSignature(record.SharedRootSignature);
        PipelineLayouts.Destroy(handle, "PipelineLayout");
    }

    private void DestroyBindingSet(BindingSetHandle handle)
    {
        ThrowIfDisposed();
        var record = BindingSets.Get(handle, "BindingSet");
        CheckSetFree(handle);
        FreeShaderDescriptor(record.ResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        FreeShaderDescriptor(record.DynamicResourceDescriptors, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        FreeShaderDescriptor(record.SamplerDescriptors, DescriptorHeapType.Sampler);
        BindingSets.Destroy(handle, "BindingSet");
    }

    private void DestroyPipeline(PipelineHandle handle)
    {
        ThrowIfDisposed();
        var record = Pipelines.Get(handle, "Pipeline");
        CheckPipelineFree(handle);
        record.State?.Dispose();
        record.StateObjectProperties?.Dispose();
        record.StateObject?.Dispose();
        Pipelines.Destroy(handle, "Pipeline");
    }

    private void DestroyPipelineCache(PipelineCacheHandle handle)
    {
        ThrowIfDisposed();
        var record = PipelineCaches.Get(handle, "PipelineCache");
        PipelineCaches.Destroy(handle, "PipelineCache");
        record.Dispose();
    }

    private void DestroyAccelerationStructure(AccelerationStructureHandle handle)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structures require ray tracing support.");
        var record = AccelerationStructures.Get(handle, "AccelerationStructure");
        CheckAccelerationFree(handle);
        CheckAccelerationReferences(handle);
        CbvSrvUavDescriptors.Free(record.Allocation);
        record.Resource.Dispose();
        AccelerationStructures.Destroy(handle, "AccelerationStructure");
    }

    private void DestroyCommandBuffer(CommandBufferHandle handle)
    {
        ThrowIfDisposed();
        var record = CommandBuffers.Get(handle, "CommandBuffer");
        CommandBuffers.Destroy(handle, "CommandBuffer");
        if (record.Submitted && MayUseResources(record))
        {
            _pendingCommandBufferRetirements.Add(record);
            return;
        }

        ReleaseCommandStorage(record);
    }

    private void DestroyFence(FenceHandle handle)
    {
        ThrowIfDisposed();
        var record = Fences.Get(handle, "Fence");
        if (record.Fence.CompletedValue < record.LastSignaledValue)
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a D3D12 fence with an unfinished signaled value.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (ReferenceEquals(commandBuffer.RetirementFence, record.Fence))
            {
                commandBuffer.RetirementFence = null;
                commandBuffer.RetirementValue = 0;
            }
        }

        foreach (var commandBuffer in _pendingCommandBufferRetirements)
        {
            if (ReferenceEquals(commandBuffer.RetirementFence, record.Fence))
            {
                commandBuffer.RetirementFence = null;
                commandBuffer.RetirementValue = 0;
            }
        }
        RetireCommandBuffers();
        record.Fence.Dispose();
        Fences.Destroy(handle, "Fence");
    }

    private void DestroyQueryPool(QueryPoolHandle handle)
    {
        ThrowIfDisposed();
        var record = QueryPools.Get(handle, "QueryPool");
        CheckQueryFree(handle);
        record.Heap.Dispose();
        QueryPools.Destroy(handle, "QueryPool");
    }

    private void DestroySwapchain(SwapchainHandle handle)
    {
        ThrowIfDisposed();
        var record = Swapchains.Get(handle, "Swapchain");
        WaitIdle();
        for (int index = 0; index < record.RenderTargetViews.Length; index++)
        {
            CheckTvFree(record.RenderTargetViews[index]);
            CheckTextureFree(record.Textures[index]);
        }

        for (int index = 0; index < record.RenderTargetViews.Length; index++)
        {
            var view = TextureViews.Get(record.RenderTargetViews[index], "TextureView");
            RtvDescriptors.Free(view.Allocation);
            TextureViews.Destroy(record.RenderTargetViews[index], "TextureView");
            var texture = Textures.Get(record.Textures[index], "Texture");
            texture.Resource.Dispose();
            Textures.Destroy(record.Textures[index], "Texture");
        }

        D3D12Swapchain.LeaveFullscreen(record);
        record.Swapchain.Dispose();
        Swapchains.Destroy(handle, "Swapchain");
    }

    private void DestroyMemoryHeap(MemoryHeapHandle handle)
    {
        ThrowIfDisposed();
        var record = MemoryHeaps.Get(handle, "MemoryHeap");
        if (record.LiveResourceCount != 0)
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a memory heap while placed resources are still alive.");
        record.Heap.Dispose();
        MemoryHeaps.Destroy(handle, "MemoryHeap");
    }

    public void WaitIdle()
    {
        ThrowIfDisposed();
        _graphicsQueue.WaitIdle();
        _computeQueue.WaitIdle();
        _copyQueue.WaitIdle();
        RetireCommandBuffers();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        WaitIdle();
        RetireCommandBuffers();
        _disposed = true;
        CommandBuffers.Clear(record =>
        {
            FreeTrackedDescriptor(record.TransientDescriptors);
            record.Lease.Dispose();
        });
        Swapchains.Clear(static record =>
        {
            D3D12Swapchain.LeaveFullscreen(record);
            record.Swapchain.Dispose();
        });
        QueryPools.Clear(static record => record.Heap.Dispose());
        Fences.Clear(static record => record.Fence.Dispose());
        Pipelines.Clear(static record =>
        {
            record.State?.Dispose();
            record.StateObjectProperties?.Dispose();
            record.StateObject?.Dispose();
        });
        PipelineCaches.Clear(static record => record.Dispose());
        PipelineLayouts.Clear(record => ReleaseRootSignature(record.SharedRootSignature));
        _meshDispatchIndirectSignature?.Dispose();
        _dispatchIndirectSignature?.Dispose();
        _drawIndexedIndirectSignature?.Dispose();
        _drawIndirectSignature?.Dispose();
        TextureViews.Clear();
        BufferViews.Clear();
        Samplers.Clear();
        AccelerationStructures.Clear(static record =>
        {
            record.Resource.Dispose();
        });
        Textures.Clear(static record =>
        {
            if (record.Allocation.Ownership != ResourceOwnership.External)
                record.Resource.Dispose();
        });
        Buffers.Clear(static record =>
        {
            if (record.Allocation.Ownership != ResourceOwnership.External)
                record.Resource.Dispose();
        });
        MemoryHeaps.Clear(static record => record.Heap.Dispose());
        ShaderSamplerDescriptors.Dispose();
        ShaderResourceDescriptors.Dispose();
        DsvDescriptors.Dispose();
        RtvDescriptors.Dispose();
        SamplerDescriptors.Dispose();
        CbvSrvUavDescriptors.Dispose();
        _uploadFence.Dispose();
        _copyQueue.Dispose();
        _computeQueue.Dispose();
        _graphicsQueue.Dispose();
        _infoQueue?.Dispose();
        _device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal CommandBufferHandle AddCommandBuffer(CommandBufferRecord record) => CommandBuffers.Add(record);

    internal void Pin(BufferHandle buffer) => ActiveHandles.Retain(_activeListGate, _activeListBuffers, buffer);

    internal void Pin(ReadOnlySpan<BufferHandle> buffers) => ActiveHandles.Retain(_activeListGate, _activeListBuffers, buffers);

    internal void Pin(TextureHandle texture) => ActiveHandles.Retain(_activeListGate, _activeListTextures, texture);

    internal void Pin(ReadOnlySpan<TextureHandle> textures) => ActiveHandles.Retain(_activeListGate, _activeListTextures, textures);

    internal void Pin(QueryPoolHandle queryPool) => ActiveHandles.Retain(_activeListGate, _activeListQueryPools, queryPool);

    internal void Pin(TextureViewHandle view) => ActiveHandles.Retain(_activeListGate, _activeListTextureViews, view);

    internal void Pin(BufferViewHandle view) => ActiveHandles.Retain(_activeListGate, _activeListBufferViews, view);

    internal void Pin(SamplerHandle sampler) => ActiveHandles.Retain(_activeListGate, _activeListSamplers, sampler);

    internal void Pin(AccelerationStructureHandle accelerationStructure)
        => ActiveHandles.Retain(_activeListGate, _activeListAccelerationStructures, accelerationStructure);

    internal void Pin(ReadOnlySpan<AccelerationStructureHandle> accelerationStructures)
        => ActiveHandles.Retain(_activeListGate, _activeListAccelerationStructures, accelerationStructures);

    internal void Pin(BindingLayoutHandle bindingLayout) => ActiveHandles.Retain(_activeListGate, _activeListBindingLayouts, bindingLayout);

    internal void Pin(BindingSetHandle bindingSet)
        => ActiveHandles.Retain(_activeListGate, _activeListBindingSets, bindingSet);

    internal void Pin(PipelineLayoutHandle pipelineLayout) => ActiveHandles.Retain(_activeListGate, _activeListPipelineLayouts, pipelineLayout);

    internal void Pin(PipelineHandle pipeline) => ActiveHandles.Retain(_activeListGate, _activeListPipelines, pipeline);

    internal bool HasAliasingUseValidation => _aliasingHeapResourceCount != 0;

    internal void Unpin(ReadOnlySpan<BufferHandle> buffers)
        => ActiveHandles.Release(_activeListGate, _activeListBuffers, buffers, "D3D12", "buffer");

    internal void Unpin(ReadOnlySpan<TextureHandle> textures)
        => ActiveHandles.Release(_activeListGate, _activeListTextures, textures, "D3D12", "texture");

    internal void Unpin(ReadOnlySpan<QueryPoolHandle> queryPools)
        => ActiveHandles.Release(_activeListGate, _activeListQueryPools, queryPools, "D3D12", "query-pool");

    internal void Unpin(ReadOnlySpan<TextureViewHandle> views)
        => ActiveHandles.Release(_activeListGate, _activeListTextureViews, views, "D3D12", "texture-view");

    internal void Unpin(ReadOnlySpan<BufferViewHandle> views)
        => ActiveHandles.Release(_activeListGate, _activeListBufferViews, views, "D3D12", "buffer-view");

    internal void Unpin(ReadOnlySpan<SamplerHandle> samplers)
        => ActiveHandles.Release(_activeListGate, _activeListSamplers, samplers, "D3D12", "sampler");

    internal void Unpin(ReadOnlySpan<AccelerationStructureHandle> accelerationStructures)
        => ActiveHandles.Release(_activeListGate, _activeListAccelerationStructures, accelerationStructures, "D3D12", "acceleration-structure");

    internal void Unpin(ReadOnlySpan<BindingLayoutHandle> bindingLayouts)
        => ActiveHandles.Release(_activeListGate, _activeListBindingLayouts, bindingLayouts, "D3D12", "binding-layout");

    internal void Unpin(ReadOnlySpan<BindingSetHandle> bindingSets)
        => ActiveHandles.Release(_activeListGate, _activeListBindingSets, bindingSets, "D3D12", "binding-set");

    internal void Unpin(ReadOnlySpan<PipelineLayoutHandle> pipelineLayouts)
        => ActiveHandles.Release(_activeListGate, _activeListPipelineLayouts, pipelineLayouts, "D3D12", "pipeline-layout");

    internal void Unpin(ReadOnlySpan<PipelineHandle> pipelines)
        => ActiveHandles.Release(_activeListGate, _activeListPipelines, pipelines, "D3D12", "pipeline");

    internal void SetBufferState(BufferHandle handle, ResourceState state) => Buffers.Get(handle, "Buffer").State = state;

    internal void SetTextureState(TextureHandle handle, SubresourceRange range, ResourceState state) => Textures.Get(handle, "Texture").SetState(range, state);

    internal void ValidateTransientBindings(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        var layoutRecord = BindingLayouts.Get(layout, "BindingLayout");
        ValidateTransientBindings(layoutRecord, resources);
    }

    internal void ValidateTransientBindings(LayoutRecord layoutRecord, ReadOnlySpan<BindingResourceDesc> resources)
    {
        if (!ValidationEnabled)
        {
            return;
        }

        if (!layoutRecord.SupportsTransientBindings)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Transient SetBindings does not support partially bound or bindless slots; create a BindingSet and update it instead.");
        ValidateBindingResources(layoutRecord, resources);
    }

    internal void RequireBindStates(D3D12CommandList list, ReadOnlySpan<BindingResourceDesc> resources)
    {
        if (!ValidationEnabled)
        {
            foreach (var resource in resources)
                TrackBindRefs(list, resource);
            return;
        }

        foreach (var resource in resources)
        {
            var state = BindRules.Resolve(resource.ResourceType);
            switch (state.Target)
            {
                case BindTarget.BufferView:
                {
                    RequireBufferViewState(list, resource.BufferView, BindViewKind(state), state.State, state.Label);
                    break;
                }
                case BindTarget.TextureView:
                {
                    RequireTextureViewState(list, resource.TextureView, BindViewKind(state), state.State, state.Label);
                    break;
                }
                case BindTarget.Sampler:
                    Samplers.Get(resource.SamplerHandle, "Sampler");
                    break;
                case BindTarget.AccelerationStructure:
                    AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                    break;
                case BindTarget.None:
                    break;
                default:
                {
                    throw new RhiException(
                        ErrorCode.UnsupportedFeature,
                        $"Binding state target {state.Target} is not supported by D3D12 backend.");
                }
            }
        }
    }

    internal void RequireBindStates(D3D12CommandList list, ReadOnlySpan<BindState> states)
    {
        if (!ValidationEnabled)
        {
            foreach (var bindState in states)
            {
                switch (bindState.Kind)
                {
                    case BindStateKind.Buffer:
                        list.TrackBuffer(bindState.Buffer);
                        break;
                    case BindStateKind.Texture:
                        list.TrackTexture(bindState.Texture);
                        break;
                    default:
                        throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding state kind {bindState.Kind} is not supported by D3D12 backend.");
                }
            }
            return;
        }

        foreach (var bindState in states)
        {
            switch (bindState.Kind)
            {
                case BindStateKind.Buffer:
                    list.RequireBufferState(bindState.Buffer, bindState.State, bindState.Label);
                    break;
                case BindStateKind.Texture:
                    list.RequireTextureState(bindState.Texture, bindState.TextureRange, bindState.State, bindState.Label);
                    break;
                default:
                    throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding state kind {bindState.Kind} is not supported by D3D12 backend.");
            }
        }
    }

    private void TrackBindRefs(D3D12CommandList list, BindingResourceDesc resource)
    {
        var state = BindRules.Resolve(resource.ResourceType);
        switch (state.Target)
        {
            case BindTarget.BufferView:
                list.TrackBuffer(BufferViews.Get(resource.BufferView, "BufferView").Buffer);
                break;
            case BindTarget.TextureView:
                list.TrackTexture(TextureViews.Get(resource.TextureView, "TextureView").Texture);
                break;
            case BindTarget.Sampler:
            case BindTarget.AccelerationStructure:
            case BindTarget.None:
                break;
            default:
                throw new RhiException(
                    ErrorCode.UnsupportedFeature,
                    $"Binding state target {state.Target} is not supported by D3D12 backend.");
        }
    }

    private static ViewKind BindViewKind(BindStateInfo state)
        => state.State switch
        {
            ResourceState.ConstantBuffer => ViewKind.ConstantBuffer,
            ResourceState.ShaderResource => ViewKind.ShaderResource,
            ResourceState.UnorderedAccess => ViewKind.UnorderedAccess,
            _ => throw new RhiException(
                ErrorCode.UnsupportedFeature,
                $"Binding state {state.State} has no D3D12 view kind."),
        };

    internal ShaderDescriptorAllocation? CreateTransientResources(
        LayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
        => CreateBindingDescriptors(
            layout.ResourceSlots,
            layout.ResourceSlotLookup,
            layout.ResourceDescriptorCount,
            layout.ResourceSlotsNeedNullDescriptors,
            resources,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            dynamicOffsets,
            transient: true);

    internal ShaderDescriptorAllocation? CreateTransientDynamic(
        LayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
        => CreateBindingDescriptors(
            layout.DynamicResourceSlots,
            layout.DynamicResourceSlotLookup,
            layout.DynamicResourceDescriptorCount,
            layout.DynamicResourceSlotsNeedNullDescriptors,
            resources,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            dynamicOffsets,
            transient: true);

    internal ShaderDescriptorAllocation? CreateTransientSamplers(LayoutRecord layout, ReadOnlySpan<BindingResourceDesc> resources)
        => CreateBindingDescriptors(layout.SamplerSlots, layout.SamplerSlotLookup, layout.SamplerDescriptorCount, layout.SamplerSlotsNeedNullDescriptors, resources, DescriptorHeapType.Sampler, transient: true);

    private DeviceFeatures CreateFeatures()
    {
        FeatureDataD3D12Options options = default;
        _device.CheckFeatureSupport(Vortice.Direct3D12.Feature.Options, ref options);
        FeatureDataD3D12Options5 options5 = default;
        bool hasOptions5 = _device.CheckFeatureSupport(Vortice.Direct3D12.Feature.Options5, ref options5);
        FeatureDataD3D12Options7 options7 = default;
        bool hasOptions7 = _device.CheckFeatureSupport(Vortice.Direct3D12.Feature.Options7, ref options7);
        using var device1 = _device.QueryInterfaceOrNull<ID3D12Device1>();
        using var device2 = _device.QueryInterfaceOrNull<ID3D12Device2>();
        using var device5 = _device.QueryInterfaceOrNull<ID3D12Device5>();
        return new DeviceFeatures
        {
            GraphicsQueue = true,
            ComputeQueue = true,
            CopyQueue = true,
            ParallelCommandRecording = false,
            StaticSamplers = true,
            PlacedResources = true,
            MixedResourceHeaps = options.ResourceHeapTier == ResourceHeapTier.Tier2,
            ResourceAliasing = true,
            MemoryBudget = true,
            TimestampQueries = true,
            OcclusionQueries = true,
            PipelineStatisticsQueries = true,
            PipelineCache = device1 != null,
            DrawIndirect = true,
            DispatchIndirect = true,
            MultiDrawIndirect = true,
            MultiDispatchIndirect = true,
            IndirectCount = true,
            Bindless = options.ResourceBindingTier == ResourceBindingTier.Tier3,
            PartiallyBoundDescriptors = options.ResourceBindingTier >= ResourceBindingTier.Tier2,
            DynamicOffsets = true,
            TypedUavLoad = options.TypedUAVLoadAdditionalFormats,
            SamplerAnisotropy = true,
            TextureCubeArray = true,
            ConservativeRasterization = options.ConservativeRasterizationTier != ConservativeRasterizationTier.TierNotSupported,
            IndependentBlend = true,
            GeometryShader = true,
            TessellationShader = true,
            MeshShader = device2 != null && hasOptions7 && options7.MeshShaderTier != MeshShaderTier.NotSupported,
            RayTracing = device5 != null && hasOptions5 && options5.RaytracingTier != RaytracingTier.NotSupported,
        };
    }

    private DeviceLimits CreateLimits()
    {
        ulong frequency = 0;
        _graphicsQueue.NativeQueue.GetTimestampFrequency(out frequency);
        return new DeviceLimits
        {
            BufferPlacementAlignment = 65536,
            TexturePlacementAlignment = 65536,
            MsaaTexturePlacementAlignment = 4194304,
            MinMemoryHeapAlignment = 65536,
            TextureRowPitchAlignment = 256,
            MaxDescriptorArrayLength = checked((uint)Policy.ShaderSamplerDescriptorCapacity),
            MaxBindlessResourceDescriptors = checked((uint)Policy.ShaderResourceDescriptorCapacity),
            MaxBindlessSamplerDescriptors = checked((uint)Policy.ShaderSamplerDescriptorCapacity),
            TimestampPeriodNanoseconds = frequency == 0 ? 1 : 1_000_000_000.0f / frequency,
            MaxIndirectDrawCount = 65_535,
            MaxIndirectDispatchCount = 65_535,
            MaxRayRecursionDepth = Vortice.Direct3D12.D3D12.RaytracingMaxDeclarableTraceRecursionDepth,
            MaxRayTracingAttributeSizeInBytes = Vortice.Direct3D12.D3D12.RaytracingMaxAttributeSizeInBytes,
            RayTracingShaderIdentifierSizeInBytes = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes,
            RayTracingShaderRecordAlignment = Vortice.Direct3D12.D3D12.RaytracingShaderRecordByteAlignment,
            RayTracingShaderTableAlignment = Vortice.Direct3D12.D3D12.RaytracingShaderTableByteAlignment,
            MaxRayTracingShaderRecordStride = Vortice.Direct3D12.D3D12.RaytracingMaxShaderRecordStride,
            AccelerationStructureAlignment = Vortice.Direct3D12.D3D12.RaytracingAccelerationStructureByteAlignment,
        };
    }

    private void InitializeBuffer(BufferHandle handle, ReadOnlySpan<byte> initialData, ResourceState finalState)
    {
        var record = Buffers.Get(handle, "Buffer");
        if (record.Desc.Memory == MemoryClass.CpuUpload)
        {
            EnsureUploadMap(record);
            initialData.CopyTo(record.PersistentMappedMemory!.Memory.Span);
            return;
        }

        byte[] initialBytes = initialData.ToArray();
        using var upload = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            D3D12Mappings.BufferResourceDesc(new BufferDesc { SizeInBytes = (ulong)initialBytes.Length, Memory = MemoryClass.CpuUpload }),
            ResourceStates.GenericRead,
            null);
        unsafe
        {
            byte* ptr = upload.Map<byte>(0);
            initialBytes.CopyTo(new Span<byte>(ptr, initialBytes.Length));
            upload.Unmap(0, new Vortice.Direct3D12.Range(0, (nuint)initialBytes.Length));
        }

        ExecuteAndWait(CommandQueueKind.Direct, list =>
        {
            list.CopyBufferRegion(record.Resource, 0, upload, 0, (ulong)initialBytes.Length);
            if (finalState != ResourceState.CopyDestination)
            {
                list.ResourceBarrierTransition(record.Resource, ResourceStates.CopyDest, D3D12Mappings.ToState(finalState), Vortice.Direct3D12.D3D12.ResourceBarrierAllSubResources, ResourceBarrierFlags.None);
            }
        });
        record.State = finalState;
    }

    private unsafe void EnsureUploadMap(BufferRecord record)
    {
        if (record.Desc.Memory != MemoryClass.CpuUpload || record.PersistentMappedMemory != null)
            return;
        if (record.Desc.SizeInBytes > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 persistent upload mappings are limited to 2GB.");

        byte* pointer = record.Resource.Map<byte>(0);
        record.PersistentMappedMemory = new MappedMemory(pointer, checked((int)record.Desc.SizeInBytes));
    }

    internal void ExecuteAndWait(CommandQueueKind queueKind, Action<ID3D12GraphicsCommandList> record)
    {
        var queue = GetQueue(queueKind);
        using var allocator = _device.CreateCommandAllocator(queue.NativeType);
        using var list = _device.CreateCommandList<ID3D12GraphicsCommandList>(queue.NativeType, allocator, null);
        record(list);
        list.Close();
        queue.NativeQueue.ExecuteCommandLists([list]);
        ulong fenceValue = ++_uploadFenceValue;
        queue.NativeQueue.Signal(_uploadFence, fenceValue).CheckError();
        WaitNativeFence(_uploadFence, fenceValue, ulong.MaxValue);
    }

    private void ValidatePlacement(MemoryHeapHandle heapHandle, ulong offset, ResourceMemoryRequirements requirements, MemoryClass memory)
    {
        var heap = MemoryHeaps.Get(heapHandle, "MemoryHeap");
        if (heap.Desc.Memory != memory)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Resource memory {memory} is incompatible with heap memory {heap.Desc.Memory}.");
        if (!HeapKindMatches(heap.Desc.Kind, requirements.HeapKind))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Resource heap kind {requirements.HeapKind} is incompatible with heap kind {heap.Desc.Kind}.");
        if (offset % requirements.Alignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Placed resource offset {offset} must be aligned to {requirements.Alignment}.");
        if (offset > heap.Desc.SizeInBytes || requirements.SizeInBytes > heap.Desc.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Placed resource range is outside the memory heap.");
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
        {
            foreach (var placed in heap.PlacedAllocations)
            {
                if (RangesOverlap(offset, requirements.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes))
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Overlapping placed resources require a memory heap created with AllowAliasing.");
            }
        }
    }

    private bool HeapKindMatches(MemoryHeapKind heapKind, MemoryHeapKind requiredKind)
        => heapKind == requiredKind || (heapKind == MemoryHeapKind.Mixed && Features.MixedResourceHeaps);

    private static ResourceStates InitialBufferState(BufferDesc desc, ResourceState logicalState)
        => desc.Memory switch
        {
            MemoryClass.CpuUpload => ResourceStates.GenericRead,
            MemoryClass.CpuReadback => ResourceStates.CopyDest,
            _ => D3D12Mappings.ToState(logicalState),
        };

    private void RegisterPlacedAllocation(AliasingResource resource, ResourceAllocationInfo allocation)
    {
        if (allocation.Ownership != ResourceOwnership.Placed)
            return;
        var heap = MemoryHeaps.Get(allocation.Heap, "MemoryHeap");
        bool active = WouldBeActive(heap, allocation.HeapOffset, allocation.SizeInBytes);

        heap.PlacedAllocations.Add(new PlacedRecord(resource, allocation) { Active = active });
        heap.LiveResourceCount++;
        if (heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            _aliasingHeapResourceCount++;
    }

    private static bool WouldBeActive(HeapRecord heap, ulong offset, ulong sizeInBytes)
    {
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            return true;

        foreach (var placed in heap.PlacedAllocations)
        {
            if (RangesOverlap(offset, sizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes)
                && placed.Active)
            {
                return false;
            }
        }

        return true;
    }

    private void ReleaseAllocation(ResourceAllocationInfo allocation, AliasingResource resource)
    {
        if (allocation.Ownership != ResourceOwnership.Placed)
            return;
        var heap = MemoryHeaps.Get(allocation.Heap, "MemoryHeap");
        for (int index = 0; index < heap.PlacedAllocations.Count; index++)
        {
            if (heap.PlacedAllocations[index].Resource == resource)
            {
                heap.PlacedAllocations.RemoveAt(index);
                break;
            }
        }

        heap.LiveResourceCount--;
        if (heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            _aliasingHeapResourceCount--;
    }

    internal void ValidateAliasingBarrier(AliasingBarrier barrier)
    {
        var before = AllocationFor(barrier.Before);
        var after = AllocationFor(barrier.After);
        if (before == null && after == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier cannot have both endpoints empty.");
        if (before != null && before.Value.Ownership != ResourceOwnership.Placed)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier before endpoint must be placed.");
        if (after != null && after.Value.Ownership != ResourceOwnership.Placed)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier after endpoint must be placed.");
        if (before != null)
            ValidateAliasingHeap(before.Value);
        if (after != null && (before == null || after.Value.Heap != before.Value.Heap))
            ValidateAliasingHeap(after.Value);
        if (before != null && after != null)
        {
            if (before.Value.Heap != after.Value.Heap)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier endpoints must be placed in the same heap.");
            if (!RangesOverlap(before.Value.HeapOffset, before.Value.SizeInBytes, after.Value.HeapOffset, after.Value.SizeInBytes))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier endpoints must overlap in heap memory.");
        }
    }

    private void ValidateAliasingHeap(ResourceAllocationInfo allocation)
    {
        var heap = MemoryHeaps.Get(allocation.Heap, "AliasingMemoryHeap");
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier requires a memory heap created with AllowAliasing.");
    }

    private ResourceAllocationInfo? AllocationFor(AliasingResource resource)
        => resource.Kind switch
        {
            AliasingResourceKind.None => null,
            AliasingResourceKind.Buffer => Buffers.Get(resource.Buffer, "AliasingBuffer").Allocation,
            AliasingResourceKind.Texture => Textures.Get(resource.Texture, "AliasingTexture").Allocation,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined."),
        };

    internal PlacedRecord? FindPlacedAllocation(AliasingResource resource)
    {
        ResourceAllocationInfo allocation = resource.Kind switch
        {
            AliasingResourceKind.Buffer => Buffers.Get(resource.Buffer, "AliasingBuffer").Allocation,
            AliasingResourceKind.Texture => Textures.Get(resource.Texture, "AliasingTexture").Allocation,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined."),
        };
        if (allocation.Ownership != ResourceOwnership.Placed)
            return null;
        var heap = MemoryHeaps.Get(allocation.Heap, "AliasingMemoryHeap");
        foreach (var placed in heap.PlacedAllocations)
        {
            if (placed.Resource == resource)
                return placed;
        }

        throw new RhiException(ErrorCode.InvalidHandle, "Placed allocation record is missing.");
    }

    internal bool UsesAliasingHeap(PlacedRecord placed)
        => MemoryHeaps.Get(placed.Allocation.Heap, "AliasingMemoryHeap").Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing);

    internal bool NeedsAliasValidation(AliasingResource resource)
    {
        if (_aliasingHeapResourceCount == 0)
            return false;
        var placed = FindPlacedAllocation(resource);
        return placed != null && UsesAliasingHeap(placed);
    }

    internal void ApplyAliasingBarrier(AliasingBarrier barrier)
    {
        var before = barrier.Before.Kind == AliasingResourceKind.None ? null : FindPlacedAllocation(barrier.Before);
        var after = barrier.After.Kind == AliasingResourceKind.None ? null : FindPlacedAllocation(barrier.After);
        if (before != null)
            before.Active = false;
        if (after != null)
        {
            var heap = MemoryHeaps.Get(after.Allocation.Heap, "AliasingMemoryHeap");
            foreach (var placed in heap.PlacedAllocations)
            {
                if (placed != after && RangesOverlap(after.Allocation.HeapOffset, after.Allocation.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes))
                    placed.Active = false;
            }

            after.Active = true;
        }
    }

    internal void MarkQueriesWritten(ReadOnlySpan<QueryKey> queries)
    {
        foreach (var query in queries)
        {
            var pool = QueryPools.Get(query.Pool, "QueryPool");
            pool.Written[checked((int)query.Index)] = true;
        }
    }

    private void FreeTrackedDescriptor(ReadOnlySpan<TrackedDescriptorAllocation> allocations)
    {
        foreach (var allocation in allocations)
            FreeShaderDescriptor(allocation.Allocation, allocation.Type);
    }

    private void ReleaseCommandStorage(CommandBufferRecord record)
    {
        FreeTrackedDescriptor(record.TransientDescriptors);
        GetQueue(record.QueueKind).ReturnCommandList(record.Lease);
    }

    private void RetireCommandBuffers()
    {
        for (int index = _pendingCommandBufferRetirements.Count - 1; index >= 0; index--)
        {
            var record = _pendingCommandBufferRetirements[index];
            if (MayUseResources(record))
                continue;
            ReleaseCommandStorage(record);
            _pendingCommandBufferRetirements.RemoveAt(index);
        }
    }

    private IEnumerable<CommandBufferRecord> LiveCommandBuffers()
    {
        RetireCommandBuffers();
        foreach (var commandBuffer in CommandBuffers.Values)
            yield return commandBuffer;
        foreach (var commandBuffer in _pendingCommandBufferRetirements)
            yield return commandBuffer;
    }

    private void CheckBufferFree(BufferHandle buffer)
    {
        var bufferRecord = Buffers.Get(buffer, "Buffer");
        string bufferName = string.IsNullOrWhiteSpace(bufferRecord.Desc.Name)
            ? buffer.ToString()
            : $"{bufferRecord.Desc.Name} ({buffer})";
        if (ActiveHandles.Contains(_activeListGate, _activeListBuffers, buffer))
            throw new RhiException(ErrorCode.ValidationFailure, $"Cannot destroy buffer '{bufferName}' referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedBuffers)
            {
                if (referenced == buffer)
                {
                    string state = commandBuffer.Submitted
                        ? $"submitted fence={commandBuffer.RetirementValue}"
                        : "not submitted";
                    throw new RhiException(ErrorCode.ValidationFailure, $"Cannot destroy buffer '{bufferName}' referenced by a live D3D12 command buffer ({state}).");
                }
            }
        }
    }

    private void CheckBufferViews(BufferHandle buffer)
    {
        foreach (var view in BufferViews.Values)
        {
            if (view.Buffer == buffer)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer while buffer views still reference it.");
        }
    }

    internal void CheckTextureFree(TextureHandle texture)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListTextures, texture))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedTextures)
            {
                if (referenced == texture)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckTextureViews(TextureHandle texture)
    {
        foreach (var view in TextureViews.Values)
        {
            if (view.Texture == texture)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture while texture views still reference it.");
        }
    }

    private void CheckQueryFree(QueryPoolHandle queryPool)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListQueryPools, queryPool))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a query pool referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedQueryPools)
            {
                if (referenced == queryPool)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a query pool referenced by a live D3D12 command buffer.");
            }
        }
    }

    internal void CheckTvFree(TextureViewHandle view)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListTextureViews, view))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedTextureViews)
            {
                if (referenced == view)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckTvRefs(TextureViewHandle view)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Resources)
            {
                if ((resource.ResourceType == BindingType.TextureRead || resource.ResourceType == BindingType.TextureReadWrite)
                    && resource.TextureView == view)
                {
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by a live D3D12 binding set.");
                }
            }
        }
    }

    private void CheckBvFree(BufferViewHandle view)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListBufferViews, view))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedBufferViews)
            {
                if (referenced == view)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckBvRefs(BufferViewHandle view)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Resources)
            {
                if (BindRules.HasBufferView(resource.ResourceType) && resource.BufferView == view)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by a live D3D12 binding set.");
            }
        }
    }

    private void CheckSamplerFree(SamplerHandle sampler)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListSamplers, sampler))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedSamplers)
            {
                if (referenced == sampler)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckSamplerRefs(SamplerHandle sampler)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Resources)
            {
                if (resource.ResourceType == BindingType.Sampler && resource.SamplerHandle == sampler)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by a live D3D12 binding set.");
            }
        }
    }

    private void CheckLayoutFree(BindingLayoutHandle bindingLayout)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListBindingLayouts, bindingLayout))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by an active D3D12 command list.");
        foreach (var set in BindingSets.Values)
        {
            if (set.Layout == bindingLayout)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live D3D12 binding set.");
        }

        foreach (var layout in PipelineLayouts.Values)
        {
            foreach (var referenced in layout.Desc.BindingLayouts)
            {
                if (referenced == bindingLayout)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live D3D12 pipeline layout.");
            }
        }

        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedBindingLayouts)
            {
                if (referenced == bindingLayout)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckSetFree(BindingSetHandle bindingSet)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListBindingSets, bindingSet))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot update or destroy a binding set referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedBindingSets)
            {
                if (referenced == bindingSet)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding set referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckPlFree(PipelineLayoutHandle pipelineLayout)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListPipelineLayouts, pipelineLayout))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by an active D3D12 command list.");
        foreach (var pipeline in Pipelines.Values)
        {
            if (pipeline.Layout == pipelineLayout)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by a live D3D12 pipeline.");
        }

        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedPipelineLayouts)
            {
                if (referenced == pipelineLayout)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckPipelineFree(PipelineHandle pipeline)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListPipelines, pipeline))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedPipelines)
            {
                if (referenced == pipeline)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckAccelerationFree(AccelerationStructureHandle accelerationStructure)
    {
        if (ActiveHandles.Contains(_activeListGate, _activeListAccelerationStructures, accelerationStructure))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by an active D3D12 command list.");
        foreach (var commandBuffer in LiveCommandBuffers())
        {
            if (!MayUseResources(commandBuffer))
                continue;
            foreach (var referenced in commandBuffer.ReferencedAccelerationStructures)
            {
                if (referenced == accelerationStructure)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by a live D3D12 command buffer.");
            }
        }
    }

    private void CheckAccelerationReferences(AccelerationStructureHandle accelerationStructure)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Resources)
            {
                if (resource.ResourceType == BindingType.AccelerationStructure
                    && resource.AccelerationStructure == accelerationStructure)
                {
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by a live D3D12 binding set.");
                }
            }
        }
    }

    private static bool MayUseResources(CommandBufferRecord commandBuffer)
        => !commandBuffer.Submitted
            || (commandBuffer.RetirementValue != 0
                && (commandBuffer.RetirementFence == null
                    || commandBuffer.RetirementFence.CompletedValue < commandBuffer.RetirementValue));

    private static QueryHeapType ToQueryHeap(QueryPoolDesc desc)
        => desc.Type switch
        {
            QueryType.Timestamp => QueryHeapType.Timestamp,
            QueryType.Occlusion => QueryHeapType.Occlusion,
            QueryType.PipelineStatistics => QueryHeapType.PipelineStatistics,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Query type {desc.Type} is not defined."),
        };

    private SampleCountFlags GetSampleCounts(Vortice.DXGI.Format format, FormatSupport support)
    {
        if (support == FormatSupport.None)
            return SampleCountFlags.None;
        SampleCountFlags flags = SampleCountFlags.Count1;
        AddSampleCount(format, 2, SampleCountFlags.Count2, ref flags);
        AddSampleCount(format, 4, SampleCountFlags.Count4, ref flags);
        AddSampleCount(format, 8, SampleCountFlags.Count8, ref flags);
        AddSampleCount(format, 16, SampleCountFlags.Count16, ref flags);
        return flags;
    }

    private void AddSampleCount(Vortice.DXGI.Format format, uint sampleCount, SampleCountFlags flag, ref SampleCountFlags flags)
    {
        var data = new FeatureDataMultisampleQualityLevels
        {
            Format = format,
            SampleCount = sampleCount,
            Flags = MultisampleQualityLevelFlags.None,
        };
        if (_device.CheckFeatureSupport(Vortice.Direct3D12.Feature.MultisampleQualityLevels, ref data) && data.NumQualityLevels > 0)
            flags |= flag;
    }

    private void ValidateTextureFormat(TextureDesc desc)
    {
        var capabilities = GetFormatCapabilities(desc.Format);
        if (capabilities.Support == FormatSupport.None)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} is not supported by D3D12.");
        if (desc.BindFlags.HasFlag(BindFlags.ShaderResource) && !capabilities.Support.HasFlag(FormatSupport.ShaderSample))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support shader sampling.");
        if (desc.BindFlags.HasFlag(BindFlags.UnorderedAccess) && !capabilities.Support.HasFlag(FormatSupport.UnorderedAccess))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support unordered access.");
        if (desc.BindFlags.HasFlag(BindFlags.RenderTarget) && !capabilities.Support.HasFlag(FormatSupport.RenderTarget))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support render targets.");
        if (desc.BindFlags.HasFlag(BindFlags.DepthStencil) && !capabilities.Support.HasFlag(FormatSupport.DepthStencil))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support depth/stencil usage.");
        if (desc.BindFlags.HasFlag(BindFlags.CopySource) && !capabilities.Support.HasFlag(FormatSupport.CopySource))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support copy source usage.");
        if (desc.BindFlags.HasFlag(BindFlags.CopyDestination) && !capabilities.Support.HasFlag(FormatSupport.CopyDestination))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support copy destination usage.");
        if (!capabilities.SampleCounts.HasFlag(SampleCountFlag(desc.SampleCount)))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Texture format {desc.Format} does not support sample count {desc.SampleCount}.");
    }

    private static void ValidateExternalHeap(ID3D12Resource resource, MemoryClass memory, string label)
    {
        resource.GetHeapProperties(out var properties, out _).CheckError();
        var expected = D3D12Mappings.ToHeapType(memory);
        if (properties.Type != expected)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} heap type {properties.Type} does not match memory class {memory}.");
    }

    private static void ValidateExternalFlags(ResourceFlags flags, BindFlags bindFlags, string label)
    {
        if (bindFlags.HasFlag(BindFlags.RenderTarget) && !flags.HasFlag(ResourceFlags.AllowRenderTarget))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} is missing D3D12 AllowRenderTarget for RenderTarget bind flag.");
        if (bindFlags.HasFlag(BindFlags.DepthStencil) && !flags.HasFlag(ResourceFlags.AllowDepthStencil))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} is missing D3D12 AllowDepthStencil for DepthStencil bind flag.");
        if (bindFlags.HasFlag(BindFlags.UnorderedAccess) && !flags.HasFlag(ResourceFlags.AllowUnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} is missing D3D12 AllowUnorderedAccess for UnorderedAccess bind flag.");
        if (bindFlags.HasFlag(BindFlags.ShaderResource) && flags.HasFlag(ResourceFlags.DenyShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} denies shader-resource usage required by ShaderResource bind flag.");
    }

    private void ValidateSamples(Format format, uint sampleCount, string label)
    {
        var flag = SampleCountFlag(sampleCount);
        if (flag == SampleCountFlags.None)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} uses unsupported sample count {sampleCount}.");
        if (!GetFormatCapabilities(format).SampleCounts.HasFlag(flag))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"{label} does not support sample count {sampleCount}.");
    }

    private static SampleCountFlags SampleCountFlag(uint sampleCount)
        => sampleCount switch
        {
            1 => SampleCountFlags.Count1,
            2 => SampleCountFlags.Count2,
            4 => SampleCountFlags.Count4,
            8 => SampleCountFlags.Count8,
            16 => SampleCountFlags.Count16,
            _ => SampleCountFlags.None,
        };

    private void RequireBufferViewState(D3D12CommandList list, BufferViewHandle view, ViewKind expectedKind, ResourceState state, string label)
    {
        var record = BufferViews.Get(view, "BufferView");
        if (record.Kind != expectedKind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires a {expectedKind} buffer view.");
        list.RequireBufferState(record.Buffer, state, label);
    }

    private void RequireTextureViewState(D3D12CommandList list, TextureViewHandle view, ViewKind expectedKind, ResourceState state, string label)
    {
        var record = TextureViews.Get(view, "TextureView");
        if (record.Kind != expectedKind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires a {expectedKind} texture view.");
        var texture = Textures.Get(record.Texture, "Texture");
        list.RequireTextureState(record.Texture, texture.Desc, record.Desc, state, label);
    }

    internal void FreeTransientDescriptor(ShaderDescriptorAllocation? allocation, DescriptorHeapType type)
        => FreeShaderDescriptor(allocation, type);

    private void FreeShaderDescriptor(ShaderDescriptorAllocation? allocation, DescriptorHeapType type)
    {
        if (allocation is { } value)
            FreeShaderDescriptor(value, type);
    }

    private void FreeShaderDescriptor(ShaderDescriptorAllocation allocation, DescriptorHeapType type)
    {
        if (type == DescriptorHeapType.Sampler)
            ShaderSamplerDescriptors.Free(allocation);
        else
            ShaderResourceDescriptors.Free(allocation);
    }

    private void ValidateMappedRange(BufferHandle buffer, ulong offset, ulong sizeInBytes, string label)
    {
        var record = Buffers.Get(buffer, "Buffer");
        if (record.MappedMemory == null && !record.PersistentMapActive)
            throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires the buffer to be mapped.");
        if (sizeInBytes == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range size must be greater than zero.");
        if (offset < record.MappedOffset || sizeInBytes > record.MappedSize || offset - record.MappedOffset > record.MappedSize - sizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range must be inside the mapped range.");
    }

    internal static bool RangesOverlap(ulong firstOffset, ulong firstSize, ulong secondOffset, ulong secondSize)
        => firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;

    private void ValidateGraphicsDesc(GraphicsPipelineDesc desc)
    {
        if (!Enum.IsDefined(desc.Topology))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Primitive topology value {desc.Topology} is not defined.");
        ValidateShaderStages(desc);
        if (desc.SampleCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline sample count must be greater than zero.");
        if (desc.Multisample.SampleCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline multisample count must be greater than zero.");
        if (desc.SampleCount != desc.Multisample.SampleCount)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline SampleCount must match Multisample.SampleCount.");
        if ((uint)desc.ColorFormats.Count > Limits.MaxColorAttachments)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics pipeline has {desc.ColorFormats.Count} color formats, exceeding limit {Limits.MaxColorAttachments}.");
        if (desc.ColorFormats.Count == 0 && desc.DepthStencilFormat == Format.Unknown)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline requires at least one color or depth/stencil format.");
        if (desc.Blend.Targets.Count != 0 && desc.Blend.Targets.Count != desc.ColorFormats.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Blend target count must match color attachment count.");
        for (int index = 0; index < desc.ColorFormats.Count; index++)
        {
            var format = desc.ColorFormats[index];
            if (!Enum.IsDefined(format) || format == Format.Unknown)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics pipeline color format {index} is invalid.");
            if (Validation.IsDepthFormat(format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics pipeline color format {index} must not be a depth format.");
            if (!GetFormatCapabilities(format).Support.HasFlag(FormatSupport.RenderTarget))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics pipeline color format {format} does not support render targets.");
            ValidateSamples(format, desc.SampleCount, $"Graphics pipeline color format {format}");
        }

        if (desc.DepthStencilFormat != Format.Unknown && !Enum.IsDefined(desc.DepthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics pipeline depth format {desc.DepthStencilFormat} is invalid.");
        if (desc.DepthStencilFormat != Format.Unknown && !Validation.IsDepthFormat(desc.DepthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth/stencil format must be a depth format, got {desc.DepthStencilFormat}.");
        if (desc.DepthStencilFormat != Format.Unknown
            && !GetFormatCapabilities(desc.DepthStencilFormat).Support.HasFlag(FormatSupport.DepthStencil))
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth/stencil format {desc.DepthStencilFormat} does not support depth/stencil usage.");
        }
        if (desc.DepthStencilFormat != Format.Unknown)
            ValidateSamples(desc.DepthStencilFormat, desc.SampleCount, $"Graphics pipeline depth/stencil format {desc.DepthStencilFormat}");
        if (desc.VertexAttributes.Count > 0 && desc.VertexBuffers.Count == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline vertex attributes require at least one vertex buffer layout.");
        ValidateVertexInput(desc);
    }

    private void ValidateMeshDesc(MeshPipelineDesc desc)
    {
        Validation.MeshPipelineDesc(desc);
        if ((uint)desc.ColorFormats.Count > Limits.MaxColorAttachments)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline has {desc.ColorFormats.Count} color formats, exceeding limit {Limits.MaxColorAttachments}.");
        if (desc.Blend.Targets.Count != 0 && desc.Blend.Targets.Count != desc.ColorFormats.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mesh pipeline blend target count must match color attachment count.");
        for (int index = 0; index < desc.ColorFormats.Count; index++)
        {
            var format = desc.ColorFormats[index];
            if (!Enum.IsDefined(format) || format == Format.Unknown)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline color format {index} is invalid.");
            if (Validation.IsDepthFormat(format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline color format {index} must not be a depth format.");
            if (!GetFormatCapabilities(format).Support.HasFlag(FormatSupport.RenderTarget))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline color format {format} does not support render targets.");
            ValidateSamples(format, desc.SampleCount, $"Mesh pipeline color format {format}");
        }

        if (desc.DepthStencilFormat == Format.Unknown)
            return;
        if (!Enum.IsDefined(desc.DepthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline depth format {desc.DepthStencilFormat} is invalid.");
        if (!Validation.IsDepthFormat(desc.DepthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline depth/stencil format must be a depth format, got {desc.DepthStencilFormat}.");
        if (!GetFormatCapabilities(desc.DepthStencilFormat).Support.HasFlag(FormatSupport.DepthStencil))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mesh pipeline depth/stencil format {desc.DepthStencilFormat} does not support depth/stencil usage.");
        ValidateSamples(desc.DepthStencilFormat, desc.SampleCount, $"Mesh pipeline depth/stencil format {desc.DepthStencilFormat}");
    }

    private void ValidateRtShaders(RtPipelineDesc desc)
    {
        var declared = new HashSet<ShaderModuleHandle>();
        foreach (var shader in desc.Shaders)
        {
            var record = Shaders.Get(shader, "RayTracingShader");
            if (!IsRtStage(record.Desc.Stage))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing pipeline shader '{record.Desc.Name}' has invalid stage {record.Desc.Stage}.");
            if (!declared.Add(shader))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pipeline shader list contains duplicate handles.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in desc.ShaderGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader group name must be specified.");
            if (!names.Add(group.Name))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group '{group.Name}' is duplicated.");
            ValidateRtGroup(group, declared);
        }
    }

    private void ValidateRtGroup(RtGroupDesc group, HashSet<ShaderModuleHandle> declaredShaders)
    {
        if (!Enum.IsDefined(group.Kind))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group kind value {group.Kind} is not defined.");

        switch (group.Kind)
        {
            case RtGroupKind.General:
                RequireGroupShader(group.GeneralShader, declaredShaders, "general shader");
                EnsureRtStage(group.GeneralShader, ShaderStage.RayGeneration, ShaderStage.Miss, ShaderStage.Callable);
                if (group.AnyHitShader.IsValid || group.ClosestHitShader.IsValid || group.IntersectionShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "General ray tracing shader groups cannot declare hit shaders.");
                break;
            case RtGroupKind.TrianglesHitGroup:
                if (group.GeneralShader.IsValid || group.IntersectionShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Triangle hit groups cannot declare general or intersection shaders.");
                if (!group.AnyHitShader.IsValid && !group.ClosestHitShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Triangle hit groups require at least one any-hit or closest-hit shader.");
                if (group.AnyHitShader.IsValid)
                {
                    RequireGroupShader(group.AnyHitShader, declaredShaders, "any-hit shader");
                    EnsureRtStage(group.AnyHitShader, ShaderStage.AnyHit);
                }
                if (group.ClosestHitShader.IsValid)
                {
                    RequireGroupShader(group.ClosestHitShader, declaredShaders, "closest-hit shader");
                    EnsureRtStage(group.ClosestHitShader, ShaderStage.ClosestHit);
                }
                break;
            case RtGroupKind.ProceduralHitGroup:
                if (group.GeneralShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Procedural hit groups cannot declare a general shader.");
                RequireGroupShader(group.IntersectionShader, declaredShaders, "intersection shader");
                EnsureRtStage(group.IntersectionShader, ShaderStage.Intersection);
                if (group.AnyHitShader.IsValid)
                {
                    RequireGroupShader(group.AnyHitShader, declaredShaders, "any-hit shader");
                    EnsureRtStage(group.AnyHitShader, ShaderStage.AnyHit);
                }
                if (group.ClosestHitShader.IsValid)
                {
                    RequireGroupShader(group.ClosestHitShader, declaredShaders, "closest-hit shader");
                    EnsureRtStage(group.ClosestHitShader, ShaderStage.ClosestHit);
                }
                break;
        }
    }

    private void RequireGroupShader(ShaderModuleHandle shader, HashSet<ShaderModuleHandle> declaredShaders, string label)
    {
        if (!shader.IsValid)
            throw new RhiException(ErrorCode.InvalidHandle, $"Ray tracing shader group {label} handle is invalid.");
        if (!declaredShaders.Contains(shader))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group {label} is not present in the pipeline shader list.");
    }

    private void EnsureRtStage(ShaderModuleHandle shader, params ShaderStage[] allowed)
    {
        var stage = Shaders.Get(shader, "RayTracingShader").Desc.Stage;
        for (int index = 0; index < allowed.Length; index++)
        {
            if (stage == allowed[index])
                return;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader stage {stage} is not valid for this shader group slot.");
    }

    private static bool IsRtStage(ShaderStage stage)
        => stage is ShaderStage.RayGeneration
            or ShaderStage.AnyHit
            or ShaderStage.ClosestHit
            or ShaderStage.Miss
            or ShaderStage.Intersection
            or ShaderStage.Callable;

    private ID3D12StateObject CreateRtState(ID3D12Device5 device5, PipeLayoutRecord layout, RtPipelineDesc desc)
    {
        if (desc.PipelineCache.IsValid)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 pipeline cache objects do not support ray tracing state objects; leave RtPipelineDesc.PipelineCache invalid.");

        var subobjects = new List<StateSubObject>();
        foreach (var shader in CreateRtLibs(desc))
            subobjects.Add(shader);
        foreach (var group in desc.ShaderGroups)
        {
            if (group.Kind == RtGroupKind.General)
                continue;
            subobjects.Add(new StateSubObject(CreateHitGroup(group)));
        }

        subobjects.Add(new StateSubObject(new RaytracingShaderConfig(desc.MaxPayloadSizeInBytes, desc.MaxAttributeSizeInBytes)));
        subobjects.Add(new StateSubObject(new RaytracingPipelineConfig(desc.MaxRayRecursionDepth)));
        subobjects.Add(new StateSubObject(new GlobalRootSignature(layout.RootSignature)));

        ulong debugMessageStart = GetDebugCount();
        try
        {
            return device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subobjects.ToArray()));
        }
        catch (SharpGenException ex)
        {
            throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create ray tracing pipeline '{desc.Name}': {ex.Message}{FormatDebugMessages(debugMessageStart)}");
        }
    }

    private IEnumerable<StateSubObject> CreateRtLibs(RtPipelineDesc desc)
    {
        foreach (var shaderHandle in desc.Shaders)
        {
            var shader = Shaders.Get(shaderHandle, "RayTracingShader");
            var exports = new List<ExportDescription>();
            foreach (var exportName in RtExports(desc, shaderHandle, shader.Desc.EntryPoint))
            {
                string? rename = exportName == shader.Desc.EntryPoint ? null : shader.Desc.EntryPoint;
                exports.Add(new ExportDescription(exportName, rename!, ExportFlags.None));
            }

            if (exports.Count == 0)
                exports.Add(new ExportDescription(shader.Desc.EntryPoint, null!, ExportFlags.None));
            yield return new StateSubObject(new DxilLibraryDescription(shader.Desc.Bytecode, exports.ToArray()));
        }
    }

    private static IEnumerable<string> RtExports(RtPipelineDesc desc, ShaderModuleHandle shader, string entryPoint)
    {
        var exports = new HashSet<string>(StringComparer.Ordinal);
        bool exportedEntryPoint = false;
        foreach (var group in desc.ShaderGroups)
        {
            switch (group.Kind)
            {
                case RtGroupKind.General when group.GeneralShader == shader:
                    if (exports.Add(group.Name))
                        yield return group.Name;
                    exportedEntryPoint |= group.Name == entryPoint;
                    break;
                case RtGroupKind.TrianglesHitGroup:
                case RtGroupKind.ProceduralHitGroup:
                    if (group.AnyHitShader == shader || group.ClosestHitShader == shader || group.IntersectionShader == shader)
                        exportedEntryPoint = true;
                    break;
            }
        }

        if (exportedEntryPoint && exports.Add(entryPoint))
            yield return entryPoint;
    }

    private HitGroupDescription CreateHitGroup(RtGroupDesc group)
    {
        var type = group.Kind switch
        {
            RtGroupKind.TrianglesHitGroup => HitGroupType.Triangles,
            RtGroupKind.ProceduralHitGroup => HitGroupType.ProceduralPrimitive,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group kind {group.Kind} is not a hit group."),
        };

        return new HitGroupDescription(
            group.Name,
            type,
            group.AnyHitShader.IsValid ? Shaders.Get(group.AnyHitShader, "AnyHitShader").Desc.EntryPoint : null!,
            group.ClosestHitShader.IsValid ? Shaders.Get(group.ClosestHitShader, "ClosestHitShader").Desc.EntryPoint : null!,
            group.IntersectionShader.IsValid ? Shaders.Get(group.IntersectionShader, "IntersectionShader").Desc.EntryPoint : null!);
    }

    private ID3D12Device5 RequireRtDevice()
    {
        var device5 = _device.QueryInterfaceOrNull<ID3D12Device5>();
        if (device5 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 ray tracing requires ID3D12Device5.");
        return device5;
    }

    internal BuildRaytracingAccelerationStructureInputs CreateRtInputs(AccelBuildDesc desc, bool requireResourceHandles)
    {
        if (desc.Kind == AccelerationStructureKind.TopLevel)
        {
            var geometry = desc.Geometries[0];
            ulong instanceAddress = requireResourceHandles
                ? Buffers.Get(geometry.InstanceBuffer, "AccelerationStructureInstances").Resource.GPUVirtualAddress + geometry.InstanceOffset
                : 0;
            return new BuildRaytracingAccelerationStructureInputs
            {
                Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = D3D12Mappings.ToAccelFlags(desc.Flags),
                DescriptorsCount = geometry.InstanceCount,
                Layout = ElementsLayout.Array,
                InstanceDescriptions = instanceAddress,
            };
        }

        var geometries = new RaytracingGeometryDescription[desc.Geometries.Count];
        for (int index = 0; index < desc.Geometries.Count; index++)
            geometries[index] = CreateRaytracingGeometry(desc.Geometries[index], requireResourceHandles);
        return new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = D3D12Mappings.ToAccelFlags(desc.Flags),
            DescriptorsCount = checked((uint)geometries.Length),
            Layout = ElementsLayout.Array,
            GeometryDescriptions = geometries,
        };
    }

    private RaytracingGeometryDescription CreateRaytracingGeometry(AccelGeomDesc geometry, bool requireResourceHandles)
        => geometry.Kind switch
        {
            AccelGeomKind.Triangles => new RaytracingGeometryDescription(CreateRaytracingTriangles(geometry, requireResourceHandles), D3D12Mappings.ToRtGeom(geometry.Flags)),
            AccelGeomKind.Aabbs => new RaytracingGeometryDescription(CreateRaytracingAabbs(geometry, requireResourceHandles), D3D12Mappings.ToRtGeom(geometry.Flags)),
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Bottom-level acceleration structure geometry kind {geometry.Kind} is not supported."),
        };

    private RaytracingGeometryTrianglesDescription CreateRaytracingTriangles(AccelGeomDesc geometry, bool requireResourceHandles)
    {
        ulong vertexAddress = requireResourceHandles ? Buffers.Get(geometry.VertexBuffer, "AccelerationStructureVertexBuffer").Resource.GPUVirtualAddress + geometry.VertexOffset : 0;
        ulong indexAddress = geometry.IndexBuffer.IsValid && requireResourceHandles ? Buffers.Get(geometry.IndexBuffer, "AccelerationStructureIndexBuffer").Resource.GPUVirtualAddress + geometry.IndexOffset : 0;
        ulong transformAddress = geometry.TransformBuffer.IsValid && requireResourceHandles ? Buffers.Get(geometry.TransformBuffer, "AccelerationStructureTransformBuffer").Resource.GPUVirtualAddress + geometry.TransformOffset : 0;
        bool indexed = geometry.IndexBuffer.IsValid || geometry.IndexCount > 0;
        var vertex = new GpuVirtualAddressAndStride(vertexAddress, geometry.VertexStrideInBytes);
        return new RaytracingGeometryTrianglesDescription(
            in vertex,
            D3D12Mappings.ToRtVertex(geometry.VertexFormat),
            geometry.VertexCount,
            transformAddress,
            indexAddress,
            indexed ? geometry.IndexFormat == IndexFormat.UInt16 ? Vortice.DXGI.Format.R16_UInt : Vortice.DXGI.Format.R32_UInt : Vortice.DXGI.Format.Unknown,
            indexed ? geometry.IndexCount : 0);
    }

    private RaytracingGeometryAabbsDescription CreateRaytracingAabbs(AccelGeomDesc geometry, bool requireResourceHandles)
    {
        ulong address = requireResourceHandles ? Buffers.Get(geometry.AabbBuffer, "AccelerationStructureAabbBuffer").Resource.GPUVirtualAddress + geometry.AabbOffset : 0;
        return new RaytracingGeometryAabbsDescription(geometry.AabbCount, new GpuVirtualAddressAndStride(address, geometry.AabbStrideInBytes));
    }

    private void ValidateShaderStages(GraphicsPipelineDesc desc)
    {
        if (desc.GeometryShader.IsValid)
        {
            if (!Features.GeometryShader)
                throw new RhiException(ErrorCode.UnsupportedFeature, "Geometry shaders are not supported by this device.");
            var geometry = Shaders.Get(desc.GeometryShader, "GeometryShader").Desc;
            if (geometry.Stage != ShaderStage.Geometry)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline geometry shader handle does not reference a geometry shader.");
        }

        bool hasHull = desc.HullShader.IsValid;
        bool hasDomain = desc.DomainShader.IsValid;
        if (hasHull != hasDomain)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Tessellation requires both hull and domain shaders.");
        if (hasHull)
        {
            if (!Features.TessellationShader)
                throw new RhiException(ErrorCode.UnsupportedFeature, "Tessellation shaders are not supported by this device.");
            if (desc.Topology != PrimitiveTopology.PatchList)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Tessellation shaders require PatchList topology.");
            if (desc.PatchControlPoints == 0 || desc.PatchControlPoints > Limits.MaxPatchControlPoints)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"PatchControlPoints must be in [1, {Limits.MaxPatchControlPoints}].");

            var hull = Shaders.Get(desc.HullShader, "HullShader").Desc;
            var domain = Shaders.Get(desc.DomainShader, "DomainShader").Desc;
            if (hull.Stage != ShaderStage.Hull)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline hull shader handle does not reference a hull shader.");
            if (domain.Stage != ShaderStage.Domain)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline domain shader handle does not reference a domain shader.");
        }
        else if (desc.Topology == PrimitiveTopology.PatchList)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, "PatchList topology requires tessellation shaders.");
        }
    }

    private void ValidateVertexInput(GraphicsPipelineDesc desc)
    {
        for (int layoutIndex = 0; layoutIndex < desc.VertexBuffers.Count; layoutIndex++)
        {
            var layout = desc.VertexBuffers[layoutIndex];
            if (!Enum.IsDefined(layout.InputRate))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex input rate value {layout.InputRate} is not defined.");
            if (layout.StrideInBytes == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Vertex buffer stride must be greater than zero.");
            if (layout.InputRate == VertexInputRate.Instance && layout.InstanceStepRate == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Instance-rate vertex buffers require InstanceStepRate greater than zero.");
            for (int previousIndex = 0; previousIndex < layoutIndex; previousIndex++)
            {
                if (desc.VertexBuffers[previousIndex].Slot == layout.Slot)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Duplicate vertex buffer slot {layout.Slot}.");
            }
        }

        for (int attributeIndex = 0; attributeIndex < desc.VertexAttributes.Count; attributeIndex++)
        {
            var attribute = desc.VertexAttributes[attributeIndex];
            if (!Enum.IsDefined(attribute.Format) || attribute.Format == Format.Unknown || Validation.IsDepthFormat(attribute.Format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Invalid vertex attribute format {attribute.Format}.");
            if (!GetFormatCapabilities(attribute.Format).Support.HasFlag(FormatSupport.VertexAttribute))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex attribute format {attribute.Format} does not support input assembler usage.");

            bool slotDeclared = false;
            foreach (var layout in desc.VertexBuffers)
            {
                if (layout.Slot == attribute.BufferSlot)
                {
                    slotDeclared = true;
                    break;
                }
            }

            if (!slotDeclared)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex attribute location {attribute.Location} references undeclared buffer slot {attribute.BufferSlot}.");
            for (int previousIndex = 0; previousIndex < attributeIndex; previousIndex++)
            {
                if (desc.VertexAttributes[previousIndex].Location == attribute.Location)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Duplicate vertex attribute location {attribute.Location}.");
            }
        }
    }

    private static GraphicsPipelineDesc Snapshot(GraphicsPipelineDesc desc)
    {
        var blend = desc.Blend ?? new BlendDesc();
        return desc with
        {
            VertexBuffers = desc.VertexBuffers.ToArray(),
            VertexAttributes = desc.VertexAttributes.ToArray(),
            ColorFormats = desc.ColorFormats.ToArray(),
            Blend = blend with { Targets = blend.Targets.ToArray() },
        };
    }

    private static MeshPipelineDesc Snapshot(MeshPipelineDesc desc)
    {
        var blend = desc.Blend ?? new BlendDesc();
        return desc with
        {
            ColorFormats = desc.ColorFormats.ToArray(),
            Blend = blend with { Targets = blend.Targets.ToArray() },
        };
    }

    private static RtPipelineDesc Snapshot(RtPipelineDesc desc)
        => desc with
        {
            Shaders = desc.Shaders.ToArray(),
            ShaderGroups = desc.ShaderGroups.ToArray(),
        };

    private static void WaitNativeFence(ID3D12Fence fence, ulong value, ulong timeoutNanoseconds)
    {
        if (fence.CompletedValue >= value)
            return;
        using var wait = new ManualResetEvent(false);
        fence.SetEventOnCompletion(value, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
        if (timeoutNanoseconds == ulong.MaxValue)
        {
            wait.WaitOne();
            return;
        }

        ulong timeoutMilliseconds = Math.Max(1, timeoutNanoseconds / 1_000_000);
        if (!wait.WaitOne(checked((int)Math.Min(timeoutMilliseconds, int.MaxValue))))
            throw new RhiException(ErrorCode.ValidationFailure, $"Timed out waiting for D3D12 fence value {value}.");
    }

}
