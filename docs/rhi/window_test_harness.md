# RHI Visible Swapchain Test Harness

`SomeEngine.Rhi.WindowTests` is a standalone executable for RHI display-path validation that cannot be proven by headless xUnit tests. The visible window is only a platform fixture: the program creates a real Win32 window through `Silk.NET.Windowing`, extracts the native HWND from `IWindow.Native.Win32.Hwnd`, and drives the D3D12 RHI swapchain directly.

This tool is deliberately outside Engine renderer integration. It validates the RHI swapchain/present boundary without pulling in RenderGraph, shader assets, input, ImGui, or Runtime scene code.

## Scope

The harness covers:

- D3D12 device creation through the public RHI backend factory.
- Real HWND swapchain creation.
- Backbuffer state transitions from `Present` to `RenderTarget` and back.
- Command encoder creation, command buffer finish, queue submit, fence wait, and command buffer destruction on visible swapchain frames.
- Windowed flip-discard present with vsync.
- Slang-authored shader fixtures compiled to DXIL by the test executable, then consumed by RHI shader modules.
- Visible graphics pipeline creation and `Draw(3)` to the swapchain backbuffer.
- Visible compute pipeline dispatch, raw UAV write, GPU copy, and CPU readback validation.
- Long-running visible present pressure with optional resize cadence.
- Real window resize plus `ISwapchain.Resize`.
- Tearing present when DXGI reports tearing support.
- Borderless fullscreen through a fullscreen Silk window plus RHI `SwapchainMode.BorderlessFullscreen`.
- scRGB and HDR10 swapchain color-space setup when the display path supports presentation.
- Optional exclusive fullscreen through RHI `SwapchainMode.ExclusiveFullscreen`.
- JSON result output for CI or local automation.

It intentionally does not cover:

- Engine renderer or RenderGraph integration.
- Shader reflection or asset pipeline behavior.
- Screenshot/image-diff certification. The harness validates API execution and present success, not pixel capture from DWM.

Shader compilation in this executable is fixture setup, not a core RHI responsibility. The tool uses `SlangShaderSharp` to produce DXIL from small Slang sources, then passes only bytecode and explicit layout data into RHI. Slang's DXIL target still needs `dxcompiler.dll` and `dxil.dll`; the harness resolves the Windows SDK x64 directory automatically.

## Running

Build:

```powershell
dotnet build tools\SomeEngine.Rhi.WindowTests\SomeEngine.Rhi.WindowTests.csproj --no-restore -v minimal -p:UseSharedCompilation=false
```

Default short run:

```powershell
dotnet run --project tools\SomeEngine.Rhi.WindowTests\SomeEngine.Rhi.WindowTests.csproj --no-restore -- --frames 3 --width 320 --height 180 --json C:\tmp\someengine-rhi-window-tests.json
```

Short stress run:

```powershell
dotnet run --project tools\SomeEngine.Rhi.WindowTests\SomeEngine.Rhi.WindowTests.csproj --no-restore -- --frames 2 --stress-frames 12 --stress-resize-every 6 --width 320 --height 180
```

Run optional exclusive fullscreen probe:

```powershell
dotnet run --project tools\SomeEngine.Rhi.WindowTests\SomeEngine.Rhi.WindowTests.csproj --no-restore -- --frames 2 --width 320 --height 180 --include-exclusive
```

Strict local certification flags:

```powershell
dotnet run --project tools\SomeEngine.Rhi.WindowTests\SomeEngine.Rhi.WindowTests.csproj --no-restore -- --require-tearing --require-hdr --require-exclusive
```

`--require-*` flags convert unsupported optional display capabilities into failures. Without the flags, unsupported optional capabilities are reported as `SKIPPED` and the process can still exit successfully.

## Scenarios

| Scenario | Default | Failure rule | RHI surface being validated |
|---|---:|---|---|
| `rhi-present-vsync` | required | always fails run | BGRA8 swapchain creation, render-pass clear, state barriers, fence wait, present with sync interval 1. |
| `rhi-triangle-draw` | required | always fails run | Slang-to-DXIL fixture bytecode, shader modules, empty pipeline layout, graphics PSO, viewport/scissor, and `Draw(3)` into the visible backbuffer. |
| `rhi-compute-uav-readback` | required | always fails run | Slang-to-DXIL compute shader, raw UAV binding, compute queue submit, UAV-to-copy barrier, copy to readback buffer, and CPU value validation. |
| `rhi-frame-retirement` | required | always fails run | Repeated backbuffer cycling, command-buffer submit/wait/destroy, and present-path frame resource retirement. |
| `rhi-present-stress` | required | always fails run | Repeated sync-0 visible presents, fence retirement, backbuffer state cycling, and optional resize cadence under pressure. |
| `rhi-swapchain-resize` | required | always fails run | `ISwapchain.Resize`, backbuffer handle/view recreation, state tracking, and present after resize. |
| `rhi-present-tearing` | optional | fail with `--require-tearing` | Allow-tearing swapchain creation and sync-0 present flags. |
| `rhi-borderless-present` | required | always fails run | RHI presentation while the fixture window is in borderless fullscreen state. |
| `rhi-swapchain-scrgb` | optional | fail with `--require-hdr` | `Format.Rgba16Float` plus `ColorSpace.ScRgbLinear` DXGI color-space setup. |
| `rhi-swapchain-hdr10` | optional | fail with `--require-hdr` | `Format.Rgb10A2Unorm`, `ColorSpace.Hdr10`, and HDR10 metadata submission. |
| `rhi-exclusive-present` | opt-in | fail with `--require-exclusive` | RHI/DXGI exclusive fullscreen swapchain mode; skipped unless explicitly requested. |

## Implementation Boundary

The tool uses Silk only for fixture lifecycle and native handle extraction. It does not define a second SomeEngine window abstraction. The rendering path is pure RHI:

1. Create `IInstance` with `D3D12Backend.Factory`.
2. Create `IDevice` with `Backend.D3D12`.
3. Create an `ISwapchain` from the Silk HWND.
4. Compile small Slang fixture shaders to DXIL when a shader scenario needs bytecode.
5. For each frame, transition the current backbuffer from `Present` to `RenderTarget`.
6. Begin a lightweight render pass with a clear color or triangle draw.
7. Transition back to `Present`.
8. Submit, wait on a fence, and call `ISwapchain.Present`.

The exclusive fullscreen scenario is opt-in because it can be unavailable under remote desktop, desktop capture, compositor restrictions, or non-exclusive display ownership. In those cases DXGI commonly returns `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` or `DXGI_ERROR_INVALID_CALL`; the harness reports those exclusive-fullscreen probe failures as optional skips unless strict mode is requested.
