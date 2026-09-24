using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    internal sealed class RenderGraphCompiler
    {
        private readonly RenderGraph _graph;

        internal readonly System.Threading.Lock Gate = new();
        internal CompiledGraph? CompileResult;
        internal CompiledGraph? CompileScratch;
        internal CompiledGraph? CompileWork;
        internal Task<CompiledGraph>? CompileTask;
        internal GraphSchema? CompileTaskSchema;
        internal int CompileTaskGeneration;
        internal bool CompileTaskCompute;
        internal bool CompileTaskCopy;
        internal GraphSchema? CompileSchema;
        internal readonly GraphCache<CompiledGraph> GraphCache = new();

        public RenderGraphCompiler(RenderGraph graph)
            => _graph = graph ?? throw new ArgumentNullException(nameof(graph));

        public void Compile(GraphQueues queues)
        {
            queues.Require();
            CompileCurrentCore(queues.HasCompute, queues.HasCopy);
        }

        public void Compile()
            => CompileCurrentCore();

        internal Task CompileAsync(
            bool asyncCompute = false,
            bool asyncCopy = false,
            CancellationToken cancellationToken = default)
            => CompileAsyncCore(asyncCompute, asyncCopy, cancellationToken);

        internal Task CompileAsyncCore(
            bool asyncCompute = false,
            bool asyncCopy = false,
            CancellationToken cancellationToken = default)
        {
            _graph.ThrowIfDisposed();
            _graph.ThrowIfActive(nameof(RenderGraph.CompileAsync));
            cancellationToken.ThrowIfCancellationRequested();

            WaitForBackgroundCompile();
            lock (Gate)
            {
                if (CompileTask is { IsCompleted: false })
                    return CompileTask;

                Interlocked.Increment(ref _graph._backgroundCompileActive);
                try
                {
                    PrepareCompile(ref asyncCompute, ref asyncCopy);
                    if (HasCompile(asyncCompute, asyncCopy))
                    {
                        Interlocked.Decrement(ref _graph._backgroundCompileActive);
                        return Task.CompletedTask;
                    }

                    GraphSchema schema = CurrentSchema();
                    if (GraphCache.TryGet(schema, out CompiledGraph? cached, out GraphHit hit))
                    {
                        Profiler.GraphCompileHit(RenderGraph.ResultName(hit));
                        SetCompile(cached, schema, asyncCompute, asyncCopy);
                        Interlocked.Decrement(ref _graph._backgroundCompileActive);
                        return Task.CompletedTask;
                    }

                    RenderGraphSnapshot snapshot = CaptureSnapshot(schema);
                    CompileTaskGeneration = _graph._generation;
                    CompileTaskSchema = schema;
                    CompileTaskCompute = asyncCompute;
                    CompileTaskCopy = asyncCopy;
                    Volatile.Write(ref _graph._backgroundCompilePending, 1);

                    CompileTask = Task.Run(
                        () => CompileInBackground(snapshot, asyncCompute, asyncCopy, cancellationToken));
                    return CompileTask;
                }
                catch
                {
                    Volatile.Write(ref _graph._backgroundCompilePending, 0);
                    Interlocked.Decrement(ref _graph._backgroundCompileActive);
                    throw;
                }
            }
        }

        internal CompiledGraph CompileGraph(bool asyncCompute, bool asyncCopy)
        {
            WaitForBackgroundCompile();
            lock (Gate)
            {
                PrepareCompile(ref asyncCompute, ref asyncCopy);
                if (TryUseCompile(asyncCompute, asyncCopy, out CompiledGraph? compile))
                    return compile;

                CompiledGraph result = CompileCore(asyncCompute, asyncCopy);
                SetCompile(result, asyncCompute, asyncCopy);
                return UseCompile(result);
            }
        }

        internal CompiledGraph CompileInBackground(
            RenderGraphSnapshot snapshot,
            bool asyncCompute,
            bool asyncCopy,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompiledGraph result = snapshot.Compile(asyncCompute, asyncCopy);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _graph._backgroundCompileActive);
            }
        }

        internal void WaitForBackgroundCompile()
        {
            Task<CompiledGraph>? task;
            lock (Gate)
                task = CompileTask;
            if (task == null)
                return;

            CompiledGraph result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch
            {
                lock (Gate)
                {
                    if (ReferenceEquals(CompileTask, task) && task.IsCompleted)
                    {
                        CompileTask = null;
                        CompileTaskSchema = null;
                        Volatile.Write(ref _graph._backgroundCompilePending, 0);
                    }
                }

                throw;
            }

            lock (Gate)
            {
                if (!ReferenceEquals(CompileTask, task) || CompileTaskSchema is not { } taskSchema)
                    return;

                if (_graph._generation != CompileTaskGeneration
                    || _graph._schema == null
                    || !_graph._schema.Equals(taskSchema))
                {
                    CompileTask = null;
                    CompileTaskSchema = null;
                    Volatile.Write(ref _graph._backgroundCompilePending, 0);
                    throw new InvalidOperationException(
                        "RenderGraph was changed while background compilation was active.");
                }

                GraphCache.Store(taskSchema, result);
                SetCompile(result, taskSchema, CompileTaskCompute, CompileTaskCopy);
                CompileTask = null;
                CompileTaskSchema = null;
                Volatile.Write(ref _graph._backgroundCompilePending, 0);
            }
        }

        internal void ThrowIfBackgroundCompile(string operation)
        {
            if (Volatile.Read(ref _graph._backgroundCompileActive) > 0
                || Volatile.Read(ref _graph._backgroundCompilePending) > 0)
            {
                throw new InvalidOperationException(
                    $"RenderGraph {operation} cannot mutate the live graph while background compilation is active.");
            }
        }

        internal void PrepareCompile(ref bool asyncCompute, ref bool asyncCopy)
        {
            if (!_graph._featuresAppliedThisFrame && _graph._features.Count != 0)
            {
                throw new InvalidOperationException(
                    "RenderGraph compile requires render features to be recorded on the owner thread before compilation. Use BeginFrame() or BeginFrame(record) for feature-driven frames.");
            }

            bool hasComputeMode = false;
            bool hasCopyMode = false;
            for (int passIndex = 0; passIndex < _graph._passes.Count; passIndex++)
            {
                PassMode mode = _graph._passes[passIndex].Mode;
                hasComputeMode |= mode is PassMode.Compute or PassMode.AsyncCompute;
                hasCopyMode |= mode is PassMode.Copy or PassMode.AsyncCopy;
            }

            asyncCompute &= hasComputeMode;
            asyncCopy &= hasCopyMode;

            _graph.Declarations().EnsureShape(_graph._passes.Count, _graph._resources.Count);
            _graph._schema = _graph._schemaState.Finish(_graph._passes.Count, _graph._resources.Count, asyncCompute, asyncCopy);
        }

        internal RenderGraphSnapshot CaptureSnapshot(GraphSchema schema)
        {
            var passes = new PassSnapshot[_graph._passes.Count];
            for (int passIndex = 0; passIndex < passes.Length; passIndex++)
            {
                passes[passIndex] = new PassSnapshot(
                    _graph._passes[passIndex].Name,
                    _graph._passes[passIndex].Mode,
                    _graph._passes[passIndex].SideEffect);
            }

            var resources = new ResourceSnapshot[_graph._resources.Count];
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
                resources[resourceIndex] = ResourceSnapshot.From(_graph._resources[resourceIndex]);

            var textureExports = new int[_graph._textureExports.Count];
            for (int exportIndex = 0; exportIndex < textureExports.Length; exportIndex++)
                textureExports[exportIndex] = _graph._textureExports[exportIndex].ResourceIndex;

            var bufferExports = new int[_graph._bufferExports.Count];
            for (int exportIndex = 0; exportIndex < bufferExports.Length; exportIndex++)
                bufferExports[exportIndex] = _graph._bufferExports[exportIndex].ResourceIndex;

            return new RenderGraphSnapshot(
                schema,
                _graph.Declarations().Copy(),
                passes,
                resources,
                textureExports,
                bufferExports,
                _graph._finalStates.ToArray());
        }

        internal CompiledGraph CompileCore(bool asyncCompute, bool asyncCopy)
        {
            GraphSchema schema = CurrentSchema();
            if (GraphCache.TryGet(schema, out CompiledGraph? cached, out GraphHit hit))
            {
                Profiler.GraphCompileHit(RenderGraph.ResultName(hit));
                _graph._declarationsFrozen = true;
                return cached;
            }

            var compile = TakeCompile();
            compile.UseDeclarations(_graph.Declarations(), _graph._passes.Count, _graph._resources.Count);
            Profiler.GraphCompileMiss();
            CompiledGraph result = CompileUncached(compile, asyncCompute, asyncCopy);
            GraphCache.Store(schema, result);
            _graph._declarationsFrozen = true;
            return result;
        }

        internal CompiledGraph CompileUncached(bool asyncCompute, bool asyncCopy)
        {
            var compile = TakeCompile();
            compile.UseDeclarations(_graph.Declarations(), _graph._passes.Count, _graph._resources.Count);
            return CompileUncached(compile, asyncCompute, asyncCopy);
        }

        internal CompiledGraph CompileUncached(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
        {
            CullPasses(compile, asyncCompute, asyncCopy);
            _graph.GatherResolves(compile);
            BuildBatches(compile);
            ApplyQueueLifetimePadding(compile);
            _graph.BuildFrameSync(compile);
            PreparePassEpilogues(compile);
            CompiledGraph result = compile.Copy();
            StoreCompile(compile);
            return result;
        }

        internal bool TryUseCompile(bool asyncCompute, bool asyncCopy, [NotNullWhen(true)] out CompiledGraph? compile)
        {
            if (!HasCompile(asyncCompute, asyncCopy) || CompileResult is not { } result)
            {
                compile = null;
                return false;
            }

            Profiler.GraphCompileRecent();
            compile = UseCompile(result);
            return true;
        }

        internal bool HasCompile(bool asyncCompute, bool asyncCopy)
            => CompileResult != null
                && _graph._compileCompute == asyncCompute
                && _graph._compileCopy == asyncCopy
                && CompileSchema != null
                && CompileSchema.Equals(CurrentSchema());

        internal void SetCompile(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
            => SetCompile(compile, CurrentSchema(), asyncCompute, asyncCopy);

        internal void SetCompile(
            CompiledGraph compile,
            GraphSchema schema,
            bool asyncCompute,
            bool asyncCopy)
        {
            CompileResult = compile;
            CompileSchema = schema;
            _graph._compileCompute = asyncCompute;
            _graph._compileCopy = asyncCopy;
        }

        internal CompiledGraph UseCompile(CompiledGraph compile)
        {
            if (CompileWork == null)
            {
                CompileWork = compile.Materialize();
                return CompileWork;
            }

            CompileWork.UseShared(compile);
            return CompileWork;
        }

        internal void MarkGraphChanged()
        {
            _graph._schema = null;
            CompileResult = null;
            CompileSchema = null;
            CompileWork = null;
            _graph._graphRevision++;
            if (_graph._graphRevision > 0)
                return;

            _graph._graphRevision = 1;
            GraphCache.Clear();
            _graph._aliasCache.Clear();
        }

        internal GraphSchema CurrentSchema()
            => _graph._schema ?? throw new InvalidOperationException("RenderGraph schema has not been prepared.");

        internal CompiledGraph TakeCompile()
        {
            var compile = CompileScratch;
            if (compile == null)
                return new CompiledGraph(_graph._passes.Count, _graph._resources.Count);

            CompileScratch = null;
            return compile;
        }

        internal void StoreCompile(CompiledGraph compile)
        {
            if (CompileScratch == null)
                CompileScratch = compile;
        }

        private void CompileCurrentCore(bool asyncCompute = false, bool asyncCopy = false)
        {
            _graph.ThrowIfDisposed();
            _graph.ThrowIfActive(nameof(Compile));
            CompileGraph(asyncCompute, asyncCopy);
        }

        // --- Core compilation logic ---

        private void CullPasses(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
        {
            var scratch = _graph._scratch;
            scratch.BeginCull(_graph._resources.Count, _graph._passes.Count);
            bool[] neededResources = scratch.Needed;
            bool[] liveResources = scratch.LiveRes;
            MarkCullRoots(neededResources, liveResources);

            bool[] livePasses = scratch.LivePass;
            for (int passIndex = _graph._passes.Count - 1; passIndex >= 0; passIndex--)
            {
                bool passIsLive = _graph._passes[passIndex].SideEffect;
                string? keepReason = passIsLive ? "side effect" : null;
                var passUses = compile.Uses[passIndex];
                if (!passIsLive)
                {
                    for (int useIndex = 0; useIndex < passUses.Count; useIndex++)
                    {
                        var use = passUses[useIndex];
                        if (WritesNeededResource(neededResources, use))
                        {
                            passIsLive = true;
                            keepReason = $"writes needed resource '{_graph._resources[use.ResourceIndex].Name}'";
                            break;
                        }
                    }
                }

                if (!passIsLive)
                {
                    compile.PassCullReasons[passIndex] = CullReason(passUses);
                    continue;
                }

                livePasses[passIndex] = true;
                compile.PassKeepReasons[passIndex] = keepReason;
                compile.PassCullReasons[passIndex] = null;
                for (int useIndex = 0; useIndex < passUses.Count; useIndex++)
                {
                    var use = passUses[useIndex];
                    liveResources[use.ResourceIndex] = true;
                    bool reads = Reads(use.Access);
                    bool writes = Writes(use.Access);
                    if (writes && !reads)
                        ConsumeNeededResource(neededResources, use);
                    if (reads)
                        MarkNeededResource(neededResources, use.ResourceIndex, use.Range);
                }
            }

            for (int resourceIndex = 0; resourceIndex < _graph._resources.Count; resourceIndex++)
                compile.Resources[resourceIndex].Live = liveResources[resourceIndex];

            AddLivePasses(compile, livePasses);
            ValidateInitializedReads(compile);
            AnalyzeVersions(compile);
            StabilizePassQueues(compile, asyncCompute, asyncCopy);
        }

        private string CullReason(List<ResourceUse> uses)
        {
            if (uses.Count == 0)
                return "no side effect and no declared resource use";

            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                if (Writes(uses[useIndex].Access))
                    return "declared writes do not feed final state, extraction, side effect, or downstream dependency";
            }

            return "declared reads have no side effect or live downstream consumer";
        }

        private bool WritesNeededResource(bool[] neededResources, ResourceUse use)
        {
            if (!Writes(use.Access))
                return false;

            var resource = _graph._resources[use.ResourceIndex];
            if (resource.Kind == ResourceKind.Buffer)
                return neededResources[use.ResourceIndex];

            TextureDesc desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            var range = ActualRange(desc, use.Range);
            bool[] bits = _graph._scratch.EnsureTextureBits(
                use.ResourceIndex,
                checked((int)((ulong)desc.MipLevels * desc.ArraySize)));
            for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
            {
                for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                {
                    if (bits[TextureStateIndex(desc, mip, slice)])
                        return true;
                }
            }

            return false;
        }

        private void MarkNeededResource(bool[] neededResources, int resourceIndex, SubResourceRange range)
        {
            var resource = _graph._resources[resourceIndex];
            if (resource.Kind == ResourceKind.Buffer)
            {
                neededResources[resourceIndex] = true;
                return;
            }

            TextureDesc desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            MarkNeededTextureRange(resourceIndex, desc, ActualRange(desc, range));
        }

        private void ConsumeNeededResource(bool[] neededResources, ResourceUse use)
        {
            var resource = _graph._resources[use.ResourceIndex];
            if (resource.Kind == ResourceKind.Buffer)
            {
                neededResources[use.ResourceIndex] = false;
                return;
            }

            TextureDesc desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            var range = ActualRange(desc, use.Range);
            bool[] bits = _graph._scratch.EnsureTextureBits(
                use.ResourceIndex,
                checked((int)((ulong)desc.MipLevels * desc.ArraySize)));
            for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
            {
                for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                    bits[TextureStateIndex(desc, mip, slice)] = false;
            }
        }

        private void MarkNeededTextureRange(
            int resourceIndex,
            TextureDesc desc,
            SomeEngine.Rhi.SubresourceRange range)
        {
            bool[] bits = _graph._scratch.EnsureTextureBits(
                resourceIndex,
                checked((int)((ulong)desc.MipLevels * desc.ArraySize)));
            for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
            {
                for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                    bits[TextureStateIndex(desc, mip, slice)] = true;
            }
        }

        private void ValidateInitializedReads(CompiledGraph compile)
        {
            var scratch = _graph._scratch;
            scratch.BeginInitialization(_graph._resources.Count);
            bool[] buffers = scratch.Buffers;
            bool[][] textures = scratch.Textures;
            for (int resourceIndex = 0; resourceIndex < _graph._resources.Count; resourceIndex++)
            {
                if (!compile.Resources[resourceIndex].Live)
                    continue;

                var resource = _graph._resources[resourceIndex];
                if (resource.Kind == ResourceKind.Texture)
                {
                    if (resource.Imported)
                        continue;

                    var desc = resource.TextureDesc
                        ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
                    textures[resourceIndex] = scratch.TextureBits(
                        resourceIndex,
                        checked((int)((ulong)desc.MipLevels * desc.ArraySize)));
                    continue;
                }

                var bufferDesc = resource.BufferDesc
                    ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
                buffers[resourceIndex] = resource.Imported
                    || HasBufferInitialData(resource);
            }

            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                var uses = compile.Uses[passIndex];
                for (int useIndex = 0; useIndex < uses.Count; useIndex++)
                {
                    var use = uses[useIndex];
                    int resourceIndex = use.ResourceIndex;
                    var resource = _graph._resources[resourceIndex];
                    bool reads = Reads(use.Access);
                    bool writes = Writes(use.Access);

                    if (reads)
                    {
                        if (resource.Kind == ResourceKind.Buffer)
                        {
                            if (!buffers[resourceIndex])
                            {
                                throw new InvalidOperationException(
                                    $"RenderGraph pass '{_graph._passes[passIndex].Name}' reads transient resource '{resource.Name}' before any live pass initializes buffer.");
                            }
                        }
                        else
                        {
                            if (resource.Imported)
                                continue;

                            var desc = resource.TextureDesc
                                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
                            var actual = ActualRange(desc, use.Range);
                            var initialized = textures[resourceIndex]
                                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' initialization state was not prepared.");
                            for (uint slice = actual.FirstSlice; slice < actual.FirstSlice + actual.SliceCount; slice++)
                            {
                                for (uint mip = actual.FirstMip; mip < actual.FirstMip + actual.MipCount; mip++)
                                {
                                    if (!initialized[TextureStateIndex(desc, mip, slice)])
                                    {
                                        throw new InvalidOperationException(
                                            $"RenderGraph pass '{_graph._passes[passIndex].Name}' reads transient resource '{resource.Name}' before any live pass initializes {RangeText(use.Range)}.");
                                    }
                                }
                            }
                        }
                    }

                    if (!writes)
                        continue;

                    if (resource.Kind == ResourceKind.Buffer)
                    {
                        buffers[resourceIndex] = true;
                        continue;
                    }

                    var writeDesc = resource.TextureDesc
                        ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
                    if (resource.Imported)
                        continue;

                    var writeRange = ActualRange(writeDesc, use.Range);
                    var writeInitialized = textures[resourceIndex]
                        ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' initialization state was not prepared.");
                    for (uint slice = writeRange.FirstSlice; slice < writeRange.FirstSlice + writeRange.SliceCount; slice++)
                    {
                        for (uint mip = writeRange.FirstMip; mip < writeRange.FirstMip + writeRange.MipCount; mip++)
                            writeInitialized[TextureStateIndex(writeDesc, mip, slice)] = true;
                    }
                }
            }
        }

        private void AnalyzeLifetimes(CompiledGraph compile)
        {
            for (int resourceIndex = 0; resourceIndex < _graph._resources.Count; resourceIndex++)
            {
                compile.Resources[resourceIndex].FirstPass = -1;
                compile.Resources[resourceIndex].LastPass = -1;
                compile.Resources[resourceIndex].UseCount = 0;
            }

            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                var uses = compile.Uses[passIndex];
                for (int useIndex = 0; useIndex < uses.Count; useIndex++)
                {
                    var use = uses[useIndex];
                    int resourceIndex = use.ResourceIndex;
                    if (!compile.Resources[resourceIndex].Live)
                        continue;

                    if (compile.Resources[resourceIndex].FirstPass < 0)
                        compile.Resources[resourceIndex].FirstPass = passSlot;
                    compile.Resources[resourceIndex].LastPass = passSlot;
                    compile.Resources[resourceIndex].UseCount++;
                }
            }
        }

        private void AnalyzeVersions(CompiledGraph compile)
        {
            compile.Dependencies.Clear();
            var scratch = _graph._scratch;
            scratch.BeginDependencies(_graph._resources.Count);
            List<int>[] readers = scratch.Readers;
            for (int resourceIndex = 0; resourceIndex < _graph._resources.Count; resourceIndex++)
            {
                compile.Resources[resourceIndex].Version = 0;
                compile.Resources[resourceIndex].LastWriter = -1;
            }

            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                var uses = compile.Uses[passIndex];
                for (int useIndex = 0; useIndex < uses.Count; useIndex++)
                {
                    var use = uses[useIndex];
                    int resourceIndex = use.ResourceIndex;
                    var record = compile.Resources[resourceIndex];
                    bool reads = Reads(use.Access);
                    bool writes = Writes(use.Access);
                    int oldVersion = record.Version;
                    int previousWriter = record.LastWriter;
                    int inVersion = reads ? oldVersion : -1;
                    int sourcePass = reads && previousWriter != passIndex ? previousWriter : -1;
                    int outVersion = writes ? oldVersion + 1 : inVersion;
                    var resourceReaders = readers[resourceIndex];

                    if (reads && !writes)
                    {
                        if (previousWriter >= 0 && previousWriter != passIndex)
                            AddDependency(compile, previousWriter, passIndex, resourceIndex, oldVersion, oldVersion, Hazards.Raw);
                        if (resourceReaders.Count == 0 || resourceReaders[^1] != passIndex)
                            resourceReaders.Add(passIndex);
                    }

                    if (writes)
                    {
                        Hazards writeHazards = Hazards.Waw;
                        if (reads)
                            writeHazards |= Hazards.Raw;
                        if (previousWriter >= 0 && previousWriter != passIndex)
                            AddDependency(compile, previousWriter, passIndex, resourceIndex, oldVersion, outVersion, writeHazards);

                        for (int readerIndex = 0; readerIndex < resourceReaders.Count; readerIndex++)
                        {
                            int readerPass = resourceReaders[readerIndex];
                            if (readerPass != passIndex)
                                AddDependency(compile, readerPass, passIndex, resourceIndex, oldVersion, outVersion, Hazards.War);
                        }

                        resourceReaders.Clear();
                        record.Version = outVersion;
                        record.LastWriter = passIndex;
                        compile.Resources[resourceIndex] = record;
                    }

                    uses[useIndex] = use with
                    {
                        InVersion = inVersion,
                        OutVersion = outVersion,
                        SourcePass = sourcePass,
                    };
                }
            }
        }

        private void AddDependency(
            CompiledGraph compile,
            int sourcePass,
            int targetPass,
            int resourceIndex,
            int inVersion,
            int outVersion,
            Hazards hazards)
        {
            if (sourcePass < 0 || sourcePass == targetPass || hazards == Hazards.None)
                return;

            var key = new EdgeKey(sourcePass, targetPass, resourceIndex, inVersion, outVersion);
            if (_graph._scratch.EdgeMap.TryGetValue(key, out int dependencyIndex))
            {
                var dependency = compile.Dependencies[dependencyIndex];
                compile.Dependencies[dependencyIndex] = dependency with { Hazards = dependency.Hazards | hazards };
                return;
            }

            _graph._scratch.EdgeMap.Add(key, compile.Dependencies.Count);
            compile.Dependencies.Add(new PassDependency(sourcePass, targetPass, resourceIndex, inVersion, outVersion, hazards));
        }

        private void StabilizePassQueues(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
        {
            int maxIterations = Math.Max(1, compile.Count + 1);
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                AnalyzeLifetimes(compile);
                _graph.GatherResources(compile);
                _graph.GatherBarriers(compile);
                bool queueChanged = RefreshPassQueues(compile, asyncCompute, asyncCopy);
                bool reordered = SortPasses(compile);
                if (reordered)
                    AnalyzeVersions(compile);
                if (!queueChanged && !reordered)
                    return;
            }

            throw new InvalidOperationException(
                "RenderGraph pass queue assignment did not stabilize for the current graph order.");
        }

        private bool RefreshPassQueues(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
        {
            bool changed = false;
            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                QueueType queue = ModeQueue(_graph._passes[passIndex].Mode, compile.Transitions[passIndex], asyncCompute, asyncCopy);
                if (compile.PassQueues[passIndex] == queue)
                    continue;

                compile.PassQueues[passIndex] = queue;
                changed = true;
            }

            return changed;
        }

        private bool SortPasses(CompiledGraph compile)
        {
            int passCount = compile.Count;
            bool queueAware = HasNonGraphicsQueue(compile);
            if (passCount < 2 || (compile.Dependencies.Count == 0 && !queueAware))
                return false;

            var scratch = _graph._scratch;
            scratch.BeginTopology(passCount);
            int[] indegrees = scratch.Indegrees;
            for (int dependencyIndex = 0; dependencyIndex < compile.Dependencies.Count; dependencyIndex++)
            {
                var dependency = compile.Dependencies[dependencyIndex];
                int sourceSlot = compile.SlotOf(dependency.SourcePass);
                if (sourceSlot < 0)
                    throw new InvalidOperationException("RenderGraph pass dependency references a culled source pass.");
                int targetSlot = compile.SlotOf(dependency.TargetPass);
                if (targetSlot < 0)
                    throw new InvalidOperationException("RenderGraph pass dependency references a culled target pass.");
                indegrees[targetSlot]++;
                scratch.Edges[sourceSlot].Add(targetSlot);
            }

            for (int passSlot = 0; passSlot < passCount; passSlot++)
            {
                if (indegrees[passSlot] == 0)
                    scratch.Ready.Add(passSlot);
            }

            int[] order = scratch.Order;
            QueueType? currentQueue = null;
            for (int orderIndex = 0; orderIndex < passCount; orderIndex++)
            {
                if (scratch.Ready.Count == 0)
                    throw new InvalidOperationException("RenderGraph pass dependencies contain a cycle.");

                int passSlot = PickPass(compile, currentQueue);
                scratch.Ready.Remove(passSlot);
                int passIndex = compile.Passes[passSlot];
                order[orderIndex] = passIndex;
                currentQueue = compile.PassQueues[passIndex];

                var edges = scratch.Edges[passSlot];
                for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
                {
                    int targetSlot = edges[edgeIndex];
                    indegrees[targetSlot]--;
                    if (indegrees[targetSlot] == 0)
                        scratch.Ready.Add(targetSlot);
                }
            }

            bool reordered = false;
            for (int passSlot = 0; passSlot < passCount; passSlot++)
            {
                if (order[passSlot] != compile.Passes[passSlot])
                {
                    reordered = true;
                    break;
                }
            }

            if (!reordered)
                return false;

            compile.SetOrder(order.AsSpan(0, passCount));
            return true;
        }

        private static bool HasNonGraphicsQueue(CompiledGraph compile)
        {
            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                if (compile.PassQueues[passIndex] != QueueType.Graphics)
                    return true;
            }

            return false;
        }

        private int PickPass(CompiledGraph compile, QueueType? currentQueue)
        {
            if (!currentQueue.HasValue)
                return PickFirstPass(compile);

            var scratch = _graph._scratch;
            int firstSlot = scratch.Ready.Min;
            int effectSlot = FirstEffect(compile);
            int lastSlot = firstSlot < effectSlot
                ? effectSlot - 1
                : effectSlot;
            foreach (int passSlot in scratch.Ready)
            {
                if (passSlot > lastSlot)
                    break;

                int passIndex = compile.Passes[passSlot];
                if (compile.PassQueues[passIndex] == currentQueue.Value)
                    return passSlot;
            }

            return firstSlot;
        }

        private int PickFirstPass(CompiledGraph compile)
        {
            var scratch = _graph._scratch;
            int firstSlot = scratch.Ready.Min;
            int effectSlot = FirstEffect(compile);
            int lastSlot = firstSlot < effectSlot
                ? effectSlot - 1
                : effectSlot;
            foreach (int passSlot in scratch.Ready)
            {
                if (passSlot > lastSlot)
                    break;

                int passIndex = compile.Passes[passSlot];
                if (compile.PassQueues[passIndex] != QueueType.Graphics)
                    return passSlot;
            }

            return firstSlot;
        }

        private int FirstEffect(CompiledGraph compile)
        {
            var scratch = _graph._scratch;
            foreach (int passSlot in scratch.Ready)
            {
                int passIndex = compile.Passes[passSlot];
                if (_graph._passes[passIndex].SideEffect)
                    return passSlot;
            }

            return int.MaxValue;
        }

        private void MarkCullRoots(bool[] neededResources, bool[] liveResources)
        {
            for (int i = 0; i < _graph._finalStates.Count; i++)
            {
                int resourceIndex = _graph._finalStates[i].ResourceIndex;
                MarkNeededResource(neededResources, resourceIndex, SubResourceRange.All);
                liveResources[resourceIndex] = true;
            }
        }

        private void AddLivePasses(CompiledGraph compile, bool[] livePasses)
        {
            for (int passIndex = 0; passIndex < _graph._passes.Count; passIndex++)
            {
                if (!livePasses[passIndex])
                    continue;

                compile.LivePasses[passIndex] = true;
                compile.PassSlots[passIndex] = compile.Passes.Count;
                compile.Passes.Add(passIndex);
            }
        }

        private void BuildBatches(CompiledGraph compile)
        {
            compile.QueueBatches.Clear();
            compile.QueueLinks.Clear();
            compile.Queues = QueueMask.None;
            for (int passSlot = 0; passSlot < compile.Count;)
            {
                int startSlot = passSlot;
                int passIndex = compile.Passes[passSlot];
                PassMode mode = _graph._passes[passIndex].Mode;
                QueueType queue = compile.PassQueues[passIndex];
                passSlot++;
                bool mixed = false;
                while (passSlot < compile.Count)
                {
                    int nextPassIndex = compile.Passes[passSlot];
                    PassMode nextMode = _graph._passes[nextPassIndex].Mode;
                    if (compile.PassQueues[nextPassIndex] != queue)
                        break;

                    mixed |= nextMode != mode;
                    passSlot++;
                }

                var kind = mixed ? QueueBatchKind.Mixed : ModeKind(mode);
                compile.Queues |= RenderGraph.QueueBit(queue);
                compile.QueueBatches.Add(new QueueBatch(
                    startSlot,
                    passSlot - startSlot,
                    kind,
                    queue,
                    0,
                    0,
                    QueueMask.None,
                    QueueMask.None));
            }

            BuildQueueLinks(compile);
        }

        private QueueType ModeQueue(
            PassMode mode,
            TransitionBatch transitions,
            bool asyncCompute,
            bool asyncCopy)
        {
            if (mode is PassMode.Copy or PassMode.AsyncCopy)
            {
                if (asyncCopy)
                {
                    if (transitions.HasChecks)
                        return QueueType.Graphics;

                    IReadOnlyList<ResourceTransition> transitionSource = transitions.TransitionSource;
                    for (int transitionIndex = 0; transitionIndex < transitionSource.Count; transitionIndex++)
                    {
                        ResourceTransition transition = transitionSource[transitionIndex];
                        if (!IsCopyState(transition.Before) || !IsCopyState(transition.After))
                            return QueueType.Graphics;
                    }

                    return QueueType.Copy;
                }

                if (asyncCompute && CanComputeTransitions(transitions))
                    return QueueType.Compute;

                return QueueType.Graphics;
            }

            if ((mode is PassMode.Compute or PassMode.AsyncCompute) && asyncCompute)
            {
                if (CanComputeTransitions(transitions))
                    return QueueType.Compute;
            }

            return QueueType.Graphics;
        }

        private bool CanComputeTransitions(TransitionBatch transitions)
        {
            IReadOnlyList<ResourceTransition> transitionSource = transitions.TransitionSource;
            for (int transitionIndex = 0; transitionIndex < transitionSource.Count; transitionIndex++)
            {
                ResourceTransition transition = transitionSource[transitionIndex];
                if (!IsComputeState(transition.Before) || !IsComputeState(transition.After))
                    return false;
            }

            for (int checkIndex = 0; checkIndex < transitions.CheckResources.Count; checkIndex++)
            {
                Resource resource = _graph._resources[transitions.CheckResources[checkIndex]];
                if (resource.Kind == ResourceKind.Texture)
                {
                    TextureDesc? desc = resource.TextureDesc;
                    if (desc != null
                        && (desc.BindFlags & (BindFlags.RenderTarget | BindFlags.DepthStencil)) != 0)
                    {
                        return false;
                    }
                }
                else
                {
                    BufferDesc? desc = resource.BufferDesc;
                    if (desc != null
                        && (desc.BindFlags & (BindFlags.VertexBuffer | BindFlags.IndexBuffer | BindFlags.ConstantBuffer)) != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static QueueBatchKind ModeKind(PassMode mode)
            => mode switch
            {
                PassMode.Compute or PassMode.AsyncCompute => QueueBatchKind.Compute,
                PassMode.Copy or PassMode.AsyncCopy => QueueBatchKind.Copy,
                _ => QueueBatchKind.Command,
            };

        private void BuildQueueLinks(CompiledGraph compile)
        {
            compile.PrepareQueueWaits();
            if (compile.Dependencies.Count == 0 || compile.QueueBatches.Count < 2)
                return;

            var scratch = _graph._scratch;
            scratch.BeginQueues(_graph._passes.Count);
            int[] passBatches = scratch.Batches;
            for (int batchIndex = 0; batchIndex < compile.QueueBatches.Count; batchIndex++)
            {
                var batch = compile.QueueBatches[batchIndex];
                int endSlot = batch.StartSlot + batch.Count;
                for (int passSlot = batch.StartSlot; passSlot < endSlot; passSlot++)
                    passBatches[compile.Passes[passSlot]] = batchIndex;
            }

            for (int dependencyIndex = 0; dependencyIndex < compile.Dependencies.Count; dependencyIndex++)
            {
                var dependency = compile.Dependencies[dependencyIndex];
                int sourceBatch = passBatches[dependency.SourcePass];
                int targetBatch = passBatches[dependency.TargetPass];
                if (sourceBatch < 0 || targetBatch < 0 || sourceBatch == targetBatch)
                    continue;

                var source = compile.QueueBatches[sourceBatch];
                var target = compile.QueueBatches[targetBatch];
                if (source.Queue == target.Queue)
                    continue;
                var key = new QueueLinkKey(sourceBatch, targetBatch);
                if (scratch.QueueLinks.TryGetValue(key, out int linkIndex))
                {
                    var link = compile.QueueLinks[linkIndex];
                    compile.QueueLinks[linkIndex] = link with
                    {
                        DependencyCount = checked(link.DependencyCount + 1),
                        Hazards = link.Hazards | dependency.Hazards,
                    };
                }
                else
                {
                    linkIndex = compile.QueueLinks.Count;
                    scratch.QueueLinks.Add(key, linkIndex);
                    var link = new QueueLink(
                        sourceBatch,
                        targetBatch,
                        source.Queue,
                        target.Queue,
                        1,
                        dependency.Hazards);
                    compile.QueueLinks.Add(link);
                    compile.QueueWaits[targetBatch].Add(linkIndex);
                    QueueBatch sourceData = compile.QueueBatches[sourceBatch];
                    compile.QueueBatches[sourceBatch] = sourceData with
                    {
                        OutputLinks = checked(sourceData.OutputLinks + 1),
                        SignalQueues = sourceData.SignalQueues | RenderGraph.QueueBit(target.Queue),
                    };
                    QueueBatch targetData = compile.QueueBatches[targetBatch];
                    compile.QueueBatches[targetBatch] = targetData with
                    {
                        InputLinks = checked(targetData.InputLinks + 1),
                        WaitQueues = targetData.WaitQueues | RenderGraph.QueueBit(source.Queue),
                    };
                }
            }
        }

        private void ApplyQueueLifetimePadding(CompiledGraph compile)
        {
            int batchCount = compile.QueueBatches.Count;
            if (batchCount < 2 || !HasNonGraphicsQueue(compile))
                return;

            bool[,] ordered = OrderedQueueBatches(compile);
            int[] slotBatches = new int[compile.Count];
            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                QueueBatch batch = compile.QueueBatches[batchIndex];
                int endSlot = batch.StartSlot + batch.Count;
                for (int passSlot = batch.StartSlot; passSlot < endSlot; passSlot++)
                    slotBatches[passSlot] = batchIndex;
            }

            bool[] resourceBatches = new bool[batchCount];
            for (int resourceIndex = 0; resourceIndex < compile.Resources.Length; resourceIndex++)
            {
                var record = compile.Resources[resourceIndex];
                if (!record.Live || record.FirstPass < 0)
                    continue;

                Array.Clear(resourceBatches);
                bool used = false;
                for (int passSlot = record.FirstPass; passSlot <= record.LastPass; passSlot++)
                {
                    int passIndex = compile.Passes[passSlot];
                    var uses = compile.Uses[passIndex];
                    for (int useIndex = 0; useIndex < uses.Count; useIndex++)
                    {
                        if (uses[useIndex].ResourceIndex != resourceIndex)
                            continue;

                        resourceBatches[slotBatches[passSlot]] = true;
                        used = true;
                        break;
                    }
                }

                if (!used)
                    continue;

                int firstPass = record.FirstPass;
                int lastPass = record.LastPass;
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    if (!resourceBatches[batchIndex])
                        continue;

                    for (int otherBatchIndex = 0; otherBatchIndex < batchCount; otherBatchIndex++)
                    {
                        if (batchIndex == otherBatchIndex
                            || ordered[batchIndex, otherBatchIndex]
                            || ordered[otherBatchIndex, batchIndex])
                        {
                            continue;
                        }

                        QueueBatch other = compile.QueueBatches[otherBatchIndex];
                        firstPass = Math.Min(firstPass, other.StartSlot);
                        lastPass = Math.Max(lastPass, checked(other.StartSlot + other.Count - 1));
                    }
                }

                if (firstPass != record.FirstPass || lastPass != record.LastPass)
                    compile.Resources[resourceIndex] = record with { FirstPass = firstPass, LastPass = lastPass };
            }
        }

        private static bool[,] OrderedQueueBatches(CompiledGraph compile)
        {
            int batchCount = compile.QueueBatches.Count;
            var ordered = new bool[batchCount, batchCount];
            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                ordered[batchIndex, batchIndex] = true;

            for (int sourceBatch = 0; sourceBatch < batchCount; sourceBatch++)
            {
                QueueBatch source = compile.QueueBatches[sourceBatch];
                for (int targetBatch = sourceBatch + 1; targetBatch < batchCount; targetBatch++)
                {
                    if (compile.QueueBatches[targetBatch].Queue == source.Queue)
                    {
                        ordered[sourceBatch, targetBatch] = true;
                        break;
                    }
                }
            }

            for (int linkIndex = 0; linkIndex < compile.QueueLinks.Count; linkIndex++)
            {
                QueueLink link = compile.QueueLinks[linkIndex];
                ordered[link.SourceBatch, link.TargetBatch] = true;
            }

            for (int via = 0; via < batchCount; via++)
            {
                for (int source = 0; source < batchCount; source++)
                {
                    if (!ordered[source, via])
                        continue;

                    for (int target = 0; target < batchCount; target++)
                        ordered[source, target] |= ordered[via, target];
                }
            }

            return ordered;
        }

        internal void BuildFrameSync(CompiledGraph compile)
        {
            compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
            compile.BatchSignal = CanBatchSignal(compile, compile.FinalWork);
            compile.FrameQueue = compile.FinalWork
                ? FinalQueue(compile)
                : compile.QueueBatches.Count == 0
                    ? QueueType.Graphics
                    : compile.QueueBatches[^1].Queue;
            compile.FrameWaits = compile.BatchSignal
                ? QueueMask.None
                : compile.Queues & ~RenderGraph.QueueBit(compile.FrameQueue);
        }

        private void PreparePassEpilogues(CompiledGraph compile)
        {
            for (int passIndex = 0; passIndex < compile.Uses.Length; passIndex++)
            {
                List<ResourceUse> uses = compile.Uses[passIndex];
                if (uses.Count == 0)
                {
                    compile.PassEpilogues[passIndex] = [];
                    continue;
                }

                var epilogue = new List<PassEpilogueState>(uses.Count);
                Dictionary<int, List<ResourceUse>>? bufferUses = null;
                List<int>? bufferOrder = null;
                for (int useIndex = 0; useIndex < uses.Count; useIndex++)
                {
                    ResourceUse use = uses[useIndex];
                    if (_graph._resources[use.ResourceIndex].Kind == ResourceKind.Buffer)
                    {
                        bufferUses ??= [];
                        bufferOrder ??= [];
                        if (!bufferUses.TryGetValue(use.ResourceIndex, out List<ResourceUse>? resourceUses))
                        {
                            resourceUses = [];
                            bufferUses.Add(use.ResourceIndex, resourceUses);
                            bufferOrder.Add(use.ResourceIndex);
                        }

                        resourceUses.Add(use);
                        continue;
                    }

                    epilogue.Add(new PassEpilogueState(
                        use.ResourceIndex,
                        use.EffectiveBarrierEntryState,
                        use.EffectiveBarrierExitState,
                        use.Access,
                        use.Range));
                }

                if (bufferUses != null && bufferOrder != null)
                {
                    for (int bufferIndex = 0; bufferIndex < bufferOrder.Count; bufferIndex++)
                    {
                        int resourceIndex = bufferOrder[bufferIndex];
                        List<ResourceUse> resourceUses = bufferUses[resourceIndex];
                        ResourceUse firstUse = resourceUses[0];
                        ResourceState initialState = firstUse.EffectiveBarrierEntryState;
                        ResourceState currentState = initialState;
                        RenderGraphAccess access = RenderGraphAccess.None;
                        for (int useIndex = 0; useIndex < resourceUses.Count; useIndex++)
                        {
                            ResourceUse use = resourceUses[useIndex];
                            ResourceState entryState = use.EffectiveBarrierEntryState;
                            ResourceState requestedExitState = use.EffectiveBarrierExitState;
                            ResourceState exitState = currentState == requestedExitState
                                ? currentState
                                : requestedExitState;
                            currentState = exitState;
                            if (exitState != ResourceState.UnorderedAccess)
                                access = RenderGraphAccess.None;
                            if (entryState == ResourceState.UnorderedAccess
                                && exitState == ResourceState.UnorderedAccess)
                                access |= use.Access;
                        }

                        epilogue.Add(new PassEpilogueState(
                            resourceIndex,
                            initialState,
                            currentState,
                            access,
                            SubResourceRange.All));
                    }
                }

                compile.PassEpilogues[passIndex] = [.. epilogue];
            }
        }
    }
}
