using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using RhiPrimitiveTopology = SomeEngine.Rhi.PrimitiveTopology;

namespace SomeEngine.Rhi.D3D12;

internal sealed partial class D3D12Device
{
    private const uint PushConstantRegisterBase = 100;
    private const int DescriptorCopyRangeStackCapacity = 64;
    private const int BindingValidationStackKeyLimit = 64;
    private const ulong RootSignatureFingerprintOffsetBasis = 14695981039346656037UL;
    private const ulong RootSignatureFingerprintPrime = 1099511628211UL;

    private ID3D12PipelineState CreateComputeState(
        ComputePipelineDesc desc,
        ComputePipelineStateDescription nativeDesc,
        ShaderRecord shader)
    {
        if (!desc.PipelineCache.IsValid)
        {
            ulong debugMessageStart = GetDebugCount();
            try
            {
                return _device.CreateComputePipelineState(nativeDesc);
            }
            catch (SharpGenException ex)
            {
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create compute pipeline '{desc.Name}': {ex.Message}{FormatDebugMessages(debugMessageStart)}");
            }
        }

        var cache = PipelineCaches.Get(desc.PipelineCache, "PipelineCache");
        string key = ComputeKey(desc, shader);
        ulong cachedDebugMessageStart = GetDebugCount();
        try
        {
            return cache.Library.LoadComputePipeline(key, nativeDesc);
        }
        catch (SharpGenException)
        {
            try
            {
                var state = _device.CreateComputePipelineState(nativeDesc);
                StorePipeline(cache.Library, key, state);
                return state;
            }
            catch (SharpGenException ex)
            {
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create compute pipeline '{desc.Name}': {ex.Message}{FormatDebugMessages(cachedDebugMessageStart)}");
            }
        }
    }

    private ID3D12PipelineState CreateGraphicsState(
        GraphicsPipelineDesc desc,
        GraphicsPipelineStateDescription nativeDesc,
        ShaderRecord vertex,
        ShaderRecord pixel,
        ShaderRecord? hull,
        ShaderRecord? domain,
        ShaderRecord? geometry)
    {
        if (!desc.PipelineCache.IsValid)
        {
            ulong debugMessageStart = GetDebugCount();
            try
            {
                return _device.CreateGraphicsPipelineState(nativeDesc);
            }
            catch (SharpGenException ex)
            {
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create graphics pipeline '{desc.Name}': {ex.Message}{FormatDebugMessages(debugMessageStart)}");
            }
        }

        var cache = PipelineCaches.Get(desc.PipelineCache, "PipelineCache");
        string key = GraphicsKey(desc, vertex, pixel, hull, domain, geometry);
        ulong cachedDebugMessageStart = GetDebugCount();
        try
        {
            return cache.Library.LoadGraphicsPipeline(key, nativeDesc);
        }
        catch (SharpGenException)
        {
            try
            {
                var state = _device.CreateGraphicsPipelineState(nativeDesc);
                StorePipeline(cache.Library, key, state);
                return state;
            }
            catch (SharpGenException ex)
            {
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create graphics pipeline '{desc.Name}': {ex.Message}{FormatDebugMessages(cachedDebugMessageStart)}");
            }
        }
    }

    private static void StorePipeline(ID3D12PipelineLibrary library, string key, ID3D12PipelineState state)
    {
        try
        {
            library.StorePipeline(key, state);
        }
        catch (SharpGenException ex)
        {
            throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to store pipeline cache entry '{key}': {ex.Message}");
        }
    }

    private ID3D12PipelineState CreateMeshState(
        MeshPipelineDesc desc,
        PipeLayoutRecord layout,
        ShaderRecord? amplification,
        ShaderRecord mesh,
        ShaderRecord? pixel)
    {
        using var stream = MeshStateStream(desc, layout, amplification, mesh, pixel);
        if (!desc.PipelineCache.IsValid)
            return CreateStateStream(stream.Bytes);

        var cache = PipelineCaches.Get(desc.PipelineCache, "PipelineCache");
        string key = MeshKey(desc, amplification, mesh, pixel);
        using var library1 = cache.Library.QueryInterfaceOrNull<ID3D12PipelineLibrary1>();
        if (library1 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 mesh pipeline caches require ID3D12PipelineLibrary1.");

        try
        {
            return LoadPipeline(library1, key, stream.Bytes);
        }
        catch (SharpGenException)
        {
            var state = CreateStateStream(stream.Bytes);
            StorePipeline(cache.Library, key, state);
            return state;
        }
    }

    private unsafe ID3D12PipelineState CreateStateStream(byte[] stream)
    {
        using var device2 = _device.QueryInterfaceOrNull<ID3D12Device2>();
        if (device2 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 pipeline state streams require ID3D12Device2.");

        ulong debugMessageStart = GetDebugCount();
        fixed (byte* streamPtr = stream)
        {
            try
            {
                var desc = new PipelineStateStreamDescription { SizeInBytes = (nuint)stream.Length, SubObjectStream = (IntPtr)streamPtr };
                var state = device2.CreatePipelineState<ID3D12PipelineState>(desc);
                if (state == null)
                    throw new RhiException(ErrorCode.BackendFailure, "D3D12 returned a null mesh pipeline state.");
                return state;
            }
            catch (SharpGenException ex)
            {
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 failed to create mesh pipeline state: {ex.Message}{FormatDebugMessages(debugMessageStart)}");
            }
        }
    }

    private unsafe ID3D12PipelineState LoadPipeline(ID3D12PipelineLibrary1 library, string key, byte[] stream)
    {
        fixed (byte* streamPtr = stream)
        {
            var desc = new PipelineStateStreamDescription { SizeInBytes = (nuint)stream.Length, SubObjectStream = (IntPtr)streamPtr };
            var state = library.LoadPipeline(key, desc);
            if (state == null)
                throw new RhiException(ErrorCode.BackendFailure, $"D3D12 returned a null cached mesh pipeline state for '{key}'.");
            return state;
        }
    }

    private MeshStream MeshStateStream(
        MeshPipelineDesc desc,
        PipeLayoutRecord layout,
        ShaderRecord? amplification,
        ShaderRecord mesh,
        ShaderRecord? pixel)
    {
        var stream = new MeshStream();
        try
        {
            var root = new PipelineStateSubObjectTypeRootSignature(layout.RootSignature);
            var amplificationShader = amplification == null ? default : stream.PinAmplificationShader(amplification.Desc.Bytecode);
            var meshShader = stream.PinMeshShader(mesh.Desc.Bytecode);
            var pixelShader = pixel == null ? default : stream.PinPixelShader(pixel.Desc.Bytecode);
            var sampleMask = new PipelineStateSubObjectTypeSampleMask(desc.Multisample.SampleMask);
            var rasterizer = new PipelineStateSubObjectTypeRasterizer(CreateRasterizer(desc.Rasterizer));
            var depthStencil = new PipelineStateSubObjectTypeDepthStencil(CreateDepthStencil(desc.DepthStencil));
            var blend = new PipelineStateSubObjectTypeBlend(CreateBlend(desc.Blend));
            var topology = new PipelineStateSubObjectTypePrimitiveTopology(D3D12Mappings.ToTopologyType(desc.Topology));
            var renderTargetFormats = new PipelineStateSubObjectTypeRenderTargetFormats(desc.ColorFormats.Select(D3D12Mappings.ToDxgi).ToArray());
            var depthStencilFormat = new PipelineStateSubObjectTypeDepthStencilFormat(D3D12Mappings.ToDxgi(desc.DepthStencilFormat));
            var sampleDescription = new PipelineStateSubObjectTypeSampleDescription(new SampleDescription(desc.SampleCount, 0));

            if (amplification != null)
            {
                if (pixel != null)
                {
                    stream.Set(
                        new MeshAmpPixel(
                            root,
                            amplificationShader,
                            meshShader,
                            pixelShader,
                            blend,
                            sampleMask,
                            rasterizer,
                            depthStencil,
                            topology,
                            renderTargetFormats,
                            depthStencilFormat,
                            sampleDescription));
                }
                else
                {
                    stream.Set(
                        new MeshStreamAmp(
                            root,
                            amplificationShader,
                            meshShader,
                            blend,
                            sampleMask,
                            rasterizer,
                            depthStencil,
                            topology,
                            renderTargetFormats,
                            depthStencilFormat,
                            sampleDescription));
                }
            }
            else if (pixel != null)
            {
                stream.Set(
                    new MeshStreamPixel(
                        root,
                        meshShader,
                        pixelShader,
                        blend,
                        sampleMask,
                        rasterizer,
                        depthStencil,
                        topology,
                        renderTargetFormats,
                        depthStencilFormat,
                        sampleDescription));
            }
            else
            {
                stream.Set(
                    new MeshStreamNative(
                        root,
                        meshShader,
                        blend,
                        sampleMask,
                        rasterizer,
                        depthStencil,
                        topology,
                        renderTargetFormats,
                        depthStencilFormat,
                        sampleDescription));
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private unsafe PipelineCacheRecord CreatePipelineLib(PipelineCacheDesc storedDesc, byte[] initialData)
    {
        using var device1 = _device.QueryInterfaceOrNull<ID3D12Device1>();
        if (device1 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 pipeline libraries require ID3D12Device1.");

        GCHandle initialDataPin = default;
        try
        {
            var initialDataSpan = Span<byte>.Empty;
            if (initialData.Length != 0)
            {
                initialDataPin = GCHandle.Alloc(initialData, GCHandleType.Pinned);
                initialDataSpan = initialData;
            }

            device1.CreatePipelineLibrary(initialDataSpan, out ID3D12PipelineLibrary? library);
            if (library == null)
            {
                var code = initialData.Length == 0 ? ErrorCode.BackendFailure : ErrorCode.InvalidDescriptor;
                throw new RhiException(code, "D3D12 returned a null pipeline library.");
            }
            return new PipelineCacheRecord(storedDesc, library, initialDataPin);
        }
        catch (SharpGenException ex)
        {
            if (initialDataPin.IsAllocated)
                initialDataPin.Free();
            throw new RhiException(ErrorCode.InvalidDescriptor, $"D3D12 rejected the pipeline cache initial data: {ex.Message}");
        }
        catch
        {
            if (initialDataPin.IsAllocated)
                initialDataPin.Free();
            throw;
        }
    }

    private static unsafe byte[] SerializePipelineLibrary(ID3D12PipelineLibrary library)
    {
        nuint size = library.SerializedSize;
        if (size == 0)
            return [];
        if (size > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 pipeline cache data exceeds the maximum managed array size.");

        var data = new byte[(int)size];
        fixed (byte* ptr = data)
            library.Serialize((IntPtr)ptr, size);
        return data;
    }

    internal ulong GetDebugCount()
        => _infoQueue?.NumStoredMessages ?? 0;

    internal string FormatDebugMessages(ulong start)
    {
        var infoQueue = _infoQueue;
        if (infoQueue == null)
            return string.Empty;
        ulong end = infoQueue.NumStoredMessages;
        if (end <= start)
            return string.Empty;

        var builder = new StringBuilder();
        ulong emitted = 0;
        for (ulong index = start; index < end && emitted < 8; index++, emitted++)
        {
            var message = infoQueue.GetMessage(index);
            if (builder.Length != 0)
                builder.Append(" | ");
            builder.Append(message.Severity)
                .Append(' ')
                .Append(message.Id)
                .Append(": ")
                .Append(message.Description);
        }

        return builder.Length == 0 ? string.Empty : $" D3D12 debug: {builder}";
    }

    private string ComputeKey(ComputePipelineDesc desc, ShaderRecord shader)
    {
        using var bytes = new LibraryKeyBytes();
        bytes.Add((uint)PipelineKind.Compute);
        WriteLayoutKey(bytes, desc.Layout);
        WriteShaderKey(bytes, shader);
        return bytes.Build();
    }

    private string GraphicsKey(
        GraphicsPipelineDesc desc,
        ShaderRecord vertex,
        ShaderRecord pixel,
        ShaderRecord? hull,
        ShaderRecord? domain,
        ShaderRecord? geometry)
    {
        using var bytes = new LibraryKeyBytes();
        bytes.Add((uint)PipelineKind.Graphics);
        WriteLayoutKey(bytes, desc.Layout);
        WriteShaderKey(bytes, vertex);
        WriteShaderKey(bytes, pixel);
        WriteOptShader(bytes, hull);
        WriteOptShader(bytes, domain);
        WriteOptShader(bytes, geometry);
        bytes.Add((uint)desc.Topology);
        bytes.Add(desc.PatchControlPoints);
        bytes.Add(desc.SampleCount);
        WriteMultisampleKey(bytes, desc.Multisample);
        WriteRasterizerKey(bytes, desc.Rasterizer);
        WriteDepthKey(bytes, desc.DepthStencil);
        WriteBlendKey(bytes, desc.Blend);
        bytes.Add(desc.ColorFormats.Count);
        for (int index = 0; index < desc.ColorFormats.Count; index++)
            bytes.Add((uint)desc.ColorFormats[index]);
        bytes.Add((uint)desc.DepthStencilFormat);
        bytes.Add(desc.VertexBuffers.Count);
        for (int index = 0; index < desc.VertexBuffers.Count; index++)
        {
            var buffer = desc.VertexBuffers[index];
            bytes.Add(buffer.Slot);
            bytes.Add(buffer.StrideInBytes);
            bytes.Add((uint)buffer.InputRate);
            bytes.Add(buffer.InstanceStepRate);
        }

        bytes.Add(desc.VertexAttributes.Count);
        for (int index = 0; index < desc.VertexAttributes.Count; index++)
        {
            var attribute = desc.VertexAttributes[index];
            bytes.Add(attribute.Location);
            bytes.Add(attribute.BufferSlot);
            bytes.Add((uint)attribute.Format);
            bytes.Add(attribute.OffsetInBytes);
        }

        return bytes.Build();
    }

    private string MeshKey(
        MeshPipelineDesc desc,
        ShaderRecord? amplification,
        ShaderRecord mesh,
        ShaderRecord? pixel)
    {
        using var bytes = new LibraryKeyBytes();
        bytes.Add((uint)PipelineKind.Mesh);
        WriteLayoutKey(bytes, desc.Layout);
        WriteOptShader(bytes, amplification);
        WriteShaderKey(bytes, mesh);
        WriteOptShader(bytes, pixel);
        bytes.Add((uint)desc.Topology);
        bytes.Add(desc.SampleCount);
        WriteMultisampleKey(bytes, desc.Multisample);
        WriteRasterizerKey(bytes, desc.Rasterizer);
        WriteDepthKey(bytes, desc.DepthStencil);
        WriteBlendKey(bytes, desc.Blend);
        bytes.Add(desc.ColorFormats.Count);
        for (int index = 0; index < desc.ColorFormats.Count; index++)
            bytes.Add((uint)desc.ColorFormats[index]);
        bytes.Add((uint)desc.DepthStencilFormat);
        return bytes.Build();
    }

    private void WriteLayoutKey(LibraryKeyBytes bytes, PipelineLayoutHandle handle)
    {
        var layout = PipelineLayouts.Get(handle, "PipelineLayout");
        bytes.Add(layout.Desc.BindingLayouts.Count);
        for (int index = 0; index < layout.Desc.BindingLayouts.Count; index++)
        {
            var bindingLayout = BindingLayouts.Get(layout.Desc.BindingLayouts[index], "BindingLayout");
            bytes.Add(bindingLayout.Signature.Hash);
            bytes.Add(bindingLayout.Signature.SlotCount);
        }

        bytes.Add(layout.Desc.PushConstants.Count);
        for (int index = 0; index < layout.Desc.PushConstants.Count; index++)
        {
            var range = layout.Desc.PushConstants[index];
            bytes.Add((uint)range.Stages);
            bytes.Add(range.Offset);
            bytes.Add(range.SizeInBytes);
        }

        bytes.Add(layout.Desc.StaticSamplers.Count);
        for (int index = 0; index < layout.Desc.StaticSamplers.Count; index++)
        {
            var sampler = layout.Desc.StaticSamplers[index];
            bytes.Add(sampler.Set);
            bytes.Add(sampler.Binding);
            bytes.Add((uint)sampler.Stages);
            WriteSamplerKey(bytes, sampler.Sampler);
        }
    }

    private static void WriteShaderKey(LibraryKeyBytes bytes, ShaderRecord shader)
    {
        bytes.Add((uint)shader.Desc.Stage);
        bytes.Add((uint)shader.Desc.BytecodeFormat);
        ReadOnlySpan<byte> bytecode = shader.Desc.Bytecode.Span;
        bytes.Add(bytecode.Length);
        bytes.Add(bytecode);
    }

    private static void WriteOptShader(LibraryKeyBytes bytes, ShaderRecord? shader)
    {
        bytes.Add(shader != null);
        if (shader != null)
            WriteShaderKey(bytes, shader);
    }

    private static void WriteMultisampleKey(LibraryKeyBytes bytes, MultisampleDesc desc)
    {
        bytes.Add(desc.SampleCount);
        bytes.Add(desc.SampleMask);
    }

    private static void WriteRasterizerKey(LibraryKeyBytes bytes, RasterizerDesc desc)
    {
        bytes.Add((uint)desc.CullMode);
        bytes.Add((uint)desc.FillMode);
        bytes.Add(desc.FrontCounterClockwise);
        bytes.Add(desc.DepthBias);
        bytes.Add(BitConverter.SingleToUInt32Bits(desc.DepthBiasClamp));
        bytes.Add(BitConverter.SingleToUInt32Bits(desc.SlopeScaledDepthBias));
    }

    private static void WriteDepthKey(LibraryKeyBytes bytes, DepthStencilDesc desc)
    {
        bytes.Add(desc.DepthEnable);
        bytes.Add(desc.DepthWriteEnable);
        bytes.Add((uint)desc.DepthCompare);
    }

    private static void WriteBlendKey(LibraryKeyBytes bytes, BlendDesc desc)
    {
        bytes.Add(desc.AlphaToCoverageEnable);
        bytes.Add(desc.Targets.Count);
        for (int index = 0; index < desc.Targets.Count; index++)
        {
            var target = desc.Targets[index];
            bytes.Add(target.Enable);
            bytes.Add((uint)target.SourceColor);
            bytes.Add((uint)target.DestinationColor);
            bytes.Add((uint)target.ColorOp);
            bytes.Add((uint)target.SourceAlpha);
            bytes.Add((uint)target.DestinationAlpha);
            bytes.Add((uint)target.AlphaOp);
            bytes.Add((uint)target.WriteMask);
        }
    }

    private static void WriteSamplerKey(LibraryKeyBytes bytes, SamplerDesc sampler)
    {
        bytes.Add((uint)sampler.MinFilter);
        bytes.Add((uint)sampler.MagFilter);
        bytes.Add((uint)sampler.MipmapMode);
        bytes.Add((uint)sampler.AddressU);
        bytes.Add((uint)sampler.AddressV);
        bytes.Add((uint)sampler.AddressW);
        bytes.Add(BitConverter.SingleToUInt32Bits(sampler.MipLodBias));
        bytes.Add(BitConverter.SingleToUInt32Bits(sampler.MinLod));
        bytes.Add(BitConverter.SingleToUInt32Bits(sampler.MaxLod));
        bytes.Add(sampler.MaxAnisotropy);
        bytes.Add(sampler.Compare.HasValue);
        bytes.Add(sampler.Compare.HasValue ? (uint)sampler.Compare.Value : 0);
        bytes.Add((uint)sampler.BorderColor);
    }

    private sealed class LibraryKeyBytes : IDisposable
    {
        private const int InitialCapacity = 1024;

        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        private int _length;
        private bool _built;

        public void Add(bool value)
        {
            Add((byte)(value ? 1 : 0));
        }

        public void Add(int value)
        {
            Ensure(sizeof(int));
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(_length, sizeof(int)), value);
            _length += sizeof(int);
        }

        public void Add(uint value)
        {
            Ensure(sizeof(uint));
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(_length, sizeof(uint)), value);
            _length += sizeof(uint);
        }

        public void Add(ulong value)
        {
            Ensure(sizeof(ulong));
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(_length, sizeof(ulong)), value);
            _length += sizeof(ulong);
        }

        public void Add(ReadOnlySpan<byte> value)
        {
            Ensure(value.Length);
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            value.CopyTo(buffer.AsSpan(_length, value.Length));
            _length += value.Length;
        }

        public string Build()
        {
            ThrowIfBuilt();
            _built = true;
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            string key = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, _length)));
            Return();
            return key;
        }

        public void Dispose()
        {
            Return();
        }

        private void Add(byte value)
        {
            Ensure(sizeof(byte));
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            buffer[_length++] = value;
        }

        private void Ensure(int count)
        {
            ThrowIfBuilt();
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(LibraryKeyBytes));
            int required = _length + count;
            if (required <= buffer.Length)
                return;

            int capacity = buffer.Length * 2;
            while (capacity < required)
                capacity *= 2;

            byte[] next = ArrayPool<byte>.Shared.Rent(capacity);
            buffer.AsSpan(0, _length).CopyTo(next);
            ArrayPool<byte>.Shared.Return(buffer);
            _buffer = next;
        }

        private void Return()
        {
            byte[]? buffer = _buffer;
            if (buffer == null)
                return;

            ArrayPool<byte>.Shared.Return(buffer);
            _buffer = null;
            _length = 0;
        }

        private void ThrowIfBuilt()
        {
            if (_built)
                throw new InvalidOperationException("Pipeline library key is already built.");
        }
    }

    private void CreateTextureDescriptor(TextureRecord texture, TextureViewDesc desc, CpuDescriptorHandle destination, DepthStencilViewFlags dsvFlags = DepthStencilViewFlags.None)
    {
        var format = desc.Kind == ViewKind.ShaderResource
            ? D3D12Mappings.ToSrvDxgi(desc.Format)
            : D3D12Mappings.ToDxgi(desc.Format);
        switch (desc.Kind)
        {
            case ViewKind.RenderTarget:
                _device.CreateRenderTargetView(texture.Resource, CreateRtv(desc, format), destination);
                break;
            case ViewKind.DepthStencil:
                _device.CreateDepthStencilView(texture.Resource, CreateDsv(desc, format, dsvFlags), destination);
                break;
            case ViewKind.ShaderResource:
                _device.CreateShaderResourceView(texture.Resource, CreateTextureSrv(desc, format), destination);
                break;
            case ViewKind.UnorderedAccess:
                _device.CreateUnorderedAccessView(texture.Resource, null, CreateTextureUav(desc, format), destination);
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view kind {desc.Kind} is not valid.");
        }
    }

    private static RenderTargetViewDescription CreateRtv(TextureViewDesc desc, Vortice.DXGI.Format format)
    {
        var rtv = new RenderTargetViewDescription { Format = format, ViewDimension = ToRtvDimension(desc.Dimension) };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                rtv.Texture1D = new Texture1DRenderTargetView { MipSlice = desc.FirstMip };
                break;
            case TextureViewDimension.Texture1DArray:
                rtv.Texture1DArray = new Texture1DArrayRenderTargetView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture2D:
                rtv.Texture2D = new Texture2DRenderTargetView { MipSlice = desc.FirstMip, PlaneSlice = desc.PlaneSlice };
                break;
            case TextureViewDimension.Texture2DArray:
                rtv.Texture2DArray = new Texture2DArrayRenderTargetView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount, PlaneSlice = desc.PlaneSlice };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                rtv.Texture2DMS = new Texture2DMultisampledRenderTargetView();
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                rtv.Texture2DMSArray = new Texture2DMultisampledArrayRenderTargetView { FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture3D:
                rtv.Texture3D = new Texture3DRenderTargetView { MipSlice = desc.FirstMip, FirstWSlice = 0, WSize = desc.SliceCount };
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {desc.Dimension} is not supported for RTV.");
        }

        return rtv;
    }

    private static DepthStencilViewDescription CreateDsv(TextureViewDesc desc, Vortice.DXGI.Format format, DepthStencilViewFlags flags)
    {
        var dsv = new DepthStencilViewDescription { Format = format, ViewDimension = ToDsvDimension(desc.Dimension), Flags = flags };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                dsv.Texture1D = new Texture1DDepthStencilView { MipSlice = desc.FirstMip };
                break;
            case TextureViewDimension.Texture1DArray:
                dsv.Texture1DArray = new Texture1DArrayDepthStencilView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture2D:
                dsv.Texture2D = new Texture2DDepthStencilView { MipSlice = desc.FirstMip };
                break;
            case TextureViewDimension.Texture2DArray:
                dsv.Texture2DArray = new Texture2DArrayDepthStencilView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                dsv.Texture2DMS = new Texture2DMultisampledDepthStencilView();
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                dsv.Texture2DMSArray = new Texture2DMultisampledArrayDepthStencilView { FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {desc.Dimension} is not supported for DSV.");
        }

        return dsv;
    }

    private static ShaderResourceViewDescription CreateTextureSrv(TextureViewDesc desc, Vortice.DXGI.Format format)
    {
        var srv = new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ToSrvDimension(desc.Dimension),
            Shader4ComponentMapping = 5768,
        };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                srv.Texture1D = new Texture1DShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.Texture1DArray:
                srv.Texture1DArray = new Texture1DArrayShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.Texture2D:
                srv.Texture2D = new Texture2DShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, PlaneSlice = desc.PlaneSlice, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.Texture2DArray:
                srv.Texture2DArray = new Texture2DArrayShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount, PlaneSlice = desc.PlaneSlice, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                srv.Texture2DMS = new Texture2DMultisampledShaderResourceView();
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                srv.Texture2DMSArray = new Texture2DMultisampledArrayShaderResourceView { FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture3D:
                srv.Texture3D = new Texture3DShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.TextureCube:
                srv.TextureCube = new TextureCubeShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, ResourceMinLODClamp = 0 };
                break;
            case TextureViewDimension.TextureCubeArray:
                srv.TextureCubeArray = new TextureCubeArrayShaderResourceView { MostDetailedMip = desc.FirstMip, MipLevels = desc.MipCount, First2DArrayFace = desc.FirstSlice, NumCubes = desc.SliceCount / 6, ResourceMinLODClamp = 0 };
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {desc.Dimension} is not supported for SRV.");
        }

        return srv;
    }

    private static UnorderedAccessViewDescription CreateTextureUav(TextureViewDesc desc, Vortice.DXGI.Format format)
    {
        var uav = new UnorderedAccessViewDescription
        {
            Format = format,
            ViewDimension = ToUavDimension(desc.Dimension),
        };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                uav.Texture1D = new Texture1DUnorderedAccessView { MipSlice = desc.FirstMip };
                break;
            case TextureViewDimension.Texture1DArray:
                uav.Texture1DArray = new Texture1DArrayUnorderedAccessView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount };
                break;
            case TextureViewDimension.Texture2D:
                uav.Texture2D = new Texture2DUnorderedAccessView { MipSlice = desc.FirstMip, PlaneSlice = desc.PlaneSlice };
                break;
            case TextureViewDimension.Texture2DArray:
                uav.Texture2DArray = new Texture2DArrayUnorderedAccessView { MipSlice = desc.FirstMip, FirstArraySlice = desc.FirstSlice, ArraySize = desc.SliceCount, PlaneSlice = desc.PlaneSlice };
                break;
            case TextureViewDimension.Texture3D:
                uav.Texture3D = new Texture3DUnorderedAccessView { MipSlice = desc.FirstMip, FirstWSlice = 0, WSize = desc.SliceCount };
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {desc.Dimension} is not supported for UAV.");
        }

        return uav;
    }

    private void CreateBufferDescriptor(BufferRecord buffer, BufferViewDesc desc, CpuDescriptorHandle destination)
        => CreateBufferDescriptor(buffer, desc, desc.Offset, desc.SizeInBytes == 0 ? buffer.Desc.SizeInBytes - desc.Offset : desc.SizeInBytes, destination);

    private void CreateBufferDescriptor(BufferRecord buffer, BufferViewDesc desc, ulong offset, ulong size, CpuDescriptorHandle destination)
    {
        switch (desc.Kind)
        {
            case ViewKind.ConstantBuffer:
                if (offset % 256 != 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Constant buffer view offset must be 256-byte aligned.");
                ulong alignedSize = AlignUp(size, 256);
                if (offset > buffer.Desc.SizeInBytes || alignedSize > buffer.Desc.SizeInBytes - offset)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Constant buffer view aligned range exceeds the buffer.");
                _device.CreateConstantBufferView(
                    new ConstantBufferViewDescription(buffer.Resource.GPUVirtualAddress + offset, checked((uint)alignedSize)),
                    destination);
                break;
            case ViewKind.ShaderResource:
                _device.CreateShaderResourceView(buffer.Resource, CreateBufferSrv(buffer, desc, offset, size), destination);
                break;
            case ViewKind.UnorderedAccess:
                _device.CreateUnorderedAccessView(buffer.Resource, null, CreateBufferUav(buffer, desc, offset, size), destination);
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Buffer view kind {desc.Kind} is not valid for buffers.");
        }
    }

    private static ShaderResourceViewDescription CreateBufferSrv(BufferRecord buffer, BufferViewDesc desc, ulong offset, ulong size)
    {
        uint stride = desc.Raw ? 4 : desc.StrideInBytes != 0 ? desc.StrideInBytes : buffer.Desc.StrideInBytes;
        if (stride == 0 && desc.Format == Format.Unknown)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Formatted or structured buffer SRV requires a format or stride.");
        uint elementSize = checked((uint)(stride == 0 ? FormatByteSize(desc.Format) : stride));
        if (offset % elementSize != 0 || size % elementSize != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Buffer SRV offset and size must be aligned to element size {elementSize}.");
        return new ShaderResourceViewDescription
        {
            Format = desc.Raw ? Vortice.DXGI.Format.R32_Typeless : desc.Format == Format.Unknown ? Vortice.DXGI.Format.Unknown : D3D12Mappings.ToDxgi(desc.Format),
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = 5768,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = offset / elementSize,
                NumElements = checked((uint)(size / elementSize)),
                StructureByteStride = desc.Raw ? 0 : stride,
                Flags = desc.Raw ? BufferShaderResourceViewFlags.Raw : BufferShaderResourceViewFlags.None,
            },
        };
    }

    private static UnorderedAccessViewDescription CreateBufferUav(BufferRecord buffer, BufferViewDesc desc, ulong offset, ulong size)
    {
        uint stride = desc.Raw ? 4 : desc.StrideInBytes != 0 ? desc.StrideInBytes : buffer.Desc.StrideInBytes;
        if (stride == 0 && desc.Format == Format.Unknown)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Formatted or structured buffer UAV requires a format or stride.");
        uint elementSize = checked((uint)(stride == 0 ? FormatByteSize(desc.Format) : stride));
        if (offset % elementSize != 0 || size % elementSize != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Buffer UAV offset and size must be aligned to element size {elementSize}.");
        return new UnorderedAccessViewDescription
        {
            Format = desc.Raw ? Vortice.DXGI.Format.R32_Typeless : desc.Format == Format.Unknown ? Vortice.DXGI.Format.Unknown : D3D12Mappings.ToDxgi(desc.Format),
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = offset / elementSize,
                NumElements = checked((uint)(size / elementSize)),
                StructureByteStride = desc.Raw ? 0 : stride,
                Flags = desc.Raw ? BufferUnorderedAccessViewFlags.Raw : BufferUnorderedAccessViewFlags.None,
            },
        };
    }

    private static ShaderResourceViewDimension ToSrvDimension(TextureViewDimension dimension)
        => dimension switch
        {
            TextureViewDimension.Texture1D => ShaderResourceViewDimension.Texture1D,
            TextureViewDimension.Texture1DArray => ShaderResourceViewDimension.Texture1DArray,
            TextureViewDimension.Texture2D => ShaderResourceViewDimension.Texture2D,
            TextureViewDimension.Texture2DArray => ShaderResourceViewDimension.Texture2DArray,
            TextureViewDimension.Texture2DMultisampled => ShaderResourceViewDimension.Texture2DMultisampled,
            TextureViewDimension.Texture2DMultisampledArray => ShaderResourceViewDimension.Texture2DMultisampledArray,
            TextureViewDimension.Texture3D => ShaderResourceViewDimension.Texture3D,
            TextureViewDimension.TextureCube => ShaderResourceViewDimension.TextureCube,
            TextureViewDimension.TextureCubeArray => ShaderResourceViewDimension.TextureCubeArray,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {dimension} is not supported for SRV."),
        };

    private static RenderTargetViewDimension ToRtvDimension(TextureViewDimension dimension)
        => dimension switch
        {
            TextureViewDimension.Texture1D => RenderTargetViewDimension.Texture1D,
            TextureViewDimension.Texture1DArray => RenderTargetViewDimension.Texture1DArray,
            TextureViewDimension.Texture2D => RenderTargetViewDimension.Texture2D,
            TextureViewDimension.Texture2DArray => RenderTargetViewDimension.Texture2DArray,
            TextureViewDimension.Texture2DMultisampled => RenderTargetViewDimension.Texture2DMultisampled,
            TextureViewDimension.Texture2DMultisampledArray => RenderTargetViewDimension.Texture2DMultisampledArray,
            TextureViewDimension.Texture3D => RenderTargetViewDimension.Texture3D,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {dimension} is not supported for RTV."),
        };

    private static DepthStencilViewDimension ToDsvDimension(TextureViewDimension dimension)
        => dimension switch
        {
            TextureViewDimension.Texture1D => DepthStencilViewDimension.Texture1D,
            TextureViewDimension.Texture1DArray => DepthStencilViewDimension.Texture1DArray,
            TextureViewDimension.Texture2D => DepthStencilViewDimension.Texture2D,
            TextureViewDimension.Texture2DArray => DepthStencilViewDimension.Texture2DArray,
            TextureViewDimension.Texture2DMultisampled => DepthStencilViewDimension.Texture2DMultisampled,
            TextureViewDimension.Texture2DMultisampledArray => DepthStencilViewDimension.Texture2DMultisampledArray,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {dimension} is not supported for DSV."),
        };

    private static UnorderedAccessViewDimension ToUavDimension(TextureViewDimension dimension)
        => dimension switch
        {
            TextureViewDimension.Texture1D => UnorderedAccessViewDimension.Texture1D,
            TextureViewDimension.Texture1DArray => UnorderedAccessViewDimension.Texture1DArray,
            TextureViewDimension.Texture2D => UnorderedAccessViewDimension.Texture2D,
            TextureViewDimension.Texture2DArray => UnorderedAccessViewDimension.Texture2DArray,
            TextureViewDimension.Texture2DMultisampled => UnorderedAccessViewDimension.Texture2DMultisampled,
            TextureViewDimension.Texture2DMultisampledArray => UnorderedAccessViewDimension.Texture2DMultisampledArray,
            TextureViewDimension.Texture3D => UnorderedAccessViewDimension.Texture3D,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view dimension {dimension} is not supported for UAV."),
        };

    private void FreeTextureView(TextureViewRecord record)
    {
        FreeTextureDescriptor(record.Kind, record.Allocation);
        if (record.ReadOnlyDepthAllocation is { } readOnly)
            DsvDescriptors.Free(readOnly);
    }

    private void FreeTextureDescriptor(ViewKind kind, DescriptorAllocation allocation)
    {
        switch (kind)
        {
            case ViewKind.RenderTarget:
                RtvDescriptors.Free(allocation);
                break;
            case ViewKind.DepthStencil:
                DsvDescriptors.Free(allocation);
                break;
            case ViewKind.ShaderResource:
            case ViewKind.UnorderedAccess:
                CbvSrvUavDescriptors.Free(allocation);
                break;
        }
    }

    private static void BuildBindingSlots(
        BindingLayoutDesc desc,
        out ValidationSlot[] validationSlots,
        out SlotLookup[] validationSlotLookup,
        out int requiredBindingResourceCount,
        out bool supportsTransientBindings,
        out SlotLayout[] resourceSlots,
        out SlotLookup[] resourceSlotLookup,
        out int resourceDescriptorCount,
        out bool resourceSlotsNeedNullDescriptors,
        out SlotLayout[] dynamicResourceSlots,
        out SlotLookup[] dynamicResourceSlotLookup,
        out int dynamicResourceDescriptorCount,
        out bool dynamicResourceSlotsNeedNullDescriptors,
        out SlotLayout[] samplerSlots,
        out SlotLookup[] samplerSlotLookup,
        out int samplerDescriptorCount,
        out bool samplerSlotsNeedNullDescriptors,
        out LayoutSignature signature)
    {
        var resource = new List<SlotLayout>();
        var dynamicResource = new List<SlotLayout>();
        var sampler = new List<SlotLayout>();
        uint resourceBase = 0;
        uint dynamicResourceBase = 0;
        uint samplerBase = 0;
        foreach (var slot in desc.Slots
            .OrderBy(static slot => D3D12Mappings.ToRangeType(slot.Type))
            .ThenBy(static slot => slot.Binding))
        {
            if (slot.Type == BindingType.Sampler)
            {
                sampler.Add(new SlotLayout(slot.Binding, slot.Type, slot.Count, slot.Flags, samplerBase, slot.Shape));
                samplerBase += slot.Count;
            }
            else if ((slot.Flags & BindingFlags.DynamicOffset) != 0)
            {
                dynamicResource.Add(new SlotLayout(slot.Binding, slot.Type, slot.Count, slot.Flags, dynamicResourceBase, slot.Shape));
                dynamicResourceBase += slot.Count;
            }
            else
            {
                resource.Add(new SlotLayout(slot.Binding, slot.Type, slot.Count, slot.Flags, resourceBase, slot.Shape));
                resourceBase += slot.Count;
            }
        }

        resourceSlots = resource.ToArray();
        resourceSlotLookup = BuildSlotLookup(resourceSlots);
        resourceDescriptorCount = checked((int)resourceBase);
        resourceSlotsNeedNullDescriptors = HasPartialSlots(resourceSlots);
        dynamicResourceSlots = dynamicResource.ToArray();
        dynamicResourceSlotLookup = BuildSlotLookup(dynamicResourceSlots);
        dynamicResourceDescriptorCount = checked((int)dynamicResourceBase);
        dynamicResourceSlotsNeedNullDescriptors = HasPartialSlots(dynamicResourceSlots);
        samplerSlots = sampler.ToArray();
        samplerSlotLookup = BuildSlotLookup(samplerSlots);
        samplerDescriptorCount = checked((int)samplerBase);
        samplerSlotsNeedNullDescriptors = HasPartialSlots(samplerSlots);
        BuildSlots(desc, out validationSlots, out validationSlotLookup, out supportsTransientBindings);
        requiredBindingResourceCount = CountResources(validationSlots);
        signature = new LayoutSignature(RhiBindingValidation.LayoutHash(desc), desc.Slots.Count);
    }

    private static int CountResources(ValidationSlot[] validationSlots)
    {
        int count = 0;
        for (int index = 0; index < validationSlots.Length; index++)
            count = checked(count + checked((int)validationSlots[index].Count));
        return count;
    }

    private static bool HasPartialSlots(SlotLayout[] slots)
    {
        for (int index = 0; index < slots.Length; index++)
        {
            if ((slots[index].Flags & BindingFlags.PartiallyBound) != 0)
                return true;
        }

        return false;
    }

    private static void BuildSlots(
        BindingLayoutDesc desc,
        out ValidationSlot[] validationSlots,
        out SlotLookup[] validationSlotLookup,
        out bool supportsTransientBindings)
    {
        validationSlots = new ValidationSlot[desc.Slots.Count];
        supportsTransientBindings = true;
        for (int index = 0; index < desc.Slots.Count; index++)
        {
            var slot = desc.Slots[index];
            if ((slot.Flags & (BindingFlags.PartiallyBound | BindingFlags.Bindless)) != 0)
                supportsTransientBindings = false;
            validationSlots[index] = new ValidationSlot(
                slot.Binding,
                slot.Type,
                slot.Count,
                slot.Flags,
                slot.Shape,
                RhiBindingValidation.HasShapeRule(slot));
        }

        validationSlotLookup = BuildSlotLookup(validationSlots);
    }

    private static SlotLookup[] BuildSlotLookup(SlotLayout[] slots)
    {
        var lookup = new SlotLookup[slots.Length];
        for (int index = 0; index < slots.Length; index++)
            lookup[index] = new SlotLookup(slots[index].Binding, RegOf(slots[index].Type), index);

        Array.Sort(
            lookup,
            static (left, right) =>
            {
                int binding = left.Binding.CompareTo(right.Binding);
                return binding != 0 ? binding : left.RegisterClass.CompareTo(right.RegisterClass);
            });
        return lookup;
    }

    private static SlotLookup[] BuildSlotLookup(ValidationSlot[] slots)
    {
        var lookup = new SlotLookup[slots.Length];
        for (int index = 0; index < slots.Length; index++)
            lookup[index] = new SlotLookup(slots[index].Binding, RegOf(slots[index].Type), index);

        Array.Sort(
            lookup,
            static (left, right) =>
            {
                int binding = left.Binding.CompareTo(right.Binding);
                return binding != 0 ? binding : left.RegisterClass.CompareTo(right.RegisterClass);
            });
        return lookup;
    }

    private ID3D12RootSignature CreateRootSignature(
        PipelineLayoutDesc desc,
        out RootBinding[] setRootBindings,
        out LayoutSignature[] setSignatures,
        out PushRoot[] pushConstantRootBindings)
    {
        var parameters = new List<RootParameter>();
        setRootBindings = new RootBinding[desc.BindingLayouts.Count];
        setSignatures = new LayoutSignature[desc.BindingLayouts.Count];
        for (int set = 0; set < desc.BindingLayouts.Count; set++)
        {
            var layout = BindingLayouts.Get(desc.BindingLayouts[set], "BindingLayout");
            setSignatures[set] = layout.Signature;
            ValidateRegisters(layout, set);
            int resourceRoot = -1;
            int dynamicResourceRoot = -1;
            int samplerRoot = -1;
            if (layout.ResourceSlots.Length > 0)
            {
                resourceRoot = parameters.Count;
                parameters.Add(CreateDescriptorTable(layout.ResourceSlots, (uint)set));
            }

            if (layout.DynamicResourceSlots.Length > 0)
            {
                dynamicResourceRoot = parameters.Count;
                parameters.Add(CreateDescriptorTable(layout.DynamicResourceSlots, (uint)set));
            }

            if (layout.SamplerSlots.Length > 0)
            {
                samplerRoot = parameters.Count;
                parameters.Add(CreateDescriptorTable(layout.SamplerSlots, (uint)set));
            }

            setRootBindings[set] = new RootBinding(resourceRoot, dynamicResourceRoot, samplerRoot);
        }

        pushConstantRootBindings = new PushRoot[desc.PushConstants.Count];
        for (int index = 0; index < desc.PushConstants.Count; index++)
        {
            var range = desc.PushConstants[index];
            if (range.SizeInBytes % 4 != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 push constant size must be a multiple of 4 bytes.");
            int root = parameters.Count;
            parameters.Add(new RootParameter(new RootConstants(checked(PushConstantRegisterBase + (uint)index), 0, range.SizeInBytes / 4), D3D12Mappings.ToShaderVisibility(range.Stages)));
            pushConstantRootBindings[index] = new PushRoot(range.Stages, range.Offset, range.SizeInBytes, root);
        }

        var staticSamplers = desc.StaticSamplers
            .Select(static sampler => new StaticSamplerDescription(sampler.Binding, D3D12Mappings.ToFilter(sampler.Sampler), D3D12Mappings.ToAddressMode(sampler.Sampler.AddressU), D3D12Mappings.ToAddressMode(sampler.Sampler.AddressV), D3D12Mappings.ToAddressMode(sampler.Sampler.AddressW), sampler.Sampler.MipLodBias, sampler.Sampler.MaxAnisotropy, D3D12Mappings.ToComparison(sampler.Sampler.Compare), ToBorderColor(sampler.Sampler.BorderColor), sampler.Sampler.MinLod, sampler.Sampler.MaxLod, D3D12Mappings.ToShaderVisibility(sampler.Stages), sampler.Set))
            .ToArray();
        var rootDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters.ToArray(), staticSamplers);
        return _device.CreateRootSignature(rootDesc, RootSignatureVersion.Version10);
    }

    private static RootParameter CreateDescriptorTable(SlotLayout[] slots, uint registerSpace)
    {
        var ranges = slots.Select(slot => new DescriptorRange(D3D12Mappings.ToRangeType(slot.Type), slot.Count, slot.Binding, registerSpace, slot.BaseDescriptor)).ToArray();
        return new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.All);
    }

    private static StaticBorderColor ToBorderColor(BorderColor color)
        => color switch
        {
            BorderColor.TransparentBlack => StaticBorderColor.TransparentBlack,
            BorderColor.OpaqueBlack => StaticBorderColor.OpaqueBlack,
            BorderColor.OpaqueWhite => StaticBorderColor.OpaqueWhite,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Border color {color} is not defined."),
        };

    private static void ValidateRegisters(LayoutRecord layout, int set)
    {
        if (set != 0)
            return;

        foreach (var slot in layout.Desc.Slots)
        {
            if (slot.Type != BindingType.ConstantBuffer)
                continue;
            ulong end = (ulong)slot.Binding + slot.Count;
            if (end > PushConstantRegisterBase)
                throw new RhiException(ErrorCode.InvalidDescriptor, "D3D12 set 0 constant buffer registers b100+ are reserved for push constants.");
        }
    }

    private ShaderDescriptorAllocation? CreateBindingDescriptors(
        SlotLayout[] slots,
        SlotLookup[] slotLookup,
        int descriptorCount,
        bool slotsNeedNullDescriptors,
        ReadOnlySpan<BindingResourceDesc> resources,
        DescriptorHeapType type,
        ReadOnlySpan<DynamicOffset> dynamicOffsets = default,
        bool transient = false)
    {
        if (descriptorCount == 0)
            return null;
        var allocation = AllocateShaderDescriptors(type, descriptorCount, transient);
        try
        {
            WriteBindingDescriptors(slots, slotLookup, slotsNeedNullDescriptors, resources, allocation, type, dynamicOffsets, transient);
            return allocation;
        }
        catch
        {
            FreeShaderDescriptor(allocation, type);
            throw;
        }
    }

    private ShaderDescriptorAllocation AllocateShaderDescriptors(DescriptorHeapType type, int count, bool transient)
        => type == DescriptorHeapType.Sampler
            ? transient ? ShaderSamplerDescriptors.AllocateTransient(count) : ShaderSamplerDescriptors.Allocate(count)
            : transient ? ShaderResourceDescriptors.AllocateTransient(count) : ShaderResourceDescriptors.Allocate(count);

    private void WriteBindingDescriptors(
        SlotLayout[] slots,
        SlotLookup[] slotLookup,
        bool slotsNeedNullDescriptors,
        ReadOnlySpan<BindingResourceDesc> resources,
        ShaderDescriptorAllocation? allocation,
        DescriptorHeapType type,
        ReadOnlySpan<DynamicOffset> dynamicOffsets = default,
        bool transient = false)
    {
        if (slots.Length == 0)
            return;
        if (allocation == null)
            throw new RhiException(ErrorCode.ValidationFailure, "Descriptor allocation is missing for a non-empty binding layout.");

        int nullWriteCount = 0;
        int copyWriteCount = 0;
        int copyRangeCountTotal = 0;
        int copyApiCallCount = 0;
        int dynamicWriteCount = 0;

        if (slotsNeedNullDescriptors)
        {
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                var slot = slots[slotIndex];
                if ((slot.Flags & BindingFlags.PartiallyBound) == 0)
                    continue;
                for (uint array = 0; array < slot.Count; array++)
                {
                    var destination = type == DescriptorHeapType.Sampler
                        ? ShaderSamplerDescriptors.CpuAt(allocation.Value, checked((int)(slot.BaseDescriptor + array)))
                        : ShaderResourceDescriptors.CpuAt(allocation.Value, checked((int)(slot.BaseDescriptor + array)));
                    CreateNullDescriptor(slot, destination);
                    nullWriteCount++;
                }
            }
        }

        int copyRangeCapacity = Math.Min(resources.Length, DescriptorCopyRangeStackCapacity);
        Span<CpuDescriptorHandle> copyDestinationStarts = stackalloc CpuDescriptorHandle[copyRangeCapacity];
        Span<uint> copyDestinationSizes = stackalloc uint[copyRangeCapacity];
        Span<CpuDescriptorHandle> copySourceStarts = stackalloc CpuDescriptorHandle[copyRangeCapacity];
        Span<uint> copySourceSizes = stackalloc uint[copyRangeCapacity];
        CpuDescriptorHandle[]? pooledDestinationStarts = null;
        uint[]? pooledDestinationSizes = null;
        CpuDescriptorHandle[]? pooledSourceStarts = null;
        uint[]? pooledSourceSizes = null;
        if (resources.Length > DescriptorCopyRangeStackCapacity)
        {
            pooledDestinationStarts = ArrayPool<CpuDescriptorHandle>.Shared.Rent(resources.Length);
            pooledDestinationSizes = ArrayPool<uint>.Shared.Rent(resources.Length);
            pooledSourceStarts = ArrayPool<CpuDescriptorHandle>.Shared.Rent(resources.Length);
            pooledSourceSizes = ArrayPool<uint>.Shared.Rent(resources.Length);
            copyDestinationStarts = pooledDestinationStarts.AsSpan(0, resources.Length);
            copyDestinationSizes = pooledDestinationSizes.AsSpan(0, resources.Length);
            copySourceStarts = pooledSourceStarts.AsSpan(0, resources.Length);
            copySourceSizes = pooledSourceSizes.AsSpan(0, resources.Length);
        }

        try
        {
            CpuDescriptorHandle copyRangeDestination = default;
            CpuDescriptorHandle copyRangeSource = default;
            int copyRangeCount = 0;
            int copyRangeLastDestinationIndex = -1;
            int copyRangeLastSourceIndex = -1;
            int queuedCopyRanges = 0;

            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                int slotIndex = FindSlot(slotLookup, resource.Binding, resource.ResourceType);
                if (slotIndex < 0)
                    continue;

                var slot = slots[slotIndex];
                int destinationIndex = checked((int)(slot.BaseDescriptor + resource.ArrayElement));
                var destination = type == DescriptorHeapType.Sampler
                    ? ShaderSamplerDescriptors.CpuAt(allocation.Value, destinationIndex)
                    : ShaderResourceDescriptors.CpuAt(allocation.Value, destinationIndex);
                var dynamicOffsetIndex = (slot.Flags & BindingFlags.DynamicOffset) != 0
                    ? RhiBindingValidation.FindOffset(dynamicOffsets, resource.Binding, resource.ArrayElement)
                    : -1;
                if (dynamicOffsetIndex >= 0)
                {
                    FlushDescCopies(
                        copyDestinationStarts,
                        copyDestinationSizes,
                        copySourceStarts,
                        copySourceSizes,
                        type,
                        copyRangeDestination,
                        copyRangeSource,
                        ref copyRangeCount,
                        ref copyRangeLastDestinationIndex,
                        ref copyRangeLastSourceIndex,
                        ref queuedCopyRanges,
                        ref copyWriteCount,
                        ref copyRangeCountTotal,
                        ref copyApiCallCount,
                        transient);
                    CreateDynamicBuffer(resource, dynamicOffsets[dynamicOffsetIndex].OffsetInBytes, destination);
                    dynamicWriteCount++;
                    continue;
                }

                var source = SourceDescriptor(resource);
                if (copyRangeCount != 0
                    && destinationIndex == copyRangeLastDestinationIndex + 1
                    && source.Index == copyRangeLastSourceIndex + 1)
                {
                    copyRangeCount++;
                    copyRangeLastDestinationIndex = destinationIndex;
                    copyRangeLastSourceIndex = source.Index;
                    continue;
                }

                QueueDescCopy(
                    copyDestinationStarts,
                    copyDestinationSizes,
                    copySourceStarts,
                    copySourceSizes,
                    copyRangeDestination,
                    copyRangeSource,
                    ref copyRangeCount,
                    ref copyRangeLastDestinationIndex,
                    ref copyRangeLastSourceIndex,
                    ref queuedCopyRanges,
                    ref copyWriteCount,
                    ref copyRangeCountTotal);
                copyRangeDestination = destination;
                copyRangeSource = source.Cpu;
                copyRangeCount = 1;
                copyRangeLastDestinationIndex = destinationIndex;
                copyRangeLastSourceIndex = source.Index;
            }

            FlushDescCopies(
                copyDestinationStarts,
                copyDestinationSizes,
                copySourceStarts,
                copySourceSizes,
                type,
                copyRangeDestination,
                copyRangeSource,
                ref copyRangeCount,
                ref copyRangeLastDestinationIndex,
                ref copyRangeLastSourceIndex,
                ref queuedCopyRanges,
                ref copyWriteCount,
                ref copyRangeCountTotal,
                ref copyApiCallCount,
                transient);
        }
        finally
        {
            if (pooledDestinationStarts != null)
                ArrayPool<CpuDescriptorHandle>.Shared.Return(pooledDestinationStarts);
            if (pooledDestinationSizes != null)
                ArrayPool<uint>.Shared.Return(pooledDestinationSizes);
            if (pooledSourceStarts != null)
                ArrayPool<CpuDescriptorHandle>.Shared.Return(pooledSourceStarts);
            if (pooledSourceSizes != null)
                ArrayPool<uint>.Shared.Return(pooledSourceSizes);
        }

    }

    private static void QueueDescCopy(
        Span<CpuDescriptorHandle> destinationStarts,
        Span<uint> destinationSizes,
        Span<CpuDescriptorHandle> sourceStarts,
        Span<uint> sourceSizes,
        CpuDescriptorHandle copyRangeDestination,
        CpuDescriptorHandle copyRangeSource,
        ref int copyRangeCount,
        ref int copyRangeLastDestinationIndex,
        ref int copyRangeLastSourceIndex,
        ref int queuedCopyRanges,
        ref int copyWriteCount,
        ref int copyRangeCountTotal)
    {
        if (copyRangeCount == 0)
            return;

        uint descriptorCount = checked((uint)copyRangeCount);
        destinationStarts[queuedCopyRanges] = copyRangeDestination;
        destinationSizes[queuedCopyRanges] = descriptorCount;
        sourceStarts[queuedCopyRanges] = copyRangeSource;
        sourceSizes[queuedCopyRanges] = descriptorCount;
        queuedCopyRanges++;
        copyWriteCount += copyRangeCount;
        copyRangeCountTotal++;
        copyRangeCount = 0;
        copyRangeLastDestinationIndex = -1;
        copyRangeLastSourceIndex = -1;
    }

    private void FlushDescCopies(
        Span<CpuDescriptorHandle> destinationStarts,
        Span<uint> destinationSizes,
        Span<CpuDescriptorHandle> sourceStarts,
        Span<uint> sourceSizes,
        DescriptorHeapType type,
        CpuDescriptorHandle copyRangeDestination,
        CpuDescriptorHandle copyRangeSource,
        ref int copyRangeCount,
        ref int copyRangeLastDestinationIndex,
        ref int copyRangeLastSourceIndex,
        ref int queuedCopyRanges,
        ref int copyWriteCount,
        ref int copyRangeCountTotal,
        ref int copyApiCallCount,
        bool transient)
    {
        QueueDescCopy(
            destinationStarts,
            destinationSizes,
            sourceStarts,
            sourceSizes,
            copyRangeDestination,
            copyRangeSource,
            ref copyRangeCount,
            ref copyRangeLastDestinationIndex,
            ref copyRangeLastSourceIndex,
            ref queuedCopyRanges,
            ref copyWriteCount,
            ref copyRangeCountTotal);

        if (queuedCopyRanges == 0)
            return;

        if (queuedCopyRanges == 1)
        {
            _device.CopyDescriptorsSimple(destinationSizes[0], destinationStarts[0], sourceStarts[0], type);
        }
        else
        {
            _device.CopyDescriptors(
                checked((uint)queuedCopyRanges),
                destinationStarts[..queuedCopyRanges],
                destinationSizes[..queuedCopyRanges],
                checked((uint)queuedCopyRanges),
                sourceStarts[..queuedCopyRanges],
                sourceSizes[..queuedCopyRanges],
                type);
        }

        copyApiCallCount++;
        queuedCopyRanges = 0;
    }

    private void WriteDescUpdates(
        SlotLayout[] slots,
        ReadOnlySpan<BindingResourceDesc> updates,
        ShaderDescriptorAllocation? allocation,
        DescriptorHeapType type)
    {
        if (updates.Length == 0)
            return;
        if (slots.Length == 0)
            return;
        if (allocation == null)
            throw new RhiException(ErrorCode.ValidationFailure, "Descriptor allocation is missing for a non-empty binding layout.");

        for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
        {
            var update = updates[updateIndex];
            int slotIndex = update.ResourceType == BindingType.None
                ? FindSingleSlot(slots, update.Binding)
                : FindSlot(slots, update.Binding, update.ResourceType);
            if (slotIndex < 0)
                continue;

            var slot = slots[slotIndex];
            if (update.ArrayElement >= slot.Count)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {update.Binding} array element {update.ArrayElement} exceeds descriptor count {slot.Count}.");
            var destination = type == DescriptorHeapType.Sampler
                ? ShaderSamplerDescriptors.CpuAt(allocation.Value, checked((int)(slot.BaseDescriptor + update.ArrayElement)))
                : ShaderResourceDescriptors.CpuAt(allocation.Value, checked((int)(slot.BaseDescriptor + update.ArrayElement)));
            if (update.ResourceType == BindingType.None)
                CreateNullDescriptor(slot, destination);
            else
            {
                var source = SourceDescriptor(update);
                _device.CopyDescriptorsSimple(1, destination, source.Cpu, type);
            }
        }
    }

    private ulong ComputeRootHash(PipelineLayoutDesc desc)
    {
        ulong hash = RootSignatureFingerprintOffsetBasis;
        hash = HashRootSig(hash, checked((uint)desc.BindingLayouts.Count));
        for (int index = 0; index < desc.BindingLayouts.Count; index++)
        {
            var layout = BindingLayouts.Get(desc.BindingLayouts[index], "PipelineBindingLayout");
            hash = HashRootSig(hash, layout.Signature.Hash);
            hash = HashRootSig(hash, checked((uint)layout.Signature.SlotCount));
        }

        hash = HashRootSig(hash, checked((uint)desc.PushConstants.Count));
        for (int index = 0; index < desc.PushConstants.Count; index++)
        {
            var range = desc.PushConstants[index];
            hash = HashRootSig(hash, (uint)range.Stages);
            hash = HashRootSig(hash, range.Offset);
            hash = HashRootSig(hash, range.SizeInBytes);
        }

        hash = HashRootSig(hash, checked((uint)desc.StaticSamplers.Count));
        for (int index = 0; index < desc.StaticSamplers.Count; index++)
        {
            var sampler = desc.StaticSamplers[index];
            hash = HashRootSig(hash, sampler.Set);
            hash = HashRootSig(hash, sampler.Binding);
            hash = HashRootSig(hash, (uint)sampler.Stages);
            hash = RhiBindingValidation.HashSamplerShape(hash, sampler.Sampler);
        }

        return hash;
    }

    private static ulong HashRootSig(ulong hash, ulong value)
    {
        hash = HashRootSig(hash, (uint)value);
        return HashRootSig(hash, (uint)(value >> 32));
    }

    private static ulong HashRootSig(ulong hash, uint value)
    {
        hash ^= value;
        hash *= RootSignatureFingerprintPrime;
        return hash;
    }

    private static int FindSlot(SlotLookup[] slotLookup, uint binding, BindingType resourceType)
    {
        var registerClass = RegOf(resourceType);
        int low = 0;
        int high = slotLookup.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            var candidate = slotLookup[mid];
            int compare = candidate.Binding.CompareTo(binding);
            if (compare == 0)
                compare = candidate.RegisterClass.CompareTo(registerClass);
            if (compare == 0)
                return candidate.SlotIndex;
            if (compare < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return -1;
    }

    private static int FindSlot(SlotLayout[] slots, uint binding, BindingType resourceType)
    {
        var registerClass = RegOf(resourceType);
        for (int index = 0; index < slots.Length; index++)
        {
            if (slots[index].Binding == binding && RegOf(slots[index].Type) == registerClass)
                return index;
        }

        return -1;
    }

    private static int FindSlot(ValidationSlot[] slots, uint binding)
    {
        for (int index = 0; index < slots.Length; index++)
        {
            if (slots[index].Binding == binding)
                return index;
        }

        return -1;
    }

    private static void CheckDupKeys(Span<BindingKey> keys)
    {
        if (keys.Length <= 1)
            return;

        keys.Sort();
        for (int index = 1; index < keys.Length; index++)
        {
            if (keys[index - 1].CompareTo(keys[index]) == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {keys[index].Binding} array index {keys[index].ArrayElement} is duplicated.");
        }
    }

    private static RegisterClass RegOf(BindingType type)
        => type switch
        {
            BindingType.ConstantBuffer => RegisterClass.ConstantBuffer,
            BindingType.StorageBufferRead
                or BindingType.RawBufferRead
                or BindingType.TextureRead
                or BindingType.AccelerationStructure => RegisterClass.ShaderResource,
            BindingType.StorageBufferReadWrite
                or BindingType.RawBufferReadWrite
                or BindingType.TextureReadWrite => RegisterClass.UnorderedAccess,
            BindingType.Sampler => RegisterClass.Sampler,
            BindingType.None => RegisterClass.None,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding type {type} is not defined."),
        };

    private readonly record struct BindingKey(uint Binding, uint ArrayElement, RegisterClass RegisterClass) : IComparable<BindingKey>
    {
        public int CompareTo(BindingKey other)
        {
            int binding = Binding.CompareTo(other.Binding);
            if (binding != 0)
                return binding;
            int arrayElement = ArrayElement.CompareTo(other.ArrayElement);
            return arrayElement != 0 ? arrayElement : RegisterClass.CompareTo(other.RegisterClass);
        }
    }

    private static int FindSingleSlot(SlotLayout[] slots, uint binding)
    {
        int match = -1;
        for (int index = 0; index < slots.Length; index++)
        {
            if (slots[index].Binding != binding)
                continue;
            if (match >= 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {binding} is ambiguous across descriptor register classes.");
            match = index;
        }

        return match;
    }

    private void CreateDynamicBuffer(BindingResourceDesc resource, uint dynamicOffset, CpuDescriptorHandle destination)
    {
        var view = BufferViews.Get(resource.BufferView, "DynamicOffsetBufferView");
        var buffer = Buffers.Get(view.Buffer, "DynamicOffsetBuffer");
        ulong baseSize = BufferViewSize(buffer.Desc, view.Desc);
        if (dynamicOffset >= baseSize)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset for binding {resource.Binding} leaves an empty buffer view range.");
        CreateBufferDescriptor(buffer, view.Desc, checked(view.Desc.Offset + dynamicOffset), baseSize - dynamicOffset, destination);
    }

    private SourceDescriptor SourceDescriptor(BindingResourceDesc resource)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
            case BindingType.RawBufferRead:
            case BindingType.RawBufferReadWrite:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                return new SourceDescriptor(view.Descriptor, view.Allocation.Index);
            }
            case BindingType.TextureRead:
            case BindingType.TextureReadWrite:
            {
                var view = TextureViews.Get(resource.TextureView, "TextureView");
                return new SourceDescriptor(view.Descriptor, view.Allocation.Index);
            }
            case BindingType.AccelerationStructure:
            {
                var accelerationStructure = AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                return new SourceDescriptor(accelerationStructure.Descriptor, accelerationStructure.Allocation.Index);
            }
            case BindingType.Sampler:
            {
                var sampler = Samplers.Get(resource.SamplerHandle, "Sampler");
                return new SourceDescriptor(sampler.Descriptor, sampler.Allocation.Index);
            }
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by D3D12 backend.");
        }
    }

    private void CreateNullDescriptor(SlotLayout slot, CpuDescriptorHandle destination)
    {
        switch (slot.Type)
        {
            case BindingType.ConstantBuffer:
                _device.CreateConstantBufferView(null, destination);
                break;
            case BindingType.StorageBufferRead:
                _device.CreateShaderResourceView(null, new ShaderResourceViewDescription
                {
                    Format = NullBufferFormat(slot.Shape),
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = 5768,
                    Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = 0, StructureByteStride = NullBufferStride(slot.Shape) },
                }, destination);
                break;
            case BindingType.RawBufferRead:
                _device.CreateShaderResourceView(null, new ShaderResourceViewDescription
                {
                    Format = Vortice.DXGI.Format.R32_Typeless,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = 5768,
                    Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = 0, StructureByteStride = 0, Flags = BufferShaderResourceViewFlags.Raw },
                }, destination);
                break;
            case BindingType.StorageBufferReadWrite:
                _device.CreateUnorderedAccessView(null, null, new UnorderedAccessViewDescription
                {
                    Format = NullBufferFormat(slot.Shape),
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 0, StructureByteStride = NullBufferStride(slot.Shape) },
                }, destination);
                break;
            case BindingType.RawBufferReadWrite:
                _device.CreateUnorderedAccessView(null, null, new UnorderedAccessViewDescription
                {
                    Format = Vortice.DXGI.Format.R32_Typeless,
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 0, StructureByteStride = 0, Flags = BufferUnorderedAccessViewFlags.Raw },
                }, destination);
                break;
            case BindingType.TextureRead:
                _device.CreateShaderResourceView(null, CreateTextureSrv(CreateNullView(ViewKind.ShaderResource, slot.Shape), D3D12Mappings.ToSrvDxgi(slot.Shape.Format)), destination);
                break;
            case BindingType.TextureReadWrite:
                _device.CreateUnorderedAccessView(null, null, CreateTextureUav(CreateNullView(ViewKind.UnorderedAccess, slot.Shape), D3D12Mappings.ToDxgi(slot.Shape.Format)), destination);
                break;
            case BindingType.AccelerationStructure:
                _device.CreateShaderResourceView(null, new ShaderResourceViewDescription
                {
                    Format = Vortice.DXGI.Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
                    Shader4ComponentMapping = 5768,
                    RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = 0 },
                }, destination);
                break;
            case BindingType.Sampler:
                var sampler = CreateSamplerDescription(slot.Shape.Sampler);
                _device.CreateSampler(ref sampler, destination);
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {slot.Type} does not support null descriptors in the D3D12 backend.");
        }
    }

    private static Vortice.DXGI.Format NullBufferFormat(BindShapeDesc shape)
        => shape.Format == Format.Unknown ? Vortice.DXGI.Format.Unknown : D3D12Mappings.ToDxgi(shape.Format);

    private static uint NullBufferStride(BindShapeDesc shape)
        => shape.Format == Format.Unknown ? shape.StrideInBytes : 0;

    private static TextureViewDesc CreateNullView(ViewKind kind, BindShapeDesc shape)
        => new()
        {
            Kind = kind,
            Dimension = shape.TextureDimension,
            Format = shape.Format,
            MipCount = 1,
            SliceCount = NullSliceCount(shape.TextureDimension),
        };

    private static uint NullSliceCount(TextureViewDimension dimension)
        => dimension is TextureViewDimension.TextureCube or TextureViewDimension.TextureCubeArray ? 6u : 1u;

    private void CollectBindMeta(
        ReadOnlySpan<BindingResourceDesc> resources,
        out BindState[] bindStates,
        out BufferHandle[] buffers,
        out TextureHandle[] textures,
        out AccelerationStructureHandle[] accelerationStructures)
    {
        var bindStateList = new List<BindState>();
        var bufferList = new List<BufferHandle>();
        var textureList = new List<TextureHandle>();
        var accelerationStructureList = new List<AccelerationStructureHandle>();
        foreach (var resource in resources)
            AddBindMeta(resource, bindStateList, bufferList, textureList, accelerationStructureList);
        bindStates = bindStateList.ToArray();
        buffers = bufferList.ToArray();
        textures = textureList.ToArray();
        accelerationStructures = accelerationStructureList.ToArray();
    }

    private void AddBindMeta(
        BindingResourceDesc resource,
        List<BindState> bindStates,
        List<BufferHandle> buffers,
        List<TextureHandle> textures,
        List<AccelerationStructureHandle> accelerationStructures)
    {
        var state = BindRules.Resolve(resource.ResourceType);
        switch (state.Target)
        {
            case BindTarget.BufferView:
                AddBufferMeta(resource.BufferView, state.State, state.Label, bindStates, buffers);
                break;
            case BindTarget.TextureView:
                AddTextureMeta(resource.TextureView, state.State, state.Label, bindStates, textures);
                break;
            case BindTarget.AccelerationStructure:
                AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                AddUnique(accelerationStructures, resource.AccelerationStructure);
                break;
            case BindTarget.Sampler:
            case BindTarget.None:
                break;
            default:
                throw new RhiException(
                    ErrorCode.UnsupportedFeature,
                    $"Binding state target {state.Target} is not supported by D3D12 backend.");
        }
    }

    private void AddBufferMeta(
        BufferViewHandle view,
        ResourceState requiredState,
        string label,
        List<BindState> bindStates,
        List<BufferHandle> buffers)
    {
        var record = BufferViews.Get(view, "BufferView");
        AddUnique(buffers, record.Buffer);
        bindStates.Add(BindState.ForBuffer(record.Buffer, requiredState, label));
    }

    private void AddTextureMeta(
        TextureViewHandle view,
        ResourceState requiredState,
        string label,
        List<BindState> bindStates,
        List<TextureHandle> textures)
    {
        var viewRecord = TextureViews.Get(view, "TextureView");
        var texture = Textures.Get(viewRecord.Texture, "Texture");
        var range = ActualViewRange(texture.Desc, viewRecord.Desc);
        AddUnique(textures, viewRecord.Texture);
        bindStates.Add(BindState.ForTexture(viewRecord.Texture, range, requiredState, label));
    }

    private static SubresourceRange ActualViewRange(TextureDesc texture, TextureViewDesc view)
    {
        uint mipCount = view.MipCount == uint.MaxValue ? texture.MipLevels - view.FirstMip : view.MipCount;
        uint sliceCount = view.SliceCount == uint.MaxValue ? texture.ArraySize - view.FirstSlice : view.SliceCount;
        var range = new SubresourceRange(view.FirstMip, mipCount, view.FirstSlice, sliceCount);
        RhiCommandValidation.ValidateSubresource(texture, range);
        return range;
    }

    private static void AddUnique<T>(List<T> values, T value)
        where T : struct, IEquatable<T>
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(value))
                return;
        }

        values.Add(value);
    }

    private void ValidateBindingResources(LayoutRecord layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        if (layout.SupportsTransientBindings && resources.Length == layout.RequiredBindingResourceCount)
        {
            ValidateComplete(layout, resources);
            return;
        }

        ValidateCounted(layout, resources);
    }

    private void ValidateComplete(
        LayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources)
    {
        BindingKey[]? rentedKeys = null;
        int[]? rentedSlotIndices = null;
        Span<BindingKey> resourceKeys = resources.Length <= BindingValidationStackKeyLimit
            ? stackalloc BindingKey[resources.Length]
            : (rentedKeys = ArrayPool<BindingKey>.Shared.Rent(resources.Length)).AsSpan(0, resources.Length);
        Span<int> resourceSlotIndices = resources.Length <= BindingValidationStackKeyLimit
            ? stackalloc int[resources.Length]
            : (rentedSlotIndices = ArrayPool<int>.Shared.Rent(resources.Length)).AsSpan(0, resources.Length);

        try
        {
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                int slotIndex = resource.ResourceType == BindingType.None
                    ? FindSlot(layout.ValidationSlots, resource.Binding)
                    : FindSlot(layout.ValidationSlotLookup, resource.Binding, resource.ResourceType);
                if (slotIndex < 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding resource targets undeclared binding {resource.Binding} for {resource.ResourceType}.");

                var slot = layout.ValidationSlots[slotIndex];
                resourceSlotIndices[resourceIndex] = slotIndex;
                resourceKeys[resourceIndex] = new BindingKey(resource.Binding, resource.ArrayElement, RegOf(resource.ResourceType));
                if (resource.ResourceType != slot.Type)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} expects {slot.Type}, got {resource.ResourceType}.");
                if (resource.ArrayElement >= slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} array index {resource.ArrayElement} exceeds descriptor count {slot.Count}.");
            }

            CheckDupKeys(resourceKeys);

            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                var slot = layout.ValidationSlots[resourceSlotIndices[resourceIndex]];
                ValidateBindingResource(slot, resource);
            }
        }
        finally
        {
            if (rentedKeys != null)
                ArrayPool<BindingKey>.Shared.Return(rentedKeys);
            if (rentedSlotIndices != null)
                ArrayPool<int>.Shared.Return(rentedSlotIndices);
        }
    }

    private void ValidateCounted(
        LayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources)
    {
        int slotCount = layout.ValidationSlots.Length;
        int[]? rentedCounts = null;
        BindingKey[]? rentedKeys = null;
        int[]? rentedSlotIndices = null;
        Span<int> resourceCounts = slotCount <= BindingValidationStackKeyLimit
            ? stackalloc int[slotCount]
            : (rentedCounts = ArrayPool<int>.Shared.Rent(slotCount)).AsSpan(0, slotCount);
        Span<BindingKey> resourceKeys = resources.Length <= BindingValidationStackKeyLimit
            ? stackalloc BindingKey[resources.Length]
            : (rentedKeys = ArrayPool<BindingKey>.Shared.Rent(resources.Length)).AsSpan(0, resources.Length);
        Span<int> resourceSlotIndices = resources.Length <= BindingValidationStackKeyLimit
            ? stackalloc int[resources.Length]
            : (rentedSlotIndices = ArrayPool<int>.Shared.Rent(resources.Length)).AsSpan(0, resources.Length);

        resourceCounts.Clear();
        try
        {
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                int slotIndex = resource.ResourceType == BindingType.None
                    ? FindSlot(layout.ValidationSlots, resource.Binding)
                    : FindSlot(layout.ValidationSlotLookup, resource.Binding, resource.ResourceType);
                if (slotIndex < 0)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding resource targets undeclared binding {resource.Binding} for {resource.ResourceType}.");

                var slot = layout.ValidationSlots[slotIndex];
                resourceCounts[slotIndex]++;
                resourceSlotIndices[resourceIndex] = slotIndex;
                resourceKeys[resourceIndex] = new BindingKey(resource.Binding, resource.ArrayElement, RegOf(resource.ResourceType));
                if (resource.ResourceType != slot.Type)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} expects {slot.Type}, got {resource.ResourceType}.");
                if (resource.ArrayElement >= slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} array index {resource.ArrayElement} exceeds descriptor count {slot.Count}.");
            }

            CheckDupKeys(resourceKeys);

            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var slot = layout.ValidationSlots[slotIndex];
                int resourceCount = resourceCounts[slotIndex];
                if ((slot.Flags & BindingFlags.PartiallyBound) == 0 && resourceCount != slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} expects {slot.Count} descriptors, got {resourceCount}.");
                if (resourceCount > slot.Count)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding {slot.Binding} has {resourceCount} descriptors, exceeding declared count {slot.Count}.");
            }

            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                var resource = resources[resourceIndex];
                var slot = layout.ValidationSlots[resourceSlotIndices[resourceIndex]];
                ValidateBindingResource(slot, resource);
            }
        }
        finally
        {
            if (rentedCounts != null)
                ArrayPool<int>.Shared.Return(rentedCounts);
            if (rentedKeys != null)
                ArrayPool<BindingKey>.Shared.Return(rentedKeys);
            if (rentedSlotIndices != null)
                ArrayPool<int>.Shared.Return(rentedSlotIndices);
        }
    }

    internal void ValidateDynamicOffsets(
        LayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        if (!layout.HasDynamicResourceSlots)
        {
            if (!dynamicOffsets.IsEmpty)
                RhiBindingValidation.ValidateOffsets(layout.Desc, resources, dynamicOffsets);
            return;
        }

        RhiBindingValidation.ValidateOffsets(layout.Desc, resources, dynamicOffsets);
        foreach (var dynamicOffset in dynamicOffsets)
        {
            int resourceIndex = RhiBindingValidation.FindResourceIndex(resources, dynamicOffset.Binding, dynamicOffset.ArrayElement);
            if (resourceIndex < 0)
                throw new RhiException(ErrorCode.ValidationFailure, "Validated dynamic offset lost its binding resource.");
            var resource = resources[resourceIndex];
            var view = BufferViews.Get(resource.BufferView, "DynamicOffsetBufferView");
            var buffer = Buffers.Get(view.Buffer, "DynamicOffsetBuffer");
            ulong alignment = resource.ResourceType == BindingType.ConstantBuffer
                ? Limits.MinConstantBufferOffsetAlignment
                : Limits.MinStorageBufferOffsetAlignment;
            if (dynamicOffset.OffsetInBytes % alignment != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset for binding {dynamicOffset.Binding} must be aligned to {alignment} bytes.");
            ulong size = BufferViewSize(buffer.Desc, view.Desc);
            if (dynamicOffset.OffsetInBytes >= size)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset for binding {dynamicOffset.Binding} leaves an empty buffer view range.");
        }
    }

    private static ulong BufferViewSize(BufferDesc buffer, BufferViewDesc view)
    {
        if (view.Offset > buffer.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer view offset exceeds buffer size.");
        return view.SizeInBytes == 0 ? buffer.SizeInBytes - view.Offset : view.SizeInBytes;
    }

    private void ValidateBindingResource(ValidationSlot slot, BindingResourceDesc resource)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                EnsureBufferKind(view, ViewKind.ConstantBuffer);
                break;
            }
            case BindingType.StorageBufferRead:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                EnsureBufferKind(view, ViewKind.ShaderResource);
                EnsureBufferRaw(view, expectedRaw: false, resource.ResourceType);
                ValidateBufferShape(slot, view);
                break;
            }
            case BindingType.RawBufferRead:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                EnsureBufferKind(view, ViewKind.ShaderResource);
                EnsureBufferRaw(view, expectedRaw: true, resource.ResourceType);
                break;
            }
            case BindingType.StorageBufferReadWrite:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                EnsureBufferKind(view, ViewKind.UnorderedAccess);
                EnsureBufferRaw(view, expectedRaw: false, resource.ResourceType);
                ValidateBufferShape(slot, view);
                break;
            }
            case BindingType.RawBufferReadWrite:
            {
                var view = BufferViews.Get(resource.BufferView, "BufferView");
                EnsureBufferKind(view, ViewKind.UnorderedAccess);
                EnsureBufferRaw(view, expectedRaw: true, resource.ResourceType);
                break;
            }
            case BindingType.TextureRead:
            {
                var view = TextureViews.Get(resource.TextureView, "TextureView");
                EnsureTextureKind(view, ViewKind.ShaderResource);
                ValidateTextureShape(slot, view);
                break;
            }
            case BindingType.TextureReadWrite:
            {
                var view = TextureViews.Get(resource.TextureView, "TextureView");
                EnsureTextureKind(view, ViewKind.UnorderedAccess);
                ValidateTextureShape(slot, view);
                break;
            }
            case BindingType.AccelerationStructure:
                AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                break;
            case BindingType.Sampler:
                Samplers.Get(resource.SamplerHandle, "Sampler");
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by D3D12 backend.");
        }
    }

    private void ValidateBufferShape(ValidationSlot slot, BufferViewRecord view)
    {
        if (!slot.HasShapeRule)
            return;

        BufferDesc? buffer = slot.Shape.StrideInBytes != 0 && view.Desc.StrideInBytes == 0
            ? Buffers.Get(view.Buffer, "BufferViewBuffer").Desc
            : null;
        RhiBindingValidation.ValidateBufferShape(slot.Binding, slot.Shape, view.Desc, buffer);
    }

    private static void ValidateTextureShape(ValidationSlot slot, TextureViewRecord view)
    {
        if (!slot.HasShapeRule)
            return;

        RhiBindingValidation.ValidateTextureShape(slot.Binding, slot.Shape, view.Desc);
    }

    private static void EnsureBufferKind(BufferViewRecord record, ViewKind expected)
    {
        if (record.Kind != expected)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Buffer view kind {record.Kind} does not match required kind {expected}.");
    }

    private static void EnsureBufferRaw(BufferViewRecord record, bool expectedRaw, BindingType bindingType)
    {
        if (record.Desc.Raw != expectedRaw)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding type {bindingType} requires a {(expectedRaw ? "raw" : "non-raw")} buffer view.");
    }

    private static void EnsureTextureKind(TextureViewRecord record, ViewKind expected)
    {
        if (record.Kind != expected)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view kind {record.Kind} does not match required kind {expected}.");
    }

    private static RasterizerDescription CreateRasterizer(RasterizerDesc desc)
        => new(
            desc.CullMode switch
            {
                CullMode.None => Vortice.Direct3D12.CullMode.None,
                CullMode.Front => Vortice.Direct3D12.CullMode.Front,
                CullMode.Back => Vortice.Direct3D12.CullMode.Back,
                _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Cull mode {desc.CullMode} is not defined."),
            },
            desc.FillMode switch
            {
                FillMode.Solid => Vortice.Direct3D12.FillMode.Solid,
                FillMode.Wireframe => Vortice.Direct3D12.FillMode.Wireframe,
                _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Fill mode {desc.FillMode} is not defined."),
            },
            desc.FrontCounterClockwise,
            desc.DepthBias,
            desc.DepthBiasClamp,
            desc.SlopeScaledDepthBias,
            true,
            false,
            false,
            0,
            ConservativeRasterizationMode.Off);

    private static DepthStencilDescription CreateDepthStencil(DepthStencilDesc desc)
        => new(
            desc.DepthEnable,
            desc.DepthWriteEnable ? DepthWriteMask.All : DepthWriteMask.Zero,
            D3D12Mappings.ToComparison(desc.DepthCompare));

    private ID3D12CommandSignature CreateCommandSignature(IndirectArgumentType type, uint byteStride)
    {
        var arguments = new[] { new IndirectArgumentDescription { Type = type } };
        var desc = new CommandSignatureDescription
        {
            ByteStride = checked((int)byteStride),
            IndirectArguments = arguments,
        };
        return _device.CreateCommandSignature<ID3D12CommandSignature>(desc, null);
    }

    private static BlendDescription CreateBlend(BlendDesc desc)
    {
        var blend = BlendDescription.Opaque;
        blend.AlphaToCoverageEnable = desc.AlphaToCoverageEnable;
        blend.IndependentBlendEnable = desc.Targets.Count > 1;
        for (int index = 0; index < 8; index++)
        {
            blend.RenderTarget[index] = index < desc.Targets.Count
                ? CreateBlendTarget(desc.Targets[index])
                : CreateBlendTarget(new BlendTargetDesc());
        }

        return blend;
    }

    private static RenderTargetBlendDescription CreateBlendTarget(BlendTargetDesc desc)
        => new(
            desc.Enable,
            false,
            D3D12Mappings.ToBlend(desc.SourceColor),
            D3D12Mappings.ToBlend(desc.DestinationColor),
            D3D12Mappings.ToBlendOperation(desc.ColorOp),
            D3D12Mappings.ToBlend(desc.SourceAlpha),
            D3D12Mappings.ToBlend(desc.DestinationAlpha),
            D3D12Mappings.ToBlendOperation(desc.AlphaOp),
            LogicOp.Noop,
            D3D12Mappings.ToColorWrite(desc.WriteMask));

    private static InputElementDescription[] CreateInputElements(GraphicsPipelineDesc desc)
    {
        var elements = new InputElementDescription[desc.VertexAttributes.Count];
        for (int index = 0; index < elements.Length; index++)
        {
            var attribute = desc.VertexAttributes[index];
            elements[index] = ToInputElement(attribute, FindVertexLayout(desc.VertexBuffers, attribute.BufferSlot));
        }

        return elements;
    }

    private static InputElementDescription ToInputElement(VertexAttributeDesc attr, VertexLayoutDesc layout)
        => new()
        {
            SemanticName = "ATTRIB",
            SemanticIndex = attr.Location,
            Format = D3D12Mappings.ToDxgi(attr.Format),
            AlignedByteOffset = attr.OffsetInBytes,
            Slot = attr.BufferSlot,
            Classification = ToInputClassification(layout.InputRate),
            InstanceDataStepRate = layout.InputRate == VertexInputRate.Instance ? layout.InstanceStepRate : 0,
        };

    private static VertexLayoutDesc FindVertexLayout(IReadOnlyList<VertexLayoutDesc> layouts, uint slot)
    {
        foreach (var layout in layouts)
        {
            if (layout.Slot == slot)
                return layout;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex attribute references undeclared buffer slot {slot}.");
    }

    private static InputClassification ToInputClassification(VertexInputRate rate)
        => rate switch
        {
            VertexInputRate.Vertex => InputClassification.PerVertexData,
            VertexInputRate.Instance => InputClassification.PerInstanceData,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex input rate value {rate} is not defined."),
        };

    private static ulong AlignUp(ulong value, ulong alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    private static ulong FormatByteSize(Format format)
        => format switch
        {
            Format.R8Unorm or Format.R8UInt => 1,
            Format.R16UInt or Format.R16Float or Format.Rg8Unorm => 2,
            Format.Rgba8Unorm
                or Format.Rgba8UnormSrgb
                or Format.Bgra8Unorm
                or Format.Bgra8UnormSrgb
                or Format.Rgb10A2Unorm
                or Format.R32UInt
                or Format.R32Float
                or Format.Rg16Float
                or Format.D32Float
                or Format.D24UnormS8UInt => 4,
            Format.Rgba16Float or Format.Rg32Float => 8,
            Format.Rgb32Float => 12,
            Format.Rgba32Float => 16,
            _ => 4,
        };

    private sealed class MeshStream : IDisposable
    {
        private readonly List<GCHandle> _pins = [];

        public byte[] Bytes { get; private set; } = [];

        public PipelineStateSubObjectTypeAmplificationShader PinAmplificationShader(ReadOnlyMemory<byte> bytecode)
            => new(PinShaderBytes(bytecode));

        public PipelineStateSubObjectTypeMeshShader PinMeshShader(ReadOnlyMemory<byte> bytecode)
            => new(PinShaderBytes(bytecode));

        public PipelineStateSubObjectTypePixelShader PinPixelShader(ReadOnlyMemory<byte> bytecode)
            => new(PinShaderBytes(bytecode));

        public unsafe ReadOnlySpan<byte> PinShaderBytes(ReadOnlyMemory<byte> bytecode)
        {
            if (bytecode.IsEmpty)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Mesh pipeline shader bytecode must not be empty.");
            ArraySegment<byte> segment = MemoryMarshal.TryGetArray(bytecode, out ArraySegment<byte> array)
                ? array
                : new ArraySegment<byte>(bytecode.ToArray());
            var handle = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
            _pins.Add(handle);
            return new ReadOnlySpan<byte>((byte*)handle.AddrOfPinnedObject() + segment.Offset, segment.Count);
        }

        public void Set<T>(T value)
            where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
            Bytes = bytes[..^IntPtr.Size].ToArray();
        }

        public void Dispose()
        {
            for (int index = 0; index < _pins.Count; index++)
            {
                if (_pins[index].IsAllocated)
                    _pins[index].Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshStreamNative
    {
        public PipelineStateSubObjectTypeRootSignature RootSignature;
        public PipelineStateSubObjectTypeMeshShader MeshShader;
        public PipelineStateSubObjectTypeBlend Blend;
        public PipelineStateSubObjectTypeSampleMask SampleMask;
        public PipelineStateSubObjectTypeRasterizer Rasterizer;
        public PipelineStateSubObjectTypeDepthStencil DepthStencil;
        public PipelineStateSubObjectTypePrimitiveTopology PrimitiveTopology;
        public PipelineStateSubObjectTypeRenderTargetFormats RenderTargetFormats;
        public PipelineStateSubObjectTypeDepthStencilFormat DepthStencilFormat;
        public PipelineStateSubObjectTypeSampleDescription SampleDescription;

        public MeshStreamNative(
            PipelineStateSubObjectTypeRootSignature rootSignature,
            PipelineStateSubObjectTypeMeshShader meshShader,
            PipelineStateSubObjectTypeBlend blend,
            PipelineStateSubObjectTypeSampleMask sampleMask,
            PipelineStateSubObjectTypeRasterizer rasterizer,
            PipelineStateSubObjectTypeDepthStencil depthStencil,
            PipelineStateSubObjectTypePrimitiveTopology primitiveTopology,
            PipelineStateSubObjectTypeRenderTargetFormats renderTargetFormats,
            PipelineStateSubObjectTypeDepthStencilFormat depthStencilFormat,
            PipelineStateSubObjectTypeSampleDescription sampleDescription)
        {
            RootSignature = rootSignature;
            MeshShader = meshShader;
            Blend = blend;
            SampleMask = sampleMask;
            Rasterizer = rasterizer;
            DepthStencil = depthStencil;
            PrimitiveTopology = primitiveTopology;
            RenderTargetFormats = renderTargetFormats;
            DepthStencilFormat = depthStencilFormat;
            SampleDescription = sampleDescription;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshStreamPixel
    {
        public PipelineStateSubObjectTypeRootSignature RootSignature;
        public PipelineStateSubObjectTypeMeshShader MeshShader;
        public PipelineStateSubObjectTypePixelShader PixelShader;
        public PipelineStateSubObjectTypeBlend Blend;
        public PipelineStateSubObjectTypeSampleMask SampleMask;
        public PipelineStateSubObjectTypeRasterizer Rasterizer;
        public PipelineStateSubObjectTypeDepthStencil DepthStencil;
        public PipelineStateSubObjectTypePrimitiveTopology PrimitiveTopology;
        public PipelineStateSubObjectTypeRenderTargetFormats RenderTargetFormats;
        public PipelineStateSubObjectTypeDepthStencilFormat DepthStencilFormat;
        public PipelineStateSubObjectTypeSampleDescription SampleDescription;

        public MeshStreamPixel(
            PipelineStateSubObjectTypeRootSignature rootSignature,
            PipelineStateSubObjectTypeMeshShader meshShader,
            PipelineStateSubObjectTypePixelShader pixelShader,
            PipelineStateSubObjectTypeBlend blend,
            PipelineStateSubObjectTypeSampleMask sampleMask,
            PipelineStateSubObjectTypeRasterizer rasterizer,
            PipelineStateSubObjectTypeDepthStencil depthStencil,
            PipelineStateSubObjectTypePrimitiveTopology primitiveTopology,
            PipelineStateSubObjectTypeRenderTargetFormats renderTargetFormats,
            PipelineStateSubObjectTypeDepthStencilFormat depthStencilFormat,
            PipelineStateSubObjectTypeSampleDescription sampleDescription)
        {
            RootSignature = rootSignature;
            MeshShader = meshShader;
            PixelShader = pixelShader;
            Blend = blend;
            SampleMask = sampleMask;
            Rasterizer = rasterizer;
            DepthStencil = depthStencil;
            PrimitiveTopology = primitiveTopology;
            RenderTargetFormats = renderTargetFormats;
            DepthStencilFormat = depthStencilFormat;
            SampleDescription = sampleDescription;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshStreamAmp
    {
        public PipelineStateSubObjectTypeRootSignature RootSignature;
        public PipelineStateSubObjectTypeAmplificationShader AmplificationShader;
        public PipelineStateSubObjectTypeMeshShader MeshShader;
        public PipelineStateSubObjectTypeBlend Blend;
        public PipelineStateSubObjectTypeSampleMask SampleMask;
        public PipelineStateSubObjectTypeRasterizer Rasterizer;
        public PipelineStateSubObjectTypeDepthStencil DepthStencil;
        public PipelineStateSubObjectTypePrimitiveTopology PrimitiveTopology;
        public PipelineStateSubObjectTypeRenderTargetFormats RenderTargetFormats;
        public PipelineStateSubObjectTypeDepthStencilFormat DepthStencilFormat;
        public PipelineStateSubObjectTypeSampleDescription SampleDescription;

        public MeshStreamAmp(
            PipelineStateSubObjectTypeRootSignature rootSignature,
            PipelineStateSubObjectTypeAmplificationShader amplificationShader,
            PipelineStateSubObjectTypeMeshShader meshShader,
            PipelineStateSubObjectTypeBlend blend,
            PipelineStateSubObjectTypeSampleMask sampleMask,
            PipelineStateSubObjectTypeRasterizer rasterizer,
            PipelineStateSubObjectTypeDepthStencil depthStencil,
            PipelineStateSubObjectTypePrimitiveTopology primitiveTopology,
            PipelineStateSubObjectTypeRenderTargetFormats renderTargetFormats,
            PipelineStateSubObjectTypeDepthStencilFormat depthStencilFormat,
            PipelineStateSubObjectTypeSampleDescription sampleDescription)
        {
            RootSignature = rootSignature;
            AmplificationShader = amplificationShader;
            MeshShader = meshShader;
            Blend = blend;
            SampleMask = sampleMask;
            Rasterizer = rasterizer;
            DepthStencil = depthStencil;
            PrimitiveTopology = primitiveTopology;
            RenderTargetFormats = renderTargetFormats;
            DepthStencilFormat = depthStencilFormat;
            SampleDescription = sampleDescription;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshAmpPixel
    {
        public PipelineStateSubObjectTypeRootSignature RootSignature;
        public PipelineStateSubObjectTypeAmplificationShader AmplificationShader;
        public PipelineStateSubObjectTypeMeshShader MeshShader;
        public PipelineStateSubObjectTypePixelShader PixelShader;
        public PipelineStateSubObjectTypeBlend Blend;
        public PipelineStateSubObjectTypeSampleMask SampleMask;
        public PipelineStateSubObjectTypeRasterizer Rasterizer;
        public PipelineStateSubObjectTypeDepthStencil DepthStencil;
        public PipelineStateSubObjectTypePrimitiveTopology PrimitiveTopology;
        public PipelineStateSubObjectTypeRenderTargetFormats RenderTargetFormats;
        public PipelineStateSubObjectTypeDepthStencilFormat DepthStencilFormat;
        public PipelineStateSubObjectTypeSampleDescription SampleDescription;

        public MeshAmpPixel(
            PipelineStateSubObjectTypeRootSignature rootSignature,
            PipelineStateSubObjectTypeAmplificationShader amplificationShader,
            PipelineStateSubObjectTypeMeshShader meshShader,
            PipelineStateSubObjectTypePixelShader pixelShader,
            PipelineStateSubObjectTypeBlend blend,
            PipelineStateSubObjectTypeSampleMask sampleMask,
            PipelineStateSubObjectTypeRasterizer rasterizer,
            PipelineStateSubObjectTypeDepthStencil depthStencil,
            PipelineStateSubObjectTypePrimitiveTopology primitiveTopology,
            PipelineStateSubObjectTypeRenderTargetFormats renderTargetFormats,
            PipelineStateSubObjectTypeDepthStencilFormat depthStencilFormat,
            PipelineStateSubObjectTypeSampleDescription sampleDescription)
        {
            RootSignature = rootSignature;
            AmplificationShader = amplificationShader;
            MeshShader = meshShader;
            PixelShader = pixelShader;
            Blend = blend;
            SampleMask = sampleMask;
            Rasterizer = rasterizer;
            DepthStencil = depthStencil;
            PrimitiveTopology = primitiveTopology;
            RenderTargetFormats = renderTargetFormats;
            DepthStencilFormat = depthStencilFormat;
            SampleDescription = sampleDescription;
        }
    }
}
