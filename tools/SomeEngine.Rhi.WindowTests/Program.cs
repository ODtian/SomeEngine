using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SlangShaderSharp;
using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;

namespace SomeEngine.Rhi.WindowTests;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = WindowTestOptions.Parse(args);
            if (options.ShowHelp)
            {
                WindowTestOptions.PrintHelp();
                return 0;
            }

            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("SomeEngine.Rhi.WindowTests requires Windows and D3D12.");

            var results = WindowTestRunner.Run(options);
            PrintResults(results);
            if (!string.IsNullOrWhiteSpace(options.JsonPath))
                WriteJson(options.JsonPath, options, results);

            return results.Any(static result => result.Status == ScenarioStatus.Failed) ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static void PrintResults(IReadOnlyList<ScenarioResult> results)
    {
        foreach (var result in results)
            Console.WriteLine($"{result.StatusText,-7} {result.Name,-28} {result.ElapsedMilliseconds,5} ms  {result.Details}");

        int passed = results.Count(static result => result.Status == ScenarioStatus.Passed);
        int skipped = results.Count(static result => result.Status == ScenarioStatus.Skipped);
        int failed = results.Count(static result => result.Status == ScenarioStatus.Failed);
        Console.WriteLine($"SUMMARY passed={passed} skipped={skipped} failed={failed}");
    }

    private static void WriteJson(string path, WindowTestOptions options, IReadOnlyList<ScenarioResult> results)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var payload = new
        {
            options = new
            {
                options.Width,
                options.Height,
                options.Frames,
                options.StressFrames,
                options.StressResizeEvery,
                options.IncludeExclusive,
                options.RequireTearing,
                options.RequireHdr,
                options.RequireExclusive,
                options.KeepOpenMilliseconds,
            },
            results,
        };
        var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload, serializerOptions));
    }
}

internal sealed record WindowTestOptions
{
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 360;
    public int Frames { get; init; } = 30;
    public int StressFrames { get; init; } = 120;
    public int StressResizeEvery { get; init; } = 60;
    public int KeepOpenMilliseconds { get; init; }
    public bool IncludeExclusive { get; init; }
    public bool RequireTearing { get; init; }
    public bool RequireHdr { get; init; }
    public bool RequireExclusive { get; init; }
    public bool ShowHelp { get; init; }
    public string? JsonPath { get; init; }

    public static WindowTestOptions Parse(string[] args)
    {
        var options = new WindowTestOptions();
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options = options with { ShowHelp = true };
                    break;
                case "--width":
                    options = options with { Width = PositiveInt(args, ref index, arg) };
                    break;
                case "--height":
                    options = options with { Height = PositiveInt(args, ref index, arg) };
                    break;
                case "--frames":
                    options = options with { Frames = PositiveInt(args, ref index, arg) };
                    break;
                case "--stress-frames":
                    options = options with { StressFrames = PositiveInt(args, ref index, arg) };
                    break;
                case "--stress-resize-every":
                    options = options with { StressResizeEvery = NonNegativeInt(args, ref index, arg) };
                    break;
                case "--keep-open-ms":
                    options = options with { KeepOpenMilliseconds = NonNegativeInt(args, ref index, arg) };
                    break;
                case "--json":
                    options = options with { JsonPath = RequiredValue(args, ref index, arg) };
                    break;
                case "--include-exclusive":
                    options = options with { IncludeExclusive = true };
                    break;
                case "--require-tearing":
                    options = options with { RequireTearing = true };
                    break;
                case "--require-hdr":
                    options = options with { RequireHdr = true };
                    break;
                case "--require-exclusive":
                    options = options with { RequireExclusive = true, IncludeExclusive = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'. Use --help for usage.");
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("SomeEngine.Rhi.WindowTests");
        Console.WriteLine();
        Console.WriteLine("Runs D3D12 RHI visible-swapchain validation through a Silk.NET.Windowing HWND fixture.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --width <pixels>         Initial framebuffer width. Default: 640");
        Console.WriteLine("  --height <pixels>        Initial framebuffer height. Default: 360");
        Console.WriteLine("  --frames <count>         Frames per frame-loop scenario. Default: 30");
        Console.WriteLine("  --stress-frames <count>  Frames for RHI present stress. Default: 120");
        Console.WriteLine("  --stress-resize-every <count>");
        Console.WriteLine("                           Resize interval during stress; 0 disables. Default: 60");
        Console.WriteLine("  --keep-open-ms <ms>      Keep the final window visible after tests. Default: 0");
        Console.WriteLine("  --json <path>            Write machine-readable results.");
        Console.WriteLine("  --include-exclusive      Attempt exclusive fullscreen scenario.");
        Console.WriteLine("  --require-tearing        Treat tearing unsupported as failure.");
        Console.WriteLine("  --require-hdr            Treat scRGB/HDR10 unsupported as failure.");
        Console.WriteLine("  --require-exclusive      Run exclusive fullscreen and treat unsupported as failure.");
        Console.WriteLine("  --help                   Show this help.");
    }

    private static int PositiveInt(string[] args, ref int index, string option)
    {
        int value = NonNegativeInt(args, ref index, option);
        if (value <= 0)
            throw new ArgumentException($"{option} must be greater than zero.");
        return value;
    }

    private static int NonNegativeInt(string[] args, ref int index, string option)
    {
        string value = RequiredValue(args, ref index, option);
        if (!int.TryParse(value, out int parsed) || parsed < 0)
            throw new ArgumentException($"{option} requires a non-negative integer.");
        return parsed;
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}

internal static class WindowTestRunner
{
    public static IReadOnlyList<ScenarioResult> Run(WindowTestOptions options)
    {
        using var window = CreateSilkWindow(options);
        PumpFor(window, TimeSpan.FromMilliseconds(120));

        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var results = new List<ScenarioResult>
        {
            RunScenario("rhi-present-vsync", required: true, () => RunWindowedVsync(device, window, options)),
            RunScenario("rhi-triangle-draw", required: true, () => RunTriangleDraw(device, window, options)),
            RunScenario("rhi-compute-uav-readback", required: true, () => RunComputeUavReadback(device, window, options)),
            RunScenario("rhi-frame-retirement", required: true, () => RunFrameRetirement(device, window, options)),
            RunScenario("rhi-present-stress", required: true, () => RunPresentStress(device, window, options)),
            RunScenario("rhi-swapchain-resize", required: true, () => RunResize(device, window, options)),
            RunScenario("rhi-present-tearing", options.RequireTearing, () => RunTearing(device, window, options)),
            RunScenario("rhi-borderless-present", required: true, () => RunBorderless(device, window, options)),
            RunScenario("rhi-swapchain-scrgb", options.RequireHdr, () => RunColorSpace(device, window, options, ColorSpace.ScRgbLinear)),
            RunScenario("rhi-swapchain-hdr10", options.RequireHdr, () => RunColorSpace(device, window, options, ColorSpace.Hdr10)),
        };

        if (options.IncludeExclusive || options.RequireExclusive)
            results.Add(RunScenario("rhi-exclusive-present", options.RequireExclusive, () => RunExclusive(device, window, options)));
        else
            results.Add(ScenarioResult.Skip("rhi-exclusive-present", "not requested; pass --include-exclusive or --require-exclusive to run"));

        if (options.KeepOpenMilliseconds > 0)
            PumpFor(window, TimeSpan.FromMilliseconds(options.KeepOpenMilliseconds));

        window.Close();
        return results;
    }

    private static IWindow CreateSilkWindow(WindowTestOptions options)
    {
        var windowOptions = WindowOptions.Default;
        windowOptions.API = GraphicsAPI.None;
        windowOptions.Size = new Vector2D<int>(options.Width, options.Height);
        windowOptions.Title = "SomeEngine RHI Visible Swapchain Tests";
        windowOptions.VSync = false;
        windowOptions.IsVisible = true;
        windowOptions.WindowBorder = WindowBorder.Resizable;

        var window = Silk.NET.Windowing.Window.Create(windowOptions);
        window.Initialize();
        if (!window.IsInitialized)
            throw new InvalidOperationException("Silk.NET window did not initialize.");
        if (NativeHwnd(window) == 0)
            throw new InvalidOperationException("Silk.NET window did not expose a valid Win32 HWND.");
        return window;
    }

    private static ScenarioResult RunScenario(string name, bool required, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
            return ScenarioResult.Pass(name, stopwatch.ElapsedMilliseconds, "completed");
        }
        catch (RhiException ex) when (!required && ex.Code == ErrorCode.UnsupportedFeature)
        {
            return ScenarioResult.Skip(name, ex.Message, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!required && IsUnsupportedDisplayMode(ex))
        {
            return ScenarioResult.Skip(name, ex.Message, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ScenarioResult.Fail(name, stopwatch.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool IsUnsupportedDisplayMode(Exception ex)
        => ex is RhiException { Code: ErrorCode.UnsupportedFeature }
            || ex.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);

    private static IInstance CreateD3D12Instance()
    {
        var instance = SomeEngine.Rhi.Instance.Create(D3D12Backend.Factory);
        if (instance.EnumerateAdapters().Any(static adapter => adapter.Backend == Backend.D3D12))
            return instance;

        instance.Dispose();
        throw new InvalidOperationException("No D3D12 adapter is available.");
    }

    private static void RunWindowedVsync(IDevice device, IWindow window, WindowTestOptions options)
    {
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible RHI vsync present",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        RunFrameLoop(device, scope.Swapchain, window, options.Frames, new PresentDesc { SyncInterval = 1 });
    }

    private static void RunTriangleDraw(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible RHI triangle draw",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        var vertex = ShaderModuleHandle.Invalid;
        var pixel = ShaderModuleHandle.Invalid;
        var layout = PipelineLayoutHandle.Invalid;
        var pipeline = PipelineHandle.Invalid;
        try
        {
            vertex = device.CreateShaderModule(
                new ShaderModuleDesc
                {
                    Name = "visible triangle vs",
                    Backend = Backend.D3D12,
                    Stage = ShaderStage.Vertex,
                    EntryPoint = "VSMain",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = SlangFixtureCompiler.CompileDxil(
                        VisibleTriangleSlang,
                        "visible_triangle",
                        "VSMain"),
                });
            pixel = device.CreateShaderModule(
                new ShaderModuleDesc
                {
                    Name = "visible triangle ps",
                    Backend = Backend.D3D12,
                    Stage = ShaderStage.Pixel,
                    EntryPoint = "PSMain",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = SlangFixtureCompiler.CompileDxil(
                        VisibleTriangleSlang,
                        "visible_triangle",
                        "PSMain"),
                });
            layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "visible triangle layout" });
            pipeline = device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    Name = "visible triangle pipeline",
                    Layout = layout,
                    VertexShader = vertex,
                    PixelShader = pixel,
                    Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                    ColorFormats = [scope.Swapchain.Format],
                });

            DrawTrianglePresent(device, scope.Swapchain, window, pipeline, new PresentDesc { SyncInterval = 1 });
        }
        finally
        {
            device.WaitIdle();
            if (pipeline.IsValid)
                device.Destroy(pipeline);
            if (layout.IsValid)
                device.Destroy(layout);
            if (pixel.IsValid)
                device.Destroy(pixel);
            if (vertex.IsValid)
                device.Destroy(vertex);
        }
    }

    private static void RunComputeUavReadback(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible RHI compute readback",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        var bindingLayout = BindingLayoutHandle.Invalid;
        var pipelineLayout = PipelineLayoutHandle.Invalid;
        var shader = ShaderModuleHandle.Invalid;
        var pipeline = PipelineHandle.Invalid;
        var output = BufferHandle.Invalid;
        var outputView = BufferViewHandle.Invalid;
        var readback = BufferHandle.Invalid;
        try
        {
            bindingLayout = device.CreateBindingLayout(
                new BindingLayoutDesc
                {
                    Name = "visible compute uav layout",
                    Slots =
                    [
                        new BindingSlotDesc
                        {
                            Binding = 0,
                            Type = BindingType.RawBufferReadWrite,
                            Stages = ShaderStageFlags.Compute,
                        },
                    ],
                });
            pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "visible compute layout", BindingLayouts = [bindingLayout] });
            shader = device.CreateShaderModule(
                new ShaderModuleDesc
                {
                    Name = "visible compute uav cs",
                    Backend = Backend.D3D12,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "CSMain",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = SlangFixtureCompiler.CompileDxil(
                        VisibleComputeUavSlang,
                        "visible_compute_uav",
                        "CSMain"),
                });
            pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Name = "visible compute uav pipeline", Layout = pipelineLayout, ComputeShader = shader });
            output = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "visible compute output",
                    SizeInBytes = 16,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                    InitialState = ResourceState.UnorderedAccess,
                    Raw = true,
                });
            outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
            readback = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "visible compute readback",
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });

            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
            var pass = list.BeginComputePass(new ComputePassDesc { Name = "visible compute uav" });
            pass.SetPipeline(pipeline);
            pass.SetBindings(
                0,
                bindingLayout,
                [new BindingResourceDesc { Binding = 0, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView }]);
            pass.Dispatch(1, 1, 1);
            pass.End();
            list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
            list.CopyBuffer(output, 0, readback, 0, sizeof(uint));
            SubmitAndWait(device, list.Finish(), "visible compute uav", QueueType.Compute);

            var mapped = device.MapBuffer(readback, MapMode.Read, 0, sizeof(uint));
            uint value = BitConverter.ToUInt32(mapped.Span[..sizeof(uint)]);
            device.UnmapBuffer(readback);
            if (value != 0x12345678u)
                throw new InvalidOperationException($"Visible compute UAV readback expected 0x12345678, got 0x{value:X8}.");

            ClearPresent(device, scope.Swapchain, window, new Color(0.1f, 0.16f, 0.28f, 1), new PresentDesc { SyncInterval = 1 });
        }
        finally
        {
            device.WaitIdle();
            if (readback.IsValid)
                device.Destroy(readback);
            if (outputView.IsValid)
                device.Destroy(outputView);
            if (output.IsValid)
                device.Destroy(output);
            if (pipeline.IsValid)
                device.Destroy(pipeline);
            if (shader.IsValid)
                device.Destroy(shader);
            if (pipelineLayout.IsValid)
                device.Destroy(pipelineLayout);
            if (bindingLayout.IsValid)
                device.Destroy(bindingLayout);
        }
    }

    private static void RunResize(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var initialSize = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible resize",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)initialSize.X),
                Height = checked((uint)initialSize.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        ClearPresent(device, scope.Swapchain, window, new Color(0.05f, 0.1f, 0.2f, 1), new PresentDesc { SyncInterval = 1 });
        EnsureWindowSize(window, Math.Max(128, options.Width + 113), Math.Max(128, options.Height + 57));
        var resized = CurrentSize(window);
        scope.Swapchain.Resize(checked((uint)resized.X), checked((uint)resized.Y));
        ClearPresent(device, scope.Swapchain, window, new Color(0.2f, 0.05f, 0.1f, 1), new PresentDesc { SyncInterval = 1 });
        EnsureWindowSize(window, options.Width, options.Height);
    }

    private static void RunFrameRetirement(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible RHI frame retirement",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        RunFrameLoop(
            device,
            scope.Swapchain,
            window,
            Math.Max(12, options.Frames),
            new PresentDesc { SyncInterval = 1 });
    }

    private static void RunPresentStress(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible RHI present stress",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = false,
            });

        bool alternateSize = false;
        for (int frame = 0; frame < options.StressFrames; frame++)
        {
            if (options.StressResizeEvery > 0 && frame > 0 && frame % options.StressResizeEvery == 0)
            {
                alternateSize = !alternateSize;
                int width = alternateSize ? Math.Max(128, options.Width + 37) : options.Width;
                int height = alternateSize ? Math.Max(128, options.Height + 29) : options.Height;
                EnsureWindowSize(window, width, height);
                var resized = CurrentSize(window);
                scope.Swapchain.Resize(checked((uint)resized.X), checked((uint)resized.Y));
            }

            float t = options.StressFrames <= 1 ? 0 : frame / (float)(options.StressFrames - 1);
            var color = new Color(0.02f + 0.25f * t, 0.18f + 0.1f * (1 - t), 0.35f + 0.12f * t, 1);
            ClearPresent(device, scope.Swapchain, window, color, new PresentDesc { SyncInterval = 0 });
        }

        EnsureWindowSize(window, options.Width, options.Height);
    }

    private static void RunTearing(IDevice device, IWindow window, WindowTestOptions options)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        using var scope = SwapchainScope.Create(
            device,
            new SwapchainDesc
            {
                Name = "visible tearing",
                NativeWindowHandle = NativeHwnd(window),
                Width = checked((uint)size.X),
                Height = checked((uint)size.Y),
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
                AllowTearing = true,
            });

        RunFrameLoop(
            device,
            scope.Swapchain,
            window,
            Math.Max(4, options.Frames / 2),
            new PresentDesc { SyncInterval = 0, AllowTearing = true });
    }

    private static void RunBorderless(IDevice device, IWindow window, WindowTestOptions options)
    {
        WindowState previousState = window.WindowState;
        try
        {
            window.WindowState = WindowState.Fullscreen;
            PumpFor(window, TimeSpan.FromMilliseconds(180));
            var size = CurrentSize(window);
            using var scope = SwapchainScope.Create(
                device,
                new SwapchainDesc
                {
                    Name = "visible borderless",
                    NativeWindowHandle = NativeHwnd(window),
                    Width = checked((uint)size.X),
                    Height = checked((uint)size.Y),
                    Format = Format.Bgra8Unorm,
                    BufferCount = 3,
                    AllowTearing = false,
                    Mode = SwapchainMode.BorderlessFullscreen,
                });

            RunFrameLoop(device, scope.Swapchain, window, Math.Max(2, options.Frames / 3), new PresentDesc { SyncInterval = 1 });
        }
        finally
        {
            window.WindowState = previousState;
            EnsureWindowSize(window, options.Width, options.Height);
        }
    }

    private static void RunColorSpace(IDevice device, IWindow window, WindowTestOptions options, ColorSpace colorSpace)
    {
        EnsureWindowSize(window, options.Width, options.Height);
        var size = CurrentSize(window);
        var desc = new SwapchainDesc
        {
            Name = "visible " + colorSpace,
            NativeWindowHandle = NativeHwnd(window),
            Width = checked((uint)size.X),
            Height = checked((uint)size.Y),
            Format = colorSpace == ColorSpace.ScRgbLinear ? Format.Rgba16Float : Format.Rgb10A2Unorm,
            BufferCount = 3,
            AllowTearing = false,
            ColorSpace = colorSpace,
            Hdr10Metadata = colorSpace == ColorSpace.Hdr10 ? TestHdr10Metadata() : null,
        };
        using var scope = SwapchainScope.Create(device, desc);
        var color = colorSpace == ColorSpace.ScRgbLinear
            ? new Color(1.25f, 0.2f, 0.08f, 1)
            : new Color(0.9f, 0.25f, 0.05f, 1);
        RunFrameLoop(device, scope.Swapchain, window, Math.Max(2, options.Frames / 3), new PresentDesc { SyncInterval = 1 }, color);
    }

    private static void RunExclusive(IDevice device, IWindow window, WindowTestOptions options)
    {
        try
        {
            window.WindowState = WindowState.Normal;
            EnsureWindowSize(window, options.Width, options.Height);
            var size = CurrentSize(window);
            using var scope = SwapchainScope.Create(
                device,
                new SwapchainDesc
                {
                    Name = "visible exclusive fullscreen",
                    NativeWindowHandle = NativeHwnd(window),
                    Width = checked((uint)size.X),
                    Height = checked((uint)size.Y),
                    Format = Format.Bgra8Unorm,
                    BufferCount = 3,
                    AllowTearing = false,
                    Mode = SwapchainMode.ExclusiveFullscreen,
                });

            RunFrameLoop(device, scope.Swapchain, window, Math.Max(2, options.Frames / 3), new PresentDesc { SyncInterval = 1 });
        }
        catch (Exception ex) when (IsExclusiveFullscreenUnavailable(ex))
        {
            throw new RhiException(ErrorCode.UnsupportedFeature, "Exclusive fullscreen is unavailable in the current desktop environment: " + ex.Message);
        }
    }

    private static bool IsExclusiveFullscreenUnavailable(Exception ex)
        => ex.Message.Contains("DXGI_ERROR_NOT_CURRENTLY_AVAILABLE", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("DXGI_ERROR_INVALID_CALL", StringComparison.OrdinalIgnoreCase);

    private static void RunFrameLoop(
        IDevice device,
        ISwapchain swapchain,
        IWindow window,
        int frameCount,
        PresentDesc present,
        Color? fixedColor = null)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            float t = frameCount <= 1 ? 0 : frame / (float)(frameCount - 1);
            var color = fixedColor ?? new Color(0.05f + 0.3f * t, 0.08f + 0.2f * (1 - t), 0.25f + 0.25f * t, 1);
            ClearPresent(device, swapchain, window, color, present);
            Pump(window);
        }
    }

    private static void ClearPresent(IDevice device, ISwapchain swapchain, IWindow window, Color clearColor, PresentDesc present)
    {
        Pump(window);
        var texture = swapchain.CurrentTexture;
        var view = swapchain.CurrentRenderTargetView;
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([new TextureBarrier(texture, ResourceState.Present, ResourceState.RenderTarget, SubresourceRange.All)], []);
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, checked((int)swapchain.Width), checked((int)swapchain.Height)),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = view,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clearColor,
                    },
                ],
            });
        pass.End();
        list.Barrier([new TextureBarrier(texture, ResourceState.RenderTarget, ResourceState.Present, SubresourceRange.All)], []);
        SubmitAndWait(device, list.Finish(), "window present");
        swapchain.Present(present);
    }

    private static void DrawTrianglePresent(IDevice device, ISwapchain swapchain, IWindow window, PipelineHandle pipeline, PresentDesc present)
    {
        Pump(window);
        var texture = swapchain.CurrentTexture;
        var view = swapchain.CurrentRenderTargetView;
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([new TextureBarrier(texture, ResourceState.Present, ResourceState.RenderTarget, SubresourceRange.All)], []);
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, checked((int)swapchain.Width), checked((int)swapchain.Height)),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = view,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetViewport(new Viewport(0, 0, swapchain.Width, swapchain.Height));
        pass.SetScissor(new Rect(0, 0, checked((int)swapchain.Width), checked((int)swapchain.Height)));
        pass.SetPipeline(pipeline);
        pass.Draw(3);
        pass.End();
        list.Barrier([new TextureBarrier(texture, ResourceState.RenderTarget, ResourceState.Present, SubresourceRange.All)], []);
        SubmitAndWait(device, list.Finish(), "visible triangle present");
        swapchain.Present(present);
    }

    private static void SubmitAndWait(IDevice device, CommandBufferHandle commandBuffer, string fenceName, QueueType queueType = QueueType.Graphics)
    {
        var fence = device.CreateFence(fenceName);
        device.GetQueue(queueType).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);
        device.Destroy(commandBuffer);
        device.Destroy(fence);
    }

    private static void EnsureWindowSize(IWindow window, int width, int height)
    {
        window.Size = new Vector2D<int>(width, height);
        PumpFor(window, TimeSpan.FromMilliseconds(100));
        var size = CurrentSize(window);
        if (size.X <= 0 || size.Y <= 0)
            throw new InvalidOperationException($"Silk.NET window framebuffer size is invalid: {size.X}x{size.Y}.");
    }

    private static Vector2D<int> CurrentSize(IWindow window)
    {
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0)
            size = window.Size;
        if (size.X <= 0 || size.Y <= 0)
            throw new InvalidOperationException($"Silk.NET window size is invalid: {size.X}x{size.Y}.");
        return size;
    }

    private static nint NativeHwnd(IWindow window)
    {
        var native = window.Native
            ?? throw new InvalidOperationException("Silk.NET window does not expose native window data.");
        var win32 = native.Win32
            ?? throw new InvalidOperationException("Silk.NET window does not expose a Win32 native window.");
        if (win32.Hwnd == 0)
            throw new InvalidOperationException("Silk.NET Win32 native window has a null HWND.");
        return win32.Hwnd;
    }

    private static void PumpFor(IWindow window, TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            Pump(window);
            Thread.Sleep(1);
        }
        while (stopwatch.Elapsed < duration);
    }

    private static void Pump(IWindow window)
    {
        window.DoEvents();
        if (window.IsClosing)
            throw new InvalidOperationException("Visible RHI test window was closed.");
    }

    private const string VisibleTriangleSlang =
        """
        struct PSInput
        {
            float4 Position : SV_POSITION;
            float4 Color : COLOR0;
        };

        [shader("vertex")]
        PSInput VSMain(uint vertexID : SV_VertexID)
        {
            float2 positions[3];
            positions[0] = float2(0.0, 0.6);
            positions[1] = float2(0.6, -0.6);
            positions[2] = float2(-0.6, -0.6);

            float4 colors[3];
            colors[0] = float4(1.0, 0.1, 0.1, 1.0);
            colors[1] = float4(0.1, 1.0, 0.1, 1.0);
            colors[2] = float4(0.1, 0.25, 1.0, 1.0);

            PSInput output;
            output.Position = float4(positions[vertexID], 0.0, 1.0);
            output.Color = colors[vertexID];
            return output;
        }

        [shader("pixel")]
        float4 PSMain(PSInput input) : SV_Target0
        {
            return input.Color;
        }
        """;

    private const string VisibleComputeUavSlang =
        """
        RWByteAddressBuffer Output : register(u0, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output.Store(0, 0x12345678);
        }
        """;

    private static Hdr10Metadata TestHdr10Metadata()
        => new()
        {
            RedPrimaryX = 34000,
            RedPrimaryY = 16000,
            GreenPrimaryX = 13250,
            GreenPrimaryY = 34500,
            BluePrimaryX = 7500,
            BluePrimaryY = 3000,
            WhitePointX = 15635,
            WhitePointY = 16450,
            MaxMasteringLuminance = 1000,
            MinMasteringLuminance = 1,
            MaxContentLightLevel = 1000,
            MaxFrameAverageLightLevel = 400,
        };
}

internal static class SlangFixtureCompiler
{
    private static readonly object s_globalSessionLock = new();
    private static IGlobalSession? s_globalSession;
    private static bool s_dxilCompilerLoaded;

    public static byte[] CompileDxil(string source, string moduleName, string entryPoint)
    {
        lock (s_globalSessionLock)
        {
            EnsureDxilCompilerAvailable();
            var globalSession = GetGlobalSession();
            var profile = globalSession.FindProfile("sm_6_5");
            var options = new[]
            {
                new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                new CompilerOptionEntry(
                    CompilerOptionName.DebugInformation,
                    CompilerOptionValue.FromInt(0, 0)),
            };
            var target = new TargetDesc
            {
                Format = SlangCompileTarget.Dxil,
                Profile = profile,
                CompilerOptionEntries = options,
            };
            var sessionDesc = new SessionDesc
            {
                Targets = [target],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
                CompilerOptionEntries = options,
            };
            globalSession.CreateSession(sessionDesc, out var session);
            if (session == null)
                throw new InvalidOperationException("Failed to create Slang session for visible RHI shader fixture.");

            var sourceBlob = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
            var module = session.LoadModuleFromSource(
                moduleName,
                moduleName + ".slang",
                sourceBlob,
                out var moduleDiagnostics);
            if (module == null)
                throw new InvalidOperationException($"Failed to load Slang module '{moduleName}': {BlobText(moduleDiagnostics)}");

            module.FindEntryPointByName(entryPoint, out var entryPointComponent);
            if (entryPointComponent == null)
                throw new InvalidOperationException($"Slang module '{moduleName}' does not define entry point '{entryPoint}'.");

            session.CreateCompositeComponentType(
                [module, entryPointComponent],
                out var composedProgram,
                out var composeDiagnostics);
            if (composedProgram == null)
                throw new InvalidOperationException($"Failed to compose Slang entry point '{entryPoint}': {BlobText(composeDiagnostics)}");

            composedProgram.Link(out var linkedProgram, out var linkDiagnostics);
            if (linkedProgram == null)
                throw new InvalidOperationException($"Failed to link Slang entry point '{entryPoint}': {BlobText(linkDiagnostics)}");

            linkedProgram.GetEntryPointCode(0, 0, out var codeBlob, out var codeDiagnostics);
            if (codeBlob == null || codeBlob.GetBufferSize() == 0)
                throw new InvalidOperationException($"Slang entry point '{entryPoint}' produced empty DXIL: {BlobText(codeDiagnostics)}");

            return codeBlob.Buffer.ToArray();
        }
    }

    private static IGlobalSession GetGlobalSession()
    {
        if (s_globalSession == null)
            Slang.CreateGlobalSession(Slang.ApiVersion, out s_globalSession);
        return s_globalSession ?? throw new InvalidOperationException("Failed to create Slang global session.");
    }

    private static void EnsureDxilCompilerAvailable()
    {
        if (s_dxilCompilerLoaded)
            return;

        string? directory = ResolveDxilCompilerDirectory();
        if (directory == null)
            throw new InvalidOperationException("Slang DXIL compilation requires dxcompiler.dll and dxil.dll. Install the Windows SDK or put both DLLs on PATH.");

        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        bool alreadyOnPath = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(path => string.Equals(Path.GetFullPath(path), directory, StringComparison.OrdinalIgnoreCase));
        if (!alreadyOnPath)
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + currentPath);

        NativeLibrary.Load(Path.Combine(directory, "dxcompiler.dll"));
        NativeLibrary.Load(Path.Combine(directory, "dxil.dll"));
        s_dxilCompilerLoaded = true;
    }

    private static string? ResolveDxilCompilerDirectory()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string fullPath = Path.GetFullPath(directory);
                if (HasDxilCompiler(fullPath))
                    return fullPath;
            }
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string kitBin = Path.Combine(root, "Windows Kits", "10", "bin");
            if (!Directory.Exists(kitBin))
                continue;

            string directCandidate = Path.Combine(kitBin, "x64");
            if (HasDxilCompiler(directCandidate))
                return directCandidate;

            foreach (string versionDirectory in Directory.EnumerateDirectories(kitBin).OrderByDescending(static directory => Path.GetFileName(directory)))
            {
                string candidate = Path.Combine(versionDirectory, "x64");
                if (HasDxilCompiler(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static bool HasDxilCompiler(string directory)
        => File.Exists(Path.Combine(directory, "dxcompiler.dll"))
            && File.Exists(Path.Combine(directory, "dxil.dll"));

    private static string BlobText(ISlangBlob? blob)
    {
        if (blob == null)
            return string.Empty;
        return blob.AsString;
    }
}

internal sealed class SwapchainScope : IDisposable
{
    private readonly IDevice _device;

    private SwapchainScope(IDevice device, SwapchainHandle handle, ISwapchain swapchain)
    {
        _device = device;
        Handle = handle;
        Swapchain = swapchain;
    }

    public SwapchainHandle Handle { get; }
    public ISwapchain Swapchain { get; }

    public static SwapchainScope Create(IDevice device, SwapchainDesc desc)
    {
        var handle = device.CreateSwapchain(desc);
        return new SwapchainScope(device, handle, device.GetSwapchain(handle));
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _device.Destroy(Handle);
    }
}

internal sealed record ScenarioResult(string Name, ScenarioStatus Status, long ElapsedMilliseconds, string Details)
{
    public string StatusText => Status.ToString().ToUpperInvariant();

    public static ScenarioResult Pass(string name, long elapsedMilliseconds, string details)
        => new(name, ScenarioStatus.Passed, elapsedMilliseconds, details);

    public static ScenarioResult Skip(string name, string details, long elapsedMilliseconds = 0)
        => new(name, ScenarioStatus.Skipped, elapsedMilliseconds, details);

    public static ScenarioResult Fail(string name, long elapsedMilliseconds, string details)
        => new(name, ScenarioStatus.Failed, elapsedMilliseconds, details);
}

internal enum ScenarioStatus
{
    Passed,
    Skipped,
    Failed,
}
