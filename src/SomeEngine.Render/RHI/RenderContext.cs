using System;
using System.Collections.Generic;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;
using Silk.NET.Windowing;
using RhiBackend = SomeEngine.Rhi.Backend;
using RhiBindFlags = SomeEngine.Rhi.BindFlags;
using RhiClearDepthStencil = SomeEngine.Rhi.ClearDepthStencil;
using RhiClearValue = SomeEngine.Rhi.ClearValue;
using RhiFormat = SomeEngine.Rhi.Format;
using RhiMemoryClass = SomeEngine.Rhi.MemoryClass;
using RhiResourceDimension = SomeEngine.Rhi.ResourceDimension;
using RhiResourceState = SomeEngine.Rhi.ResourceState;
using RhiTextureDesc = SomeEngine.Rhi.TextureDesc;

namespace SomeEngine.Render.RHI;

public sealed class RenderContext : IDisposable
{
    private readonly List<ShaderModuleHandle> _ownedShaderModules = [];
    private readonly uint _presentSyncInterval;
    private SomeEngine.Rhi.IInstance? _graphicsInstance;
    private SwapchainHandle _graphicsSwapchainHandle;
    private RhiBackend _shaderModuleBackend = RhiBackend.Null;
    private PipelineSources? _pipelineSources;

    public SomeEngine.Rhi.IDevice? GraphicsDevice { get; private set; }
    public SomeEngine.Rhi.IQueue? GraphicsQueue { get; private set; }
    public SomeEngine.Rhi.ISwapchain? GraphicsSwapchain { get; private set; }
    public RhiTextureDesc GraphicsDepthBufferDesc { get; private set; } = new();
    public uint PresentSyncInterval => _presentSyncInterval;
    internal PipelineCache? PipelineCache { get; private set; }

    public RenderContext(uint? presentSyncInterval = null)
    {
        _presentSyncInterval = presentSyncInterval ?? 1u;
    }

    internal static RenderContext CreateHeadless(
        RhiBackend backend = RhiBackend.Null,
        uint width = 1,
        uint height = 1,
        uint? presentSyncInterval = null,
        bool validation = true)
    {
        var context = new RenderContext(presentSyncInterval);
        context._graphicsInstance = SomeEngine.Rhi.Instance.Create();
        context.GraphicsDevice = context._graphicsInstance.CreateDevice(new SomeEngine.Rhi.DeviceDesc
        {
            Backend = backend,
            EnableValidation = validation,
        });
        context.PipelineCache = new PipelineCache(context.GraphicsDevice);
        context._pipelineSources = new PipelineSources(context.PipelineCache);
        context._shaderModuleBackend = context.GraphicsDevice.AdapterInfo.Backend;
        context.GraphicsQueue = context.GraphicsDevice.GetQueue(SomeEngine.Rhi.QueueType.Graphics);
        context.UpdateDepthDesc(width, height);
        return context;
    }

    public void Initialize(IWindow window, RhiBackend backend, params IBackendFactory[] backendFactories)
        => Initialize(window, backend, true, backendFactories);

    public void Initialize(IWindow window, RhiBackend backend, bool validation, params IBackendFactory[] backendFactories)
    {
        if (window?.Native?.Win32 == null)
            throw new NotSupportedException("Window rendering requires a Win32 native window.");
        if (GraphicsDevice != null)
            throw new InvalidOperationException("Render context is already initialized.");
        if (!HasFactory(backend, backendFactories))
            throw new InvalidOperationException($"Window rendering for backend {backend} requires a backend factory supplied by the host application.");

        var (hwnd, _, _) = window.Native.Win32.Value;
        uint width = checked((uint)Math.Max(1, window.Size.X));
        uint height = checked((uint)Math.Max(1, window.Size.Y));

        IBackendFactory[] factories = backendFactories ?? [];
        _graphicsInstance = SomeEngine.Rhi.Instance.Create(factories);
        GraphicsDevice = _graphicsInstance.CreateDevice(new SomeEngine.Rhi.DeviceDesc
        {
            Backend = backend,
            EnableValidation = validation,
        });
        PipelineCache = new PipelineCache(GraphicsDevice);
        _pipelineSources = new PipelineSources(PipelineCache);
        _shaderModuleBackend = GraphicsDevice.AdapterInfo.Backend;
        GraphicsQueue = GraphicsDevice.GetQueue(SomeEngine.Rhi.QueueType.Graphics);
        _graphicsSwapchainHandle = GraphicsDevice.CreateSwapchain(new SomeEngine.Rhi.SwapchainDesc
        {
            Name = "SomeEngine Swapchain",
            NativeWindowHandle = hwnd,
            Width = width,
            Height = height,
            Format = RhiFormat.Bgra8Unorm,
            BufferCount = 3,
            AllowTearing = true,
        });
        GraphicsSwapchain = GraphicsDevice.GetSwapchain(_graphicsSwapchainHandle);
        UpdateDepthDesc(width, height);
    }

    private void UpdateDepthDesc(uint width, uint height)
    {
        GraphicsDepthBufferDesc = new RhiTextureDesc
        {
            Name = "DepthBuffer",
            Dimension = RhiResourceDimension.Texture2D,
            Width = width,
            Height = height,
            Format = RhiFormat.D32Float,
            Memory = RhiMemoryClass.DeviceLocal,
            BindFlags = RhiBindFlags.DepthStencil | RhiBindFlags.ShaderResource,
            InitialState = RhiResourceState.DepthWrite,
            OptimizedClearValue = RhiClearValue.FromDepthStencil(
                RhiFormat.D32Float,
                new RhiClearDepthStencil(1.0f, 0)),
        };
    }

    public void Resize(uint width, uint height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (GraphicsSwapchain != null)
        {
            GraphicsSwapchain.Resize(width, height);
            UpdateDepthDesc(width, height);
            return;
        }

        throw new InvalidOperationException("Render context has not been initialized.");
    }

    public void Present()
    {
        var swapchain = GraphicsSwapchain
            ?? throw new InvalidOperationException("Render context has no swapchain.");

        swapchain.Present(new SomeEngine.Rhi.PresentDesc
        {
            SyncInterval = _presentSyncInterval,
            AllowTearing = _presentSyncInterval == 0,
        });
    }

    public void ReleasePipelines()
    {
        if (GraphicsDevice == null)
            return;

        GraphicsDevice.WaitIdle();
        _pipelineSources?.Dispose();
        _pipelineSources = null;
        PipelineCache?.Dispose();
        PipelineCache = null;
    }

    public void LoadPipelineCache(ReadOnlyMemory<byte> data)
        => RequireCache().LoadCache(data);

    public byte[] SavePipelineCache()
        => RequireCache().SaveCache();

    public PipelineSourceLease AddPipelineSource(IPipelineSource source)
        => RequireSources().Add(source);

    internal bool HasPipelineSource(IPipelineSource source)
        => _pipelineSources?.Contains(source) ?? false;

    public PipelineWarmup RefreshSources()
        => RequireSources().Refresh();

    public PipelineWarmup WarmupSources(int budget)
        => RequireSources().Warmup(budget);

    public PipelineWarmup WaitSources()
        => RequireSources().WaitRequired();

    public PipelineLease LeasePipelines(PipelineCollector collector)
        => RequireCache().Lease(collector);

    public PipelineWarmup WarmupPipelines(ReadOnlySpan<PipelineTicket> tickets, int budget)
        => RequireCache().Warmup(tickets, budget);

    public PipelineWarmup WaitRequired(ReadOnlySpan<PipelineTicket> tickets)
        => RequireCache().WaitRequired(tickets);

    public PipelineIssueSink? PipelineIssueSink
    {
        get => RequireCache().IssueSink;
        set => RequireCache().IssueSink = value;
    }

    internal ShaderModuleHandle CreateShaderModule(Shader shader, string entryPointName)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (GraphicsDevice == null)
            throw new InvalidOperationException("Render context has not been initialized.");

        string backend = PipelineCache.BackendName(_shaderModuleBackend);
        if (!shader.TryVariant(backend, entryPointName, out ShaderVariant variant))
            throw new InvalidOperationException(
                $"Shader variant not found for backend {backend} and entry point {entryPointName} in shader {shader.Name}.");
        if (variant.Bytecode.IsEmpty)
            throw new InvalidOperationException($"Shader variant '{shader.Name}:{entryPointName}' has no bytecode.");

        var handle = GraphicsDevice.CreateShaderModule(new SomeEngine.Rhi.ShaderModuleDesc
        {
            Name = $"{shader.Name}_{entryPointName}",
            Backend = _shaderModuleBackend,
            Stage = variant.Stage,
            EntryPoint = entryPointName,
            BytecodeFormat = ShaderModules.BytecodeFormat(backend),
            Bytecode = variant.Bytecode,
        });
        _ownedShaderModules.Add(handle);
        return handle;
    }

    public void Dispose()
    {
        if (GraphicsDevice != null)
        {
            ReleasePipelines();

            for (int i = _ownedShaderModules.Count - 1; i >= 0; i--)
                GraphicsDevice.Destroy(_ownedShaderModules[i]);
            _ownedShaderModules.Clear();

            if (_graphicsSwapchainHandle.IsValid)
                GraphicsDevice.Destroy(_graphicsSwapchainHandle);
        }

        GraphicsDevice?.Dispose();
        _graphicsInstance?.Dispose();

        GraphicsSwapchain = null;
        GraphicsQueue = null;
        GraphicsDevice = null;
        PipelineCache = null;
        _pipelineSources = null;
        _graphicsInstance = null;
        _graphicsSwapchainHandle = default;
    }

    private PipelineCache RequireCache()
        => PipelineCache ?? throw new InvalidOperationException("Render context has no pipeline cache.");

    private PipelineSources RequireSources()
        => _pipelineSources ?? throw new InvalidOperationException("Render context has no pipeline sources.");

    private static bool HasFactory(RhiBackend backend, IBackendFactory[]? factories)
    {
        if (backend == RhiBackend.Null)
            return true;
        if (factories == null)
            return false;

        for (int i = 0; i < factories.Length; i++)
        {
            if (factories[i].Backend == backend)
                return true;
        }

        return false;
    }

}
