using Silk.NET.Windowing;
using Silk.NET.Maths;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Systems;
using SomeEngine.Core.ECS;
using SomeEngine.Core.Math;
using System.Numerics;
using Diligent;
using SomeEngine.Assets.Schema; // Added

namespace SomeEngine.Editor;

class Program
{
    private static IWindow? _window;
    private static RenderContext? _renderContext;
    private static TriangleRenderPass? _trianglePass;
    private static ClusterRenderPass? _clusterPass;
    private static ClusterResourceManager? _clusterManager;
    private static RenderGraph? _renderGraph;
    private static GameWorld? _gameWorld;
    private static TransformSyncSystem? _transformSync;

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

        _transformSync = new TransformSyncSystem(_renderContext);
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
            e.AddComponent(new TransformQvvs(new Vector3((i % 10 - 4.5f) * 1.5f, (i / 10 - 4.5f) * 1.5f, 0), Quaternion.Identity));
        }

        _renderGraph = new RenderGraph();
        _trianglePass = new TriangleRenderPass(_renderContext);
        _trianglePass.TransformSystem = _transformSync;
        _trianglePass.InitPSO();
        
        _clusterPass = new ClusterRenderPass(_renderContext, _transformSync, _clusterManager);
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
        if (_renderContext == null || _renderGraph == null || _transformSync == null) return;
        
        if (_clusterPass == null) return;

        _renderGraph.Reset();
        
        var bbView = _renderContext.SwapChain?.GetCurrentBackBufferRTV();
        if (bbView == null) return;
        
        var bbHandle = _renderGraph.ImportTexture("BackBuffer", bbView.GetTexture(), ResourceState.Common, bbView);
        
        // Use Cluster Pass instead of Triangle Pass
        // Or both? Cluster pass assumes it draws to a generic RT for now since I didn't verify its Output setup.
        // Wait, ClusterRenderPass SetOutput? 
        // I didn't implement SetOutput on ClusterRenderPass. It inherits RenderPass...
        
        // I need to manually set the output in ClusterRenderPass definition or externally.
        // TrianglePass has `SetOutput(bbHandle)`. 
        // I'll update ClusterRenderPass to allow setting output, or use `_trianglePass` and wire it up.
        // For now, I'll hack `ClusterRenderPass` to use ImmediateContext to Clear and SetTargets inside Execute?
        // No, RenderGraph should handle it.
        // I'll add `SetOutput` to `ClusterRenderPass`.
        
        // Actually RenderPass base doesn't have SetOutput? TriangleRenderPass defined it.
        // I'll skip RenderGraph for `ClusterPass` for a quick test?
        // No, `_renderGraph.Execute` runs passes.
        
        _renderGraph.AddPass(_clusterPass); 
        // But `ClusterRenderPass` needs to know WHERE to draw.
        // I'll modify `ClusterRenderPass` to accept an Output Handle.
        
        _renderGraph.Compile(); 
        // Compile resolves dependencies. If ClusterPass doesn't declare outputs, RenderGraph might prune it?
        // Or if it has no inputs/outputs...
        
        // Let's manually invoke for test:
        var ctx = _renderContext.ImmediateContext;
        ITextureView[] rtv = { bbView };
        ctx.SetRenderTargets(rtv, _renderContext.SwapChain.GetDepthBufferDSV(), ResourceStateTransitionMode.Transition);
        var clearColor = new Vector4(0.1f, 0.2f, 0.4f, 1.0f);
        ctx.ClearRenderTarget(bbView, clearColor, ResourceStateTransitionMode.Transition);
        ctx.ClearDepthStencil(_renderContext.SwapChain.GetDepthBufferDSV(), ClearDepthStencilFlags.Depth, 1.0f, 0, ResourceStateTransitionMode.Transition);
        
        _clusterPass.Execute(_renderContext, null!); 
        // graphContext null might crash if I used it. I didn't use it in Execute implementation.

        _renderContext.Present();
    }
}
