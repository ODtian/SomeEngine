using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using FlatSharp;
using Friflo.Engine.ECS;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Utils;

namespace SomeEngine.Runtime;

internal static class NativeFileDialog
{
    private const int MaxPath = 1024;
    private const uint OfnPathMustExist = 0x00000800;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnNoChangeDir = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDir;
        public nint Title;
        public uint Flags;
        public short FileOffset;
        public short FileExtension;
        public nint DefExt;
        public nint CustData;
        public nint Hook;
        public nint TemplateName;
        public nint ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    public static string? ShowOpenModelDialog(string title, string? initialDirectory)
    {
        string filter = "GLTF/GLB files (*.gltf;*.glb)\0*.gltf;*.glb\0All files (*.*)\0*.*\0\0";
        nint filterPtr = nint.Zero;
        nint filePtr = nint.Zero;
        nint initialDirPtr = nint.Zero;
        nint titlePtr = nint.Zero;

        try
        {
            filterPtr = Marshal.StringToHGlobalUni(filter);
            filePtr = Marshal.AllocHGlobal(MaxPath * sizeof(char));
            Marshal.Copy(new byte[MaxPath * sizeof(char)], 0, filePtr, MaxPath * sizeof(char));

            if (!string.IsNullOrWhiteSpace(initialDirectory))
                initialDirPtr = Marshal.StringToHGlobalUni(initialDirectory);
            titlePtr = Marshal.StringToHGlobalUni(title);

            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf(typeof(OpenFileName)),
                Filter = filterPtr,
                File = filePtr,
                MaxFile = MaxPath,
                Title = titlePtr,
                InitialDir = initialDirPtr,
                FilterIndex = 1,
                Flags = OfnPathMustExist | OfnFileMustExist | OfnNoChangeDir,
            };

            if (!GetOpenFileName(ref ofn))
                return null;

            return Marshal.PtrToStringUni(filePtr);
        }
        finally
        {
            if (titlePtr != nint.Zero)
                Marshal.FreeHGlobal(titlePtr);
            if (initialDirPtr != nint.Zero)
                Marshal.FreeHGlobal(initialDirPtr);
            if (filePtr != nint.Zero)
                Marshal.FreeHGlobal(filePtr);
            if (filterPtr != nint.Zero)
                Marshal.FreeHGlobal(filterPtr);
        }
    }
}

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
        ClusterPipeline? clusterPipeline = null;
        RenderGraph? renderGraph = null;
        SimpleMeshRenderPass? simplePass = null;
        ImGuiRenderer? imguiRenderer = null;
        ImGuiInputHandler? imguiInput = null;
        GameWorld? world = null;
        InstanceSyncSystem? transformSystem = null;
        IInputContext? input = null;
        IKeyboard? keyboard = null;
        IMouse? mouse = null;
        int debugLOD = -1;
        bool _key1Pressed = false;
        bool _key2Pressed = false;
        bool _key3Pressed = false;
        bool _key4Pressed = false;
        bool showEntityEditor = true;
        int spawnedEntityCount = 1;
        int selectedAvailableMeshIndex = 0;
        int selectedEntityMeshIndex = 0;
        string importModelPath = string.Empty;
        string meshUiMessage = string.Empty;
        List<string> availableMeshes = new();
        var random = new Random();

        static string ResolveSamplesDirectory()
        {
            string[] candidates =
            [
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples")),
                Path.GetFullPath("samples"),
                Path.GetFullPath("../../../../../samples"),
                "d:/SomeEngine/samples"
            ];

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return candidates[0];
        }

        string samplesDirectory = ResolveSamplesDirectory();

        void RefreshAvailableMeshes()
        {
            availableMeshes.Clear();

            if (!Directory.Exists(samplesDirectory))
                return;

            availableMeshes.AddRange(
                Directory
                    .EnumerateFiles(samplesDirectory, "*.mesh", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            );

            if (selectedAvailableMeshIndex >= availableMeshes.Count)
                selectedAvailableMeshIndex = Math.Max(availableMeshes.Count - 1, 0);
        }

        bool TryLoadMeshFromFile(string meshFilePath, out string message)
        {
            message = string.Empty;

            if (resourceManager == null)
            {
                message = "Load Mesh failed: resource manager is not initialized.";
                return false;
            }

            if (!File.Exists(meshFilePath))
            {
                message = $"Load Mesh failed: file does not exist: {meshFilePath}";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(meshFilePath);
                var meshAsset = MeshAsset.Serializer.Parse(bytes);
                uint rootIndex = resourceManager.AddMesh(meshAsset);

                if (rootIndex == uint.MaxValue)
                {
                    message = $"Load Mesh failed: {Path.GetFileName(meshFilePath)} produced invalid BVH root.";
                    return false;
                }

                string loadedName = meshAsset.Name ?? Path.GetFileNameWithoutExtension(meshFilePath);
                message =
                    $"Loaded mesh '{loadedName}' from {Path.GetFileName(meshFilePath)} (BVHRootIndex={rootIndex}).";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Load Mesh failed: {ex.Message}";
                return false;
            }
        }

        bool TryImportModelToMesh(string modelPath, out string message)
        {
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                message = "Import failed: model path is empty.";
                return false;
            }

            string resolvedPath = Path.GetFullPath(modelPath);
            string ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
            if (ext != ".gltf" && ext != ".glb")
            {
                message = "Import failed: only .gltf or .glb files are supported.";
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                message = $"Import failed: file does not exist: {resolvedPath}";
                return false;
            }

            try
            {
                var importedMesh = ClusterBuilder.Process(resolvedPath);
                string outBaseName = Path.GetFileNameWithoutExtension(resolvedPath);
                importedMesh.Name = outBaseName;

                Directory.CreateDirectory(samplesDirectory);
                string outMeshPath = Path.Combine(samplesDirectory, outBaseName + ".mesh");
                MeshAssetSerializer.Save(importedMesh, outMeshPath);

                RefreshAvailableMeshes();
                selectedAvailableMeshIndex = 0;
                message = $"Imported {Path.GetFileName(resolvedPath)} to {outMeshPath}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Import failed: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"Import failed for '{resolvedPath}': {ex}");
                return false;
            }
        }

        string? TryPickImportModelPath()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            string initialDirectory = Directory.Exists(samplesDirectory)
                ? samplesDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            return NativeFileDialog.ShowOpenModelDialog("Select GLTF/GLB file", initialDirectory);
        }

        void SpawnEntity(GameWorld targetWorld, uint rootIndex, Vector3 position, float scale)
        {
            var e = targetWorld.EntityStore.CreateEntity();
            e.AddComponent(new TransformQvvs(position, Quaternion.Identity, scale));
            e.AddComponent(new MeshInstance { BVHRootIndex = rootIndex });
        }

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
            transformSystem = new InstanceSyncSystem(context);
            world.SystemRoot.Add(transformSystem);

            // 2. Init Cluster Manager
            resourceManager = new ClusterResourceManager(context);

            // 3. Discover and optionally load mesh assets from samples/
            RefreshAvailableMeshes();

            if (availableMeshes.Count > 0)
            {
                if (TryLoadMeshFromFile(availableMeshes[0], out string loadMessage))
                {
                    Console.WriteLine(loadMessage);

                    if (resourceManager.MeshBVHRoots.Count > 0)
                    {
                        var firstLoaded = resourceManager
                            .MeshBVHRoots
                            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .First();

                        var entity = world.EntityStore.CreateEntity();
                        entity.AddComponent(
                            new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity, 1.0f)
                        );
                        entity.AddComponent(new MeshInstance { BVHRootIndex = firstLoaded.Value });
                    }
                }
                else
                {
                    Console.WriteLine(loadMessage);
                }
            }
            else
            {
                Console.WriteLine($"Warning: no .mesh files found in {samplesDirectory}");
            }

            // 4. Init Pipeline
            clusterPipeline = new ClusterPipeline(context, transformSystem, resourceManager);
            clusterPipeline.Init();
            renderGraph = new RenderGraph();

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
                    if (ImGui.GetIO().WantCaptureMouse)
                        return;
                    if (scroll.Y > 0)
                        debugLOD++;
                    else if (scroll.Y < 0)
                        debugLOD--;

                    if (debugLOD < -1)
                        debugLOD = -1;
                    Console.WriteLine(
                        $"LOD Mode: {(debugLOD == -1 ? "Auto" : debugLOD.ToString())}"
                    );
                };
            }
        };

        window.Update += (double delta) =>
        {
            imguiInput?.Update((float)delta);
        };

        window.Render += (double delta) =>
        {
            if (
                context == null
                || clusterPipeline == null
                || world == null
                || renderGraph == null
                || resourceManager == null
            )
                return;

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
                if (keyboard.IsKeyPressed(Key.W))
                    move.Z += 1.0f;
                if (keyboard.IsKeyPressed(Key.S))
                    move.Z -= 1.0f;
                if (keyboard.IsKeyPressed(Key.D))
                    move.X += 1.0f;
                if (keyboard.IsKeyPressed(Key.A))
                    move.X -= 1.0f;
                if (keyboard.IsKeyPressed(Key.Space))
                    move.Y += 1.0f;
                if (
                    keyboard.IsKeyPressed(Key.ControlLeft)
                    || keyboard.IsKeyPressed(Key.ControlRight)
                )
                    move.Y -= 1.0f;

                if (keyboard.IsKeyPressed(Key.Number1))
                {
                    if (!_key1Pressed)
                    {
                        clusterPipeline.OverdrawEnabled = !clusterPipeline.OverdrawEnabled;
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
                        clusterPipeline.DebugSpheresEnabled = !clusterPipeline.DebugSpheresEnabled;
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
                        clusterPipeline.WireframeEnabled = !clusterPipeline.WireframeEnabled;
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
                        clusterPipeline.DebugClusterID = !clusterPipeline.DebugClusterID;
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
                        bool overdraw = clusterPipeline.OverdrawEnabled;
                        if (ImGui.Checkbox("Overdraw", ref overdraw))
                            clusterPipeline.OverdrawEnabled = overdraw;

                        bool wireframe = clusterPipeline.WireframeEnabled;
                        if (ImGui.Checkbox("Wireframe", ref wireframe))
                            clusterPipeline.WireframeEnabled = wireframe;

                        bool debugSpheres = clusterPipeline.DebugSpheresEnabled;
                        if (ImGui.Checkbox("Debug Spheres", ref debugSpheres))
                            clusterPipeline.DebugSpheresEnabled = debugSpheres;

                        bool visualizeBVH = clusterPipeline.VisualiseBVH;
                        if (ImGui.Checkbox("Visualize BVH", ref visualizeBVH))
                            clusterPipeline.VisualiseBVH = visualizeBVH;

                        int bvhDepth = clusterPipeline.DebugBVHDepth;
                        if (ImGui.SliderInt("BVH Depth (-1=All)", ref bvhDepth, -1, 16))
                            clusterPipeline.DebugBVHDepth = bvhDepth;

                        bool clusterId = clusterPipeline.DebugClusterID;
                        if (ImGui.Checkbox("Debug Cluster ID", ref clusterId))
                            clusterPipeline.DebugClusterID = clusterId;

                        ImGui.Separator();
                        if (ImGui.TreeNode("BVH Details"))
                        {
                            for (var i = 0; i < 8; i++)
                            {
                                var groups =
                                    clusterPipeline.DebugBVHGroupCount.Length > i
                                        ? clusterPipeline.DebugBVHGroupCount[i]
                                        : 0;
                                var items =
                                    clusterPipeline.DebugBVHItemCount.Length > i
                                        ? clusterPipeline.DebugBVHItemCount[i]
                                        : 0;
                                if (groups > 0 || items > 0)
                                {
                                    ImGui.Text($"Level {i}: {groups} groups, {items} items");
                                }
                            }
                            ImGui.TreePop();
                        }

                        ImGui.Separator();
                        ImGui.Text($"LOD Mode: {(debugLOD == -1 ? "Auto" : debugLOD.ToString())}");
                        if (ImGui.SliderInt("Manual LOD", ref debugLOD, -1, 10)) { }
                    }

                    if (ImGui.CollapsingHeader("Meshes", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.Text($"Samples dir: {samplesDirectory}");
                        if (ImGui.Button("Refresh Mesh List"))
                        {
                            RefreshAvailableMeshes();
                        }

                        if (!string.IsNullOrWhiteSpace(meshUiMessage))
                        {
                            ImGui.TextWrapped(meshUiMessage);
                        }

                        ImGui.Separator();
                        ImGui.Text("Loaded Mesh Assets");
                        if (resourceManager.MeshBVHRoots.Count == 0)
                        {
                            ImGui.TextDisabled("No meshes loaded.");
                        }
                        else
                        {
                            foreach (
                                var pair in resourceManager
                                    .MeshBVHRoots
                                    .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                            )
                            {
                                ImGui.BulletText($"{pair.Key} (BVHRootIndex={pair.Value})");
                            }
                        }

                        ImGui.Separator();
                        var unloadedMeshPaths = availableMeshes
                            .Where(path =>
                            {
                                string baseName = Path.GetFileNameWithoutExtension(path);
                                return !resourceManager.MeshBVHRoots.Keys.Any(key =>
                                    string.Equals(key, baseName, StringComparison.OrdinalIgnoreCase)
                                );
                            })
                            .ToList();

                        if (unloadedMeshPaths.Count > 0)
                        {
                            if (selectedAvailableMeshIndex >= unloadedMeshPaths.Count)
                                selectedAvailableMeshIndex = unloadedMeshPaths.Count - 1;

                            string selectedFileName = Path.GetFileName(
                                unloadedMeshPaths[selectedAvailableMeshIndex]
                            );

                            if (ImGui.BeginCombo("Available .mesh", selectedFileName))
                            {
                                for (int i = 0; i < unloadedMeshPaths.Count; i++)
                                {
                                    string label = Path.GetFileName(unloadedMeshPaths[i]);
                                    bool isSelected = i == selectedAvailableMeshIndex;
                                    if (ImGui.Selectable(label, isSelected))
                                        selectedAvailableMeshIndex = i;
                                    if (isSelected)
                                        ImGui.SetItemDefaultFocus();
                                }
                                ImGui.EndCombo();
                            }

                            ImGui.SameLine();
                            if (ImGui.Button("Load Mesh"))
                            {
                                string selectedPath = unloadedMeshPaths[selectedAvailableMeshIndex];
                                if (TryLoadMeshFromFile(selectedPath, out string loadMessage))
                                {
                                    meshUiMessage = loadMessage;
                                    Console.WriteLine(loadMessage);
                                }
                                else
                                {
                                    meshUiMessage = loadMessage;
                                    Console.WriteLine(loadMessage);
                                }
                            }
                        }
                        else
                        {
                            ImGui.TextDisabled("No unloaded .mesh files available.");
                        }

                        ImGui.Separator();
                        ImGui.InputText("Import Path", ref importModelPath, 1024);
                        if (ImGui.Button("Import GLTF/GLB..."))
                        {
                            string? pickedPath = TryPickImportModelPath();
                            if (!string.IsNullOrWhiteSpace(pickedPath))
                            {
                                importModelPath = pickedPath;
                            }

                            if (TryImportModelToMesh(importModelPath, out string importMessage))
                            {
                                meshUiMessage = importMessage;
                                Console.WriteLine(importMessage);
                            }
                            else
                            {
                                meshUiMessage = importMessage;
                                Console.WriteLine(importMessage);
                            }
                        }
                    }

                    if (ImGui.CollapsingHeader("Entities", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.Text($"Runtime instances: {transformSystem?.Count ?? 0}");

                        var loadedMeshes = resourceManager
                            .MeshBVHRoots
                            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        if (loadedMeshes.Length > 0)
                        {
                            if (selectedEntityMeshIndex >= loadedMeshes.Length)
                                selectedEntityMeshIndex = loadedMeshes.Length - 1;

                            string selectedMeshLabel =
                                $"{loadedMeshes[selectedEntityMeshIndex].Key} ({loadedMeshes[selectedEntityMeshIndex].Value})";
                            if (ImGui.BeginCombo("Target Mesh", selectedMeshLabel))
                            {
                                for (int i = 0; i < loadedMeshes.Length; i++)
                                {
                                    bool isSelected = i == selectedEntityMeshIndex;
                                    string option =
                                        $"{loadedMeshes[i].Key} ({loadedMeshes[i].Value})";
                                    if (ImGui.Selectable(option, isSelected))
                                        selectedEntityMeshIndex = i;
                                    if (isSelected)
                                        ImGui.SetItemDefaultFocus();
                                }
                                ImGui.EndCombo();
                            }
                        }
                        else
                        {
                            ImGui.TextDisabled("No loaded mesh roots available for new entities.");
                        }

                        if (ImGui.Button("Add Entity"))
                        {
                            if (loadedMeshes.Length == 0)
                            {
                                Console.WriteLine("Add Entity skipped: no mesh BVH root loaded.");
                            }
                            else
                            {
                                uint rootIndex = loadedMeshes[selectedEntityMeshIndex].Value;
                                int spawnIndex = spawnedEntityCount++;
                                float x = (spawnIndex % 5) * 2.5f;
                                float z = (spawnIndex / 5) * 2.5f;
                                SpawnEntity(world, rootIndex, new Vector3(x, 0, z), 1.0f);
                            }
                        }

                        if (ImGui.Button("Add 100 Random Entities"))
                        {
                            if (loadedMeshes.Length == 0)
                            {
                                Console.WriteLine(
                                    "Add 100 Random Entities skipped: no mesh BVH root loaded."
                                );
                            }
                            else
                            {
                                for (int i = 0; i < 100; i++)
                                {
                                    uint rootIndex = loadedMeshes[random.Next(loadedMeshes.Length)].Value;
                                    float x = (random.NextSingle() - 0.5f) * 80.0f;
                                    float y = (random.NextSingle() - 0.5f) * 10.0f;
                                    float z = (random.NextSingle() - 0.5f) * 80.0f;
                                    float scale = 0.5f + random.NextSingle() * 2.0f;
                                    SpawnEntity(world, rootIndex, new Vector3(x, y, z), scale);
                                }
                            }
                        }

                        foreach (var entity in world.EntityStore.Entities)
                        {
                            if (ImGui.TreeNode($"Entity {entity.Id}"))
                            {
                                if (entity.HasComponent<TransformQvvs>())
                                {
                                    ref var transform = ref entity.GetComponent<TransformQvvs>();
                                    Vector3 pos = transform.Position;
                                    if (ImGui.DragFloat3("Position", ref pos, 0.1f))
                                        transform.Position = pos;

                                    float scale = transform.Scale;
                                    if (ImGui.DragFloat("Scale", ref scale, 0.05f))
                                        transform.Scale = scale;
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
            clusterPipeline.SetCamera(view, proj, camera.Position, 1.0f, lodScale, debugLOD);

            var pRTV = context.SwapChain!.GetCurrentBackBufferRTV();
            var pDSV = context.SwapChain!.GetDepthBufferDSV();

            renderGraph.Reset();
            var bbTex = pRTV.GetTexture();
            var colorHandle = renderGraph.ImportTexture(
                "BackBuffer",
                bbTex,
                ResourceState.Unknown,
                pRTV
            );
            var depthTex = pDSV.GetTexture();
            var depthHandle = renderGraph.ImportTexture(
                "DepthBuffer",
                depthTex,
                ResourceState.Unknown,
                pDSV
            );

            renderGraph.AddPass<object>(
                "Clear Main RT",
                (builder, _) =>
                {
                    builder.WriteTexture(colorHandle, ResourceState.RenderTarget);
                    builder.WriteTexture(depthHandle, ResourceState.DepthWrite);
                },
                (ctx, _) =>
                {
                    ctx.CommandList.SetRenderTargets([pRTV], pDSV, ResourceStateTransitionMode.Verify);
                    ctx.CommandList.ClearRenderTarget(pRTV, new System.Numerics.Vector4(0.1f, 0.1f, 0.15f, 1.0f), ResourceStateTransitionMode.Verify);
                    ctx.CommandList.ClearDepthStencil(pDSV, ClearDepthStencilFlags.Depth | ClearDepthStencilFlags.Stencil, 1.0f, 0, ResourceStateTransitionMode.Verify);
                }
            );

            clusterPipeline.AddToRenderGraph(renderGraph, colorHandle, depthHandle);
            renderGraph.Compile(context.Device);
            renderGraph.Execute(context);
            // simplePass?.Execute(context, null);

            // Render ImGui
            imguiRenderer?.Render(context.ImmediateContext!, ImGui.GetDrawData());

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
            clusterPipeline?.Dispose();
            renderGraph?.Dispose();
            resourceManager?.Dispose();
            context?.Dispose();
        };

        window.Run();
    }
}
