using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;

namespace SomeEngine.Editor;

internal static class EditorApp
{
    private readonly record struct RenderFrameData(
        FrameData Frame,
        ViewData View);

    private static IWindow? _window;
    private static RenderContext? _renderContext;
    private static ClusterPipeline? _clusterPipeline;
    private static PostTonemapPass? _postTonemap;
    private static RenderGraph? _renderGraph;
    private static readonly ViewHistory _histories = new();
    private static AssetDatabase? _assetDb;
    private static AssetStore? _assets;
    private static ClusterRenderAsset? clusterRenderAsset;
    private static GameWorld? _gameWorld;
    private static RenderWorld? _renderWorld;
    private static RenderWorldExtractor? _renderExtractor;
    private static readonly CameraHistory _cameraHistory = new();
    private static readonly TemporalState _temporalState = new();
    private static uint _temporalFrameIndex;
    private static int _frameLimit;
    private static uint _frameIndex;
    private static uint _pendingResizeWidth;
    private static uint _pendingResizeHeight;
    private static bool _resizePending;

    static int Main(string[] args)
    {
        CrashDialogPolicy.DisableUi();

        try
        {
            Profiler.Configure(Profiler.ParseOptions(args));
            Profiler.SetThreadName("SomeEngine Editor");
            Start(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unhandled editor exception:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Start(string[] args)
    {
        _frameLimit = ResolveFrameLimit(args);

        WindowOptions options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = "SomeEngine Editor";
        options.API = GraphicsAPI.None;

        _window = Window.Create(options);

        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Update += OnUpdate;
        _window.Resize += OnResize;
        _window.Closing += OnClose;

        _window.Run();
    }

    private static int ResolveFrameLimit(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--frames", StringComparison.OrdinalIgnoreCase))
                continue;
            if (++index >= args.Length)
                throw new ArgumentException("--frames requires a positive integer value.");
            if (!int.TryParse(args[index], out int frameLimit) || frameLimit <= 0)
                throw new ArgumentException("--frames requires a positive integer value.");
            return frameLimit;
        }

        return 0;
    }

    private static void OnResize(Vector2D<int> size)
    {
        _pendingResizeWidth = checked((uint)Math.Max(1, size.X));
        _pendingResizeHeight = checked((uint)Math.Max(1, size.Y));
        _resizePending = true;
    }

    private static bool ApplyPendingResize()
    {
        if (!_resizePending || _renderContext == null || _renderGraph == null)
            return false;

        if (!_renderGraph.TryRetireSubmittedFrames())
            return true;

        _renderGraph.ClearBindSets(waitForGpu: false);

        var swapchain = _renderContext.GraphicsSwapchain;
        if (swapchain != null
            && (swapchain.Width != _pendingResizeWidth || swapchain.Height != _pendingResizeHeight))
        {
            _renderContext.Resize(_pendingResizeWidth, _pendingResizeHeight);
            _histories.ResetAll(waitForGpu: false);
            _cameraHistory.Reset();
            _temporalState.Reset();
            _temporalFrameIndex = 0;
        }

        _resizePending = false;
        return false;
    }

    private static void OnClose()
    {
        _renderGraph?.Dispose();
        _postTonemap?.Dispose();
        _postTonemap = null;
        _clusterPipeline?.Dispose();
        _renderExtractor = null;
        _renderWorld = null;
        _histories.Dispose();
        _renderContext?.Dispose();
        _assets?.Dispose();
        _assets = null;
        _assetDb?.Dispose();
        Profiler.Shutdown();
        Console.WriteLine($"Editor shutdown after {_frameIndex} frame(s).");
    }

    private static string ResolveProjectRoot()
    {
        foreach (string startDirectory in new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        })
        {
            string? current = Path.GetFullPath(startDirectory);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "SomeEngine.slnx")))
                    return current;

                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SomeEngine project root.");
    }

    private static AssetGuid DefaultGuid()
    {
        AssetDatabase assetDb = _assetDb
            ?? throw new InvalidOperationException("Editor asset database has not been initialized.");
        AssetGuid? materialGuid = assetDb.Resolve(GltfImporterSettings.DefaultLitMaterialTemplate);
        if (materialGuid is not { IsEmpty: false })
        {
            string materialPath = Path.Combine(
                ResolveProjectRoot(),
                GltfImporterSettings.DefaultLitMaterialTemplate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(materialPath))
            {
                assetDb.Import(GltfImporterSettings.DefaultLitMaterialTemplate);
                materialGuid = assetDb.Resolve(GltfImporterSettings.DefaultLitMaterialTemplate);
            }
        }

        return materialGuid is { IsEmpty: false } guid
            ? guid
            : throw new InvalidOperationException(
                $"Editor requires material asset '{GltfImporterSettings.DefaultLitMaterialTemplate}'.");
    }

    private static Handle<Shader> LoadShader(AssetGuid guid)
    {
        if (guid.IsEmpty)
            return default;
        AssetStore assets = _assets
            ?? throw new InvalidOperationException("Editor asset store has not been initialized.");
        AssetDatabase assetDb = _assetDb
            ?? throw new InvalidOperationException("Editor asset database has not been initialized.");

        return RuntimeAssetLoader
            .RequestShader(assets, assetDb, guid)
            .GetAwaiter()
            .GetResult();
    }

    private static Handle<Material> LoadMaterial(AssetGuid guid)
    {
        if (guid.IsEmpty)
            return default;
        AssetStore assets = _assets
            ?? throw new InvalidOperationException("Editor asset store has not been initialized.");
        AssetDatabase assetDb = _assetDb
            ?? throw new InvalidOperationException("Editor asset database has not been initialized.");

        return RuntimeAssetLoader
            .RequestMaterial(assets, assetDb, guid)
            .GetAwaiter()
            .GetResult();
    }

    private static void OnLoad()
    {
        Console.WriteLine("Window Loaded.");
        Console.WriteLine(Profiler.Status.ToDisplayString());

        _gameWorld = new GameWorld();

        _renderContext = new RenderContext();
        if (_window != null)
        {
            _renderContext.Initialize(_window, Backend.D3D12, D3D12Backend.Factory);
        }
        _assetDb = AssetCatalog.CreateDatabase(ResolveProjectRoot());
        _assets = new AssetStore();
        clusterRenderAsset = ClusterRenderAssets.LoadDefault(_assetDb);

        _renderWorld = new RenderWorld();
        Handle<Material> material = LoadMaterial(DefaultGuid());
        if (!material.IsValid)
            throw new InvalidOperationException("Editor default material did not load into AssetStore.");

        for (int i = 0; i < 100; i++)
        {
            var e = _gameWorld.World.CreateEntity();
            _gameWorld.World.Add(e, new LocalTransform
            {
                Value = new TransformQvvs(
                    new Vector3((i % 10 - 4.5f) * 1.5f, (i / 10 - 4.5f) * 1.5f, 0),
                    Quaternion.Identity
                ),
            });
            _gameWorld.World.Add(e, new WorldTransform());
            _gameWorld.World.Add(e, new MeshInstance());
            _gameWorld.World.Add(e, new MeshMaterialBindings { Materials = new[] { material } });
        }

        _renderGraph = new RenderGraph();

        _clusterPipeline = ClusterPipelineAssets.LoadOpaque(
            _renderContext,
            _assetDb,
            clusterRenderAsset,
            _assets);
        AssetGuid? postTonemapGuid = _assetDb.Resolve(HostShaders.PostTonemapPath);
        if (postTonemapGuid is not { IsEmpty: false } postTonemapShaderGuid)
            throw new InvalidOperationException($"Required host shader '{HostShaders.PostTonemapPath}' is not indexed.");
        Handle<Shader> postTonemapShader = LoadShader(postTonemapShaderGuid);
        if (!postTonemapShader.IsValid)
            throw new InvalidOperationException($"Required host shader '{HostShaders.PostTonemapPath}' did not load.");
        _postTonemap = new PostTonemapPass(
            _renderContext,
            _assets.Get(postTonemapShader));
        _renderExtractor = new RenderWorldExtractor(_renderWorld);
        _clusterPipeline.Initialize(_renderContext);
    }

    /*
    private static MeshAsset CreateCube()
    {
        return new MeshAsset();
    }
    */

    private static void OnUpdate(double deltaTime)
    {
        using var scope = Profiler.BeginScope("Editor.Update");
        _gameWorld?.Update(deltaTime);
    }

    private static void RecordRenderFrame(RenderGraph graph, RenderFrameData data)
    {
        using (Profiler.BeginScope("Editor.RenderGraph.Build"))
        {
            var clusterPipeline = _clusterPipeline
                ?? throw new InvalidOperationException("Cluster pipeline must be initialized before recording the editor render frame.");
            var renderWorld = _renderWorld
                ?? throw new InvalidOperationException("Render world must be initialized before recording the editor render frame.");
            var postTonemap = _postTonemap
                ?? throw new InvalidOperationException("Post tonemap pass must be initialized before recording the editor render frame.");

            SceneTextures sceneTextures = FrameResources.CreateSceneTextures(
                graph,
                data.Frame,
                data.View);
            FrameOutputs outputs = clusterPipeline.AddPasses(
                graph,
                renderWorld,
                sceneTextures,
                _histories,
                _cameraHistory,
                _temporalState);

            using (Profiler.BeginScope("Editor.RenderFrame.PostTonemap"))
            {
                postTonemap.AddTo(graph, outputs.PostSceneColor, sceneTextures.OutputColor);
            }
        }
    }

    private static void OnRender(double deltaTime)
    {
        using var frameScope = Profiler.BeginScope("Editor.Frame");
        if (_renderContext == null || _renderGraph == null || _renderExtractor == null || _renderWorld == null)
            return;

        if (_clusterPipeline == null)
            return;

        if (ApplyPendingResize())
            return;

        var device = _renderContext.GraphicsDevice;
        var queue = _renderContext.GraphicsQueue;
        var swapchain = _renderContext.GraphicsSwapchain;
        if (device == null || queue == null || swapchain == null)
            return;

        TextureDesc backBufferDesc = device.GetTextureDesc(swapchain.CurrentTexture);

        var cameraPosition = new Vector3(0, 0, -12);
        var view = Matrix4x4.CreateLookAt(cameraPosition, Vector3.Zero, Vector3.UnitY);
        float aspect = backBufferDesc.Width / (float)Math.Max(backBufferDesc.Height, 1u);
        var motionProj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4.0f, aspect, 0.1f, 1000.0f);
        var proj = motionProj;
        Vector2 temporalJitterPixels = Vector2.Zero;
        if (_clusterPipeline.TemporalResolveEnabled
            && _clusterPipeline.TemporalJitterEnabled
            && _temporalState.Ready)
        {
            temporalJitterPixels = TemporalJitter.SamplePixels(_temporalFrameIndex);
            proj = TemporalJitter.ApplyToProjection(
                proj,
                temporalJitterPixels,
                backBufferDesc.Width,
                backBufferDesc.Height);
            _temporalFrameIndex++;
        }
        _clusterPipeline.TemporalJitterPixels = temporalJitterPixels;
        _renderExtractor!.Rebuild(_gameWorld!.World);
        _clusterPipeline.PrepareFrame(_renderWorld, _histories, _cameraHistory, _temporalState);
        _clusterPipeline.SetCamera(view, proj, motionProj, cameraPosition, _cameraHistory);

        RenderFrameData frameData = new(
            new FrameData(
                device,
                backBufferDesc,
                swapchain.CurrentTexture,
                swapchain.CurrentRenderTargetView),
            new ViewData(backBufferDesc.Width, backBufferDesc.Height));
        var graphQueues = new GraphQueues(device, queue);
        using (Profiler.BeginScope("Editor.RenderGraph.BeginFrame"))
        {
            _renderGraph.BeginFrame(frameData, static (graph, data) => RecordRenderFrame(graph, data));
        }
        using (Profiler.BeginScope("Editor.RenderGraph.Compile"))
        {
            _renderGraph.Compile(graphQueues);
        }
        using (Profiler.BeginScope("Editor.RenderGraph.ExecuteFrame"))
        {
            _renderGraph.Execute(graphQueues, swapchain, _renderContext.PresentSyncInterval);
        }

        _frameIndex++;
        if (_frameLimit > 0 && _frameIndex >= _frameLimit)
            _window?.Close();
    }
}
