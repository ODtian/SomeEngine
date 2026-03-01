using System;
using System.Runtime.InteropServices;
using Diligent;
using Silk.NET.Windowing;

namespace SomeEngine.Render.RHI;

public unsafe class RenderContext : IDisposable
{
    public IEngineFactory? Factory { get; private set; }
    public IRenderDevice? Device { get; private set; }
    public IDeviceContext? ImmediateContext { get; private set; }
    public ISwapChain? SwapChain { get; private set; }
    public ITexture? DepthBuffer { get; private set; }
    public ITextureView? DepthBufferDSV { get; private set; }

    public void Initialize(IWindow window)
    {
        if (window?.Native?.Win32 == null)
            throw new NotSupportedException("Only Win32 windows are supported for now.");
        var (Hwnd, _, _) = window.Native.Win32.Value;

        var scDesc = new SwapChainDesc
        {
            ColorBufferFormat = TextureFormat.RGBA8_UNorm,
            DepthBufferFormat = TextureFormat.Unknown, // We manage depth buffer manually
            BufferCount = 2,
            Width = (uint)window.Size.X,
            Height = (uint)window.Size.Y,
            Usage = SwapChainUsageFlags.RenderTarget,
        };

        // Try D3D12 first
        try
        {
            InitializeD3D12(Hwnd, scDesc);
        }
        catch
        {
            // Fallback or just fail for now
            throw new Exception("Failed to initialize D3D12 backend.");
        }
    }

    private void InitializeD3D12(nint windowHandle, SwapChainDesc scDesc)
    {
        var factory = Native.CreateEngineFactory<IEngineFactoryD3D12>();
        Factory = factory;
        var engineCI = new EngineD3D12CreateInfo
        {
            EnableValidation = true,
            D3D12ValidationFlags =
                D3D12ValidationFlags.BreakOnError | D3D12ValidationFlags.BreakOnCorruption,
        };
        factory.CreateDeviceAndContextsD3D12(engineCI, out var device, out var contexts);
        Device = device;

        ImmediateContext = contexts[0];
        var fsDesc = new FullScreenModeDesc();
        var win32Window = new Win32NativeWindow { Wnd = windowHandle };

        SwapChain = factory.CreateSwapChainD3D12(device, contexts[0], scDesc, fsDesc, win32Window);
        CreateDepthBuffer(scDesc.Width, scDesc.Height);
    }

    private void CreateDepthBuffer(uint width, uint height)
    {
        DepthBuffer?.Dispose();
        
        var depthDesc = new TextureDesc
        {
            Name = "Custom Depth Buffer",
            Type = ResourceDimension.Tex2d,
            Width = width,
            Height = height,
            Format = TextureFormat.D32_Float,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            ClearValue = new OptimizedClearValue
            {
                Format = TextureFormat.D32_Float,
                DepthStencil = new DepthStencilClearValue { Depth = 1.0f, Stencil = 0 }
            }
        };

        DepthBuffer = Device!.CreateTexture(depthDesc);
        DepthBufferDSV = DepthBuffer.GetDefaultView(TextureViewType.DepthStencil);
    }

    public void Resize(uint width, uint height)
    {
        SwapChain?.Resize(width, height, SurfaceTransform.Optimal);
        CreateDepthBuffer(width, height);
    }

    public void Present()
    {
        SwapChain?.Present(1);
    }

    public void Dispose()
    {
        DepthBuffer?.Dispose();
        SwapChain?.Dispose();
        ImmediateContext?.Dispose();
        Device?.Dispose();
        Factory?.Dispose();
    }
}
