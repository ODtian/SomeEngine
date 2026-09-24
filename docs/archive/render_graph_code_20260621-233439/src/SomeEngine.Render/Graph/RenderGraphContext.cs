using System.Collections.Generic;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using RhiRenderPass = SomeEngine.Rhi.IRenderPass;

namespace SomeEngine.Render.Graph;

internal sealed class GraphFrameExecution(
    RenderGraph graph,
    IDevice device,
    ICommandList list)
{
    internal IDevice Device => device;

    internal TextureHandle GetTexture(RenderGraphHandle handle)
        => graph.GetTexture(handle);

    internal BufferHandle GetBuffer(RenderGraphHandle handle)
        => graph.GetBuffer(handle);

    internal TextureViewHandle GetTextureView(RenderGraphHandle handle, TextureViewDesc desc)
        => graph.GetTextureView(device, handle, desc);

    internal BufferViewHandle GetBufferView(RenderGraphHandle handle, BufferViewDesc desc)
        => graph.GetBufferView(device, handle, desc);

    internal BindingSetHandle GetBindingSet(
        PassParameters parameters,
        string name = "")
    {
        return graph.GetBindingSet(device, parameters, name);
    }

    internal BindingSetHandle GetBindingSet(
        PassBindings bindings,
        string name = "")
    {
        return graph.GetBindingSet(device, bindings, name);
    }

    internal void TrackPipeline(PipelineHandle pipeline)
        => graph.TrackPipeline(pipeline);

    internal PipelineHandle GetPipeline(PipelineTicket ticket, PipelineNeed need, string site)
        => graph.GetPipeline(ticket, need, site);

    internal RhiRenderPass BeginRenderPass(in RenderPassDesc desc)
        => list.BeginRenderPass(desc);

    internal void CopyBuffer(
        RenderGraphHandle source,
        ulong sourceOffset,
        RenderGraphHandle destination,
        ulong destinationOffset,
        ulong byteCount)
    {
        list.CopyBuffer(
            graph.GetBuffer(source),
            sourceOffset,
            graph.GetBuffer(destination),
            destinationOffset,
            byteCount);
    }

    internal void CopyToBuffer(
        RenderGraphHandle source,
        TextureCopyRegion sourceRegion,
        RenderGraphHandle destination,
        BufferTextureCopy destinationRegion)
    {
        list.CopyToBuffer(
            graph.GetTexture(source),
            sourceRegion,
            graph.GetBuffer(destination),
            destinationRegion);
    }

    internal void CopyTexture(
        RenderGraphHandle source,
        TextureCopyRegion sourceRegion,
        RenderGraphHandle destination,
        TextureCopyRegion destinationRegion)
    {
        list.CopyTexture(
            graph.GetTexture(source),
            sourceRegion,
            graph.GetTexture(destination),
            destinationRegion);
    }
}

public sealed class RenderGraphContext
{
    private readonly RenderGraph _graph;
    private readonly GraphFrameExecution _execution;
    private readonly Dictionary<RenderGraphHandle, ResourceState> _bufferRuntimeStates = new();
    private RenderPassCommands? _activeRenderPass;
    private int _passIndex = -1;
    private RenderGraph.PassMode _passMode;

    internal RenderGraphContext(RenderGraph graph, GraphFrameExecution execution)
    {
        _graph = graph;
        _execution = execution;
    }

    internal IDevice Device => _execution.Device;

    internal void BeginPass(int passIndex, RenderGraph.PassMode mode)
    {
        _passIndex = passIndex;
        _passMode = mode;
        _bufferRuntimeStates.Clear();
    }

    internal void EndPass()
    {
        RenderPassCommands? activeRenderPass = _activeRenderPass;
        InvalidOperationException? missingEnd = activeRenderPass == null
            ? null
            : new InvalidOperationException(
                $"RenderGraph pass '{_graph.PassName(_passIndex)}' returned with an active render pass. Call {nameof(RenderPassCommands.End)} before the pass callback returns.");
        try
        {
            activeRenderPass?.End();
        }
        finally
        {
            _activeRenderPass?.Invalidate();
            _activeRenderPass = null;
            _passIndex = -1;
            _passMode = RenderGraph.PassMode.Command;
            _bufferRuntimeStates.Clear();
        }

        if (missingEnd != null)
            throw missingEnd;
    }

    internal int GraphGeneration => _graph.Generation;
    internal int GraphId => _graph.GraphId;

    internal TextureHandle GetTexture(RenderGraphHandle handle)
    {
        ValidateTexture(handle, nameof(GetTexture));
        return _execution.GetTexture(handle);
    }

    internal TextureHandle GetTexture(
        RenderGraphHandle handle,
        ResourceState state,
        RenderGraphAccess access,
        string operation)
    {
        ValidateTexture(handle, operation, state, access);
        return _execution.GetTexture(handle);
    }

    internal TextureHandle GetTexture(
        RenderGraphHandle handle,
        ResourceState state,
        RenderGraphAccess access,
        SubResourceRange range,
        string operation)
    {
        ValidateTexture(handle, operation, state, access, range);
        return _execution.GetTexture(handle);
    }

    internal BufferHandle GetBuffer(RenderGraphHandle handle)
    {
        ValidateBuffer(handle, nameof(GetBuffer));
        return _execution.GetBuffer(handle);
    }

    internal BufferHandle GetBuffer(
        RenderGraphHandle handle,
        ResourceState state,
        RenderGraphAccess access,
        string operation)
    {
        ValidateBuffer(handle, operation, state, access);
        TrackBufferState(handle, state);
        return _execution.GetBuffer(handle);
    }

    internal BufferHandle GetIndirectCountBufferOrDefault(RenderGraphHandle handle, string operation)
        => handle.IsValid
            ? GetBuffer(handle, ResourceState.IndirectArgument, RenderGraphAccess.ReadOnly, operation)
            : default;

    public TextureDesc GetTextureDesc(RenderGraphHandle handle)
    {
        ValidateTexture(handle, nameof(GetTextureDesc));
        return _graph.GetTextureDesc(handle);
    }

    public BufferDesc GetBufferDesc(RenderGraphHandle handle)
    {
        ValidateBuffer(handle, nameof(GetBufferDesc));
        return _graph.GetBufferDesc(handle);
    }

    public TextureViewHandle GetTextureView(
        RenderGraphHandle handle,
        ViewKind kind,
        Format format = Format.Unknown,
        TextureViewDimension dimension = TextureViewDimension.Texture2D,
        uint firstMip = 0,
        uint mipCount = 1,
        uint firstSlice = 0,
        uint sliceCount = 1,
        bool depthReadOnly = false)
    {
        RequireBindingPass(nameof(GetTextureView));
        SubResourceRange range = new(firstMip, mipCount, firstSlice, sliceCount);
        ResourceState state = kind == ViewKind.DepthStencil && depthReadOnly
            ? ResourceState.DepthRead
            : ViewState(kind);
        RenderGraphAccess access = kind == ViewKind.DepthStencil && depthReadOnly
            ? RenderGraphAccess.ReadOnly
            : ViewAccess(kind);
        ValidateTexture(handle, nameof(GetTextureView), state, access, range);
        var desc = _graph.GetTextureDesc(handle);
        return _execution.GetTextureView(
            handle,
            new TextureViewDesc
            {
                Kind = kind,
                Dimension = dimension,
                Format = format == Format.Unknown ? desc.Format : format,
                FirstMip = firstMip,
                MipCount = mipCount,
                FirstSlice = firstSlice,
                SliceCount = sliceCount,
            });
    }

    public BufferViewHandle GetBufferView(
        RenderGraphHandle handle,
        ViewKind kind,
        bool raw = false)
    {
        RequireBindingPass(nameof(GetBufferView));
        ValidateBuffer(handle, nameof(GetBufferView), ViewState(kind), ViewAccess(kind));
        var desc = _graph.GetBufferDesc(handle);
        return _execution.GetBufferView(
            handle,
            new BufferViewDesc
            {
                Kind = kind,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                StrideInBytes = raw ? 0 : desc.StrideInBytes,
                Raw = raw,
            });
    }

    public BufferViewHandle GetBufferView(
        RenderGraphHandle handle,
        ViewKind kind,
        ulong offset,
        ulong sizeInBytes,
        Format format = Format.Unknown,
        uint strideInBytes = 0,
        bool raw = false)
    {
        RequireBindingPass(nameof(GetBufferView));
        ValidateBuffer(handle, nameof(GetBufferView), ViewState(kind), ViewAccess(kind));
        return _execution.GetBufferView(
            handle,
            new BufferViewDesc
            {
                Kind = kind,
                Offset = offset,
                SizeInBytes = sizeInBytes,
                Format = format,
                StrideInBytes = strideInBytes,
                Raw = raw,
            });
    }

    public RenderPassCommands BeginRenderPass(in RenderPassDesc desc)
    {
        RequireDrawPass(nameof(BeginRenderPass));
        if (_activeRenderPass != null)
            throw new InvalidOperationException($"RenderGraph pass '{_graph.PassName(_passIndex)}' already has an active render pass.");
        ValidateRenderPass(desc);
        var commands = new RenderPassCommands(this, _execution, _execution.BeginRenderPass(desc));
        _activeRenderPass = commands;
        return commands;
    }

    public PassBindings Bindings(BindingLayoutHandle layout)
    {
        RequireBindingPass(nameof(Bindings));
        return new PassBindings(this, layout);
    }

    internal PipelineHandle GetPipeline(PipelineTicket ticket, PipelineNeed need = PipelineNeed.Required)
    {
        RequireActive(nameof(GetPipeline));
        return _execution.GetPipeline(ticket, need, PipelineSite());
    }

    public void CopyBuffer(
        RenderGraphHandle source,
        ulong sourceOffset,
        RenderGraphHandle destination,
        ulong destinationOffset,
        ulong byteCount)
    {
        RequireCopyPass(nameof(CopyBuffer));
        ValidateBuffer(source, nameof(CopyBuffer), ResourceState.CopySource, RenderGraphAccess.ReadOnly);
        ValidateBuffer(destination, nameof(CopyBuffer), ResourceState.CopyDestination, RenderGraphAccess.WriteOnly);
        TrackBufferState(source, ResourceState.CopySource);
        TrackBufferState(destination, ResourceState.CopyDestination);
        _execution.CopyBuffer(source, sourceOffset, destination, destinationOffset, byteCount);
    }

    public void CopyToBuffer(
        RenderGraphHandle source,
        TextureCopyRegion sourceRegion,
        RenderGraphHandle destination,
        BufferTextureCopy destinationRegion)
    {
        RequireCopyPass(nameof(CopyToBuffer));
        ValidateTexture(
            source,
            nameof(CopyToBuffer),
            ResourceState.CopySource,
            RenderGraphAccess.ReadOnly,
            new SubResourceRange(sourceRegion.MipLevel, 1, sourceRegion.ArraySlice, 1));
        ValidateBuffer(destination, nameof(CopyToBuffer), ResourceState.CopyDestination, RenderGraphAccess.WriteOnly);
        TrackBufferState(destination, ResourceState.CopyDestination);
        _execution.CopyToBuffer(source, sourceRegion, destination, destinationRegion);
    }

    public void CopyTexture(
        RenderGraphHandle source,
        TextureCopyRegion sourceRegion,
        RenderGraphHandle destination,
        TextureCopyRegion destinationRegion)
    {
        RequireCopyPass(nameof(CopyTexture));
        ValidateTexture(
            source,
            nameof(CopyTexture),
            ResourceState.CopySource,
            RenderGraphAccess.ReadOnly,
            new SubResourceRange(sourceRegion.MipLevel, 1, sourceRegion.ArraySlice, 1));
        ValidateTexture(
            destination,
            nameof(CopyTexture),
            ResourceState.CopyDestination,
            RenderGraphAccess.WriteOnly,
            new SubResourceRange(destinationRegion.MipLevel, 1, destinationRegion.ArraySlice, 1));
        _execution.CopyTexture(source, sourceRegion, destination, destinationRegion);
    }

    internal ResourceKind GetResourceKind(RenderGraphHandle handle, string operation)
    {
        RequireActive(operation);
        return _graph.GetResourceKind(handle, operation);
    }

    private void ValidateTexture(RenderGraphHandle handle, string operation)
    {
        RequireActive(operation);
        _graph.ValidatePassAccess(handle, _passIndex, ResourceKind.Texture, operation);
    }

    private void ValidateBuffer(RenderGraphHandle handle, string operation)
    {
        RequireActive(operation);
        _graph.ValidatePassAccess(handle, _passIndex, ResourceKind.Buffer, operation);
    }

    private void ValidateTexture(
        RenderGraphHandle handle,
        string operation,
        ResourceState state,
        RenderGraphAccess access)
    {
        RequireActive(operation);
        _graph.ValidatePassAccess(handle, _passIndex, ResourceKind.Texture, state, access, operation);
    }

    private void ValidateTexture(
        RenderGraphHandle handle,
        string operation,
        SubResourceRange range)
    {
        RequireActive(operation);
        _graph.ValidatePassRange(handle, _passIndex, ResourceKind.Texture, range, operation);
    }

    private void ValidateTexture(
        RenderGraphHandle handle,
        string operation,
        ResourceState state,
        RenderGraphAccess access,
        SubResourceRange range)
    {
        RequireActive(operation);
        _graph.ValidatePassAccess(handle, _passIndex, ResourceKind.Texture, state, access, range, operation);
    }

    private void ValidateBuffer(
        RenderGraphHandle handle,
        string operation,
        ResourceState state,
        RenderGraphAccess access)
    {
        RequireActive(operation);
        _graph.ValidatePassAccess(
            handle,
            _passIndex,
            ResourceKind.Buffer,
            state,
            access,
            operation,
            _bufferRuntimeStates.TryGetValue(handle, out ResourceState current) ? current : null);
    }

    private void TrackBufferState(RenderGraphHandle handle, ResourceState state)
    {
        if (_bufferRuntimeStates.TryGetValue(handle, out ResourceState current))
        {
            _bufferRuntimeStates[handle] = MergeBufferRuntimeState(current, state);
            return;
        }

        _bufferRuntimeStates[handle] = state;
    }

    private static ResourceState MergeBufferRuntimeState(ResourceState current, ResourceState next)
    {
        if (current == next)
            return current;

        bool currentRead = IsGenericReadState(current);
        bool nextRead = IsGenericReadState(next);
        if (currentRead && nextRead)
            return ResourceState.GenericRead;
        if (currentRead)
            return next;
        if (nextRead)
            return current;

        return next;
    }

    private static bool IsGenericReadState(ResourceState state)
        => state is ResourceState.GenericRead
            or ResourceState.VertexBuffer
            or ResourceState.IndexBuffer
            or ResourceState.ConstantBuffer
            or ResourceState.ShaderResource
            or ResourceState.CopySource
            or ResourceState.IndirectArgument;

    internal void ValidateBufferTransition(
        RenderGraphHandle handle,
        ResourceState before,
        ResourceState after,
        string operation)
    {
        ResourceState? current = _bufferRuntimeStates.TryGetValue(handle, out ResourceState currentState)
            ? currentState
            : null;
        _graph.ValidatePassTransition(
            handle,
            _passIndex,
            ResourceKind.Buffer,
            before,
            after,
            RenderGraph.StateAccess(before) | RenderGraph.StateAccess(after),
            operation,
            current);
        _bufferRuntimeStates[handle] = after;
    }

    private static ResourceState ViewState(ViewKind kind)
        => kind switch
        {
            ViewKind.ConstantBuffer => ResourceState.ConstantBuffer,
            ViewKind.ShaderResource => ResourceState.ShaderResource,
            ViewKind.UnorderedAccess => ResourceState.UnorderedAccess,
            ViewKind.RenderTarget => ResourceState.RenderTarget,
            ViewKind.DepthStencil => ResourceState.DepthWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static RenderGraphAccess ViewAccess(ViewKind kind)
        => kind switch
        {
            ViewKind.ConstantBuffer or ViewKind.ShaderResource => RenderGraphAccess.ReadOnly,
            ViewKind.UnorderedAccess or ViewKind.RenderTarget or ViewKind.DepthStencil => RenderGraphAccess.WriteOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    internal void ValidateTextureView(TextureViewHandle view, ViewKind kind, ResourceState state, RenderGraphAccess access, string operation)
    {
        RequireActive(operation);
        _graph.ValidateTextureView(_passIndex, view, kind, state, access, operation);
    }

    internal RenderGraphHandle ValidateBufferView(BufferViewHandle view, ViewKind kind, ResourceState state, RenderGraphAccess access, string operation)
    {
        RequireActive(operation);
        return _graph.ValidateBufferView(_passIndex, view, kind, state, access, operation);
    }

    internal void ValidateBindingResources(
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<RenderGraphAccess> accessOverrides,
        string operation)
    {
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            BindingResourceDesc resource = resources[resourceIndex];
            RenderGraphAccess access = resourceIndex < accessOverrides.Length
                ? accessOverrides[resourceIndex]
                : PassBindings.BindingAccess(resource.ResourceType);
            if (resource.TextureView.IsValid)
            {
                ValidateTextureView(resource.TextureView, PassBindings.TextureKind(resource.ResourceType), BindRules.Resolve(resource.ResourceType).State, access, operation);
                continue;
            }

            if (resource.BufferView.IsValid)
            {
                ResourceState state = BindRules.Resolve(resource.ResourceType).State;
                RenderGraphHandle handle = ValidateBufferView(resource.BufferView, PassBindings.BufferKind(resource.ResourceType), state, access, operation);
                TrackBufferState(handle, state);
            }
        }
    }

    internal void ValidateParameterLifetime(PassParameters parameters, string operation)
    {
        if (parameters.GraphGeneration < 0)
        {
            throw new InvalidOperationException(
                $"RenderGraph {operation} requires parameter packets built through RenderGraphContext.Bindings on the active frame.");
        }

        if (parameters.GraphId != GraphId || parameters.GraphGeneration != GraphGeneration)
        {
            throw new InvalidOperationException(
                $"RenderGraph {operation} cannot reuse parameter packets across frames when they were built from graph-owned resources.");
        }
    }

    internal void ValidateParameterLifetime(PassBindings bindings, string operation)
    {
        if (bindings.GraphId != GraphId || bindings.GraphGeneration != GraphGeneration)
        {
            throw new InvalidOperationException(
                $"RenderGraph {operation} cannot reuse binding packets across frames when they were built from graph-owned resources.");
        }
    }

    internal TextureViewHandle GetTextureView(
        RenderGraphHandle handle,
        TextureViewDesc desc,
        RenderGraphAccess access,
        string operation)
    {
        SubResourceRange range = new(desc.FirstMip, desc.MipCount, desc.FirstSlice, desc.SliceCount);
        ValidateTexture(handle, operation, ViewState(desc.Kind), access, range);
        return _execution.GetTextureView(handle, desc);
    }

    internal BufferViewHandle GetBufferView(
        RenderGraphHandle handle,
        BufferViewDesc desc,
        RenderGraphAccess access,
        string operation)
    {
        ValidateBuffer(handle, operation, ViewState(desc.Kind), access);
        return _execution.GetBufferView(handle, desc);
    }

    private void ValidateRenderPass(RenderPassDesc desc)
    {
        foreach (ColorAttachmentDesc attachment in desc.ColorAttachments)
        {
            RenderGraphAccess access = attachment.LoadOp == LoadOp.Load
                ? RenderGraphAccess.ReadWrite
                : RenderGraphAccess.WriteOnly;
            _graph.ValidateTextureView(_passIndex, attachment.View, ViewKind.RenderTarget, ResourceState.RenderTarget, access, nameof(BeginRenderPass));
            if (attachment.ResolveTarget.IsValid)
            {
                _graph.ValidateTextureView(_passIndex, attachment.ResolveTarget, ViewKind.RenderTarget, ResourceState.RenderTarget, RenderGraphAccess.WriteOnly, nameof(BeginRenderPass));
            }
        }

        if (desc.DepthStencilAttachment is { } depthAttachment)
        {
            if (depthAttachment.DepthReadOnly && depthAttachment.DepthLoadOp == LoadOp.Clear)
            {
                throw new InvalidOperationException(
                    "RenderGraph depth read-only attachments cannot use LoadOp.Clear. Clear requires a writable depth attachment declaration.");
            }

            RenderGraphAccess access = depthAttachment.DepthReadOnly || depthAttachment.DepthLoadOp == LoadOp.Load
                ? RenderGraphAccess.ReadWrite
                : RenderGraphAccess.WriteOnly;
            if (depthAttachment.DepthReadOnly)
                access = RenderGraphAccess.ReadOnly;
            _graph.ValidateTextureView(
                _passIndex,
                depthAttachment.View,
                ViewKind.DepthStencil,
                depthAttachment.DepthReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite,
                access,
                nameof(BeginRenderPass));
        }
    }

    private void RequireDrawPass(string operation)
    {
        RequireActive(operation);
        if (_passMode == RenderGraph.PassMode.Command)
            return;

        string passKind = PassKind(_passMode);
        throw new InvalidOperationException(
            $"RenderGraph {operation} cannot be used by {passKind} pass '{_graph.PassName(_passIndex)}'. {passKind} passes cannot issue graphics commands.");
    }

    private void RequireBindingPass(string operation)
    {
        RequireActive(operation);
        if (_passMode is not RenderGraph.PassMode.Copy and not RenderGraph.PassMode.AsyncCopy)
            return;

        throw new InvalidOperationException(
            $"RenderGraph {operation} cannot be used by copy pass '{_graph.PassName(_passIndex)}'. Copy passes may only query resource descriptions and issue copy commands for declared resources.");
    }

    private void RequireCopyPass(string operation)
    {
        RequireActive(operation);
        if (_passMode is RenderGraph.PassMode.Copy or RenderGraph.PassMode.AsyncCopy)
            return;

        string passKind = _passMode == RenderGraph.PassMode.Command ? "graphics" : "compute";
        throw new InvalidOperationException(
            $"RenderGraph {operation} requires a copy pass. Use AddCopyPass for copy commands instead of {passKind} pass '{_graph.PassName(_passIndex)}'.");
    }

    private void RequireActive(string operation)
    {
        if (_passIndex >= 0)
            return;

        throw new InvalidOperationException(
            $"RenderGraph {operation} requires an active RenderGraph pass callback.");
    }

    private static string PassKind(RenderGraph.PassMode mode)
        => mode is RenderGraph.PassMode.Copy or RenderGraph.PassMode.AsyncCopy ? "copy" : "compute";

    private string PipelineSite()
        => _passIndex >= 0 ? _graph.PassName(_passIndex) : string.Empty;

    internal void EndRenderPass(RenderPassCommands commands)
    {
        if (ReferenceEquals(_activeRenderPass, commands))
            _activeRenderPass = null;
    }
}

public sealed class RenderPassCommands : IParameterSink
{
    private readonly RenderGraphContext _context;
    private readonly GraphFrameExecution _execution;
    private readonly RhiRenderPass _inner;
    private bool _active = true;

    internal RenderPassCommands(
        RenderGraphContext context,
        GraphFrameExecution execution,
        RhiRenderPass inner)
    {
        _context = context;
        _execution = execution;
        _inner = inner;
    }

    public void SetViewport(Viewport viewport)
    {
        RequireActive();
        _inner.SetViewport(viewport);
    }

    public void SetScissor(Rect rect)
    {
        RequireActive();
        _inner.SetScissor(rect);
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        RequireActive();
        _execution.TrackPipeline(pipeline);
        _inner.SetPipeline(pipeline);
    }

    public void SetParameters(uint setIndex, PassParameters parameters)
    {
        RequireActive();
        _context.ValidateParameterLifetime(parameters, nameof(SetParameters));
        _context.ValidateBindingResources(parameters.ResourceSpan, parameters.ResourceAccessSpan, nameof(SetParameters));
        var bindingSet = _execution.GetBindingSet(parameters);
        _inner.SetBindingSet(setIndex, bindingSet);
    }

    public void SetParameters(uint setIndex, PassBindings bindings)
    {
        RequireActive();
        _context.ValidateParameterLifetime(bindings, nameof(SetParameters));
        _context.ValidateBindingResources(bindings.ResourceSpan, bindings.ResourceAccessSpan, nameof(SetParameters));
        var bindingSet = _execution.GetBindingSet(bindings);
        _inner.SetBindingSet(setIndex, bindingSet);
    }

    public void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)
    {
        RequireActive();
        _inner.SetPushConstants(stages, offset, data);
    }

    public void SetVertexBuffer(uint slot, RenderGraphHandle buffer, ulong offset = 0)
    {
        RequireActive();
        _inner.SetVertexBuffer(
            slot,
            _context.GetBuffer(
                buffer,
                ResourceState.VertexBuffer,
                RenderGraphAccess.ReadOnly,
                nameof(SetVertexBuffer)),
            offset);
    }

    public void SetIndexBuffer(RenderGraphHandle buffer, IndexFormat format, ulong offset = 0)
    {
        RequireActive();
        _inner.SetIndexBuffer(
            _context.GetBuffer(
                buffer,
                ResourceState.IndexBuffer,
                RenderGraphAccess.ReadOnly,
                nameof(SetIndexBuffer)),
            format,
            offset);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        RequireActive();
        _inner.Draw(vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        RequireActive();
        _inner.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public void DrawIndirect(
        RenderGraphHandle arguments,
        ulong argumentOffset = 0,
        uint drawCount = 1,
        uint strideInBytes = IndirectArgumentSize.Draw,
        RenderGraphHandle countBuffer = default,
        ulong countBufferOffset = 0)
    {
        RequireActive();
        _inner.DrawIndirect(new IndirectDrawDesc
        {
            Arguments = _context.GetBuffer(
                arguments,
                ResourceState.IndirectArgument,
                RenderGraphAccess.ReadOnly,
                nameof(DrawIndirect)),
            ArgumentOffset = argumentOffset,
            DrawCount = drawCount,
            StrideInBytes = strideInBytes,
            CountBuffer = _context.GetIndirectCountBufferOrDefault(countBuffer, nameof(DrawIndirect)),
            CountBufferOffset = countBufferOffset,
        });
    }

    public void DrawIndexedIndirect(
        RenderGraphHandle arguments,
        ulong argumentOffset = 0,
        uint drawCount = 1,
        uint strideInBytes = IndirectArgumentSize.DrawIndexed,
        RenderGraphHandle countBuffer = default,
        ulong countBufferOffset = 0)
    {
        RequireActive();
        _inner.DrawIndexedIndirect(new DrawIdxDesc
        {
            Arguments = _context.GetBuffer(
                arguments,
                ResourceState.IndirectArgument,
                RenderGraphAccess.ReadOnly,
                nameof(DrawIndexedIndirect)),
            ArgumentOffset = argumentOffset,
            DrawCount = drawCount,
            StrideInBytes = strideInBytes,
            CountBuffer = _context.GetIndirectCountBufferOrDefault(countBuffer, nameof(DrawIndexedIndirect)),
            CountBufferOffset = countBufferOffset,
        });
    }

    public void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        RequireActive();
        _inner.DispatchMesh(groupCountX, groupCountY, groupCountZ);
    }

    public void DispatchMeshIndirect(
        RenderGraphHandle arguments,
        ulong argumentOffset = 0,
        uint dispatchCount = 1,
        uint strideInBytes = IndirectArgumentSize.Dispatch,
        RenderGraphHandle countBuffer = default,
        ulong countBufferOffset = 0)
    {
        RequireActive();
        _inner.DispatchMeshIndirect(new IndirectDispatchDesc
        {
            Arguments = _context.GetBuffer(
                arguments,
                ResourceState.IndirectArgument,
                RenderGraphAccess.ReadOnly,
                nameof(DispatchMeshIndirect)),
            ArgumentOffset = argumentOffset,
            DispatchCount = dispatchCount,
            StrideInBytes = strideInBytes,
            CountBuffer = _context.GetIndirectCountBufferOrDefault(countBuffer, nameof(DispatchMeshIndirect)),
            CountBufferOffset = countBufferOffset,
        });
    }

    public void End()
    {
        RequireActive();
        try
        {
            _inner.End();
        }
        finally
        {
            Invalidate();
            _context.EndRenderPass(this);
        }
    }

    internal void Invalidate()
        => _active = false;

    private void RequireActive()
    {
        if (!_active)
            throw new InvalidOperationException("RenderPassCommands cannot be used after the render pass has ended or the RenderGraph pass callback has returned.");
    }
}
