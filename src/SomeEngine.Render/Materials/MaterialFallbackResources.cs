using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

public sealed class MaterialFallbackResources : IDisposable
{
    private IDevice? _device;
    private TextureHandle _whiteTexture;
    private TextureHandle _blackTexture;
    private TextureHandle _flatNormalTexture;
    private TextureHandle _lightCookieAtlas;
    private TextureViewHandle _whiteTextureView;
    private TextureViewHandle _blackTextureView;
    private TextureViewHandle _flatNormalTextureView;
    private TextureViewHandle _lightCookieAtlasView;
    private BufferHandle _defaultStorageBuffer;
    private BufferHandle _defaultRawUnorderedAccessBuffer;
    private BufferHandle _defaultConstantBuffer;
    private BufferViewHandle _defaultStorageBufferView;
    private BufferViewHandle _defaultRawView;
    private BufferViewHandle _defaultRawUnorderedAccessView;
    private BufferViewHandle _defaultConstantBufferView;
    private SamplerHandle _defaultSampler;
    private SamplerHandle _lightCookieSampler;
    private bool _initialized;

    public MaterialResourceFallbacks Fallbacks { get; private set; } = new();

    public void EnsureInitialized(RenderContext context)
    {
        if (_initialized)
            return;

        var device = context.GraphicsDevice;
        var queue = context.GraphicsQueue;
        if (device == null || queue == null)
            return;

        _device = device;
        _whiteTextureView = CreateTextureView(device, queue, "MaterialFallback.White", [255, 255, 255, 255], out _whiteTexture);
        _blackTextureView = CreateTextureView(device, queue, "MaterialFallback.Black", [0, 0, 0, 255], out _blackTexture);
        _flatNormalTextureView = CreateTextureView(device, queue, "MaterialFallback.FlatNormal", [128, 128, 255, 255], out _flatNormalTexture);
        _lightCookieAtlasView = CreateTextureView(
            device,
            queue,
            "MaterialFallback.LightCookieAtlas",
            [255, 255, 255, 255],
            out _lightCookieAtlas,
            TextureViewDimension.Texture2DArray);

        _defaultStorageBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "MaterialFallback.DefaultStorageBuffer",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                Raw = true,
            },
            Zero16());
        _defaultStorageBufferView = device.CreateBufferView(
            _defaultStorageBuffer,
            new BufferViewDesc
            {
                Name = "MaterialFallback.DefaultStorageBufferView",
                Kind = ViewKind.ShaderResource,
                SizeInBytes = 16,
                StrideInBytes = sizeof(uint),
            });
        _defaultRawView = device.CreateBufferView(
            _defaultStorageBuffer,
            new BufferViewDesc
            {
                Name = "MaterialFallback.DefaultRawView",
                Kind = ViewKind.ShaderResource,
                SizeInBytes = 16,
                Raw = true,
            });

        _defaultRawUnorderedAccessBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "MaterialFallback.DefaultRawUnorderedAccessBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        _defaultRawUnorderedAccessView = device.CreateBufferView(
            _defaultRawUnorderedAccessBuffer,
            new BufferViewDesc
            {
                Name = "MaterialFallback.DefaultRawUnorderedAccessView",
                Kind = ViewKind.UnorderedAccess,
                SizeInBytes = 16,
                Raw = true,
            });

        _defaultConstantBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "MaterialFallback.DefaultConstantBuffer",
                SizeInBytes = 256,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            Zero256());
        _defaultConstantBufferView = device.CreateBufferView(
            _defaultConstantBuffer,
            new BufferViewDesc
            {
                Name = "MaterialFallback.DefaultConstantBufferView",
                Kind = ViewKind.ConstantBuffer,
                SizeInBytes = 256,
            });

        _defaultSampler = device.CreateSampler(
            new SamplerDesc
            {
                Name = "MaterialFallback.LinearWrapSampler",
                MinFilter = FilterMode.Linear,
                MagFilter = FilterMode.Linear,
                MipmapMode = MipmapMode.Linear,
                AddressU = AddressMode.Repeat,
                AddressV = AddressMode.Repeat,
                AddressW = AddressMode.Repeat,
            });
        _lightCookieSampler = device.CreateSampler(
            new SamplerDesc
            {
                Name = "MaterialFallback.LightCookieSampler",
                MinFilter = FilterMode.Linear,
                MagFilter = FilterMode.Linear,
                MipmapMode = MipmapMode.Linear,
                AddressU = AddressMode.ClampToEdge,
                AddressV = AddressMode.ClampToEdge,
                AddressW = AddressMode.ClampToEdge,
            });

        Fallbacks = new MaterialResourceFallbacks
        {
            WhiteTexture = _whiteTextureView,
            BlackTexture = _blackTextureView,
            FlatNormalTexture = _flatNormalTextureView,
            LightCookieAtlas = _lightCookieAtlasView,
            DefaultBufferView = _defaultStorageBufferView,
            DefaultRawView = _defaultRawView,
            DefaultRawUnorderedAccessView = _defaultRawUnorderedAccessView,
            DefaultConstantBufferView = _defaultConstantBufferView,
            DefaultSampler = _defaultSampler,
            LightCookieSampler = _lightCookieSampler,
        };

        _initialized = true;
    }

    public void Dispose()
    {
        Destroy(_device);
        Fallbacks = new MaterialResourceFallbacks();
        _initialized = false;
        _device = null;
    }

    public void Dispose(RenderContext context)
    {
        Destroy(context.GraphicsDevice);
        Fallbacks = new MaterialResourceFallbacks();
        _initialized = false;
        _device = null;
    }

    internal void Destroy(IDevice? device)
    {
        if (device == null)
            return;

        if (_defaultSampler.IsValid)
            device.Destroy(_defaultSampler);
        if (_lightCookieSampler.IsValid)
            device.Destroy(_lightCookieSampler);
        if (_defaultConstantBufferView.IsValid)
            device.Destroy(_defaultConstantBufferView);
        if (_defaultRawUnorderedAccessView.IsValid)
            device.Destroy(_defaultRawUnorderedAccessView);
        if (_defaultRawView.IsValid)
            device.Destroy(_defaultRawView);
        if (_defaultStorageBufferView.IsValid)
            device.Destroy(_defaultStorageBufferView);
        if (_defaultConstantBuffer.IsValid)
            device.Destroy(_defaultConstantBuffer);
        if (_defaultRawUnorderedAccessBuffer.IsValid)
            device.Destroy(_defaultRawUnorderedAccessBuffer);
        if (_defaultStorageBuffer.IsValid)
            device.Destroy(_defaultStorageBuffer);
        if (_flatNormalTextureView.IsValid)
            device.Destroy(_flatNormalTextureView);
        if (_lightCookieAtlasView.IsValid)
            device.Destroy(_lightCookieAtlasView);
        if (_blackTextureView.IsValid)
            device.Destroy(_blackTextureView);
        if (_whiteTextureView.IsValid)
            device.Destroy(_whiteTextureView);
        if (_flatNormalTexture.IsValid)
            device.Destroy(_flatNormalTexture);
        if (_lightCookieAtlas.IsValid)
            device.Destroy(_lightCookieAtlas);
        if (_blackTexture.IsValid)
            device.Destroy(_blackTexture);
        if (_whiteTexture.IsValid)
            device.Destroy(_whiteTexture);

        _defaultSampler = default;
        _lightCookieSampler = default;
        _defaultConstantBufferView = default;
        _defaultRawUnorderedAccessView = default;
        _defaultRawView = default;
        _defaultStorageBufferView = default;
        _defaultConstantBuffer = default;
        _defaultRawUnorderedAccessBuffer = default;
        _defaultStorageBuffer = default;
        _flatNormalTextureView = default;
        _lightCookieAtlasView = default;
        _blackTextureView = default;
        _whiteTextureView = default;
        _flatNormalTexture = default;
        _lightCookieAtlas = default;
        _blackTexture = default;
        _whiteTexture = default;
        _device = null;
    }

    private static TextureViewHandle CreateTextureView(
        IDevice device,
        IQueue queue,
        string name,
        ReadOnlySpan<byte> rgba,
        out TextureHandle texture,
        TextureViewDimension dimension = TextureViewDimension.Texture2D)
    {
        if (rgba.Length != 4)
            throw new ArgumentException("fallback texture data must be one RGBA8 texel.", nameof(rgba));

        uint rowPitch = checked((uint)AlignUp(4, device.Limits.TextureRowPitchAlignment));
        byte[] uploadData = new byte[rowPitch];
        rgba.CopyTo(uploadData);

        texture = device.CreateTexture(new TextureDesc
        {
            Name = name,
            Dimension = ResourceDimension.Texture2D,
            Width = 1,
            Height = 1,
            ArraySize = 1,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopyDestination,
        });

        var uploadBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = $"{name}.Upload",
                SizeInBytes = rowPitch,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadData);

        var list = device.CreateCommandList(new CommandListDesc
        {
            Name = $"Upload {name}",
            QueueType = QueueType.Graphics,
        });
        list.CopyToTexture(
            uploadBuffer,
            new BufferTextureCopy(0, rowPitch, rowPitch),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1));
        list.Barrier(
            [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderResource, SubresourceRange.All)],
            default);
        var commandBuffer = list.Finish();
        queue.Submit([commandBuffer]);
        queue.WaitIdle();
        device.Destroy(commandBuffer);
        device.Destroy(uploadBuffer);

        return device.CreateTextureView(texture, new TextureViewDesc
        {
            Name = $"{name}.SRV",
            Kind = ViewKind.ShaderResource,
            Dimension = dimension,
            Format = Format.Rgba8Unorm,
            SliceCount = 1,
        });
    }

    private static ReadOnlySpan<byte> Zero16()
        => [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> Zero256()
        => new byte[256];

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
            return value;
        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
