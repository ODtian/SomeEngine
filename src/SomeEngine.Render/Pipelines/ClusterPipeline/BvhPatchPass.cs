using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using System.Runtime.InteropServices;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class BvhPatchPass
{
    private const string EntryPoint = "main";
    private const string GlobalBVHResource = "GlobalBVH";
    private const string UniformsResource = "Uniforms";
    private const string PatchesResource = "Patches";

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _layout;
    private readonly UniformGpu<PatchUniforms> _uniformGpu;
    private PipelineTicket _pipeline;
    private bool _disposed;

    public BvhPatchPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster BVH patch pass requires an initialized render context.");
        _layout = ShaderBindings.Create(
            device,
            "Cluster BVH Patch",
            shader,
            [GlobalBVHResource, UniformsResource, PatchesResource],
            EntryPoint);
        _pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            EntryPoint,
            "Cluster BVH Patch",
            source: ClusterSources.Builtins);
        _uniformGpu = new UniformGpu<PatchUniforms>(device);
    }

    private const string PassName = "Cluster BVH Patch";

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_pipeline);
    }

    public bool AddTo(
        RenderGraph graph,
        RenderGraphHandle globalBVH,
        IReadOnlyList<ClusterBvhPatch> patches)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(patches);
        if (!globalBVH.IsValid)
            throw new ArgumentException("BVH patch requires a valid GlobalBVH handle.", nameof(globalBVH));
        if (patches.Count == 0)
            return false;

        var patchArray = new ClusterBvhPatch[patches.Count];
        for (int i = 0; i < patchArray.Length; i++)
            patchArray[i] = patches[i];
        ReadOnlySpan<byte> patchBytes = MemoryMarshal.AsBytes<ClusterBvhPatch>(patchArray);
        var uniforms = new PatchUniforms((uint)patchArray.Length, 0, 0, 0);

        RenderGraphHandle patchBuffer = graph.CreateBuffer(
            "Cluster BVH Patches",
            new BufferDesc
            {
                Name = "Cluster BVH Patches",
                SizeInBytes = checked((ulong)patchBytes.Length),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 8,
            },
            patchBytes);
        RenderGraphHandle uniformBuffer = _uniformGpu.Add(graph, [uniforms], "Cluster BVH Patch Uniforms").Buffer;
        int patchCount = patchArray.Length;
        graph.AddComputePass<PassData>(
            PassName,
            (builder, data) =>
            {
                data.GlobalBVH = globalBVH;
                data.Patches = patchBuffer;
                data.Uniforms = uniformBuffer;
                data.PatchCount = patchCount;
                if (!data.GlobalBVH.IsValid || !data.Patches.IsValid || !data.Uniforms.IsValid || data.PatchCount <= 0)
                    throw new InvalidOperationException("BVH patch pass requires valid GlobalBVH, patch buffer, and uniforms.");

                builder.Use(_layout.GetRequired(GlobalBVHResource), data.GlobalBVH);
                builder.Use(_layout.GetRequired(PatchesResource), data.Patches);
                builder.Use(_layout.GetRequired(UniformsResource), data.Uniforms);
            },
            (context, pass, data) =>
            {
                ThrowIfDisposed();
                ReflectedBinding globalBvh = _layout.GetRequired(GlobalBVHResource);
                ReflectedBinding uniformsBinding = _layout.GetRequired(UniformsResource);
                ReflectedBinding patchesBinding = _layout.GetRequired(PatchesResource);
                uint set = globalBvh.Set;
                if (uniformsBinding.Set != set || patchesBinding.Set != set)
                    throw new InvalidOperationException("BVH patch currently requires all resources in one binding set.");

                pass.SetPipeline(context.GetPipeline(_pipeline));
                pass.SetParameters(
                    set,
                    context.Bindings(_layout.Layout(set))
                        .Buffer(globalBvh, data.GlobalBVH)
                        .Buffer(uniformsBinding, data.Uniforms)
                        .Buffer(patchesBinding, data.Patches));
                pass.Dispatch(checked((uint)((data.PatchCount + 63) / 64)), 1, 1);
            });
        return true;
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        store.ReleaseIdle(ref _pipeline);
        ShaderBindings.Destroy(_renderContext.GraphicsDevice, _layout);
        _uniformGpu.Dispose();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PatchUniforms(
        uint PatchCount,
        uint Pad0,
        uint Pad1,
        uint Pad2);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BvhPatchPass));
    }

    private sealed class PassData
    {
        public RenderGraphHandle GlobalBVH;
        public RenderGraphHandle Patches;
        public RenderGraphHandle Uniforms;
        public int PatchCount;
    }
}


