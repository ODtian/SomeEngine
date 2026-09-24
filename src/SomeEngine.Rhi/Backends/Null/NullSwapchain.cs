using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Rhi.Backends.Null;

internal sealed class NullSwapchain(NullDevice device, SwapchainHandle handle) : ISwapchain
{
    public uint Width => Record.TextureDesc.Width;
    public uint Height => Record.TextureDesc.Height;
    public Format Format => Record.TextureDesc.Format;
    public uint CurrentBackBufferIndex => Record.CurrentIndex;
    public TextureHandle CurrentTexture => Record.Texture;
    public TextureViewHandle CurrentRenderTargetView => Record.RenderTargetView;

    public T? Get<T>() where T : class
        => this is T self ? self : null;

    public void Resize(uint width, uint height)
    {
        device.ThrowIfDisposed();
        if (width == 0 || height == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Swapchain resize dimensions must be greater than zero.");

        var record = Record;
        device.ValidateSwapchain(record);
        for (int index = 0; index < record.RenderTargetViews.Length; index++)
        {
            device.TextureViews.Destroy(record.RenderTargetViews[index], "TextureView");
            device.Textures.Destroy(record.Textures[index], "Texture");
        }

        var textureDesc = record.TextureDesc with
        {
            Width = width,
            Height = height,
        };
        var textures = new TextureHandle[record.Textures.Length];
        var views = new TextureViewHandle[record.RenderTargetViews.Length];
        for (int index = 0; index < textures.Length; index++)
        {
            textures[index] = device.Textures.Add(new TextureRecord(
                textureDesc,
                new ResourceAllocationInfo(ResourceOwnership.Swapchain, textureDesc.Memory, MemoryHeapHandle.Invalid, 0, NullDevice.EstimateSwapchain(textureDesc))));
            views[index] = device.TextureViews.Add(new TextureViewRecord(
                textures[index],
                new TextureViewDesc
                {
                    Name = $"{textureDesc.Name} RTV {index}",
                    Kind = ViewKind.RenderTarget,
                    Format = textureDesc.Format,
                },
                ResourceOwnership.Swapchain));
        }

        record.Textures = textures;
        record.RenderTargetViews = views;
        record.TextureDesc = textureDesc;
        record.Desc = record.Desc with { Width = width, Height = height };
        record.CurrentIndex = 0;
    }

    public void Present(in PresentDesc desc)
    {
        Profiler.Present("Null", desc.SyncInterval, desc.AllowTearing);
        using var scope = Profiler.BeginScope("NullSwapchain.Present");
        device.ThrowIfDisposed();
        var record = Record;
        Validation.PresentDesc(desc, record.Desc);
        var state = device.GetTextureState(CurrentTexture);
        if (state != ResourceState.Present)
            throw new RhiException(ErrorCode.ValidationFailure, $"Swapchain present requires texture state Present, actual state is {state}.");
        record.CurrentIndex = (record.CurrentIndex + 1) % (uint)record.Textures.Length;
    }

    private SwapchainRecord Record => device.Swapchains.Get(handle, "Swapchain");
}
