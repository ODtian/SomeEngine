using SomeEngine.Core.Collections;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    internal sealed class RenderGraphExecutor
    {
        private readonly RenderGraph _graph;
        internal readonly System.Threading.Lock ViewCacheGate = new();
        internal readonly System.Threading.Lock BindingSetGate = new();
        internal readonly System.Threading.Lock FrameUseGate = new();
        internal readonly List<TextureViewHandle> OwnedTextureViews = [];
        internal readonly List<BufferViewHandle> OwnedBufferViews = [];
        internal readonly FlatDictionary<TexturePoolKey, Stack<PooledTexture>> TexturePool = new();
        internal readonly FlatDictionary<BufferPoolKey, Stack<PooledBuffer>> BufferPool = new();
        internal readonly FlatDictionary<PlacedTexturePoolKey, Stack<PooledTexture>> PlacedTexturePool = new();
        internal readonly FlatDictionary<PlacedBufferPoolKey, Stack<PooledBuffer>> PlacedBufferPool = new();
        internal readonly FrameUploadBuffer FrameUploadBuffer = new();
        internal readonly FlatDictionary<AliasHeapPoolKey, Stack<MemoryHeapHandle>> AliasHeapPool = new();
        internal readonly List<AliasHeap> AliasHeaps = [];
        internal readonly FlatDictionary<int, List<BindingSetEntry>> BindingSetOwner = new();
        internal readonly BindingIndex BindingIndex = new();
        internal readonly BindingCache BindingCache = new();
        internal readonly Dictionary<TextureViewHandle, RegisteredTextureView> TextureViewBindings = [];
        internal readonly Dictionary<BufferViewHandle, RegisteredBufferView> BufferViewBindings = [];
        internal readonly HashSet<TextureViewHandle> FrameExternalTextureViews = [];
        internal readonly HashSet<BufferViewHandle> FrameExternalBufferViews = [];
        internal readonly HashSet<BindingSetHandle> FrameTransientBindingSets = [];
        internal readonly HashSet<BindingSetHandle> FrameBindingSets = [];
        internal readonly HashSet<PipelineHandle> FramePipelines = [];
        internal readonly FlatDictionary<BindingSetHandle, ulong> BindingSetLastUse = new();
        internal readonly List<(BindingSetHandle Handle, ulong FenceValue)> DeferredBindingSetDestroys = [];
        internal readonly List<PendingFrameResources> PendingFrames = [];
        internal readonly Stack<PendingFrameResources> PendingFramePool = [];
        internal readonly List<DeviceTimestamps> TimestampPool = [];
        internal readonly List<CommandBufferHandle> CommandBuffers = [];
        internal readonly FenceHandle[] QueueFences = new FenceHandle[QueueCount];
        internal readonly ulong[] QueueFenceValues = new ulong[QueueCount];
        internal FenceHandle FrameFence;
        internal ulong FrameFenceValue;
        internal bool CurrentFrameSubmitted;
        internal PipelineCache? PipelineCache;

        public RenderGraphExecutor(RenderGraph graph)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        }

        public void Execute(IDevice device, IQueue queue, ISwapchain? swapchain = null, uint syncInterval = 1)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(queue);
            Execute(device, new GraphQueues(device, queue), swapchain, syncInterval);
        }

        public void Execute(GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1)
            => _graph.ExecuteCore(queues.Device, queues, swapchain, syncInterval);

        public void Execute(IDevice device, GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1)
            => _graph.ExecuteCore(device, queues, swapchain, syncInterval);
    }
}
