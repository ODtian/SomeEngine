using System.Reflection;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using ReflectionBindingFlags = System.Reflection.BindingFlags;

namespace SomeEngine.Tests;

internal static class RenderGraphTestHelpers
{
    public static void WriteTexture(
        RenderGraph graph,
        RenderGraphHandle texture,
        string name = "Write Texture",
        ResourceState state = ResourceState.RenderTarget)
        => graph.AddRasterPass(name, builder => builder.Write(texture, state), _ => { });

    public static string[] ExecutedPassNames(RenderGraph graph)
    {
        var passes = Passes(graph);
        object compiled = Compiled(graph);
        var passField = compiled.GetType().GetField(
            "Passes",
            ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance);
        var passOrder = (IEnumerable<int>)passField!.GetValue(compiled)!;
        return passOrder.Select(passIndex => PassName(passes[passIndex])).ToArray();
    }

    public static int RequiredTransitionCount(RenderGraph graph, string passName)
    {
        var passes = Passes(graph);
        object compiled = Compiled(graph);
        var transitions = (Array)compiled.GetType()
            .GetField("Transitions", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            if (PassName(passes[passIndex]) == passName)
                return RequiredCount(transitions.GetValue(passIndex)!);
        }

        throw new InvalidOperationException($"RenderGraph pass '{passName}' was not registered.");
    }

    public static int FinalTransitionCount(RenderGraph graph)
    {
        object compiled = Compiled(graph);
        object finalTransitions = compiled.GetType()
            .GetField("FinalTransitions", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return RequiredCount(finalTransitions);
    }

    public static int FinalTransitionTotal(RenderGraph graph)
    {
        object compiled = Compiled(graph);
        object finalTransitions = compiled.GetType()
            .GetField("FinalTransitions", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return Count(finalTransitions.GetType()
            .GetProperty("TransitionSource", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(finalTransitions)!);
    }

    public static int ResolveCount(RenderGraph graph, string passName)
    {
        var passes = Passes(graph);
        object compiled = Compiled(graph);
        var resolves = (Array)compiled.GetType()
            .GetField("Resolves", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            if (PassName(passes[passIndex]) == passName)
                return Count(resolves.GetValue(passIndex)!);
        }

        throw new InvalidOperationException($"RenderGraph pass '{passName}' was not registered.");
    }

    public static int UseCount(RenderGraph graph, string passName)
    {
        var passes = Passes(graph);
        object compiled = Compiled(graph);
        var uses = (Array)compiled.GetType()
            .GetField("Uses", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            if (PassName(passes[passIndex]) == passName)
                return Count(uses.GetValue(passIndex)!);
        }

        throw new InvalidOperationException($"RenderGraph pass '{passName}' was not registered.");
    }

    public static int PassEpilogueCount(RenderGraph graph, string passName)
    {
        var passes = Passes(graph);
        object compiled = Compiled(graph);
        var epilogues = (Array)compiled.GetType()
            .GetField("PassEpilogues", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            if (PassName(passes[passIndex]) == passName)
                return Count(epilogues.GetValue(passIndex)!);
        }

        throw new InvalidOperationException($"RenderGraph pass '{passName}' was not registered.");
    }

    public static int RootResolveCount(RenderGraph graph)
    {
        object compiled = Compiled(graph);
        object rootResolves = compiled.GetType()
            .GetField("RootResolves", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return Count(rootResolves);
    }

    public static int DependencyCount(RenderGraph graph)
    {
        object compiled = Compiled(graph);
        object dependencies = compiled.GetType()
            .GetField("Dependencies", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return Count(dependencies);
    }

    public static string[] QueueBatches(RenderGraph graph)
        => QueueBatches(graph, asyncCompute: false, asyncCopy: false);

    public static string[] QueueBatches(RenderGraph graph, bool asyncCompute)
        => QueueBatches(graph, asyncCompute, asyncCopy: false);

    public static string[] QueueBatches(RenderGraph graph, bool asyncCompute, bool asyncCopy)
    {
        object compiled = Compiled(graph, asyncCompute, asyncCopy);
        var batches = (System.Collections.IEnumerable)compiled.GetType()
            .GetField("QueueBatches", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        var result = new List<string>();
        foreach (object batch in batches)
        {
            var type = batch.GetType();
            string kind = type
                .GetProperty("Kind", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(batch)!
                .ToString()!;
            string queue = type
                .GetProperty("Queue", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(batch)!
                .ToString()!;
            int startSlot = (int)type
                .GetProperty("StartSlot", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(batch)!;
            int count = (int)type
                .GetProperty("Count", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(batch)!;
            result.Add($"{kind}:{queue}:{startSlot}:{count}");
        }

        return result.ToArray();
    }

    public static string[] QueueLinks(RenderGraph graph, bool asyncCompute)
        => QueueLinks(graph, asyncCompute, asyncCopy: false);

    public static string[] QueueLinks(RenderGraph graph, bool asyncCompute, bool asyncCopy)
    {
        object compiled = Compiled(graph, asyncCompute, asyncCopy);
        var links = (System.Collections.IEnumerable)compiled.GetType()
            .GetField("QueueLinks", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        var result = new List<string>();
        foreach (object link in links)
        {
            var type = link.GetType();
            int sourceBatch = (int)type
                .GetProperty("SourceBatch", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(link)!;
            int targetBatch = (int)type
                .GetProperty("TargetBatch", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(link)!;
            string sourceQueue = type
                .GetProperty("SourceQueue", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(link)!
                .ToString()!;
            string targetQueue = type
                .GetProperty("TargetQueue", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(link)!
                .ToString()!;
            result.Add($"{sourceBatch}:{targetBatch}:{sourceQueue}->{targetQueue}");
        }

        return result.ToArray();
    }

    public static bool ResourceReusable(RenderGraph graph, RenderGraphHandle handle)
        => (bool)RecordField(ResourceRecord(graph, handle), "Reusable");

    public static bool ResourceRetained(RenderGraph graph, RenderGraphHandle handle)
        => (bool)RecordField(ResourceRecord(graph, handle), "Retained");

    public static bool ResourceExported(RenderGraph graph, RenderGraphHandle handle)
        => (bool)RecordField(ResourceRecord(graph, handle), "Exported");

    public static bool ResourceFinalState(RenderGraph graph, RenderGraphHandle handle)
        => (bool)RecordField(ResourceRecord(graph, handle), "FinalState");

    public static (int FirstPass, int LastPass) ResourceLifetime(
        RenderGraph graph,
        RenderGraphHandle handle,
        bool asyncCompute = false,
        bool asyncCopy = false)
    {
        object record = ResourceRecord(graph, handle, asyncCompute, asyncCopy);
        return ((int)RecordField(record, "FirstPass"), (int)RecordField(record, "LastPass"));
    }

    public static int OperationCount(IDevice device, string kind)
    {
        int count = 0;
        foreach (object record in CommandRecords(device))
        {
            int operationCount = (int)record.GetType()
                .GetProperty("OperationCount", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(record)!;
            var operations = (Array)record.GetType()
                .GetProperty("Operations", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(record)!;
            for (int i = 0; i < operationCount; i++)
            {
                object operation = operations.GetValue(i)!;
                string operationKind = operation.GetType()
                    .GetProperty("Kind", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                    .GetValue(operation)!
                    .ToString()!;
                if (operationKind == kind)
                    count++;
            }
        }

        return count;
    }

    public static int LiveHandleCount(IDevice device, string storeName)
    {
        object store = device.GetType()
            .GetField(storeName, ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .GetValue(device)!;
        object entries = store.GetType()
            .GetField("_entries", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(store)!;
        return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
    }

    public static int CommandCount(IDevice device, string queue)
    {
        int count = 0;
        foreach (object record in CommandRecords(device))
        {
            string queueType = record.GetType()
                .GetProperty("QueueType", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(record)!
                .ToString()!;
            if (queueType == queue)
                count++;
        }

        return count;
    }

    public static int RetainCount(RenderGraph graph)
    {
        object compiled = Compiled(graph);
        object retains = compiled.GetType()
            .GetField("Retains", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return Count(retains);
    }

    public static BindFlags BufferPoolBindFlags(RenderGraph graph, RenderGraphHandle handle)
    {
        object key = RecordField(ResourceRecord(graph, handle), "BufferKey");
        return (BindFlags)key.GetType()
            .GetProperty("BindFlags", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(key)!;
    }

    public static ulong BufferPoolSize(RenderGraph graph, RenderGraphHandle handle)
    {
        object key = RecordField(ResourceRecord(graph, handle), "BufferKey");
        return (ulong)key.GetType()
            .GetProperty("SizeInBytes", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(key)!;
    }

    public static int ResourceIndex(RenderGraph graph, RenderGraphHandle handle)
    {
        var method = typeof(RenderGraph).GetMethod(
            "GetResourceIndex",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance);
        return (int)method!.Invoke(graph, [handle, nameof(ResourceIndex)])!;
    }

    public static bool ResourceStateTrusted(RenderGraph graph, RenderGraphHandle handle)
    {
        var resourcesField = typeof(RenderGraph).GetField(
            "_resources",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance);
        var resources = (System.Collections.IList)resourcesField!.GetValue(graph)!;
        object resource = resources[ResourceIndex(graph, handle)]!;
        return (bool)resource.GetType()
            .GetField("StateTrusted", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(resource)!;
    }

    public static TextureDesc BackBufferDesc(uint width, uint height)
    {
        return new TextureDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Width = width,
            Height = height,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
    }

    private static object[] Passes(RenderGraph graph)
    {
        var passesField = typeof(RenderGraph).GetField(
            "_passes",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance);
        return ((System.Collections.IEnumerable)passesField!.GetValue(graph)!).Cast<object>().ToArray();
    }

    private static string PassName(object pass)
        => (string)pass.GetType()
            .GetProperty("Name", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(pass)!;

    private static object Compiled(RenderGraph graph)
        => Compiled(graph, asyncCompute: false, asyncCopy: false);

    private static object Compiled(RenderGraph graph, bool asyncCompute, bool asyncCopy)
    {
        var method = typeof(RenderGraph).GetMethod(
            "CompileGraph",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance);
        return method!.Invoke(graph, [asyncCompute, asyncCopy])!;
    }

    private static object ResourceRecord(RenderGraph graph, RenderGraphHandle handle)
        => ResourceRecord(graph, handle, asyncCompute: false, asyncCopy: false);

    private static object ResourceRecord(
        RenderGraph graph,
        RenderGraphHandle handle,
        bool asyncCompute,
        bool asyncCopy)
    {
        object compiled = Compiled(graph, asyncCompute, asyncCopy);
        var resources = (Array)compiled.GetType()
            .GetField("Resources", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        return resources.GetValue(ResourceIndex(graph, handle))!;
    }

    private static object RecordField(object resourceRecord, string name)
        => resourceRecord.GetType()
            .GetField(name, ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(resourceRecord)!;

    private static System.Collections.IEnumerable CommandRecords(IDevice device)
    {
        var buffersField = device.GetType().GetField(
            "CommandBuffers",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance);
        if (buffersField == null)
            return Array.Empty<object>();

        object store = buffersField.GetValue(device)!;
        return (System.Collections.IEnumerable)store.GetType()
            .GetProperty("Values", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(store)!;
    }

    private static int RequiredCount(object transitions)
        => Count(
            transitions.GetType()
                .GetProperty("RequiredTransitionSource", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .GetValue(transitions)!);

    private static int Count(object collection)
    {
        PropertyInfo? countProperty = collection.GetType()
            .GetProperty("Count", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance);
        if (countProperty != null)
            return (int)countProperty.GetValue(collection)!;

        if (collection is Array array)
            return array.Length;

        throw new InvalidOperationException($"Collection '{collection.GetType().FullName}' does not expose Count or array Length.");
    }

    public static ShaderAsset CreatePostTonemapShader()
        => CreateGraphicsShader(AssetGuid.New(), "PostTonemap");

    public static ShaderAsset CreateTemporalResolveShader()
        => CreateGraphicsShader(AssetGuid.New(), "TemporalResolve");

    private static ShaderAsset CreateGraphicsShader(AssetGuid guid, string name)
        => new()
        {
            AssetGuid = guid.ToFlatString(),
            Name = name,
            ImportTrace = new ImportTrace
            {
                SourceGuid = SourceGuid.New().ToFlatString(),
                SourcePath = $"{name}.slang",
                SubAssetKey = "shader:main",
                ContentFingerprint = "fp",
                Dependencies = [],
                ImporterVersion = 1,
            },
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "dxil",
                    Stage = AssetShaderStage.Vertex,
                    EntryPoint = "VSMain",
                    Data = Array.Empty<byte>(),
                    ContentHash = $"{name}-vs",
                },
                new ShaderBytecode
                {
                    Backend = "dxil",
                    Stage = AssetShaderStage.Pixel,
                    EntryPoint = "PSMain",
                    Data = Array.Empty<byte>(),
                    ContentHash = $"{name}-ps",
                },
            ],
            EntryPointAttributes = [],
            Reflections = [],
            Metadata = new ShaderMetadata
            {
                Tags = [],
                MaterialBindings = [],
                MaterialScalarLayouts = [],
            },
        };

}
