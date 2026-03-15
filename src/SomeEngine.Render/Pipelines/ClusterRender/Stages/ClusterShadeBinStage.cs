using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: Shade Binning — 按 MaterialID 分组可见像素坐标。
/// </summary>
public class ClusterShadeBinStage : IDisposable
{
    private readonly RenderContext _context;
    private ClusterShadeBinCountPass? _countPass;
    private ClusterShadeBinReservePass? _reservePass;
    private ClusterShadeBinScatterPass? _scatterPass;
    private bool _initialized;

    public ClusterShadeBinStage(RenderContext context)
    {
        _context = context;
    }

    public void Init()
    {
        if (!_initialized)
        {
            var resources = new ClusterShadeBinningResources();
            resources.Init(_context);
            _countPass = new ClusterShadeBinCountPass(_context, resources);
            _reservePass = new ClusterShadeBinReservePass(_context, resources);
            _scatterPass = new ClusterShadeBinScatterPass(_context, resources);
            _initialized = true;
        }
    }

    /// <summary>
    /// 添加 ShadeBin pass（Count + Reserve + Scatter），返回像素分组结果。
    /// </summary>
    public ClusterShadeBinOutput AddPasses(
        RenderGraph graph,
        in ClusterRasterOutput raster,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterShadeBinConfig config,
        uint activeMaterialCount,
        uint screenWidth,
        uint screenHeight
    )
    {
        if (!_initialized) Init();

        const int MaxMaterials = 256;

        // ─── Create shade binning buffers ───
        var hBinUniforms = graph.CreateBuffer("ShadeBinUniforms", new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<ShadeBinUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        var hBinCounts = graph.CreateBuffer("BinCounts", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinOffsets = graph.CreateBuffer("BinOffsets", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinScatterCount = graph.CreateBuffer("BinScatterCount", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hPixelCoordBuffer = config.OutputPixelCoordBuffer.IsValid
            ? config.OutputPixelCoordBuffer
            : graph.CreateBuffer("PixelCoordBuffer", new BufferDesc
            {
                Size = (ulong)(screenWidth * screenHeight * 4),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 4,
            });
        var hBinIndirectArgs = graph.CreateBuffer("BinIndirectArgs", new BufferDesc
        {
            Size = (ulong)(MaxMaterials * 12),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        // ─── Upload shade bin uniforms ───
        var binUniformData = new ShadeBinUniforms
        {
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            MaterialCount = activeMaterialCount,
        };
        graph.AddPass<object>(
            "UploadShadeBinUniforms",
            (builder, _) => { builder.Write(hBinUniforms, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinUniforms);
                if (ctx2 != null && buf != null)
                {
                    var mapped = ctx2.MapBuffer<ShadeBinUniforms>(buf, MapType.Write, MapFlags.Discard);
                    mapped[0] = binUniformData;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        // ─── Clear bin counts ───
        graph.AddPass<object>(
            "ClearBinCounts",
            (builder, _) => { builder.Write(hBinCounts, ResourceState.CopyDest); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinCounts);
                if (ctx2 != null && buf != null)
                {
                    Span<byte> zeros = stackalloc byte[MaxMaterials * 4];
                    zeros.Clear();
                    ctx2.UpdateBuffer(buf, 0, (ReadOnlySpan<byte>)zeros, ResourceStateTransitionMode.Verify);
                }
            }
        );

        // ─── ShadeBin Count / Reserve / Scatter ───
        _countPass!.HVisBuffer = raster.VisBuffer;
        _countPass.HVisibleClusters = cull.VisibleClusters;
        _countPass.HInstanceHeaders = globals.GlobalInstanceHeader;
        _countPass.HShadeBinUniforms = hBinUniforms;
        _countPass.HBinCounts = hBinCounts;
        graph.AddPass(_countPass);

        _reservePass!.HShadeBinUniforms = hBinUniforms;
        _reservePass.HBinCounts = hBinCounts;
        _reservePass.HBinOffsets = hBinOffsets;
        _reservePass.HBinScatterCount = hBinScatterCount;
        _reservePass.HBinIndirectArgs = hBinIndirectArgs;
        graph.AddPass(_reservePass);

        _scatterPass!.HVisBuffer = raster.VisBuffer;
        _scatterPass.HVisibleClusters = cull.VisibleClusters;
        _scatterPass.HInstanceHeaders = globals.GlobalInstanceHeader;
        _scatterPass.HShadeBinUniforms = hBinUniforms;
        _scatterPass.HBinOffsets = hBinOffsets;
        _scatterPass.HBinScatterCount = hBinScatterCount;
        _scatterPass.HPixelCoordBuffer = hPixelCoordBuffer;
        graph.AddPass(_scatterPass);

        return new ClusterShadeBinOutput(hPixelCoordBuffer, hBinOffsets, hBinCounts, hBinIndirectArgs);
    }

    public void Dispose()
    {
        // Resources are owned by the ShadeBinning passes internals
    }
}
