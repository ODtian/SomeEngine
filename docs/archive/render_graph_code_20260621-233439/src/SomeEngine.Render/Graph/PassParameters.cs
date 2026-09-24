using SomeEngine.Core.Collections;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public readonly struct PassParameters
{
    private readonly BindingResourceDesc[]? _resources;
    private readonly RenderGraphAccess[]? _resourceAccesses;
    internal int GraphId { get; }
    internal int GraphGeneration { get; }

    internal PassParameters(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)
        : this(layout, resources.ToArray(), DefaultAccesses(resources), BindingHash.Compute(layout, resources), -1, -1)
    {
    }

    private PassParameters(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        RenderGraphAccess[] resourceAccesses,
        int graphId,
        int graphGeneration)
        : this(layout, resources, resourceAccesses, BindingHash.Compute(layout, resources), graphId, graphGeneration)
    {
    }

    internal PassParameters(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        RenderGraphAccess[] resourceAccesses,
        int hash,
        int graphId,
        int graphGeneration)
    {
        Layout = layout;
        _resources = resources.Length == 0 ? Array.Empty<BindingResourceDesc>() : resources;
        _resourceAccesses = resourceAccesses.Length == 0 ? Array.Empty<RenderGraphAccess>() : resourceAccesses;
        Hash = hash;
        GraphId = graphId;
        GraphGeneration = graphGeneration;
    }

    public BindingLayoutHandle Layout { get; }
    public int Hash { get; }

    internal ReadOnlySpan<BindingResourceDesc> ResourceSpan => _resources ?? Array.Empty<BindingResourceDesc>();
    internal ReadOnlySpan<RenderGraphAccess> ResourceAccessSpan => _resourceAccesses ?? Array.Empty<RenderGraphAccess>();
    internal BindingResourceDesc[] OwnedResources => _resources ?? Array.Empty<BindingResourceDesc>();

    private static RenderGraphAccess[] DefaultAccesses(ReadOnlySpan<BindingResourceDesc> resources)
    {
        if (resources.IsEmpty)
            return Array.Empty<RenderGraphAccess>();

        var accesses = new RenderGraphAccess[resources.Length];
        for (int i = 0; i < resources.Length; i++)
            accesses[i] = PassBindings.BindingAccess(resources[i].ResourceType);
        return accesses;
    }
}

public sealed class PassBindings
{
    private readonly RenderGraphContext _context;
    private readonly BindingLayoutHandle _layout;
    private readonly int _graphId;
    private readonly int _graphGeneration;
    private SmallList<BindingResourceDesc> _resources;
    private SmallList<RenderGraphAccess> _accesses;
    private HashCode _hash;

    internal PassBindings(RenderGraphContext context, BindingLayoutHandle layout)
    {
        _context = context;
        _layout = layout;
        _graphId = context.GraphId;
        _graphGeneration = context.GraphGeneration;
        _resources = default;
        _accesses = default;
        _hash = BindingHash.Begin(layout);
    }

    public int Count
    {
        get
        {
            EnsureValid();
            return _resources.Count;
        }
    }

    public PassBindings Reserve(int count)
    {
        EnsureValid();
        _resources.EnsureCapacity(count);
        return this;
    }

    public PassBindings Clear(uint binding, uint arrayElement = 0)
    {
        Add(BindingResourceDesc.Clear(binding, arrayElement));
        return this;
    }

    public PassBindings Buffer(
        uint binding,
        RenderGraphHandle handle,
        BindingType type = BindingType.StorageBufferRead,
        uint arrayElement = 0)
        => Buffer(binding, handle, type, BindingAccess(type), arrayElement);

    public PassBindings Buffer(
        uint binding,
        RenderGraphHandle handle,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (!IsBuffer(type))
            throw new ArgumentException($"Binding type {type} is not a buffer.", nameof(type));
        ValidateAccessOverride(type, access);

        RenderGraphContext context = Context;
        ViewKind kind = BufferKind(type);
        bool raw = type is BindingType.RawBufferRead or BindingType.RawBufferReadWrite;
        BufferDesc desc = context.GetBufferDesc(handle);
        BufferViewHandle view = context.GetBufferView(
            handle,
            new BufferViewDesc
            {
                Kind = kind,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                StrideInBytes = raw ? 0 : desc.StrideInBytes,
                Raw = raw,
            },
            access,
            nameof(Buffer));

        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            BufferView = view,
        }, access);
        return this;
    }

    public PassBindings Buffer(
        uint binding,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        BindingType type = BindingType.StorageBufferRead,
        uint arrayElement = 0)
        => Buffer(binding, handle, desc, type, BindingAccess(type), arrayElement);

    public PassBindings Buffer(
        uint binding,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (!IsBuffer(type))
            throw new ArgumentException($"Binding type {type} is not a buffer.", nameof(type));
        ValidateAccessOverride(type, access);

        BufferViewHandle view = Context.GetBufferView(
            handle,
            desc,
            access,
            nameof(Buffer));
        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            BufferView = view,
        }, access);
        return this;
    }

    public PassBindings Buffer(
        uint binding,
        BufferViewHandle view,
        BindingType type = BindingType.StorageBufferRead,
        uint arrayElement = 0)
        => Buffer(binding, view, type, BindingAccess(type), arrayElement);

    public PassBindings Buffer(
        uint binding,
        BufferViewHandle view,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (!IsBuffer(type))
            throw new ArgumentException($"Binding type {type} is not a buffer.", nameof(type));
        ValidateAccessOverride(type, access);
        Context.ValidateBufferView(view, BufferKind(type), BindRules.Resolve(type).State, access, nameof(Buffer));

        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            BufferView = view,
        }, access);
        return this;
    }

    internal PassBindings Buffer(ReflectedBinding binding, BufferViewHandle view, uint arrayElement = 0)
        => Buffer(binding.Binding, view, binding.Type, arrayElement);

    internal PassBindings Buffer(ReflectedBinding binding, RenderGraphHandle handle, uint arrayElement = 0)
        => Resource(binding.Buffer(Context, handle, arrayElement));

    internal PassBindings Buffer(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        RenderGraphAccess access,
        uint arrayElement = 0)
        => Resource(binding.Buffer(Context, handle, access, arrayElement), access);

    internal PassBindings Buffer(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        uint arrayElement = 0)
        => Resource(binding.Buffer(Context, handle, desc, arrayElement));

    internal PassBindings Buffer(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        RenderGraphAccess access,
        uint arrayElement = 0)
        => Resource(binding.Buffer(Context, handle, desc, access, arrayElement), access);

    public PassBindings Texture(
        uint binding,
        RenderGraphHandle handle,
        BindingType type = BindingType.TextureRead,
        uint arrayElement = 0)
        => Texture(binding, handle, type, BindingAccess(type), arrayElement);

    public PassBindings Texture(
        uint binding,
        RenderGraphHandle handle,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (type is not BindingType.TextureRead and not BindingType.TextureReadWrite)
            throw new ArgumentException($"Binding type {type} is not a texture.", nameof(type));
        ValidateAccessOverride(type, access);

        RenderGraphContext context = Context;
        ViewKind kind = TextureKind(type);
        TextureDesc desc = context.GetTextureDesc(handle);
        TextureViewHandle view = context.GetTextureView(
            handle,
            new TextureViewDesc
            {
                Kind = kind,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = desc.MipLevels,
                FirstSlice = 0,
                SliceCount = desc.ArraySize,
            },
            access,
            nameof(Texture));

        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            TextureView = view,
        }, access);
        return this;
    }

    public PassBindings Texture(
        uint binding,
        RenderGraphHandle handle,
        TextureViewDesc desc,
        BindingType type = BindingType.TextureRead,
        uint arrayElement = 0)
        => Texture(binding, handle, desc, type, BindingAccess(type), arrayElement);

    public PassBindings Texture(
        uint binding,
        RenderGraphHandle handle,
        TextureViewDesc desc,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (type is not BindingType.TextureRead and not BindingType.TextureReadWrite)
            throw new ArgumentException($"Binding type {type} is not a texture.", nameof(type));
        ValidateAccessOverride(type, access);

        TextureViewHandle view = Context.GetTextureView(
            handle,
            desc,
            access,
            nameof(Texture));
        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            TextureView = view,
        }, access);
        return this;
    }

    public PassBindings Texture(
        uint binding,
        TextureViewHandle view,
        BindingType type = BindingType.TextureRead,
        uint arrayElement = 0)
        => Texture(binding, view, type, BindingAccess(type), arrayElement);

    public PassBindings Texture(
        uint binding,
        TextureViewHandle view,
        BindingType type,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        if (type is not BindingType.TextureRead and not BindingType.TextureReadWrite)
            throw new ArgumentException($"Binding type {type} is not a texture.", nameof(type));
        ValidateAccessOverride(type, access);
        Context.ValidateTextureView(view, TextureKind(type), BindRules.Resolve(type).State, access, nameof(Texture));

        Add(new BindingResourceDesc
        {
            Binding = binding,
            ArrayElement = arrayElement,
            ResourceType = type,
            TextureView = view,
        }, access);
        return this;
    }

    internal PassBindings Texture(ReflectedBinding binding, TextureViewHandle view, uint arrayElement = 0)
        => Texture(binding.Binding, view, binding.Type, arrayElement);

    internal PassBindings Texture(ReflectedBinding binding, RenderGraphHandle handle, uint arrayElement = 0)
        => Resource(binding.Texture(Context, handle, arrayElement));

    internal PassBindings Texture(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        RenderGraphAccess access,
        uint arrayElement = 0)
        => Resource(binding.Texture(Context, handle, access, arrayElement), access);

    internal PassBindings Texture(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        TextureViewDesc desc,
        uint arrayElement = 0)
        => Resource(binding.Texture(Context, handle, desc, arrayElement));

    public PassBindings Sampler(uint binding, SamplerHandle sampler, uint arrayElement = 0)
    {
        Add(BindingResourceDesc.Sampler(binding, sampler, arrayElement));
        return this;
    }

    internal PassBindings Sampler(ReflectedBinding binding, SamplerHandle sampler, uint arrayElement = 0)
    {
        if (binding.Type != BindingType.Sampler)
            throw new InvalidOperationException($"Binding '{binding.Name}' expects {binding.Type}, not a sampler.");

        return Sampler(binding.Binding, sampler, arrayElement);
    }

    internal PassBindings Resource(BindingResourceDesc resource)
    {
        Add(resource);
        return this;
    }

    internal PassBindings Resource(BindingResourceDesc resource, RenderGraphAccess access)
    {
        ValidateAccessOverride(resource.ResourceType, access);
        Add(resource, access);
        return this;
    }

    internal PassParameters Build()
    {
        EnsureValid();
        BindingResourceDesc[] resources = _resources.Count == 0
            ? Array.Empty<BindingResourceDesc>()
            : _resources.AsSpan().ToArray();
        RenderGraphAccess[] accesses = _accesses.Count == 0
            ? Array.Empty<RenderGraphAccess>()
            : _accesses.AsSpan().ToArray();
        return new PassParameters(_layout, resources, accesses, BindingHash.Finish(_hash, _resources.Count), _graphId, _graphGeneration);
    }

    internal BindingLayoutHandle Layout
    {
        get
        {
            EnsureValid();
            return _layout;
        }
    }
    internal ReadOnlySpan<BindingResourceDesc> ResourceSpan
    {
        get
        {
            EnsureValid();
            return _resources.AsSpan();
        }
    }
    internal int Hash
    {
        get
        {
            EnsureValid();
            return BindingHash.Finish(_hash, _resources.Count);
        }
    }

    internal ReadOnlySpan<RenderGraphAccess> ResourceAccessSpan
    {
        get
        {
            EnsureValid();
            return _accesses.AsSpan();
        }
    }

    internal int GraphGeneration
    {
        get
        {
            EnsureValid();
            return _graphGeneration;
        }
    }

    internal int GraphId
    {
        get
        {
            EnsureValid();
            return _graphId;
        }
    }

    private void Add(BindingResourceDesc resource)
        => Add(
            resource,
            resource.ResourceType is BindingType.None or BindingType.Sampler or BindingType.AccelerationStructure
                ? RenderGraphAccess.None
                : BindingAccess(resource.ResourceType));

    private void Add(BindingResourceDesc resource, RenderGraphAccess access)
    {
        EnsureValid();
        _resources.Add(resource);
        _accesses.Add(access);
        _hash.Add(resource);
    }

    private void EnsureValid()
    {
        if (_context == null)
            throw new InvalidOperationException("Pass bindings must be created by RenderGraphContext.Bindings.");
    }

    private RenderGraphContext Context
    {
        get
        {
            EnsureValid();
            return _context;
        }
    }

    private static bool IsBuffer(BindingType type)
        => BindRules.HasBufferView(type);

    internal static void ValidateAccessOverride(BindingType type, RenderGraphAccess access)
    {
        switch (type)
        {
            case BindingType.ConstantBuffer:
            case BindingType.StorageBufferRead:
            case BindingType.RawBufferRead:
            case BindingType.TextureRead:
                if (access != RenderGraphAccess.ReadOnly)
                    throw new InvalidOperationException($"Binding type {type} only permits {RenderGraphAccess.ReadOnly}.");
                return;

            case BindingType.StorageBufferReadWrite:
            case BindingType.RawBufferReadWrite:
            case BindingType.TextureReadWrite:
                if (access is RenderGraphAccess.WriteOnly or RenderGraphAccess.ReadWrite)
                    return;
                throw new InvalidOperationException($"Binding type {type} only permits {RenderGraphAccess.WriteOnly} or {RenderGraphAccess.ReadWrite}.");

            case BindingType.None:
            case BindingType.Sampler:
            case BindingType.AccelerationStructure:
                if (access != RenderGraphAccess.None)
                    throw new InvalidOperationException($"Binding type {type} does not permit RenderGraph access overrides.");
                return;

            default:
                throw new InvalidOperationException($"Binding type {type} does not define a valid RenderGraph access override.");
        }
    }

    internal static ViewKind BufferKind(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => ViewKind.ConstantBuffer,
            BindingType.StorageBufferRead or BindingType.RawBufferRead => ViewKind.ShaderResource,
            BindingType.StorageBufferReadWrite or BindingType.RawBufferReadWrite => ViewKind.UnorderedAccess,
            _ => throw new ArgumentException($"Binding type {type} is not a buffer.", nameof(type)),
        };

    internal static RenderGraphAccess BindingAccess(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer
                or BindingType.StorageBufferRead
                or BindingType.RawBufferRead
                or BindingType.TextureRead => RenderGraphAccess.ReadOnly,
            BindingType.StorageBufferReadWrite
                or BindingType.RawBufferReadWrite
                or BindingType.TextureReadWrite => RenderGraphAccess.ReadWrite,
            BindingType.None
                or BindingType.Sampler
                or BindingType.AccelerationStructure => RenderGraphAccess.None,
            _ => throw new ArgumentException($"Binding type {type} does not map to a RenderGraph access contract.", nameof(type)),
        };

    internal static ViewKind TextureKind(BindingType type)
        => type switch
        {
            BindingType.TextureRead => ViewKind.ShaderResource,
            BindingType.TextureReadWrite => ViewKind.UnorderedAccess,
            _ => throw new ArgumentException($"Binding type {type} is not a texture.", nameof(type)),
        };
}

internal static class BindingResources
{
    internal static BindingResourceDesc Buffer(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        return binding.Buffer(binding.BufferView(context, handle)) with { ArrayElement = arrayElement };
    }

    internal static BindingResourceDesc Buffer(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        return binding.Buffer(binding.BufferView(context, handle, access)) with { ArrayElement = arrayElement };
    }

    internal static BindingResourceDesc Buffer(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        return binding.Buffer(binding.BufferView(context, handle, desc)) with { ArrayElement = arrayElement };
    }

    internal static BindingResourceDesc Buffer(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        return binding.Buffer(binding.BufferView(context, handle, desc, access)) with { ArrayElement = arrayElement };
    }

    internal static BufferViewHandle BufferView(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        ViewKind kind = PassBindings.BufferKind(binding.Type);
        bool raw = IsRaw(binding.Type);
        BufferDesc desc = context.GetBufferDesc(handle);
        return binding.BufferView(
            context,
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

    internal static BufferViewHandle BufferView(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        RenderGraphAccess access)
    {
        ArgumentNullException.ThrowIfNull(context);
        PassBindings.ValidateAccessOverride(binding.Type, access);
        ViewKind kind = PassBindings.BufferKind(binding.Type);
        bool raw = IsRaw(binding.Type);
        BufferDesc desc = context.GetBufferDesc(handle);
        return context.GetBufferView(
            handle,
            new BufferViewDesc
            {
                Kind = kind,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                StrideInBytes = raw ? 0 : desc.StrideInBytes,
                Raw = raw,
            },
            access,
            nameof(BufferView));
    }

    internal static BufferViewHandle BufferView(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        BufferViewDesc desc)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = PassBindings.BufferKind(binding.Type);
        return context.GetBufferView(handle, desc, PassBindings.BindingAccess(binding.Type), nameof(BufferView));
    }

    internal static BufferViewHandle BufferView(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        BufferViewDesc desc,
        RenderGraphAccess access)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = PassBindings.BufferKind(binding.Type);
        PassBindings.ValidateAccessOverride(binding.Type, access);
        return context.GetBufferView(handle, desc, access, nameof(BufferView));
    }

    internal static BindingResourceDesc Texture(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ViewKind kind = PassBindings.TextureKind(binding.Type);
        TextureDesc desc = context.GetTextureDesc(handle);
        return binding.Texture(
            context,
            handle,
            new TextureViewDesc
            {
                Kind = kind,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = desc.MipLevels,
                FirstSlice = 0,
                SliceCount = desc.ArraySize,
            },
            arrayElement);
    }

    internal static BindingResourceDesc Texture(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        PassBindings.ValidateAccessOverride(binding.Type, access);
        ViewKind kind = PassBindings.TextureKind(binding.Type);
        TextureDesc desc = context.GetTextureDesc(handle);
        return binding.Texture(
            context,
            handle,
            new TextureViewDesc
            {
                Kind = kind,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = desc.MipLevels,
                FirstSlice = 0,
                SliceCount = desc.ArraySize,
            },
            access,
            arrayElement);
    }

    internal static BindingResourceDesc Texture(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        TextureViewDesc desc,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = PassBindings.TextureKind(binding.Type);
        TextureViewHandle view = context.GetTextureView(handle, desc, PassBindings.BindingAccess(binding.Type), nameof(Texture));
        return binding.Texture(view) with { ArrayElement = arrayElement };
    }

    internal static BindingResourceDesc Texture(
        this ReflectedBinding binding,
        RenderGraphContext context,
        RenderGraphHandle handle,
        TextureViewDesc desc,
        RenderGraphAccess access,
        uint arrayElement = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = PassBindings.TextureKind(binding.Type);
        PassBindings.ValidateAccessOverride(binding.Type, access);
        TextureViewHandle view = context.GetTextureView(handle, desc, access, nameof(Texture));
        return binding.Texture(view) with { ArrayElement = arrayElement };
    }

    private static bool IsRaw(BindingType type)
        => type is BindingType.RawBufferRead or BindingType.RawBufferReadWrite;
}
