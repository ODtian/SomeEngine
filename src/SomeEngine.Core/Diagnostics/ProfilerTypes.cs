namespace SomeEngine.Core.Diagnostics;

public sealed record ProfilerOptions
{
    public bool EnableTracy { get; init; }
    public string? TracyNativeLibraryPath { get; init; }
    public bool ThrowOnUnavailable { get; init; }

    public static ProfilerOptions Disabled { get; } = new();
}

public sealed record ProfilerStatus(
    bool IsRequested,
    bool HasActiveProfiler,
    bool TracyActive,
    bool TracyConnected,
    string? TracyNativeLibraryPath,
    string? FailureReason)
{
    public string ToDisplayString()
    {
        if (!IsRequested)
            return "Profiler: disabled.";

        if (!HasActiveProfiler)
            return $"Profiler: requested but inactive ({FailureReason ?? "unavailable"}).";

        List<string> active = [];
        if (TracyActive)
        {
            active.Add(
                $"Tracy active via {(string.IsNullOrWhiteSpace(TracyNativeLibraryPath) ? "default native bridge" : TracyNativeLibraryPath)}; connected={TracyConnected}");
        }

        string suffix = string.IsNullOrWhiteSpace(FailureReason) ? string.Empty : $" ({FailureReason})";
        return $"Profiler: {string.Join("; ", active)}{suffix}.";
    }
}

internal enum ProfileScopeKind
{
    Cpu,
    RenderGraphPass,
    CommandMarker,
}

internal readonly record struct ProfileScopeEvent(
    ProfileScopeKind Kind,
    string Name,
    string MemberName = "",
    string FilePath = "",
    int LineNumber = 0,
    int PassIndex = -1,
    string Phase = "",
    string Backend = "",
    ulong FrameIndex = 0)
{
    public static ProfileScopeEvent Cpu(
        string? name,
        string memberName = "",
        string filePath = "",
        int lineNumber = 0)
    {
        string resolvedName = string.IsNullOrWhiteSpace(name) ? memberName : name;
        return new ProfileScopeEvent(
            ProfileScopeKind.Cpu,
            resolvedName,
            string.IsNullOrWhiteSpace(memberName) ? resolvedName : memberName,
            filePath,
            lineNumber);
    }

    public static ProfileScopeEvent GraphPass(
        string name,
        int passIndex,
        string phase,
        ulong frameIndex = 0)
    {
        string resolvedPhase = string.IsNullOrWhiteSpace(phase) ? "Pass" : phase;
        string resolvedName = string.IsNullOrWhiteSpace(name) ? resolvedPhase : name;
        return new ProfileScopeEvent(
            ProfileScopeKind.RenderGraphPass,
            $"RenderGraph {resolvedPhase}: {resolvedName}",
            $"RenderGraph.{resolvedPhase}",
            string.Empty,
            0,
            passIndex,
            resolvedPhase,
            string.Empty,
            frameIndex);
    }

    public static ProfileScopeEvent Marker(
        string name,
        string backend,
        ulong frameIndex = 0)
    {
        string resolvedBackend = string.IsNullOrWhiteSpace(backend) ? "Unknown" : backend;
        string resolvedName = string.IsNullOrWhiteSpace(name) ? resolvedBackend : name;
        return new ProfileScopeEvent(
            ProfileScopeKind.CommandMarker,
            $"CommandMarker {resolvedBackend}: {resolvedName}",
            "CommandMarker",
            string.Empty,
            0,
            -1,
            string.Empty,
            resolvedBackend,
            frameIndex);
    }
}

internal readonly record struct ThreadNameEvent(string Name);

internal readonly record struct FrameMarkEvent(string? Name = null);

internal readonly record struct QueueSubmitEvent(
    string Backend,
    string QueueType,
    int CommandBufferCount);

internal readonly record struct PresentEvent(
    string Backend,
    uint SyncInterval,
    bool AllowTearing);

internal readonly record struct ValidationMessageEvent(
    string Source,
    string Severity,
    string Message,
    int? Id = null);

internal readonly record struct ProfileCount(string Name, long Amount);

internal readonly record struct BindingStats
{
    private BindingStats(string metric, long amount)
    {
        Metric = metric;
        Amount = amount;
    }

    internal string Metric { get; }
    internal long Amount { get; }

    public static BindingStats LocalHit(long amount = 1) => new("LocalHit", amount);
    public static BindingStats Shared(long amount = 1) => new("Shared", amount);
    public static BindingStats Created(long amount = 1) => new("Created", amount);
}

internal readonly record struct GraphStats
{
    private GraphStats(string metric, long amount)
    {
        Metric = metric;
        Amount = amount;
    }

    internal string Metric { get; }
    internal long Amount { get; }

    public static GraphStats CompileHit(long amount = 1) => new("CompileHit", amount);
    public static GraphStats CompileMiss(long amount = 1) => new("CompileMiss", amount);
    public static GraphStats CompileRecent(long amount = 1) => new("CompileRecent", amount);
    public static GraphStats CompileLocal(long amount = 1) => new("CompileLocal", amount);
    public static GraphStats CompileShared(long amount = 1) => new("CompileShared", amount);
    public static GraphStats AliasHit(long amount = 1) => new("AliasHit", amount);
    public static GraphStats AliasMiss(long amount = 1) => new("AliasMiss", amount);
}

internal readonly record struct QueueStats
{
    private QueueStats(string metric, string queue, string target, long amount)
    {
        Metric = metric;
        Queue = queue;
        Target = target;
        Amount = amount;
    }

    internal string Metric { get; }
    internal string Queue { get; }
    internal string Target { get; }
    internal long Amount { get; }
    internal bool HasTarget => !string.IsNullOrWhiteSpace(Target);

    public static QueueStats Batch(string queue, long amount = 1) => new("Batch", queue, string.Empty, amount);
    public static QueueStats Final(string queue, long amount = 1) => new("Final", queue, string.Empty, amount);
    public static QueueStats Wait(string queue, long amount = 1) => new("Wait", queue, string.Empty, amount);
    public static QueueStats Signal(string queue, long amount = 1) => new("Signal", queue, string.Empty, amount);
    public static QueueStats Link(string source, string target, long amount = 1) => new("Link", source, target, amount);
}

internal readonly record struct BarrierStats(
    string Source,
    long Textures,
    long Buffers,
    long Aliases,
    long Native = 0,
    long Dependencies = 0);

internal readonly record struct DescriptorStats(
    string Source,
    long Resources,
    long Samplers);

internal readonly record struct PipelineStats
{
    private PipelineStats(string metric, string use, long amount)
    {
        Metric = metric;
        Use = string.IsNullOrWhiteSpace(use) ? "Any" : use;
        Amount = amount;
    }

    internal string Metric { get; }
    internal string Use { get; }
    internal long Amount { get; }

    public static PipelineStats Invalid(string use = "Any", long amount = 1) => new("Invalid", use, amount);
    public static PipelineStats Hit(string use = "Any", long amount = 1) => new("Hit", use, amount);
    public static PipelineStats Queued(string use = "Any", long amount = 1) => new("Queued", use, amount);
    public static PipelineStats Shared(string use = "Any", long amount = 1) => new("Shared", use, amount);
    public static PipelineStats LocalHit(string use = "Any", long amount = 1) => new("LocalHit", use, amount);
    public static PipelineStats Creating(string use = "Any", long amount = 1) => new("Creating", use, amount);
    public static PipelineStats Ready(string use = "Any", long amount = 1) => new("Ready", use, amount);
    public static PipelineStats Failed(string use = "Any", long amount = 1) => new("Failed", use, amount);
    public static PipelineStats Missed(string use = "Any", long amount = 1) => new("Missed", use, amount);
    public static PipelineStats TooLate(string use = "Any", long amount = 1) => new("TooLate", use, amount);
    public static PipelineStats Untracked(string use = "Any", long amount = 1) => new("Untracked", use, amount);
}

internal readonly record struct DeviceTimeEvent(
    string Name,
    int PassIndex,
    string Phase,
    long Nanoseconds);

internal readonly struct ProfileScope : IDisposable
{
    private readonly IProfileSink? _sink;
    private readonly ProfileToken _token;

    internal ProfileScope(IProfileSink sink, ProfileToken token)
    {
        _sink = sink;
        _token = token;
    }

    internal bool IsActive => _sink != null && _token.Active != 0;

    public void Dispose()
        => _sink?.EndScope(_token);
}

internal readonly record struct ProfileToken(ulong Id, int Active);

internal interface IProfileSink
{
    ProfilerStatus Status { get; }
    bool NeedsCounters { get; }
    bool NeedsDeviceTime { get; }
    bool TryBeginScope(in ProfileScopeEvent ev, out ProfileToken token);
    void EndScope(ProfileToken token);
    void SetThreadName(in ThreadNameEvent ev);
    void FrameMark(in FrameMarkEvent ev);
    void QueueSubmit(in QueueSubmitEvent ev);
    void Present(in PresentEvent ev);
    void ValidationMessage(in ValidationMessageEvent ev);
    void Count(in ProfileCount count);
    void DeviceTime(in DeviceTimeEvent ev);
    void Shutdown();
}

internal sealed class EmptySink : IProfileSink
{
    public static readonly EmptySink Disabled = new(
        new ProfilerStatus(
            IsRequested: false,
            HasActiveProfiler: false,
            TracyActive: false,
            TracyConnected: false,
            TracyNativeLibraryPath: null,
            FailureReason: null));

    private EmptySink(ProfilerStatus status)
    {
        Status = status;
    }

    public ProfilerStatus Status { get; }
    public bool NeedsCounters => false;
    public bool NeedsDeviceTime => false;

    public static EmptySink Unavailable(string? tracyNativeLibraryPath, string? failureReason)
        => new(
            new ProfilerStatus(
                IsRequested: true,
                HasActiveProfiler: false,
                TracyActive: false,
                TracyConnected: false,
                TracyNativeLibraryPath: tracyNativeLibraryPath,
                FailureReason: string.IsNullOrWhiteSpace(failureReason) ? "Native profiler backend is unavailable." : failureReason));

    public bool TryBeginScope(in ProfileScopeEvent ev, out ProfileToken token)
    {
        token = default;
        return false;
    }

    public void EndScope(ProfileToken token)
    {
    }

    public void SetThreadName(in ThreadNameEvent ev)
    {
    }

    public void FrameMark(in FrameMarkEvent ev)
    {
    }

    public void QueueSubmit(in QueueSubmitEvent ev)
    {
    }

    public void Present(in PresentEvent ev)
    {
    }

    public void ValidationMessage(in ValidationMessageEvent ev)
    {
    }

    public void Count(in ProfileCount count)
    {
    }

    public void DeviceTime(in DeviceTimeEvent ev)
    {
    }

    public void Shutdown()
    {
    }
}

internal static class ProfileSinks
{
    public static IProfileSink Create(ProfilerOptions options)
    {
        if (!options.EnableTracy)
            return EmptySink.Disabled;

        string? failureReason = null;

        if (TracySink.TryCreate(options.TracyNativeLibraryPath, out var tracy, out failureReason))
            return tracy;

        if (options.ThrowOnUnavailable)
        {
            throw new InvalidOperationException(
                $"Tracy profiling was requested, but the native Tracy bridge is unavailable: {failureReason}");
        }

        return EmptySink.Unavailable(options.TracyNativeLibraryPath, failureReason);
    }
}
