using Diligent;
using SomeEngine.Render.Graph;

namespace SomeEngine.Render.Frame;

public readonly struct FrameTargetKey : IEquatable<FrameTargetKey>
{
    public FrameTargetKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Frame target key cannot be empty.", nameof(name));

        Name = name;
    }

    public string Name { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Name);

    public bool Equals(FrameTargetKey other) => string.Equals(Name, other.Name, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is FrameTargetKey other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
    public override string ToString() => Name ?? string.Empty;

    public static bool operator ==(FrameTargetKey left, FrameTargetKey right) => left.Equals(right);
    public static bool operator !=(FrameTargetKey left, FrameTargetKey right) => !left.Equals(right);
}

public static class StandardFrameTargets
{
    public static readonly FrameTargetKey SceneColor = new("SceneColor");
    public static readonly FrameTargetKey SceneDepth = new("SceneDepth");
    public static readonly FrameTargetKey HiZ = new("HiZ");
    public static readonly FrameTargetKey MotionVectors = new("MotionVectors");
}

public readonly struct FrameTargetHandle : IEquatable<FrameTargetHandle>
{
    internal FrameTargetHandle(FrameTargetKey key, ResourceKind kind)
    {
        Key = key;
        Kind = kind;
    }

    public FrameTargetKey Key { get; }
    public ResourceKind Kind { get; }
    public bool IsValid => Key.IsValid;

    public bool Equals(FrameTargetHandle other) => Key == other.Key && Kind == other.Kind;
    public override bool Equals(object? obj) => obj is FrameTargetHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Key, Kind);
    public override string ToString() => IsValid ? $"{Key}:{Kind}" : "<invalid>";

    public static readonly FrameTargetHandle Invalid = default;
}

public readonly record struct FrameTargetContext(uint Width, uint Height, ulong FrameIndex = 0);

public enum FrameTargetLifetime
{
    FrameLocal,
    History,
    Imported,
}

public readonly record struct FrameTargetInvalidationPolicy(uint Version = 0);

public delegate TextureDesc FrameTargetTextureDescFactory(FrameTargetContext context);

public delegate BufferDesc FrameTargetBufferDescFactory(FrameTargetContext context);

public readonly record struct FrameTargetHistoryHandles(
    RenderGraphHandle Current,
    RenderGraphHandle Previous,
    bool HasPrevious
);

public readonly record struct FrameTargetDeclaration
{
    public required FrameTargetKey Key { get; init; }
    public required ResourceKind Kind { get; init; }
    public required FrameTargetLifetime Lifetime { get; init; }
    public string? DebugName { get; init; }
    public ResourceState InitialState { get; init; }
    public FrameTargetInvalidationPolicy Invalidation { get; init; }
    public FrameTargetTextureDescFactory? TextureDescFactory { get; init; }
    public FrameTargetBufferDescFactory? BufferDescFactory { get; init; }
    public ITexture? ImportedTexture { get; init; }
    public IBuffer? ImportedBuffer { get; init; }
}

public sealed class FrameTargetRegistry
{
    private readonly Dictionary<FrameTargetKey, FrameTargetDeclaration> _declarations = [];
    private readonly Dictionary<FrameTargetKey, RenderGraphHandle> _resolved = [];
    private readonly Dictionary<FrameTargetKey, HistoryTextureState> _textureHistory = [];
    private readonly Dictionary<FrameTargetKey, HistoryBufferState> _bufferHistory = [];

    private RenderGraph? _graph;
    private FrameTargetContext _context;
    private bool _hasFrame;
    private bool _frozen;

    public bool IsFrozen => _frozen;

    public void BeginFrame(RenderGraph graph, FrameTargetContext context)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _context = context;
        _hasFrame = true;
        _frozen = false;
        _declarations.Clear();
        _resolved.Clear();
    }

    public void Freeze()
    {
        EnsureFrame();
        foreach (var (_, declaration) in _declarations)
        {
            ValidateDeclaration(declaration);
        }

        _frozen = true;
    }

    public FrameTargetHandle DeclareTexture(
        FrameTargetKey key,
        FrameTargetTextureDescFactory descFactory,
        FrameTargetLifetime lifetime = FrameTargetLifetime.FrameLocal,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null,
        FrameTargetInvalidationPolicy invalidation = default
    )
    {
        if (descFactory == null)
            throw new ArgumentNullException(nameof(descFactory));

        var declaration = new FrameTargetDeclaration
        {
            Key = key,
            Kind = ResourceKind.Texture,
            Lifetime = lifetime,
            InitialState = initialState,
            DebugName = debugName,
            Invalidation = invalidation,
            TextureDescFactory = descFactory,
        };

        return Declare(declaration);
    }

    public FrameTargetHandle DeclareBuffer(
        FrameTargetKey key,
        FrameTargetBufferDescFactory descFactory,
        FrameTargetLifetime lifetime = FrameTargetLifetime.FrameLocal,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null,
        FrameTargetInvalidationPolicy invalidation = default
    )
    {
        if (descFactory == null)
            throw new ArgumentNullException(nameof(descFactory));

        var declaration = new FrameTargetDeclaration
        {
            Key = key,
            Kind = ResourceKind.Buffer,
            Lifetime = lifetime,
            InitialState = initialState,
            DebugName = debugName,
            Invalidation = invalidation,
            BufferDescFactory = descFactory,
        };

        return Declare(declaration);
    }

    public FrameTargetHandle ImportTexture(
        FrameTargetKey key,
        ITexture texture,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null
    )
    {
        if (texture == null)
            throw new ArgumentNullException(nameof(texture));

        return Declare(
            new FrameTargetDeclaration
            {
                Key = key,
                Kind = ResourceKind.Texture,
                Lifetime = FrameTargetLifetime.Imported,
                InitialState = initialState,
                DebugName = debugName,
                ImportedTexture = texture,
                TextureDescFactory = _ => texture.GetDesc(),
            }
        );
    }

    public FrameTargetHandle ImportBuffer(
        FrameTargetKey key,
        IBuffer buffer,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null
    )
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        return Declare(
            new FrameTargetDeclaration
            {
                Key = key,
                Kind = ResourceKind.Buffer,
                Lifetime = FrameTargetLifetime.Imported,
                InitialState = initialState,
                DebugName = debugName,
                ImportedBuffer = buffer,
                BufferDescFactory = _ => buffer.GetDesc(),
            }
        );
    }

    public FrameTargetHandle OverrideTexture(
        FrameTargetKey key,
        FrameTargetTextureDescFactory descFactory,
        FrameTargetLifetime lifetime = FrameTargetLifetime.FrameLocal,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null,
        FrameTargetInvalidationPolicy invalidation = default
    )
    {
        EnsureMutable();
        _declarations.Remove(key);
        _resolved.Remove(key);
        return DeclareTexture(key, descFactory, lifetime, initialState, debugName, invalidation);
    }

    public FrameTargetHandle OverrideImportedTexture(
        FrameTargetKey key,
        ITexture texture,
        ResourceState initialState = ResourceState.Unknown,
        string? debugName = null
    )
    {
        EnsureMutable();
        _declarations.Remove(key);
        _resolved.Remove(key);
        return ImportTexture(key, texture, initialState, debugName);
    }

    public bool TryGetDeclaration(FrameTargetKey key, out FrameTargetDeclaration declaration) =>
        _declarations.TryGetValue(key, out declaration);

    public FrameTargetHandle Require(FrameTargetKey key)
    {
        EnsureDeclared(key, out var declaration);
        return new FrameTargetHandle(key, declaration.Kind);
    }

    public RenderGraphHandle ResolveTexture(FrameTargetKey key) => ResolveTexture(Require(key));

    public RenderGraphHandle ResolveTexture(FrameTargetHandle handle)
    {
        EnsureFrame();
        EnsureKind(handle, ResourceKind.Texture);
        if (_resolved.TryGetValue(handle.Key, out var existing))
            return existing;

        var declaration = GetDeclaration(handle);
        var graph = RequireGraph();
        string name = GetResourceName(declaration);

        RenderGraphHandle rgHandle;
        if (declaration.Lifetime == FrameTargetLifetime.Imported)
        {
            if (declaration.ImportedTexture == null)
                throw new InvalidOperationException($"Frame target '{handle.Key}' has no imported texture.");

            rgHandle = graph.Import(name, declaration.ImportedTexture, declaration.InitialState);
        }
        else
        {
            var desc = GetTextureDesc(declaration);
            desc = desc with { Name = string.IsNullOrWhiteSpace(desc.Name) ? name : desc.Name };
            rgHandle = graph.CreateTexture(name, desc);
        }

        _resolved[handle.Key] = rgHandle;
        return rgHandle;
    }

    public RenderGraphHandle ResolveBuffer(FrameTargetKey key) => ResolveBuffer(Require(key));

    public RenderGraphHandle ResolveBuffer(FrameTargetHandle handle)
    {
        EnsureFrame();
        EnsureKind(handle, ResourceKind.Buffer);
        if (_resolved.TryGetValue(handle.Key, out var existing))
            return existing;

        var declaration = GetDeclaration(handle);
        var graph = RequireGraph();
        string name = GetResourceName(declaration);

        RenderGraphHandle rgHandle;
        if (declaration.Lifetime == FrameTargetLifetime.Imported)
        {
            if (declaration.ImportedBuffer == null)
                throw new InvalidOperationException($"Frame target '{handle.Key}' has no imported buffer.");

            rgHandle = graph.Import(name, declaration.ImportedBuffer, declaration.InitialState);
        }
        else
        {
            rgHandle = graph.CreateBuffer(name, GetBufferDesc(declaration));
        }

        _resolved[handle.Key] = rgHandle;
        return rgHandle;
    }

    public FrameTargetHistoryHandles ResolveHistoryTexture(FrameTargetKey key) =>
        ResolveHistoryTexture(Require(key));

    public FrameTargetHistoryHandles ResolveHistoryTexture(FrameTargetHandle handle)
    {
        EnsureFrame();
        EnsureKind(handle, ResourceKind.Texture);

        var declaration = GetDeclaration(handle);
        if (declaration.Lifetime != FrameTargetLifetime.History)
            throw new InvalidOperationException($"Frame target '{handle.Key}' is not a history texture.");

        var graph = RequireGraph();
        var desc = GetTextureDesc(declaration);
        string baseName = GetResourceName(declaration);
        var history = GetTextureHistory(handle.Key);

        bool descriptorChanged =
            history.HasHistory
            && (!AreCompatible(history.Desc, desc) || history.Version != declaration.Invalidation.Version);
        if (descriptorChanged)
            history.Reset();

        RenderGraphHandle previous = RenderGraphHandle.Invalid;
        bool hasPrevious = history.HasHistory && history.Texture != null;
        if (hasPrevious)
            previous = graph.Import($"{baseName}_Previous", history.Texture!, history.State);

        string currentName = history.Flip ? $"{baseName}_A" : $"{baseName}_B";
        var current = graph.CreateTexture(currentName, desc with { Name = currentName });
        graph.QueueTextureExtraction(
            current,
            texture =>
            {
                if (texture == null)
                    return;

                history.Texture = texture;
                history.Desc = desc;
                history.State = ResourceState.Unknown;
                history.Version = declaration.Invalidation.Version;
                history.HasHistory = true;
                history.Flip = !history.Flip;
            }
        );

        _resolved[handle.Key] = current;
        return new FrameTargetHistoryHandles(current, previous, hasPrevious);
    }

    public FrameTargetHistoryHandles ResolveHistoryBuffer(FrameTargetKey key) =>
        ResolveHistoryBuffer(Require(key));

    public FrameTargetHistoryHandles ResolveHistoryBuffer(FrameTargetHandle handle)
    {
        EnsureFrame();
        EnsureKind(handle, ResourceKind.Buffer);

        var declaration = GetDeclaration(handle);
        if (declaration.Lifetime != FrameTargetLifetime.History)
            throw new InvalidOperationException($"Frame target '{handle.Key}' is not a history buffer.");

        var graph = RequireGraph();
        var desc = GetBufferDesc(declaration);
        string baseName = GetResourceName(declaration);
        var history = GetBufferHistory(handle.Key);

        bool descriptorChanged =
            history.HasHistory
            && (!AreCompatible(history.Desc, desc) || history.Version != declaration.Invalidation.Version);
        if (descriptorChanged)
            history.Reset();

        RenderGraphHandle previous = RenderGraphHandle.Invalid;
        bool hasPrevious = history.HasHistory && history.Buffer != null;
        if (hasPrevious)
            previous = graph.Import($"{baseName}_Previous", history.Buffer!, history.State);

        string currentName = history.Flip ? $"{baseName}_A" : $"{baseName}_B";
        var current = graph.CreateBuffer(currentName, desc);
        graph.QueueBufferExtraction(
            current,
            buffer =>
            {
                if (buffer == null)
                    return;

                history.Buffer = buffer;
                history.Desc = desc;
                history.State = ResourceState.Unknown;
                history.Version = declaration.Invalidation.Version;
                history.HasHistory = true;
                history.Flip = !history.Flip;
            }
        );

        _resolved[handle.Key] = current;
        return new FrameTargetHistoryHandles(current, previous, hasPrevious);
    }

    public void Invalidate(FrameTargetKey key)
    {
        if (_textureHistory.TryGetValue(key, out var textureHistory))
            textureHistory.Reset();
        if (_bufferHistory.TryGetValue(key, out var bufferHistory))
            bufferHistory.Reset();
    }

    private FrameTargetHandle Declare(FrameTargetDeclaration declaration)
    {
        EnsureMutable();
        ValidateDeclaration(declaration);

        if (_declarations.TryGetValue(declaration.Key, out var existing))
        {
            if (!AreCompatible(existing, declaration))
                throw new InvalidOperationException(
                    $"Frame target '{declaration.Key}' was declared with incompatible descriptors."
                );

            return new FrameTargetHandle(existing.Key, existing.Kind);
        }

        _declarations.Add(declaration.Key, declaration);
        return new FrameTargetHandle(declaration.Key, declaration.Kind);
    }

    private void EnsureMutable()
    {
        EnsureFrame();
        if (_frozen)
            throw new InvalidOperationException("FrameTargetRegistry is frozen for this frame.");
    }

    private void EnsureFrame()
    {
        if (!_hasFrame || _graph == null)
            throw new InvalidOperationException("FrameTargetRegistry.BeginFrame must be called first.");
    }

    private RenderGraph RequireGraph()
    {
        EnsureFrame();
        return _graph!;
    }

    private FrameTargetDeclaration GetDeclaration(FrameTargetHandle handle)
    {
        if (!handle.IsValid)
            throw new ArgumentException("Frame target handle is invalid.", nameof(handle));

        EnsureDeclared(handle.Key, out var declaration);
        if (declaration.Kind != handle.Kind)
            throw new InvalidOperationException(
                $"Frame target '{handle.Key}' is declared as {declaration.Kind}, not {handle.Kind}."
            );

        return declaration;
    }

    private void EnsureDeclared(FrameTargetKey key, out FrameTargetDeclaration declaration)
    {
        if (!key.IsValid)
            throw new ArgumentException("Frame target handle is invalid.", nameof(key));

        if (!_declarations.TryGetValue(key, out declaration))
            throw new KeyNotFoundException($"Frame target '{key}' is not declared.");
    }

    private static void EnsureKind(FrameTargetHandle handle, ResourceKind kind)
    {
        if (!handle.IsValid)
            throw new ArgumentException("Frame target handle is invalid.", nameof(handle));

        if (handle.Kind != kind)
            throw new InvalidOperationException($"Frame target '{handle.Key}' is {handle.Kind}, not {kind}.");
    }

    private void ValidateDeclaration(FrameTargetDeclaration declaration)
    {
        if (!declaration.Key.IsValid)
            throw new ArgumentException("Frame target key cannot be empty.", nameof(declaration));

        switch (declaration.Kind)
        {
            case ResourceKind.Texture:
                if (declaration.TextureDescFactory == null)
                    throw new InvalidOperationException($"Texture frame target '{declaration.Key}' has no descriptor.");
                _ = GetTextureDesc(declaration);
                break;
            case ResourceKind.Buffer:
                if (declaration.BufferDescFactory == null)
                    throw new InvalidOperationException($"Buffer frame target '{declaration.Key}' has no descriptor.");
                _ = GetBufferDesc(declaration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(declaration));
        }
    }

    private TextureDesc GetTextureDesc(FrameTargetDeclaration declaration)
    {
        if (declaration.TextureDescFactory == null)
            throw new InvalidOperationException($"Texture frame target '{declaration.Key}' has no descriptor.");

        return declaration.TextureDescFactory(_context);
    }

    private BufferDesc GetBufferDesc(FrameTargetDeclaration declaration)
    {
        if (declaration.BufferDescFactory == null)
            throw new InvalidOperationException($"Buffer frame target '{declaration.Key}' has no descriptor.");

        return declaration.BufferDescFactory(_context);
    }

    private static string GetResourceName(FrameTargetDeclaration declaration) =>
        string.IsNullOrWhiteSpace(declaration.DebugName)
            ? declaration.Key.Name
            : declaration.DebugName!;

    private HistoryTextureState GetTextureHistory(FrameTargetKey key)
    {
        if (!_textureHistory.TryGetValue(key, out var history))
        {
            history = new HistoryTextureState();
            _textureHistory.Add(key, history);
        }

        return history;
    }

    private HistoryBufferState GetBufferHistory(FrameTargetKey key)
    {
        if (!_bufferHistory.TryGetValue(key, out var history))
        {
            history = new HistoryBufferState();
            _bufferHistory.Add(key, history);
        }

        return history;
    }

    private bool AreCompatible(FrameTargetDeclaration left, FrameTargetDeclaration right)
    {
        if (left.Kind != right.Kind || left.Lifetime != right.Lifetime)
            return false;

        if (left.Invalidation.Version != right.Invalidation.Version)
            return false;

        if (left.Kind == ResourceKind.Texture)
            return AreCompatible(GetTextureDesc(left), GetTextureDesc(right));

        return AreCompatible(GetBufferDesc(left), GetBufferDesc(right));
    }

    private static bool AreCompatible(TextureDesc a, TextureDesc b)
    {
        return a.Width == b.Width
            && a.Height == b.Height
            && a.Format == b.Format
            && a.BindFlags == b.BindFlags
            && a.Type == b.Type
            && a.ArraySizeOrDepth == b.ArraySizeOrDepth
            && a.MipLevels == b.MipLevels
            && a.Usage == b.Usage
            && a.CPUAccessFlags == b.CPUAccessFlags;
    }

    private static bool AreCompatible(BufferDesc a, BufferDesc b)
    {
        return a.Size == b.Size
            && a.BindFlags == b.BindFlags
            && a.Usage == b.Usage
            && a.Mode == b.Mode
            && a.CPUAccessFlags == b.CPUAccessFlags
            && a.ElementByteStride == b.ElementByteStride;
    }

    private sealed class HistoryTextureState
    {
        public ITexture? Texture;
        public TextureDesc Desc;
        public ResourceState State = ResourceState.Unknown;
        public uint Version;
        public bool HasHistory;
        public bool Flip;

        public void Reset()
        {
            Texture = null;
            Desc = default;
            State = ResourceState.Unknown;
            Version = 0;
            HasHistory = false;
            Flip = false;
        }
    }

    private sealed class HistoryBufferState
    {
        public IBuffer? Buffer;
        public BufferDesc Desc;
        public ResourceState State = ResourceState.Unknown;
        public uint Version;
        public bool HasHistory;
        public bool Flip;

        public void Reset()
        {
            Buffer = null;
            Desc = default;
            State = ResourceState.Unknown;
            Version = 0;
            HasHistory = false;
            Flip = false;
        }
    }
}
