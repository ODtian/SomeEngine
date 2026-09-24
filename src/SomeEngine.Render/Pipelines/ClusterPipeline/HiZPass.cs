using SomeEngine.Render.Graph;
using System.Numerics;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct HiZOutput(
    RenderGraphHandle Texture,
    uint MipCount,
    Vector2 InvSize);

internal sealed class HiZPass
{
    private const string BuildMip0EntryPoint = "BuildMip0";
    private const string BuildMip0And1EntryPoint = "BuildMip0And1";
    private const string DownsampleTwoEntryPoint = "DownsampleTwoMips";

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _layout;
    private readonly ReflectedBinding _depth;
    private readonly ReflectedBinding _hiZMip0;
    private readonly ReflectedBinding _hiZMip1;
    private readonly ReflectedBinding _sourceMip;
    private readonly ReflectedBinding _destinationMip1;
    private readonly ReflectedBinding _destinationMip2;
    private readonly uint _buildSet;
    private readonly BindingLayoutHandle _buildLayout;
    private PipelineTicket _buildMip0Pipeline;
    private PipelineTicket _buildMip0And1Pipeline;
    private PipelineTicket _downsampleTwoPipeline;
    private bool _disposed;

    public HiZPass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster HiZ build pass requires an initialized render context.");
        _layout = ShaderBindings.Create(
            device,
            "HiZ Build Chain",
            shader,
            ["DepthTexture", "HiZMip0", "HiZMip1", "SrcMip", "DstMip1", "DstMip2"],
            BuildMip0EntryPoint,
            BuildMip0And1EntryPoint,
            DownsampleTwoEntryPoint);
        _depth = _layout.GetRequired("DepthTexture");
        _hiZMip0 = _layout.GetRequired("HiZMip0");
        _hiZMip1 = _layout.GetRequired("HiZMip1");
        _sourceMip = _layout.GetRequired("SrcMip");
        _destinationMip1 = _layout.GetRequired("DstMip1");
        _destinationMip2 = _layout.GetRequired("DstMip2");
        _buildSet = BindInput.GetSetIndex(
            "HiZ build",
            _depth,
            _hiZMip0,
            _hiZMip1,
            _sourceMip,
            _destinationMip1,
            _destinationMip2);
        _buildLayout = _layout.Layout(_buildSet);
        _buildMip0Pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            BuildMip0EntryPoint,
            "HiZ Build Mip0",
            source: ClusterSources.Builtins);
        _buildMip0And1Pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            BuildMip0And1EntryPoint,
            "HiZ Build Mip0+1",
            source: ClusterSources.Builtins);
        _downsampleTwoPipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            DownsampleTwoEntryPoint,
            "HiZ Downsample Two Mips",
            source: ClusterSources.Builtins);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_buildMip0Pipeline);
        tickets.Add(_buildMip0And1Pipeline);
        tickets.Add(_downsampleTwoPipeline);
    }

    public HiZOutput AddPasses(
        RenderGraph graph,
        RenderGraphHandle depthTarget,
        uint width,
        uint height,
        RenderGraphHandle outputTexture = default,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!depthTarget.IsValid)
            throw new ArgumentException("HiZ build requires a valid depth target.", nameof(depthTarget));
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "HiZ dimensions must be non-zero.");

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        uint mipCount = CalculateMipCount(width, height);
        var hiZ = outputTexture.IsValid
            ? outputTexture
            : graph.CreateTexture(
                $"{prefix}HiZ",
                new TextureDesc
                {
                    Name = $"{prefix}HiZ",
                    Dimension = ResourceDimension.Texture2D,
                    Width = width,
                    Height = height,
                    MipLevels = mipCount,
                    Format = Format.R32Float,
                    BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                    InitialState = ResourceState.Undefined,
                });

        ValidateHiZ(graph.GetTextureDesc(hiZ), width, height, mipCount);
        AddBuildPass(graph, depthTarget, hiZ, width, height, mipCount, prefix);

        return new HiZOutput(
            hiZ,
            mipCount,
            new Vector2(1.0f / width, 1.0f / height));
    }

    public static uint CalculateMipCount(uint width, uint height)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "HiZ dimensions must be non-zero.");

        uint maxDimension = Math.Max(width, height);
        uint levels = 1;
        while (maxDimension > 1)
        {
            maxDimension >>= 1;
            levels++;
        }

        return levels;
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        IDevice? device = _renderContext.GraphicsDevice;
        store.ReleaseIdle(ref _downsampleTwoPipeline);
        store.ReleaseIdle(ref _buildMip0And1Pipeline);
        store.ReleaseIdle(ref _buildMip0Pipeline);
        ShaderBindings.Destroy(device, _layout);
        _disposed = true;
    }

    private void AddBuildPass(
        RenderGraph graph,
        RenderGraphHandle depthTarget,
        RenderGraphHandle hiZ,
        uint width,
        uint height,
        uint mipCount,
        string prefix)
    {
        graph.AddComputePass(
            $"{prefix}HiZ Build Chain",
            builder =>
            {
                builder.Read(depthTarget, ResourceState.ShaderResource);
                builder.ReadWrite(hiZ, ResourceState.UnorderedAccess, SubResourceRange.MipRange(0, mipCount));
            },
            (context, pass) =>
            {
                if (mipCount == 1)
                {
                    BuildMipZero(context, pass, depthTarget, hiZ, width, height);
                    return;
                }

                uint sourceMip;
                if ((mipCount & 1u) == 0)
                {
                    BuildBaseMips(context, pass, depthTarget, hiZ, width, height);
                    sourceMip = 1;
                }
                else
                {
                    BuildMipZero(context, pass, depthTarget, hiZ, width, height);
                    sourceMip = 0;
                }

                while (sourceMip + 2 < mipCount)
                {
                    AddMipBarrier(pass, hiZ, sourceMip);
                    BuildTwoMips(
                        context,
                        pass,
                        depthTarget,
                        hiZ,
                        sourceMip,
                        width,
                        height);
                    sourceMip += 2;
                }
            });
    }

    private void BuildMipZero(
        RenderGraphContext context,
        IComputeCommands pass,
        RenderGraphHandle depthTarget,
        RenderGraphHandle hiZ,
        uint width,
        uint height)
    {
        var mip0View = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: 0, mipCount: 1);
        pass.SetPipeline(context.GetPipeline(_buildMip0Pipeline));
        pass.SetParameters(
            _buildSet,
            context.Bindings(_buildLayout)
                .Reserve(6)
                .Texture(
                    _depth,
                    context.GetTextureView(depthTarget, ViewKind.ShaderResource, Format.D32Float))
                .Texture(_hiZMip0, mip0View)
                .Texture(_hiZMip1, mip0View)
                .Texture(_sourceMip, mip0View)
                .Texture(_destinationMip1, mip0View)
                .Texture(_destinationMip2, mip0View));
        pass.Dispatch(
            DispatchCount(width),
            DispatchCount(height),
            1);
    }

    private void BuildBaseMips(
        RenderGraphContext context,
        IComputeCommands pass,
        RenderGraphHandle depthTarget,
        RenderGraphHandle hiZ,
        uint width,
        uint height)
    {
        uint mip1Width = MipSize(width, 1);
        uint mip1Height = MipSize(height, 1);
        var mip0View = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: 0, mipCount: 1);
        var mip1View = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: 1, mipCount: 1);
        pass.SetPipeline(context.GetPipeline(_buildMip0And1Pipeline));
        pass.SetParameters(
            _buildSet,
            context.Bindings(_buildLayout)
                .Reserve(6)
                .Texture(
                    _depth,
                    context.GetTextureView(depthTarget, ViewKind.ShaderResource, Format.D32Float))
                .Texture(_hiZMip0, mip0View)
                .Texture(_hiZMip1, mip1View)
                .Texture(_sourceMip, mip0View)
                .Texture(_destinationMip1, mip0View)
                .Texture(_destinationMip2, mip1View));
        pass.Dispatch(
            DispatchCount(mip1Width),
            DispatchCount(mip1Height),
            1);
    }

    private void BuildTwoMips(
        RenderGraphContext context,
        IComputeCommands pass,
        RenderGraphHandle depthTarget,
        RenderGraphHandle hiZ,
        uint sourceMip,
        uint width,
        uint height)
    {
        uint targetMip = sourceMip + 2;
        uint mipWidth = MipSize(width, targetMip);
        uint mipHeight = MipSize(height, targetMip);
        var sourceView = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: sourceMip, mipCount: 1);
        var destination1View = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: sourceMip + 1, mipCount: 1);
        var destination2View = context.GetTextureView(hiZ, ViewKind.UnorderedAccess, Format.R32Float, firstMip: targetMip, mipCount: 1);

        pass.SetPipeline(context.GetPipeline(_downsampleTwoPipeline));
        pass.SetParameters(
            _buildSet,
            context.Bindings(_buildLayout)
                .Reserve(6)
                .Texture(
                    _depth,
                    context.GetTextureView(depthTarget, ViewKind.ShaderResource, Format.D32Float))
                .Texture(_hiZMip0, destination1View)
                .Texture(_hiZMip1, destination2View)
                .Texture(
                    _sourceMip,
                    sourceView)
                .Texture(
                    _destinationMip1,
                    destination1View)
                .Texture(
                    _destinationMip2,
                    destination2View));
        pass.Dispatch(
            DispatchCount(mipWidth),
            DispatchCount(mipHeight),
            1);
    }

    private static void AddMipBarrier(
        IComputeCommands pass,
        RenderGraphHandle texture,
        uint mip)
        => pass.Barrier(
            [new UavBarrier(
                texture,
                new SubResourceRange(mip, 1, 0, 1))]);

    private static void ValidateHiZ(TextureDesc desc, uint width, uint height, uint mipCount)
    {
        if (desc.Dimension != ResourceDimension.Texture2D || desc.Depth != 1 || desc.ArraySize != 1)
            throw new InvalidOperationException("HiZ texture must be a single-slice Texture2D.");
        if (desc.SampleCount != 1)
            throw new InvalidOperationException("HiZ texture must not be multisampled.");
        if (desc.Format != Format.R32Float)
            throw new InvalidOperationException("HiZ texture format must be R32Float.");
        if (desc.Width != width || desc.Height != height || desc.MipLevels < mipCount)
        {
            throw new InvalidOperationException(
                $"HiZ texture dimensions {desc.Width}x{desc.Height} mips={desc.MipLevels} do not match required {width}x{height} mips={mipCount}.");
        }
        if ((desc.BindFlags & (BindFlags.ShaderResource | BindFlags.UnorderedAccess))
            != (BindFlags.ShaderResource | BindFlags.UnorderedAccess))
        {
            throw new InvalidOperationException("HiZ texture must be bindable as both ShaderResource and UnorderedAccess.");
        }
    }

    private static uint DispatchCount(uint size)
        => checked((size + 7u) / 8u);

    private static uint MipSize(uint size, uint mip)
        => Math.Max(1u, size >> checked((int)mip));

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HiZPass));
    }
}
