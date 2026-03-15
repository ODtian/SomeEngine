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
    public TextureDesc DepthBufferDesc { get; private set; }

    public void Initialize(IWindow window)
    {
        if (window?.Native?.Win32 == null)
            throw new NotSupportedException("Only Win32 windows are supported for now.");
        var (Hwnd, _, _) = window.Native.Win32.Value;

        var scDesc = new SwapChainDesc
        {
            ColorBufferFormat = TextureFormat.RGBA8_UNorm,
            DepthBufferFormat = TextureFormat.Unknown, // We manage depth buffer via RG
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
        };
        factory.CreateDeviceAndContextsD3D12(engineCI, out var device, out var contexts);
        Device = device;

        ImmediateContext = contexts[0];
        var fsDesc = new FullScreenModeDesc();
        var win32Window = new Win32NativeWindow { Wnd = windowHandle };

        SwapChain = factory.CreateSwapChainD3D12(device, contexts[0], scDesc, fsDesc, win32Window);
        UpdateDepthBufferDesc(scDesc.Width, scDesc.Height);
    }

    private void UpdateDepthBufferDesc(uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;

        DepthBufferDesc = new TextureDesc
        {
            Name = "DepthBuffer",
            Type = ResourceDimension.Tex2d,
            Width = width,
            Height = height,
            Format = TextureFormat.D32_Float,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            ClearValue = new OptimizedClearValue
            {
                Format = TextureFormat.D32_Float,
                DepthStencil = new DepthStencilClearValue { Depth = 1.0f, Stencil = 0 },
            },
        };
    }

    public void Resize(uint width, uint height)
    {
        SwapChain?.Resize(width, height, SurfaceTransform.Optimal);
        UpdateDepthBufferDesc(width, height);
    }

    public void Present()
    {
        // Wait for idle to test SRB mutable overwrite issue
        ImmediateContext?.WaitForIdle();
        SwapChain?.Present(1);
    }

    public void Dispose()
    {
        ImmediateContext?.Flush();
        ImmediateContext?.WaitForIdle();
        SwapChain?.Dispose();
        ImmediateContext?.Dispose();
        Device?.Dispose();
        Factory?.Dispose();
    }
}
