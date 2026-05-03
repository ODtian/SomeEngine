using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using FlatSharp;
using Friflo.Engine.ECS;
using ImGuiNET;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
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
        options.VSync = true;
        options.UpdatesPerSecond = 60;
        options.FramesPerSecond = 60;

        var window = Window.Create(options);

        RenderContext? context = null;
        ClusterResourceManager? resourceManager = null;
        ClusterPipeline? clusterPipeline = null;
        EntityStore? materialStore = null;
        RenderWorld? renderWorld = null;
        RenderGraph? renderGraph = null;
        FrameTargetRegistry frameTargets = new();
        GlobalPsoCache? psoCache = null;
        SimpleMeshRenderPass? simplePass = null;
        ImGuiRenderer? imguiRenderer = null;
        ImGuiInputHandler? imguiInput = null;
        GameWorld? world = null;
        InstanceSyncSystem? transformSystem = null;
        InstanceDataManager? instanceDataManager = null;
        IInputContext? input = null;

        // HiZ visualization state
        ITexture? lastHiZTexture = null;
        List<IntPtr> hizMipTexIds = new();
        IKeyboard? keyboard = null;
        IMouse? mouse = null;
        int debugLOD = -1;
        bool _key1Pressed = false;
        bool _key2Pressed = false;
        bool _key3Pressed = false;
        bool _key4Pressed = false;
        bool _keyF5Pressed = false;
        bool _keyF6Pressed = false;
        bool showEntityEditor = true;
        int spawnedEntityCount = 1;
        int startupInstanceCount = ResolveStartupInstanceCount();
        int selectedAvailableMeshIndex = 0;
        int selectedEntityMeshIndex = 0;
        string importModelPath = string.Empty;
        string meshUiMessage = string.Empty;
        List<string> availableMeshes = new();
        var random = new Random();
        AssetDatabase? assetDb = null;
        var materialCache = new Dictionary<AssetGuid, Material>();
        var textureCache = new Dictionary<AssetGuid, ITexture>();
        var availableMaterialGuids = new List<AssetGuid>();
        Func<AssetGuid, Material?>? resolveRuntimeMaterial = null;
        ClusterDebugMode[] debugModeValues = ClusterDebugModeExtensions.GetValues();
        string[] debugModeNames = ClusterDebugModeExtensions.GetNames();
        HiZDebugMode[] hizModeValues = HiZDebugModeExtensions.GetValues();
        string[] hizModeNames = HiZDebugModeExtensions.GetNames();

        static string? TryFindProjectRoot(string startDirectory)
        {
            string? current = Path.GetFullPath(startDirectory);

            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "SomeEngine.slnx"))
                    && Directory.Exists(Path.Combine(current, "samples")))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return null;
        }

        static string ResolveProjectRoot()
        {
            foreach (string startDirectory in new[]
            {
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory(),
            })
            {
                if (TryFindProjectRoot(startDirectory) is string projectRoot)
                    return projectRoot;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        }

        static string ResolveSamplesDirectory(string projectRoot)
        {
            string[] candidates =
            [
                Path.Combine(projectRoot, "samples"),
                Path.GetFullPath("samples"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples")),
                "d:/SomeEngine/samples",
            ];

            foreach (string candidate in candidates)
            {
                string fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                    return fullPath;
            }

            return Path.GetFullPath(candidates[0]);
        }

        static int ResolveStartupInstanceCount()
        {
            const int defaultInstanceCount = 1024;
            const int maxInstanceCount = 100_000;

            string? value = Environment.GetEnvironmentVariable("SOMEENGINE_RUNTIME_INSTANCE_COUNT");
            if (string.IsNullOrWhiteSpace(value))
                return defaultInstanceCount;

            if (!int.TryParse(value, out int parsed))
            {
                Console.WriteLine(
                    $"Invalid SOMEENGINE_RUNTIME_INSTANCE_COUNT='{value}', using {defaultInstanceCount}."
                );
                return defaultInstanceCount;
            }

            return Math.Clamp(parsed, 1, maxInstanceCount);
        }

        string projectRoot = ResolveProjectRoot();
        string samplesDirectory = ResolveSamplesDirectory(projectRoot);

        void RefreshAvailableMeshes()
        {
            availableMeshes.Clear();

            if (!Directory.Exists(samplesDirectory))
                return;

            availableMeshes.AddRange(
                Directory
                    .EnumerateFiles(samplesDirectory, "*.mesh.asset", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            );

            if (selectedAvailableMeshIndex >= availableMeshes.Count)
                selectedAvailableMeshIndex = Math.Max(availableMeshes.Count - 1, 0);
        }

        void RefreshAvailableMaterialGuids()
        {
            availableMaterialGuids.Clear();
            if (assetDb == null)
            {
                return;
            }

            foreach (AssetManifestRecord record in assetDb.List(nameof(MaterialAsset)))
            {
                string path = record.Path.Replace('\\', '/');
                if (record.Guid.IsEmpty
                    || string.Equals(
                        path,
                        GltfImporterSettings.DefaultUnlitMaterialTemplate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                availableMaterialGuids.Add(record.Guid);
            }
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
                var meshAsset = assetDb!.Load<MeshAsset>(meshFilePath);
                if (meshAsset == null)
                {
                    message = $"Load Mesh failed: AssetDatabase could not load {Path.GetFileName(meshFilePath)}.";
                    return false;
                }
                uint rootIndex = resourceManager.AddMesh(meshAsset);

                if (rootIndex == uint.MaxValue)
                {
                    message =
                        $"Load Mesh failed: {Path.GetFileName(meshFilePath)} produced invalid BVH root.";
                    return false;
                }

                string loadedName =
                    meshAsset.Name ?? Path.GetFileNameWithoutExtension(meshFilePath);
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
                if (assetDb == null)
                {
                    message = "Import failed: asset database is not initialized.";
                    return false;
                }

                IReadOnlyList<AssetGuid> importedGuids = assetDb.Import(resolvedPath);
                int meshCount = importedGuids.Count(guid =>
                {
                    if (!assetDb.Manifest.TryGetAsset(guid, out AssetManifestRecord record))
                    {
                        return false;
                    }

                    return string.Equals(record.AssetType, nameof(MeshAsset), StringComparison.Ordinal);
                });

                RefreshAvailableMeshes();
                RefreshAvailableMaterialGuids();
                selectedAvailableMeshIndex = 0;
                message = $"Imported {Path.GetFileName(resolvedPath)} and generated {meshCount} mesh asset(s).";
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

        void SpawnEntity(
            GameWorld targetWorld,
            uint rootIndex,
            Vector3 position,
            float scale,
            AssetGuid materialAssetGuid = default)
        {
            var e = targetWorld.EntityStore.CreateEntity();
            e.AddComponent(new LocalTransform { Value = new TransformQvvs(position, Quaternion.Identity, scale) });
            e.AddComponent(new WorldTransform());
            e.AddComponent(new MeshInstance { BVHRootIndex = rootIndex });
            if (!materialAssetGuid.IsEmpty)
            {
                e.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialAssetGuid] });
            }
            Console.WriteLine($"SpawnEntity: id={e.Id} rootIndex={rootIndex} pos={position}");
        }

        void SpawnStartupInstances(
            GameWorld targetWorld,
            uint rootIndex,
            int instanceCount,
            IReadOnlyList<AssetGuid> materialGuids
        )
        {
            int columns = (int)MathF.Ceiling(MathF.Sqrt(instanceCount));
            const float spacing = 1.5f;
            const float planeZ = 65.0f;
            const float scale = 0.6f;
            float originX = -((columns - 1) * spacing) * 0.5f;
            float originY = -((columns - 1) * spacing) * 0.5f;

            for (int i = 0; i < instanceCount; i++)
            {
                int xIndex = i % columns;
                int yIndex = i / columns;
                var position = new Vector3(
                    originX + xIndex * spacing,
                    originY + yIndex * spacing,
                    planeZ
                );

                var entity = targetWorld.EntityStore.CreateEntity();
                entity.AddComponent(new LocalTransform
                {
                    Value = new TransformQvvs(position, Quaternion.Identity, scale),
                });
                entity.AddComponent(new WorldTransform());
                entity.AddComponent(new MeshInstance { BVHRootIndex = rootIndex });

                if (materialGuids.Count > 0)
                {
                    AssetGuid materialGuid = materialGuids[i % materialGuids.Count];
                    entity.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });
                }

                float tint = (i % columns) / MathF.Max(columns - 1, 1);
                entity.AddComponent(new MaterialOverride
                {
                    BaseColorTint = new Vector4(0.55f + tint * 0.45f, 0.75f, 1.0f - tint * 0.35f, 1.0f),
                });
            }

            spawnedEntityCount = Math.Max(spawnedEntityCount, instanceCount + 1);
            Console.WriteLine(
                $"Spawned {instanceCount} startup instances for profiling (rootIndex={rootIndex}, grid={columns}x{columns})."
            );
        }

        ITexture? LoadGpuTexture(AssetGuid textureGuid)
        {
            if (textureGuid.IsEmpty || assetDb == null || context == null)
            {
                return null;
            }

            if (textureCache.TryGetValue(textureGuid, out ITexture? cached))
            {
                return cached;
            }

            TextureAsset? asset = assetDb.Load<TextureAsset>(textureGuid);
            if (asset == null)
            {
                return null;
            }

            ITexture texture = CreateGpuTexture(context, asset, textureGuid.ToFlatString());
            textureCache[textureGuid] = texture;
            return texture;
        }

        static ITexture CreateGpuTexture(RenderContext renderContext, TextureAsset asset, string fallbackName)
        {
            TextureFileImage image = TextureFileLoader.Decode(asset, asset.Name ?? fallbackName);
            GCHandle handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
            try
            {
                var texDesc = new TextureDesc
                {
                    Name = asset.Name ?? fallbackName,
                    Type = ResourceDimension.Tex2d,
                    Width = (uint)image.Width,
                    Height = (uint)image.Height,
                    Format = TextureFormat.RGBA8_UNorm,
                    Usage = Usage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };
                var texData = new TextureData
                {
                    SubResources =
                    [
                        new TextureSubResData
                        {
                            Data = handle.AddrOfPinnedObject(),
                            Stride = (uint)(image.Width * 4),
                        },
                    ],
                };

                ITexture texture = renderContext.Device!.CreateTexture(texDesc, texData)
                    ?? throw new InvalidOperationException($"Failed to create texture '{texDesc.Name}'.");
                renderContext.ImmediateContext!.TransitionResourceStates(
                [
                    new StateTransitionDesc
                    {
                        Resource = texture,
                        OldState = ResourceState.Unknown,
                        NewState = ResourceState.ShaderResource,
                        Flags = StateTransitionFlags.UpdateState,
                    },
                ]);
                return texture;
            }
            finally
            {
                handle.Free();
            }
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
            instanceDataManager = new InstanceDataManager();
            transformSystem = new InstanceSyncSystem(instanceDataManager, world.SystemContext);
            world.SystemRoot.Add(transformSystem);

            // 2. Init Cluster Manager
            resourceManager = new ClusterResourceManager(context);

            // 3. Init Pipeline
            materialStore = new EntityStore();
            renderWorld = new RenderWorld();
            psoCache = new GlobalPsoCache();

            clusterPipeline = ClusterPipeline.Opaque(
                context, resourceManager, instanceDataManager!, psoCache, renderWorld);
            clusterPipeline.Initialize(context);
            // ─── Initialize CPU Asset Database ───
            assetDb = global::SomeEngine.Assets.GeneratedAssetPipelineCatalog.CreateDatabase(projectRoot);
            ShaderAsset? LoadShader(AssetGuid guid) => assetDb.Load<ShaderAsset>(guid);

            foreach (string materialPath in new[]
            {
                GltfImporterSettings.DefaultLitMaterialTemplate,
                GltfImporterSettings.DefaultUnlitMaterialTemplate,
            })
            {
                if (assetDb.Resolve(materialPath) == null
                    && File.Exists(Path.Combine(projectRoot, materialPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    assetDb.Import(materialPath);
                }
            }

            renderGraph = new RenderGraph();
            renderGraph.Initialize(context.Device!);

            Diligent.ITextureView? LoadTexture(AssetGuid textureGuid)
            {
                Diligent.ITexture? tex = LoadGpuTexture(textureGuid);
                return tex?.GetDefaultView(Diligent.TextureViewType.ShaderResource);
            }

            resolveRuntimeMaterial = guid =>
            {
                if (guid.IsEmpty || assetDb == null || materialStore == null)
                {
                    return null;
                }

                if (materialCache.TryGetValue(guid, out Material? cachedMaterial))
                {
                    return cachedMaterial;
                }

                MaterialAsset? asset = assetDb.Load<MaterialAsset>(guid);
                if (asset == null)
                {
                    return null;
                }

                Material material = SomeEngine.Render.Assets.MaterialAssetLoader.LoadFromAsset(
                    asset,
                    materialStore,
                    LoadTexture,
                    LoadShader);
                materialCache[guid] = material;
                return material;
            };

            RefreshAvailableMaterialGuids();

            // 4. Discover and optionally load mesh assets from samples/
            RefreshAvailableMeshes();

            if (availableMeshes.Count > 0)
            {
                if (TryLoadMeshFromFile(availableMeshes[0], out string loadMessage))
                {
                    Console.WriteLine(loadMessage);

                    if (resourceManager.MeshBVHRoots.Count > 0)
                    {
                        var firstLoaded = resourceManager
                            .MeshBVHRoots.OrderBy(
                                pair => pair.Key,
                                StringComparer.OrdinalIgnoreCase
                            )
                            .First();

                        SpawnStartupInstances(
                            world,
                            firstLoaded.Value,
                            startupInstanceCount,
                            availableMaterialGuids
                        );
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

        bool logFrameProfile = Environment.GetEnvironmentVariable("SOMEENGINE_FRAME_PROFILE") == "1";
        int frameCount = 0;

        window.Render += (double delta) =>
        {
          try
          {
            ClusterPipeline? activePipeline = clusterPipeline;
            if (
                context == null
                || activePipeline == null
                || world == null
                || renderGraph == null
                || resourceManager == null
            )
                return;
            var _sw = Stopwatch.StartNew();
            // Update Logic
            world.Update(delta);
            var _tUpdate = _sw.Elapsed.TotalMilliseconds; _sw.Restart();
            if (resolveRuntimeMaterial != null)
            {
                activePipeline.PrepareFrame(world.EntityStore, resolveRuntimeMaterial);
            }

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
                        activePipeline.OverdrawEnabled = !activePipeline.OverdrawEnabled;
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
                        activePipeline.DebugSpheresEnabled = !activePipeline.DebugSpheresEnabled;
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
                        activePipeline.WireframeEnabled = !activePipeline.WireframeEnabled;
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
                        activePipeline.DebugClusterID = !activePipeline.DebugClusterID;
                        _key4Pressed = true;
                    }
                }
                else
                {
                    _key4Pressed = false;
                }

                if (keyboard.IsKeyPressed(Key.F5))
                {
                    if (!_keyF5Pressed)
                    {
                        activePipeline.DumpNextFrame = true;
                        Console.WriteLine("[Debug] HiZ dump triggered for next frame...");
                        _keyF5Pressed = true;
                    }
                }
                else
                {
                    _keyF5Pressed = false;
                }

                if (keyboard.IsKeyPressed(Key.F6))
                {
                    if (!_keyF6Pressed)
                    {
                        ClusterSWDraw.DebugDumpNextFrame = true;
                        Console.WriteLine("[Debug] SW raster debug dump triggered for next frame...");
                        _keyF6Pressed = true;
                    }
                }
                else
                {
                    _keyF6Pressed = false;
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
                        bool overdraw = activePipeline.OverdrawEnabled;
                        if (ImGui.Checkbox("Overdraw", ref overdraw))
                            activePipeline.OverdrawEnabled = overdraw;

                        bool wireframe = activePipeline.WireframeEnabled;
                        if (ImGui.Checkbox("Wireframe", ref wireframe))
                            activePipeline.WireframeEnabled = wireframe;

                        bool debugSpheres = activePipeline.DebugSpheresEnabled;
                        if (ImGui.Checkbox("Debug Spheres", ref debugSpheres))
                            activePipeline.DebugSpheresEnabled = debugSpheres;

                        int debugModeIdx = Array.IndexOf(debugModeValues, activePipeline.DebugMode);
                        if (ImGui.Combo("Shade Debug", ref debugModeIdx, debugModeNames, debugModeNames.Length))
                        {
                            activePipeline.DebugMode = debugModeValues[debugModeIdx];
                        }

                        int hizMode = Array.IndexOf(hizModeValues, activePipeline.HiZMode);
                        if (ImGui.Combo("HiZ Mode", ref hizMode, hizModeNames, hizModeNames.Length))
                        {
                            activePipeline.HiZMode = hizModeValues[hizMode];
                        }

                        bool freezeCull = activePipeline.FreezeCullingCamera;
                        if (ImGui.Checkbox("Freeze Culling Camera", ref freezeCull))
                            activePipeline.FreezeCullingCamera = freezeCull;

                        bool showAABBs = activePipeline.DebugShowHiZAABBs;
                        if (ImGui.Checkbox("Debug HiZ AABBs", ref showAABBs))
                            activePipeline.DebugShowHiZAABBs = showAABBs;

                        ImGui.Separator();
                        if (ImGui.TreeNode("Culling Stats"))
                        {
                            uint candidateCount = activePipeline.DebugCandidateCount;
                            uint p1SW = activePipeline.DebugDrawSWCount;
                            uint p1HW = activePipeline.DebugDrawHWCount;
                            uint p1Visible = activePipeline.DebugDrawInstanceCount;
                            uint p2Candidates = activePipeline.DebugPhase2Count;
                            uint p2SW = activePipeline.DebugPhase2DrawSWCount;
                            uint p2HW = activePipeline.DebugPhase2DrawHWCount;
                            uint p2Visible = activePipeline.DebugPhase2DrawInstanceCount;
                            uint lodRejected =
                                candidateCount > (p1Visible + p2Candidates)
                                    ? candidateCount - (p1Visible + p2Candidates)
                                    : 0;
                            uint totalDrawn = p1Visible + p2Visible;

                            ImGui.Text($"BVH Output:      {candidateCount}");
                            ImGui.Text($"  LOD Rejected:  {lodRejected}");
                            uint afterLod = candidateCount - lodRejected;
                            ImGui.Text($"  After LOD:     {afterLod}");
                            ImGui.Separator();
                            ImGui.Text($"Phase1 HiZ Cull: {p2Candidates}");
                            ImGui.Text($"Phase1 Drawn:    {p1Visible}");
                            ImGui.Text($"  SW/HW:          {p1SW} / {p1HW}");
                            ImGui.Separator();
                            ImGui.Text($"Phase2 Input:    {p2Candidates}");
                            ImGui.Text($"Phase2 HiZ Cull: {p2Candidates - p2Visible}");
                            ImGui.Text($"Phase2 Drawn:    {p2Visible}");
                            ImGui.Text($"  SW/HW:          {p2SW} / {p2HW}");
                            ImGui.Separator();
                            ImGui.TextColored(
                                new System.Numerics.Vector4(0, 1, 0, 1),
                                $"Total Drawn:     {totalDrawn}  (saved {candidateCount - totalDrawn - lodRejected})"
                            );
                            ImGui.Text($"Dispatch: [{activePipeline.DebugCandidateArgsX}, ...]");
                            ImGui.Text($"Page Faults: {activePipeline.LastPageFaultCount}");
                            ImGui.Text($"Loaded Pages: {activePipeline.LastLoadedPageCount}");
                            ImGui.Text(
                                $"Resident Pages: {resourceManager.ResidentPageCount} / {resourceManager.PageCount}"
                            );
                            if (ImGui.Button("Evict All Pages"))
                            {
                                uint evicted = 0;
                                for (uint i = 0; i < resourceManager.PageCount; i++)
                                {
                                    if (resourceManager.MarkPageNonResident(i))
                                        evicted++;
                                }
                                Console.WriteLine($"[Streaming Test] Evicted {evicted} pages");
                            }
                            ImGui.TreePop();
                        }

                        if (ImGui.TreeNode("HiZ Visualization"))
                        {
                            if (lastHiZTexture != null && hizMipTexIds.Count > 0)
                            {
                                var texDesc = lastHiZTexture.GetDesc();
                                int mipCount = Math.Min(hizMipTexIds.Count, (int)texDesc.MipLevels);
                                ImGui.Text($"HiZ: {texDesc.Width}x{texDesc.Height}, {texDesc.MipLevels} mips");

                                float fixedW = 200f;
                                float hizAspect = (float)texDesc.Height / Math.Max(texDesc.Width, 1);
                                float fixedH = fixedW * hizAspect;

                                for (int m = 0; m < mipCount; m++)
                                {
                                    uint mipW = Math.Max(1, texDesc.Width >> m);
                                    uint mipH = Math.Max(1, texDesc.Height >> m);
                                    ImGui.Text($"Mip {m}: {mipW}x{mipH}");
                                    ImGui.Image(hizMipTexIds[m], new Vector2(fixedW, fixedH));
                                }
                            }
                            else
                            {
                                ImGui.TextDisabled("HiZ debug data is not available for the current frame");
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
                                var pair in resourceManager.MeshBVHRoots.OrderBy(
                                    p => p.Key,
                                    StringComparer.OrdinalIgnoreCase
                                )
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
                        ImGui.Text($"Runtime instances: {instanceDataManager?.Count ?? 0}");

                        var loadedMeshes = resourceManager
                            .MeshBVHRoots.OrderBy(
                                pair => pair.Key,
                                StringComparer.OrdinalIgnoreCase
                            )
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
                                AssetGuid materialGuid = availableMaterialGuids.Count == 0
                                    ? AssetGuid.Empty
                                    : availableMaterialGuids[spawnIndex % availableMaterialGuids.Count];
                                SpawnEntity(world, rootIndex, new Vector3(x, 0, z), 1.0f, materialGuid);
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
                                    uint rootIndex = loadedMeshes[
                                        random.Next(loadedMeshes.Length)
                                    ].Value;
                                    float x = (random.NextSingle() - 0.5f) * 80.0f;
                                    float y = (random.NextSingle() - 0.5f) * 10.0f;
                                    float z = (random.NextSingle() - 0.5f) * 80.0f;
                                    float scale = 0.5f + random.NextSingle() * 2.0f;
                                    AssetGuid materialGuid = availableMaterialGuids.Count == 0
                                        ? AssetGuid.Empty
                                        : availableMaterialGuids[i % availableMaterialGuids.Count];
                                    SpawnEntity(world, rootIndex, new Vector3(x, y, z), scale, materialGuid);
                                }
                            }
                        }

                        foreach (var entity in world.EntityStore.Entities)
                        {
                            if (ImGui.TreeNode($"Entity {entity.Id}"))
                            {
                                if (entity.HasComponent<LocalTransform>())
                                {
                                    ref var local = ref entity.GetComponent<LocalTransform>();
                                    Vector3 pos = local.Value.Position;
                                    if (ImGui.DragFloat3("Position", ref pos, 0.1f))
                                        local.Value.Position = pos;

                                    float scale = local.Value.Scale;
                                    if (ImGui.DragFloat("Scale", ref scale, 0.05f))
                                        local.Value.Scale = scale;
                                }
                                ImGui.TreePop();
                            }
                        }
                    }
                }
                ImGui.End();
            }

            // Draw HiZ AABB debug overlay

            ImGui.Render();
            var _tImGui = _sw.Elapsed.TotalMilliseconds; _sw.Restart();

            var scDesc = context.SwapChain!.GetDesc();
            float aspect = scDesc.Width / (float)Math.Max(scDesc.Height, 1u);
            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix(aspect);
            var lodScale = camera.GetLodScale(scDesc.Height);
            activePipeline.SetCamera(view, proj, camera.Position, 1.0f, lodScale, debugLOD);

            var pRTV = context.SwapChain!.GetCurrentBackBufferRTV();

            renderGraph.BeginFrame();
            var bbTex = pRTV.GetTexture();
            frameTargets.BeginFrame(
                renderGraph,
                new FrameTargetContext(scDesc.Width, scDesc.Height, (ulong)frameCount)
            );
            frameTargets.ImportTexture(
                StandardFrameTargets.SceneColor,
                bbTex,
                ResourceState.Unknown,
                "SceneColor"
            );
            frameTargets.DeclareTexture(
                StandardFrameTargets.SceneDepth,
                _ => context.DepthBufferDesc with { Name = "SceneDepth" },
                FrameTargetLifetime.FrameLocal,
                ResourceState.Unknown,
                "SceneDepth"
            );
            var colorHandle = frameTargets.ResolveTexture(StandardFrameTargets.SceneColor);
            var depthHandle = frameTargets.ResolveTexture(StandardFrameTargets.SceneDepth);

            renderGraph.AddPass<object>(
                "Clear Main RT",
                (builder, _) =>
                {
                    builder.Write(colorHandle, ResourceState.RenderTarget);
                    builder.Write(depthHandle, ResourceState.DepthWrite);
                },
                (ctx, _) =>
                {
                    var pDSV = ctx.GetTextureView(depthHandle, TextureViewType.DepthStencil);
                    if (pDSV == null)
                        return;

                    ctx.CommandList.SetRenderTargets(
                        [pRTV],
                        pDSV,
                        ResourceStateTransitionMode.Verify
                    );
                    ctx.CommandList.ClearRenderTarget(
                        pRTV,
                        new System.Numerics.Vector4(0.1f, 0.1f, 0.15f, 1.0f),
                        ResourceStateTransitionMode.Verify
                    );
                    ctx.CommandList.ClearDepthStencil(
                        pDSV,
                        ClearDepthStencilFlags.Depth | ClearDepthStencilFlags.Stencil,
                        1.0f,
                        0,
                        ResourceStateTransitionMode.Verify
                    );
                }
            );

            var _tSetup = _sw.Elapsed.TotalMilliseconds; _sw.Restart();
            activePipeline.AddPasses(renderGraph, frameTargets);
            var _tAddPasses = _sw.Elapsed.TotalMilliseconds; _sw.Restart();

            if (imguiRenderer != null && imguiRenderer.FontTexture != null)
            {
                var drawData = ImGui.GetDrawData();
                if (drawData.TotalVtxCount > 0)
                {
                    imguiRenderer.EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
                }

                var fontHandle = renderGraph.Import(
                    "ImGui Font",
                    imguiRenderer.FontTexture,
                    ResourceState.Unknown
                );

                var imguiVbHandle =
                    imguiRenderer.VertexBuffer != null
                        ? renderGraph.Import(
                            "ImGui VB",
                            imguiRenderer.VertexBuffer,
                            ResourceState.Unknown
                        )
                        : RenderGraphHandle.Invalid;
                var imguiIbHandle =
                    imguiRenderer.IndexBuffer != null
                        ? renderGraph.Import(
                            "ImGui IB",
                            imguiRenderer.IndexBuffer,
                            ResourceState.Unknown
                        )
                        : RenderGraphHandle.Invalid;
                var imguiUbHandle =
                    imguiRenderer.UniformBuffer != null
                        ? renderGraph.Import(
                            "ImGui UB",
                            imguiRenderer.UniformBuffer,
                            ResourceState.Unknown
                        )
                        : RenderGraphHandle.Invalid;

                renderGraph.AddPass<object>(
                    "ImGui Pass",
                    (builder, _) =>
                    {
                        builder.Read(fontHandle, ResourceState.ShaderResource);
                        builder.Write(colorHandle, ResourceState.RenderTarget);
                        if (imguiVbHandle.IsValid)
                            builder.Read(imguiVbHandle, ResourceState.VertexBuffer);
                        if (imguiIbHandle.IsValid)
                            builder.Read(imguiIbHandle, ResourceState.IndexBuffer);
                        if (imguiUbHandle.IsValid)
                            builder.Read(imguiUbHandle, ResourceState.ConstantBuffer);
                    },
                    (ctx, _) =>
                    {
                        ctx.CommandList.SetRenderTargets(
                            [pRTV],
                            null,
                            ResourceStateTransitionMode.Verify
                        );
                        imguiRenderer.Render(ctx.CommandList, ImGui.GetDrawData());
                    }
                );
            }

            renderGraph.Compile(context.Device);
            var _tCompile = _sw.Elapsed.TotalMilliseconds; _sw.Restart();
            renderGraph.Execute(context);
            var _tExecute = _sw.Elapsed.TotalMilliseconds; _sw.Restart();

            // Resolve HiZ texture after execute for ImGui visualization
            if (activePipeline.LastHiZTextureHandle.IsValid && imguiRenderer != null)
            {
                var hizTex = renderGraph.GetPhysicalTexture(activePipeline.LastHiZTextureHandle);
                if (hizTex != null && hizTex != lastHiZTexture)
                {
                    // Texture changed — unregister old SRVs
                    foreach (var id in hizMipTexIds)
                        imguiRenderer.UnregisterTexture(id);
                    hizMipTexIds.Clear();

                    // Create per-mip SRVs
                    var desc = hizTex.GetDesc();
                    int mipCount = (int)desc.MipLevels;
                    for (int m = 0; m < mipCount; m++)
                    {
                        var viewDesc = new TextureViewDesc
                        {
                            ViewType = TextureViewType.ShaderResource,
                            Name = $"HiZ_Mip{m}_SRV",
                            Format = desc.Format,
                            MostDetailedMip = (uint)m,
                            NumMipLevels = 1,
                        };
                        var mipSrv = hizTex.CreateView(viewDesc);
                        if (mipSrv != null)
                            hizMipTexIds.Add(imguiRenderer.RegisterTexture(mipSrv));
                    }
                    lastHiZTexture = hizTex;
                }
            }
            else if (lastHiZTexture != null)
            {
                if (imguiRenderer != null)
                    foreach (var id in hizMipTexIds)
                        imguiRenderer.UnregisterTexture(id);
                hizMipTexIds.Clear();
                lastHiZTexture = null;
            }

            frameCount++;

            // Present handled by RenderContext helper or manually
            context.Present();
            var _tPresent = _sw.Elapsed.TotalMilliseconds;

            if (logFrameProfile && frameCount % 120 == 0)
                Console.WriteLine($"[Profile] Update={_tUpdate:F1} ImGui={_tImGui:F1} Setup={_tSetup:F1} AddPass={_tAddPasses:F1} Compile={_tCompile:F1} Execute={_tExecute:F1} Present={_tPresent:F1}ms");
          }
          catch (Exception ex)
          {
              Console.Error.WriteLine($"[RENDER CRASH] {ex}");
              Console.Error.Flush();
              throw;
          }
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
            psoCache?.Dispose();
            renderGraph?.Dispose();
            resourceManager?.Dispose();
            foreach (ITexture texture in textureCache.Values)
            {
                texture.Dispose();
            }
            textureCache.Clear();
            assetDb?.Dispose();
            context?.Dispose();
        };

        window.Run();
    }
}
