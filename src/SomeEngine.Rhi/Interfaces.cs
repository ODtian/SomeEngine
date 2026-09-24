namespace SomeEngine.Rhi;

public interface IInstance : IDisposable
{
    IReadOnlyList<AdapterInfo> EnumerateAdapters();
    IDevice CreateDevice(DeviceDesc desc);
}

public interface IBackendFactory
{
    Backend Backend { get; }
    IReadOnlyList<AdapterInfo> EnumerateAdapters();
    IDevice CreateDevice(DeviceDesc desc);
}

public interface IExtensible
{
    T? Get<T>() where T : class;
}

public interface IDevice : IDisposable, IExtensible
{
    AdapterInfo AdapterInfo { get; }
    DeviceFeatures Features { get; }
    DeviceLimits Limits { get; }

    IReadOnlyList<QueueFamilyInfo> QueueFamilies { get; }
    IQueue GetQueue(QueueType type, uint index = 0);

    BufferHandle CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default);
    TextureHandle CreateTexture(TextureDesc desc);
    TextureViewHandle CreateTextureView(TextureHandle texture, TextureViewDesc desc);
    BufferViewHandle CreateBufferView(BufferHandle buffer, BufferViewDesc desc);
    SamplerHandle CreateSampler(SamplerDesc desc);
    ShaderModuleHandle CreateShaderModule(ShaderModuleDesc desc);
    BindingLayoutHandle CreateBindingLayout(BindingLayoutDesc desc);
    BindingLayoutHandle CreateBindingLayout(ReadOnlySpan<BindingSlotDesc> slots, string name = "");
    PipelineLayoutHandle CreatePipelineLayout(PipelineLayoutDesc desc);
    PipelineLayoutHandle CreatePipelineLayout(
        ReadOnlySpan<BindingLayoutHandle> bindingLayouts,
        ReadOnlySpan<PushRangeDesc> pushConstants = default,
        ReadOnlySpan<StaticSamplerDesc> staticSamplers = default,
        string name = "");
    BindingSetHandle CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc);
    BindingSetHandle CreateBindingSet(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        string name = "",
        BindingSetFlags flags = BindingSetFlags.None);
    void UpdateBindingSet(BindingSetHandle bindingSet, ReadOnlySpan<BindingResourceDesc> resources);
    PipelineHandle CreateComputePipeline(ComputePipelineDesc desc);
    PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDesc desc);
    PipelineHandle CreateGraphicsPipeline(
        GraphicsPipelineDesc desc,
        ReadOnlySpan<VertexLayoutDesc> vertexBuffers,
        ReadOnlySpan<VertexAttributeDesc> vertexAttributes,
        ReadOnlySpan<Format> colorFormats,
        ReadOnlySpan<BlendTargetDesc> blendTargets);
    PipelineHandle CreateMeshPipeline(MeshPipelineDesc desc);
    ICommandList CreateCommandList(CommandListDesc desc);
    FenceHandle CreateFence(string name, ulong initialValue = 0);
    SwapchainHandle CreateSwapchain(SwapchainDesc desc);
    ISwapchain GetSwapchain(SwapchainHandle handle);

    BufferDesc GetBufferDesc(BufferHandle buffer);
    TextureDesc GetTextureDesc(TextureHandle texture);
    TextureViewDesc GetTextureViewDesc(TextureViewHandle view);
    BufferViewDesc GetBufferViewDesc(BufferViewHandle view);
    bool TryGetTextureViewOwner(TextureViewHandle view, out TextureHandle texture, out TextureViewDesc desc);
    bool TryGetBufferViewOwner(BufferViewHandle view, out BufferHandle buffer, out BufferViewDesc desc);
    ResourceAllocationInfo GetBufferAlloc(BufferHandle buffer);
    ResourceAllocationInfo GetTextureAlloc(TextureHandle texture);
    ResourceState GetBufferState(BufferHandle buffer);
    ResourceState GetTextureState(TextureHandle texture, uint mipLevel = 0, uint arraySlice = 0);
    FormatSupport GetFormatSupport(Format format);
    FormatCapabilities GetFormatCapabilities(Format format);
    ulong GetFenceValue(FenceHandle fence);
    void WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue);
    Memory<byte> MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1);
    void FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes);
    void InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes);
    void UnmapBuffer(BufferHandle buffer);

    void Destroy(BufferHandle handle);
    void Destroy(TextureHandle handle);
    void Destroy(TextureViewHandle handle);
    void Destroy(BufferViewHandle handle);
    void Destroy(SamplerHandle handle);
    void Destroy(ShaderModuleHandle handle);
    void Destroy(BindingLayoutHandle handle);
    void Destroy(PipelineLayoutHandle handle);
    void Destroy(BindingSetHandle handle);
    void Destroy(PipelineHandle handle);
    void Destroy(PipelineCacheHandle handle);
    void Destroy(AccelerationStructureHandle handle);
    void Destroy(CommandBufferHandle handle);
    void Destroy(FenceHandle handle);
    void Destroy(QueryPoolHandle handle);
    void Destroy(SwapchainHandle handle);
    void Destroy(MemoryHeapHandle handle);

    void WaitIdle();
}

public interface IMemoryDevice
{
    ResourceMemoryRequirements GetBufferReqs(BufferDesc desc);
    ResourceMemoryRequirements GetTextureReqs(TextureDesc desc);
    MemoryHeapHandle CreateMemoryHeap(MemoryHeapDesc desc);
    MemoryHeapDesc GetHeapDesc(MemoryHeapHandle heap);
    MemoryBudget GetMemoryBudget(MemoryClass memory);
    BufferHandle CreatePlacedBuffer(MemoryHeapHandle heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> initialData = default);
    TextureHandle CreatePlacedTexture(MemoryHeapHandle heap, ulong offset, TextureDesc desc);
}

public interface ICacheDevice
{
    PipelineCacheHandle CreatePipelineCache(PipelineCacheDesc desc);
    byte[] GetPipelineData(PipelineCacheHandle cache);
}

public interface IRtDevice
{
    PipelineHandle CreateRtPipeline(RtPipelineDesc desc);
    int GetRtSize(PipelineHandle pipeline);
    void GetRtId(PipelineHandle pipeline, string shaderGroupName, Span<byte> destination);
    AccelBuildSizes GetAccelSizes(AccelBuildDesc desc);
    AccelerationStructureHandle CreateAccelerationStructure(AccelerationStructureDesc desc);
}

public interface IQueryDevice
{
    QueryPoolHandle CreateQueryPool(QueryPoolDesc desc);
}

public interface IQueue : IExtensible
{
    IDevice Device { get; }
    QueueType Type { get; }
    void Submit(
        ReadOnlySpan<CommandBufferHandle> commandBuffers,
        ReadOnlySpan<QueueWait> waits = default,
        ReadOnlySpan<QueueSignal> signals = default);
    void WaitIdle();
}

public interface ICommandList : IDisposable
{
    bool DebugMarkersEnabled { get; }

    void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default);
    void UavBarrier(TextureHandle texture);
    void UavBarrier(BufferHandle buffer);
    IRenderPass BeginRenderPass(in RenderPassDesc desc);
    IComputePass BeginComputePass(ComputePassDesc desc);
    IRtPass BeginRtPass(RtPassDesc desc);
    void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong byteCount);
    void CopyToTexture(BufferHandle source, BufferTextureCopy sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion);
    void CopyToBuffer(TextureHandle source, TextureCopyRegion sourceRegion, BufferHandle destination, BufferTextureCopy destinationRegion);
    void CopyTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion);
    void ResolveTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion);
    void BuildAccelerationStructure(AccelBuildDesc desc);
    void CopyAccelerationStructure(AccelerationStructureHandle source, AccelerationStructureHandle destination, AccelCopyMode mode);
    void WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex);
    void BeginQuery(QueryPoolHandle queryPool, uint queryIndex);
    void EndQuery(QueryPoolHandle queryPool, uint queryIndex);
    void ResolveQueryData(QueryPoolHandle queryPool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset);
    void PushDebugGroup(string name);
    void PopDebugGroup();
    void InsertDebugMarker(string name);
    CommandBufferHandle Finish();
}

public interface IRenderPass
{
    void SetViewport(Viewport viewport);
    void SetScissor(Rect rect);
    void SetPipeline(PipelineHandle pipeline);
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data);
    void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset = 0);
    void SetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset = 0);
    void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0);
    void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);
    void DrawIndirect(in IndirectDrawDesc desc);
    void DrawIndexedIndirect(in DrawIdxDesc desc);
    void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ);
    void DispatchMeshIndirect(in IndirectDispatchDesc desc);
    void End();
}

public interface IComputePass
{
    void SetPipeline(PipelineHandle pipeline);
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default);
    void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data);
    void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    void DispatchIndirect(in IndirectDispatchDesc desc);
    void End();
}

public interface IRtPass
{
    void SetPipeline(PipelineHandle pipeline);
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data);
    void TraceRays(in ShaderTableDesc shaderBindingTable, uint width, uint height, uint depth);
    void End();
}

public interface ISwapchain : IExtensible
{
    uint Width { get; }
    uint Height { get; }
    Format Format { get; }
    uint CurrentBackBufferIndex { get; }
    TextureHandle CurrentTexture { get; }
    TextureViewHandle CurrentRenderTargetView { get; }
    void Resize(uint width, uint height);
    void Present(in PresentDesc desc);
}
