using SomeEngine.Render.Graph;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.UI;

public sealed class ImGuiLayer : IDisposable
{
    public const string VertexEntryPoint = "VSMain";
    public const string PixelEntryPoint = "PSMain";

    private const int FontTextureId = 1;
    private const int InitialVertexCapacity = 10_000;
    private const int InitialIndexCapacity = 10_000;
    private const ulong UniformBufferSize = 256;
    private readonly RenderContext _context;
    private readonly Shader _shader;
    private readonly List<RegisteredTexture> _textures = [];
    private int _nextTextureId = 2;

    private ShaderBindingTable? _layout;
    private PipelineTicket _pipeline;
    private Format _pipelineFormat = Format.Unknown;

    private BufferHandle _vertexBuffer;
    private BufferHandle _indexBuffer;
    private BufferHandle _uniformBuffer;
    private BufferViewHandle _uniformView;
    private int _vertexCapacity = InitialVertexCapacity;
    private int _indexCapacity = InitialIndexCapacity;

    private TextureHandle _fontTexture;
    private TextureViewHandle _fontTextureView;
    private SamplerHandle _linearSampler;
    private SamplerHandle _pointSampler;
    private bool _disposed;

    public ImGuiLayer(RenderContext context, Shader shader)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _shader = shader ?? throw new ArgumentNullException(nameof(shader));
        Initialize();
    }

    public IntPtr RegisterTexture(TextureViewHandle textureView, bool pointSampled = true)
    {
        if (!textureView.IsValid)
            throw new ArgumentException("ImGui texture registration requires a valid texture view.", nameof(textureView));

        var id = (IntPtr)_nextTextureId++;
        _textures.Add(new RegisteredTexture(id, new TextureBinding(textureView, pointSampled ? _pointSampler : _linearSampler)));
        return id;
    }

    public void UnregisterTexture(IntPtr textureId)
    {
        for (int i = 0; i < _textures.Count; i++)
        {
            if (_textures[i].Id == textureId)
            {
                _textures.RemoveAt(i);
                return;
            }
        }
    }

    public BufferDesc GetVertexBufferDesc(int vertexCount)
        => VertexBufferDesc(Capacity(_vertexCapacity, vertexCount));

    public BufferDesc GetIndexBufferDesc(int indexCount)
        => IndexBufferDesc(Capacity(_indexCapacity, indexCount));

    public void AddPass(RenderGraph graph, RenderGraphHandle output, ImDrawDataPtr drawData)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!output.IsValid)
            throw new ArgumentException("ImGui render pass requires a valid output target.", nameof(output));

        graph.UsePipelineCache(_context.PipelineCache);
        if (drawData.TotalVtxCount == 0 || drawData.TotalIdxCount == 0)
            return;

        EnsurePipeline(graph.GetTextureDesc(output).Format);
        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        UploadDrawData(drawData);

        var vertexBuffer = graph.ImportBuffer(
            "ImGuiVertexBuffer",
            _vertexBuffer,
            VertexBufferDesc(),
            new ImportDesc(ResourceState.VertexBuffer));
        var indexBuffer = graph.ImportBuffer(
            "ImGuiIndexBuffer",
            _indexBuffer,
            IndexBufferDesc(),
            new ImportDesc(ResourceState.IndexBuffer));

        graph.AddRasterPass(
            "ImGui",
            builder =>
            {
                builder.ReadWrite(output, ResourceState.RenderTarget);
                builder.Read(vertexBuffer, ResourceState.VertexBuffer);
                builder.Read(indexBuffer, ResourceState.IndexBuffer);
            },
            context =>
            {
                var outputDesc = context.GetTextureDesc(output);

                var rtv = context.GetTextureView(output, ViewKind.RenderTarget, outputDesc.Format);
                Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
                colorAttachments[0] = new ColorAttachmentDesc
                {
                    View = rtv,
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                };

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = "ImGui",
                    RenderArea = new Rect(0, 0, checked((int)outputDesc.Width), checked((int)outputDesc.Height)),
                    ColorAttachments = colorAttachments,
                });

                pass.SetViewport(new Viewport(0, 0, outputDesc.Width, outputDesc.Height));
                pass.SetPipeline(context.GetPipeline(_pipeline));
                pass.SetVertexBuffer(0, vertexBuffer);
                pass.SetIndexBuffer(indexBuffer, IndexFormat.UInt16);
                DrawCommands(context, pass, drawData, checked((int)outputDesc.Width), checked((int)outputDesc.Height));
                pass.End();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var device = _context.GraphicsDevice;
        if (device != null)
        {
            _context.PipelineCache?.Release(ref _pipeline);
            device.WaitIdle();
            _context.PipelineCache?.Retire(ulong.MaxValue);

            if (_layout.HasValue)
                ShaderBindings.Destroy(device, _layout.Value);
            if (_uniformView.IsValid)
                device.Destroy(_uniformView);
            if (_vertexBuffer.IsValid)
                device.Destroy(_vertexBuffer);
            if (_indexBuffer.IsValid)
                device.Destroy(_indexBuffer);
            if (_uniformBuffer.IsValid)
                device.Destroy(_uniformBuffer);
            if (_fontTextureView.IsValid)
                device.Destroy(_fontTextureView);
            if (_fontTexture.IsValid)
                device.Destroy(_fontTexture);
            if (_linearSampler.IsValid)
                device.Destroy(_linearSampler);
            if (_pointSampler.IsValid)
                device.Destroy(_pointSampler);
        }

        _layout = null;
        _textures.Clear();
        _pipeline = default;
        _uniformView = default;
        _vertexBuffer = default;
        _indexBuffer = default;
        _uniformBuffer = default;
        _fontTextureView = default;
        _fontTexture = default;
        _linearSampler = default;
        _pointSampler = default;

        if (ImGui.GetCurrentContext() != IntPtr.Zero)
            ImGui.DestroyContext();

        _disposed = true;
    }

    private unsafe void Initialize()
    {
        var device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ImGui layer requires an initialized graphics context.");
        var queue = _context.GraphicsQueue
            ?? throw new InvalidOperationException("ImGui layer requires a graphics queue.");

        ImGui.CreateContext();
        ImGui.StyleColorsDark();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        UploadFontTexture(device, queue, pixels, width, height, bytesPerPixel);
        io.Fonts.SetTexID((IntPtr)FontTextureId);

        _linearSampler = device.CreateSampler(new SamplerDesc
        {
            Name = "ImGui Linear Sampler",
            MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear,
            MipmapMode = MipmapMode.Linear,
            AddressU = AddressMode.Repeat,
            AddressV = AddressMode.Repeat,
            AddressW = AddressMode.Repeat,
        });
        _pointSampler = device.CreateSampler(new SamplerDesc
        {
            Name = "ImGui Point Sampler",
            MinFilter = FilterMode.Nearest,
            MagFilter = FilterMode.Nearest,
            MipmapMode = MipmapMode.Nearest,
            AddressU = AddressMode.ClampToEdge,
            AddressV = AddressMode.ClampToEdge,
            AddressW = AddressMode.ClampToEdge,
        });

        _textures.Add(new RegisteredTexture((IntPtr)FontTextureId, new TextureBinding(_fontTextureView, _linearSampler)));

        _uniformBuffer = device.CreateBuffer(new BufferDesc
        {
            Name = "ImGui Uniform Buffer",
            SizeInBytes = UniformBufferSize,
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.ConstantBuffer,
            InitialState = ResourceState.ConstantBuffer,
        });
        _uniformView = device.CreateBufferView(_uniformBuffer, new BufferViewDesc
        {
            Name = "ImGui Uniform View",
            Kind = ViewKind.ConstantBuffer,
            SizeInBytes = UniformBufferSize,
        });

        EnsureBuffers(_vertexCapacity, _indexCapacity);
    }

    private unsafe void UploadFontTexture(
        IDevice device,
        IQueue queue,
        byte* pixels,
        int width,
        int height,
        int bytesPerPixel)
    {
        if (width <= 0 || height <= 0 || bytesPerPixel != 4)
            throw new InvalidOperationException("ImGui font atlas must be non-empty RGBA32 data.");

        int sourceRowSize = checked(width * bytesPerPixel);
        int destinationRowSize = checked(width * 4);
        uint rowPitch = checked((uint)AlignUp((uint)destinationRowSize, device.Limits.TextureRowPitchAlignment));
        uint slicePitch = checked(rowPitch * (uint)height);
        byte[] uploadData = new byte[slicePitch];
        for (int y = 0; y < height; y++)
        {
            var source = new ReadOnlySpan<byte>(pixels + checked(y * sourceRowSize), sourceRowSize);
            Span<byte> destination = uploadData.AsSpan(checked((int)((uint)y * rowPitch)), destinationRowSize);
            source.CopyTo(destination);
        }

        _fontTexture = device.CreateTexture(new TextureDesc
        {
            Name = "ImGui Font Texture",
            Dimension = ResourceDimension.Texture2D,
            Width = checked((uint)width),
            Height = checked((uint)height),
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopyDestination,
        });

        var uploadBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "ImGui Font Upload",
                SizeInBytes = checked((ulong)uploadData.Length),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadData);

        var list = device.CreateCommandList(new CommandListDesc
        {
            Name = "Upload ImGui font",
            QueueType = QueueType.Graphics,
        });
        list.CopyToTexture(
            uploadBuffer,
            new BufferTextureCopy(0, rowPitch, slicePitch),
            _fontTexture,
            new TextureCopyRegion(0, 0, 0, 0, 0, checked((uint)width), checked((uint)height), 1));
        list.Barrier(
            [new TextureBarrier(_fontTexture, ResourceState.CopyDestination, ResourceState.ShaderResource, SubresourceRange.All)],
            default);
        var commandBuffer = list.Finish();
        queue.Submit([commandBuffer]);
        queue.WaitIdle();
        device.Destroy(commandBuffer);
        device.Destroy(uploadBuffer);

        _fontTextureView = device.CreateTextureView(_fontTexture, new TextureViewDesc
        {
            Name = "ImGui Font View",
            Kind = ViewKind.ShaderResource,
            Format = Format.Rgba8Unorm,
        });
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        var device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ImGui layer requires an initialized graphics context.");

        if (!_vertexBuffer.IsValid || vertexCount > _vertexCapacity)
        {
            if (_vertexBuffer.IsValid)
                device.Destroy(_vertexBuffer);

            _vertexCapacity = Math.Max(vertexCount, _vertexCapacity + (_vertexCapacity / 2));
            _vertexBuffer = device.CreateBuffer(VertexBufferDesc());
        }

        if (!_indexBuffer.IsValid || indexCount > _indexCapacity)
        {
            if (_indexBuffer.IsValid)
                device.Destroy(_indexBuffer);

            _indexCapacity = Math.Max(indexCount, _indexCapacity + (_indexCapacity / 2));
            _indexBuffer = device.CreateBuffer(IndexBufferDesc());
        }
    }

    private BufferDesc VertexBufferDesc()
        => VertexBufferDesc(_vertexCapacity);

    private static BufferDesc VertexBufferDesc(int vertexCapacity)
        => new()
        {
            Name = "ImGui Vertex Buffer",
            SizeInBytes = checked((ulong)vertexCapacity * (ulong)Unsafe.SizeOf<ImDrawVert>()),
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.VertexBuffer,
            InitialState = ResourceState.VertexBuffer,
        };

    private BufferDesc IndexBufferDesc()
        => IndexBufferDesc(_indexCapacity);

    private static BufferDesc IndexBufferDesc(int indexCapacity)
        => new()
        {
            Name = "ImGui Index Buffer",
            SizeInBytes = checked((ulong)indexCapacity * sizeof(ushort)),
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.IndexBuffer,
            InitialState = ResourceState.IndexBuffer,
        };

    private static int Capacity(int current, int required)
        => required > current
            ? Math.Max(required, current + (current / 2))
            : current;

    private unsafe void UploadDrawData(ImDrawDataPtr drawData)
    {
        var device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ImGui layer requires an initialized graphics context.");

        float left = drawData.DisplayPos.X;
        float right = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float top = drawData.DisplayPos.Y;
        float bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        Vector4 transform = new(
            2.0f / (right - left),
            -2.0f / (bottom - top),
            -1.0f - (left * 2.0f / (right - left)),
            1.0f + (top * 2.0f / (bottom - top)));
        var mappedUniform = device.MapBuffer(_uniformBuffer, MapMode.Write, 0, Unsafe.SizeOf<Vector4>());
        MemoryMarshal.Write(mappedUniform.Span, in transform);
        device.UnmapBuffer(_uniformBuffer);

        var mappedVertices = device.MapBuffer(
            _vertexBuffer,
            MapMode.Write,
            0,
            checked(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>()));
        var mappedIndices = device.MapBuffer(
            _indexBuffer,
            MapMode.Write,
            0,
            checked(drawData.TotalIdxCount * sizeof(ushort)));

        int vertexByteOffset = 0;
        int indexByteOffset = 0;
        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr commandList = drawData.CmdLists[listIndex];
            int vertexByteCount = checked(commandList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
            int indexByteCount = checked(commandList.IdxBuffer.Size * sizeof(ushort));
            new ReadOnlySpan<byte>((void*)commandList.VtxBuffer.Data, vertexByteCount)
                .CopyTo(mappedVertices.Span.Slice(vertexByteOffset, vertexByteCount));
            new ReadOnlySpan<byte>((void*)commandList.IdxBuffer.Data, indexByteCount)
                .CopyTo(mappedIndices.Span.Slice(indexByteOffset, indexByteCount));
            vertexByteOffset += vertexByteCount;
            indexByteOffset += indexByteCount;
        }

        device.UnmapBuffer(_vertexBuffer);
        device.UnmapBuffer(_indexBuffer);
    }

    private void DrawCommands(
        RenderGraphContext context,
        RenderPassCommands pass,
        ImDrawDataPtr drawData,
        int targetWidth,
        int targetHeight)
    {
        var layout = _layout ?? throw new InvalidOperationException("ImGui pipeline layout was not created.");
        var uniform = layout.GetRequired("UniformBuffer");
        var texture = layout.GetRequired("g_Texture");
        var sampler = layout.GetRequired("g_Texture_sampler");
        uint set = BindInput.GetSetIndex("ImGui", uniform, texture, sampler);

        Vector2 clipOffset = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;
        if (clipScale.X <= 0.0f || clipScale.Y <= 0.0f)
            throw new InvalidOperationException($"ImGui framebuffer scale must be positive, got {clipScale.X}x{clipScale.Y}.");
        int vertexOffset = 0;
        uint indexOffset = 0;

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr commandList = drawData.CmdLists[listIndex];
            for (int commandIndex = 0; commandIndex < commandList.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr command = commandList.CmdBuffer[commandIndex];
                if (command.UserCallback != IntPtr.Zero)
                    throw new NotSupportedException("ImGui user draw callbacks are not supported by the graphics layer.");
                if (command.ElemCount == 0)
                    continue;

                Vector4 clip = command.ClipRect;
                int clipX = (int)MathF.Max((clip.X - clipOffset.X) * clipScale.X, 0.0f);
                int clipY = (int)MathF.Max((clip.Y - clipOffset.Y) * clipScale.Y, 0.0f);
                int clipZ = (int)MathF.Min((clip.Z - clipOffset.X) * clipScale.X, targetWidth);
                int clipW = (int)MathF.Min((clip.W - clipOffset.Y) * clipScale.Y, targetHeight);
                if (clipZ <= clipX || clipW <= clipY)
                    continue;

                TextureBinding binding = ResolveTexture(command.TextureId);
                pass.SetParameters(
                    set,
                    context.Bindings(layout.Layout(set))
                        .Buffer(uniform, _uniformView)
                        .Texture(texture, binding.View)
                        .Sampler(sampler, binding.Sampler));
                pass.SetScissor(new Rect(clipX, clipY, clipZ - clipX, clipW - clipY));
                pass.DrawIndexed(
                    command.ElemCount,
                    firstIndex: checked(indexOffset + command.IdxOffset),
                    vertexOffset: checked(vertexOffset + (int)command.VtxOffset));
            }

            vertexOffset += commandList.VtxBuffer.Size;
            indexOffset += checked((uint)commandList.IdxBuffer.Size);
        }
    }

    private void EnsurePipeline(Format outputFormat)
    {
        var device = _context.GraphicsDevice
            ?? throw new InvalidOperationException("ImGui requires an initialized render context.");

        if (_pipeline.IsValid && _pipelineFormat == outputFormat)
            return;

        if (_pipeline.IsValid)
        {
            _context.PipelineCache?.Release(ref _pipeline);
        }

        _layout ??= ShaderBindings.Create(
            device,
            "ImGui",
            _shader,
            ["UniformBuffer", "g_Texture", "g_Texture_sampler"],
            VertexEntryPoint,
            PixelEntryPoint);

        ShaderBindingTable layout = _layout
            ?? throw new InvalidOperationException("ImGui pipeline layout was not created.");
        _pipeline = _context.PipelineCache!.QueueGraphics(new GraphicsState
        {
            Name = "ImGui PipelineState",
            VertexShader = _shader,
            VertexEntry = VertexEntryPoint,
            PixelShader = _shader,
            PixelEntry = PixelEntryPoint,
            Layout = layout.PipelineLayout,
            Bindings = layout.Key,
            Topology = PrimitiveTopology.TriangleList,
            VertexBuffers =
            [
                new VertexLayoutDesc
                {
                    Slot = 0,
                    StrideInBytes = checked((uint)Unsafe.SizeOf<ImDrawVert>()),
                },
            ],
            VertexAttributes =
            [
                new VertexAttributeDesc
                {
                    Location = 0,
                    BufferSlot = 0,
                    Format = Format.Rg32Float,
                    OffsetInBytes = 0,
                },
                new VertexAttributeDesc
                {
                    Location = 1,
                    BufferSlot = 0,
                    Format = Format.Rg32Float,
                    OffsetInBytes = 8,
                },
                new VertexAttributeDesc
                {
                    Location = 2,
                    BufferSlot = 0,
                    Format = Format.Rgba8Unorm,
                    OffsetInBytes = 16,
                },
            ],
            Rasterizer = new RasterizerDesc
            {
                CullMode = CullMode.None,
                FrontCounterClockwise = false,
            },
            DepthStencil = new DepthStencilDesc
            {
                DepthEnable = false,
                DepthWriteEnable = false,
            },
            Blend = new BlendDesc
            {
                Targets =
                [
                    new BlendTargetDesc
                    {
                        Enable = true,
                        SourceColor = BlendFactor.SourceAlpha,
                        DestinationColor = BlendFactor.OneMinusSourceAlpha,
                        ColorOp = BlendOp.Add,
                        SourceAlpha = BlendFactor.One,
                        DestinationAlpha = BlendFactor.OneMinusSourceAlpha,
                        AlphaOp = BlendOp.Add,
                    },
                ],
            },
            ColorFormats = [outputFormat],
        });
        _context.WaitRequired([_pipeline]);
        _pipelineFormat = outputFormat;
    }

    private TextureBinding ResolveTexture(IntPtr textureId)
    {
        if (textureId == IntPtr.Zero)
            textureId = (IntPtr)FontTextureId;
        for (int i = 0; i < _textures.Count; i++)
        {
            if (_textures[i].Id == textureId)
                return _textures[i].Binding;
        }

        throw new InvalidOperationException($"ImGui draw command referenced unregistered texture id {textureId}.");
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            return value;
        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ImGuiLayer));
    }

    private readonly record struct RegisteredTexture(IntPtr Id, TextureBinding Binding);

    private readonly record struct TextureBinding(TextureViewHandle View, SamplerHandle Sampler);

}

