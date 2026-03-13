using System.Numerics;
using Diligent;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets.Schema; // Added
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Editor;

class Program
{
    private static IWindow? _window;
    private static RenderContext? _renderContext;
    private static TriangleRenderPass? _trianglePass;
    private static ClusterRenderFeature? _clusterPipeline;
    private static ClusterResourceManager? _clusterManager;
    private static RenderGraph? _renderGraph;
    private static GameWorld? _gameWorld;
    private static InstanceSyncSystem? _transformSync;
    private static InstanceDataManager? _instanceDataManager;

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
        _transformSync = new InstanceSyncSystem(_instanceDataManager);
        _gameWorld.SystemRoot.Add(_transformSync);

        // Create Clusters
        _clusterManager = new ClusterResourceManager(_renderContext);
        // var cube = CreateCube(); // Deprecated
        // _clusterManager.AddMesh(cube); // Deprecated signature
        // _clusterManager.CommitPageTable(); // New API call if we had data

        // Create Test Entities
        for (int i = 0; i < 100; i++)
        {
            var e = _gameWorld.EntityStore.CreateEntity();
            e.AddComponent(
                new TransformQvvs(
                    new Vector3((i % 10 - 4.5f) * 1.5f, (i / 10 - 4.5f) * 1.5f, 0),
                    Quaternion.Identity
                )
            );
            e.AddComponent(new MeshInstance { BVHRootIndex = 0 }); // Placeholder root
        }

        _renderGraph = new RenderGraph();
        _trianglePass = new TriangleRenderPass(_renderContext);
        _trianglePass.TransformSystem = _instanceDataManager;
        _trianglePass.InitPSO();

        var services = new ServiceCollection();
        services.AddSingleton(_renderContext);
        services.AddSingleton(_instanceDataManager!);
        services.AddSingleton(_clusterManager);
        services.AddSingleton<ClusterRenderFeature>();
        var provider = services.BuildServiceProvider();

        _clusterPipeline = provider.GetRequiredService<ClusterRenderFeature>();
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
        var bbHandle = _renderGraph.Import(
            "ColorTarget",
            bbView.GetTexture(),
            ResourceState.RenderTarget
        );
        var depthHandle = _renderGraph.CreateTexture("DepthTarget", _renderContext.DepthBufferDesc);

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

        _clusterPipeline!.AddPasses(_renderGraph);
        _renderGraph.Compile(_renderContext.Device);
        _renderGraph.Execute(_renderContext);

        _renderContext.Present();
    }
}
