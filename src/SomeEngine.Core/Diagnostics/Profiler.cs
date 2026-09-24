using System.Runtime.CompilerServices;
using System.Threading;

namespace SomeEngine.Core.Diagnostics;

// Profiler is only an instrumentation bridge for external profilers such as
// Tracy. Do not add in-engine profiler storage, timing tables, call trees,
// counter aggregation, report writers, or profile-output files here. Counters
// must be pass-through events for an external profiler sink, not local reports.
public static class Profiler
{
    private static readonly object ConfigureLock = new();
    private static IProfileSink _sink = EmptySink.Disabled;

    public static bool IsEnabled => Status.HasActiveProfiler;

    public static bool IsActive => CurrentSink is not EmptySink;

    public static bool IsConnected => Status.TracyConnected;

    public static bool NeedsCounters => CurrentSink.NeedsCounters;

    public static bool NeedsDeviceTime => CurrentSink.NeedsDeviceTime;

    public static ProfilerStatus Status => CurrentSink.Status;

    public static ProfilerOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool? enableTracy = null;
        bool throwOnUnavailable = false;
        string? nativeLibraryPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            RejectManagedProfilerOption(arg);

            if (IsAny(arg, "--profile"))
            {
                enableTracy = true;
                continue;
            }

            if (IsAny(arg, "--tracy"))
            {
                enableTracy = true;
                continue;
            }

            if (IsAny(arg, "--no-profile"))
            {
                enableTracy = false;
                continue;
            }

            if (IsAny(arg, "--no-tracy"))
            {
                enableTracy = false;
                continue;
            }

            if (IsAny(arg, "--tracy-required", "--profile-required"))
            {
                enableTracy = true;
                throwOnUnavailable = true;
                continue;
            }

            if (ReadInline(arg, "--tracy-native=", out string? inlinePath)
                || ReadInline(arg, "--tracy-native-dll=", out inlinePath))
            {
                nativeLibraryPath = RequirePath(arg, inlinePath);
                continue;
            }

            if (IsAny(arg, "--tracy-native", "--tracy-native-dll"))
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"{arg} requires a native library path.");
                nativeLibraryPath = RequirePath(arg, args[index]);
            }
        }

        enableTracy ??= false;

        return new ProfilerOptions
        {
            EnableTracy = enableTracy.Value,
            TracyNativeLibraryPath = string.IsNullOrWhiteSpace(nativeLibraryPath) ? null : nativeLibraryPath,
            ThrowOnUnavailable = throwOnUnavailable,
        };
    }

    public static void Configure(ProfilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IProfileSink next = ProfileSinks.Create(options);
        lock (ConfigureLock)
        {
            IProfileSink previous = _sink;
            _sink = next;
            previous.Shutdown();
        }
    }

    public static Scope BeginScope(
        string? name = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (!IsActive)
            return default;

        return new Scope(Begin(ProfileScopeEvent.Cpu(name, memberName, filePath, lineNumber)));
    }

    public static Scope BeginGraphPass(string name, int passIndex, string phase, ulong frameIndex = 0)
    {
        if (!IsActive)
            return default;

        return new Scope(Begin(ProfileScopeEvent.GraphPass(name, passIndex, phase, frameIndex)));
    }

    public static Scope BeginMarker(string name, string backend, ulong frameIndex = 0)
    {
        if (!IsActive)
            return default;

        return new Scope(Begin(ProfileScopeEvent.Marker(name, backend, frameIndex)));
    }

    public static void FrameMark(string? name = null)
    {
        if (IsActive)
            CurrentSink.FrameMark(new FrameMarkEvent(name));
    }

    public static void SetThreadName(string name)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(name))
            return;

        CurrentSink.SetThreadName(new ThreadNameEvent(name));
    }

    public static void QueueSubmit(string backend, string queueType, int commandBufferCount)
    {
        if (IsActive)
            CurrentSink.QueueSubmit(new QueueSubmitEvent(backend, queueType, commandBufferCount));
    }

    public static void Present(string backend, uint syncInterval, bool allowTearing)
    {
        if (IsActive)
            CurrentSink.Present(new PresentEvent(backend, syncInterval, allowTearing));
    }

    public static bool NeedsNames(bool debugMarkers)
        => debugMarkers || IsActive;

    public static void GraphCompileHit(string hit)
    {
        if (!NeedsCounters)
            return;

        Report(hit switch
        {
            "Recent" => GraphStats.CompileRecent(),
            "Local" => GraphStats.CompileLocal(),
            "Shared" => GraphStats.CompileShared(),
            _ => GraphStats.CompileHit(),
        });
    }

    public static void GraphCompileMiss()
    {
        if (NeedsCounters)
            Report(GraphStats.CompileMiss());
    }

    public static void GraphCompileRecent()
    {
        if (NeedsCounters)
            Report(GraphStats.CompileRecent());
    }

    public static void GraphAliasHit()
    {
        if (NeedsCounters)
            Report(GraphStats.AliasHit());
    }

    public static void GraphAliasMiss()
    {
        if (NeedsCounters)
            Report(GraphStats.AliasMiss());
    }

    public static void BindingLocalHit()
    {
        if (NeedsCounters)
            Report(BindingStats.LocalHit());
    }

    public static void BindingShared()
    {
        if (NeedsCounters)
            Report(BindingStats.Shared());
    }

    public static void BindingCreated()
    {
        if (NeedsCounters)
            Report(BindingStats.Created());
    }

    public static void RenderGraphBarriers(int textures, int buffers, int aliases)
    {
        if (NeedsCounters)
            Barriers("RenderGraph", textures, buffers, aliases);
    }

    public static void Barriers(string source, int textures, int buffers, int aliases)
    {
        if (NeedsCounters)
            Report(new BarrierStats(source, textures, buffers, aliases));
    }

    public static void NativeBarriers(string source, int count)
    {
        if (NeedsCounters)
            Report(new BarrierStats(source, 0, 0, 0, Native: count));
    }

    public static void BarrierDependencies(string source, int count)
    {
        if (NeedsCounters)
            Report(new BarrierStats(source, 0, 0, 0, Dependencies: count));
    }

    public static void Descriptors(string source, int resources, int samplers)
    {
        if (NeedsCounters)
            Report(new DescriptorStats(source, resources, samplers));
    }

    public static void QueueBatch(string queue)
    {
        if (NeedsCounters)
            Report(QueueStats.Batch(Label(queue)));
    }

    public static void QueueFinal(string queue)
    {
        if (NeedsCounters)
            Report(QueueStats.Final(Label(queue)));
    }

    public static void QueueLink(string source, string target)
    {
        if (NeedsCounters)
            Report(QueueStats.Link(Label(source), Label(target)));
    }

    public static void QueueWait(string queue, int count)
    {
        if (NeedsCounters)
            Report(QueueStats.Wait(Label(queue), count));
    }

    public static void QueueSignal(string queue, int count = 1)
    {
        if (NeedsCounters)
            Report(QueueStats.Signal(Label(queue), count));
    }

    public static void PipelineIssue(
        string result,
        string need,
        string source = "",
        string site = "",
        string owner = "")
    {
        if (!NeedsCounters)
            return;

        string use = Use(need);
        string metric = Label(result);
        Report(PipelineStatsForIssue(metric, use));

        if (string.IsNullOrWhiteSpace(source)
            && string.IsNullOrWhiteSpace(site)
            && string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        Count(
            $"Pipeline:Issue:{metric}:{use}:Source={Label(source)}:Site={Label(site)}:Owner={Label(owner)}",
            1);
    }

    public static void PipelineInvalid(string need)
    {
        if (NeedsCounters)
            Report(PipelineStats.Invalid(Use(need)));
    }

    public static void PipelineHit(string need)
    {
        if (NeedsCounters)
            Report(PipelineStats.Hit(Use(need)));
    }

    public static void PipelineLocalHit(string need)
    {
        if (NeedsCounters)
            Report(PipelineStats.LocalHit(Use(need)));
    }

    public static void PipelineQueued(string need)
    {
        if (NeedsCounters)
            Report(PipelineStats.Queued(Use(need)));
    }

    public static void PipelineShared(string need)
    {
        if (NeedsCounters)
            Report(PipelineStats.Shared(Use(need)));
    }

    public static void PipelineStatus(string status, string need)
    {
        if (NeedsCounters)
            Report(PipelineStatsForStatus(status, Use(need)));
    }

    public static void PipelineReady()
    {
        if (NeedsCounters)
            Report(PipelineStats.Ready());
    }

    public static void PipelineFailed()
    {
        if (NeedsCounters)
            Report(PipelineStats.Failed());
    }

    internal static void Report(in BindingStats stats)
    {
        if (NeedsCounters)
            Count($"BindingSet:Cache:{stats.Metric}", stats.Amount);
    }

    internal static void Report(in GraphStats stats)
    {
        if (!NeedsCounters || stats.Amount == 0)
            return;

        switch (stats.Metric)
        {
            case "CompileHit":
                Count("Graph:Cache:CompileHit", stats.Amount);
                break;
            case "CompileMiss":
                Count("Graph:Cache:CompileMiss", stats.Amount);
                break;
            case "CompileRecent":
                Count("Graph:Cache:CompileHit", stats.Amount);
                Count("Graph:Cache:CompileRecent", stats.Amount);
                break;
            case "CompileLocal":
                Count("Graph:Cache:CompileHit", stats.Amount);
                Count("Graph:Cache:CompileLocal", stats.Amount);
                break;
            case "CompileShared":
                Count("Graph:Cache:CompileHit", stats.Amount);
                Count("Graph:Cache:CompileShared", stats.Amount);
                break;
            case "AliasHit":
                Count("Graph:Cache:AliasHit", stats.Amount);
                break;
            case "AliasMiss":
                Count("Graph:Cache:AliasMiss", stats.Amount);
                break;
        }
    }

    internal static void Report(in PipelineStats stats)
    {
        if (NeedsCounters)
            Count($"Pipeline:Cache:{stats.Metric}:{stats.Use}", stats.Amount);
    }

    internal static void Report(in QueueStats stats)
    {
        if (!NeedsCounters || stats.Amount == 0 || string.IsNullOrWhiteSpace(stats.Queue))
            return;

        if (stats.HasTarget)
        {
            if (!string.IsNullOrWhiteSpace(stats.Target))
                Count($"QueueGraph:{stats.Metric}:{stats.Queue}->{stats.Target}", stats.Amount);
            return;
        }

        Count($"QueueGraph:{stats.Metric}:{stats.Queue}", stats.Amount);
    }

    internal static void Report(in BarrierStats stats)
    {
        if (!NeedsCounters || string.IsNullOrWhiteSpace(stats.Source))
            return;

        if (stats.Textures != 0)
            Count($"Barrier:{stats.Source}:Texture", stats.Textures);
        if (stats.Buffers != 0)
            Count($"Barrier:{stats.Source}:Buffer", stats.Buffers);
        if (stats.Aliases != 0)
            Count($"Barrier:{stats.Source}:Alias", stats.Aliases);
        if (stats.Native != 0)
            Count($"Barrier:{stats.Source}:Native", stats.Native);
        if (stats.Dependencies != 0)
            Count($"Barrier:{stats.Source}:Dependency", stats.Dependencies);
    }

    internal static void Report(in DescriptorStats stats)
    {
        if (!NeedsCounters || string.IsNullOrWhiteSpace(stats.Source))
            return;

        if (stats.Resources != 0)
            Count($"Descriptor:{stats.Source}:Resource", stats.Resources);
        if (stats.Samplers != 0)
            Count($"Descriptor:{stats.Source}:Sampler", stats.Samplers);
    }

    public static void DeviceTime(string name, int passIndex, string phase, long nanoseconds)
    {
        if (!NeedsDeviceTime || nanoseconds <= 0 || string.IsNullOrWhiteSpace(name))
            return;

        CurrentSink.DeviceTime(new DeviceTimeEvent(name, passIndex, phase, nanoseconds));
    }

    private static void Count(string name, long amount)
    {
        if (!NeedsCounters || amount == 0 || string.IsNullOrWhiteSpace(name))
            return;

        CurrentSink.Count(new ProfileCount(name, amount));
    }

    private static string Label(string value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private static string Use(string need)
        => need switch
        {
            "Required" => "Required",
            "Optional" => "Optional",
            _ => "Any",
        };

    private static PipelineStats PipelineStatsForStatus(string status, string use)
        => status switch
        {
            "Queued" => PipelineStats.Queued(use),
            "Creating" => PipelineStats.Creating(use),
            "Ready" => PipelineStats.Ready(use),
            "Failed" => PipelineStats.Failed(use),
            _ => PipelineStats.Invalid(use),
        };

    private static PipelineStats PipelineStatsForIssue(string result, string use)
        => result switch
        {
            "Missed" => PipelineStats.Missed(use),
            "TooLate" => PipelineStats.TooLate(use),
            "Failed" => PipelineStats.Failed(use),
            "Untracked" => PipelineStats.Untracked(use),
            _ => PipelineStats.Invalid(use),
        };

    public static void Shutdown()
    {
        lock (ConfigureLock)
        {
            _sink.Shutdown();
        }
    }

    internal static void ValidationMessage(string source, string severity, string message, int? id = null)
    {
        if (IsActive)
            CurrentSink.ValidationMessage(new ValidationMessageEvent(source, severity, message, id));
    }

    private static ProfileScope Begin(in ProfileScopeEvent ev)
    {
        IProfileSink sink = CurrentSink;
        if (sink is EmptySink)
            return default;

        return sink.TryBeginScope(ev, out ProfileToken token)
            ? new ProfileScope(sink, token)
            : default;
    }

    private static IProfileSink CurrentSink => Volatile.Read(ref _sink);

    private static bool IsAny(string value, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ReadInline(string arg, string prefix, out string? value)
    {
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        value = arg[prefix.Length..];
        return true;
    }

    private static string RequirePath(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name.TrimEnd('=')} requires a path.");

        return value;
    }

    private static void RejectManagedProfilerOption(string arg)
    {
        string option = arg;
        int equals = option.IndexOf('=');
        if (equals >= 0)
            option = option[..equals];

        if (IsAny(
            option,
            "--profile-csharp",
            "--csharp-profile",
            "--profile-managed",
            "--managed-profile",
            "--no-profile-csharp",
            "--no-csharp-profile",
            "--no-profile-managed",
            "--no-managed-profile",
            "--profile-detail-counters",
            "--profile-detailed-counters",
            "--profile-deep-counters",
            "--profile-top-n",
            "--profile-output",
            "--profile-warmup-frames"))
        {
            throw new ArgumentException(
                $"{option} was removed. Profiler is only an instrumentation bridge; use --profile/--tracy with Tracy or another external profiler.");
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ProfileScope _scope;

        internal Scope(ProfileScope scope)
        {
            _scope = scope;
        }

        public bool IsActive => _scope.IsActive;

        public void Dispose()
            => _scope.Dispose();
    }
}
