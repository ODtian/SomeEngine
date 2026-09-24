namespace SomeEngine.Rhi.Backends.Null;

internal sealed class RenderPass(
    NullCommandList owner,
    NullDevice device,
    RenderPassCompatibility compatibility) : IRenderPass
{
    private bool _ended;
    private PipelineHandle _pipeline;
    private PipelineRecord? _pipelineRecord;
    private bool _hasIndexBuffer;

    public void SetViewport(Viewport viewport)
    {
        ThrowIfEnded();
        if (viewport.Width <= 0 || viewport.Height <= 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Viewport width and height must be positive.");
        if (viewport.MinDepth < 0 || viewport.MaxDepth > 1 || viewport.MinDepth > viewport.MaxDepth)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Viewport depth range must be within [0, 1] and ordered.");
    }

    public void SetScissor(Rect rect)
    {
        ThrowIfEnded();
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Scissor width and height must be positive.");
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        var record = device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.Graphics && record.Kind != PipelineKind.Mesh)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass requires a graphics or mesh pipeline.");
        NullPassValidation.ValidatePassCompatibility(record, compatibility);
        _pipeline = pipeline;
        _pipelineRecord = record;
        owner.AddOperation(NullCommandOperation.RenderPipelineDependency(pipeline, compatibility));
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var record = device.BindingSets.Get(bindingSet, "BindingSet");
        var expectedLayout = ExpectedBindingLayout(setIndex);
        if (record.LayoutSignature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Binding set layout is incompatible with the current pipeline set.");
        var layout = device.BindingLayouts.Get(record.Layout, "BindingLayout");
        var resources = record.Desc.Resources;
        device.ValidateDynamicOffsets(layout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, record.BindStates);
        owner.AddOperation(NullCommandOperation.BindingSetDependency(bindingSet));
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var expectedLayout = ExpectedBindingLayout(setIndex);
        var providedLayout = device.BindingLayouts.Get(layout, "BindingLayout");
        if (providedLayout.Signature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Transient binding layout is incompatible with the current pipeline set.");
        device.ValidateTransientBindings(layout, resources);
        device.ValidateDynamicOffsets(providedLayout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, device, resources);
        owner.AddTransientDep(layout, resources);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (stages == ShaderStageFlags.None || (stages & ~ShaderStageFlags.AllGraphics) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass push constants must target graphics shader stages.");
        NullPassValidation.ValidatePushConstants(device, _pipelineRecord!.Layout, stages, offset, data);
    }

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset = 0)
    {
        ThrowIfEnded();
        var record = device.Buffers.Get(buffer, "VertexBuffer");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.VertexBuffer))
            throw new RhiException(ErrorCode.InvalidDescriptor, "SetVertexBuffer requires a buffer with VertexBuffer bind flag.");
        if (offset >= record.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Vertex buffer offset is outside the buffer.");
        owner.RequireBufferState(buffer, ResourceState.VertexBuffer, "Vertex buffer binding");
        owner.AddOperation(NullCommandOperation.BufferDependency(buffer, ResourceState.VertexBuffer, "Vertex buffer binding"));
    }

    public void SetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset = 0)
    {
        ThrowIfEnded();
        var record = device.Buffers.Get(buffer, "IndexBuffer");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.IndexBuffer))
            throw new RhiException(ErrorCode.InvalidDescriptor, "SetIndexBuffer requires a buffer with IndexBuffer bind flag.");
        if (offset >= record.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Index buffer offset is outside the buffer.");
        owner.RequireBufferState(buffer, ResourceState.IndexBuffer, "Index buffer binding");
        owner.AddOperation(NullCommandOperation.BufferDependency(buffer, ResourceState.IndexBuffer, "Index buffer binding"));
        _hasIndexBuffer = true;
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireGraphicsPipeline("Draw");
        if (vertexCount == 0 || instanceCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Draw vertex count and instance count must be greater than zero.");
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireGraphicsPipeline("DrawIndexed");
        if (!_hasIndexBuffer)
            throw new RhiException(ErrorCode.ValidationFailure, "DrawIndexed requires an index buffer.");
        if (indexCount == 0 || instanceCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "DrawIndexed index count and instance count must be greater than zero.");
    }

    public void DrawIndirect(in IndirectDrawDesc desc)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireGraphicsPipeline("DrawIndirect");
        EnsureDrawIndirect(desc.Arguments, desc.ArgumentOffset, desc.DrawCount, desc.StrideInBytes, desc.CountBuffer, desc.CountBufferOffset, indexed: false);
    }

    public void DrawIndexedIndirect(in DrawIdxDesc desc)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireGraphicsPipeline("DrawIndexedIndirect");
        if (!_hasIndexBuffer)
            throw new RhiException(ErrorCode.ValidationFailure, "DrawIndexedIndirect requires an index buffer.");
        EnsureDrawIndirect(desc.Arguments, desc.ArgumentOffset, desc.DrawCount, desc.StrideInBytes, desc.CountBuffer, desc.CountBufferOffset, indexed: true);
    }

    public void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireMeshPipeline("DispatchMesh");
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "DispatchMesh group counts must all be greater than zero.");
    }

    public void DispatchMeshIndirect(in IndirectDispatchDesc desc)
    {
        ThrowIfEnded();
        RequirePipeline();
        RequireMeshPipeline("DispatchMeshIndirect");
        var validation = RhiCommandValidation.ValidateIndirectDispatch(device.Features, device.Limits, desc);
        var record = device.Buffers.Get(desc.Arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(record.Desc, desc.ArgumentOffset, validation.ArgumentByteCount, "Indirect mesh dispatch argument");
        owner.RequireBufferState(desc.Arguments, ResourceState.IndirectArgument, "Indirect mesh dispatch arguments");
        owner.AddOperation(NullCommandOperation.BufferDependency(desc.Arguments, ResourceState.IndirectArgument, "Indirect mesh dispatch arguments"));
        if (desc.CountBuffer.IsValid)
        {
            var countRecord = device.Buffers.Get(desc.CountBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countRecord.Desc, desc.CountBufferOffset);
            owner.RequireBufferState(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect mesh dispatch count");
            owner.AddOperation(NullCommandOperation.BufferDependency(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect mesh dispatch count"));
        }
    }

    public void End()
    {
        ThrowIfEnded();
        _ended = true;
        owner.ClosePass();
    }

    private void RequirePipeline()
    {
        if (!_pipeline.IsValid)
            throw new RhiException(ErrorCode.ValidationFailure, "A render pipeline must be set before drawing, dispatching, or binding resources.");
    }

    private void RequireGraphicsPipeline(string command)
    {
        if (_pipelineRecord!.Kind != PipelineKind.Graphics)
            throw new RhiException(ErrorCode.ValidationFailure, $"{command} requires a graphics pipeline.");
    }

    private void RequireMeshPipeline(string command)
    {
        if (_pipelineRecord!.Kind != PipelineKind.Mesh)
            throw new RhiException(ErrorCode.ValidationFailure, $"{command} requires a mesh pipeline.");
    }

    private void EnsureDrawIndirect(BufferHandle arguments, ulong offset, uint count, uint strideInBytes, BufferHandle countBuffer, ulong countBufferOffset, bool indexed)
    {
        var validation = RhiCommandValidation.ValidateIndirectDraw(
            device.Features,
            device.Limits,
            indexed,
            count,
            strideInBytes,
            offset,
            countBuffer.IsValid,
            countBufferOffset);
        var record = device.Buffers.Get(arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(record.Desc, offset, validation.ArgumentByteCount, "Indirect draw argument");
        owner.RequireBufferState(arguments, ResourceState.IndirectArgument, "Indirect draw arguments");
        owner.AddOperation(NullCommandOperation.BufferDependency(arguments, ResourceState.IndirectArgument, "Indirect draw arguments"));
        if (countBuffer.IsValid)
        {
            var countRecord = device.Buffers.Get(countBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countRecord.Desc, countBufferOffset);
            owner.RequireBufferState(countBuffer, ResourceState.IndirectArgument, "Indirect draw count");
            owner.AddOperation(NullCommandOperation.BufferDependency(countBuffer, ResourceState.IndirectArgument, "Indirect draw count"));
        }
    }

    private BindingLayoutRecord ExpectedBindingLayout(uint setIndex)
    {
        var pipelineLayout = device.PipelineLayouts.Get(_pipelineRecord!.Layout, "PipelineLayout").Desc;
        if (setIndex >= pipelineLayout.BindingLayouts.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline layout does not contain binding set {setIndex}.");
        return device.BindingLayouts.Get(pipelineLayout.BindingLayouts[(int)setIndex], "BindingLayout");
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Render pass list is already ended.");
        owner.CheckPass();
    }
}

internal sealed class ComputePass(NullCommandList owner, NullDevice device) : IComputePass
{
    private bool _ended;
    private PipelineHandle _pipeline;
    private PipelineRecord? _pipelineRecord;

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        var record = device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pass requires a compute pipeline.");
        _pipeline = pipeline;
        _pipelineRecord = record;
        owner.AddOperation(NullCommandOperation.PipelineDependency(pipeline));
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var record = device.BindingSets.Get(bindingSet, "BindingSet");
        var expectedLayout = ExpectedBindingLayout(setIndex);
        if (record.LayoutSignature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Binding set layout is incompatible with the current pipeline set.");
        var layout = device.BindingLayouts.Get(record.Layout, "BindingLayout");
        var resources = record.Desc.Resources;
        device.ValidateDynamicOffsets(layout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, record.BindStates);
        owner.AddOperation(NullCommandOperation.BindingSetDependency(bindingSet));
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var expectedLayout = ExpectedBindingLayout(setIndex);
        var providedLayout = device.BindingLayouts.Get(layout, "BindingLayout");
        if (providedLayout.Signature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Transient binding layout is incompatible with the current pipeline set.");
        device.ValidateTransientBindings(layout, resources);
        device.ValidateDynamicOffsets(providedLayout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, device, resources);
        owner.AddTransientDep(layout, resources);
    }

    public void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        ThrowIfEnded();
        owner.CheckOpenPass(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (stages != ShaderStageFlags.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pass push constants must target the compute shader stage.");
        NullPassValidation.ValidatePushConstants(device, _pipelineRecord!.Layout, stages, offset, data);
    }

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Dispatch group counts must all be greater than zero.");
    }

    public void DispatchIndirect(in IndirectDispatchDesc desc)
    {
        ThrowIfEnded();
        RequirePipeline();
        var validation = RhiCommandValidation.ValidateIndirectDispatch(device.Features, device.Limits, desc);
        var record = device.Buffers.Get(desc.Arguments, "IndirectArguments");
        RhiCommandValidation.ValidateArgsBuffer(record.Desc, desc.ArgumentOffset, validation.ArgumentByteCount, "Indirect dispatch argument");
        owner.RequireBufferState(desc.Arguments, ResourceState.IndirectArgument, "Indirect dispatch arguments");
        owner.AddOperation(NullCommandOperation.BufferDependency(desc.Arguments, ResourceState.IndirectArgument, "Indirect dispatch arguments"));
        if (desc.CountBuffer.IsValid)
        {
            var countRecord = device.Buffers.Get(desc.CountBuffer, "IndirectCountBuffer");
            RhiCommandValidation.ValidateCountBuffer(countRecord.Desc, desc.CountBufferOffset);
            owner.RequireBufferState(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect dispatch count");
            owner.AddOperation(NullCommandOperation.BufferDependency(desc.CountBuffer, ResourceState.IndirectArgument, "Indirect dispatch count"));
        }
    }

    public void End()
    {
        ThrowIfEnded();
        _ended = true;
        owner.ClosePass();
    }

    private void RequirePipeline()
    {
        if (!_pipeline.IsValid)
            throw new RhiException(ErrorCode.ValidationFailure, "A compute pipeline must be set before dispatching or binding resources.");
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Compute pass list is already ended.");
        owner.CheckPass();
    }

    private BindingLayoutRecord ExpectedBindingLayout(uint setIndex)
    {
        var pipelineLayout = device.PipelineLayouts.Get(_pipelineRecord!.Layout, "PipelineLayout").Desc;
        if (setIndex >= pipelineLayout.BindingLayouts.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline layout does not contain binding set {setIndex}.");
        return device.BindingLayouts.Get(pipelineLayout.BindingLayouts[(int)setIndex], "BindingLayout");
    }
}

internal sealed class RtPass(NullCommandList owner, NullDevice device) : IRtPass
{
    private bool _ended;
    private PipelineHandle _pipeline;
    private PipelineRecord? _pipelineRecord;

    public void SetPipeline(PipelineHandle pipeline)
    {
        ThrowIfEnded();
        var record = device.Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pass requires a ray tracing pipeline.");
        _pipeline = pipeline;
        _pipelineRecord = record;
        owner.AddOperation(NullCommandOperation.PipelineDependency(pipeline));
    }

    public void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var record = device.BindingSets.Get(bindingSet, "BindingSet");
        var expectedLayout = ExpectedBindingLayout(setIndex);
        if (record.LayoutSignature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Binding set layout is incompatible with the current pipeline set.");
        var layout = device.BindingLayouts.Get(record.Layout, "BindingLayout");
        var resources = record.Desc.Resources;
        device.ValidateDynamicOffsets(layout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, record.BindStates);
        owner.AddOperation(NullCommandOperation.BindingSetDependency(bindingSet));
    }

    public void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)
    {
        ThrowIfEnded();
        RequirePipeline();
        var expectedLayout = ExpectedBindingLayout(setIndex);
        var providedLayout = device.BindingLayouts.Get(layout, "BindingLayout");
        if (providedLayout.Signature != expectedLayout.Signature)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Transient binding layout is incompatible with the current pipeline set.");
        device.ValidateTransientBindings(layout, resources);
        device.ValidateDynamicOffsets(providedLayout, resources, dynamicOffsets);
        NullPassValidation.ValidateBindStates(owner, device, resources);
        owner.AddTransientDep(layout, resources);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (stages == ShaderStageFlags.None || (stages & ~ShaderStageFlags.AllRayTracing) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pass push constants must target ray tracing shader stages.");
        NullPassValidation.ValidatePushConstants(device, _pipelineRecord!.Layout, stages, offset, data);
    }

    public void TraceRays(in ShaderTableDesc shaderBindingTable, uint width, uint height, uint depth)
    {
        ThrowIfEnded();
        RequirePipeline();
        if (width == 0 || height == 0 || depth == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "TraceRays dimensions must all be greater than zero.");
        ValidateSbtRegion(shaderBindingTable.RayGeneration, singleRecord: true, required: true, "ray-generation shader record");
        ValidateSbtRegion(shaderBindingTable.Miss, singleRecord: false, required: false, "miss shader table");
        ValidateSbtRegion(shaderBindingTable.HitGroup, singleRecord: false, required: false, "hit-group shader table");
        ValidateSbtRegion(shaderBindingTable.Callable, singleRecord: false, required: false, "callable shader table");
    }

    public void End()
    {
        ThrowIfEnded();
        _ended = true;
        owner.ClosePass();
    }

    private void ValidateSbtRegion(ShaderTableRegion region, bool singleRecord, bool required, string label)
    {
        if (!region.Buffer.IsValid)
        {
            if (required)
                throw new RhiException(ErrorCode.InvalidHandle, $"{label} buffer handle is invalid.");
            if (region.Offset != 0 || region.SizeInBytes != 0 || region.StrideInBytes != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Empty {label} must have zero offset, size, and stride.");
            return;
        }

        var buffer = device.Buffers.Get(region.Buffer, label);
        if (!buffer.Desc.BindFlags.HasFlag(BindFlags.ShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} buffer requires ShaderResource bind flag.");
        if (region.Offset % device.Limits.RayTracingShaderTableAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} offset must be aligned to {device.Limits.RayTracingShaderTableAlignment} bytes.");
        if (region.SizeInBytes == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} size must be greater than zero.");
        if (singleRecord)
        {
            if (region.StrideInBytes != 0 && region.StrideInBytes != region.SizeInBytes)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} stride must be zero or equal to size.");
        }
        else if (region.StrideInBytes == 0
            || region.StrideInBytes % device.Limits.RayTracingShaderRecordAlignment != 0
            || region.StrideInBytes > device.Limits.MaxRayTracingShaderRecordStride)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} stride must be non-zero, aligned, and within device limits.");
        }

        RhiCommandValidation.ValidateBufferRange(buffer.Desc, region.Offset, region.SizeInBytes, label);
        owner.RequireBufferState(region.Buffer, ResourceState.ShaderResource, label);
        owner.AddOperation(NullCommandOperation.BufferDependency(region.Buffer, ResourceState.ShaderResource, label));
    }

    private void RequirePipeline()
    {
        if (!_pipeline.IsValid)
            throw new RhiException(ErrorCode.ValidationFailure, "A ray tracing pipeline must be set before tracing or binding resources.");
    }

    private void ThrowIfEnded()
    {
        if (_ended)
            throw new RhiException(ErrorCode.ValidationFailure, "Ray tracing pass list is already ended.");
        owner.CheckPass();
    }

    private BindingLayoutRecord ExpectedBindingLayout(uint setIndex)
    {
        var pipelineLayout = device.PipelineLayouts.Get(_pipelineRecord!.Layout, "PipelineLayout").Desc;
        if (setIndex >= pipelineLayout.BindingLayouts.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline layout does not contain binding set {setIndex}.");
        return device.BindingLayouts.Get(pipelineLayout.BindingLayouts[(int)setIndex], "BindingLayout");
    }
}

internal static class NullPassValidation
{
    public static void ValidatePassCompatibility(PipelineRecord pipeline, RenderPassCompatibility compatibility)
    {
        var colorFormats = pipeline.Kind == PipelineKind.Graphics
            ? pipeline.GraphicsDesc?.ColorFormats
            : pipeline.MeshDesc?.ColorFormats;
        var depthStencilFormat = pipeline.Kind == PipelineKind.Graphics
            ? pipeline.GraphicsDesc?.DepthStencilFormat
            : pipeline.MeshDesc?.DepthStencilFormat;
        var sampleCount = pipeline.Kind == PipelineKind.Graphics
            ? pipeline.GraphicsDesc?.SampleCount
            : pipeline.MeshDesc?.SampleCount;
        var depthWriteEnable = pipeline.Kind == PipelineKind.Graphics
            ? pipeline.GraphicsDesc?.DepthStencil.DepthWriteEnable
            : pipeline.MeshDesc?.DepthStencil.DepthWriteEnable;
        if (colorFormats == null || depthStencilFormat == null || sampleCount == null || depthWriteEnable == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass requires a graphics or mesh pipeline.");
        if (sampleCount.Value != compatibility.SampleCount)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline sample count {sampleCount.Value} does not match render pass sample count {compatibility.SampleCount}.");
        if (colorFormats.Count != compatibility.ColorCount)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline color target count {colorFormats.Count} does not match render pass color target count {compatibility.ColorCount}.");
        for (int index = 0; index < compatibility.ColorCount; index++)
        {
            var requiredFormat = compatibility.ColorFormatAt(index);
            if (colorFormats[index] != requiredFormat)
                throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline color format at index {index} is {colorFormats[index]}, render pass requires {requiredFormat}.");
        }
        if (depthStencilFormat.Value != compatibility.DepthStencilFormat)
            throw new RhiException(ErrorCode.ValidationFailure, $"Pipeline depth/stencil format {depthStencilFormat.Value} does not match render pass format {compatibility.DepthStencilFormat}.");
        if (compatibility.DepthReadOnly && depthWriteEnable.Value)
            throw new RhiException(ErrorCode.ValidationFailure, "A depth-writing pipeline cannot be bound to a depth-read-only render pass.");
    }

    public static void ValidateBindStates(NullCommandList owner, NullDevice device, BindingSetDesc desc)
        => ValidateBindStates(owner, device, desc.Resources);

    public static void ValidateBindStates(NullCommandList owner, ReadOnlySpan<BindState> states)
    {
        foreach (var bindState in states)
        {
            switch (bindState.Kind)
            {
                case BindStateKind.Buffer:
                    owner.RequireBufferState(bindState.Buffer, bindState.State, bindState.Label);
                    break;
                case BindStateKind.Texture:
                    owner.RequireTextureState(bindState.Texture, bindState.TextureRange, bindState.State, bindState.Label);
                    break;
                default:
                    throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding state kind {bindState.Kind} is not supported by Null backend.");
            }
        }
    }

    public static void ValidateBindStates(NullCommandList owner, NullDevice device, ReadOnlySpan<BindingResourceDesc> resources)
    {
        foreach (var resource in resources)
            ValidateBindState(owner, device, resource);
    }

    private static void ValidateBindState(NullCommandList owner, NullDevice device, BindingResourceDesc resource)
    {
        var state = BindRules.Resolve(resource.ResourceType);
        switch (state.Target)
        {
            case BindTarget.BufferView:
                RequireBufferViewState(owner, device, resource.BufferView, state.State, state.Label);
                break;
            case BindTarget.TextureView:
                RequireTextureViewState(owner, device, resource.TextureView, state.State, state.Label);
                break;
            case BindTarget.Sampler:
                break;
            case BindTarget.AccelerationStructure:
                device.AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                break;
            case BindTarget.None:
                break;
            default:
                throw new RhiException(
                    ErrorCode.UnsupportedFeature,
                    $"Binding state target {state.Target} is not supported by Null backend.");
        }
    }

    public static void ValidatePushConstants(
        NullDevice device,
        PipelineLayoutHandle layout,
        ShaderStageFlags stages,
        uint offset,
        ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant data must not be empty.");

        var pipelineLayout = device.PipelineLayouts.Get(layout, "PipelineLayout").Desc;
        foreach (var range in pipelineLayout.PushConstants)
        {
            if ((range.Stages & stages) != stages)
                continue;
            if (offset < range.Offset)
                continue;
            uint relativeOffset = offset - range.Offset;
            if (relativeOffset <= range.SizeInBytes && (uint)data.Length <= range.SizeInBytes - relativeOffset)
                return;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, "Push constant write is outside the pipeline layout ranges.");
    }

    private static void RequireBufferViewState(NullCommandList owner, NullDevice device, BufferViewHandle view, ResourceState state, string label)
    {
        var record = device.BufferViews.Get(view, "BufferView");
        owner.RequireBufferState(record.Buffer, state, label);
    }

    private static void RequireTextureViewState(NullCommandList owner, NullDevice device, TextureViewHandle view, ResourceState state, string label)
    {
        var record = device.TextureViews.Get(view, "TextureView");
        owner.RequireTextureState(record.Texture, record.Desc, state, label);
    }
}
