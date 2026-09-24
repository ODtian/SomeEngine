using SomeEngine.Assets;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeECS.Core;
using ReflectFlags = System.Reflection.BindingFlags;

namespace SomeEngine.Tests;

public class RenderWorldRefactorTests
{
    [Fact]
    public void DeletedConcepts_DoNotExistInProduction()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string[] forbidden =
        [
            "BindPlan",
            "BindRole",
            "BatchBind",
            "BindingSetCache",
            "ClusterBatch",
            "ClusterBatches",
            "ClusterDispatch",
            "ClusterDispatches",
            "ClusterPipes",
            "ClusterPsoSet",
            "ClusterPsoRun",
            "ClusterShaderSet",
            "ClusterModes",
            "ClusterRow",
            "ClusterMaterial",
            "ClusterPassState",
            "ClusterPassTargets",
            "DeformCacheSet",
            "ClusterShadeComponent",
            "FullscreenPipelineCache",
            "PassBakers",
            "ClusterBakers",
            "MaterialPassBaker",
            "MaterialSlotBind",
            "MaterialSlotBuffer",
            "MaterialSlotOffset",
            "MaterialProgram",
            "OverlayShade : IComponent",
            "ShaderParamBag",
            "ShaderVariantRef",
            "StageGpu",
            "VertexLayoutReq",
            "CanonicalDescriptor",
            "CacheSignature",
            "SurfaceSignature",
            "MaterialAssetGuids",
            "PassEntities",
            "PassWorld",
            "PsoOwner",
            "_lastStamp",
            "MaterialStamp",
            "EntryStamp",
            "InputHash",
            "SnapshotChanged",
            "RefreshSnapshot",
            "sourceHashes",
            "_sourceHashes",
            "ShaderEntryRef",
            "GraphPlan",
            "ResourcePlan",
            "BarrierPlan",
            "BarrierReport",
            "CompilePlan",
            "BuildPassPlan",
            "BuildBarrierPlans",
            "BuildResolvePlans",
            "BuildResourcePlans",
            "ExecutePlan",
            "BindingReport",
            "CounterHub",
            "EventHub",
            "FramePlan",
            "FrameTemplate",
            "FrameDiagnostics",
            "FrameTiming",
            "StepSet",
            "StepsSet",
            "BarrierDesc",
            "BindingWriter",
            "BindGroup",
            "DescriptorGroup",
            "PassTiming",
            "TimingSample",
            "TimingSpan",
            "PipelineMissKind",
            "ProfilerSink",
            "ProfilerSinkFactory",
            "IProfilerSink",
            "NoopProfiler",
            "CompositeProfiler",
            "ManagedProfilerSink",
            "TracyProfilerSink",
            "PipelineProfiler",
            "ProfileCenter",
            "ProfilerHub",
            "PsoStore",
            "RenderProfiler",
            "RhiProfiler",
            "VertexLayoutDescriptor",
            "ShaderProgram",
            "TimingTable",
            "UsePso",
            "class Program",
            "RunRuntime(",
            "RuntimeSatisfied",
            "WaitAll",
            "WaitPipelines",
            "WaitReady",
            "Profiler.Bindings(\"",
            "Profiler.Pipelines(\"",
            "Profiler.Count(",
        ];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (string name in forbidden)
                Assert.DoesNotContain(name, source);
        }

        Assert.Null(typeof(Material).Assembly.GetType("SomeEngine.Render.Materials.PipelineState"));
    }

    [Fact]
    public void ProfileSinks_StayInDiagnostics()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string diagnosticsRoot = Path.Combine(root, "SomeEngine.Core", "Diagnostics")
            .Replace('\\', '/');
        string[] forbidden =
        [
            "IProfileSink",
            "ProfileSinks",
            "ProfileSinkGroup",
            "ManagedProfileSink",
            "TracySink",
        ];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.StartsWith(diagnosticsRoot, StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (string name in forbidden)
                Assert.DoesNotContain(name, source);
        }
    }

    [Fact]
    public void ProfileCounts_UseSingleReporter()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string[] forbidden =
        [
            "FlushProfile",
            "Profiler.DependencyBarriers(",
            "Profiler.Bindings(",
            "Profiler.Pipelines(",
            "Profiler.Graph(",
            "Profiler.Queue(",
            "Profiler.Barrier(",
            "Profiler.Descriptor(",
            "Profiler.Binding(",
            "Profiler.Pipeline(",
            "Profiler.Count(",
            "BindingMetric",
            "GraphMetric",
            "QueueMetric",
            "PipelineMetric",
            "PipelineUse",
            "FlushCounts(",
            "DeviceCounts",
            "PipelineCounts",
            "PipelineCounters",
            "CountBarriers",
            "CountDescriptors",
            "CountNative",
            "CountDependencies",
        ];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.EndsWith("/SomeEngine.Core/Diagnostics/Profiler.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (string name in forbidden)
                Assert.DoesNotContain(name, source);
        }
    }

    [Fact]
    public void Profiling_UsesSingleCenter()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string profilerPath = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "Profiler.cs");
        string[] removed =
        [
            Path.Combine(root, "SomeEngine.Runtime", "RuntimeTrace.cs"),
            Path.Combine(root, "SomeEngine.Rhi", "Diagnostics", "RhiTrace.cs"),
            Path.Combine(root, "SomeEngine.Render", "RenderTrace.cs"),
            Path.Combine(root, "SomeEngine.Render", "RHI", "PipelineTrace.cs"),
            Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraphTrace.cs"),
        ];

        foreach (string path in removed)
            Assert.False(File.Exists(path), path);

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain("RuntimeTrace", source);
            Assert.DoesNotContain("RhiTrace", source);
            Assert.DoesNotContain("RenderTrace", source);
            Assert.DoesNotContain("RenderGraphTrace", source);
            Assert.DoesNotContain("PipelineTrace", source);
        }

        string profiler = File.ReadAllText(profilerPath);
        Assert.Contains("public static class Profiler", profiler);
    }

    [Fact]
    public void RenderGraphQueue_WaitsAreCompiled()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src", "SomeEngine.Render", "Graph");
        string graph = File.ReadAllText(Path.Combine(root, "RenderGraph.cs"));
        string compiler = File.ReadAllText(Path.Combine(root, "RenderGraphCompiler.cs"));
        string execute = File.ReadAllText(Path.Combine(root, "RenderGraph.Execute.cs"));

        Assert.Contains("public List<int>[] QueueWaits = [];", graph);
        Assert.Contains("compile.PrepareQueueWaits();", compiler);
        Assert.Contains("compile.QueueWaits[targetBatch].Add(linkIndex);", compiler);
        Assert.Contains("private void SubmitBatches(", execute);
        Assert.Contains("private static bool CanSubmit(", execute);
        Assert.Contains("var links = compile.QueueWaits[batchIndex];", execute);
        Assert.Contains("Span<int> queueHeads = stackalloc int[QueueCount];", execute);
        Assert.Contains("nextSameQueue[batchIndex] = queueHeads[queueSlot];", execute);
        Assert.Contains("queueHeads[QueueSlot(batch.Queue)] != batchIndex", execute);
        Assert.Contains("compile.QueueLinks[links[linkIndex]]", execute);
        Assert.Contains("submittedBatches[link.SourceBatch] == 0", execute);
        Assert.DoesNotContain("link.TargetBatch != batchIndex", execute);
        Assert.DoesNotContain("if (linkIndex < compile.QueueLinks.Count)", execute);
    }

    [Fact]
    public void RenderGraphTimestamps_ReuseBacking()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src", "SomeEngine.Render", "Graph");
        string graph = File.ReadAllText(Path.Combine(root, "RenderGraph.cs"));
        string executor = File.ReadAllText(Path.Combine(root, "RenderGraphExecutor.cs"));
        string timing = File.ReadAllText(Path.Combine(root, "RenderGraph.Timing.cs"));
        string execute = File.ReadAllText(Path.Combine(root, "RenderGraph.Execute.cs"));
        string resources = File.ReadAllText(Path.Combine(root, "RenderGraph.Resources.cs"));

        Assert.Contains("internal readonly List<DeviceTimestamps> TimestampPool = [];", executor);
        Assert.Contains("internal readonly System.Threading.Lock ViewCacheGate = new();", executor);
        Assert.Contains("internal readonly System.Threading.Lock BindingSetGate = new();", executor);
        Assert.Contains("internal readonly System.Threading.Lock FrameUseGate = new();", executor);
        Assert.Contains("internal readonly List<TextureViewHandle> OwnedTextureViews = [];", executor);
        Assert.Contains("internal readonly List<BufferViewHandle> OwnedBufferViews = [];", executor);
        Assert.Contains("internal readonly FrameUploadBuffer FrameUploadBuffer = new();", executor);
        Assert.Contains("internal readonly FlatDictionary<int, List<BindingSetEntry>> BindingSetOwner = new();", executor);
        Assert.Contains("internal readonly BindingIndex BindingIndex = new();", executor);
        Assert.Contains("internal readonly BindingCache BindingCache = new();", executor);
        Assert.Contains("internal PipelineCache? PipelineCache;", executor);
        Assert.Contains("private System.Threading.Lock _viewCacheGate => _executor.ViewCacheGate;", graph);
        Assert.Contains("private System.Threading.Lock _bindingSetGate => _executor.BindingSetGate;", graph);
        Assert.Contains("private System.Threading.Lock _frameUseGate => _executor.FrameUseGate;", graph);
        Assert.Contains("private List<TextureViewHandle> _ownedTextureViews => _executor.OwnedTextureViews;", graph);
        Assert.Contains("private List<BufferViewHandle> _ownedBufferViews => _executor.OwnedBufferViews;", graph);
        Assert.Contains("private FrameUploadBuffer _frameUploadBuffer => _executor.FrameUploadBuffer;", graph);
        Assert.Contains("private BindingIndex _bindingIndex => _executor.BindingIndex;", graph);
        Assert.Contains("private BindingCache _bindingCache => _executor.BindingCache;", graph);
        Assert.Contains("private PipelineCache? _pipelineCache", graph);
        Assert.DoesNotContain("private readonly System.Threading.Lock _viewCacheGate = new();", graph);
        Assert.DoesNotContain("private readonly System.Threading.Lock _bindingSetGate = new();", graph);
        Assert.DoesNotContain("private readonly System.Threading.Lock _frameUseGate = new();", graph);
        Assert.DoesNotContain("private readonly List<TextureViewHandle> _ownedTextureViews = [];", graph);
        Assert.DoesNotContain("private readonly List<BufferViewHandle> _ownedBufferViews = [];", graph);
        Assert.DoesNotContain("private readonly FrameUploadBuffer _frameUploadBuffer;", graph);
        Assert.DoesNotContain("private readonly FlatDictionary<int, List<BindingSetEntry>> _bindingSetOwner = new();", graph);
        Assert.DoesNotContain("private readonly BindingIndex _bindingIndex = new();", graph);
        Assert.DoesNotContain("private readonly BindingCache _bindingCache = new();", graph);
        Assert.DoesNotContain("private PipelineCache? _pipelineCache;", graph);
        Assert.Contains("private DeviceTimestamps? RentTimestamps", timing);
        Assert.Contains("private void ReturnTimestamps", timing);
        Assert.Contains("private void DestroyTimestamps", timing);
        Assert.Contains("timestamps.Reset();", timing);
        Assert.Contains("RentTimestamps(device, compile.Count, finalCommandWork)", execute);
        Assert.Contains("DestroyTimestamps();", execute);
        Assert.Contains("ReturnTimestamps(timestamps);", execute);
        Assert.Contains("ReturnTimestamps(timestamps);", resources);
        Assert.Contains("timestamps?.Destroy();", resources);
        Assert.Contains("DestroyTimestamps();", resources);
        Assert.DoesNotContain("DeviceTimestamps.Create(device, compile.Count", execute);
        Assert.DoesNotContain("frame.Timestamps?.Destroy()", resources);
    }

    [Fact]
    public void ProductionPassBindings_UseDirectPath()
    {
        string renderRoot = Path.Combine(TestProjectPaths.ProjectRoot(), "src", "SomeEngine.Render");
        string[] folders =
        [
            "Graph",
            "Pipelines",
            "UI",
        ];

        foreach (string folder in folders)
        {
            string path = Path.Combine(renderRoot, folder);
            foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals("PassParameters.cs", StringComparison.Ordinal))
                    continue;

                string source = File.ReadAllText(file);
                Assert.DoesNotContain(".Build()", source);
                Assert.DoesNotContain("RenderBindings.Set(", source);
                Assert.DoesNotContain("stackalloc BindingResourceDesc", source);
                Assert.DoesNotContain("_bindingResources", source);
                Assert.DoesNotContain("_materialResources", source);
                Assert.DoesNotContain("BindInput.FillList(", source);
                Assert.DoesNotContain("RequireSingleSet(", source);
                Assert.DoesNotContain("EnsureSet(", source);
                Assert.DoesNotContain("void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)", source);
                Assert.DoesNotContain("void SetParameters(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)", source);
                Assert.DoesNotContain(".SetBindings(", source);
            }
        }

        string parameters = File.ReadAllText(Path.Combine(renderRoot, "Graph", "PassParameters.cs"));
        string builder = File.ReadAllText(Path.Combine(renderRoot, "Graph", "RenderGraphBuilder.cs"));
        string hash = File.ReadAllText(Path.Combine(renderRoot, "Graph", "BindingHash.cs"));
        string resources = File.ReadAllText(Path.Combine(renderRoot, "Graph", "RenderGraph.Resources.cs"));
        string bindState = File.ReadAllText(Path.Combine(TestProjectPaths.ProjectRoot(), "src", "SomeEngine.Rhi", "BindState.cs"));
        Assert.DoesNotContain("public PassParameters(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)", parameters);
        Assert.DoesNotContain("public PassParameters Build()", parameters);
        Assert.DoesNotContain("public ReadOnlyMemory<BindingResourceDesc> Resources", parameters);
        Assert.DoesNotContain("public ReadOnlySpan<BindingResourceDesc> ResourceSpan", parameters);
        Assert.DoesNotContain("private bool _dirty;", parameters);
        Assert.DoesNotContain("BindingHash.Compute(_layout, ResourceSpan)", parameters);
        Assert.Contains("internal PassParameters(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)", parameters);
        Assert.Contains("internal PassParameters Build()", parameters);
        Assert.Contains("internal BindingResourceDesc[] OwnedResources", parameters);
        Assert.Contains("internal ReadOnlySpan<BindingResourceDesc> ResourceSpan", parameters);
        Assert.Contains("internal PassBindings Buffer(ReflectedBinding binding, RenderGraphHandle handle", parameters);
        Assert.Contains("internal PassBindings Texture(ReflectedBinding binding, RenderGraphHandle handle", parameters);
        Assert.DoesNotContain("public PassBindings Buffer(BindingResourceDesc resource", parameters);
        Assert.DoesNotContain("public PassBindings Texture(BindingResourceDesc resource", parameters);
        Assert.Contains("internal static class BindingResources", parameters);
        string bindInput = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "BindInput.cs"));
        Assert.Contains("internal static void AddResource(", bindInput);
        Assert.Contains("internal static uint GetSetIndex(", bindInput);
        Assert.Contains("resources.ResourceSpan", bindInput);
        Assert.Contains("public static class BindRules", bindState);
        Assert.Contains("public readonly record struct BindStateInfo", bindState);
        Assert.Contains("internal RenderGraphHandle Use(ReflectedBinding binding, RenderGraphHandle handle)", builder);
        Assert.Contains("BindStateInfo info = BindRules.Resolve(binding.Type);", builder);
        Assert.Contains("info.Target is not BindTarget.BufferView and not BindTarget.TextureView", builder);
        Assert.Contains("PassBindings.BindingAccess(binding.Type)", builder);
        Assert.DoesNotContain("private static RenderGraphAccess BindingAccess", builder);
        Assert.DoesNotContain("BindingType.StorageBufferReadWrite", builder);
        Assert.DoesNotContain("info.State == ResourceState.UnorderedAccess", builder);
        Assert.Contains("internal static BufferViewHandle BufferView(", parameters);
        Assert.Contains("_hash = BindingHash.Begin(layout);", parameters);
        Assert.Contains("_hash.Add(resource);", parameters);
        Assert.Contains("BindingHash.Finish(_hash, _resources.Count)", parameters);
        Assert.Contains("public static HashCode Begin(BindingLayoutHandle layout)", hash);
        Assert.Contains("public static int Finish(HashCode hash, int count)", hash);
        Assert.Contains("ReadOnlySpan<BindingResourceDesc> resources = bindings.ResourceSpan;", resources);
        Assert.Contains("BindingSetHandle createdHandle = device.CreateBindingSet(layout, snapshot, name);", resources);
        Assert.Contains("BindingSetHandle result = createdHandle;", resources);
        Assert.Contains("device.Destroy(createdHandle);", resources);
        Assert.Null(typeof(RenderGraph).Assembly.GetType("SomeEngine.Render.RHI.RenderBindings"));
        int snapshotIndex = resources.IndexOf("snapshot = ownedResources ?? resources.ToArray();", StringComparison.Ordinal);
        int createIndex = resources.IndexOf("BindingSetHandle createdHandle = device.CreateBindingSet", StringComparison.Ordinal);
        int secondLockIndex = resources.IndexOf("lock (_bindingSetGate)", createIndex, StringComparison.Ordinal);
        Assert.True(snapshotIndex >= 0);
        Assert.True(createIndex > snapshotIndex);
        Assert.True(secondLockIndex > createIndex);

        string shadeBin = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "ShadeBinPass.cs"));
        string clusterDeform = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "ClusterDeformPass.cs"));
        string clusterDraw = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "ClusterDrawPass.cs"));
        string materialShade = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "MaterialShadePass.cs"));
        string swRaster = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "SwRasterPass.cs"));
        Assert.DoesNotContain("params ShaderBindingTable[]", shadeBin);
        Assert.DoesNotContain("new List<ShaderResourceUse>", shadeBin);
        Assert.DoesNotContain("List<ShaderResourceUse>", shadeBin);
        Assert.Contains("stackalloc ShaderResourceUse[ShadeResourceCount]", shadeBin);
        Assert.Contains("BindStateInfo info = BindRules.Resolve(type);", shadeBin);
        Assert.Contains("if (info.State == ResourceState.UnorderedAccess)", shadeBin);
        Assert.Contains("AccessForWriteTarget(binding.Name)", shadeBin);
        Assert.DoesNotContain("BindingType.ConstantBuffer => ResourceState.ConstantBuffer", shadeBin);
        Assert.Contains("\"BinIndirectArgs\" or \"PixelCoordBuffer\" => RenderGraphAccess.WriteOnly", shadeBin);
        foreach (string source in new[] { shadeBin, clusterDeform, clusterDraw, materialShade, swRaster })
        {
            Assert.DoesNotContain("switch (binding.Name)", source);
            Assert.DoesNotContain("switch(binding.Name)", source);
            Assert.DoesNotContain("FindShadeResource", source);
            Assert.DoesNotContain("AccessFor(string name", source);
        }

        string bvhPatch = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "BvhPatchPass.cs"));
        string traverse = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "ClusterTraversePass.cs"));
        string scene = File.ReadAllText(Path.Combine(renderRoot, "Pipelines", "ClusterPipeline", "ClusterSceneStage.cs"));
        string clusterRoot = Path.Combine(renderRoot, "Pipelines", "ClusterPipeline");
        foreach (string file in Directory.EnumerateFiles(clusterRoot, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("GetBufferView(", source);
            Assert.DoesNotContain("UsesRawView", source);
            Assert.DoesNotContain("ViewKindOf(", source);
        }

        Assert.Contains(".Buffer(globalBvh, data.GlobalBVH)", bvhPatch);
        Assert.Contains("builder.Use(_layout.GetRequired(GlobalBVHResource), data.GlobalBVH);", bvhPatch);
        Assert.Contains("builder.Use(_layout.GetRequired(PatchesResource), data.Patches);", bvhPatch);
        Assert.Contains("builder.Use(_layout.GetRequired(UniformsResource), data.Uniforms);", bvhPatch);
        Assert.DoesNotContain("builder.ReadWrite(data.GlobalBVH, ResourceState.UnorderedAccess);", bvhPatch);
        Assert.DoesNotContain("builder.Read(data.Patches, ResourceState.ShaderResource);", bvhPatch);
        Assert.DoesNotContain("builder.Read(data.Uniforms, ResourceState.ConstantBuffer);", bvhPatch);
        Assert.Contains(".Buffer(_traverseCandidateCount, candidateCount)", traverse);
        Assert.Contains(".Buffer(_traverseCandidateArgs, candidateArgs)", traverse);
        Assert.Contains(".Buffer(phase2Count, phase2CandidateCount)", scene);
        Assert.Contains(".Buffer(phase2Args, phase2CandidateArgs)", scene);
        Assert.Contains(".Texture(hiZ, HiZView(context, hiZTexture))", scene);
    }

    [Fact]
    public void ShadeBinPass_BindingStepsUseExplicitWriteOnlyAndReadWriteAccess()
    {
        var createSteps = typeof(ShadeBinPass).GetMethod("CreateSteps", ReflectFlags.Static | ReflectFlags.NonPublic);
        Assert.NotNull(createSteps);

        ReflectedBinding[] bindings =
        [
            new(
                "BinIndirectArgs",
                Set: 0,
                Binding: 0,
                BindingType.StorageBufferReadWrite,
                ShaderStageFlags.Compute),
            new(
                "ReserveCounters",
                Set: 0,
                Binding: 1,
                BindingType.StorageBufferReadWrite,
                ShaderStageFlags.Compute),
        ];

        var steps = (Array)createSteps!.Invoke(null, [bindings])!;
        Assert.Equal(2, steps.Length);

        object writeOnlyStep = steps.GetValue(0)!;
        object readWriteStep = steps.GetValue(1)!;
        var accessProperty = writeOnlyStep.GetType().GetProperty("Access", ReflectFlags.Instance | ReflectFlags.Public | ReflectFlags.NonPublic);
        Assert.NotNull(accessProperty);

        Assert.Equal(RenderGraphAccess.WriteOnly, Assert.IsType<RenderGraphAccess>(accessProperty!.GetValue(writeOnlyStep)!));
        Assert.Equal(RenderGraphAccess.ReadWrite, Assert.IsType<RenderGraphAccess>(accessProperty.GetValue(readWriteStep)!));
    }

    [Fact]
    public void ClusterDeformPass_BindingStepsUseExplicitWriteOnlyAndReadWriteAccess()
    {
        var executionPlanType = typeof(ClusterDeformPass).GetNestedType("BindingSlotExecutionPlan", ReflectFlags.NonPublic);
        Assert.NotNull(executionPlanType);

        var create = executionPlanType!.GetMethod("Create", ReflectFlags.Static | ReflectFlags.Public);
        Assert.NotNull(create);

        BindingSlotPlan plan = new(
        [
            new(
                "DeformCache",
                Set: 0,
                Binding: 0,
                BindingType.StorageBufferReadWrite,
                ShaderStageFlags.Compute),
            new(
                "CacheOffsetsWrite",
                Set: 0,
                Binding: 1,
                BindingType.StorageBufferReadWrite,
                ShaderStageFlags.Compute),
            new(
                "CacheAllocationCounter",
                Set: 0,
                Binding: 2,
                BindingType.StorageBufferReadWrite,
                ShaderStageFlags.Compute),
        ]);

        object executionPlan = create!.Invoke(null, [plan])!;
        var stepsProperty = executionPlan.GetType().GetProperty("Steps", ReflectFlags.Instance | ReflectFlags.Public | ReflectFlags.NonPublic);
        Assert.NotNull(stepsProperty);

        var steps = (Array)stepsProperty!.GetValue(executionPlan)!;
        Assert.Equal(3, steps.Length);

        var accessProperty = steps.GetValue(0)!.GetType().GetProperty("Access", ReflectFlags.Instance | ReflectFlags.Public | ReflectFlags.NonPublic);
        Assert.NotNull(accessProperty);

        Assert.Equal(RenderGraphAccess.WriteOnly, Assert.IsType<RenderGraphAccess>(accessProperty!.GetValue(steps.GetValue(0)!)!));
        Assert.Equal(RenderGraphAccess.WriteOnly, Assert.IsType<RenderGraphAccess>(accessProperty.GetValue(steps.GetValue(1)!)!));
        Assert.Equal(RenderGraphAccess.ReadWrite, Assert.IsType<RenderGraphAccess>(accessProperty.GetValue(steps.GetValue(2)!)!));
    }

    [Fact]
    public void MaterialBindings_UseSortedLookup()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "MaterialBindings.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("public bool TryGet", StringComparison.Ordinal);
        int end = source.IndexOf("public static MaterialBindings Create(", start, StringComparison.Ordinal);
        string lookup = source[start..end];

        Assert.Contains("while (lo <= hi)", lookup);
        Assert.Contains("int compare = Compare(entry, binding);", lookup);
        Assert.DoesNotContain("for (int i = 0; i < entries.Length; i++)", lookup);
        Assert.Contains("Array.Sort(sorted, Compare);", source);
        Assert.Contains("private static int Compare(Entry entry, ReflectedBinding binding)", source);
    }

    [Fact]
    public void RhiBindingSets_UseDescriptors()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string interfaces = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Interfaces.cs"));
        string descriptors = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Descriptors.cs"));
        string validation = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "RhiBindingValidation.cs"));
        string d3d12 = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12Device.cs"));
        string nul = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullDevice.cs"));
        string nullPasses = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullPasses.cs"));
        string nullCommandList = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullCommandList.cs"));

        Assert.Contains("public BindingResourceDesc[] Resources { get; init; }", descriptors);
        Assert.Contains("MergeResources(ReadOnlySpan<BindingResourceDesc> current", validation);
        Assert.DoesNotContain("CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources", interfaces);
        Assert.DoesNotContain("CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources", d3d12);
        Assert.DoesNotContain("CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources", nul);
        Assert.DoesNotContain("IReadOnlyList<BindingResourceDesc> Resources", descriptors);
        Assert.DoesNotContain("MergeResources(IReadOnlyList<BindingResourceDesc>", validation);
        Assert.DoesNotContain("desc.Resources.ToArray()", d3d12);
        Assert.DoesNotContain("desc.Resources.ToArray()", nul);
        Assert.DoesNotContain("record.Desc.Resources is BindingResourceDesc[]", nullPasses);
        Assert.DoesNotContain("IReadOnlyList<BindingResourceDesc> resources", nullPasses);
        Assert.DoesNotContain("IReadOnlyList<BindingResourceDesc> resources", nullCommandList);
        Assert.DoesNotContain("Resources = resources.ToArray()", d3d12);
        Assert.DoesNotContain("Resources = resources.ToArray()", nul);
    }

    [Fact]
    public void MaterialItems_UseSourceWarmup()
    {
        string clusterRoot = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");
        string source = File.ReadAllText(Path.Combine(clusterRoot, "MaterialItems.cs"));

        Assert.DoesNotContain("int pipelineBudget", source);
        Assert.DoesNotContain("CollectPipelines(pipelineBudget)", source);
        Assert.DoesNotContain("Process(budget);", source);
        Assert.DoesNotContain("public int Process(int budget)", source);
        Assert.DoesNotContain("store.Process(", source);
        Assert.DoesNotContain("_pipelineWork", source);
        Assert.DoesNotContain("AddPipelineWork", source);
        Assert.DoesNotContain("PipelineDone", source);
        Assert.DoesNotContain("Forget(states)", source);
        Assert.Contains("private void CollectPipelines()", source);
        Assert.Contains(
            "context.GetPipeline(state.PipelineState, PipelineNeed.Optional)",
            File.ReadAllText(Path.Combine(clusterRoot, "MaterialShadePass.cs")));
        Assert.DoesNotContain(
            "BindInput.FillAll(",
            File.ReadAllText(Path.Combine(clusterRoot, "MaterialShadePass.cs")));
        Assert.Contains(
            "context.GetPipeline(state.PipelineState, PipelineNeed.Optional)",
            File.ReadAllText(Path.Combine(clusterRoot, "SwRasterPass.cs")));
        Assert.DoesNotContain(
            "BindInput.FillAll(",
            File.ReadAllText(Path.Combine(clusterRoot, "SwRasterPass.cs")));
        Assert.Contains(
            "context.GetPipeline(state.PipelineState, PipelineNeed.Optional)",
            File.ReadAllText(Path.Combine(clusterRoot, "ClusterDrawPass.cs")));
        Assert.Contains(
            "UniformPool<DrawDispatchUniforms>",
            File.ReadAllText(Path.Combine(clusterRoot, "ClusterDrawPass.cs")));
        Assert.Contains(
            "context.GetPipeline(state.PipelineState, PipelineNeed.Optional)",
            File.ReadAllText(Path.Combine(clusterRoot, "ClusterDeformPass.cs")));

        string pipeline = File.ReadAllText(Path.Combine(clusterRoot, "ClusterPipeline.Runtime.cs"));
        Assert.DoesNotContain("ProcessPipelines(budget)", pipeline);
        Assert.DoesNotContain("_context.WaitRequired();", pipeline);
        Assert.DoesNotContain("_materials.Process(budget)", pipeline);
        Assert.DoesNotContain("_materials.Add(graph, renderWorld, modes.UseDeformCache, modes.MaterialPipelineBudget)", pipeline);
        Assert.DoesNotContain("tickets.ToArray()", pipeline);
        Assert.Contains("CollectionsMarshal.AsSpan(tickets)", pipeline);
        Assert.Contains("WarmupPipelines(modes.MaterialPipelineBudget);", pipeline);
        Assert.Contains("_context.WarmupSources(budget);", pipeline);
        Assert.Contains("_context.RefreshSources();", pipeline);
        Assert.Contains("WarmupBuiltins();", pipeline);
    }

    [Fact]
    public void MaterialItems_RegistersPipelineSource()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");
        string bin = File.ReadAllText(Path.Combine(root, "MaterialBin.cs"));
        string items = File.ReadAllText(Path.Combine(root, "MaterialItems.cs"));
        string pipeline = File.ReadAllText(Path.Combine(root, "ClusterPipeline.Runtime.cs"));

        Assert.Contains("internal PipelineRequest PipelineRequest;", bin);
        Assert.Contains("PipelineRequest.ForCompute(", items);
        Assert.Contains("PipelineRequest.ForGraphics(", items);
        Assert.Contains("_context.LeasePipelines(_pipelineCollector)", items);
        Assert.Contains("CollectBins(collector, _uncached.Shade);", items);
        Assert.Contains("CollectBins(collector, _uncached.Sw);", items);
        Assert.Contains("CollectBins(collector, _uncached.Draw);", items);
        Assert.Contains("CollectBins(collector, _uncached.Deform);", items);
        Assert.Contains("CollectBins(collector, _cached.Shade);", items);
        Assert.Contains("CollectBins(collector, _cached.Sw);", items);
        Assert.Contains("CollectBins(collector, _cached.Draw);", items);
        Assert.Contains("CollectBins(collector, _cached.Deform);", items);
        Assert.DoesNotContain("_context.LeasePipelines(this)", items);
        Assert.DoesNotContain("ClearPipelines", items);
        Assert.Contains("private readonly PipelineSourceLease _materialsSource;", pipeline);
        Assert.Contains("_materialsSource = context.AddPipelineSource(_materials);", pipeline);
        Assert.Contains("_materialsSource.Dispose();", pipeline);
    }

    [Fact]
    public void ClusterTraverse_FlushesQueuedUploadCopiesBeforeTraverse()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");
        string traverse = File.ReadAllText(Path.Combine(root, "ClusterTraversePass.cs"));
        string pipeline = File.ReadAllText(Path.Combine(root, "ClusterPipeline.Runtime.cs"));

        Assert.Contains("clears.AddCopiesTo(copyRequests);", traverse);
        Assert.Contains("BufferCopyPasses.AddCopyBatch(graph, \"Cluster Frame Uploads\", [.. copyRequests]);", traverse);
        int enqueueIndex = traverse.IndexOf("clears.AddCopiesTo(copyRequests);", StringComparison.Ordinal);
        int flushIndex = traverse.IndexOf("BufferCopyPasses.AddCopyBatch(graph, \"Cluster Frame Uploads\", [.. copyRequests]);", StringComparison.Ordinal);
        int layersIndex = traverse.IndexOf("AddTraverseLayers(", StringComparison.Ordinal);

        Assert.True(enqueueIndex >= 0);
        Assert.True(flushIndex > enqueueIndex);
        Assert.True(layersIndex > flushIndex);
        Assert.Contains("_uploadCopyRequests);", pipeline);
    }

    [Fact]
    public void ClusterClears_UseCopyStarts()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");
        string traverse = File.ReadAllText(Path.Combine(root, "ClusterTraversePass.cs"));
        string scene = File.ReadAllText(Path.Combine(root, "ClusterSceneStage.cs"));
        string shade = File.ReadAllText(Path.Combine(root, "ShadeBinPass.cs"));
        string bin = File.ReadAllText(Path.Combine(root, "ClusterBinGpu.cs"));
        string shader = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "assets",
            "Shaders",
            "cluster_bvh_traverse.slang"));
        Assert.Contains("new BufferUploadBatch(graph, \"Clear Cluster Traverse Args\")", traverse);
        Assert.Contains("clears.AddUpload(candidateCount, 0, new byte[sizeof(uint)]);", traverse);
        Assert.Contains("clears.AddUpload(candidateArgs, 0, DispatchArgsBytes(0u, 1u, 1u));", traverse);
        Assert.Contains("clears.AddUpload(buffers.PageFault, 0, new byte[checked((int)PageFaults.ByteCount)]);", traverse);
        Assert.DoesNotContain("\"Cluster BVH Traverse Clear\"", traverse);
        Assert.DoesNotContain("\"clear_args\"", traverse);
        Assert.Contains("\"Clear Cluster Cull Args\"", scene);
        Assert.Contains("new BufferUploadBatch(graph, \"Clear Cluster Cull Args\")", scene);
        Assert.Contains("clears.AddUpload(phase2DrawArgs, 0, CullDrawArgsBytes());", scene);
        Assert.DoesNotContain("BufferUploadPasses.AddUploadPass(\r\n            graph,\r\n            \"Clear Cluster Cull Args\",\r\n            cull.Phase2DrawArgs,", scene);
        Assert.DoesNotContain("\"Cluster Cull Clear Main\"", scene);
        Assert.DoesNotContain("\"clear_main_single\"", scene);
        Assert.DoesNotContain("\"clear_main_phase1\"", scene);
        Assert.DoesNotContain("\"clear_main_phase2\"", scene);
        Assert.DoesNotContain("new BufferUploadBatch(", shade);
        Assert.Contains("\"Cluster Shade Bin Clear Prepare\"", shade);
        Assert.Contains("pass.SetPipeline(context.GetPipeline(_clearPreparePipeline));", shade);
        Assert.Contains("pass.Barrier(", shade);
        Assert.DoesNotContain("\"Clear ShadeBin Buffers\"", shade);
        Assert.DoesNotContain("\"Clear ShadeBin Buffers Source\"", shade);
        Assert.DoesNotContain("Clear Cluster Page Fault", traverse);
        Assert.Contains("builder.ReadWrite(buffers.PageFault, ResourceState.UnorderedAccess);", traverse);
        Assert.Contains("\"PageFaultBuffer\"", traverse);
        Assert.Contains("PageFaultBuffer.Store(0, 0u);", shader);
        Assert.Contains("\"CandidateCount\"", traverse);
        Assert.Contains("RawArgs(graph, \"CandidateArgs\", 16, ResourceState.CopyDestination, ResourceLifetime.Transient)", traverse);
        Assert.Contains("RawArgs(graph, \"IndirectDrawArgs\", 256, ResourceState.CopyDestination, ResourceLifetime.Transient)", traverse);
        Assert.Contains("\"Phase2CandidateCount\"", scene);
        Assert.DoesNotContain("BVHQueueA", traverse);
        Assert.DoesNotContain("BVHQueueB", traverse);
        Assert.DoesNotContain("BVHArgsA", traverse);
        Assert.DoesNotContain("BVHArgsB", traverse);
        Assert.DoesNotContain("UpdateArgs(", shader);
        Assert.DoesNotContain("InitQueue", shader);
        Assert.Contains("RawArgs(graph, \"Phase2CandidateArgs\", 16, ResourceState.CopyDestination, ResourceLifetime.Transient)", scene);
        Assert.Contains("RawArgs(graph, \"Phase2DrawArgs\", 256, ResourceState.CopyDestination, ResourceLifetime.Transient)", scene);
        Assert.Contains("InitialState = copy ? ResourceState.CopyDestination : ResourceState.UnorderedAccess", bin);
        Assert.Contains("ResourceLifetime.Transient", traverse);
        Assert.Contains("ResourceLifetime.Transient", scene);
        Assert.Contains("copy ? ResourceLifetime.Transient : ResourceLifetime.Pooled", bin);
        Assert.DoesNotContain("ReadOnlySpan<byte> initialData", bin);
    }

    [Fact]
    public void RasterBinReserveCounters_FollowComputeClearOwnership()
    {
        string raster = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "RasterBinPass.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("\"{prefix}RasterBinReserveCounters\",\n            2,\n            copy: false,\n            lifetime: ResourceLifetime.Transient", raster);
        Assert.Contains("\"{prefix}DeformBinReserveCounters\",\n            1,\n            copy: false,\n            lifetime: ResourceLifetime.Transient", raster);
        Assert.Contains("\"Cluster Binning Clear Prepare\"", raster);
        Assert.Contains("\"Cluster Raster+Deform Bin Clear Prepare\"", raster);
    }

    [Fact]
    public void RasterBinChains_MergeClearPrepareIntoComputePass()
    {
        string graphPass = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraphPass.cs"));
        string raster = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "RasterBinPass.cs"));

        Assert.Contains("void BufferBarrier(RenderGraphHandle handle, ResourceState before, ResourceState after);", graphPass);
        Assert.DoesNotContain("AddClearPrepare(graph", raster);
        Assert.DoesNotContain("AddCombinedPrepare(", raster);
        Assert.Contains("pass.BufferBarrier(", raster);
        Assert.Contains("ResourceState.UnorderedAccess,", raster);
        Assert.Contains("ResourceState.IndirectArgument);", raster);
    }

    [Fact]
    public void RenderGraph_UsesResourceLifetime()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph");
        string graph = File.ReadAllText(Path.Combine(root, "RenderGraph.cs"));
        string compile = File.ReadAllText(Path.Combine(root, "RenderGraph.Compile.cs"));
        string resources = File.ReadAllText(Path.Combine(root, "RenderGraph.Resources.cs"));

        Assert.Contains("public enum ResourceLifetime", graph);
        Assert.Contains("Pooled", graph);
        Assert.Contains("Transient", graph);
        Assert.Contains("public ResourceLifetime Lifetime = ResourceLifetime.Pooled;", graph);
        Assert.Contains("CreateBuffer(string name, BufferDesc desc, ResourceLifetime lifetime)", graph);
        Assert.Contains("values.Add((int)resource.Lifetime);", compile);
        Assert.Contains("resource.Lifetime is ResourceLifetime.Pooled or ResourceLifetime.Transient", resources);
    }

    [Fact]
    public void BufferUpload_UsesCopyBatch()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI");
        string upload = File.ReadAllText(Path.Combine(root, "BufferUploadPasses.cs"));
        string copy = File.ReadAllText(Path.Combine(root, "BufferCopyPasses.cs"));

        Assert.Contains("BufferCopyPasses.AddCopyBatch(graph, name, [.. copyRequests]);", upload);
        Assert.Contains("internal static void AddCopyBatch(", copy);
        Assert.DoesNotContain("var capturedRequests = requests.ToArray();", copy);
    }

    [Fact]
    public void RenderGraphCompile_UsesFlatReadySet()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "GraphScratch.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("SortedSet<int>", source);
        Assert.Contains("ReadyList", source);
    }

    [Fact]
    public void RenderGraphCompile_ReusesCompileWork()
    {
        string graphPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.cs");
        string compilePath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Compile.cs");
        string compilerPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraphCompiler.cs");
        string graph = File.ReadAllText(graphPath);
        string compile = File.ReadAllText(compilePath);
        string compiler = File.ReadAllText(compilerPath);

        Assert.Contains("private readonly RenderGraphCompiler _compiler;", graph);
        Assert.Contains("internal CompiledGraph? CompileWork;", compiler);
        Assert.Contains("UseCompile(", compile);
        Assert.Contains("UseShared(", graph);
        Assert.Contains("target.SharedTransitions = shared.SharedTransitions ?? shared.Transitions;", graph);
        Assert.Contains("target.SharedRequiredTransitions = shared.SharedRequiredTransitions ?? shared.RequiredTransitions;", graph);
        Assert.Contains("target.SharedResources = shared.SharedResources ?? shared.Resources;", graph);
        Assert.DoesNotContain("target.Transitions.AddRange(shared.Transitions);", graph);
        Assert.DoesNotContain("target.RequiredTransitions.AddRange(shared.RequiredTransitions);", graph);
        Assert.DoesNotContain("target.Resources.AddRange(shared.Resources);", graph);
        Assert.DoesNotContain("return result.Materialize()", compile);
        Assert.DoesNotContain("compile = _compileResult!.Materialize()", compile);
    }

    [Fact]
    public void RenderGraphCompile_UsesSingleFrameDeclarationPath()
    {
        string graphPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.cs");
        string compilePath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Compile.cs");
        string compilerPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraphCompiler.cs");
        string resourcesPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Resources.cs");
        string executePath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Execute.cs");
        string fullscreenPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "FullscreenPipeline.cs");
        string graph = File.ReadAllText(graphPath);
        string compile = File.ReadAllText(compilePath);
        string compiler = File.ReadAllText(compilerPath);
        string resources = File.ReadAllText(resourcesPath);
        string execute = File.ReadAllText(executePath);
        string fullscreen = File.ReadAllText(fullscreenPath);
        string compileOwner = compile + "\n" + compiler;
        string graphCore = graph + "\n" + compile + "\n" + resources + "\n" + execute;

        Assert.Contains("private sealed class GraphSchemaState", graph);
        Assert.Contains("private const int ResourceToken", graph);
        Assert.Contains("private const int PassToken", graph);
        Assert.Contains("private const int UseToken", graph);
        Assert.Contains("_schemaState.AddPass(passIndex, mode);", graph);
        Assert.Contains("SetupPass(passIndex, pass);", graph);
        Assert.Contains("private sealed class Pass(", graph);
        Assert.Contains("private readonly List<Pass> _passes = [];", graph);
        Assert.DoesNotContain("_passModes", graph);
        Assert.DoesNotContain("_commandPasses", graph);
        Assert.DoesNotContain("_computePasses", graph);
        Assert.DoesNotContain("_passSideEffects", graph);
        Assert.DoesNotContain("interface IPass", File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraphPass.cs")));
        Assert.Contains("_schemaState.AddUse(passIndex, use);", graph);
        Assert.Contains("_schemaState.SetResource(index, resource);", graph);
        Assert.DoesNotContain("_resourceLookup", graph);
        Assert.DoesNotContain("_resourceLookup", resources);
        Assert.DoesNotContain("already exists in this frame", resources);
        Assert.DoesNotContain("PublishState", graphCore);
        Assert.DoesNotContain("Publishes", graphCore);
        Assert.DoesNotContain("PublishStates", graphCore);
        Assert.DoesNotContain("record.Publish", graphCore);
        Assert.DoesNotContain("Dictionary<string", fullscreen);
        Assert.DoesNotContain("SetTextureResource(string", fullscreen);
        Assert.DoesNotContain("SetBufferResource(string", fullscreen);
        Assert.Contains("private ReflectedBinding[] _pixelBindings = [];", fullscreen);
        Assert.Contains("public void SetTextureResource(int resourceIndex, TextureViewHandle view)", fullscreen);
        Assert.Contains("public void SetBufferResource(int resourceIndex, BufferViewHandle view)", fullscreen);
        Assert.Contains("private struct ResourceSchema", graph);
        Assert.Contains("private readonly List<ResourceSchema> _resources = [];", graph);
        Assert.Contains("private readonly record struct FinalStateSchema", graph);
        Assert.Contains("_sortedOutputs.AddRange(_textureExports);", graph);
        Assert.Contains("_sortedOutputs.AddRange(_bufferExports);", graph);
        Assert.Contains("_sortedFinalStates.Sort", graph);
        Assert.DoesNotContain("MarkOutput(", graph);
        Assert.DoesNotContain("_markedOutputs", graph);
        Assert.Contains("ReplaceResource(schema.StartIndex, schema.Length, _resourceScratch);", graph);
        Assert.Contains("ShiftSchemaIndexes(startIndex, delta);", graph);
        Assert.Contains("bool resourceChanged = EnsureState(resource, entryState);", graph);
        Assert.Contains("resourceChanged |= EnsureState(resource, exitState);", graph);
        Assert.Contains("bool allowPresent = resource.Imported && resource.Kind == ResourceKind.Texture;", graph);
        Assert.Contains("if (EnsureState(resource, finalState, allowPresent))", graph);
        Assert.Contains("_schemaState.SetResource(resourceIndex, resource);", graph);
        Assert.Contains("private static bool EnsureState(Resource resource, ResourceState state)", compile);
        Assert.Contains("return true;", compile);
        Assert.Contains("private bool _declarationScratchDirty;", graph);
        Assert.Contains("public void BeginFrame(Action<RenderGraph> record)", graph);
        Assert.Contains("public void BeginFrame<TState>(TState state, Action<RenderGraph, TState> record)", graph);
        Assert.Contains("private void BeginFrameCore<TState>(Action<RenderGraph, TState> record, TState state)", graph);
        Assert.Contains("BeginFrame();", graph);
        Assert.Contains("Profiler.BeginScope(\"RenderGraph.BeginFrame.Record\")", graph);
        Assert.Contains("record(this, state);", graph);
        Assert.Contains("foreach (IRenderFeature feature in _features)", graph);
        Assert.Contains("feature.AddPasses(this);", graph);
        Assert.Contains("_featuresAppliedThisFrame = true;", graph);
        Assert.Contains("private void EnsureDeclarationScratch()", graph);
        Assert.Contains("private void ResetDeclarationScratch()", graph);
        Assert.Contains("_declarationScratchDirty = true;", graph);
        Assert.Contains("EnsureDeclarationScratch();", graph);
        Assert.Contains("_graph._schema = _graph._schemaState.Finish(_graph._passes.Count, _graph._resources.Count, asyncCompute, asyncCopy);", compiler);
        Assert.Contains("_graph.Declarations().EnsureShape(_graph._passes.Count, _graph._resources.Count);", compiler);
        Assert.Contains("GraphSchema schema = CurrentSchema();", compileOwner);
        Assert.Contains("GraphSchema schema = CurrentSchema();", resources);
        Assert.Contains("compile.UseDeclarations(_graph.Declarations()", compiler);
        Assert.True(
            compiler.IndexOf("GraphCache.TryGet(schema", StringComparison.Ordinal)
            < compiler.IndexOf("compile.UseDeclarations(_graph.Declarations()", StringComparison.Ordinal));
        Assert.Contains("CompileSchema.Equals(CurrentSchema())", compiler);
        Assert.Contains("=> SetCompile(compile, CurrentSchema(), asyncCompute, asyncCopy);", compiler);
        Assert.Contains("internal GraphSchema CurrentSchema()", compiler);
        Assert.Contains("Profiler.GraphCompileRecent();", compiler);
        Assert.Contains("internal Task CompileAsync(", compileOwner);
        Assert.Contains("CompileInBackground(", compileOwner);
        Assert.Contains("RenderGraphSnapshot snapshot = CaptureSnapshot(schema);", compiler);
        Assert.Contains("RenderGraphSnapshot CaptureSnapshot(GraphSchema schema)", compiler);
        Assert.Contains("CompiledGraph result = snapshot.Compile(asyncCompute, asyncCopy);", compiler);
        Assert.DoesNotContain("SetCompile(CompileCore(asyncCompute, asyncCopy), asyncCompute, asyncCopy);", compileOwner);
        Assert.Contains("ThrowIfBackgroundCompile(", compileOwner);
        Assert.Contains("Interlocked.Increment(ref _graph._backgroundCompileActive);", compiler);
        Assert.Contains("Interlocked.Decrement(ref _graph._backgroundCompileActive);", compiler);
        Assert.Contains("Volatile.Read(ref _graph._backgroundCompileActive) > 0", compiler);
        Assert.Contains("requires render features to be recorded", compiler);
        Assert.DoesNotContain("feature.AddPasses(this);", compiler);
        Assert.Contains("WaitForBackgroundCompile();", compileOwner);
        Assert.Contains("WaitForBackgroundCompile();", graph);
        Assert.Contains("Volatile.Write(ref _graph._backgroundCompilePending, 1);", compiler);
        Assert.Contains("internal readonly System.Threading.Lock Gate = new();", compiler);
        Assert.Contains("internal Task<CompiledGraph>? CompileTask;", compiler);
        Assert.Contains("internal sealed class RenderGraphSnapshot", graph);
        Assert.Contains("internal readonly record struct PassSnapshot", graph);
        Assert.Contains("internal readonly record struct ResourceSnapshot", graph);
        Assert.Contains("private int _backgroundCompileActive;", graph);
        Assert.Contains("private int _backgroundCompilePending;", graph);
        Assert.Contains("_featuresAppliedThisFrame = true;", graph);
        Assert.Contains("private readonly Stack<Resource> _resourcePool = [];", graph);
        Assert.Contains("RecycleResources();", graph);
        Assert.Contains("private Resource TakeResource(string name, ResourceKind kind)", resources);
        Assert.Contains("resource.Reset(name, kind);", resources);
        Assert.DoesNotContain("_declarations?.EnsureShape", resources);
        Assert.DoesNotContain("_schemaValues", graph);
        Assert.DoesNotContain("_schemaValues", compile);
        Assert.DoesNotContain("CollectionsMarshal.AsSpan(_schemaValues)", resources);
        Assert.DoesNotContain("_resources.Add(new Resource", resources);
        Assert.DoesNotContain("FillSchema(", compile);
        Assert.DoesNotContain("pass.Setup(builder)", compile);
        Assert.DoesNotContain("new RenderGraphBuilder(this, passIndex)", compile);
        Assert.DoesNotContain("_compileRevision", graph);
        Assert.DoesNotContain("_compileRevision", compile);
        Assert.DoesNotContain("RenderGraphInput", graph);
        Assert.DoesNotContain("DeclarationKey", graph);
        Assert.DoesNotContain("DeclarationCache", graph);
        Assert.DoesNotContain("_declarationCache", graph);
        Assert.DoesNotContain("_declarationKey", graph);
        Assert.DoesNotContain("_activeInput", graph);
        Assert.DoesNotContain("DeclarationSnapshot", graph);
        Assert.DoesNotContain("DeclarationReplay", graph);
        Assert.DoesNotContain("_activeDeclaration", graph);
        Assert.DoesNotContain("_frameDeclaration", graph);
        Assert.DoesNotContain("_declarationReplay", graph);
        Assert.DoesNotContain("_declarationResourceCursor", graph);
        Assert.DoesNotContain("_declarationPassCursor", graph);
        Assert.DoesNotContain("TryReplayFrame", graph);
        Assert.DoesNotContain("RecordFreshFrame", graph);
        Assert.DoesNotContain("replayDeclaration", graph);
        Assert.DoesNotContain("CaptureDeclaration", compile);
        Assert.DoesNotContain("_frameDeclaration", compile);
        Assert.DoesNotContain("ReplayResource", resources);
        Assert.DoesNotContain("_activeDeclaration", resources);
        Assert.DoesNotContain("_declarationReplay", resources);
        Assert.False(File.Exists(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraphInput.cs")));
    }

    [Fact]
    public void Runtime_RecordsRenderGraphCallback()
    {
        string runtime = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Runtime",
            "RuntimeApp.cs"));
        string frameSurfaces = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Frame",
            "FrameResources.cs"));
        string extractor = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Systems",
            "RenderWorldExtractor.cs"));

        Assert.Contains("private readonly record struct RenderFrameData", runtime);
        Assert.Contains("private static void RecordRenderFrame(RenderGraph graph, RenderFrameData data)", runtime);
        Assert.Contains("var graphQueues = startupOptions.AsyncCompute", runtime);
        Assert.Contains("? new GraphQueues(device, queue)", runtime);
        Assert.Contains(": new GraphQueues(device, queue, compute: null, copy: device.Features.CopyQueue ? device.GetQueue(QueueType.Copy) : null);", runtime);
        Assert.Contains("renderGraph.BeginFrame(frameData, static (graph, data) => RecordRenderFrame(graph, data));", runtime);
        Assert.Contains("renderGraph.Compile(graphQueues);", runtime);
        Assert.Contains("renderGraph.Execute(graphQueues, executionSwapchain, context.PresentSyncInterval);", runtime);
        Assert.Contains("clusterPipeline.PrepareFrame(renderWorld, histories, cameraHistory, temporalState);", runtime);
        Assert.Contains("new FrameData(", runtime);
        Assert.Contains("new ViewData(backBufferDesc.Width, backBufferDesc.Height)", runtime);
        Assert.Contains("SceneTextures sceneTextures = FrameResources.CreateSceneTextures(", runtime);
        Assert.Contains("FrameOutputs outputs = data.ClusterPipeline.AddPasses(", runtime);
        Assert.Contains("data.PostTonemap.AddTo(graph, outputs.PostSceneColor, sceneTextures.OutputColor);", runtime);
        Assert.Contains("public static SceneTextures CreateSceneTextures(", frameSurfaces);
        Assert.Contains("public uint ShapeVersion => _shapeVersion;", extractor);
        Assert.Contains("private void TouchShape()", extractor);
        Assert.DoesNotContain("private static ulong RenderGraphVersion(RenderFrameData data)", runtime);
        Assert.DoesNotContain("RenderGraphInput.Create(", runtime);
        Assert.DoesNotContain("RenderGraphVersion(frameData)", runtime);
        Assert.DoesNotContain("renderGraph.BeginFrame(graphInput);", runtime);
        Assert.DoesNotContain("renderGraph.BeginFrame();", runtime);
        Assert.DoesNotContain("RecordRenderFrame(renderGraph, frameData);", runtime);
        Assert.DoesNotContain("renderGraph.Execute(device, new GraphQueues(device, queue)", runtime);
        Assert.DoesNotContain("data.Histories.BeginFrame(graph, data.Device);", runtime);
        Assert.DoesNotContain("FrameSurfaceGraph.AddMainPasses(", runtime);
        Assert.DoesNotContain("graph.MarkOutput(", runtime);
        Assert.DoesNotContain("                frameIndex,\r\n                frameData,", runtime);
        Assert.DoesNotContain("                frameIndex,\n                frameData,", runtime);
    }

    [Fact]
    public void Editor_RecordsRenderGraphCallback()
    {
        string editor = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Editor",
            "EditorApp.cs"));

        Assert.Contains("private readonly record struct RenderFrameData", editor);
        Assert.Contains("private static void RecordRenderFrame(RenderGraph graph, RenderFrameData data)", editor);
        Assert.Contains("var graphQueues = new GraphQueues(device, queue);", editor);
        Assert.Contains("_renderGraph.BeginFrame(frameData, static (graph, data) => RecordRenderFrame(graph, data));", editor);
        Assert.Contains("_renderGraph.Compile(graphQueues);", editor);
        Assert.Contains("_renderGraph.Execute(graphQueues, swapchain, _renderContext.PresentSyncInterval);", editor);
        Assert.Contains("_clusterPipeline.PrepareFrame(_renderWorld, _histories, _cameraHistory, _temporalState);", editor);
        Assert.Contains("new FrameData(", editor);
        Assert.Contains("new ViewData(backBufferDesc.Width, backBufferDesc.Height)", editor);
        Assert.Contains("SceneTextures sceneTextures = FrameResources.CreateSceneTextures(", editor);
        Assert.Contains("FrameOutputs outputs = clusterPipeline.AddPasses(", editor);
        Assert.Contains("postTonemap.AddTo(graph, outputs.PostSceneColor, sceneTextures.OutputColor);", editor);
        Assert.DoesNotContain("_renderGraph!.BeginFrame();", editor);
        Assert.DoesNotContain("_histories.BeginFrame(_renderGraph, device);", editor);
        Assert.DoesNotContain("FrameSurfaceGraph.AddMainPasses(", editor);
        Assert.DoesNotContain("FrameSurfaceGraph.CreateMainSurfaces(", editor);
        Assert.DoesNotContain("graph.MarkOutput(", editor);
    }

    [Fact]
    public void ClusterPipeline_SplitsInstanceGpuPrepareAndRecord()
    {
        string pipeline = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "ClusterPipeline.Runtime.cs"));
        string instanceGpu = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Systems",
            "InstanceGpu.cs"));

        Assert.Contains("_instanceGpu.PrepareFrame(renderWorld, _materials.Headers);", pipeline);
        Assert.Contains("instances = _instanceGpu.RecordFrame(graph, renderWorld, _materials.Headers, _uploadCopyRequests);", pipeline);
        Assert.DoesNotContain("instances = _instanceGpu.Add(graph, renderWorld, _materials.Headers, _uploadCopyRequests);", pipeline);

        Assert.Contains("public void PrepareFrame(", instanceGpu);
        Assert.Contains("public InstanceFrame RecordFrame(", instanceGpu);
        Assert.Contains("public InstanceFrame Add(", instanceGpu);
    }

    [Fact]
    public void GraphCache_AvoidsLinkedNodes()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "GraphCache.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("LinkedList", source);
        Assert.DoesNotContain("LinkedListNode", source);
        Assert.DoesNotContain("IndexOf(", source);
        Assert.DoesNotContain("Insert(0", source);
        Assert.DoesNotContain("RemoveAt(", source);
        Assert.Contains("private readonly List<Entry> _entries = new(Capacity);", source);
        Assert.Contains("private const int LocalSize = 256;", source);
        Assert.Contains("private readonly Entry?[] _local = new Entry?[LocalSize];", source);
        Assert.Contains("public bool TryGet(GraphSchema schema", source);
        Assert.Contains("public void Store(GraphSchema schema", source);
        Assert.Contains("local.Schema.Equals(schema)", source);
        Assert.Contains("_local[Slot(hash)] = entry;", source);
        Assert.Contains("_local[Slot(hash)] = added;", source);
        Assert.Contains("ClearLocal(old);", source);
        Assert.Contains("private int _next;", source);
        Assert.Contains("_entries[_next] = added;", source);

        string graph = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.cs"));
        Assert.Contains("private const int Size = 256;", graph);
    }

    [Fact]
    public void Profiler_IsOnlyProfilerCounterBoundary()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string diagnostics = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "Profiler.cs");
        string[] forbidden =
        [
            "Profiler.Report(",
            "GraphStats.",
            "QueueStats.",
            "BindingStats.",
            "BarrierStats(",
            "PipelineStats.",
        ];

        string[] scanRoots =
        [
            Path.Combine(root, "SomeEngine.Render"),
            Path.Combine(root, "SomeEngine.Rhi"),
            Path.Combine(root, "SomeEngine.Rhi.D3D12"),
        ];
        foreach (string scanRoot in scanRoots)
        {
            foreach (string file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/obj/", StringComparison.Ordinal)
                    || normalized.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                foreach (string token in forbidden)
                    Assert.DoesNotContain(token, source);
            }
        }

        string diagnosticsSource = File.ReadAllText(diagnostics);
        Assert.Contains("public static class Profiler", diagnosticsSource);
        Assert.Contains("internal static void Report(", diagnosticsSource);
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "DeviceDiagnostics.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "RenderDiagnostics.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Render", "Diagnostics", "RenderDiagnostics.cs")));
    }

    [Fact]
    public void RhiBackends_UseProfilerForProfilerCounters()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string[] roots =
        [
            Path.Combine(root, "SomeEngine.Rhi"),
            Path.Combine(root, "SomeEngine.Rhi.D3D12"),
        ];
        string[] forbidden =
        [
            "Profiler.Report(",
            "BarrierStats(",
            "DescriptorStats(",
        ];

        foreach (string scanRoot in roots)
        {
            foreach (string file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/obj/", StringComparison.Ordinal)
                    || normalized.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                foreach (string token in forbidden)
                    Assert.DoesNotContain(token, source);
            }
        }

        string diagnostics = File.ReadAllText(Path.Combine(
            root,
            "SomeEngine.Core",
            "Diagnostics",
            "Profiler.cs"));
        Assert.Contains("public static class Profiler", diagnostics);
        Assert.Contains("internal static void Report(", diagnostics);
        Assert.DoesNotContain("DeviceDiagnostics", diagnostics);
        Assert.DoesNotContain("RenderDiagnostics", diagnostics);
    }

    [Fact]
    public void RenderGraphBarriers_ReuseStateArrays()
    {
        string barrierPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Barriers.cs");
        string scratchPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "GraphScratch.cs");
        string executePath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Execute.cs");
        string dumpPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Dump.cs");
        string graphPath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.cs");
        string compilePath = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Compile.cs");
        string barriers = File.ReadAllText(barrierPath);
        string scratch = File.ReadAllText(scratchPath);
        string execute = File.ReadAllText(executePath);
        string dump = File.ReadAllText(dumpPath);
        string graph = File.ReadAllText(graphPath);
        string compile = File.ReadAllText(compilePath);
        string resources = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Resources.cs"));

        Assert.DoesNotContain("new ResourceState[_resources.Count]", barriers);
        Assert.DoesNotContain("new RenderGraphAccess[_resources.Count]", barriers);
        Assert.Contains("_scratch.Barriers", barriers);
        Assert.Contains("BarrierState Barriers", scratch);
        Assert.DoesNotContain("WholeCheckLookup", graph);
        Assert.Contains("private int[] _wholeMarks = [];", graph);
        Assert.Contains("public readonly List<int> CheckResources = [];", graph);
        Assert.Contains("AddCheck(transition.ResourceIndex);", graph);
        Assert.DoesNotContain("TrimTransitions", graph);
        Assert.Contains("StartCheck(resources.Length)", graph);
        Assert.Contains("out bool includeAliases", barriers);
        Assert.Contains("includeChecks = NeedsChecks(compile, states, transitions);", barriers);
        Assert.Contains("includeAliases = NeedsAlias(compile, states, transitions);", barriers);
        Assert.Contains("return transitions.HasRequired || includeAliases || includeChecks;", barriers);
        Assert.Contains("bool includeAliases,", barriers);
        Assert.Contains("if (includeAliases)", barriers);
        Assert.Contains("if (includeChecks)", barriers);
        Assert.Contains("if (!transitions.IsEmpty", barriers);
        Assert.DoesNotContain("if (NeedsBarriers(compile, _resourceStates, transitions", barriers);
        Assert.Contains("TrackExitStates(_resourceStates, compile, passIndex);", barriers);
        Assert.DoesNotContain("private bool CanSkipBarriers", barriers);
        Assert.Contains("private bool NeedsChecks(CompiledGraph compile, ResourceStateTracker states, TransitionBatch transitions)", barriers);
        Assert.Contains("private bool CanSkipChecks", barriers);
        Assert.Contains("transitions.CheckResources.Count", barriers);
        Assert.Contains("if (StateSatisfied(resource, states, transition))", barriers);
        Assert.Contains("states.Trusted[transition.ResourceIndex] = !record.Aliased", barriers);
        Assert.Contains("&& !resource.Imported", barriers);
        Assert.DoesNotContain("if (!record.Reusable)", barriers);
        Assert.Contains("if (range.CoversAll(desc))", barriers);
        Assert.Contains("if (!transition.Range.CoversAll(desc))", barriers);
        Assert.DoesNotContain("ActualRange(desc, transition.Range).CoversAll(desc)", barriers);
        Assert.Contains("transitions.RequiredTransitionSource", dump);
        Assert.DoesNotContain("if (!transition.Required)", dump);
        Assert.Contains("compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty", compile);
        Assert.DoesNotContain("CanSkipBarriers(compile, compile.FinalTransitions)", execute);
        Assert.Contains("private void BeginCommandBarriers(CompiledGraph compile)", barriers);
        Assert.Contains("BeginCommandBarriers(compile);", execute);
        Assert.Contains("private void FinalizeResources(IDevice device, CompiledGraph compile)", resources);
        Assert.Contains("ResolveRootResources(device, compile);", resources);
        Assert.Contains("private void CreatePassBarriers(IDevice device, CompiledGraph compile)", barriers);
        Assert.Contains("FinalizeResources(device, compile);", execute);
        Assert.Contains("CreatePassBarriers(device, compile);", execute);
        Assert.Contains("using (Profiler.BeginScope(\"RenderGraph.FinalizeResources\"))", execute);
        Assert.Contains("using (Profiler.BeginScope(\"RenderGraph.CreatePassBarriers\"))", execute);
        Assert.Contains("PassEpilogueState[] epilogue = compile.PassEpilogues[passIndex];", barriers);
        Assert.DoesNotContain("var uses = compile.Uses[passIndex];", barriers[
            barriers.IndexOf("private void TrackExitStates(", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("using (Profiler.BeginScope(\"RenderGraph.PrepareBarriers\"))", execute);
        Assert.DoesNotContain("ResolvePassResources(graphContext.Device, compile, passIndex);", execute);
        Assert.DoesNotContain("ResolvePassResources(execution.Device, compile, passIndex);", execute);
        Assert.DoesNotContain("PreparePassBarriers(graphContext.Device, compile, passIndex);", execute);
        Assert.DoesNotContain("PreparePassBarriers(execution.Device, compile, passIndex);", execute);
        Assert.DoesNotContain("RenderGraph Resolve", execute);
        Assert.DoesNotContain("ResolveRootResources(device, compile);", barriers);
        Assert.DoesNotContain("if (!transitions.IsEmpty)", execute);
        Assert.DoesNotContain("RefreshBarrierChecks(compile);", execute);
        Assert.DoesNotContain("_resourceStates.Refresh(resourceIndex, _resources[resourceIndex]);", resources);
        Assert.Contains("Parallel.For(", execute);
        Assert.Contains("CanRecordBatchesInParallel", execute);
        Assert.Contains("device.Features.ParallelCommandRecording", execute);
        Assert.Contains("timestamps == null", execute);
        Assert.Contains("compile.QueueBatches.Count > 1", execute);
        Assert.Contains("CommitResourceStates(compile);", execute);
        Assert.Contains("CommandBarriers barriers = _passBarriers[passIndex];", execute);
        Assert.Contains("EmitBarriers(list, barriers);", execute);
        Assert.Contains("EmitBarriers(computePass, barriers);", execute);
        Assert.Contains("EmitBarriers(list, _finalBarriers);", execute);
        Assert.DoesNotContain("NeedsBarriers(", execute);
        Assert.DoesNotContain("ApplyBarriers(", execute);
        Assert.DoesNotContain("ApplyExitStates(", execute);
        Assert.DoesNotContain("_executePassIndex", execute);
        int finalizeIndex = execute.IndexOf("FinalizeResources(device, compile);", StringComparison.Ordinal);
        int beginIndex = execute.IndexOf("BeginCommandBarriers(compile);", StringComparison.Ordinal);
        int createIndex = execute.IndexOf("CreatePassBarriers(device, compile);", StringComparison.Ordinal);
        Assert.True(finalizeIndex >= 0 && beginIndex >= 0 && createIndex >= 0);
        Assert.True(finalizeIndex < beginIndex && beginIndex < createIndex);
        Assert.Contains("[ThreadStatic]", graph);
        Assert.Contains("private static RenderGraph? threadExecuteGraph;", graph);
        Assert.Contains("private static int threadExecutePassIndex;", graph);
        Assert.Contains("private readonly List<int> _dirtyBarrierSlots = [];", graph);
        Assert.Contains("_dirtyBarrierSlots.Add(passIndex);", barriers);
        Assert.Contains("_dirtyBarrierSlots.Clear();", barriers);
        Assert.DoesNotContain("for (int passIndex = 0; passIndex < passCount; passIndex++)", barriers);
    }

    [Fact]
    public void RhiCommandRecording_UsesThreadSafeSharedStores()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string handleStore = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "HandleStore.cs"));
        string activeHandles = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "ActiveHandles.cs"));
        string descriptors = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Descriptors.cs"));
        string nullDevice = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullDevice.cs"));
        string d3d12Device = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12Device.cs"));
        string d3d12Queue = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12Queue.cs"));
        string d3d12Descriptors = File.ReadAllText(Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12DescriptorAllocator.cs"));

        Assert.Contains("private readonly System.Threading.Lock _gate = new();", handleStore);
        Assert.Contains("private static readonly System.Threading.Lock s_idGate = new();", handleStore);
        Assert.Contains("lock (_gate)", handleStore);
        Assert.Contains("lock (s_idGate)", handleStore);
        Assert.Contains("List<TValue> values = [];", handleStore);
        Assert.Contains("public bool ParallelCommandRecording { get; init; }", descriptors);
        Assert.Contains("ParallelCommandRecording = true,", nullDevice);
        Assert.Contains("ParallelCommandRecording = false,", d3d12Device);
        Assert.Contains("lock (active)", activeHandles);
        Assert.Contains("public static bool Contains<THandle>", activeHandles);
        Assert.Contains("ActiveHandles.Contains(_activeBuffers", nullDevice);
        Assert.Contains("ActiveHandles.Contains(_activeListGate, _activeListBuffers", d3d12Device);

        Assert.Contains("private readonly System.Threading.Lock _commandListGate = new();", d3d12Queue);
        Assert.Contains("lock (_commandListGate)", d3d12Queue);

        Assert.Contains("private readonly object _gate = new();", d3d12Descriptors);
        Assert.Contains("lock (_gate)", d3d12Descriptors);
        Assert.Contains("AllocateTransient", d3d12Descriptors);
    }

    [Fact]
    public void RenderGraphDump_ShowsQueues()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Dump.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("public string DumpText(bool asyncCompute, bool asyncCopy)", source);
        Assert.Contains("CompileGraph(asyncCompute, asyncCopy)", source);
        Assert.Contains("Queue Links", source);
        Assert.Contains("AppendLinkDump", source);
        Assert.Contains("compile.QueueLinks[linkIndex]", source);
    }

    [Fact]
    public void RenderGraphFinal_UsesWorkQueue()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Execute.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("private void SubmitFinal", StringComparison.Ordinal);
        int end = source.IndexOf("private void SubmitFrame", start, StringComparison.Ordinal);
        string submit = source[start..end];

        Assert.Contains("QueueType finalQueue = compile.FrameQueue;", source);
        Assert.Contains("RecordFinal(device, timestamps, finalQueue", source);
        Assert.Contains("SubmitFinal(device, queues, compile, finalBuffer, queuePoints, finalQueue", source);
        Assert.Contains("compile.FrameWaits", source);
        Assert.Contains("BuildFrameSync(compile)", File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Compile.cs")));
        Assert.Contains("QueueType = queue,", source);
        Assert.Contains("timestamps?.Resolve(list, queue, timingStart);", source);
        Assert.Contains("AddFrameWaits(points, waits, queue, compile.FrameWaits)", submit);
        Assert.Contains("queues.Get(queue).Submit(", submit);
        Assert.DoesNotContain("queues.Graphics.Submit", submit);
        Assert.DoesNotContain("PrepareFinalBarriers(device, compile);", source);
    }

    [Fact]
    public void RenderGraphSignal_UsesWorkQueue()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Graph",
            "RenderGraph.Execute.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("private void SubmitFrame", StringComparison.Ordinal);
        int end = source.IndexOf("private int AddBatchWaits", start, StringComparison.Ordinal);
        string submit = source[start..end];

        Assert.Contains("SubmitFrame(device, queues, compile, queuePoints", source);
        Assert.Contains("compile.QueueBatches[^1].Queue", source);
        Assert.Contains("QueueType queue = compile.FrameQueue;", submit);
        Assert.Contains("AddFrameWaits(points, waits, queue, compile.FrameWaits)", submit);
        Assert.Contains("queues.Get(queue).Submit(", submit);
        Assert.DoesNotContain("queues.Graphics.Submit", submit);
    }

    [Fact]
    public void RenderContext_RequiresPipelineScope()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "RenderContext.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("WarmupPipelines(int budget)", source);
        Assert.DoesNotContain("ProcessPipelines(int budget)", source);
        Assert.DoesNotContain("ProcessPipelines(ReadOnlySpan<PipelineTicket> tickets, int budget)", source);
        Assert.DoesNotContain("public PipelineWarmup WaitRequired()", source);
        Assert.DoesNotContain("InspectPipelines()", source);
        Assert.Contains("WarmupPipelines(ReadOnlySpan<PipelineTicket> tickets, int budget)", source);
        Assert.Contains("WarmupSources(int budget)", source);
    }

    [Fact]
    public void PipelineCache_RequiresPipelineScope()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "PipelineCache.cs");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("internal sealed class PipelineCache", StringComparison.Ordinal);
        int end = source.IndexOf("public sealed record ComputeState", StringComparison.Ordinal);
        string cache = source[start..end];

        Assert.DoesNotContain("WarmupAll(", cache);
        Assert.DoesNotContain("InspectAll(", cache);
        Assert.DoesNotContain("ActiveTickets(", cache);
        Assert.DoesNotContain("public int Process(int budget)", cache);
        Assert.DoesNotContain("public int Process(ReadOnlySpan<PipelineTicket> tickets, int budget)", cache);
        Assert.DoesNotContain("return WarmupTickets(tickets, budget);", cache);
        Assert.DoesNotContain("public PipelineWarmup WaitRequired()", cache);
        Assert.Contains("WaitRequired(ReadOnlySpan<PipelineTicket> tickets)", cache);
        Assert.Contains("ProcessQueue()", cache);
        Assert.DoesNotContain("DrainQueue(int budget)", cache);
        Assert.Contains("bool BudgetLimited", source);
        Assert.Contains("private static bool BudgetLimited", cache);
        Assert.Contains("BudgetLimited(budget, processed, queued)", cache);
    }

    [Fact]
    public void PipelineSources_RefreshesRecordsIndependently()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "PipelineSources.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("Dictionary<IPipelineSource, SourceRecord>", source);
        Assert.Contains("private void RefreshRecords()", source);
        Assert.Contains("private PipelineTicket[] CaptureTickets()", source);
        Assert.Contains("private PipelineLease? Create(IPipelineSource source)", source);
        Assert.Contains("private sealed class SourceRecord", source);
        Assert.Contains("record.Lease?.Tickets.Count", source);
        Assert.Contains("old?.Dispose();", source);
        Assert.DoesNotContain("SourceStamp", source);
        Assert.DoesNotContain("_leaseStamp", source);
        Assert.DoesNotContain("CopySources()", source);
        Assert.DoesNotContain("ReadOnlySpan<IPipelineSource> sources", source);
    }

    [Fact]
    public void PipelineKeyWriter_UsesPooledBytes()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "PipelineCache.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("MemoryStream", source);
        Assert.DoesNotContain("BinaryWriter", source);
        Assert.Contains("ArrayPool<byte>.Shared.Rent", source);
        Assert.Contains("BinaryPrimitives.WriteInt32LittleEndian", source);
        Assert.Contains("PipelineStateWriter : IDisposable", source);
        Assert.Contains("using var builder = new PipelineStateWriter", source);
    }

    [Fact]
    public void ProfileStats_UseCoreReports()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string cachePath = Path.Combine(root, "SomeEngine.Render", "RHI", "PipelineCache.cs");
        string pipelineTracePath = Path.Combine(root, "SomeEngine.Render", "RHI", "PipelineTrace.cs");
        string graphPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.cs");
        string tracePath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraphTrace.cs");
        string compilePath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.Compile.cs");
        string compilerPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraphCompiler.cs");
        string resourcesPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.Resources.cs");
        string executePath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.Execute.cs");
        string barriersPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.Barriers.cs");
        string timingPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.Timing.cs");
        string profilerPath = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "Profiler.cs");
        string typesPath = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "ProfilerTypes.cs");
        string tracyPath = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "TracySink.cs");
        string nativeTracyPath = Path.Combine(root, "SomeEngine.Tracy.Native", "SomeEngineTracyBridge.cpp");
        string renderDiagnosticsPath = Path.Combine(root, "SomeEngine.Core", "Diagnostics", "Profiler.cs");
        string d3d12CommandsPath = Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12CommandList.cs");
        string nullCommandsPath = Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullCommandList.cs");
        string rhiTracePath = Path.Combine(root, "SomeEngine.Rhi", "Diagnostics", "RhiTrace.cs");
        string deviceCountsPath = Path.Combine(root, "SomeEngine.Rhi", "Diagnostics", "DeviceCounts.cs");
        string d3d12CountsPath = Path.Combine(root, "SomeEngine.Rhi.D3D12", "D3D12Device.Diagnostics.cs");
        string nullCountsPath = Path.Combine(root, "SomeEngine.Rhi", "Backends", "Null", "NullDevice.Diagnostics.cs");

        string cache = File.ReadAllText(cachePath);
        string graph = File.ReadAllText(graphPath);
        string compile = File.ReadAllText(compilePath);
        string compiler = File.ReadAllText(compilerPath);
        string resources = File.ReadAllText(resourcesPath);
        string execute = File.ReadAllText(executePath);
        string barriers = File.ReadAllText(barriersPath);
        string timing = File.ReadAllText(timingPath);
        string profiler = File.ReadAllText(profilerPath);
        string types = File.ReadAllText(typesPath);
        string tracy = File.ReadAllText(tracyPath);
        string nativeTracy = File.ReadAllText(nativeTracyPath);
        string renderDiagnostics = File.ReadAllText(renderDiagnosticsPath);
        string d3d12Commands = File.ReadAllText(d3d12CommandsPath);
        string nullCommands = File.ReadAllText(nullCommandsPath);
        string d3d12Counts = File.ReadAllText(d3d12CountsPath);
        string nullCounts = File.ReadAllText(nullCountsPath);

        Assert.False(File.Exists(pipelineTracePath));
        Assert.False(File.Exists(tracePath));
        Assert.False(File.Exists(rhiTracePath));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "ManagedProfileSink.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "ProfileSinkGroup.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "DeviceDiagnostics.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Core", "Diagnostics", "RenderDiagnostics.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SomeEngine.Render", "Diagnostics", "RenderDiagnostics.cs")));

        Assert.DoesNotContain("Profiler.Bindings", resources);
        Assert.DoesNotContain("Profiler.Pipelines", resources);
        Assert.DoesNotContain("PipelineCounts", cache);
        Assert.DoesNotContain("PipelineCounters", cache);
        Assert.DoesNotContain("PipelineCount", cache);
        Assert.DoesNotContain("TakeStats()", cache);
        Assert.DoesNotContain("_counts.Add", cache);
        Assert.Contains("private static void ReportStatus(PipelineStatus status, PipelineNeed need)", cache);
        Assert.Contains("Profiler.PipelineStatus(StatusName(status), NeedName(need));", cache);
        Assert.Contains("Profiler.PipelineIssue(", cache);
        Assert.Contains("issue.Source", cache);
        Assert.Contains("issue.Site", cache);
        Assert.Contains("issue.Owner", cache);
        Assert.Contains("Profiler.PipelineReady();", cache);
        Assert.DoesNotContain("PipelineStats.", cache);
        Assert.Contains("PipelineStats.TooLate", renderDiagnostics);
        Assert.Contains("PipelineStats.Untracked", renderDiagnostics);
        Assert.Contains("private static string Use(string need)", renderDiagnostics);
        Assert.DoesNotContain("StatusStats", cache);
        Assert.DoesNotContain("InvalidStats", cache);
        Assert.DoesNotContain("default(PipelineStats)", cache);
        Assert.DoesNotContain("_bindingCounts.Add", resources);
        Assert.Contains("Profiler.BindingLocalHit();", resources);
        Assert.DoesNotContain("_trace.", resources);
        Assert.DoesNotContain("FlushCounts()", execute);
        Assert.DoesNotContain("TakeStats()", execute);
        Assert.DoesNotContain("private static void ReportBinding(BindingCount count)", graph);
        Assert.DoesNotContain("RenderGraphTrace", graph);
        Assert.DoesNotContain("private static void ReportGraph(GraphCount count)", graph);
        Assert.Contains("out GraphHit hit", compiler);
        Assert.Contains("Profiler.GraphCompileHit(RenderGraph.ResultName(hit));", compiler);
        Assert.Contains("\"Recent\"", renderDiagnostics);
        Assert.Contains("\"Local\"", renderDiagnostics);
        Assert.Contains("\"Shared\"", renderDiagnostics);
        Assert.Contains("Profiler.GraphCompileMiss();", compiler);
        Assert.Contains("Profiler.GraphAliasHit();", resources);
        Assert.Contains("Profiler.GraphAliasMiss();", resources);
        Assert.Contains("Profiler is only an instrumentation bridge", profiler);
        Assert.DoesNotContain("EnableManagedProfiler", types);
        Assert.DoesNotContain("ManagedProfilerActive", types);
        Assert.DoesNotContain("EnableDetailedCounters", types);
        Assert.DoesNotContain("ProfileOutputPath", types);
        Assert.DoesNotContain("public enum BindingMetric", types);
        Assert.DoesNotContain("public enum GraphMetric", types);
        Assert.DoesNotContain("public enum QueueMetric", types);
        Assert.DoesNotContain("public enum PipelineMetric", types);
        Assert.DoesNotContain("public enum PipelineUse", types);
        Assert.Contains("TooLate", types);
        Assert.Contains("Untracked", types);
        Assert.Contains("internal readonly record struct BarrierStats", types);
        Assert.Contains("internal readonly record struct DescriptorStats", types);
        Assert.Contains("internal readonly record struct BindingStats", types);
        Assert.Contains("internal readonly record struct GraphStats", types);
        Assert.Contains("internal readonly record struct QueueStats", types);
        Assert.Contains("internal readonly record struct PipelineStats", types);
        Assert.Contains("internal static void Report(in BindingStats stats)", profiler);
        Assert.Contains("internal static void Report(in GraphStats stats)", profiler);
        Assert.Contains("internal static void Report(in QueueStats stats)", profiler);
        Assert.Contains("internal static void Report(in PipelineStats stats)", profiler);
        Assert.Contains("internal static void Report(in BarrierStats stats)", profiler);
        Assert.Contains("internal static void Report(in DescriptorStats stats)", profiler);
        Assert.DoesNotContain("public static void Binding(BindingMetric metric)", profiler);
        Assert.DoesNotContain("public static void Graph(GraphMetric metric)", profiler);
        Assert.DoesNotContain("public static void Queue(QueueMetric metric", profiler);
        Assert.DoesNotContain("public static void Pipeline(PipelineMetric metric", profiler);
        Assert.DoesNotContain("public static void Barrier(", profiler);
        Assert.DoesNotContain("public static void Descriptor(", profiler);
        Assert.Contains("public static bool NeedsCounters", profiler);
        Assert.Contains("QueueGraph:{stats.Metric}", profiler);
        Assert.Contains("Graph:Cache:CompileRecent", profiler);
        Assert.Contains("Graph:Cache:CompileLocal", profiler);
        Assert.Contains("Graph:Cache:CompileShared", profiler);
        Assert.Contains("public static bool NeedsDeviceTime", profiler);
        Assert.Contains("bool NeedsCounters { get; }", types);
        Assert.Contains("bool NeedsDeviceTime { get; }", types);
        Assert.Contains("public bool NeedsCounters => true;", tracy);
        Assert.Contains("SomeEngineTracyPlotInt", tracy);
        Assert.Contains("___tracy_emit_plot_int(name, value);", nativeTracy);
        Assert.Contains("private static void Count(string name, long amount)", profiler);
        Assert.DoesNotContain("public static void Count(string name, long amount)", profiler);
        Assert.Contains("public bool NeedsDeviceTime => false;", tracy);
        Assert.Contains("public static bool NeedsNames(bool debugMarkers)", profiler);
        Assert.Contains("public bool IsActive => _scope.IsActive;", profiler);
        Assert.Contains("internal bool IsActive => _sink != null && _token.Active != 0;", types);
        Assert.Contains("Profiler.NeedsNames(emitDebugMarkers)", execute);
        Assert.Contains("Profiler.NeedsDeviceTime", timing);
        Assert.Contains("Profiler.DeviceTime", timing);
        Assert.Contains("ReportQueues(compile, finalSubmitWork);", execute);
        Assert.Contains("Profiler.QueueBatch(QueueTypeName(batch.Queue));", execute);
        Assert.Contains("Profiler.QueueLink(QueueTypeName(link.SourceQueue), QueueTypeName(link.TargetQueue));", execute);
        Assert.Contains("Profiler.QueueFinal(QueueTypeName(FinalQueue(compile)));", execute);
        Assert.Contains("if (Profiler.NeedsCounters)", execute);
        Assert.Contains("string queueName = QueueTypeName(queue);", execute);
        Assert.Contains("Profiler.QueueWait(queueName, waitCount);", execute);
        Assert.Contains("Profiler.QueueSignal(queueName", execute);
        Assert.DoesNotContain("Profiler.QueueWait(QueueTypeName(queue), waitCount);", execute);
        Assert.DoesNotContain("Profiler.QueueSignal(QueueTypeName(queue)", execute);
        Assert.DoesNotContain("private static string QueueLabel(QueueType queue)", execute);
        Assert.Contains("Profiler.Scope scope = Profiler.BeginMarker(name, \"D3D12\")", d3d12Commands);
        Assert.Contains("if (scope.IsActive)", d3d12Commands);
        Assert.Contains("Profiler.Scope scope = Profiler.BeginMarker(name, \"Null\")", nullCommands);
        Assert.Contains("if (scope.IsActive)", nullCommands);
        Assert.Contains("Profiler.RenderGraphBarriers(", barriers);
        Assert.False(File.Exists(deviceCountsPath));
        Assert.DoesNotContain("private readonly DeviceCounts _counts = new();", d3d12Counts);
        Assert.DoesNotContain("private readonly DeviceCounts _counts = new();", nullCounts);
        Assert.DoesNotContain("FlushCounts", d3d12Counts);
        Assert.DoesNotContain("FlushCounts", nullCounts);
        Assert.Contains("Profiler.Barriers(\"D3D12\"", d3d12Counts);
        Assert.Contains("Profiler.Descriptors(\"D3D12\"", d3d12Counts);
        Assert.Contains("Profiler.Barriers(\"Null\"", nullCounts);
        Assert.Contains("Report(new BarrierStats(source", renderDiagnostics);
        Assert.DoesNotContain("WriteCounters", profiler);
        Assert.DoesNotContain("WriteGroup", profiler);
        Assert.DoesNotContain("WriteCounters", types);
        Assert.DoesNotContain("WriteGroup", types);
        Assert.DoesNotContain("private sealed class DeviceCounts", d3d12Counts);
        Assert.DoesNotContain("private sealed class DeviceCounts", nullCounts);

        string[] forbiddenCalls =
        [
            "Profiler.Bindings(",
            "Profiler.Pipelines(",
            "Profiler.DependencyBarriers(",
            "Profiler.Graph(",
            "Profiler.Queue(",
            "Profiler.Barrier(",
            "Profiler.Descriptor(",
            "Profiler.Binding(",
            "Profiler.Pipeline(",
            "Profiler.Count(",
            "BindingMetric",
            "GraphMetric",
            "QueueMetric",
            "PipelineMetric",
            "PipelineUse",
            "CountBarriers",
            "CountDescriptors",
            "CountNative",
            "CountDependencies",
            "DeviceCounts",
            "PipelineCounts",
            "PipelineCounters",
            "Profiler.IsActive",
        ];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.EndsWith("/SomeEngine.Core/Diagnostics/Profiler.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            for (int i = 0; i < forbiddenCalls.Length; i++)
                Assert.DoesNotContain(forbiddenCalls[i], source);
        }
    }

    [Fact]
    public void PipelineIssues_RecordUseSite()
    {
        string root = Path.Combine(TestProjectPaths.ProjectRoot(), "src");
        string cachePath = Path.Combine(root, "SomeEngine.Render", "RHI", "PipelineCache.cs");
        string sourcesPath = Path.Combine(root, "SomeEngine.Render", "RHI", "PipelineSources.cs");
        string contextPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraphContext.cs");
        string graphPath = Path.Combine(root, "SomeEngine.Render", "Graph", "RenderGraph.cs");
        string renderContextPath = Path.Combine(root, "SomeEngine.Render", "RHI", "RenderContext.cs");
        string clusterPath = Path.Combine(root, "SomeEngine.Render", "Pipelines", "ClusterPipeline", "ClusterPipeline.Runtime.cs");
        string runtimePath = Path.Combine(root, "SomeEngine.Runtime", "RuntimeApp.cs");
        string startupOptionsPath = Path.Combine(root, "SomeEngine.Runtime", "RuntimeStartupOptions.cs");
        string issueLogPath = Path.Combine(root, "SomeEngine.Runtime", "PipelineIssueLog.cs");

        string cache = File.ReadAllText(cachePath);
        string sources = File.ReadAllText(sourcesPath);
        string context = File.ReadAllText(contextPath);
        string graph = File.ReadAllText(graphPath);
        string renderContext = File.ReadAllText(renderContextPath);
        string cluster = File.ReadAllText(clusterPath);
        string runtime = File.ReadAllText(runtimePath);
        string startupOptions = File.ReadAllText(startupOptionsPath);
        string issueLog = File.ReadAllText(issueLogPath);

        Assert.DoesNotContain("private readonly PipelineIssues _issues", cache);
        Assert.DoesNotContain("public PipelineIssue[] TakeIssues()", cache);
        Assert.Contains("public PipelineIssueSink? IssueSink", cache);
        Assert.Contains("internal void RecordIssues(IReadOnlyList<PipelineIssue> issues)", cache);
        Assert.Contains("_cache.RecordIssues(report.Issues);", sources);
        Assert.Contains("string Site = \"\"", sources);
        Assert.Contains("string Source = \"\"", sources);
        Assert.Contains("int Count = 1", sources);
        Assert.Contains("PipelineIssueResult Result", sources);
        Assert.Contains("PipelineIssueResult.TooLate", cache);
        Assert.Contains("PipelineIssueResult.Untracked", cache);
        Assert.Contains("public interface PipelineIssueSink", sources);
        Assert.Contains("Source: state.Source", cache);
        Assert.Contains("string Name => GetType().Name;", sources);
        Assert.Contains("public bool ReadyToUse => RequiredReady && Complete;", sources);
        Assert.Contains("collector.SetSource(source.Name);", cache);
        Assert.Contains("collector.SetSource(SourceName(source));", sources);
        Assert.DoesNotContain("AddFrom(PipelineCollector", sources);
        Assert.Contains("IssueSink?.Add(issue);", cache);
        Assert.DoesNotContain("_issues.Add(IssueFor", cache);
        Assert.Contains("PipelineSite()", context);
        Assert.Contains("graph.GetPipeline(ticket, need, site)", context);
        Assert.Contains("cache.GetPipeline(ticket, need, site)", graph);
        Assert.Contains("public PipelineIssueSink? PipelineIssueSink", renderContext);
        Assert.Contains("public PipelineWarmup LastWarmup", cluster);
        Assert.Contains("LastWarmup = WarmupPipelines", cluster);
        Assert.Contains("public PipelineWarmup WarmupPipelines()", cluster);
        Assert.Contains("_context.WarmupSources(int.MaxValue)", cluster);
        Assert.Contains("DrawPipelineWarmup(clusterPipeline.LastWarmup)", runtime);
        Assert.Contains("warmup.BudgetLimited", runtime);
        Assert.Contains("context.PipelineIssueSink = debugUiOpen ? pipelineIssues : null;", runtime);
        Assert.Contains("startupOptions.WaitForPipelineWarmup && !pipelineWarmupReady", runtime);
        Assert.Contains("PipelineWarmup warmup = clusterPipeline.WarmupPipelines();", runtime);
        Assert.Contains("if (warmup.RequiredFailed > 0)", runtime);
        Assert.Contains("Required pipeline warmup failed", runtime);
        Assert.Contains("pipelineWarmupReady = warmup.ReadyToUse;", runtime);
        Assert.DoesNotContain("pipelineWarmupReady = warmup.Complete;", runtime);
        Assert.Contains("clusterPipeline.MaterialPipelineBudget = startupOptions.PipelineWarmupBudget;", runtime);
        Assert.Contains("int PipelineWarmupBudget", startupOptions);
        Assert.Contains("bool WaitForPipelineWarmup", startupOptions);
        Assert.Contains("--pipeline-budget", startupOptions);
        Assert.Contains("--wait-pipelines", startupOptions);
        Assert.Contains("--no-wait-pipelines", startupOptions);
        Assert.DoesNotContain("SOMEENGINE_", startupOptions);
        Assert.DoesNotContain("pipelineIssues.Collect(context);", runtime);
        Assert.Contains("pipelineIssues.Draw();", runtime);
        Assert.Contains("PipelineIssueLog : PipelineIssueSink", issueLog);
        Assert.Contains("Pipeline Issues", issueLog);
        Assert.Contains("issue.Result", issueLog);
        Assert.Contains("issue.Source", issueLog);
        Assert.DoesNotContain("private void Merge(PipelineIssue issue)", issueLog);
        Assert.DoesNotContain("Console.WriteLine(Text(issue))", issueLog);

        int sinkIndex = runtime.IndexOf("context.PipelineIssueSink = debugUiOpen ? pipelineIssues : null;", StringComparison.Ordinal);
        int beginFrameIndex = runtime.IndexOf("renderGraph.BeginFrame(frameData, static (graph, data) => RecordRenderFrame(graph, data));", StringComparison.Ordinal);
        int waitIndexRuntime = runtime.IndexOf("PipelineWarmup warmup = clusterPipeline.WarmupPipelines();", StringComparison.Ordinal);
        int executeIndex = runtime.IndexOf("renderGraph.Execute(graphQueues, executionSwapchain, context.PresentSyncInterval);", StringComparison.Ordinal);
        Assert.True(sinkIndex >= 0);
        Assert.True(beginFrameIndex > sinkIndex);
        Assert.True(waitIndexRuntime > sinkIndex);
        Assert.True(executeIndex > waitIndexRuntime);

        int warmupIndex = cache.IndexOf("public PipelineWarmup Warmup(ReadOnlySpan<PipelineTicket> tickets, int budget)", StringComparison.Ordinal);
        int waitIndex = cache.IndexOf("public PipelineWarmup WaitRequired(ReadOnlySpan<PipelineTicket> tickets)", warmupIndex, StringComparison.Ordinal);
        Assert.True(warmupIndex >= 0);
        Assert.True(waitIndex > warmupIndex);
        Assert.DoesNotContain("_cache.RecordIssues(report.Issues);", cache[warmupIndex..waitIndex]);
    }

    [Fact]
    public void D3D12LibraryKey_UsesPooledBytes()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Rhi.D3D12",
            "D3D12Device.Helpers.cs");
        string source = File.ReadAllText(path);

        Assert.DoesNotContain("new MemoryStream", source);
        Assert.DoesNotContain("new BinaryWriter", source);
        Assert.DoesNotContain("stream.ToArray()", source);
        Assert.DoesNotContain("Bytecode.ToArray()", source);
        Assert.Contains("LibraryKeyBytes : IDisposable", source);
        Assert.Contains("ArrayPool<byte>.Shared.Rent", source);
        Assert.Contains("BinaryPrimitives.WriteInt32LittleEndian", source);
        Assert.Contains("SHA256.HashData(buffer.AsSpan(0, _length))", source);
    }

    [Fact]
    public void PipelineEntry_RequiresAssetStore()
    {
        var method = typeof(ClusterPipeline).GetMethod(
            nameof(ClusterPipeline.Opaque),
            ReflectFlags.Public | ReflectFlags.Static);

        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), parameter => parameter.ParameterType == typeof(AssetStore));
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(AssetDatabase));
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType.Name == "ClusterRenderAsset");
        Assert.DoesNotContain(
            typeof(ClusterPipeline).GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance),
            field => field.FieldType.Name.Contains("PsoSet", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_DoesNotOwnGpuHandles()
    {
        string[] gpuHandles =
        [
            "TextureViewHandle",
            "BufferViewHandle",
            "SamplerHandle",
            "BindingSetHandle",
            "PipelineHandle",
        ];

        foreach (var field in typeof(Material).GetFields(ReflectFlags.Public | ReflectFlags.NonPublic | ReflectFlags.Instance))
        {
            foreach (string handle in gpuHandles)
                Assert.DoesNotContain(handle, field.FieldType.Name);
        }

        foreach (var property in typeof(Material).GetProperties(ReflectFlags.Public | ReflectFlags.NonPublic | ReflectFlags.Instance))
        {
            foreach (string handle in gpuHandles)
                Assert.DoesNotContain(handle, property.PropertyType.Name);
        }

        Assert.Equal(
            typeof(Handle<Texture>),
            typeof(Material).GetProperty(nameof(Material.AlbedoMap))!.PropertyType);

        var method = typeof(Material).GetMethod(
            "ToBindSet",
            ReflectFlags.NonPublic | ReflectFlags.Instance);

        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), parameter => parameter.ParameterType == typeof(AssetStore));
    }

    [Fact]
    public void InstanceHeader_UsesRegisteredPipelineField()
    {
        Assert.True(InstanceHeaderLayout.SlotOffset < InstanceHeaderLayout.StrideBytes);
        Assert.True(InstanceHeaderLayout.BoundsExpansionWorld < InstanceHeaderLayout.StrideBytes);

        var data = new InstanceHeaderData();
        data.SetU32(0, InstanceHeaderLayout.SlotOffset, 42);
        byte[] header = new byte[InstanceHeaderLayout.StrideBytes];
        data.Write(0, header);

        Assert.Equal(42u, BitConverter.ToUInt32(header, checked((int)InstanceHeaderLayout.SlotOffset)));
    }

    [Fact]
    public void RenderWorld_DoesNotStoreClusterBvhRoot()
    {
        Assert.Null(typeof(RenderInstance).GetField("BvhRootIndex"));
        Assert.Null(typeof(MeshInstance).GetField("BVHRootIndex"));
    }

    [Fact]
    public void RuntimeAssets_DoNotUseSchemaHandles()
    {
        Assert.Null(typeof(Shader).GetProperty("Asset"));
        Assert.Null(typeof(Mesh).GetProperty("Asset"));
        Assert.Null(typeof(RenderInstance).GetField("Materials"));

        Assert.Equal(
            typeof(Handle<Mesh>),
            typeof(MeshInstance).GetField(nameof(MeshInstance.Mesh))!.FieldType);
        Assert.Equal(
            typeof(Handle<Mesh>),
            typeof(RenderInstance).GetField(nameof(RenderInstance.Mesh))!.FieldType);

        foreach (var property in typeof(ClusterShaders).GetProperties(ReflectFlags.Public | ReflectFlags.Instance))
        {
            Assert.Equal(typeof(Handle<Shader>), property.PropertyType);
            Assert.NotEqual(typeof(SomeEngine.Assets.Schema.ShaderAsset), property.PropertyType);
        }
    }

    [Fact]
    public void RuntimeAssets_DoNotExposeSchemaConstructors()
    {
        AssertSchemaFree(typeof(Shader));
        AssertSchemaFree(typeof(Mesh));
        AssertSchemaFree(typeof(ScalarLayout));
        Assert.Null(typeof(ScalarLayout).GetMethod("FromShaderLayout", ReflectFlags.Public | ReflectFlags.Static));
    }

    [Fact]
    public void PipelineReadsRenderWorldPerFrame()
    {
        Type pipeline = typeof(ClusterPipeline);
        Assert.DoesNotContain(
            pipeline.GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance),
            field => field.FieldType == typeof(RenderWorld) || field.FieldType == typeof(World));

        var opaque = pipeline.GetMethod(nameof(ClusterPipeline.Opaque), ReflectFlags.Public | ReflectFlags.Static);
        Assert.NotNull(opaque);
        Assert.DoesNotContain(opaque!.GetParameters(), parameter => parameter.ParameterType == typeof(RenderWorld));

        var addPasses = pipeline.GetMethod(nameof(ClusterPipeline.AddPasses), ReflectFlags.Public | ReflectFlags.Instance);
        Assert.NotNull(addPasses);
        Assert.Contains(addPasses!.GetParameters(), parameter => parameter.ParameterType == typeof(RenderWorld));

        Assert.DoesNotContain(
            typeof(InstanceGpu).GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance),
            field => field.FieldType == typeof(World));

        Type materialItems = RenderType("MaterialItems");
        Assert.DoesNotContain(
            materialItems.GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance),
            field => field.FieldType == typeof(RenderWorld) || field.FieldType == typeof(World));
        Assert.DoesNotContain(
            materialItems.GetConstructors(ReflectFlags.Public | ReflectFlags.NonPublic | ReflectFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(RenderWorld) || parameter.ParameterType == typeof(World));
    }

    [Fact]
    public void PipelineCache_DoesNotUseAssetHandles()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "PipelineCache.cs"));
        Assert.DoesNotContain("SomeEngine.Assets", source);
    }

    [Fact]
    public void RenderProject_DoesNotReferenceD3D12()
    {
        string project = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "SomeEngine.Render.csproj"));
        string renderContext = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "RHI",
            "RenderContext.cs"));

        Assert.DoesNotContain("SomeEngine.Rhi.D3D12", project);
        Assert.DoesNotContain("SomeEngine.Rhi.D3D12", renderContext);
        Assert.DoesNotContain("D3D12Backend", renderContext);
    }

    [Fact]
    public void RenderContext_RequiresHostBackend()
    {
        var methods = typeof(RenderContext).GetMethods(ReflectFlags.Public | ReflectFlags.Instance);

        foreach (var method in methods)
        {
            if (method.Name != nameof(RenderContext.Initialize))
                continue;

            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(Backend));
        }
    }

    [Fact]
    public void RenderGraph_UsesBindSetOwnerNames()
    {
        Assert.Null(typeof(RenderGraph).GetMethod("ClearBindingCache"));
        Assert.NotNull(typeof(RenderGraph).GetMethod(nameof(RenderGraph.ClearBindSets)));
    }

    [Fact]
    public void ClusterPipeline_DoesNotOwnPsoLifetime()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");

        foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("PipelineState?.Drop", source);
        }
    }

    [Fact]
    public void ClusterPipeline_UsesStageOrchestrators()
    {
        Type pipeline = typeof(ClusterPipeline);
        Assert.DoesNotContain(
            pipeline.GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance),
            field => field.FieldType.Name.EndsWith("Pass", StringComparison.Ordinal));

        Assert.Null(pipeline.Assembly.GetType("SomeEngine.Render.Pipelines.ClusterUploadStage"));
        Assert.Null(pipeline.Assembly.GetType("SomeEngine.Render.Pipelines.ClusterCullStage"));
        Assert.Null(pipeline.Assembly.GetType("SomeEngine.Render.Pipelines.ClusterResolveStage"));

        AssertStage("ClusterSceneStage", 3);
        AssertStage("ClusterRasterStage", 6);
        AssertStage("ClusterShadeStage", 2);
        AssertStage("ClusterOutputStage", 2);
    }

    [Fact]
    public void ClusterPipeline_DoesNotLoadSchemaAssets()
    {
        Type pipeline = typeof(ClusterPipeline);
        Assert.DoesNotContain(
            pipeline.GetMethods(ReflectFlags.Public | ReflectFlags.Instance),
            method => method.Name == "LoadMesh");

        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("SomeEngine.Assets.Schema", source);
            Assert.DoesNotContain("MeshAsset", source);
            Assert.DoesNotContain("ShaderAsset", source);
            Assert.DoesNotContain("MaterialAsset", source);
            Assert.DoesNotContain("AssetGuid", source);
        }
    }

    [Fact]
    public void MaterialLoader_DoesNotOwnClusterTargets()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Assets",
            "MaterialAssetLoader.cs");

        string source = File.ReadAllText(path);
        Assert.DoesNotContain("ClusterPassTargets", source);
        Assert.DoesNotContain("SomeEngine.Render.Pipelines", source);
    }

    [Fact]
    public void MaterialPasses_DoNotOwnSourceState()
    {
        string[] passNames =
        [
            "ClusterDrawPass",
            "SwRasterPass",
            "MaterialShadePass",
            "ClusterDeformPass",
        ];

        foreach (string name in passNames)
        {
            Type type = RenderType(name);
            var fields = type.GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(AssetStore));
            Assert.DoesNotContain(fields, field => field.FieldType.Name == "MaterialGpu");
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(Material));
        }

        Assert.Null(RenderType("ClusterDeformPass").GetField(
            "_materialPrograms",
            ReflectFlags.NonPublic | ReflectFlags.Instance));
    }

    [Fact]
    public void DeformCache_ResourcesArePipelineOwned()
    {
        string root = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");

        string pipeline = File.ReadAllText(Path.Combine(root, "ClusterPipeline.Runtime.cs"));
        string stage = File.ReadAllText(Path.Combine(root, "ClusterRasterStage.cs"));
        string pass = File.ReadAllText(Path.Combine(root, "ClusterDeformPass.cs"));

        Assert.Contains("CreateDeformCache", pipeline);
        Assert.Contains("CreateCounterBuffer", pipeline);
        Assert.DoesNotContain("CreateDeformCache", stage);
        Assert.DoesNotContain("CreateBuffer", stage);
        Assert.DoesNotContain("CreateBuffer", pass);
        Assert.DoesNotContain("CreateCacheResources", pass);
    }

    [Fact]
    public void OutputStage_DoesNotOwnTemporalState()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "ClusterOutputStage.cs");

        string source = File.ReadAllText(path);
        Assert.DoesNotContain("RenderHistoryRegistry", source);
        Assert.DoesNotContain("RenderHistoryNames", source);
        Assert.DoesNotContain("TemporalState", source);
        Assert.DoesNotContain("CopyTexture", source);
    }

    private static void AssertStage(string name, int passCount)
    {
        Type type = RenderType(name);
        Assert.True(type.IsValueType);
        int count = type
            .GetFields(ReflectFlags.NonPublic | ReflectFlags.Instance)
            .Count(field => field.FieldType.Name.EndsWith("Pass", StringComparison.Ordinal));
        Assert.True(count >= passCount, $"{name} should hold at least {passCount} pass fields but held {count}.");
    }

    private static void AssertSchemaFree(Type type)
    {
        foreach (var constructor in type.GetConstructors(ReflectFlags.Public | ReflectFlags.NonPublic | ReflectFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Assert.False(
                    IsSchemaType(parameter.ParameterType),
                    $"{type.Name} constructor parameter {parameter.Name} exposes schema type {parameter.ParameterType}.");
            }
        }

        foreach (var method in type.GetMethods(ReflectFlags.Public | ReflectFlags.NonPublic | ReflectFlags.Static | ReflectFlags.Instance))
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.False(
                    IsSchemaType(parameter.ParameterType),
                    $"{type.Name}.{method.Name} parameter {parameter.Name} exposes schema type {parameter.ParameterType}.");
            }
        }
    }

    private static bool IsSchemaType(Type type)
        => type.Namespace?.StartsWith("SomeEngine.Assets.Schema", StringComparison.Ordinal) == true;

    private static Type RenderType(string name)
        => typeof(ClusterPipeline).Assembly.GetType($"SomeEngine.Render.Pipelines.{name}")
            ?? throw new InvalidOperationException($"Render type {name} was not found.");
}
