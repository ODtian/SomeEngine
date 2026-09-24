using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlatSharp;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Components;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Render.UI;
using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;
using SomeECS.Core;
using SomeECS.Core.Components;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;
using SomeEngine.Core.Diagnostics;

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

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenFile(ref OpenFileName ofn);

    public static string? ShowModelDialog(string title, string? initialDirectory)
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

            if (!OpenFile(ref ofn))
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

internal static class RuntimeApp
{
    private readonly record struct RenderFrameData(
        FrameData Frame,
        ViewData View,
        ViewHistory ViewHistory,
        ClusterPipeline ClusterPipeline,
        RenderWorld RenderWorld,
        CameraHistory CameraHistory,
        TemporalState TemporalState,
        PostTonemapPass PostTonemap,
        ClusterDebugFeature ClusterDebugFeature,
        ImGuiLayer ImGuiLayer,
        ImDrawDataPtr ImGuiDrawData,
        bool CaptureDebugFrame,
        FrameCapture? FrameCapture);

    private struct DynamicSceneInstance : IComponent
    {
        public Vector3 Origin;
        public uint MotionSeed;
        public uint TintSeed;
        public Vector3 MotionAmplitude;
        public float Scale;
    }

    private static void RecordRenderFrame(RenderGraph graph, RenderFrameData data)
    {
        using (Profiler.BeginScope("Runtime.RenderGraph.Build"))
        {
            SceneTextures sceneTextures = FrameResources.CreateSceneTextures(
                graph,
                data.Frame,
                data.View);
            FrameOutputs outputs = data.ClusterPipeline.AddPasses(
                graph,
                data.RenderWorld,
                sceneTextures,
                data.ViewHistory,
                data.CameraHistory,
                data.TemporalState);

            using (Profiler.BeginScope("Runtime.RenderFrame.PostTonemap"))
            {
                data.PostTonemap.AddTo(graph, outputs.PostSceneColor, sceneTextures.OutputColor);
            }

            if (data.FrameCapture != null)
            {
                data.FrameCapture.AddTo(graph, sceneTextures.SceneColor, graph.GetTextureDesc(sceneTextures.SceneColor), "SceneColor");
                data.FrameCapture.AddTo(graph, outputs.PostSceneColor, graph.GetTextureDesc(outputs.PostSceneColor), "PostSceneColor");
            }

            data.FrameCapture?.AddTo(graph, sceneTextures.OutputColor, data.Frame.BackBufferDesc, "OutputColor");

            using (Profiler.BeginScope("Runtime.ClusterDebug.AddPasses"))
            {
                data.ClusterDebugFeature.ClearCounterResources();
                data.ClusterDebugFeature.SetCounterResources(
                    data.ClusterPipeline.DebugIndirectDrawArgs,
                    data.ClusterPipeline.DebugCandidateCount,
                    data.ClusterPipeline.DebugCandidateArgs,
                    data.ClusterPipeline.DebugPhase2CandidateCount,
                    data.ClusterPipeline.DebugPhase2DrawArgs);
                data.ClusterDebugFeature.CaptureCullingCounters = data.CaptureDebugFrame;
                data.ClusterDebugFeature.CaptureHiZ = data.CaptureDebugFrame;
                data.ClusterDebugFeature.AddPasses(graph);
            }

            using (Profiler.BeginScope("Runtime.ImGui.AddPass"))
            {
                data.ImGuiLayer.AddPass(graph, sceneTextures.OutputColor, data.ImGuiDrawData);
            }
        }
    }

    private static string? TryFindRoot(string startDirectory)
    {
        string? current = Path.GetFullPath(startDirectory);

        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SomeEngine.slnx"))
                && Directory.Exists(Path.Combine(current, "assets")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static string ProjectRoot()
    {
        foreach (string startDirectory in new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        })
        {
            if (TryFindRoot(startDirectory) is string projectRoot)
                return projectRoot;
        }

        throw new DirectoryNotFoundException("Could not locate SomeEngine project root.");
    }

    private static void StartRuntime(WindowOptions options, RuntimeStartupOptions startupOptions)
    {
        int frameLimit = startupOptions.FrameLimit;
        string projectRoot = ProjectRoot();
        options.Title = "SomeEngine Runtime";
        options.API = GraphicsAPI.None;

        using AssetDatabase assetDb = global::SomeEngine.Assets.AssetCatalog.CreateDatabase(projectRoot);
        using AssetStore assets = new();
        string samplesDirectory = Path.Combine(projectRoot, "samples");
        int startupInstanceCount = StartupCount();
        ClusterRenderAsset clusterRenderAsset = ClusterRenderAssets.LoadDefault(assetDb);

        var window = Window.Create(options);
        using var renderGraph = new RenderGraph();
        SomeEngine.Render.RHI.RenderContext? context = null;
        ImGuiInput? imguiInput = null;
        RenderWorld? renderWorld = null;
        RenderWorldExtractor? renderExtractor = null;
        ClusterPipeline? clusterPipeline = null;
        ClusterDebugFeature? clusterDebugFeature = null;
        PostTonemapPass? postTonemap = null;
        ImGuiLayer? imguiLayer = null;
        FrameCapture? frameCapture = null;
        RenderDocCapture? renderDocCapture = null;
        ViewHistory histories = new();
        var cameraHistory = new CameraHistory();
        var temporalState = new TemporalState();
        GameWorld? world = null;
        var availableMeshes = new List<string>();
        var availableMaterialGuids = new List<AssetGuid>();
        var materialRequests = new HashSet<AssetGuid>();
        uint frameIndex = 0;
        uint pendingResizeWidth = 0;
        uint pendingResizeHeight = 0;
        bool resizePending = false;
        IInputContext? input = null;
        IKeyboard? keyboard = null;
        IMouse? mouse = null;
        var previousPressedKeys = new HashSet<Key>();
        Vector2 lastMousePosition = Vector2.Zero;
        bool mouseInitialized = false;
        int forcedLodLevel = -1;
        ClusterDebugMode debugMode = startupOptions.ClusterDebug ?? ClusterDebugMode.None;
        HiZMode hiZMode = HiZMode.Phase1ThenHiZ;
        bool useVisBuffer = true;
        bool useSWRaster = true;
        bool useDeformCache = true;
        bool bypassCulling = false;
        bool freezeCullingCamera = false;
        bool temporalResolveEnabled = true;
        bool dynamicSceneEnabled = startupOptions.DynamicScene;
        bool captureDebugFrame = false;
        bool debugUiOpen = true;
        bool imguiFrameLogged = false;
        bool pipelineWarmupReady = false;
        var dynamicSceneEntities = new List<Entity>();
        QueryHandle dynamicSceneQuery = default;
        Entity dynamicLightEntity = Entity.Null;
        DirectionalLight[] dynamicDirectionalLights = [];
        PointLight[] dynamicPointLights = [];
        int lastDynamicInstanceUpdates = 0;
        int lastDynamicMaterialUpdates = 0;
        int lastDynamicLightUpdates = 0;
        int lastDynamicGraphVariant = 0;
        HiZMode effectiveHiZMode = hiZMode;
        ClusterDebugMode effectiveDebugMode = debugMode;
        bool effectiveUseSWRaster = useSWRaster;
        bool effectiveUseDeformCache = useDeformCache;
        bool effectiveTemporalResolve = temporalResolveEnabled;
        var pipelineIssues = new PipelineIssueLog();
        int selectedAvailableMeshIndex = 0;
        int selectedLoadedMeshIndex = 0;
        int spawnedEntityCount = 1;
        string importModelPath = string.Empty;
        string statusMessage = string.Empty;
        var random = new Random();
        var camera = new FreeCamera(
            position: new Vector3(0, 0, -3),
            yaw: MathF.PI * 0.5f,
            pitch: 0.0f,
            fovY: MathF.PI / 4.0f,
            nearPlane: 0.1f,
            farPlane: 1000.0f);

        static int StartupCount()
        {
            const int defaultInstanceCount = 1024;
            return defaultInstanceCount;
        }

        bool ApplyPendingResize()
        {
            if (!resizePending || context == null)
                return false;

            if (!renderGraph.TryRetireSubmittedFrames())
                return true;

            renderGraph.ClearBindSets(waitForGpu: false);

            var swapchain = context.GraphicsSwapchain;
            if (swapchain != null
                && (swapchain.Width != pendingResizeWidth || swapchain.Height != pendingResizeHeight))
            {
                context.Resize(pendingResizeWidth, pendingResizeHeight);
                histories.ResetAll(waitForGpu: false);
                cameraHistory.Reset();
                temporalState.Reset();
            }

            resizePending = false;
            return false;
        }

        void SpawnStartupInstances(
            GameWorld targetWorld,
            Handle<Mesh> mesh,
            int instanceCount,
            IReadOnlyList<Handle<Material>> materials)
        {
            if (materials.Count == 0)
                throw new InvalidOperationException("Startup instances require at least one material handle.");

            dynamicSceneEntities.Clear();
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
                    planeZ);

                var entity = targetWorld.World.CreateEntity();
                targetWorld.World.Add(entity, new LocalTransform
                {
                    Value = new TransformQvvs(position, Quaternion.Identity, scale),
                });
                targetWorld.World.Add(entity, new WorldTransform());
                targetWorld.World.Add(entity, new MeshInstance { Mesh = mesh });
                targetWorld.World.Add(entity, new MeshMaterialBindings
                {
                    Materials = new[] { materials[i % materials.Count] },
                });
                targetWorld.World.Add(entity, new MaterialOverride
                {
                    BaseColorTint = new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                });
                targetWorld.World.Add(entity, new DynamicSceneInstance
                {
                    Origin = position,
                    MotionSeed = unchecked((uint)i * 0x9E3779B9u),
                    TintSeed = unchecked(((uint)i + 17u) * 0x85EBCA6Bu),
                    MotionAmplitude = new Vector3(
                        0.25f + (i & 3) * 0.035f,
                        0.12f + ((i >> 2) & 3) * 0.025f,
                        0.32f + ((i >> 4) & 3) * 0.04f),
                    Scale = scale,
                });
                dynamicSceneEntities.Add(entity);
            }

            string materialDescription = materials.Count == 1
                ? materials[0].ToString()
                : $"{materials.Count} rotating materials";
            Console.WriteLine($"Spawned {instanceCount} startup instances (mesh={mesh}, grid={columns}x{columns}, material={materialDescription}).");
        }

        void SpawnStartupLights(GameWorld targetWorld)
        {
            var lightEntity = targetWorld.World.CreateEntity();
            var keyLight = new DirectionalLight(
                Vector3.Normalize(new Vector3(0.35f, -0.85f, 0.4f)),
                new Vector3(1.0f, 0.96f, 0.9f),
                3.0f);
            var pointLight = new PointLight(
                new Vector3(0.0f, 6.0f, 52.0f),
                28.0f,
                new Vector3(0.55f, 0.75f, 1.0f),
                10.0f);
            dynamicLightEntity = lightEntity;
            dynamicDirectionalLights = [keyLight];
            dynamicPointLights =
            [
                pointLight,
                new PointLight(new Vector3(-18.0f, 4.0f, 58.0f), 22.0f, new Vector3(1.0f, 0.55f, 0.45f), 7.0f),
                new PointLight(new Vector3(18.0f, 8.0f, 60.0f), 24.0f, new Vector3(0.45f, 1.0f, 0.65f), 8.0f),
            ];
            targetWorld.World.Add(
                lightEntity,
                new SceneLights(
                    dynamicDirectionalLights,
                    new ReadOnlyMemory<PointLight>(dynamicPointLights, 0, 1),
                    ReadOnlyMemory<SpotLight>.Empty));
            Console.WriteLine("Spawned startup dynamic lights.");
        }

        void RefreshAvailableMeshes()
        {
            availableMeshes.Clear();
            if (Directory.Exists(samplesDirectory))
            {
                availableMeshes.AddRange(
                    Directory
                        .EnumerateFiles(samplesDirectory, "*.mesh.asset", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }

            if (selectedAvailableMeshIndex >= availableMeshes.Count)
                selectedAvailableMeshIndex = Math.Max(availableMeshes.Count - 1, 0);
        }

        void RefreshAvailableMaterialGuids()
        {
            availableMaterialGuids.Clear();
            foreach (AssetManifestRecord record in assetDb.List(nameof(MaterialAsset)))
            {
                string path = record.Path.Replace('\\', '/');
                if (record.Guid.IsEmpty
                    || string.Equals(path, GltfImporterSettings.DefaultUnlitMaterialTemplate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                availableMaterialGuids.Add(record.Guid);
            }

            foreach (AssetGuid guid in availableMaterialGuids)
                QueueMaterial(guid);
        }

        IReadOnlyList<AssetGuid> ResolveStartupMaterialGuids()
        {
            AssetGuid? defaultMaterialGuid = assetDb.Resolve(GltfImporterSettings.DefaultLitMaterialTemplate);
            if (defaultMaterialGuid is { IsEmpty: false } guid)
                return [guid];
            return availableMaterialGuids;
        }

        IReadOnlyList<Handle<Material>> ResolveStartupMaterials()
        {
            var materials = new List<Handle<Material>>();
            foreach (AssetGuid guid in ResolveStartupMaterialGuids())
            {
                Handle<Material> material = LoadMaterial(guid);
                if (material.IsValid)
                    materials.Add(material);
            }

            return materials;
        }

        bool TryLoadMeshFromFile(string meshFilePath, out string message)
        {
            message = string.Empty;
            if (clusterPipeline == null)
            {
                message = "Load Mesh failed: cluster render is not initialized.";
                return false;
            }

            if (!File.Exists(meshFilePath))
            {
                message = $"Load Mesh failed: file does not exist: {meshFilePath}";
                return false;
            }

            AssetGuid? meshGuid = assetDb.Resolve(meshFilePath);
            if (meshGuid is not { IsEmpty: false })
            {
                IReadOnlyList<AssetGuid> importedGuids = assetDb.Import(meshFilePath);
                meshGuid = importedGuids.FirstOrDefault(guid =>
                    assetDb.Manifest.TryGetAsset(guid, out AssetManifestRecord record)
                    && string.Equals(record.AssetType, nameof(MeshAsset), StringComparison.Ordinal));
            }

            if (meshGuid is not { IsEmpty: false } guid)
            {
                message = $"Load Mesh failed: AssetDatabase could not resolve {Path.GetFileName(meshFilePath)}.";
                return false;
            }

            Handle<Mesh> handle = RuntimeAssetLoader
                .RequestMesh(assets, assetDb, guid)
                .GetAwaiter()
                .GetResult();
            if (!handle.IsValid || !assets.TryGet(handle, out Mesh? mesh) || mesh == null || !clusterPipeline.RegisterMesh(handle))
            {
                message = $"Load Mesh failed: {Path.GetFileName(meshFilePath)} produced invalid mesh handle.";
                return false;
            }

            string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? Path.GetFileNameWithoutExtension(meshFilePath) : mesh.Name;
            message = $"Loaded mesh '{meshName}' from {Path.GetFileName(meshFilePath)} ({handle}).";
            return true;
        }

        bool TryImportModelToMesh(string modelPath, out string message)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                message = "Import failed: model path is empty.";
                return false;
            }

            string resolvedPath = Path.GetFullPath(modelPath);
            string extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
            if (extension != ".gltf" && extension != ".glb")
            {
                message = "Import failed: only .gltf or .glb files are supported.";
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                message = $"Import failed: file does not exist: {resolvedPath}";
                return false;
            }

            IReadOnlyList<AssetGuid> importedGuids = assetDb.Import(resolvedPath);
            int meshCount = importedGuids.Count(guid =>
            {
                if (!assetDb.Manifest.TryGetAsset(guid, out AssetManifestRecord record))
                    return false;
                return string.Equals(record.AssetType, nameof(MeshAsset), StringComparison.Ordinal);
            });

            RefreshAvailableMeshes();
            RefreshAvailableMaterialGuids();
            selectedAvailableMeshIndex = 0;
            message = $"Imported {Path.GetFileName(resolvedPath)} and generated {meshCount} mesh asset(s).";
            return true;
        }

        string? TryPickImportModelPath()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            string initialDirectory = Directory.Exists(samplesDirectory)
                ? samplesDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return NativeFileDialog.ShowModelDialog("Select GLTF/GLB file", initialDirectory);
        }

        void SpawnEntity(
            GameWorld targetWorld,
            Handle<Mesh> mesh,
            Vector3 position,
            float scale,
            Handle<Material> material = default)
        {
            var entity = targetWorld.World.CreateEntity();
            targetWorld.World.Add(entity, new LocalTransform { Value = new TransformQvvs(position, Quaternion.Identity, scale) });
            targetWorld.World.Add(entity, new WorldTransform());
            targetWorld.World.Add(entity, new MeshInstance { Mesh = mesh });
            if (material.IsValid)
                targetWorld.World.Add(entity, new MeshMaterialBindings { Materials = new[] { material } });
        }

        void SpawnRandomEntities(GameWorld targetWorld, int count)
        {
            if (clusterPipeline == null || clusterPipeline.MeshCount == 0)
                return;

            Handle<Mesh>[] loadedMeshes = clusterPipeline.Meshes.Select(static pair => pair.Value).ToArray();
            for (int i = 0; i < count; i++)
            {
                Handle<Mesh> mesh = loadedMeshes[random.Next(loadedMeshes.Length)];
                float x = (random.NextSingle() - 0.5f) * 80.0f;
                float y = (random.NextSingle() - 0.5f) * 10.0f;
                float z = (random.NextSingle() - 0.5f) * 80.0f;
                float scale = 0.5f + random.NextSingle() * 2.0f;
                Handle<Material> material = availableMaterialGuids.Count == 0
                    ? default
                    : FindMaterial(availableMaterialGuids[i % availableMaterialGuids.Count]);
                SpawnEntity(targetWorld, mesh, new Vector3(x, y, z), scale, material);
            }
        }

        void AnimateScene(GameWorld targetWorld)
        {
            using var scope = Profiler.BeginScope("Runtime.DynamicScene");
            lastDynamicInstanceUpdates = 0;
            lastDynamicMaterialUpdates = 0;
            lastDynamicLightUpdates = 0;
            lastDynamicGraphVariant = 0;
            if (!dynamicSceneEnabled)
                return;

            uint frame = frameIndex;
            lastDynamicGraphVariant = checked((int)(frameIndex & 3u));
            foreach (QueryChunkView chunk in targetWorld.World.RunQuery(dynamicSceneQuery).Chunks)
            {
                ReadOnlySpan<DynamicSceneInstance> instances = chunk.Read<DynamicSceneInstance>();
                Span<LocalTransform> locals = chunk.ReadWrite<LocalTransform>();
                Span<MaterialOverride> overrides = chunk.ReadWrite<MaterialOverride>();
                for (int i = 0; i < instances.Length; i++)
                {
                    DynamicSceneInstance instance = instances[i];
                    uint motion = unchecked(frame + instance.MotionSeed);
                    int motionX = (int)(motion & 127u);
                    int motionY = (int)((motion >> 3) & 127u);
                    int motionZ = (int)((motion >> 6) & 127u);
                    float waveX = ((63 - Math.Abs(motionX - 63)) * (2.0f / 63.0f)) - 1.0f;
                    float waveY = ((63 - Math.Abs(motionY - 63)) * (2.0f / 63.0f)) - 1.0f;
                    float waveZ = ((63 - Math.Abs(motionZ - 63)) * (2.0f / 63.0f)) - 1.0f;
                    Vector3 position = instance.Origin + new Vector3(
                        waveX * instance.MotionAmplitude.X,
                        waveY * instance.MotionAmplitude.Y,
                        waveZ * instance.MotionAmplitude.Z);

                    locals[i].Value = new TransformQvvs(position, Quaternion.Identity, instance.Scale);
                    lastDynamicInstanceUpdates++;

                    uint tint = unchecked((frame * 3u) + instance.TintSeed);
                    int tintR = (int)(tint & 127u);
                    int tintG = (int)((tint >> 4) & 127u);
                    int tintB = (int)((tint >> 8) & 127u);
                    float waveR = ((63 - Math.Abs(tintR - 63)) * (1.0f / 63.0f));
                    float waveG = ((63 - Math.Abs(tintG - 63)) * (1.0f / 63.0f));
                    float waveB = ((63 - Math.Abs(tintB - 63)) * (1.0f / 63.0f));
                    overrides[i].BaseColorTint = new Vector4(
                        0.45f + waveR * 0.45f,
                        0.45f + waveG * 0.45f,
                        0.45f + waveB * 0.45f,
                        1.0f);
                    lastDynamicMaterialUpdates++;
                }
            }

            if (dynamicLightEntity != Entity.Null
                && targetWorld.World.IsAlive(dynamicLightEntity)
                && targetWorld.World.Has<SceneLights>(dynamicLightEntity)
                && dynamicDirectionalLights.Length != 0)
            {
                uint lightFrame = unchecked(frame * 2u);
                int lightA = (int)(lightFrame & 127u);
                int lightB = (int)((lightFrame >> 2) & 127u);
                int lightC = (int)((lightFrame >> 5) & 127u);
                float lightWaveA = ((63 - Math.Abs(lightA - 63)) * (2.0f / 63.0f)) - 1.0f
                float lightWaveB = ((63 - Math.Abs(lightB - 63)) * (2.0f / 63.0f)) - 1.0f;
                float lightWaveC = ((63 - Math.Abs(lightC - 63)) * (2.0f / 63.0f)) - 1.0f;
                dynamicDirectionalLights[0] = new DirectionalLight(
                    Vector3.Normalize(new Vector3(
                        0.35f + lightWaveA * 0.18f,
                        -0.85f,
                        0.4f + lightWaveB * 0.16f)),
                    new Vector3(1.0f, 0.96f, 0.9f),
                    3.0f);
                if (dynamicPointLights.Length != 0)
                {
                    dynamicPointLights[0] = new PointLight(
                        new Vector3(
                            lightWaveA * 18.0f,
                            6.0f + lightWaveB * 2.0f,
                            52.0f + lightWaveC * 14.0f),
                        28.0f,
                        new Vector3(0.55f, 0.75f, 1.0f),
                        10.0f);
                }

                ref SceneLights lights = ref targetWorld.World.Get<SceneLights>(dynamicLightEntity);
                int pointLightCount = dynamicPointLights.Length == 0
                    ? 0
                    : 1 + checked((int)(frameIndex % (uint)dynamicPointLights.Length));
                lights = new SceneLights(
                    dynamicDirectionalLights,
                    new ReadOnlyMemory<PointLight>(dynamicPointLights, 0, pointLightCount),
                    ReadOnlyMemory<SpotLight>.Empty);
                lastDynamicLightUpdates = dynamicDirectionalLights.Length + pointLightCount;
            }

        }

        AssetGuid ResolveRequiredStartupMaterialGuid()
        {
            AssetGuid? materialGuid = assetDb.Resolve(GltfImporterSettings.DefaultLitMaterialTemplate);
            if (materialGuid is not { IsEmpty: false })
            {
                string materialPath = Path.Combine(
                    projectRoot,
                    GltfImporterSettings.DefaultLitMaterialTemplate.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(materialPath))
                {
                    assetDb.Import(GltfImporterSettings.DefaultLitMaterialTemplate);
                    materialGuid = assetDb.Resolve(GltfImporterSettings.DefaultLitMaterialTemplate);
                }
            }

            if (materialGuid is { IsEmpty: false } guid)
                return guid;

            throw new InvalidOperationException(
                $"Runtime requires material asset '{GltfImporterSettings.DefaultLitMaterialTemplate}' to populate real MeshMaterialBindings.");
        }

        Handle<Shader> LoadShader(AssetGuid guid)
        {
            if (guid.IsEmpty)
                return default;

            return RuntimeAssetLoader
                .RequestShader(assets, assetDb, guid)
                .GetAwaiter()
                .GetResult();
        }

        Handle<Material> LoadMaterial(AssetGuid guid)
        {
            if (guid.IsEmpty)
                return default;

            return RuntimeAssetLoader
                .RequestMaterial(assets, assetDb, guid)
                .GetAwaiter()
                .GetResult();
        }

        Handle<Material> FindMaterial(AssetGuid guid)
        {
            if (guid.IsEmpty)
                return default;

            return assets.TryFind(guid, out Handle<Material> handle)
                && assets.TryGet(handle, out Material? material)
                && material != null
                    ? handle
                    : default;
        }

        void QueueMaterial(AssetGuid guid)
        {
            if (guid.IsEmpty || FindMaterial(guid).IsValid)
                return;

            lock (materialRequests)
            {
                if (!materialRequests.Add(guid))
                    return;
            }

            Task<Handle<Material>> request = RuntimeAssetLoader.RequestMaterial(assets, assetDb, guid);
            _ = request.ContinueWith(
                completed =>
                {
                    lock (materialRequests)
                        materialRequests.Remove(guid);

                    if (completed.IsFaulted)
                    {
                        Exception error = completed.Exception?.GetBaseException()
                            ?? new InvalidOperationException($"Material request '{guid}' failed.");
                        Console.Error.WriteLine($"Material request '{guid}' failed: {error.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        bool WasPressedThisFrame(IKeyboard currentKeyboard, Key key)
        {
            bool isPressed = currentKeyboard.IsKeyPressed(key);
            bool wasPressed = previousPressedKeys.Contains(key);
            if (isPressed)
                previousPressedKeys.Add(key);
            else
                previousPressedKeys.Remove(key);
            return isPressed && !wasPressed;
        }

        void UpdateInput(double deltaSeconds)
        {
            imguiInput?.Update((float)deltaSeconds);

            float dt = MathF.Min((float)deltaSeconds, 1.0f / 30.0f);
            float moveSpeed = 6.0f;
            const float lookSpeed = 0.0035f;
            bool captureKeyboard = ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantCaptureKeyboard;
            bool captureMouse = ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantCaptureMouse;

            if (keyboard != null && !captureKeyboard)
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
                if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight))
                    move.Y -= 1.0f;

                if (move != Vector3.Zero)
                    camera.MoveLocal(Vector3.Normalize(move) * (moveSpeed * dt));

                if (WasPressedThisFrame(keyboard, Key.Number1))
                    debugMode = debugMode == ClusterDebugMode.ClusterID ? ClusterDebugMode.None : ClusterDebugMode.ClusterID;
                if (WasPressedThisFrame(keyboard, Key.Number2))
                    debugMode = debugMode == ClusterDebugMode.LODLevel ? ClusterDebugMode.None : ClusterDebugMode.LODLevel;
                if (WasPressedThisFrame(keyboard, Key.Number3))
                    debugMode = debugMode == ClusterDebugMode.SWHWView ? ClusterDebugMode.None : ClusterDebugMode.SWHWView;
                if (WasPressedThisFrame(keyboard, Key.Number4))
                    debugMode = ClusterDebugMode.None;
                if (WasPressedThisFrame(keyboard, Key.F5))
                    captureDebugFrame = true;
                if (WasPressedThisFrame(keyboard, Key.F6))
                    dynamicSceneEnabled = !dynamicSceneEnabled;
            }

            if (mouse != null && !captureMouse)
            {
                var currentMousePosition = new Vector2(mouse.Position.X, mouse.Position.Y);
                if (!mouseInitialized)
                {
                    lastMousePosition = currentMousePosition;
                    mouseInitialized = true;
                }

                Vector2 mouseDelta = currentMousePosition - lastMousePosition;
                lastMousePosition = currentMousePosition;

                if (mouse.IsButtonPressed(MouseButton.Right))
                    camera.AddYawPitch(mouseDelta.X * lookSpeed, -mouseDelta.Y * lookSpeed);
            }
        }

        void DrawDebugUi(uint width, uint height)
        {
            if (!debugUiOpen)
                return;

            ImGui.SetNextWindowPos(new Vector2(60, 60), ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(560, 680), ImGuiCond.Appearing);
            bool runtimeVisible = ImGui.Begin("Engine Debug", ref debugUiOpen);
            if (!runtimeVisible)
            {
                ImGui.End();
                return;
            }

            if (ImGui.BeginTabBar("RuntimeTabs"))
            {
                if (ImGui.BeginTabItem("Rendering"))
                {
                    int lod = forcedLodLevel;
                    if (ImGui.SliderInt("Manual LOD", ref lod, -1, 10))
                        forcedLodLevel = lod;

                    ClusterDebugMode[] debugModeValues = Enum.GetValues<ClusterDebugMode>();
                    string[] debugModeNames = Enum.GetNames<ClusterDebugMode>();
                    int selectedDebugMode = Array.IndexOf(debugModeValues, debugMode);
                    if (selectedDebugMode < 0)
                        selectedDebugMode = 0;
                    if (ImGui.Combo("Debug Mode", ref selectedDebugMode, debugModeNames, debugModeNames.Length))
                        debugMode = debugModeValues[selectedDebugMode];

                    HiZMode[] hiZModeValues = Enum.GetValues<HiZMode>();
                    string[] hiZModeNames = Enum.GetNames<HiZMode>();
                    int selectedHiZMode = Array.IndexOf(hiZModeValues, hiZMode);
                    if (selectedHiZMode < 0)
                        selectedHiZMode = 0;
                    if (ImGui.Combo("HiZ Mode", ref selectedHiZMode, hiZModeNames, hiZModeNames.Length))
                        hiZMode = hiZModeValues[selectedHiZMode];

                    ImGui.Separator();
                    ImGui.Checkbox("Use VisBuffer", ref useVisBuffer);
                    ImGui.Checkbox("Use SW Raster", ref useSWRaster);
                    ImGui.Checkbox("Use Deform Cache", ref useDeformCache);
                    ImGui.Checkbox("Bypass Culling", ref bypassCulling);
                    ImGui.Checkbox("Freeze Culling Camera", ref freezeCullingCamera);
                    ImGui.Checkbox("Temporal Resolve", ref temporalResolveEnabled);
                    if (ImGui.Button("Capture Debug Frame"))
                        captureDebugFrame = true;

                    if (clusterDebugFeature != null && ImGui.TreeNode("Culling Stats"))
                    {
                        DrawCullingStats(clusterDebugFeature);
                        ImGui.TreePop();
                    }

                    if (clusterDebugFeature != null && ImGui.TreeNode("HiZ Mips"))
                    {
                        DrawHiZMips(clusterDebugFeature);
                        ImGui.TreePop();
                    }

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Frame"))
                {
                    ImGui.Text($"Backbuffer: {width}x{height}");
                    ImGui.Text($"Instances: {renderWorld?.CountInstances() ?? 0}");
                    ImGui.Text($"Pages: {clusterPipeline?.ResidentPageCount ?? 0} / {clusterPipeline?.PageCount ?? 0}");
                    if (clusterPipeline != null)
                        DrawPipelineWarmup(clusterPipeline.LastWarmup);
                    pipelineIssues.Draw();

                    ImGui.Separator();
                    if (ImGui.Button("Evict All Pages") && clusterPipeline != null)
                    {
                        uint evicted = clusterPipeline.EvictPages();
                        statusMessage = $"Evicted {evicted} pages.";
                    }

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Scene"))
                {
                    ImGui.Checkbox("Dynamic Scene", ref dynamicSceneEnabled);
                    ImGui.Text($"Graph Variant: {lastDynamicGraphVariant}");
                    ImGui.Text($"Animated Instances: {lastDynamicInstanceUpdates} / {dynamicSceneEntities.Count}");
                    ImGui.Text($"Material Updates: {lastDynamicMaterialUpdates}");
                    ImGui.Text($"Animated Lights: {lastDynamicLightUpdates}");
                    ImGui.Text($"Effective HiZ: {effectiveHiZMode}");
                    ImGui.Text($"Effective Debug: {effectiveDebugMode}");
                    ImGui.Text($"Effective SW/Deform/Temporal: {effectiveUseSWRaster} / {effectiveUseDeformCache} / {effectiveTemporalResolve}");
                    ImGui.Text($"Render World Version: {renderExtractor?.Version ?? 0}");
                    ImGui.Text($"Render Shape Version: {renderExtractor?.ShapeVersion ?? 0}");
                    ImGui.Separator();

                    KeyValuePair<string, Handle<Mesh>>[] loadedMeshes = clusterPipeline == null
                        ? []
                        : clusterPipeline
                            .Meshes
                            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    if (loadedMeshes.Length > 0)
                    {
                        if (selectedLoadedMeshIndex >= loadedMeshes.Length)
                            selectedLoadedMeshIndex = loadedMeshes.Length - 1;

                        KeyValuePair<string, Handle<Mesh>> selected = loadedMeshes[selectedLoadedMeshIndex];
                        string selectedLabel = $"{selected.Key} ({selected.Value})";
                        if (ImGui.BeginCombo("Target Mesh", selectedLabel))
                        {
                            for (int i = 0; i < loadedMeshes.Length; i++)
                            {
                                bool isSelected = i == selectedLoadedMeshIndex;
                                string option = $"{loadedMeshes[i].Key} ({loadedMeshes[i].Value})";
                                if (ImGui.Selectable(option, isSelected))
                                    selectedLoadedMeshIndex = i;
                                if (isSelected)
                                    ImGui.SetItemDefaultFocus();
                            }

                            ImGui.EndCombo();
                        }

                        if (world != null && ImGui.Button("Add Entity"))
                        {
                            int spawnIndex = spawnedEntityCount++;
                            float x = (spawnIndex % 5) * 2.5f;
                            float z = (spawnIndex / 5) * 2.5f;
                            Handle<Material> material = availableMaterialGuids.Count == 0
                                ? default
                                : FindMaterial(availableMaterialGuids[spawnIndex % availableMaterialGuids.Count]);
                            SpawnEntity(world, selected.Value, new Vector3(x, 0, z), 1.0f, material);
                            statusMessage = $"Added entity for {selected.Key}.";
                        }

                        ImGui.SameLine();
                        if (world != null && ImGui.Button("Add 100 Random"))
                        {
                            SpawnRandomEntities(world, 100);
                            statusMessage = "Added 100 random entities.";
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("No loaded meshes available.");
                    }

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Assets"))
                {
                    ImGui.Text($"Samples: {samplesDirectory}");
                    if (ImGui.Button("Refresh Mesh List"))
                    {
                        RefreshAvailableMeshes();
                        RefreshAvailableMaterialGuids();
                    }

                    if (!string.IsNullOrWhiteSpace(statusMessage))
                        ImGui.TextWrapped(statusMessage);

                    if (availableMeshes.Count > 0)
                    {
                        if (selectedAvailableMeshIndex >= availableMeshes.Count)
                            selectedAvailableMeshIndex = availableMeshes.Count - 1;

                        string selectedPath = availableMeshes[selectedAvailableMeshIndex];
                        if (ImGui.BeginCombo("Available .mesh", Path.GetFileName(selectedPath)))
                        {
                            for (int i = 0; i < availableMeshes.Count; i++)
                            {
                                bool isSelected = i == selectedAvailableMeshIndex;
                                if (ImGui.Selectable(Path.GetFileName(availableMeshes[i]), isSelected))
                                    selectedAvailableMeshIndex = i;
                                if (isSelected)
                                    ImGui.SetItemDefaultFocus();
                            }

                            ImGui.EndCombo();
                        }

                        if (ImGui.Button("Load Mesh"))
                        {
                            TryLoadMeshFromFile(selectedPath, out statusMessage);
                            Console.WriteLine(statusMessage);
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("No .mesh.asset files found.");
                    }

                    ImGui.Separator();
                    ImGui.InputText("Import Path", ref importModelPath, 1024);
                    if (ImGui.Button("Pick GLTF/GLB"))
                    {
                        string? picked = TryPickImportModelPath();
                        if (!string.IsNullOrWhiteSpace(picked))
                            importModelPath = picked;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Import"))
                    {
                        TryImportModelToMesh(importModelPath, out statusMessage);
                        Console.WriteLine(statusMessage);
                    }

                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        void DrawCullingStats(ClusterDebugFeature debugFeature)
        {
            uint candidateCount = debugFeature.CandidateCount;
            uint p1SW = debugFeature.Phase1DrawSWCount;
            uint p1HW = debugFeature.Phase1DrawHWCount;
            uint p1Visible = debugFeature.Phase1DrawInstanceCount;
            uint p2Candidates = debugFeature.Phase2CandidateCount;
            uint p2SW = debugFeature.Phase2DrawSWCount;
            uint p2HW = debugFeature.Phase2DrawHWCount;
            uint p2Visible = debugFeature.Phase2DrawInstanceCount;
            uint lodRejected = candidateCount > p1Visible + p2Candidates
                ? candidateCount - (p1Visible + p2Candidates)
                : 0;
            uint totalDrawn = p1Visible + p2Visible;
            uint saved = candidateCount > totalDrawn + lodRejected
                ? candidateCount - totalDrawn - lodRejected
                : 0;
            uint p2Culled = p2Candidates > p2Visible
                ? p2Candidates - p2Visible
                : 0;

            ImGui.Text($"BVH Output:      {candidateCount}");
            ImGui.Text($"  LOD Rejected:  {lodRejected}");
            ImGui.Text($"  After LOD:     {candidateCount - lodRejected}");
            ImGui.Separator();
            ImGui.Text($"Phase1 HiZ Cull: {p2Candidates}");
            ImGui.Text($"Phase1 Drawn:    {p1Visible}");
            ImGui.Text($"  SW/HW:          {p1SW} / {p1HW}");
            ImGui.Separator();
            ImGui.Text($"Phase2 Input:    {p2Candidates}");
            ImGui.Text($"Phase2 HiZ Cull: {p2Culled}");
            ImGui.Text($"Phase2 Drawn:    {p2Visible}");
            ImGui.Text($"  SW/HW:          {p2SW} / {p2HW}");
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Total Drawn:     {totalDrawn}  (saved {saved})");
            ImGui.Text($"Dispatch: [{debugFeature.CandidateDispatchX}, ...]");
            ImGui.Text($"Page Faults: {clusterPipeline?.FaultCount ?? 0}");
            ImGui.Text($"Loaded Pages: {clusterPipeline?.LoadedPages ?? 0}");
            ImGui.Text($"Resident Pages: {clusterPipeline?.ResidentPageCount ?? 0} / {clusterPipeline?.PageCount ?? 0}");
        }

        static void DrawPipelineWarmup(PipelineWarmup warmup)
        {
            if (warmup.Requested == 0)
                return;

            if (!ImGui.TreeNode("Pipeline Warmup"))
                return;

            ImGui.Text($"Requested: {warmup.Requested}");
            ImGui.Text($"Processed: {warmup.Processed}");
            ImGui.Text($"Ready: {warmup.Ready}");
            ImGui.Text($"Ready to use: {warmup.ReadyToUse}");
            ImGui.Text($"Queued: {warmup.Queued} required={warmup.RequiredQueued} optional={warmup.OptionalQueued}");
            ImGui.Text($"Failed: {warmup.Failed} required={warmup.RequiredFailed} optional={warmup.OptionalFailed}");
            if (warmup.BudgetLimited)
                ImGui.TextColored(new Vector4(1.0f, 0.72f, 0.24f, 1.0f), "Budget limited");
            if (warmup.HasIssues)
                ImGui.TextDisabled($"Issues: {warmup.Issues.Count}");
            ImGui.TreePop();
        }

        void DrawHiZMips(ClusterDebugFeature debugFeature)
        {
            uint mipCount = debugFeature.HiZMipCount;
            if (mipCount == 0)
            {
                ImGui.TextDisabled("HiZ mip data is not available for the current frame.");
                return;
            }

            float contentWidth = MathF.Max(120.0f, ImGui.GetContentRegionAvail().X);
            for (uint mip = 0; mip < mipCount; mip++)
            {
                uint width = debugFeature.HiZWidth(mip);
                uint height = debugFeature.HiZHeight(mip);
                if (width == 0 || height == 0)
                    continue;

                float minValue = debugFeature.HiZMin(mip);
                float maxValue = debugFeature.HiZMax(mip);
                ImGui.Text($"Mip {mip}: {width}x{height}  depth [{minValue:F6}, {maxValue:F6}]");
                DrawMipView(debugFeature, mip, width, height, minValue, maxValue, MathF.Min(contentWidth, 320.0f));
            }
        }

        static void DrawMipView(
            ClusterDebugFeature debugFeature,
            uint mip,
            uint width,
            uint height,
            float minValue,
            float maxValue,
            float previewWidth)
        {
            float previewHeight = MathF.Max(1.0f, previewWidth * height / MathF.Max(width, 1u));
            previewHeight = MathF.Min(previewHeight, 180.0f);
            Vector2 origin = ImGui.GetCursorScreenPos();
            Vector2 size = new(previewWidth, previewHeight);
            var drawList = ImGui.GetWindowDrawList();
            uint gridW = Math.Clamp(width, 1u, 96u);
            uint gridH = Math.Clamp(height, 1u, 64u);
            float cellW = previewWidth / gridW;
            float cellH = previewHeight / gridH;
            float range = maxValue - minValue;
            bool remap = float.IsFinite(range) && range > 1e-8f;

            for (uint gy = 0; gy < gridH; gy++)
            {
                uint sy = Math.Min(height - 1u, checked((uint)(((ulong)gy * height) / gridH)));
                for (uint gx = 0; gx < gridW; gx++)
                {
                    uint sx = Math.Min(width - 1u, checked((uint)(((ulong)gx * width) / gridW)));
                    debugFeature.TryHiZ(mip, sx, sy, out float value);
                    float mapped = remap && float.IsFinite(value)
                        ? (value - minValue) / range
                        : 0.5f;
                    byte c = (byte)Math.Clamp(mapped * 255.0f, 0.0f, 255.0f);
                    uint color = 0xFF000000u | c | ((uint)c << 8) | ((uint)c << 16);
                    Vector2 min = new(origin.X + gx * cellW, origin.Y + gy * cellH);
                    Vector2 max = new(origin.X + (gx + 1u) * cellW, origin.Y + (gy + 1u) * cellH);
                    drawList.AddRectFilled(min, max, color);
                }
            }

            drawList.AddRect(origin, origin + size, 0xFFFFFFFFu);
            ImGui.Dummy(size + new Vector2(0, 6));
        }

        window.Load += () =>
        {
            context = new SomeEngine.Render.RHI.RenderContext(startupOptions.PresentSyncInterval);
            context.Initialize(window, Backend.D3D12, startupOptions.DeviceValidation, D3D12Backend.Factory);
            Console.WriteLine(Profiler.Status.ToDisplayString());
            if (startupOptions.VerifyFrameOutput)
            {
                var device = context.GraphicsDevice
                    ?? throw new InvalidOperationException("Frame output verification requires an initialized graphics device.");
                frameCapture = new FrameCapture(device);
            }
            if (!string.IsNullOrWhiteSpace(startupOptions.RenderDocCapture))
            {
                renderDocCapture = RenderDocCapture.TryCreate(
                    startupOptions.RenderDocCapture,
                    startupOptions.RenderDocFrame);
                Console.WriteLine(
                    renderDocCapture == null
                        ? "RenderDoc capture requested, but renderdoc.dll is not loaded."
                        : $"RenderDoc capture armed for frame {startupOptions.RenderDocFrame}.");
            }

            renderWorld = new RenderWorld();
            clusterPipeline = ClusterPipelineAssets.LoadOpaque(
                context,
                assetDb,
                clusterRenderAsset,
                assets);
            renderExtractor = new RenderWorldExtractor(renderWorld);
            clusterPipeline.Initialize(context);
            clusterPipeline.MaterialPipelineBudget = startupOptions.PipelineWarmupBudget;
            clusterDebugFeature = new ClusterDebugFeature();
            clusterDebugFeature.Initialize(context);
            AssetGuid? postTonemapGuid = assetDb.Resolve(HostShaders.PostTonemapPath);
            if (postTonemapGuid is not { IsEmpty: false } postTonemapShaderGuid)
                throw new InvalidOperationException($"Required host shader '{HostShaders.PostTonemapPath}' is not indexed.");
            Handle<Shader> postTonemapShader = LoadShader(postTonemapShaderGuid);
            if (!postTonemapShader.IsValid)
                throw new InvalidOperationException($"Required host shader '{HostShaders.PostTonemapPath}' did not load.");
            postTonemap = new PostTonemapPass(
                context,
                assets.Get(postTonemapShader));
            AssetGuid? imguiGuid = assetDb.Resolve(HostShaders.ImGuiPath);
            if (imguiGuid is not { IsEmpty: false } imguiShaderGuid)
                throw new InvalidOperationException($"Required host shader '{HostShaders.ImGuiPath}' is not indexed.");
            Handle<Shader> imguiShader = LoadShader(imguiShaderGuid);
            if (!imguiShader.IsValid)
                throw new InvalidOperationException($"Required host shader '{HostShaders.ImGuiPath}' did not load.");
            imguiLayer = new ImGuiLayer(
                context,
                assets.Get(imguiShader));
            input = window.CreateInput();
            keyboard = input.Keyboards.FirstOrDefault();
            mouse = input.Mice.FirstOrDefault();
            imguiInput = new ImGuiInput(input, window);
            imguiInput.Update(1.0f / 60.0f);
            if (mouse != null)
            {
                mouse.Scroll += (_, scroll) =>
                {
                    if (scroll.Y > 0)
                        forcedLodLevel++;
                    else if (scroll.Y < 0)
                        forcedLodLevel--;
                    if (forcedLodLevel < -1)
                        forcedLodLevel = -1;
                    Console.WriteLine(
                        $"LOD Mode: {(forcedLodLevel == -1 ? "Auto" : forcedLodLevel.ToString())}");
                };
            }
            world = new GameWorld();
            dynamicSceneQuery = world.World.Query(
                new QueryDefinitionBuilder()
                    .Read<DynamicSceneInstance>()
                    .ReadWrite<LocalTransform>()
                    .ReadWrite<MaterialOverride>());
            _ = ResolveRequiredStartupMaterialGuid();
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

            RefreshAvailableMaterialGuids();
            RefreshAvailableMeshes();

            string meshMessage = string.Empty;
            if (availableMeshes.Count > 0 && TryLoadMeshFromFile(availableMeshes[0], out meshMessage))
            {
                Console.WriteLine(meshMessage);
                var firstLoaded = clusterPipeline
                    .Meshes
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .First();
                SpawnStartupLights(world);
                SpawnStartupInstances(world, firstLoaded.Value, startupInstanceCount, ResolveStartupMaterials());
            }
            else
            {
                statusMessage = availableMeshes.Count == 0
                    ? $"No .mesh.asset files found in {samplesDirectory}"
                    : meshMessage;
                Console.WriteLine(statusMessage);
            }

            Console.WriteLine("Controls:");
            Console.WriteLine("  WASD + Space/Ctrl: Move");
            Console.WriteLine("  Shift: Speed boost");
            Console.WriteLine("  Right mouse: Look");
            Console.WriteLine("  Scroll: Adjust forced LOD");
            Console.WriteLine("  1: Cluster ID debug");
            Console.WriteLine("  2: LOD debug");
            Console.WriteLine("  3: Raster debug");
            Console.WriteLine("  4: Normal shading");
            Console.WriteLine("  F5: Capture one debug frame");
            Console.WriteLine("  F6: Toggle dynamic scene");
            Console.WriteLine(
                $"Dynamic scene: {(dynamicSceneEnabled ? "enabled" : "disabled")} "
                + $"(animated instances={dynamicSceneEntities.Count}, dynamic material overrides={dynamicSceneEntities.Count}, "
                + $"lights=2..{dynamicDirectionalLights.Length + dynamicPointLights.Length}, graph variants=4; use --static-scene to disable).");
        };

        window.Update += UpdateInput;

        window.Render += deltaSeconds =>
        {
            var frameScope = Profiler.BeginScope("Runtime.Frame");
            bool closeWindow = false;
            try
            {
            if (context == null
                || world == null
                || renderWorld == null
                || clusterPipeline == null
                || clusterDebugFeature == null
                || postTonemap == null
                || imguiLayer == null)
                throw new InvalidOperationException("Runtime render resources were not initialized before rendering.");

            if (ApplyPendingResize())
                return;

            GameWorld currentWorld = world;
            using (currentWorld.World.SuppressSerializationJournal())
            {
                AnimateScene(currentWorld);
                using (Profiler.BeginScope("Runtime.WorldUpdate"))
                {
                    currentWorld.Update(deltaSeconds);
                }
            }

            IDevice device;
            IQueue queue;
            ISwapchain swapchain;
            TextureDesc backBufferDesc;
            using (Profiler.BeginScope("Runtime.Frame.Context"))
            {
                device = context.GraphicsDevice
                    ?? throw new InvalidOperationException("Graphics context has not been initialized.");
                queue = context.GraphicsQueue
                    ?? throw new InvalidOperationException("Graphics context has no graphics queue.");
                swapchain = context.GraphicsSwapchain
                    ?? throw new InvalidOperationException("Graphics context has no swapchain.");
                backBufferDesc = device.GetTextureDesc(swapchain.CurrentTexture);
            }

            ImDrawDataPtr imguiDrawData;
            using (Profiler.BeginScope("Runtime.ImGui"))
            {
                imguiInput?.Update(Math.Clamp((float)deltaSeconds, 1.0f / 1000.0f, 1.0f / 30.0f), backBufferDesc.Width, backBufferDesc.Height);
                ImGui.NewFrame();
                DrawDebugUi(backBufferDesc.Width, backBufferDesc.Height);
                ImGui.Render();
                imguiDrawData = ImGui.GetDrawData();
            }
            if (!imguiFrameLogged)
            {
                imguiFrameLogged = true;
                Vector2 displaySize = ImGui.GetIO().DisplaySize;
                Console.WriteLine(
                    $"ImGui frame: open={debugUiOpen}, display={displaySize.X}x{displaySize.Y}, drawLists={imguiDrawData.CmdListsCount}, vertices={imguiDrawData.TotalVtxCount}, indices={imguiDrawData.TotalIdxCount}, framebufferScale={imguiDrawData.FramebufferScale.X}x{imguiDrawData.FramebufferScale.Y}.");
            }

            Matrix4x4 view;
            Matrix4x4 proj;
            float lodScale;
            using (Profiler.BeginScope("Runtime.Frame.CameraAndVariants"))
            {
                float aspect = backBufferDesc.Width / (float)Math.Max(backBufferDesc.Height, 1u);
                view = camera.GetViewMatrix();
                proj = camera.GetProjectionMatrix(aspect);
                lodScale = camera.GetLodScale(backBufferDesc.Height);
                effectiveDebugMode = debugMode;
                effectiveHiZMode = hiZMode;
                effectiveUseSWRaster = useSWRaster;
                effectiveUseDeformCache = useDeformCache;
                effectiveTemporalResolve = temporalResolveEnabled;
                if (dynamicSceneEnabled)
                {
                    switch (lastDynamicGraphVariant)
                    {
                        case 1:
                            effectiveTemporalResolve = false;
                            effectiveHiZMode = HiZMode.Phase1Only;
                            break;
                        case 2:
                            effectiveUseSWRaster = !useSWRaster;
                            break;
                        case 3:
                            break;
                    }
                }
            }

            using (Profiler.BeginScope("Runtime.Cluster.ApplyOptions"))
            {
                clusterPipeline.DebugMode = effectiveDebugMode;
                clusterPipeline.HiZMode = effectiveHiZMode;
                clusterPipeline.UseVisBuffer = useVisBuffer;
                clusterPipeline.UseSWRaster = effectiveUseSWRaster;
                clusterPipeline.UseDeformCache = effectiveUseDeformCache;
                clusterPipeline.BypassCulling = bypassCulling;
                clusterPipeline.FreezeCullingCamera = freezeCullingCamera;
                clusterPipeline.TemporalResolveEnabled = effectiveTemporalResolve;
            }
            using (Profiler.BeginScope("Runtime.Cluster.PrepareFrame"))
            {
                using (currentWorld.World.SuppressSerializationJournal())
                using (renderWorld.World.SuppressSerializationJournal())
                {
                    renderExtractor!.Rebuild(currentWorld.World);
                    clusterPipeline.PrepareFrame(renderWorld, histories, cameraHistory, temporalState);
                }
            }
            using (Profiler.BeginScope("Runtime.Cluster.SetCamera"))
            {
                clusterPipeline.SetCamera(
                    view,
                    proj,
                    camera.Position,
                    cameraHistory,
                    1.0f,
                    lodScale,
                    forcedLodLevel);
            }
            if (startupOptions.VerifyFrameOutput && frameIndex == 0)
            {
                SceneLights lights = renderWorld.SceneLights;
                Console.WriteLine(
                    $"Render lights: directional={lights.DirectionalLights.Length}, point={lights.PointLights.Length}, spot={lights.SpotLights.Length}.");
            }

            bool captureDebug = captureDebugFrame;
            bool captureOutput = startupOptions.VerifyFrameOutput
                && (frameLimit <= 0 ? frameIndex == 0 : frameIndex + 1u == (uint)frameLimit);
            RenderFrameData frameData;
            using (Profiler.BeginScope("Runtime.FrameData.Create"))
            {
                frameData = new RenderFrameData(
                    new FrameData(
                        device,
                        backBufferDesc,
                        swapchain.CurrentTexture,
                        swapchain.CurrentRenderTargetView),
                    new ViewData(backBufferDesc.Width, backBufferDesc.Height),
                    histories,
                    clusterPipeline,
                    renderWorld,
                    cameraHistory,
                    temporalState,
                    postTonemap,
                    clusterDebugFeature,
                    imguiLayer,
                    imguiDrawData,
                    captureDebug,
                    captureOutput ? frameCapture : null);
            }
            var graphQueues = startupOptions.AsyncCompute
                ? new GraphQueues(device, queue)
                : new GraphQueues(device, queue, compute: null, copy: device.Features.CopyQueue ? device.GetQueue(QueueType.Copy) : null);
            context.PipelineIssueSink = debugUiOpen ? pipelineIssues : null;
            using (Profiler.BeginScope("Runtime.RenderGraph.BeginFrame"))
            {
                renderGraph.BeginFrame(frameData, static (graph, data) => RecordRenderFrame(graph, data));
                captureDebugFrame = false;
            }
            using (Profiler.BeginScope("Runtime.RenderGraph.Compile"))
            {
                renderGraph.Compile(graphQueues);
            }

            if (startupOptions.WaitForPipelineWarmup && !pipelineWarmupReady)
            {
                using (Profiler.BeginScope("Runtime.PipelineWarmup.Wait"))
                {
                    PipelineWarmup warmup = clusterPipeline.WarmupPipelines();
                    if (warmup.RequiredFailed > 0)
                    {
                        PipelineIssue? failedIssue = null;
                        for (int i = 0; i < warmup.Issues.Count; i++)
                        {
                            PipelineIssue issue = warmup.Issues[i];
                            if (issue.Need == PipelineNeed.Required && issue.Status == PipelineStatus.Failed)
                            {
                                failedIssue = issue;
                                break;
                            }
                        }

                        string message = failedIssue is PipelineIssue issueWithFailure
                            ? $"Required pipeline warmup failed for '{issueWithFailure.Owner}' "
                                + $"from '{issueWithFailure.Source}': {issueWithFailure.Error}"
                            : $"Required pipeline warmup failed; requested={warmup.Requested}, ready={warmup.Ready}, failed={warmup.Failed}.";
                        throw new InvalidOperationException(message);
                    }

                    pipelineWarmupReady = warmup.ReadyToUse;
                    if (warmup.Requested > 0)
                    {
                        Console.WriteLine(
                            $"Pipeline warmup: requested={warmup.Requested}, ready={warmup.Ready}, failed={warmup.Failed}, processed={warmup.Processed}.");
                    }
                }
            }

            using (Profiler.BeginScope("Runtime.RenderGraph.ExecuteFrame"))
            {
                var executionSwapchain = startupOptions.SkipSwapchainPresent ? null : swapchain;
                renderDocCapture?.Begin(frameIndex);
                renderGraph.Execute(graphQueues, executionSwapchain, context.PresentSyncInterval);
                renderDocCapture?.End(frameIndex);
            }

            using (Profiler.BeginScope("Runtime.Frame.Finish"))
            {
                if (startupOptions.SkipSwapchainPresent)
                    Profiler.FrameMark();

                if (frameIndex == 0)
                {
                    string frameVerb = startupOptions.SkipSwapchainPresent ? "Submitted" : "Presented";
                    Console.WriteLine(
                        $"{frameVerb} frame 1 ({backBufferDesc.Width}x{backBufferDesc.Height}, instances={renderWorld.CountInstances()}).");
                }

                frameIndex++;
                closeWindow = frameLimit > 0 && frameIndex >= frameLimit;
            }
            }
            finally
            {
                frameScope.Dispose();
            }

            if (closeWindow)
                window.Close();
        };

        window.Resize += size =>
        {
            pendingResizeWidth = checked((uint)Math.Max(1, size.X));
            pendingResizeHeight = checked((uint)Math.Max(1, size.Y));
            resizePending = true;
        };

        window.Closing += () =>
        {
            Console.WriteLine($"Runtime shutdown after {frameIndex} frame(s).");
            input?.Dispose();
            input = null;
            keyboard = null;
            mouse = null;
            imguiInput?.Dispose();
            imguiInput = null;
            renderGraph.Dispose();
            clusterDebugFeature?.Dispose();
            clusterDebugFeature = null;
            postTonemap?.Dispose();
            postTonemap = null;
            imguiLayer?.Dispose();
            imguiLayer = null;
            frameCapture = null;
            renderDocCapture = null;
            clusterPipeline?.Dispose();
            clusterPipeline = null;
            context?.ReleasePipelines();
            histories.Dispose();
            renderExtractor = null;
            renderWorld = null;
            world = null;
            context?.Dispose();
            context = null;
        };

        bool renderLoopFailed = false;
        try
        {
            window.Run();
        }
        catch
        {
            renderLoopFailed = true;
            throw;
        }
        finally
        {
            DisposeWindow(window, renderLoopFailed);
            Profiler.Shutdown();
        }
    }

    private static void DisposeWindow(IWindow window, bool renderLoopFailed)
    {
        try
        {
            window.Dispose();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("render loop", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Window cleanup skipped after render-loop exception: {ex.Message}");
        }
        catch (Exception ex) when (renderLoopFailed)
        {
            Console.Error.WriteLine("Window cleanup failed after render-loop exception:");
            Console.Error.WriteLine(ex);
        }
    }

    static int Main(string[] args)
    {
        CrashDialogPolicy.DisableUi();

        try
        {
            RuntimeStartupOptions startupOptions = RuntimeStartupOptions.Parse(args);
            Profiler.Configure(startupOptions.Profiler);
            Profiler.SetThreadName("SomeEngine Runtime");

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>(1280, 720);
            options.Title = "SomeEngine Runtime - Cluster Rendering";
            options.API = GraphicsAPI.None;
            options.VSync = startupOptions.WindowVSync;
            options.UpdatesPerSecond = startupOptions.UpdatesPerSecond;
            options.FramesPerSecond = startupOptions.FramesPerSecond;

            StartRuntime(options, startupOptions);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unhandled runtime exception:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

}
