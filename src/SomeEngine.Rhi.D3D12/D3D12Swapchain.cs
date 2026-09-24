using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace SomeEngine.Rhi.D3D12;

internal sealed class D3D12Swapchain(D3D12Device device, SwapchainHandle handle) : ISwapchain, ID3D12SwapchainInterop
{
    private bool _hasCurrentBackBufferIndex;
    private uint _currentBackBufferIndex;

    public uint Width => Record.Desc.Width;
    public uint Height => Record.Desc.Height;
    public Format Format => Record.Desc.Format;
    public uint CurrentBackBufferIndex
    {
        get
        {
            if (!_hasCurrentBackBufferIndex)
            {
                _currentBackBufferIndex = Record.Swapchain.CurrentBackBufferIndex;
                _hasCurrentBackBufferIndex = true;
            }

            return _currentBackBufferIndex;
        }
    }
    public TextureHandle CurrentTexture => Record.Textures[checked((int)CurrentBackBufferIndex)];
    public TextureViewHandle CurrentRenderTargetView => Record.RenderTargetViews[checked((int)CurrentBackBufferIndex)];
    public IDXGISwapChain3 NativeSwapchain => Record.Swapchain;

    public T? Get<T>() where T : class
    {
        if (this is T self)
            return self;
        if (NativeSwapchain is T native)
            return native;
        return null;
    }

    public static SwapchainRecord Create(D3D12Device device, SwapchainDesc desc)
    {
        ValidateColorSpace(desc);
        if (desc.AllowTearing && !device.Factory.PresentAllowTearing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 tearing present was requested but DXGI tearing is not supported.");
        var flags = desc.AllowTearing && device.Factory.PresentAllowTearing
            ? SwapChainFlags.AllowTearing
            : SwapChainFlags.None;
        var swapchainDesc = new SwapChainDescription1(
            desc.Width,
            desc.Height,
            D3D12Mappings.ToDxgi(desc.Format),
            false,
            Usage.RenderTargetOutput,
            desc.BufferCount,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            flags);
        IDXGISwapChain3? swapchain = null;
        SwapchainRecord? record = null;
        try
        {
            using var swapchain1 = device.Factory.CreateSwapChainForHwnd(device.GetQueue(CommandQueueKind.Direct).NativeQueue, desc.NativeWindowHandle, swapchainDesc, null, null);
            swapchain = swapchain1.QueryInterface<IDXGISwapChain3>();
            ConfigureColorSpace(swapchain, desc);
            ConfigureFullscreen(swapchain, desc);
            record = new SwapchainRecord(desc with { }, swapchain, new TextureHandle[desc.BufferCount], new TextureViewHandle[desc.BufferCount]);
            PopulateBackBuffers(device, record);
            swapchain = null;
            return record;
        }
        catch
        {
            if (record != null)
            {
                LeaveFullscreen(record);
                ReleaseBackBuffers(device, record);
                record.Swapchain.Dispose();
            }
            else
            {
                swapchain?.Dispose();
            }

            throw;
        }
    }

    public void Resize(uint width, uint height)
    {
        device.ThrowIfDisposed();
        if (width == 0 || height == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Swapchain resize dimensions must be greater than zero.");
        if (Record.Desc.AllowTearing && !device.Factory.PresentAllowTearing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 tearing present was requested but DXGI tearing is not supported.");
        device.WaitIdle();
        var record = Record;
        for (int index = 0; index < record.RenderTargetViews.Length; index++)
        {
            device.CheckTvFree(record.RenderTargetViews[index]);
            device.CheckTextureFree(record.Textures[index]);
        }

        ReleaseBackBuffers(device, record);
        var flags = record.Desc.AllowTearing && device.Factory.PresentAllowTearing
            ? SwapChainFlags.AllowTearing
            : SwapChainFlags.None;
        record.Swapchain.ResizeBuffers(record.Desc.BufferCount, width, height, D3D12Mappings.ToDxgi(record.Desc.Format), flags).CheckError();
        record.Desc = record.Desc with { Width = width, Height = height };
        ConfigureColorSpace(record.Swapchain, record.Desc);
        ConfigureFullscreen(record.Swapchain, record.Desc);
        PopulateBackBuffers(device, record);
        _hasCurrentBackBufferIndex = false;
    }

    public void Present(in PresentDesc desc)
    {
        Profiler.Present("D3D12", desc.SyncInterval, desc.AllowTearing);
        using var scope = Profiler.BeginScope("D3D12Swapchain.Present");
        device.ThrowIfDisposed();
        Validation.PresentDesc(desc, Record.Desc);
        if (desc.AllowTearing && !device.Factory.PresentAllowTearing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 tearing present was requested but DXGI tearing is not supported.");
        var state = device.GetTextureState(CurrentTexture);
        if (state != ResourceState.Present)
            throw new RhiException(ErrorCode.ValidationFailure, $"Swapchain present requires texture state Present, actual state is {state}.");
        var flags = desc.AllowTearing && desc.SyncInterval == 0 && Record.Desc.AllowTearing && device.Factory.PresentAllowTearing
            ? PresentFlags.AllowTearing
            : PresentFlags.None;
        using (Profiler.BeginScope("D3D12Swapchain.Present.Native"))
        {
            Record.Swapchain.Present(desc.SyncInterval, flags).CheckError();
        }
        _hasCurrentBackBufferIndex = false;
    }

    private SwapchainRecord Record => device.Swapchains.Get(handle, "Swapchain");

    internal static void LeaveFullscreen(SwapchainRecord record)
    {
        if (record.Desc.Mode != SwapchainMode.ExclusiveFullscreen)
            return;
        try
        {
            record.Swapchain.SetFullscreenState(false, null).CheckError();
        }
        catch (SharpGenException)
        {
        }
    }

    private static void ValidateColorSpace(SwapchainDesc desc)
    {
        Format required = desc.ColorSpace switch
        {
            ColorSpace.Sdr => Format.Unknown,
            ColorSpace.ScRgbLinear => Format.Rgba16Float,
            ColorSpace.Hdr10 => Format.Rgb10A2Unorm,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Color space {desc.ColorSpace} is not defined."),
        };
        if (required != Format.Unknown && desc.Format != required)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{desc.ColorSpace} swapchains require {required}.");
    }

    private static void ConfigureColorSpace(IDXGISwapChain3 swapchain, SwapchainDesc desc)
    {
        var colorSpace = ToColorSpace(desc.ColorSpace);
        var support = swapchain.CheckColorSpaceSupport(colorSpace);
        if (!support.HasFlag(SwapChainColorSpaceSupportFlags.Present))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"DXGI swapchain color space {desc.ColorSpace} is not supported for presentation.");

        swapchain.SetColorSpace1(colorSpace);
        if (desc.ColorSpace == ColorSpace.Hdr10)
            SetHdrMeta(swapchain, desc.Hdr10Metadata!.Value);
    }

    private static ColorSpaceType ToColorSpace(ColorSpace colorSpace)
        => colorSpace switch
        {
            ColorSpace.Sdr => (ColorSpaceType)0, // DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
            ColorSpace.ScRgbLinear => (ColorSpaceType)1, // DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
            ColorSpace.Hdr10 => (ColorSpaceType)12, // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Color space {colorSpace} is not defined."),
        };

    private static unsafe void SetHdrMeta(IDXGISwapChain3 swapchain, Hdr10Metadata metadata)
    {
        using var swapchain4 = swapchain.QueryInterfaceOrNull<IDXGISwapChain4>();
        if (swapchain4 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, "DXGI HDR10 metadata requires IDXGISwapChain4.");

        var native = new Hdr10Meta
        {
            RedPrimaryX = metadata.RedPrimaryX,
            RedPrimaryY = metadata.RedPrimaryY,
            GreenPrimaryX = metadata.GreenPrimaryX,
            GreenPrimaryY = metadata.GreenPrimaryY,
            BluePrimaryX = metadata.BluePrimaryX,
            BluePrimaryY = metadata.BluePrimaryY,
            WhitePointX = metadata.WhitePointX,
            WhitePointY = metadata.WhitePointY,
            MaxMasteringLuminance = metadata.MaxMasteringLuminance,
            MinMasteringLuminance = metadata.MinMasteringLuminance,
            MaxContentLightLevel = metadata.MaxContentLightLevel,
            MaxFrameAverageLightLevel = metadata.MaxFrameAverageLightLevel,
        };
        swapchain4.SetHDRMetaData(HdrMetadataType.Hdr10, (uint)sizeof(Hdr10Meta), (IntPtr)(&native));
    }

    private static void ConfigureFullscreen(IDXGISwapChain3 swapchain, SwapchainDesc desc)
    {
        if (desc.Mode != SwapchainMode.ExclusiveFullscreen)
            return;

        try
        {
            if (desc.RefreshRate.Numerator != 0)
            {
                var target = new ModeDescription(
                    desc.Width,
                    desc.Height,
                    new Vortice.DXGI.Rational(desc.RefreshRate.Numerator, desc.RefreshRate.Denominator),
                    D3D12Mappings.ToDxgi(desc.Format));
                swapchain.ResizeTarget(ref target).CheckError();
            }

            swapchain.SetFullscreenState(true, null).CheckError();
        }
        catch (SharpGenException ex)
        {
            throw new RhiException(ErrorCode.UnsupportedFeature, $"DXGI exclusive fullscreen is not supported for this swapchain: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Hdr10Meta
    {
        public ushort RedPrimaryX;
        public ushort RedPrimaryY;
        public ushort GreenPrimaryX;
        public ushort GreenPrimaryY;
        public ushort BluePrimaryX;
        public ushort BluePrimaryY;
        public ushort WhitePointX;
        public ushort WhitePointY;
        public uint MaxMasteringLuminance;
        public uint MinMasteringLuminance;
        public ushort MaxContentLightLevel;
        public ushort MaxFrameAverageLightLevel;
    }

    private static void PopulateBackBuffers(D3D12Device device, SwapchainRecord record)
    {
        for (uint index = 0; index < record.Desc.BufferCount; index++)
        {
            var resource = record.Swapchain.GetBuffer<ID3D12Resource>(index);
            var textureDesc = new TextureDesc
            {
                Name = $"{record.Desc.Name} backbuffer {index}",
                Width = record.Desc.Width,
                Height = record.Desc.Height,
                Format = record.Desc.Format,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.Present,
            };
            var texture = device.Textures.Add(
                new TextureRecord(
                    textureDesc,
                    resource,
                    new ResourceAllocationInfo(ResourceOwnership.Swapchain, MemoryClass.DeviceLocal, MemoryHeapHandle.Invalid, 0, 0)));
            var allocation = device.RtvDescriptors.Allocate();
            device.NativeDevice.CreateRenderTargetView(resource, null, allocation.Cpu);
            var view = device.TextureViews.Add(
                new TextureViewRecord(
                    texture,
                    new TextureViewDesc
                    {
                        Name = $"{record.Desc.Name} RTV {index}",
                        Kind = ViewKind.RenderTarget,
                        Format = record.Desc.Format,
                    },
                    allocation.Cpu,
                    allocation,
                    ViewKind.RenderTarget,
                    ResourceOwnership.Swapchain));
            record.Textures[index] = texture;
            record.RenderTargetViews[index] = view;
        }
    }

    private static void ReleaseBackBuffers(D3D12Device device, SwapchainRecord record)
    {
        for (int index = 0; index < record.Textures.Length; index++)
        {
            if (record.RenderTargetViews[index].IsValid)
            {
                var view = device.TextureViews.Get(record.RenderTargetViews[index], "TextureView");
                device.RtvDescriptors.Free(view.Allocation);
                device.TextureViews.Destroy(record.RenderTargetViews[index], "TextureView");
                record.RenderTargetViews[index] = TextureViewHandle.Invalid;
            }

            if (record.Textures[index].IsValid)
            {
                var texture = device.Textures.Get(record.Textures[index], "Texture");
                texture.Resource.Dispose();
                device.Textures.Destroy(record.Textures[index], "Texture");
                record.Textures[index] = TextureHandle.Invalid;
            }
        }
    }
}
