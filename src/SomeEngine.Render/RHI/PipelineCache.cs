using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal sealed class PipelineCache : IDisposable
{
    private const int LocalSize = 256;

    private readonly IDevice _device;
    private readonly Backend _backend;
    private readonly string _backendName;
    private readonly PipelineTable _table = new();
    private readonly LocalCache _local = new();
    private readonly ShaderModules _modules;
    private readonly System.Threading.Lock _ticketGate = new();
    private readonly System.Threading.Lock _queueGate = new();
    private readonly System.Threading.Lock _pendingGate = new();
    private readonly Dictionary<PipelineTicket, PipelineTicketState> _tickets = [];
    private readonly Dictionary<PipelineHandle, PipelineStateEntry> _byHandle = [];
    private readonly Queue<PipelineStateEntry> _queue = [];
    private readonly List<PipelineStateEntry> _pending = [];
    private readonly ICacheDevice? _cacheDevice;
    private PipelineCacheHandle _nativeCache;
    private uint _nextTicket = 1;
    private bool _loadedCache;
    private bool _queuedAny;
    private bool _disposed;

    public PipelineIssueSink? IssueSink { get; set; }

    public PipelineCache(IDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _backend = device.AdapterInfo.Backend;
        _backendName = BackendName(_backend);
        _cacheDevice = device.Get<ICacheDevice>();
        _nativeCache = CreateCache(ReadOnlyMemory<byte>.Empty);
        _loadedCache = true;
        _modules = new ShaderModules(device, _backend, _backendName);
    }

    public PipelineTicket QueueCompute(ComputeState state, string source = "")
    {
        ThrowIfDisposed();
        state.Require();
        var key = PipelineStateKey.Compute(state, _backend);
        return Queue(key, state.Name, () => CreateCompute(state), state.Name, PipelineNeed.Required, source);
    }

    public PipelineTicket QueueCompute(
        Shader shader,
        ShaderBindingTable layout,
        string entryPoint,
        string name,
        MaterialState? material = null,
        string source = "")
        => QueueCompute(new ComputeState
        {
            Name = name,
            Shader = shader,
            EntryPoint = entryPoint,
            Layout = layout.PipelineLayout,
            Bindings = layout.Key,
            Material = material ?? MaterialState.Default,
        }, source);

    public PipelineTicket QueueGraphics(GraphicsState state, string source = "")
    {
        ThrowIfDisposed();
        state.Require();
        state = state.Snapshot();
        var key = PipelineStateKey.Graphics(state, _backend);
        return Queue(key, state.Name, () => CreateGraphics(state), state.Name, PipelineNeed.Required, source);
    }

    public void ProcessQueue()
    {
        ThrowIfDisposed();

        while (TakeQueued(out PipelineStateEntry? entry))
            WarmupEntry(entry);
    }

    public PipelineWarmup Warmup(ReadOnlySpan<PipelineTicket> tickets, int budget)
    {
        ThrowIfDisposed();
        if (budget < 0)
            throw new ArgumentOutOfRangeException(nameof(budget));

        int processed = WarmupTickets(tickets, budget);
        PipelineWarmup report = Inspect(tickets, processed, budget);
        RecordIssues(report.Issues);
        return report;
    }

    public PipelineWarmup WaitRequired(ReadOnlySpan<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        int processed = WarmupTickets(tickets, int.MaxValue, PipelineNeed.Required);
        PipelineWarmup report = Inspect(tickets, processed, budget: -1);
        RecordIssues(report.Issues);
        if (report.RequiredReady)
            return report;

        throw WarmupError(tickets, report);
    }

    public PipelineWarmup Inspect(ReadOnlySpan<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        return Inspect(tickets, processed: 0, budget: -1);
    }

    internal void RecordIssues(IReadOnlyList<PipelineIssue> issues)
    {
        for (int i = 0; i < issues.Count; i++)
            RecordIssue(issues[i]);
    }

    private void RecordIssue(PipelineIssue issue)
    {
        Profiler.PipelineIssue(
            ResultName(issue.Result),
            NeedName(issue.Need),
            issue.Source,
            issue.Site,
            issue.Owner);
        IssueSink?.Add(issue);
    }

    public PipelineTicket[] Collect(PipelineCollector collector)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(collector);
        var tickets = new PipelineTicket[collector.Count];
        int collected = 0;
        try
        {
            for (int i = 0; i < collector.Count; i++)
            {
                PipelineRequest request = collector[i];
                if (request.Kind == PipelineKind.Compute)
                {
                    ComputeState compute = request.Compute
                        ?? throw new InvalidOperationException("Pipeline compute request has no state.");
                    compute.Require();
                    tickets[i] = Queue(
                        PipelineStateKey.Compute(compute, _backend),
                        compute.Name,
                        () => CreateCompute(compute),
                        request.Owner,
                        request.Need,
                        request.Source);
                    collected++;
                    continue;
                }

                GraphicsState graphics = request.Graphics
                    ?? throw new InvalidOperationException("Pipeline graphics request has no state.");
                graphics.Require();
                graphics = graphics.Snapshot();
                tickets[i] = Queue(
                        PipelineStateKey.Graphics(graphics, _backend),
                        graphics.Name,
                        () => CreateGraphics(graphics),
                        request.Owner,
                        request.Need,
                        request.Source);
                collected++;
            }

            return tickets;
        }
        catch
        {
            ReleaseCollected(tickets.AsSpan(0, collected));
            throw;
        }
    }

    public PipelineTicket[] Collect(IPipelineSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        var collector = new PipelineCollector();
        source.Collect(collector);
        collector.SetSource(source.Name);
        return Collect(collector);
    }

    public PipelineLease Lease(PipelineCollector collector)
    {
        ThrowIfDisposed();
        return new PipelineLease(this, Collect(collector));
    }

    public PipelineLease Lease(IPipelineSource source)
    {
        ThrowIfDisposed();
        return new PipelineLease(this, Collect(source));
    }

    private void ReleaseCollected(Span<PipelineTicket> tickets)
    {
        for (int i = 0; i < tickets.Length; i++)
            Release(ref tickets[i]);
    }

    private PipelineWarmup Inspect(ReadOnlySpan<PipelineTicket> tickets, int processed, int budget)
    {
        int ready = 0;
        int queued = 0;
        int failed = 0;
        int requiredQueued = 0;
        int optionalQueued = 0;
        int requiredFailed = 0;
        int optionalFailed = 0;
        List<PipelineIssue>? issues = null;

        for (int i = 0; i < tickets.Length; i++)
        {
            if (!TryTicket(tickets[i], out PipelineTicketState? ticketState))
            {
                failed++;
                requiredFailed++;
                AddIssue(
                    ref issues,
                    new PipelineIssue(
                        tickets[i].Id,
                        "Unknown",
                        "Unknown",
                        PipelineNeed.Required,
                        PipelineStatus.Failed,
                        "Ticket is not valid.",
                        Result: PipelineIssueResult.Untracked));
                continue;
            }

            PipelineStateEntry current = ticketState.Entry;
            PipelineStatus status;
            Exception? error;
            lock (current.Gate)
            {
                status = current.Status;
                error = current.Error;
            }

            if (status == PipelineStatus.Ready)
            {
                ready++;
                continue;
            }

            if (status == PipelineStatus.Failed)
            {
                failed++;
                if (ticketState.Need == PipelineNeed.Optional)
                    optionalFailed++;
                else
                    requiredFailed++;
                AddIssue(ref issues, IssueFor(tickets[i], ticketState, current, status, error));
                continue;
            }

            queued++;
            if (ticketState.Need == PipelineNeed.Optional)
                optionalQueued++;
            else
                requiredQueued++;
            AddIssue(ref issues, IssueFor(tickets[i], ticketState, current, status, error));
        }

        return new PipelineWarmup(
            tickets.Length,
            processed,
            ready,
            queued,
            failed,
            requiredQueued,
            optionalQueued,
            requiredFailed,
            optionalFailed,
            BudgetLimited(budget, processed, queued),
            issues?.ToArray() ?? Array.Empty<PipelineIssue>());
    }

    private static bool BudgetLimited(int budget, int processed, int queued)
        => budget >= 0
            && budget < int.MaxValue
            && processed >= budget
            && queued > 0;

    private static void AddIssue(ref List<PipelineIssue>? issues, PipelineIssue issue)
    {
        issues ??= [];
        issues.Add(issue);
    }

    private static PipelineIssue IssueFor(
        PipelineTicket ticket,
        PipelineTicketState state,
        PipelineStateEntry entry,
        PipelineStatus status,
        Exception? error)
        => IssueFor(ticket, state, entry, status, error, state.Need, site: string.Empty);

    private static PipelineIssue IssueFor(
        PipelineTicket ticket,
        PipelineTicketState state,
        PipelineStateEntry entry,
        PipelineStatus status,
        Exception? error,
        PipelineNeed need,
        string site)
    {
        string owner = string.IsNullOrWhiteSpace(state.Owner) ? entry.Name : state.Owner;
        return new PipelineIssue(
            ticket.Id,
            entry.Name,
            owner,
            need,
            status,
            error?.Message,
            site,
            Source: state.Source,
            Result: ResultFor(status, site));
    }

    private static PipelineIssueResult ResultFor(PipelineStatus status, string site)
        => status switch
        {
            PipelineStatus.Failed => PipelineIssueResult.Failed,
            PipelineStatus.Queued or PipelineStatus.Creating => string.IsNullOrWhiteSpace(site)
                ? PipelineIssueResult.Missed
                : PipelineIssueResult.TooLate,
            _ => PipelineIssueResult.Missed,
        };

    private InvalidOperationException WarmupError(ReadOnlySpan<PipelineTicket> tickets, PipelineWarmup report)
    {
        for (int i = 0; i < tickets.Length; i++)
        {
            if (!TryTicket(tickets[i], out PipelineTicketState? ticketState))
            {
                return new InvalidOperationException(
                    $"Pipeline warmup required ticket {tickets[i].Id} is not valid; requested={report.Requested}, ready={report.Ready}, queued={report.Queued}, failed={report.Failed}.");
            }

            if (ticketState.Need != PipelineNeed.Required)
                continue;

            PipelineStateEntry entry = ticketState.Entry;
            PipelineStatus status;
            Exception? error;
            lock (entry.Gate)
            {
                status = entry.Status;
                error = entry.Error;
            }

            if (status == PipelineStatus.Ready)
                continue;

            string owner = string.IsNullOrWhiteSpace(ticketState.Owner) ? entry.Name : ticketState.Owner;
            string message = error == null
                ? $"Pipeline warmup required '{entry.Name}' for '{owner}' is {status}; requested={report.Requested}, ready={report.Ready}, queued={report.Queued}, failed={report.Failed}."
                : $"Pipeline warmup required '{entry.Name}' for '{owner}' failed: {error.Message}; requested={report.Requested}, ready={report.Ready}, queued={report.Queued}, failed={report.Failed}.";
            return new InvalidOperationException(message, error);
        }

        return new InvalidOperationException(
            $"Pipeline warmup required pipelines are not ready; requested={report.Requested}, ready={report.Ready}, queued={report.Queued}, failed={report.Failed}.");
    }

    public PipelineStatus GetStatus(PipelineTicket ticket)
    {
        if (!TryTicket(ticket, out PipelineTicketState? ticketState))
            return PipelineStatus.Failed;

        PipelineStateEntry current = ticketState.Entry;
        lock (current.Gate)
            return current.Status;
    }

    public PipelineHandle GetPipeline(PipelineTicket ticket, PipelineNeed need)
        => GetPipeline(ticket, need, site: string.Empty);

    public PipelineHandle GetPipeline(PipelineTicket ticket, PipelineNeed need, string site)
    {
        ThrowIfDisposed();
        if (!TryTicket(ticket, out PipelineTicketState? ticketState))
        {
            ReportInvalid(need);
            if (need != PipelineNeed.Optional || IssueSink != null || Profiler.NeedsCounters)
            {
                RecordIssue(new PipelineIssue(
                    ticket.Id,
                    "Unknown",
                    "Unknown",
                    need,
                    PipelineStatus.Failed,
                    "Ticket is not valid.",
                    site,
                    Result: PipelineIssueResult.Untracked));
            }

            if (need == PipelineNeed.Optional)
                return default;

            throw new InvalidOperationException($"Pipeline state ticket {ticket.Id} is not valid.");
        }

        PipelineStateEntry current = ticketState.Entry;
        PipelineStatus status;
        PipelineHandle handle;
        Exception? error;
        bool destroyed;
        string name;
        lock (current.Gate)
        {
            status = current.Status;
            handle = current.Handle;
            error = current.Error;
            destroyed = current.Destroyed;
            name = current.Name;
        }

        if (status == PipelineStatus.Ready && handle.IsValid && !destroyed)
        {
            ReportHit(need);
            return handle;
        }

        if (need == PipelineNeed.Optional)
        {
            ReportStatus(status, need);
            if (IssueSink != null || Profiler.NeedsCounters)
                RecordIssue(IssueFor(ticket, ticketState, current, status, error, need, site));
            return default;
        }

        ReportStatus(status, need);
        string owner = string.IsNullOrWhiteSpace(ticketState.Owner) ? name : ticketState.Owner;
        RecordIssue(IssueFor(ticket, ticketState, current, status, error, need, site));
        string message = status == PipelineStatus.Failed && error != null
            ? $"Pipeline state '{name}' for '{owner}' failed as {ticketState.Need}: {error.Message}"
            : $"Pipeline state '{name}' for '{owner}' is {status} as {ticketState.Need}.";
        throw new InvalidOperationException(message, error);
    }

    public PipelineHandle GetPipeline(PipelineTicket ticket)
    {
        ThrowIfDisposed();
        if (!TryTicket(ticket, out PipelineTicketState? ticketState))
            return GetPipeline(ticket, PipelineNeed.Required);

        return GetPipeline(ticket, ticketState.Need, site: string.Empty);
    }

    public Exception? GetError(PipelineTicket ticket)
    {
        if (!TryTicket(ticket, out PipelineTicketState? ticketState))
            return null;

        PipelineStateEntry current = ticketState.Entry;
        lock (current.Gate)
            return current.Error;
    }

    public void Release(ref PipelineTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        PipelineTicket current = ticket;
        ticket = default;
        if (_disposed)
            return;

        PipelineStateEntry? entry;
        lock (_ticketGate)
        {
            if (!_tickets.Remove(current, out PipelineTicketState? ticketState))
                return;

            entry = ticketState.Entry;
        }

        bool pend;
        bool drop;
        lock (entry.Gate)
        {
            if (entry.OwnerCount > 0)
                entry.OwnerCount--;
            if (entry.OwnerCount != 0)
                return;

            pend = entry.Status == PipelineStatus.Ready && entry.Handle.IsValid;
            drop = !pend;
            if (drop)
                entry.Destroyed = true;
        }

        if (pend)
            Pend(entry);
        else if (drop)
            DropEntry(entry);
    }

    public void Track(ReadOnlySpan<PipelineHandle> handles, ulong fence)
    {
        if (_disposed)
            return;

        for (int i = 0; i < handles.Length; i++)
            Track(handles[i], fence);
    }

    public void Track(PipelineHandle handle, ulong fence)
    {
        if (_disposed || !handle.IsValid)
            return;

        PipelineStateEntry? entry;
        lock (_ticketGate)
            _byHandle.TryGetValue(handle, out entry);
        if (entry == null)
            return;

        lock (entry.Gate)
        {
            if (entry.Destroyed)
                return;
            if (entry.HasFence && entry.LastFence >= fence)
                return;

            entry.LastFence = fence;
            entry.HasFence = true;
        }
    }

    public void Retire(ulong fence)
    {
        if (_disposed)
            return;

        while (TakeRetired(fence, out PipelineStateEntry? entry))
            RetireEntry(entry);
    }

    public void LoadCache(ReadOnlyMemory<byte> data)
    {
        ThrowIfDisposed();
        if (_queuedAny)
            throw new InvalidOperationException("Pipeline cache data must be loaded before queueing pipeline states.");
        if (_nativeCache.IsValid)
            _device.Destroy(_nativeCache);

        _nativeCache = CreateCache(data);
        _loadedCache = true;
    }

    public byte[] SaveCache()
    {
        ThrowIfDisposed();
        return _cacheDevice != null && _nativeCache.IsValid
            ? _cacheDevice.GetPipelineData(_nativeCache)
            : [];
    }

    public void ReleaseIdle(ref PipelineTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        _device.WaitIdle();
        Release(ref ticket);
        Retire(ulong.MaxValue);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        PipelineStateEntry[] entries = _table.Values();
        _table.Clear();
        _local.Clear();
        lock (_ticketGate)
        {
            _tickets.Clear();
            _byHandle.Clear();
        }

        lock (_queueGate)
            _queue.Clear();
        lock (_pendingGate)
            _pending.Clear();

        for (int i = 0; i < entries.Length; i++)
            Destroy(entries[i]);

        if (_nativeCache.IsValid)
        {
            _device.Destroy(_nativeCache);
            _nativeCache = default;
        }
    }

    private PipelineTicket Queue(
        PipelineStateKey key,
        string name,
        Func<PipelineCreate> create,
        string owner,
        PipelineNeed need,
        string source)
    {
        _queuedAny = true;
        if (!_loadedCache)
            throw new InvalidOperationException("Pipeline cache data must be loaded before queueing pipeline states.");

        while (true)
        {
            if (_local.TryGet(key, out PipelineStateEntry? local))
            {
                if (TryAddTicket(local, owner, need, source, out PipelineTicket localTicket))
                {
                    ReportLocalHit(need);
                    return localTicket;
                }

                _local.Remove(local);
            }

            PipelineStateEntry entry = _table.GetOrAdd(
                key,
                static (state, k) =>
                {
                    var created = new PipelineStateEntry(k, state.Name, state.Create);
                    state.Cache.Enqueue(created);
                    return created;
                },
                new QueueCreate(this, name, create),
                out bool created);

            if (TryAddTicket(entry, owner, need, source, out PipelineTicket ticket))
            {
                _local.Store(entry);
                if (created)
                    ReportQueued(need);
                else
                    ReportShared(need);
                return ticket;
            }

            _table.Remove(entry.Key, entry);
            _local.Remove(entry);
        }
    }

    private bool TryAddTicket(
        PipelineStateEntry entry,
        string owner,
        PipelineNeed need,
        string source,
        out PipelineTicket ticket)
    {
        ticket = default;
        lock (_ticketGate)
        {
            ticket = new PipelineTicket(_nextTicket++, 1);
            if (_nextTicket == 0)
                _nextTicket = 1;

            PipelineHandle handle;
            bool ready;
            lock (entry.Gate)
            {
                if (entry.Destroyed)
                    return false;

                if (entry.Pending)
                {
                    RemovePending(entry);
                    entry.Pending = false;
                }

                entry.OwnerCount++;
                handle = entry.Handle;
                ready = entry.Status == PipelineStatus.Ready && handle.IsValid;
            }

            if (ready)
                _byHandle[handle] = entry;
            _tickets.Add(ticket, new PipelineTicketState(entry, owner, need, source));
        }

        return true;
    }

    private bool TryTicket(PipelineTicket ticket, [NotNullWhen(true)] out PipelineTicketState? state)
    {
        state = null;
        if (!ticket.IsValid || _disposed)
            return false;

        lock (_ticketGate)
            _tickets.TryGetValue(ticket, out state);

        return state != null;
    }

    private int WarmupTickets(ReadOnlySpan<PipelineTicket> tickets, int budget)
        => WarmupTickets(tickets, budget, need: null);

    private int WarmupTickets(ReadOnlySpan<PipelineTicket> tickets, int budget, PipelineNeed? need)
    {
        if (tickets.IsEmpty || budget == 0)
            return 0;

        int processed = 0;
        for (int i = 0; i < tickets.Length && processed < budget; i++)
        {
            if (!TryTicket(tickets[i], out PipelineTicketState? state))
                continue;
            if (need.HasValue && state.Need != need.Value)
                continue;

            if (WarmupEntry(state.Entry))
                processed++;
        }

        return processed;
    }

    private bool WarmupEntry(PipelineStateEntry current)
    {
        lock (current.Gate)
        {
            if (current.Status != PipelineStatus.Queued || current.Destroyed)
            {
                current.Queued = false;
                return false;
            }

            current.Status = PipelineStatus.Creating;
            current.Queued = false;
        }

        PipelineCreate created = default;
        Exception? error = null;
        try
        {
            created = current.Create();
            if (!created.Handle.IsValid)
                throw new InvalidOperationException($"Pipeline state factory returned an invalid handle for '{current.Name}'.");
        }
        catch (Exception ex)
        {
            error = ex;
        }

        bool ready = false;
        bool failed = false;
        lock (current.Gate)
        {
            if (current.Destroyed)
            {
                if (created.Handle.IsValid)
                    _device.Destroy(created.Handle);
                _modules.Release(created.Modules);
                return true;
            }

            if (error != null)
            {
                if (created.Handle.IsValid)
                    _device.Destroy(created.Handle);
                _modules.Release(created.Modules);
                current.Error = error;
                current.Status = PipelineStatus.Failed;
                failed = true;
            }
            else
            {
                current.Handle = created.Handle;
                current.Modules = created.Modules;
                current.Status = PipelineStatus.Ready;
                lock (_ticketGate)
                    _byHandle[created.Handle] = current;
                _local.Store(current);
                ready = true;
            }
        }

        if (ready)
            Profiler.PipelineReady();
        else if (failed)
            Profiler.PipelineFailed();
        return true;
    }

    private static void ReportInvalid(PipelineNeed need)
    {
        Profiler.PipelineInvalid(NeedName(need));
    }

    private static void ReportHit(PipelineNeed need)
    {
        Profiler.PipelineHit(NeedName(need));
    }

    private static void ReportLocalHit(PipelineNeed need)
    {
        Profiler.PipelineLocalHit(NeedName(need));
    }

    private static void ReportQueued(PipelineNeed need)
    {
        Profiler.PipelineQueued(NeedName(need));
    }

    private static void ReportShared(PipelineNeed need)
    {
        Profiler.PipelineShared(NeedName(need));
    }

    private static void ReportStatus(PipelineStatus status, PipelineNeed need)
    {
        Profiler.PipelineStatus(StatusName(status), NeedName(need));
    }

    private static string NeedName(PipelineNeed need)
        => need switch
        {
            PipelineNeed.Required => "Required",
            PipelineNeed.Optional => "Optional",
            _ => "Any",
        };

    private static string StatusName(PipelineStatus status)
        => status switch
        {
            PipelineStatus.Queued => "Queued",
            PipelineStatus.Creating => "Creating",
            PipelineStatus.Ready => "Ready",
            PipelineStatus.Failed => "Failed",
            _ => "Invalid",
        };

    private static string ResultName(PipelineIssueResult result)
        => result switch
        {
            PipelineIssueResult.Missed => "Missed",
            PipelineIssueResult.TooLate => "TooLate",
            PipelineIssueResult.Failed => "Failed",
            PipelineIssueResult.Untracked => "Untracked",
            _ => "Invalid",
        };

    private PipelineCreate CreateCompute(ComputeState state)
    {
        ShaderModuleHandle shader = _modules.Acquire(state.Shader, state.EntryPoint);
        try
        {
            var handle = _device.CreateComputePipeline(new ComputePipelineDesc
            {
                Name = state.Name,
                Layout = state.Layout,
                ComputeShader = shader,
                PipelineCache = _nativeCache,
            });
            return new PipelineCreate(handle, [shader]);
        }
        catch
        {
            _modules.Release(shader);
            throw;
        }
    }

    private PipelineCreate CreateGraphics(GraphicsState state)
    {
        ShaderModuleHandle vertex = _modules.Acquire(state.VertexShader, state.VertexEntry);
        ShaderModuleHandle pixel = _modules.Acquire(state.PixelShader, state.PixelEntry);
        ShaderModuleHandle hull = _modules.AcquireOptional(state.HullShader, state.HullEntry);
        ShaderModuleHandle domain = _modules.AcquireOptional(state.DomainShader, state.DomainEntry);
        ShaderModuleHandle geometry = _modules.AcquireOptional(state.GeometryShader, state.GeometryEntry);

        try
        {
            PipelineHandle handle = _device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                Name = state.Name,
                Layout = state.Layout,
                VertexShader = vertex,
                PixelShader = pixel,
                HullShader = hull,
                DomainShader = domain,
                GeometryShader = geometry,
                Topology = state.Topology,
                PatchControlPoints = state.PatchControlPoints,
                VertexBuffers = state.VertexBuffers,
                VertexAttributes = state.VertexAttributes,
                ColorFormats = state.ColorFormats,
                DepthStencilFormat = state.DepthStencilFormat,
                SampleCount = state.SampleCount,
                Multisample = state.Multisample,
                Rasterizer = state.Rasterizer,
                DepthStencil = state.DepthStencil,
                Blend = state.Blend,
                PipelineCache = _nativeCache,
            });
            return new PipelineCreate(handle, [vertex, pixel, hull, domain, geometry]);
        }
        catch
        {
            _modules.Release(vertex);
            _modules.Release(pixel);
            _modules.Release(hull);
            _modules.Release(domain);
            _modules.Release(geometry);
            throw;
        }
    }

    private void Enqueue(PipelineStateEntry entry)
    {
        lock (_queueGate)
            _queue.Enqueue(entry);
    }

    private bool TakeQueued([NotNullWhen(true)] out PipelineStateEntry? entry)
    {
        while (true)
        {
            PipelineStateEntry current;
            lock (_queueGate)
            {
                if (_queue.Count == 0)
                {
                    entry = null;
                    return false;
                }

                current = _queue.Dequeue();
            }

            lock (current.Gate)
            {
                if (!current.Queued)
                    continue;
            }

            entry = current;
            return true;
        }
    }

    private void Pend(PipelineStateEntry entry)
    {
        _local.Remove(entry);
        lock (_ticketGate)
            _byHandle.Remove(entry.Handle);

        lock (entry.Gate)
        {
            if (entry.Destroyed || entry.Pending)
                return;
            entry.Pending = true;

            lock (_pendingGate)
            {
                entry.PendingIndex = _pending.Count;
                _pending.Add(entry);
            }
        }

        if (!entry.HasFence)
            Retire(0);
    }

    private void DropEntry(PipelineStateEntry entry)
    {
        lock (entry.Gate)
            entry.Queued = false;
        _table.Remove(entry.Key, entry);
        _local.Remove(entry);
    }

    private bool RetireEntry(PipelineStateEntry entry)
    {
        lock (entry.Gate)
        {
            if (entry.Destroyed || entry.OwnerCount != 0 || !entry.Pending)
                return false;

            entry.Pending = false;
            entry.Destroyed = true;
        }

        _table.Remove(entry.Key, entry);
        _local.Remove(entry);
        lock (_ticketGate)
            _byHandle.Remove(entry.Handle);
        Destroy(entry);
        return true;
    }

    private bool TakeRetired(ulong fence, [NotNullWhen(true)] out PipelineStateEntry? entry)
    {
        lock (_pendingGate)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                entry = _pending[i];
                if (entry.HasFence && entry.LastFence > fence)
                    continue;

                RemovePendingAt(i);
                return true;
            }
        }

        entry = null;
        return false;
    }

    private void RemovePending(PipelineStateEntry entry)
    {
        lock (_pendingGate)
        {
            int index = entry.PendingIndex;
            if ((uint)index >= (uint)_pending.Count || !ReferenceEquals(_pending[index], entry))
                return;

            RemovePendingAt(index);
        }
    }

    private void RemovePendingAt(int index)
    {
        PipelineStateEntry entry = _pending[index];
        int last = _pending.Count - 1;
        if (index != last)
        {
            PipelineStateEntry moved = _pending[last];
            _pending[index] = moved;
            moved.PendingIndex = index;
        }

        _pending.RemoveAt(last);
        entry.PendingIndex = -1;
    }

    private void Destroy(PipelineStateEntry entry)
    {
        PipelineHandle handle;
        ShaderModuleHandle[] modules;
        lock (entry.Gate)
        {
            if (entry.Destroyed && !entry.Handle.IsValid && entry.Modules.Length == 0)
                return;
            entry.Destroyed = true;
            entry.Pending = false;
            handle = entry.Handle;
            entry.Handle = default;
            modules = entry.Modules;
            entry.Modules = [];
        }

        if (handle.IsValid)
            _device.Destroy(handle);
        _modules.Release(modules);
    }

    private PipelineCacheHandle CreateCache(ReadOnlyMemory<byte> data)
        => _cacheDevice != null && _device.Features.PipelineCache
            ? _cacheDevice.CreatePipelineCache(new PipelineCacheDesc
            {
                Name = "Render PipelineState Cache",
                InitialData = data,
            })
            : default;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PipelineCache));
    }

    internal static string BackendName(Backend backend)
        => backend switch
        {
            Backend.D3D12 or Backend.Null => "dxil",
            _ => "spirv",
        };

    private readonly record struct PipelineCreate(
        PipelineHandle Handle,
        ShaderModuleHandle[] Modules);

    private readonly record struct QueueCreate(
        PipelineCache Cache,
        string Name,
        Func<PipelineCreate> Create);

    private sealed class PipelineTicketState(PipelineStateEntry entry, string owner, PipelineNeed need, string source)
    {
        public PipelineStateEntry Entry { get; } = entry;
        public string Owner { get; } = owner;
        public PipelineNeed Need { get; } = need;
        public string Source { get; } = source;
    }

    private sealed class PipelineStateEntry(
        PipelineStateKey key,
        string name,
        Func<PipelineCreate> create)
    {
        public readonly System.Threading.Lock Gate = new();
        public readonly PipelineStateKey Key = key;
        public readonly string Name = name;
        public readonly Func<PipelineCreate> Create = create;
        public PipelineHandle Handle;
        public ShaderModuleHandle[] Modules = [];
        public PipelineStatus Status = PipelineStatus.Queued;
        public Exception? Error;
        public ulong LastFence;
        public int OwnerCount;
        public int PendingIndex = -1;
        public bool HasFence;
        public bool Queued = true;
        public bool Pending;
        public bool Destroyed;
    }

    private sealed class PipelineTable
    {
        private const int ShardCount = 64;
        private readonly Shard[] _shards = Create();

        public PipelineStateEntry GetOrAdd<TState>(
            PipelineStateKey key,
            Func<TState, PipelineStateKey, PipelineStateEntry> create,
            TState state,
            out bool created)
        {
            Shard shard = Pick(key);
            lock (shard.Gate)
            {
                if (shard.Map.TryGetValue(key, out PipelineStateEntry? entry))
                {
                    created = false;
                    return entry;
                }

                entry = create(state, key);
                shard.Map.Add(key, entry);
                created = true;
                return entry;
            }
        }

        public bool Remove(PipelineStateKey key, PipelineStateEntry entry)
        {
            Shard shard = Pick(key);
            lock (shard.Gate)
            {
                if (!shard.Map.TryGetValue(key, out PipelineStateEntry? current)
                    || !ReferenceEquals(current, entry))
                {
                    return false;
                }

                shard.Map.Remove(key);
                return true;
            }
        }

        public PipelineStateEntry[] Values()
        {
            var values = new List<PipelineStateEntry>();
            for (int i = 0; i < _shards.Length; i++)
            {
                lock (_shards[i].Gate)
                    values.AddRange(_shards[i].Map.Values);
            }

            return values.ToArray();
        }

        public void Clear()
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                lock (_shards[i].Gate)
                    _shards[i].Map.Clear();
            }
        }

        private Shard Pick(PipelineStateKey key)
            => _shards[(key.GetHashCode() & 0x7fffffff) % ShardCount];

        private static Shard[] Create()
        {
            var shards = new Shard[ShardCount];
            for (int i = 0; i < shards.Length; i++)
                shards[i] = new Shard();
            return shards;
        }

        private sealed class Shard
        {
            public readonly System.Threading.Lock Gate = new();
            public readonly Dictionary<PipelineStateKey, PipelineStateEntry> Map = [];
        }
    }

    private sealed class LocalCache
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly Slot[] _slots = new Slot[LocalSize];

        public bool TryGet(PipelineStateKey key, [NotNullWhen(true)] out PipelineStateEntry? entry)
        {
            lock (_gate)
            {
                Slot slot = _slots[Index(key)];
                if (slot.Entry != null && slot.Key.Equals(key))
                {
                    entry = slot.Entry;
                    return true;
                }
            }

            entry = null;
            return false;
        }

        public void Store(PipelineStateEntry entry)
        {
            lock (_gate)
                _slots[Index(entry.Key)] = new Slot(entry.Key, entry);
        }

        public void Remove(PipelineStateEntry entry)
        {
            lock (_gate)
            {
                int index = Index(entry.Key);
                if (ReferenceEquals(_slots[index].Entry, entry))
                    _slots[index] = default;
            }
        }

        public void Clear()
        {
            lock (_gate)
                Array.Clear(_slots, 0, _slots.Length);
        }

        private static int Index(PipelineStateKey key)
            => key.GetHashCode() & (LocalSize - 1);

        private readonly record struct Slot(PipelineStateKey Key, PipelineStateEntry? Entry);
    }
}

public sealed record ComputeState
{
    public string Name { get; init; } = string.Empty;
    public Shader Shader { get; init; } = null!;
    public string EntryPoint { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public BindingKey Bindings { get; init; } = BindingKey.Empty;
    public MaterialState Material { get; init; } = MaterialState.Default;

    public void Require()
    {
        ArgumentNullException.ThrowIfNull(Shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(EntryPoint);
        if (!Layout.IsValid)
            throw new ArgumentException("Compute pipeline state requires a valid layout.", nameof(Layout));
    }
}

public sealed record GraphicsState
{
    public string Name { get; init; } = string.Empty;
    public Shader VertexShader { get; init; } = null!;
    public string VertexEntry { get; init; } = string.Empty;
    public Shader PixelShader { get; init; } = null!;
    public string PixelEntry { get; init; } = string.Empty;
    public Shader? HullShader { get; init; }
    public string HullEntry { get; init; } = string.Empty;
    public Shader? DomainShader { get; init; }
    public string DomainEntry { get; init; } = string.Empty;
    public Shader? GeometryShader { get; init; }
    public string GeometryEntry { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public BindingKey Bindings { get; init; } = BindingKey.Empty;
    public MaterialState Material { get; init; } = MaterialState.Default;
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public uint PatchControlPoints { get; init; }
    public IReadOnlyList<VertexLayoutDesc> VertexBuffers { get; init; } = Array.Empty<VertexLayoutDesc>();
    public IReadOnlyList<VertexAttributeDesc> VertexAttributes { get; init; } = Array.Empty<VertexAttributeDesc>();
    public IReadOnlyList<Format> ColorFormats { get; init; } = Array.Empty<Format>();
    public Format DepthStencilFormat { get; init; } = Format.Unknown;
    public uint SampleCount { get; init; } = 1;
    public MultisampleDesc Multisample { get; init; } = new();
    public RasterizerDesc Rasterizer { get; init; } = new();
    public DepthStencilDesc DepthStencil { get; init; } = new();
    public BlendDesc Blend { get; init; } = new();

    public void Require()
    {
        ArgumentNullException.ThrowIfNull(VertexShader);
        ArgumentNullException.ThrowIfNull(PixelShader);
        ArgumentNullException.ThrowIfNull(Multisample);
        ArgumentNullException.ThrowIfNull(Rasterizer);
        ArgumentNullException.ThrowIfNull(DepthStencil);
        ArgumentNullException.ThrowIfNull(Blend);
        ArgumentException.ThrowIfNullOrWhiteSpace(VertexEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(PixelEntry);
        if (!Layout.IsValid)
            throw new ArgumentException("Graphics pipeline state requires a valid layout.", nameof(Layout));
        if ((ColorFormats == null || ColorFormats.Count == 0) && DepthStencilFormat == Format.Unknown)
            throw new ArgumentException("Graphics pipeline state requires at least one output format.");
    }

    public GraphicsState Snapshot()
        => this with
        {
            VertexBuffers = VertexBuffers?.ToArray() ?? Array.Empty<VertexLayoutDesc>(),
            VertexAttributes = VertexAttributes?.ToArray() ?? Array.Empty<VertexAttributeDesc>(),
            ColorFormats = ColorFormats?.ToArray() ?? Array.Empty<Format>(),
            Blend = Blend with
            {
                Targets = Blend.Targets?.ToArray() ?? Array.Empty<BlendTargetDesc>(),
            },
        };
}

public readonly record struct PipelineTicket
{
    internal PipelineTicket(uint id, uint generation)
    {
        Id = id;
        Generation = generation;
    }

    public uint Id { get; }
    public uint Generation { get; }
    public bool IsValid => Id != 0 && Generation != 0;
}

public enum PipelineStatus
{
    Queued,
    Creating,
    Ready,
    Failed,
}

public enum PipelineNeed
{
    Required,
    Optional,
}

public enum PipelineIssueResult
{
    Missed,
    TooLate,
    Failed,
    Untracked,
}

internal readonly struct PipelineStateKey : IEquatable<PipelineStateKey>
{
    private readonly string _kind;
    private readonly ulong _hashA;
    private readonly ulong _hashB;
    private readonly byte[] _bytes;
    private readonly int _hash;

    internal PipelineStateKey(string kind, byte[] bytes)
    {
        _kind = kind;
        _bytes = bytes;
        byte[] hash = SHA256.HashData(bytes);
        _hashA = BitConverter.ToUInt64(hash, 0);
        _hashB = BitConverter.ToUInt64(hash, 8);
        var code = new HashCode();
        code.Add(kind, StringComparer.Ordinal);
        code.Add(_hashA);
        code.Add(_hashB);
        code.Add(bytes.Length);
        _hash = code.ToHashCode();
    }

    public static PipelineStateKey Compute(ComputeState state, Backend backend)
    {
        using var builder = new PipelineStateWriter("compute");
        builder.AddBackend(backend);
        builder.AddShader(state.Shader, state.EntryPoint);
        builder.AddHandle(state.Layout.Id, state.Layout.Generation);
        state.Bindings.AddTo(builder);
        builder.AddState(state.Material);
        return builder.Build();
    }

    public static PipelineStateKey Graphics(GraphicsState state, Backend backend)
    {
        using var builder = new PipelineStateWriter("graphics");
        builder.AddBackend(backend);
        builder.AddShader(state.VertexShader, state.VertexEntry);
        builder.AddShader(state.PixelShader, state.PixelEntry);
        builder.AddOptional(state.HullShader, state.HullEntry);
        builder.AddOptional(state.DomainShader, state.DomainEntry);
        builder.AddOptional(state.GeometryShader, state.GeometryEntry);
        builder.AddHandle(state.Layout.Id, state.Layout.Generation);
        state.Bindings.AddTo(builder);
        builder.AddState(state.Material);
        builder.AddEnum(state.Topology);
        builder.AddValue(state.PatchControlPoints);
        builder.AddLayouts(state.VertexBuffers);
        builder.AddAttributes(state.VertexAttributes);
        builder.AddFormats(state.ColorFormats);
        builder.AddEnum(state.DepthStencilFormat);
        builder.AddValue(state.SampleCount);
        builder.AddMultisample(state.Multisample);
        builder.AddRaster(state.Rasterizer);
        builder.AddDepth(state.DepthStencil);
        builder.AddBlend(state.Blend);
        return builder.Build();
    }

    public bool Equals(PipelineStateKey other)
    {
        if (!string.Equals(_kind, other._kind, StringComparison.Ordinal)
            || _hashA != other._hashA
            || _hashB != other._hashB
            || (_bytes?.Length ?? 0) != (other._bytes?.Length ?? 0))
        {
            return false;
        }

        return (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);
    }

    public override bool Equals(object? obj)
        => obj is PipelineStateKey other && Equals(other);

    public override int GetHashCode()
        => _hash;
}

internal sealed class PipelineStateWriter : IDisposable
{
    private const int InitialSize = 512;

    private readonly string _kind;
    private byte[] _buffer;
    private int _length;
    private bool _built;

    public PipelineStateWriter(string kind)
    {
        _kind = kind;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialSize);
        AddString(kind);
    }

    public void AddBackend(Backend backend)
        => AddEnum(backend);

    public void AddShader(Shader shader, string entry)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        AddString(ShaderKeys.Name(shader));
        AddString(entry);
        AddString(ShaderKeys.Version(shader, entry));
    }

    public void AddOptional(Shader? shader, string entry)
    {
        AddValue(shader != null && !string.IsNullOrWhiteSpace(entry));
        if (shader != null && !string.IsNullOrWhiteSpace(entry))
            AddShader(shader, entry);
    }

    public void AddHandle(uint id, uint generation)
    {
        AddValue(id);
        AddValue(generation);
    }

    public void AddState(MaterialState state)
    {
        AddEnum(state.Surface);
        AddValue(state.TwoSided);
        AddValue(state.OverlayLayer);
        AddValue(state.BoundsExpansion);
        AddValue(state.StencilRef);
        AddEnum(state.StencilCompare);
        AddEnum(state.StencilPass);
    }

    public void AddLayouts(IReadOnlyList<VertexLayoutDesc>? values)
    {
        AddCount(values);
        if (values == null)
            return;

        for (int i = 0; i < values.Count; i++)
        {
            VertexLayoutDesc value = values[i];
            AddValue(value.Slot);
            AddValue(value.StrideInBytes);
            AddEnum(value.InputRate);
            AddValue(value.InstanceStepRate);
        }
    }

    public void AddAttributes(IReadOnlyList<VertexAttributeDesc>? values)
    {
        AddCount(values);
        if (values == null)
            return;

        for (int i = 0; i < values.Count; i++)
        {
            VertexAttributeDesc value = values[i];
            AddValue(value.Location);
            AddValue(value.BufferSlot);
            AddEnum(value.Format);
            AddValue(value.OffsetInBytes);
        }
    }

    public void AddFormats(IReadOnlyList<Format>? values)
    {
        AddCount(values);
        if (values == null)
            return;

        for (int i = 0; i < values.Count; i++)
            AddEnum(values[i]);
    }

    public void AddMultisample(MultisampleDesc value)
    {
        AddValue(value.SampleCount);
        AddValue(value.SampleMask);
    }

    public void AddRaster(RasterizerDesc value)
    {
        AddEnum(value.CullMode);
        AddEnum(value.FillMode);
        AddValue(value.FrontCounterClockwise);
        AddValue(value.DepthBias);
        AddValue(value.DepthBiasClamp);
        AddValue(value.SlopeScaledDepthBias);
    }

    public void AddDepth(DepthStencilDesc value)
    {
        AddValue(value.DepthEnable);
        AddValue(value.DepthWriteEnable);
        AddEnum(value.DepthCompare);
    }

    public void AddBlend(BlendDesc value)
    {
        AddValue(value.AlphaToCoverageEnable);
        AddCount(value.Targets);
        for (int i = 0; i < value.Targets.Count; i++)
        {
            BlendTargetDesc target = value.Targets[i];
            AddValue(target.Enable);
            AddEnum(target.SourceColor);
            AddEnum(target.DestinationColor);
            AddEnum(target.ColorOp);
            AddEnum(target.SourceAlpha);
            AddEnum(target.DestinationAlpha);
            AddEnum(target.AlphaOp);
            AddEnum(target.WriteMask);
        }
    }

    public void AddEnum<T>(T value)
        where T : struct, Enum
        => AddValue(Convert.ToInt64(value));

    public void AddValue(bool value)
        => Reserve(1)[0] = value ? (byte)1 : (byte)0;

    public void AddValue(byte value)
        => Reserve(1)[0] = value;

    public void AddValue(int value)
        => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);

    public void AddValue(uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);

    public void AddValue(long value)
        => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);

    public void AddValue(float value)
        => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), BitConverter.SingleToInt32Bits(value));

    public PipelineStateKey Build()
    {
        ThrowIfBuilt();
        _built = true;
        byte[] bytes = _buffer.AsSpan(0, _length).ToArray();
        ReturnBuffer();
        return new PipelineStateKey(_kind, bytes);
    }

    public void Dispose()
    {
        if (_built)
            return;

        _built = true;
        ReturnBuffer();
    }

    private void AddString(string value)
    {
        value ??= string.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        AddValue(byteCount);
        Encoding.UTF8.GetBytes(value.AsSpan(), Reserve(byteCount));
    }

    private void AddCount<T>(IReadOnlyList<T>? values)
        => AddValue(values?.Count ?? 0);

    private Span<byte> Reserve(int count)
    {
        ThrowIfBuilt();
        Ensure(count);
        Span<byte> bytes = _buffer.AsSpan(_length, count);
        _length += count;
        return bytes;
    }

    private void Ensure(int count)
    {
        int required = checked(_length + count);
        if (required <= _buffer.Length)
            return;

        int size = _buffer.Length;
        do
        {
            size = checked(size * 2);
        }
        while (size < required);

        byte[] next = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _length).CopyTo(next);
        ReturnBuffer();
        _buffer = next;
    }

    private void ReturnBuffer()
    {
        if (_buffer.Length == 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Pipeline state key writer is already built.");
    }
}

internal sealed class ShaderModules
{
    private readonly IDevice _device;
    private readonly Backend _backend;
    private readonly string _backendName;
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<ShaderModuleKey, ModuleState> _byKey = [];
    private readonly Dictionary<ShaderModuleHandle, ShaderModuleKey> _byHandle = [];

    public ShaderModules(IDevice device, Backend backend, string backendName)
    {
        _device = device;
        _backend = backend;
        _backendName = backendName;
    }

    public ShaderModuleHandle Acquire(Shader shader, string entry)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        var key = ShaderModuleKey.From(shader, entry, _backendName);

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out ModuleState? state))
            {
                state.RefCount++;
                return state.Handle;
            }
        }

        ShaderVariant variant = Variant(shader, entry);
        ShaderModuleHandle handle = _device.CreateShaderModule(new ShaderModuleDesc
        {
            Name = $"{shader.Name}_{entry}",
            Backend = _backend,
            Stage = variant.Stage,
            EntryPoint = entry,
            BytecodeFormat = BytecodeFormat(_backendName),
            Bytecode = variant.Bytecode,
        });

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out ModuleState? existing))
            {
                existing.RefCount++;
                _device.Destroy(handle);
                return existing.Handle;
            }

            _byKey.Add(key, new ModuleState(handle));
            _byHandle.Add(handle, key);
            return handle;
        }
    }

    public ShaderModuleHandle AcquireOptional(Shader? shader, string entry)
        => shader != null && !string.IsNullOrWhiteSpace(entry)
            ? Acquire(shader, entry)
            : default;

    public void Release(ShaderModuleHandle handle)
    {
        if (!handle.IsValid)
            return;

        lock (_gate)
        {
            if (!_byHandle.TryGetValue(handle, out ShaderModuleKey key)
                || !_byKey.TryGetValue(key, out ModuleState? state))
            {
                return;
            }

            state.RefCount--;
            if (state.RefCount > 0)
                return;

            _byHandle.Remove(handle);
            _byKey.Remove(key);
            _device.Destroy(handle);
        }
    }

    public void Release(ReadOnlySpan<ShaderModuleHandle> handles)
    {
        for (int i = 0; i < handles.Length; i++)
            Release(handles[i]);
    }

    private ShaderVariant Variant(Shader shader, string entry)
    {
        if (!shader.TryVariant(_backendName, entry, out ShaderVariant variant))
        {
            throw new InvalidOperationException(
                $"Shader variant not found for backend {_backendName} and entry point {entry} in shader {shader.Name}.");
        }

        if (variant.Bytecode.IsEmpty)
            throw new InvalidOperationException($"Shader variant '{shader.Name}:{entry}' has no bytecode.");
        return variant;
    }

    internal static ShaderBytecodeFormat BytecodeFormat(string backend)
        => backend switch
        {
            "dxil" => ShaderBytecodeFormat.Dxil,
            "spirv" => ShaderBytecodeFormat.Spirv,
            _ => ShaderBytecodeFormat.MetalLibrary,
        };

    private sealed class ModuleState(ShaderModuleHandle handle)
    {
        public ShaderModuleHandle Handle { get; } = handle;
        public int RefCount = 1;
    }

    private readonly record struct ShaderModuleKey(
        string Name,
        string Entry,
        string Backend,
        string Version)
    {
        public static ShaderModuleKey From(Shader shader, string entry, string backend)
            => new(
                ShaderKeys.Name(shader),
                entry,
                backend,
                ShaderKeys.Version(shader, entry));
    }
}
