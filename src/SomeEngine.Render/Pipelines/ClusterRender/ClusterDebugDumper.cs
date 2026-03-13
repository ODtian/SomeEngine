using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// One-shot frame data dump for HiZ debugging.  Press F5 at runtime to
/// trigger.  Writes binary depth/HiZ maps + JSON metadata to <c>dump/</c>.
/// </summary>
internal sealed class ClusterDebugDumpPass : IRenderGraphPass
{
    public string Name => "Debug HiZ Dump";

    public RenderGraphHandle HHiZTexture = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase1HiZTexture = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTexture = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDummyOutput = RenderGraphHandle.Invalid;

    public uint HiZWidth;
    public uint HiZHeight;
    public uint HiZMipCount;
    public Matrix4x4 ViewProj;
    public Matrix4x4 PrevViewProj;
    public Vector3 CameraPos;
    public Vector2 HiZInvSize;
    public bool HasPrevHistory;

    private readonly RenderContext _context;
    private readonly string _outputDir;

    public ClusterDebugDumpPass(RenderContext context, string? outputDir = null)
    {
        _context = context;
        _outputDir = outputDir ?? Path.GetFullPath("F:\\SomeEngine\\dump");
    }

    public void Setup(RenderGraphBuilder builder)
    {
        if (HHiZTexture.IsValid)
            builder.Read(HHiZTexture, ResourceState.CopySource);
        if (HPhase1HiZTexture.IsValid)
            builder.Read(HPhase1HiZTexture, ResourceState.CopySource);
        if (HDepthTexture.IsValid)
            builder.Read(HDepthTexture, ResourceState.CopySource);
        // Dummy write to prevent RenderGraph DCE from stripping this pass
        if (HDummyOutput.IsValid)
            builder.Write(HDummyOutput, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        var ctx = _context.ImmediateContext;
        var device = _context.Device;
        if (ctx == null || device == null)
            return;

        Directory.CreateDirectory(_outputDir);

        // Dump HiZ mip 0
        if (HHiZTexture.IsValid)
        {
            Console.WriteLine("Dumping HiZ mip 0");
            var hizTex = graphContext.GetTexture(HHiZTexture);
            if (hizTex != null)
            {
                DumpTextureMip(ctx, device, hizTex, 0, Path.Combine(_outputDir, "hiz_mip0.bin"));

                // Also dump a few more mip levels for analysis
                var desc = hizTex.GetDesc();
                uint mipsToDump = desc.MipLevels;
                for (uint mip = 1; mip < mipsToDump; mip++)
                {
                    DumpTextureMip(
                        ctx,
                        device,
                        hizTex,
                        mip,
                        Path.Combine(_outputDir, $"hiz_mip{mip}.bin")
                    );
                }
            }
        }

        // Dump Phase 1 HiZ (used by Phase 2 cull)
        if (HPhase1HiZTexture.IsValid)
        {
            Console.WriteLine("Dumping Phase1 HiZ mip 0");
            var p1HizTex = graphContext.GetTexture(HPhase1HiZTexture);
            if (p1HizTex != null)
            {
                DumpTextureMip(ctx, device, p1HizTex, 0, Path.Combine(_outputDir, "phase1_hiz_mip0.bin"));
            }
        }

        // Dump depth buffer mip 0
        if (HDepthTexture.IsValid)
        {
            Console.WriteLine("Dumping depth mip 0");
            var depthTex = graphContext.GetTexture(HDepthTexture);
            if (depthTex != null)
            {
                DumpTextureMip(
                    ctx,
                    device,
                    depthTex,
                    0,
                    Path.Combine(_outputDir, "depth_mip0.bin")
                );
            }
        }

        // Dump Debug HiZ Output Buffer
        if (HDebugHiZOutput.IsValid)
        {
            var debugBuffer = graphContext.GetBuffer(HDebugHiZOutput);
            if (debugBuffer != null)
            {
                Console.WriteLine("Dumping DebugHiZOutput buffer");
                DumpBuffer(ctx, device, debugBuffer, Path.Combine(_outputDir, "debug_hiz.bin"));
            }
        }

        // Write metadata
        var meta = new
        {
            Width = HiZWidth,
            Height = HiZHeight,
            MipCount = HiZMipCount,
            HiZInvSize = new[] { HiZInvSize.X, HiZInvSize.Y },
            HasPrevHistory,
            CameraPos = new[] { CameraPos.X, CameraPos.Y, CameraPos.Z },
            ViewProj = MatrixToArray(ViewProj),
            PrevViewProj = MatrixToArray(PrevViewProj),
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            Path.Combine(_outputDir, "meta.json"),
            JsonSerializer.Serialize(meta, options)
        );

        Console.WriteLine($"[HiZ Dump] Wrote frame data to {Path.GetFullPath(_outputDir)}/");
    }

    private static void DumpTextureMip(
        IDeviceContext ctx,
        IRenderDevice device,
        ITexture srcTexture,
        uint mipLevel,
        string outputPath
    )
    {
        var srcDesc = srcTexture.GetDesc();
        uint mipW = Math.Max(1u, srcDesc.Width >> (int)mipLevel);
        uint mipH = Math.Max(1u, srcDesc.Height >> (int)mipLevel);

        // Determine the copy format.  Depth textures need R32_Float staging.
        var format = srcDesc.Format;
        bool isDepth =
            format == TextureFormat.D32_Float
            || format == TextureFormat.D32_Float_S8X24_UInt
            || format == TextureFormat.D24_UNorm_S8_UInt
            || format == TextureFormat.D16_UNorm;
        var stagingFormat = isDepth ? TextureFormat.R32_Float : format;

        // Create staging texture
        var stagingDesc = new TextureDesc
        {
            Name = $"DumpStaging_mip{mipLevel}",
            Type = ResourceDimension.Tex2d,
            Width = mipW,
            Height = mipH,
            MipLevels = 1,
            ArraySizeOrDepth = 1,
            Format = stagingFormat,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        };
        var staging = device.CreateTexture(stagingDesc, null);
        if (staging == null)
            return;

        try
        {
            // Copy GPU texture → staging
            ctx.CopyTexture(
                new CopyTextureAttribs
                {
                    SrcTexture = srcTexture,
                    SrcMipLevel = mipLevel,
                    SrcSlice = 0,
                    SrcTextureTransitionMode = ResourceStateTransitionMode.Transition,
                    DstTexture = staging,
                    DstMipLevel = 0,
                    DstSlice = 0,
                    DstTextureTransitionMode = ResourceStateTransitionMode.Transition,
                }
            );

            // Flush and wait
            ctx.WaitForIdle();

            // Map and read
            var mapped = ctx.MapTextureSubresource(
                staging,
                0,
                0,
                MapType.Read,
                MapFlags.None,
                null
            );

            // Write raw float data
            int bytesPerPixel = 4; // R32_Float
            using var fs = File.Create(outputPath);
            // Write width and height as header
            fs.Write(BitConverter.GetBytes(mipW));
            fs.Write(BitConverter.GetBytes(mipH));

            for (uint row = 0; row < mipH; row++)
            {
                nint rowPtr = mapped.Data + (nint)((long)row * (long)mapped.Stride);
                byte[] rowData = new byte[mipW * bytesPerPixel];
                Marshal.Copy(rowPtr, rowData, 0, rowData.Length);
                fs.Write(rowData);
            }

            ctx.UnmapTextureSubresource(staging, 0, 0);
        }
        finally
        {
            staging.Dispose();
        }
    }

    private static void DumpBuffer(
        IDeviceContext ctx,
        IRenderDevice device,
        IBuffer srcBuffer,
        string outputPath
    )
    {
        var srcDesc = srcBuffer.GetDesc();
        
        // Create staging buffer
        var stagingDesc = new BufferDesc
        {
            Name = "DumpStagingBuffer",
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            Size = srcDesc.Size,
        };

        var stagingBuffer = device.CreateBuffer(stagingDesc, null);
        try
        {
            ctx.CopyBuffer(srcBuffer, 0, ResourceStateTransitionMode.Verify, stagingBuffer, 0, srcDesc.Size, ResourceStateTransitionMode.Verify);
            ctx.WaitForIdle();

            var mapped = ctx.MapBuffer(stagingBuffer, MapType.Read, MapFlags.None);
            if (mapped != IntPtr.Zero)
            {
                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                byte[] data = new byte[srcDesc.Size];
                Marshal.Copy(mapped, data, 0, data.Length);
                fs.Write(data, 0, data.Length);
            }
            ctx.UnmapBuffer(stagingBuffer, MapType.Read);
        }
        finally
        {
            stagingBuffer.Dispose();
        }
    }

    private static float[] MatrixToArray(Matrix4x4 m) =>
        [
            m.M11,
            m.M12,
            m.M13,
            m.M14,
            m.M21,
            m.M22,
            m.M23,
            m.M24,
            m.M31,
            m.M32,
            m.M33,
            m.M34,
            m.M41,
            m.M42,
            m.M43,
            m.M44,
        ];
}
