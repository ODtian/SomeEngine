using System.Numerics;
using Diligent;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema; // Added
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Editor;

class Program
{
    private static IWindow? _window;
    private static RenderContext? _renderContext;
    private static TriangleRenderPass? _trianglePass;
    private static ClusterPipeline? _clusterPipeline;
    private static ClusterResourceManager? _clusterManager;
    private static RenderWorld? _renderWorld;
    private static RenderGraph? _renderGraph;
    private static readonly FrameTargetRegistry _frameTargets = new();
    private static GlobalPsoCache? _globalPsoCache;
    private static GameWorld? _gameWorld;
    private static InstanceSyncSystem? _transformSync;
    private static InstanceDataManager? _instanceDataManager;
    private static ulong _frameIndex;

    static void Main(string[] args)
    {
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

    private static void OnResize(Vector2D<int> size)
    {
        _renderContext?.Resize((uint)size.X, (uint)size.Y);
    }

    private static void OnClose()
    {
        _clusterPipeline?.Dispose();
        _globalPsoCache?.Dispose();
        _renderContext?.Dispose();
    }

    private static void OnLoad()
    {
        Console.WriteLine("Window Loaded.");

        _gameWorld = new GameWorld();

        _renderContext = new RenderContext();
        if (_window != null)
        {
            _renderContext.Initialize(_window);
        }

        _instanceDataManager = new InstanceDataManager();
        _transformSync = new InstanceSyncSystem(_instanceDataManager, _gameWorld.SystemContext);
        _gameWorld.SystemRoot.Add(_transformSync);

        // Create Clusters
        _clusterManager = new ClusterResourceManager(_renderContext);

        // Create Test Entities
        for (int i = 0; i < 100; i++)
        {
            var e = _gameWorld.EntityStore.CreateEntity();
            e.AddComponent(new LocalTransform
            {
                Value = new TransformQvvs(
                    new Vector3((i % 10 - 4.5f) * 1.5f, (i / 10 - 4.5f) * 1.5f, 0),
                    Quaternion.Identity
                ),
            });
            e.AddComponent(new WorldTransform());
            e.AddComponent(new MeshInstance { BVHRootIndex = 0 }); // Placeholder root
        }

        _renderGraph = new RenderGraph();
        _trianglePass = new TriangleRenderPass(_renderContext);
        _trianglePass.TransformSystem = _instanceDataManager;
        _trianglePass.InitPSO();

        _globalPsoCache = new GlobalPsoCache();
        _renderWorld = new RenderWorld();
        _clusterPipeline = ClusterPipeline.Opaque(
            _renderContext, _clusterManager, _instanceDataManager!, _globalPsoCache, _renderWorld);
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
        if (_gameWorld != null && _clusterPipeline != null)
        {
            _clusterPipeline.PrepareFrame(_gameWorld.EntityStore, _ => null);
        }

        _gameWorld?.Update(deltaTime);
    }

    private static void OnRender(double deltaTime)
    {
        if (_renderContext == null || _renderGraph == null || _transformSync == null)
            return;

        if (_clusterPipeline == null)
            return;

        var bbView = _renderContext.SwapChain?.GetCurrentBackBufferRTV();
        if (bbView == null)
            return;

        _renderGraph!.BeginFrame();
        var scDesc = _renderContext.SwapChain!.GetDesc();
        _frameTargets.BeginFrame(
            _renderGraph,
            new FrameTargetContext(scDesc.Width, scDesc.Height, _frameIndex++)
        );
        _frameTargets.ImportTexture(
            StandardFrameTargets.SceneColor,
            bbView.GetTexture(),
            ResourceState.RenderTarget,
            "SceneColor"
        );
        _frameTargets.DeclareTexture(
            StandardFrameTargets.SceneDepth,
            _ => _renderContext.DepthBufferDesc with { Name = "SceneDepth" },
            FrameTargetLifetime.FrameLocal,
            ResourceState.Unknown,
            "SceneDepth"
        );
        var bbHandle = _frameTargets.ResolveTexture(StandardFrameTargets.SceneColor);
        var depthHandle = _frameTargets.ResolveTexture(StandardFrameTargets.SceneDepth);

        // Clear pass via RG
        _renderGraph.AddPass<object>(
            "Clear Main RT",
            (builder, _) =>
            {
                builder.Write(bbHandle, ResourceState.RenderTarget);
                builder.Write(depthHandle, ResourceState.DepthWrite);
            },
            (rgCtx, _) =>
            {
                var pRTV = bbView;
                var pDSV = rgCtx.GetTextureView(depthHandle, TextureViewType.DepthStencil);
                if (pDSV == null)
                    return;

                rgCtx.RenderContext.ImmediateContext?.SetRenderTargets(
                    [pRTV],
                    pDSV,
                    ResourceStateTransitionMode.Verify
                );
                rgCtx.RenderContext.ImmediateContext?.ClearRenderTarget(
                    pRTV,
                    new Vector4(0.1f, 0.2f, 0.4f, 1.0f),
                    ResourceStateTransitionMode.Verify
                );
                rgCtx.RenderContext.ImmediateContext?.ClearDepthStencil(
                    pDSV,
                    ClearDepthStencilFlags.Depth,
                    1.0f,
                    0,
                    ResourceStateTransitionMode.Verify
                );
            }
        );

        _clusterPipeline!.AddPasses(_renderGraph, _frameTargets);
        _renderGraph.Compile(_renderContext.Device);
        _renderGraph.Execute(_renderContext);

        _renderContext.Present();
    }
}
