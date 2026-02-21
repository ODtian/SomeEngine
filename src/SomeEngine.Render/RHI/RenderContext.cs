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

    public void Initialize(IWindow window)
    {
        if (window.Native.Win32 == null)
            throw new NotSupportedException(
                "Only Win32 windows are supported for now."
            );

        var win32 = window.Native.Win32.Value;
        var windowHandle = win32.Hwnd;
        var hInstance = win32.HInstance;

        SwapChainDesc scDesc = new SwapChainDesc {
            ColorBufferFormat = TextureFormat.RGBA8_UNorm,
            DepthBufferFormat = TextureFormat.D32_Float,
            BufferCount = 2,
            Width = (uint)window.Size.X,
            Height = (uint)window.Size.Y,
        };

        // Try D3D12 first
        try
        {
            InitializeD3D12(windowHandle, scDesc);
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
        EngineD3D12CreateInfo engineCI = new EngineD3D12CreateInfo {
            EnableValidation = true,
            D3D12ValidationFlags = D3D12ValidationFlags.BreakOnError |
                                   D3D12ValidationFlags.BreakOnCorruption
        };
        factory.CreateDeviceAndContextsD3D12(
            engineCI, out var device, out var contexts
        );
        Device = device;

        ImmediateContext = contexts[0];
        FullScreenModeDesc fsDesc = new FullScreenModeDesc();
        Win32NativeWindow win32Window = new Win32NativeWindow { Wnd = windowHandle };

        SwapChain = factory.CreateSwapChainD3D12(
            device, contexts[0], scDesc, fsDesc, win32Window
        );
    }

    public void Resize(uint width, uint height)
    {
        SwapChain?.Resize(width, height, SurfaceTransform.Optimal);
    }

    public void Present()
    {
        SwapChain?.Present(1);
    }

    public void Dispose()
    {
        SwapChain?.Dispose();
        ImmediateContext?.Dispose();
        Device?.Dispose();
        Factory?.Dispose();
    }
}
