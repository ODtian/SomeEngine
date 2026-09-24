using SomeEngine.Core.Diagnostics;
using SomeEngine.Render;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Runtime;

internal sealed record RuntimeStartupOptions(
    int FrameLimit,
    bool WindowVSync,
    uint PresentSyncInterval,
    double UpdatesPerSecond,
    double FramesPerSecond,
    int PipelineWarmupBudget,
    bool WaitForPipelineWarmup,
    bool SkipSwapchainPresent,
    bool VerifyFrameOutput,
    string? RenderDocCapture,
    uint RenderDocFrame,
    bool DeviceValidation,
    bool DynamicScene,
    bool AsyncCompute,
    ClusterDebugMode? ClusterDebug,
    ProfilerOptions Profiler)
{
    public static RuntimeStartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool windowVSync = ReadBooleanOption(
            args,
            enableSwitches: ["--vsync"],
            disableSwitches: ["--no-vsync"],
            defaultValue: true);

        return new RuntimeStartupOptions(
            FrameLimit: ReadPositive(args, "--frames", defaultValue: 0),
            WindowVSync: windowVSync,
            PresentSyncInterval: ReadSync(args, windowVSync),
            UpdatesPerSecond: windowVSync ? 60.0 : 0.0,
            FramesPerSecond: windowVSync ? 60.0 : 0.0,
            PipelineWarmupBudget: ReadPositive(args, "--pipeline-budget", defaultValue: 4),
            WaitForPipelineWarmup: ReadBooleanOption(
                args,
                enableSwitches: ["--wait-pipelines"],
                disableSwitches: ["--no-wait-pipelines"],
                defaultValue: true),
            SkipSwapchainPresent: ReadBooleanOption(
                args,
                enableSwitches: ["--skip-present"],
                disableSwitches: ["--no-skip-present"],
                defaultValue: false),
            VerifyFrameOutput: ReadBooleanOption(
                args,
                enableSwitches: ["--verify-frame-output"],
                disableSwitches: ["--no-verify-frame-output"],
                defaultValue: false),
            RenderDocCapture: ReadOptionalValue(args, "--renderdoc-capture"),
            RenderDocFrame: checked((uint)ReadPositive(args, "--renderdoc-frame", defaultValue: 1)),
            DeviceValidation: ReadBooleanOption(
                args,
                enableSwitches: ["--gpu-validation", "--rhi-validation"],
                disableSwitches: ["--no-gpu-validation", "--no-rhi-validation"],
                defaultValue: false),
            DynamicScene: ReadBooleanOption(
                args,
                enableSwitches: ["--dynamic-scene"],
                disableSwitches: ["--static-scene", "--no-dynamic-scene"],
                defaultValue: true),
            AsyncCompute: ReadBooleanOption(
                args,
                enableSwitches: ["--async-compute"],
                disableSwitches: ["--no-async-compute"],
                defaultValue: true),
            ClusterDebug: ReadClusterDebug(args),
            Profiler: SomeEngine.Core.Diagnostics.Profiler.ParseOptions(args));
    }

    private static int ReadPositive(string[] args, string optionName, int defaultValue)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException($"{optionName} requires a positive integer value.");

            if (!int.TryParse(args[index], out int parsed) || parsed <= 0)
                throw new ArgumentException($"{optionName} requires a positive integer value.");

            return parsed;
        }

        return defaultValue;
    }

    private static uint ReadSync(string[] args, bool windowVSync)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            const string presentIntervalPrefix = "--present-interval=";
            if (arg.StartsWith(presentIntervalPrefix, StringComparison.OrdinalIgnoreCase))
                return SomeEngine.Render.PresentSyncInterval.Parse("--present-interval", arg[presentIntervalPrefix.Length..]);

            if (!string.Equals(arg, "--present-interval", StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException("--present-interval requires an integer value from 0 to 4.");

            return SomeEngine.Render.PresentSyncInterval.Parse("--present-interval", args[index]);
        }

        return windowVSync ? 1u : 0u;
    }

    private static ClusterDebugMode? ReadClusterDebug(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            const string prefix = "--cluster-debug=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return ParseClusterDebug(arg[prefix.Length..]);

            if (!string.Equals(arg, "--cluster-debug", StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException("--cluster-debug requires a ClusterDebugMode name or integer value.");

            return ParseClusterDebug(args[index]);
        }

        return null;
    }

    private static string? ReadOptionalValue(string[] args, string optionName)
    {
        string prefix = optionName + "=";
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg[prefix.Length..];

            if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException($"{optionName} requires a value.");

            return args[index];
        }

        return null;
    }

    private static ClusterDebugMode ParseClusterDebug(string value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out ClusterDebugMode mode)
            && Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new ArgumentException($"Unknown cluster debug mode '{value}'.");
    }

    private static bool ReadBooleanOption(
        string[] args,
        IReadOnlyList<string> enableSwitches,
        IReadOnlyList<string> disableSwitches,
        bool defaultValue)
    {
        for (int index = args.Length - 1; index >= 0; index--)
        {
            string arg = args[index];
            if (Contains(enableSwitches, arg))
                return true;
            if (Contains(disableSwitches, arg))
                return false;
        }

        return defaultValue;
    }

    private static bool Contains(IReadOnlyList<string> values, string candidate)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
