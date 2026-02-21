using System;
using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.Systems;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Data;
using System.IO;
using FlatSharp;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Silk.NET.Maths;
using SomeEngine.Core.ECS;
using SomeEngine.Core.Math;
using System.Numerics;
using System.Linq;
using Friflo.Engine.ECS;
using ImGuiNET;
using SomeEngine.Render.Utils;

namespace SomeEngine.Runtime;

class Program
{
    static void Main(string[] args)
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = "SomeEngine Runtime - Cluster Rendering";
        options.API = GraphicsAPI.None; // We use Diligent

        var window = Window.Create(options);

        RenderContext? context = null;
        ClusterResourceManager? resourceManager = null;
        ClusterRenderPass? clusterPass = null;
        SimpleMeshRenderPass? simplePass = null;
        ImGuiRenderer? imguiRenderer = null;
        ImGuiInputHandler? imguiInput = null;
        GameWorld? world = null;
        TransformSyncSystem? transformSystem = null;
        IInputContext? input = null;
        IKeyboard? keyboard = null;
        IMouse? mouse = null;
        int debugLOD = -1;
        bool _key1Pressed = false;
        bool _key2Pressed = false;
        bool _key3Pressed = false;
        bool _key4Pressed = false;
        bool showEntityEditor = true;

        var camera = new FreeCamera(
            position: new Vector3(0, 0, -3),
            yaw: MathF.PI * 0.5f,
            pitch: 0.0f,
            fovY: MathF.PI / 4.0f,
            nearPlane: 0.1f,
            farPlane: 1000.0f
        );
        var lastMousePos = new Vector2(0, 0);
        bool mouseInitialized = false;

        window.Load += () =>
        {
            context = new RenderContext();
            context.Initialize(window);

            // 1. Init ECS & Systems
            world = new GameWorld();
            transformSystem = new TransformSyncSystem(context);
            world.SystemRoot.Add(transformSystem);

            // Create Test Entity
            var entity = world.EntityStore.CreateEntity();
            entity.AddComponent(new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity, 1.0f));

            // 2. Init Cluster Manager
            resourceManager = new ClusterResourceManager(context);
            
            // 3. Load Asset
            string assetPath = "samples/IcoSphere.mesh"; 
            if (!File.Exists(assetPath)) 
            {
                assetPath = "../../../../../samples/IcoSphere.mesh";
                if (!File.Exists(assetPath))
                     // Try relative to workspace root if running from other cwd
                     assetPath = "d:/SomeEngine/samples/IcoSphere.mesh";
            }
            
            if (File.Exists(assetPath))
            {
                byte[] bytes = File.ReadAllBytes(assetPath);
                var meshAsset = MeshAsset.Serializer.Parse(bytes);
                resourceManager.AddMesh(meshAsset);
                resourceManager.CommitPageTable();
                Console.WriteLine($"Loaded {assetPath}");
            }
            else
            {
                Console.WriteLine("Warning: IcoSphere.mesh not found!");
            }

            // 4. Init Pipeline
            clusterPass = new ClusterRenderPass(context, transformSystem, resourceManager);
            clusterPass.Init();

            simplePass = new SimpleMeshRenderPass(context);
            simplePass.Init();

            Console.WriteLine("Controls:");
            Console.WriteLine("  WASD + Space/Ctrl: Move");
            Console.WriteLine("  Shift: Speed Boost");
            Console.WriteLine("  Scroll: Adjust LOD");
            Console.WriteLine("  1: Toggle Overdraw");
            Console.WriteLine("  2: Toggle Debug Spheres");
            Console.WriteLine("  3: Toggle Wireframe");
            Console.WriteLine("  4: Toggle ClusterID");

            input = window.CreateInput();
            keyboard = input.Keyboards.FirstOrDefault();
            mouse = input.Mice.FirstOrDefault();

            imguiRenderer = new ImGuiRenderer(context);
            imguiInput = new ImGuiInputHandler(input, window);

            if (mouse != null)
            {
                mouse.Scroll += (m, scroll) =>
                {
                    if (ImGui.GetIO().WantCaptureMouse) return;
                    if (scroll.Y > 0) debugLOD++;
                    else if (scroll.Y < 0) debugLOD--;

                    if (debugLOD < -1) debugLOD = -1;
                    Console.WriteLine($"LOD Mode: {(debugLOD == -1 ? "Auto" : debugLOD.ToString())}");
                };
            }
        };

        window.Update += (double delta) =>
        {
            imguiInput?.Update((float)delta);
        };

        window.Render += (double delta) =>
        {
            if (context == null || clusterPass == null || world == null) return;
            
            // Update Logic
            world.Update(delta);

            float dt = (float)delta;
            float moveSpeed = 6.0f;
            float lookSpeed = 0.0035f;

            var io = ImGui.GetIO();

            if (keyboard != null && !io.WantCaptureKeyboard)
            {
                if (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight))
                    moveSpeed *= 3.0f;

                Vector3 move = Vector3.Zero;
                if (keyboard.IsKeyPressed(Key.W)) move.Z += 1.0f;
                if (keyboard.IsKeyPressed(Key.S)) move.Z -= 1.0f;
                if (keyboard.IsKeyPressed(Key.D)) move.X += 1.0f;
                if (keyboard.IsKeyPressed(Key.A)) move.X -= 1.0f;
                if (keyboard.IsKeyPressed(Key.Space)) move.Y += 1.0f;
                if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight)) move.Y -= 1.0f;

                if (keyboard.IsKeyPressed(Key.Number1))
                {
                    if (!_key1Pressed)
                    {
                        clusterPass.OverdrawEnabled = !clusterPass.OverdrawEnabled;
                        _key1Pressed = true;
                    }
                }
                else
                {
                    _key1Pressed = false;
                }

                if (keyboard.IsKeyPressed(Key.Number2))
                {
                    if (!_key2Pressed)
                    {
                        clusterPass.DebugSpheresEnabled = !clusterPass.DebugSpheresEnabled;
                        _key2Pressed = true;
                    }
                }
                else
                {
                    _key2Pressed = false;
                }

                if (keyboard.IsKeyPressed(Key.Number3))
                {
                    if (!_key3Pressed)
                    {
                        clusterPass.WireframeEnabled = !clusterPass.WireframeEnabled;
                        _key3Pressed = true;
                    }
                }
                else
                {
                    _key3Pressed = false;
                }

                if (keyboard.IsKeyPressed(Key.Number4))
                {
                    if (!_key4Pressed)
                    {
                        clusterPass.DebugClusterID = !clusterPass.DebugClusterID;
                        _key4Pressed = true;
                    }
                }
                else
                {
                    _key4Pressed = false;
                }

                if (move != Vector3.Zero)
                {
                    move = Vector3.Normalize(move) * (moveSpeed * dt);
                    camera.MoveLocal(move);
                }
            }

            if (mouse != null && !io.WantCaptureMouse)
            {
                var mp = mouse.Position;
                var mousePos = new Vector2(mp.X, mp.Y);
                if (!mouseInitialized)
                {
                    lastMousePos = mousePos;
                    mouseInitialized = true;
                }

                var deltaMouse = mousePos - lastMousePos;
                lastMousePos = mousePos;

                if (mouse.IsButtonPressed(MouseButton.Right))
                {
                    camera.AddYawPitch(deltaMouse.X * lookSpeed, -deltaMouse.Y * lookSpeed);
                }
            }

            // Start ImGui Frame
            ImGui.NewFrame();
            if (showEntityEditor)
            {
                if (ImGui.Begin("Engine Debug", ref showEntityEditor))
                {
                    if (ImGui.CollapsingHeader("Rendering", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        bool overdraw = clusterPass.OverdrawEnabled;
                        if (ImGui.Checkbox("Overdraw", ref overdraw)) clusterPass.OverdrawEnabled = overdraw;
                        
                        bool wireframe = clusterPass.WireframeEnabled;
                        if (ImGui.Checkbox("Wireframe", ref wireframe)) clusterPass.WireframeEnabled = wireframe;
                        
                        bool debugSpheres = clusterPass.DebugSpheresEnabled;
                        if (ImGui.Checkbox("Debug Spheres", ref debugSpheres)) clusterPass.DebugSpheresEnabled = debugSpheres;
                        
                        bool clusterId = clusterPass.DebugClusterID;
                        if (ImGui.Checkbox("Debug Cluster ID", ref clusterId)) clusterPass.DebugClusterID = clusterId;

                        ImGui.Separator();
                        ImGui.Text($"LOD Mode: {(debugLOD == -1 ? "Auto" : debugLOD.ToString())}");
                        if (ImGui.SliderInt("Manual LOD", ref debugLOD, -1, 10)) { }
                    }

                    if (ImGui.CollapsingHeader("Entities", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        if (ImGui.Button("Add Entity"))
                        {
                            var e = world.EntityStore.CreateEntity();
                            e.AddComponent(new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity, 1.0f));
                        }

                        foreach (var entity in world.EntityStore.Entities)
                        {
                            if (ImGui.TreeNode($"Entity {entity.Id}"))
                            {
                                if (entity.HasComponent<TransformQvvs>())
                                {
                                    ref var transform = ref entity.GetComponent<TransformQvvs>();
                                    Vector3 pos = transform.Position;
                                    if (ImGui.DragFloat3("Position", ref pos, 0.1f)) transform.Position = pos;
                                    
                                    float scale = transform.Scale;
                                    if (ImGui.DragFloat("Scale", ref scale, 0.05f)) transform.Scale = scale;
                                }
                                ImGui.TreePop();
                            }
                        }
                    }
                }
                ImGui.End();
            }
            ImGui.Render();

            var scDesc = context.SwapChain!.GetDesc();
            float aspect = scDesc.Width / (float)Math.Max(scDesc.Height, 1u);
            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix(aspect);
            var lodScale = camera.GetLodScale(scDesc.Height);
            clusterPass.SetCamera(view, proj, camera.Position, 1.0f, lodScale, debugLOD);

            // Clear
            var pRTV = context.SwapChain!.GetCurrentBackBufferRTV();
            var pDSV = context.SwapChain!.GetDepthBufferDSV();
            
            context.ImmediateContext!.SetRenderTargets(new[] { pRTV }, pDSV, ResourceStateTransitionMode.Transition);
            context.ImmediateContext.ClearRenderTarget(pRTV, new System.Numerics.Vector4(0.1f, 0.1f, 0.15f, 1.0f), ResourceStateTransitionMode.Transition);
            context.ImmediateContext.ClearDepthStencil(pDSV, ClearDepthStencilFlags.Depth | ClearDepthStencilFlags.Stencil, 1.0f, 0, ResourceStateTransitionMode.Transition);

            // Render
            clusterPass.Execute(context, null!);
            // simplePass?.Execute(context, null);

            // Render ImGui
            imguiRenderer?.Render(context.ImmediateContext, ImGui.GetDrawData());

            // Present handled by RenderContext helper or manually
            context.Present();
        };

        
        window.Resize += (Vector2D<int> size) =>
        {
            context?.Resize((uint)size.X, (uint)size.Y);
        };

        window.Closing += () =>
        {
            input?.Dispose();
            imguiRenderer?.Dispose();
            simplePass?.Dispose();
            clusterPass?.Dispose();
            resourceManager?.Dispose();
            context?.Dispose();
        };

        window.Run();
    }
}
