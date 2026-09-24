using System.Threading;
using SomeEngine.Core.Collections;

namespace SomeEngine.Render.RHI;

public sealed class PipelineLease : IDisposable
{
    private readonly PipelineCache _cache;
    private PipelineTicket[] _tickets;

    internal PipelineLease(PipelineCache cache, PipelineTicket[] tickets)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
    }

    public IReadOnlyList<PipelineTicket> Tickets => _tickets;

    public PipelineWarmup Warmup(int budget)
        => _cache.Warmup(_tickets, budget);

    public PipelineWarmup WaitRequired()
        => _cache.WaitRequired(_tickets);

    public PipelineTicket[] Detach()
    {
        PipelineTicket[] tickets = _tickets;
        _tickets = [];
        return tickets;
    }

    public void Dispose()
    {
        PipelineTicket[] tickets = _tickets;
        if (tickets.Length == 0)
            return;

        _tickets = [];
        for (int i = 0; i < tickets.Length; i++)
            _cache.Release(ref tickets[i]);
    }
}

internal sealed class PipelineSources : IDisposable
{
    private readonly Lock _gate = new();
    private readonly PipelineCache _cache;
    private readonly Dictionary<IPipelineSource, SourceRecord> _sources = new(ReferenceEqualityComparer.Instance);
    private PipelineTicket[] _tickets = [];
    private PipelineWarmup _lastReport = PipelineWarmup.Empty;
    private bool _ticketsDirty = true;
    private bool _lastReportValid;
    private bool _disposed;

    internal PipelineSources(PipelineCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    internal static string SourceName(IPipelineSource source)
    {
        string name = source.Name;
        return string.IsNullOrWhiteSpace(name) ? source.GetType().Name : name;
    }

    internal PipelineSourceLease Add(IPipelineSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sources.TryGetValue(source, out SourceRecord? record))
            {
                record.Count = checked(record.Count + 1);
            }
            else
            {
                _sources.Add(source, new SourceRecord(source));
                _ticketsDirty = true;
                _lastReportValid = false;
            }
        }

        return new PipelineSourceLease(this, source);
    }

    internal bool Remove(IPipelineSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        PipelineLease? removed = null;
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (!_sources.TryGetValue(source, out SourceRecord? record))
                return false;

            if (record.Count > 1)
            {
                record.Count--;
                return true;
            }

            _sources.Remove(source);
            _ticketsDirty = true;
            _lastReportValid = false;
            removed = record.Lease;
        }

        removed?.Dispose();
        return true;
    }

    internal bool Contains(IPipelineSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _sources.ContainsKey(source);
        }
    }

    internal PipelineWarmup Refresh()
    {
        RefreshRecords();
        if (_lastReportValid && !_ticketsDirty)
            return _lastReport;

        PipelineTicket[] tickets = CaptureTickets();
        PipelineWarmup report = tickets.Length == 0
            ? PipelineWarmup.Empty
            : _cache.Inspect(tickets);
        _cache.RecordIssues(report.Issues);
        _lastReport = report;
        _lastReportValid = true;
        return report;
    }

    internal PipelineWarmup Warmup(int budget)
    {
        if (budget < 0)
            throw new ArgumentOutOfRangeException(nameof(budget));

        RefreshRecords();
        if (_lastReportValid && !_ticketsDirty && _lastReport.Complete)
        {
            PipelineTicket[] currentTickets = CaptureTickets();
            PipelineWarmup currentReport = currentTickets.Length == 0
                ? PipelineWarmup.Empty
                : _cache.Inspect(currentTickets);
            _lastReport = currentReport;
            return currentReport;
        }

        PipelineTicket[] tickets = CaptureTickets();
        PipelineWarmup report = tickets.Length == 0
            ? PipelineWarmup.Empty
            : _cache.Warmup(tickets, budget);
        _lastReport = report;
        _lastReportValid = true;
        return report;
    }

    internal PipelineWarmup WaitRequired()
    {
        RefreshRecords();
        PipelineTicket[] tickets = CaptureTickets();
        PipelineWarmup report = tickets.Length == 0
            ? PipelineWarmup.Empty
            : _cache.WaitRequired(tickets);
        _lastReport = report;
        _lastReportValid = true;
        return report;
    }

    public void Dispose()
    {
        var leases = new List<PipelineLease>();
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (SourceRecord record in _sources.Values)
            {
                if (record.Lease != null)
                    leases.Add(record.Lease);
            }
            _sources.Clear();
        }

        for (int i = 0; i < leases.Count; i++)
            leases[i].Dispose();
    }

    private void RefreshRecords()
    {
        while (true)
        {
            SourceRecord? stale = null;
            uint version = 0;
            lock (_gate)
            {
                ThrowIfDisposed();
                foreach (SourceRecord record in _sources.Values)
                {
                    uint current = record.Source.Version;
                    if (record.Loaded && record.Version == current)
                        continue;

                    stale = record;
                    version = current;
                    break;
                }
            }

            if (stale == null)
                return;

            PipelineLease? created = Create(stale.Source);
            PipelineLease? old = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    created?.Dispose();
                    return;
                }

                if (!_sources.TryGetValue(stale.Source, out SourceRecord? current)
                    || !ReferenceEquals(current, stale)
                    || current.Source.Version != version)
                {
                    created?.Dispose();
                    continue;
                }

                old = current.Lease;
                current.Lease = created;
                current.Version = version;
                current.Loaded = true;
                _ticketsDirty = true;
                _lastReportValid = false;
            }

            old?.Dispose();
        }
    }

    private PipelineTicket[] CaptureTickets()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_ticketsDirty)
                return _tickets;

            int count = 0;
            foreach (SourceRecord record in _sources.Values)
                count += record.Lease?.Tickets.Count ?? 0;

            if (count == 0)
            {
                _tickets = [];
                _ticketsDirty = false;
                return _tickets;
            }

            var tickets = new PipelineTicket[count];
            int offset = 0;
            foreach (SourceRecord record in _sources.Values)
            {
                if (record.Lease == null)
                    continue;

                IReadOnlyList<PipelineTicket> sourceTickets = record.Lease.Tickets;
                for (int i = 0; i < sourceTickets.Count; i++)
                    tickets[offset++] = sourceTickets[i];
            }

            _tickets = tickets;
            _ticketsDirty = false;
            return _tickets;
        }
    }

    private PipelineLease? Create(IPipelineSource source)
    {
        var collector = new PipelineCollector();
        source.Collect(collector);
        collector.SetSource(SourceName(source));

        return collector.Count == 0
            ? null
            : _cache.Lease(collector);
    }

    private sealed class SourceRecord(IPipelineSource source)
    {
        public IPipelineSource Source { get; } = source;
        public PipelineLease? Lease;
        public uint Version;
        public int Count = 1;
        public bool Loaded;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PipelineSources));
    }
}

public sealed class PipelineSourceLease : IDisposable
{
    private PipelineSources? _sources;
    private readonly IPipelineSource _source;

    internal PipelineSourceLease(PipelineSources sources, IPipelineSource source)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public void Dispose()
    {
        PipelineSources? sources = Interlocked.Exchange(ref _sources, null);
        sources?.Remove(_source);
    }
}

public readonly record struct PipelineWarmup(
    int Requested,
    int Processed,
    int Ready,
    int Queued,
    int Failed,
    int RequiredQueued,
    int OptionalQueued,
    int RequiredFailed,
    int OptionalFailed,
    bool BudgetLimited,
    IReadOnlyList<PipelineIssue> Issues)
{
    public static PipelineWarmup Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false,
        Array.Empty<PipelineIssue>());

    public bool RequiredReady => RequiredQueued == 0 && RequiredFailed == 0;
    public bool Complete => Queued == 0;
    public bool ReadyToUse => RequiredReady && Complete;
    public bool HasIssues => Issues is { Count: > 0 };
}

public readonly record struct PipelineIssue(
    uint TicketId,
    string Name,
    string Owner,
    PipelineNeed Need,
    PipelineStatus Status,
    string? Error,
    string Site = "",
    string Source = "",
    int Count = 1,
    PipelineIssueResult Result = PipelineIssueResult.Missed);

public interface PipelineIssueSink
{
    void Add(PipelineIssue issue);
}

internal enum PipelineKind
{
    Compute,
    Graphics,
}

internal readonly record struct PipelineRequest(
    PipelineKind Kind,
    ComputeState? Compute,
    GraphicsState? Graphics,
    PipelineNeed Need,
    string Owner,
    string Source)
{
    public static PipelineRequest ForCompute(
        ComputeState state,
        string owner,
        PipelineNeed need = PipelineNeed.Required,
        string source = "")
        => new(PipelineKind.Compute, state, null, need, owner, source);

    public static PipelineRequest ForGraphics(
        GraphicsState state,
        string owner,
        PipelineNeed need = PipelineNeed.Required,
        string source = "")
        => new(PipelineKind.Graphics, null, state, need, owner, source);
}

public sealed class PipelineCollector
{
    private readonly List<PipelineRequest> _requests = [];

    public int Count => _requests.Count;
    internal PipelineRequest this[int index] => _requests[index];

    public void AddCompute(
        ComputeState state,
        string owner,
        PipelineNeed need = PipelineNeed.Required,
        string source = "")
    {
        ArgumentNullException.ThrowIfNull(state);
        _requests.Add(PipelineRequest.ForCompute(state, owner, need, source));
    }

    public void AddGraphics(
        GraphicsState state,
        string owner,
        PipelineNeed need = PipelineNeed.Required,
        string source = "")
    {
        ArgumentNullException.ThrowIfNull(state);
        _requests.Add(PipelineRequest.ForGraphics(state, owner, need, source));
    }

    internal void Add(PipelineRequest request)
        => _requests.Add(request);

    internal void SetSource(string source)
    {
        for (int i = 0; i < _requests.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_requests[i].Source))
                _requests[i] = _requests[i] with { Source = source };
        }
    }

    public void Clear()
        => _requests.Clear();
}

public interface IPipelineSource
{
    string Name => GetType().Name;
    uint Version => 0;
    void Collect(PipelineCollector collector);
}
