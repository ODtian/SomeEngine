using System.Reflection;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;
using ReflectionBindingFlags = System.Reflection.BindingFlags;

namespace SomeEngine.Tests;

public sealed class PipelineCacheTests
{
    [Fact]
    public void QueueCompute_ReusesState()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state key layout" });
        Shader shader = ComputeShader("PipelineStateKeyTest");

        PipelineTicket first = context.PipelineCache!.QueueCompute(State(shader, layout, "Pipeline State Key Test"));
        PipelineTicket second = context.PipelineCache.QueueCompute(State(shader, layout, "Pipeline State Key Test"));
        context.PipelineCache.ProcessQueue();

        PipelineHandle firstHandle = context.PipelineCache.GetPipeline(first, PipelineNeed.Required);
        PipelineHandle secondHandle = context.PipelineCache.GetPipeline(second, PipelineNeed.Required);

        Assert.Equal(firstHandle, secondHandle);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref first);
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.False(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void RenderGraphExecute_DoesNotProcessQueue()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        IQueue queue = device.GetQueue(QueueType.Graphics);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline graph boundary layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineStateGraphBoundary"), layout, "Pipeline State Graph Boundary"));
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.UsePipelineCache(context.PipelineCache);
        graph.AddRasterPass(
            "Pipeline Queue Boundary",
            builder => builder.SideEffect(),
            _ => { });
        graph.Execute(device, queue);

        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(ticket));

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Release_QueuedTicketSkipsCreate()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline queued release layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineStateQueuedRelease"), layout, "Pipeline State Queued Release"));

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.ProcessQueue();

        Assert.False(ticket.IsValid);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Release_RetiresAfterFence()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state fence layout" });
        Shader shader = ComputeShader("PipelineStateFenceTest");
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(State(shader, layout, "Pipeline State Fence Test"));
        context.PipelineCache.ProcessQueue();
        PipelineHandle pipeline = context.PipelineCache.GetPipeline(ticket, PipelineNeed.Required);

        context.PipelineCache.Track([pipeline], 7);
        context.PipelineCache.Release(ref ticket);

        Assert.False(ticket.IsValid);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Retire(6);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Retire(7);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Release_RevivesPendingKey()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state revive layout" });
        Shader shader = ComputeShader("PipelineStateReviveTest");
        PipelineTicket first = context.PipelineCache!.QueueCompute(State(shader, layout, "Pipeline State Revive Test"));
        context.PipelineCache.ProcessQueue();
        PipelineHandle firstHandle = context.PipelineCache.GetPipeline(first, PipelineNeed.Required);

        context.PipelineCache.Track([firstHandle], 7);
        context.PipelineCache.Release(ref first);

        PipelineTicket revived = context.PipelineCache.QueueCompute(State(shader, layout, "Pipeline State Revive Test"));
        context.PipelineCache.ProcessQueue();
        PipelineHandle revivedHandle = context.PipelineCache.GetPipeline(revived, PipelineNeed.Required);

        Assert.Equal(firstHandle, revivedHandle);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));
        context.PipelineCache.Retire(7);
        Assert.True(revived.IsValid);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Track([revivedHandle], 9);
        context.PipelineCache.Release(ref revived);
        context.PipelineCache.Retire(8);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Retire(9);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Release_RetiredKeyQueuesAgain()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state retired layout" });
        Shader shader = ComputeShader("PipelineStateRetiredTest");
        PipelineTicket first = context.PipelineCache!.QueueCompute(State(shader, layout, "Pipeline State Retired Test"));
        context.PipelineCache.ProcessQueue();
        PipelineHandle firstHandle = context.PipelineCache.GetPipeline(first, PipelineNeed.Required);

        context.PipelineCache.Track([firstHandle], 3);
        context.PipelineCache.Release(ref first);
        context.PipelineCache.Retire(3);

        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));

        PipelineTicket second = context.PipelineCache.QueueCompute(State(shader, layout, "Pipeline State Retired Test"));
        context.PipelineCache.ProcessQueue();
        PipelineHandle secondHandle = context.PipelineCache.GetPipeline(second, PipelineNeed.Required);

        Assert.True(secondHandle.IsValid);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void QueueCompute_ReusesEqualShaderContent()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state shader content layout" });
        Shader firstShader = ComputeShader("PipelineStateContentTest", "same-version");
        Shader secondShader = ComputeShader("PipelineStateContentTest", "same-version");

        PipelineTicket first = context.PipelineCache!.QueueCompute(State(firstShader, layout, "Pipeline State Handle Test"));
        PipelineTicket second = context.PipelineCache.QueueCompute(State(secondShader, layout, "Pipeline State Handle Test"));
        context.PipelineCache.ProcessQueue();

        Assert.Equal(
            context.PipelineCache.GetPipeline(first, PipelineNeed.Required),
            context.PipelineCache.GetPipeline(second, PipelineNeed.Required));
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref first);
        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void QueueCompute_SeparatesShaderVersions()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state shader version layout" });
        Shader firstShader = ComputeShader("PipelineStateVersionTest", "first-version");
        Shader secondShader = ComputeShader("PipelineStateVersionTest", "second-version");

        PipelineTicket first = context.PipelineCache!.QueueCompute(State(firstShader, layout, "Pipeline State Version Test"));
        PipelineTicket second = context.PipelineCache.QueueCompute(State(secondShader, layout, "Pipeline State Version Test"));
        context.PipelineCache.ProcessQueue();

        Assert.NotEqual(
            context.PipelineCache.GetPipeline(first, PipelineNeed.Required),
            context.PipelineCache.GetPipeline(second, PipelineNeed.Required));
        Assert.Equal(2, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref first);
        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void QueueGraphics_SeparatesFormats()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state graphics layout" });
        Shader shader = GraphicsShader("PipelineStateGraphicsTest");

        PipelineTicket first = context.PipelineCache!.QueueGraphics(Graphics(shader, layout, Format.Rgba8Unorm));
        PipelineTicket second = context.PipelineCache.QueueGraphics(Graphics(shader, layout, Format.Bgra8Unorm));
        context.PipelineCache.ProcessQueue();

        Assert.NotEqual(
            context.PipelineCache.GetPipeline(first, PipelineNeed.Required),
            context.PipelineCache.GetPipeline(second, PipelineNeed.Required));
        Assert.Equal(2, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref first);
        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void GetPipeline_HandlesQueuedOptional()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state queued layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(State(ComputeShader("PipelineStateQueuedTest"), layout, "Pipeline State Queued Test"));

        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(ticket));
        Assert.False(context.PipelineCache.GetPipeline(ticket, PipelineNeed.Optional).IsValid);
        Assert.Throws<InvalidOperationException>(() => context.PipelineCache.GetPipeline(ticket, PipelineNeed.Required));

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void GetPipeline_RecordsIssue()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline issue layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineIssueTest"), layout, "Pipeline Issue Test"));

        Assert.False(context.PipelineCache.GetPipeline(ticket, PipelineNeed.Optional, "Direct Issue Site").IsValid);

        PipelineIssue issue = Assert.Single(issues.Take());
        Assert.Equal(ticket.Id, issue.TicketId);
        Assert.Equal("Pipeline Issue Test", issue.Name);
        Assert.Equal("Pipeline Issue Test", issue.Owner);
        Assert.Equal(PipelineNeed.Optional, issue.Need);
        Assert.Equal(PipelineStatus.Queued, issue.Status);
        Assert.Equal(PipelineIssueResult.TooLate, issue.Result);
        Assert.Equal("Direct Issue Site", issue.Site);
        Assert.Equal(1, issue.Count);
        Assert.Empty(issues.Take());

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void GetPipeline_RecordsUntrackedTicket()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);

        PipelineHandle handle = context.PipelineCache!.GetPipeline(default, PipelineNeed.Optional, "Invalid Ticket Site");

        Assert.False(handle.IsValid);
        PipelineIssue issue = Assert.Single(issues.Take());
        Assert.Equal(PipelineIssueResult.Untracked, issue.Result);
        Assert.Equal(PipelineStatus.Failed, issue.Status);
        Assert.Equal(PipelineNeed.Optional, issue.Need);
        Assert.Equal("Invalid Ticket Site", issue.Site);
    }

    [Fact]
    public void GetPipeline_CoalescesIssues()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink sink = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline issue merge layout" });
        Shader shader = ComputeShader("PipelineIssueMergeTest");
        PipelineTicket first = context.PipelineCache!.QueueCompute(
            State(shader, layout, "Pipeline Issue Merge Test"));
        PipelineTicket second = context.PipelineCache.QueueCompute(
            State(shader, layout, "Pipeline Issue Merge Test"));

        Assert.False(context.PipelineCache.GetPipeline(first, PipelineNeed.Optional, "Repeated Site").IsValid);
        Assert.False(context.PipelineCache.GetPipeline(second, PipelineNeed.Optional, "Repeated Site").IsValid);
        Assert.False(context.PipelineCache.GetPipeline(first, PipelineNeed.Optional, "Other Site").IsValid);

        PipelineIssue[] issues = sink.Take();
        Assert.Equal(2, issues.Length);
        PipelineIssue repeated = Assert.Single(issues, issue => issue.Site == "Repeated Site");
        PipelineIssue other = Assert.Single(issues, issue => issue.Site == "Other Site");
        Assert.Equal(2, repeated.Count);
        Assert.Equal(1, other.Count);
        Assert.Equal(first.Id, repeated.TicketId);
        Assert.Equal(PipelineStatus.Queued, repeated.Status);
        Assert.Equal(PipelineIssueResult.TooLate, repeated.Result);

        context.PipelineCache.Release(ref first);
        context.PipelineCache.Release(ref second);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void RenderGraph_RecordsPipelineIssueSite()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        IQueue queue = device.GetQueue(QueueType.Graphics);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline issue graph layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineIssueGraphTest"), layout, "Pipeline Issue Graph Test"));
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.UsePipelineCache(context.PipelineCache);
        graph.AddRasterPass(
            "Pipeline Issue Pass",
            builder => builder.SideEffect(),
            pass => Assert.False(pass.GetPipeline(ticket, PipelineNeed.Optional).IsValid));
        graph.Execute(device, queue);

        PipelineIssue issue = Assert.Single(issues.Take());
        Assert.Equal(ticket.Id, issue.TicketId);
        Assert.Equal("Pipeline Issue Graph Test", issue.Name);
        Assert.Equal(PipelineStatus.Queued, issue.Status);
        Assert.Equal(PipelineIssueResult.TooLate, issue.Result);
        Assert.Equal("Pipeline Issue Pass", issue.Site);
        Assert.Equal(1, issue.Count);

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Collect_UsesWarmupBudget()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state collector layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateCollectorFirst", "collector-first"), layout, "Pipeline State Collector First"),
            "Collector First");
        collector.AddCompute(
            State(ComputeShader("PipelineStateCollectorSecond", "collector-second"), layout, "Pipeline State Collector Second"),
            "Collector Second",
            PipelineNeed.Optional);

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);

        Assert.Equal(2, tickets.Length);
        PipelineWarmup report = context.PipelineCache.Warmup(tickets, 1);
        Assert.Equal(2, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.Ready);
        Assert.Equal(1, report.Queued);
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.RequiredQueued);
        Assert.Equal(1, report.OptionalQueued);
        Assert.True(report.BudgetLimited);
        Assert.True(report.RequiredReady);
        Assert.False(report.Complete);
        Assert.False(report.ReadyToUse);
        var queued = Assert.Single(report.Issues);
        Assert.Equal("Pipeline State Collector Second", queued.Name);
        Assert.Equal("Collector Second", queued.Owner);
        Assert.Equal(PipelineNeed.Optional, queued.Need);
        Assert.Equal(PipelineStatus.Queued, queued.Status);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(tickets[0]));
        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(tickets[1]));

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Release(ref tickets[1]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Warmup_TicketBudgetIgnoresUnrelatedQueue()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state targeted warmup layout" });
        PipelineTicket unrelated = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineStateUnrelatedWarmup", "unrelated-warmup"), layout, "Pipeline State Unrelated Warmup"));
        PipelineTicket target = context.PipelineCache.QueueCompute(
            State(ComputeShader("PipelineStateTargetWarmup", "target-warmup"), layout, "Pipeline State Target Warmup"));

        PipelineWarmup report = context.PipelineCache.Warmup([target], 1);

        Assert.Equal(1, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.Ready);
        Assert.False(report.BudgetLimited);
        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(unrelated));
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(target));

        context.PipelineCache.Release(ref unrelated);
        context.PipelineCache.Release(ref target);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Collect_AcceptsSource()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state source layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateSource"), layout, "Pipeline State Source"),
            "Source Owner");

        PipelineTicket[] tickets = context.PipelineCache!.Collect(source);
        PipelineWarmup report = context.PipelineCache.Warmup(tickets, int.MaxValue);

        Assert.Single(tickets);
        Assert.Equal(1, report.Ready);
        Assert.False(report.BudgetLimited);
        Assert.True(report.ReadyToUse);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(tickets[0]));

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void RenderContext_WarmupPipelinesReportsOwners()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state global warmup layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateGlobalFirst", "global-first"), layout, "Pipeline State Global First"),
            "Global First");
        collector.AddCompute(
            State(ComputeShader("PipelineStateGlobalSecond", "global-second"), layout, "Pipeline State Global Second"),
            "Global Second",
            PipelineNeed.Optional);

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.WarmupPipelines(tickets, 1);

        Assert.Equal(2, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.Ready);
        Assert.Equal(1, report.Queued);
        Assert.True(report.BudgetLimited);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("Global Second", issue.Owner);
        Assert.Equal(PipelineNeed.Optional, issue.Need);
        Assert.Equal(PipelineStatus.Queued, issue.Status);
        Assert.Equal(PipelineIssueResult.Missed, issue.Result);

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Release(ref tickets[1]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Lease_HoldsCollectedTickets()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state lease layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateLease"), layout, "Pipeline State Lease"),
            "Lease Owner");

        PipelineLease lease = context.PipelineCache!.Lease(source);
        PipelineWarmup report = lease.WaitRequired();
        PipelineTicket ticket = Assert.Single(lease.Tickets);

        Assert.Equal(1, report.Ready);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(ticket));
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        lease.Dispose();
        Assert.Empty(lease.Tickets);
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Lease_DetachTransfersTickets()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state detach layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateDetach"), layout, "Pipeline State Detach"),
            "Detach Owner");

        PipelineLease lease = context.PipelineCache!.Lease(source);
        lease.WaitRequired();
        PipelineTicket[] tickets = lease.Detach();

        lease.Dispose();
        context.PipelineCache.Retire(ulong.MaxValue);

        Assert.Single(tickets);
        Assert.Empty(lease.Tickets);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_HoldTicketsUntilRemoved()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source hold layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateSourceHold"), layout, "Pipeline State Source Hold"),
            "Source Hold Owner");

        PipelineSourceLease lease = context.AddPipelineSource(source);
        PipelineWarmup report = context.WaitSources();

        Assert.Equal(1, report.Ready);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        lease.Dispose();
        PipelineWarmup empty = context.RefreshSources();
        context.PipelineCache!.Retire(ulong.MaxValue);

        Assert.Equal(0, empty.Requested);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_ReferenceCountSources()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source reference layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateSourceReference"), layout, "Pipeline State Source Reference"),
            "Source Reference Owner");

        PipelineSourceLease first = context.AddPipelineSource(source);
        PipelineSourceLease second = context.AddPipelineSource(source);
        context.WaitSources();

        first.Dispose();
        PipelineWarmup held = context.RefreshSources();
        context.PipelineCache!.Retire(ulong.MaxValue);

        Assert.Equal(1, held.Requested);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        second.Dispose();
        PipelineWarmup empty = context.RefreshSources();
        context.PipelineCache.Retire(ulong.MaxValue);

        Assert.Equal(0, empty.Requested);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_UseWarmupBudget()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source budget layout" });
        var first = new TestSource(
            State(ComputeShader("PipelineStateSourceBudgetFirst", "source-budget-first"), layout, "Pipeline State Source Budget First"),
            "Source Budget First",
            "Budget Source First");
        var second = new TestSource(
            State(ComputeShader("PipelineStateSourceBudgetSecond", "source-budget-second"), layout, "Pipeline State Source Budget Second"),
            "Source Budget Second",
            "Budget Source Second",
            PipelineNeed.Optional);

        PipelineSourceLease firstLease = context.AddPipelineSource(first);
        PipelineSourceLease secondLease = context.AddPipelineSource(second);
        PipelineWarmup report = context.WarmupSources(1);

        Assert.Equal(2, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.Ready);
        Assert.Equal(1, report.Queued);
        Assert.True(report.BudgetLimited);
        Assert.True(report.RequiredReady);
        Assert.False(report.Complete);
        Assert.False(report.ReadyToUse);
        PipelineIssue queued = Assert.Single(issues.Take());
        Assert.Equal("Source Budget Second", queued.Owner);
        Assert.Equal("Budget Source Second", queued.Source);
        Assert.Equal(PipelineNeed.Optional, queued.Need);
        Assert.Equal(PipelineStatus.Queued, queued.Status);
        Assert.Equal(PipelineIssueResult.Missed, queued.Result);

        firstLease.Dispose();
        secondLease.Dispose();
        context.RefreshSources();
        context.PipelineCache!.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_OptionalFailureDoesNotBlockReadyToUse()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source optional failure layout" });
        var source = new TestSource(
            State(MissingShader("PipelineStateSourceOptionalMissing"), layout, "Pipeline State Source Optional Missing"),
            "Source Optional Missing",
            "Optional Failure Source",
            PipelineNeed.Optional);

        PipelineSourceLease lease = context.AddPipelineSource(source);
        PipelineWarmup report = context.WarmupSources(int.MaxValue);

        Assert.Equal(1, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(0, report.Ready);
        Assert.Equal(0, report.Queued);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.RequiredFailed);
        Assert.Equal(1, report.OptionalFailed);
        Assert.True(report.RequiredReady);
        Assert.True(report.Complete);
        Assert.True(report.ReadyToUse);
        PipelineIssue issue = Assert.Single(report.Issues);
        Assert.Equal("Source Optional Missing", issue.Owner);
        Assert.Equal("Optional Failure Source", issue.Source);
        Assert.Equal(PipelineNeed.Optional, issue.Need);
        Assert.Equal(PipelineStatus.Failed, issue.Status);
        Assert.Equal(PipelineIssueResult.Failed, issue.Result);
        PipelineIssue logged = Assert.Single(issues.Take());
        Assert.Equal(issue.Owner, logged.Owner);
        Assert.Equal(issue.Source, logged.Source);

        lease.Dispose();
        context.RefreshSources();
        context.PipelineCache!.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_RefreshWhenSourceVersionChanges()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        TestIssueSink issues = CaptureIssues(context);
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source version layout" });
        var source = new TestSource(
            State(ComputeShader("PipelineStateSourceVersionFirst", "source-version-first"), layout, "Pipeline State Source Version First"),
            "Source Version First",
            "Changing Source");

        PipelineSourceLease lease = context.AddPipelineSource(source);
        PipelineWarmup first = context.WaitSources();
        Assert.Equal(1, first.Ready);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        source.Set(
            State(ComputeShader("PipelineStateSourceVersionSecond", "source-version-second"), layout, "Pipeline State Source Version Second"),
            "Source Version Second");
        PipelineWarmup changed = context.RefreshSources();

        Assert.Equal(1, changed.Requested);
        Assert.Equal(1, changed.Queued);
        PipelineIssue issue = Assert.Single(changed.Issues);
        Assert.Equal("Source Version Second", issue.Owner);
        Assert.Equal("Changing Source", issue.Source);
        Assert.Equal(PipelineIssueResult.Missed, issue.Result);
        PipelineIssue logged = Assert.Single(issues.Take());
        Assert.Equal("Source Version Second", logged.Owner);
        Assert.Equal("Changing Source", logged.Source);
        Assert.Equal(PipelineIssueResult.Missed, logged.Result);
        context.PipelineCache!.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));

        PipelineWarmup second = context.WaitSources();
        Assert.Equal(1, second.Ready);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        lease.Dispose();
        context.RefreshSources();
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void PipelineSources_RefreshesOnlyChangedSource()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline source incremental layout" });
        var changed = new TestSource(
            State(ComputeShader("PipelineStateChangedFirst", "source-changed-first"), layout, "Pipeline State Changed First"),
            "Source Changed First",
            "Changed Source");
        var stable = new TestSource(
            State(ComputeShader("PipelineStateStableFirst", "source-stable-first"), layout, "Pipeline State Stable First"),
            "Source Stable First",
            "Stable Source");

        PipelineSourceLease changedLease = context.AddPipelineSource(changed);
        PipelineSourceLease stableLease = context.AddPipelineSource(stable);
        PipelineWarmup first = context.WaitSources();

        Assert.Equal(2, first.Ready);
        Assert.Equal(1, changed.CollectCount);
        Assert.Equal(1, stable.CollectCount);
        Assert.Equal(2, LiveHandleCount(device, "Pipelines"));

        changed.Set(
            State(ComputeShader("PipelineStateChangedSecond", "source-changed-second"), layout, "Pipeline State Changed Second"),
            "Source Changed Second");
        PipelineWarmup refreshed = context.RefreshSources();

        Assert.Equal(2, refreshed.Requested);
        Assert.Equal(1, refreshed.Ready);
        Assert.Equal(1, refreshed.Queued);
        Assert.Equal(2, changed.CollectCount);
        Assert.Equal(1, stable.CollectCount);

        context.PipelineCache!.Retire(ulong.MaxValue);
        Assert.Equal(1, LiveHandleCount(device, "Pipelines"));

        PipelineWarmup second = context.WaitSources();
        Assert.Equal(2, second.Ready);
        Assert.Equal(2, LiveHandleCount(device, "Pipelines"));

        changedLease.Dispose();
        stableLease.Dispose();
        context.RefreshSources();
        context.PipelineCache.Retire(ulong.MaxValue);
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Collect_ReleasesTicketsAfterFailure()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state rollback layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateRollbackFirst"), layout, "Pipeline State Rollback First"),
            "Rollback First");
        collector.AddCompute(
            State(ComputeShader("PipelineStateRollbackSecond"), default, "Pipeline State Rollback Second"),
            "Rollback Second");

        Assert.Throws<ArgumentException>(() => context.PipelineCache!.Collect(collector));

        context.PipelineCache!.ProcessQueue();
        Assert.Equal(0, LiveHandleCount(device, "Pipelines"));
        device.Destroy(layout);
    }

    [Fact]
    public void Warmup_ReportsFailedTicket()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state failed owner layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(MissingShader("PipelineStateMissingVariant"), layout, "Pipeline State Missing Variant"),
            "Missing Shader Asset");

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.PipelineCache.Warmup(tickets, int.MaxValue);

        Assert.Equal(1, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(0, report.Ready);
        Assert.Equal(0, report.Queued);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.RequiredFailed);
        Assert.False(report.RequiredReady);
        Assert.True(report.Complete);
        Assert.False(report.ReadyToUse);
        Assert.False(report.BudgetLimited);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("Pipeline State Missing Variant", issue.Name);
        Assert.Equal("Missing Shader Asset", issue.Owner);
        Assert.Equal(PipelineNeed.Required, issue.Need);
        Assert.Equal(PipelineStatus.Failed, issue.Status);
        Assert.Equal(PipelineIssueResult.Failed, issue.Result);
        Assert.Contains("Shader variant not found", issue.Error!);
        Assert.Equal(PipelineStatus.Failed, context.PipelineCache.GetStatus(tickets[0]));
        Assert.Contains("Shader variant not found", context.PipelineCache.GetError(tickets[0])!.Message);

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void WaitRequired_ThrowsForRequiredFailure()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state wait failure layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(MissingShader("PipelineStateWaitMissing"), layout, "Pipeline State Wait Missing"),
            "Wait Owner");

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);

        var ex = Assert.Throws<InvalidOperationException>(() => context.PipelineCache.WaitRequired(tickets));
        Assert.Contains("Pipeline State Wait Missing", ex.Message);
        Assert.Contains("Wait Owner", ex.Message);
        Assert.Contains("failed", ex.Message);

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Warmup_ReportsOptionalFailure()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state wait optional layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(MissingShader("PipelineStateWaitOptional"), layout, "Pipeline State Wait Optional"),
            "Optional Owner",
            PipelineNeed.Optional);

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.PipelineCache.Warmup(tickets, int.MaxValue);

        Assert.Equal(1, report.OptionalFailed);
        Assert.Equal(0, report.RequiredFailed);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("Optional Owner", issue.Owner);
        Assert.Equal(PipelineNeed.Optional, issue.Need);
        Assert.Equal(PipelineStatus.Failed, issue.Status);
        Assert.Equal(PipelineIssueResult.Failed, issue.Result);
        Assert.True(report.RequiredReady);
        Assert.True(report.Complete);
        Assert.True(report.ReadyToUse);

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void WaitRequired_LeavesOptionalQueued()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state wait required layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateWaitRequiredFirst", "wait-required-first"), layout, "Pipeline State Wait Required First"),
            "Required Owner");
        collector.AddCompute(
            State(ComputeShader("PipelineStateWaitRequiredSecond", "wait-required-second"), layout, "Pipeline State Wait Required Second"),
            "Optional Owner",
            PipelineNeed.Optional);

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.WaitRequired(tickets);

        Assert.True(report.RequiredReady);
        Assert.Equal(2, report.Requested);
        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.Ready);
        Assert.Equal(1, report.Queued);
        Assert.Equal(1, report.OptionalQueued);
        Assert.False(report.Complete);
        Assert.False(report.ReadyToUse);
        Assert.False(report.BudgetLimited);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(tickets[0]));
        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(tickets[1]));

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Release(ref tickets[1]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void WaitRequiredAll_LeavesOptionalQueued()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state wait required all layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateWaitRequiredAllFirst", "wait-required-all-first"), layout, "Pipeline State Wait Required All First"),
            "Required Owner");
        collector.AddCompute(
            State(ComputeShader("PipelineStateWaitRequiredAllSecond", "wait-required-all-second"), layout, "Pipeline State Wait Required All Second"),
            "Optional Owner",
            PipelineNeed.Optional);

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.WaitRequired(tickets);

        Assert.True(report.RequiredReady);
        Assert.Equal(1, report.Processed);
        Assert.False(report.Complete);
        Assert.False(report.ReadyToUse);
        Assert.False(report.BudgetLimited);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(tickets[0]));
        Assert.Equal(PipelineStatus.Queued, context.PipelineCache.GetStatus(tickets[1]));

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Release(ref tickets[1]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void Inspect_DoesNotProcessQueue()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state inspect layout" });
        var collector = new PipelineCollector();
        collector.AddCompute(
            State(ComputeShader("PipelineStateInspect"), layout, "Pipeline State Inspect"),
            "Inspect Owner");

        PipelineTicket[] tickets = context.PipelineCache!.Collect(collector);
        PipelineWarmup report = context.PipelineCache.Inspect(tickets);

        Assert.Equal(1, report.Requested);
        Assert.Equal(0, report.Processed);
        Assert.Equal(0, report.Ready);
        Assert.Equal(1, report.Queued);
        Assert.False(report.RequiredReady);
        Assert.False(report.Complete);
        Assert.False(report.ReadyToUse);
        Assert.False(report.BudgetLimited);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("Pipeline State Inspect", issue.Name);
        Assert.Equal("Inspect Owner", issue.Owner);
        Assert.Equal(PipelineStatus.Queued, issue.Status);
        Assert.Equal(PipelineIssueResult.Missed, issue.Result);

        context.PipelineCache.Release(ref tickets[0]);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void QueueGraphics_PassesNativeCache()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state native cache layout" });
        Shader shader = GraphicsShader("PipelineStateNativeCacheTest");

        PipelineTicket ticket = context.PipelineCache!.QueueGraphics(Graphics(shader, layout, Format.Rgba8Unorm));
        context.PipelineCache.ProcessQueue();
        PipelineHandle pipeline = context.PipelineCache.GetPipeline(ticket, PipelineNeed.Required);
        GraphicsPipelineDesc desc = GraphicsDesc(device, pipeline);

        Assert.True(desc.PipelineCache.IsValid);

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void RenderContext_ExposesNativeCache()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        byte[] data = [1, 2, 3, 4];

        context.LoadPipelineCache(data);
        byte[] saved = context.SavePipelineCache();

        Assert.Equal(data, saved);
    }

    [Fact]
    public void LoadPipelineCache_RejectsQueuedPipelines()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state cache load layout" });
        PipelineTicket ticket = context.PipelineCache!.QueueCompute(
            State(ComputeShader("PipelineStateCacheLoad"), layout, "Pipeline State Cache Load"));

        Assert.Throws<InvalidOperationException>(() => context.LoadPipelineCache(ReadOnlyMemory<byte>.Empty));

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    [Fact]
    public void QueueGraphics_SnapshotsStateLists()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        IDevice device = context.GraphicsDevice!;
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "pipeline state snapshot layout" });
        Shader shader = GraphicsShader("PipelineStateSnapshotTest");
        var colorFormats = new List<Format> { Format.Rgba8Unorm };
        var vertexBuffers = new List<VertexLayoutDesc>
        {
            new() { Slot = 0, StrideInBytes = 12 },
        };
        var vertexAttributes = new List<VertexAttributeDesc>
        {
            new() { Location = 0, BufferSlot = 0, Format = Format.Rgb32Float },
        };
        var blendTargets = new List<BlendTargetDesc>
        {
            new() { Enable = false, WriteMask = ColorWriteMask.All },
        };

        GraphicsState state = Graphics(shader, layout, Format.Rgba8Unorm) with
        {
            ColorFormats = colorFormats,
            VertexBuffers = vertexBuffers,
            VertexAttributes = vertexAttributes,
            Blend = new BlendDesc { Targets = blendTargets },
        };

        PipelineTicket ticket = context.PipelineCache!.QueueGraphics(state);
        colorFormats[0] = Format.Bgra8Unorm;
        vertexBuffers[0] = vertexBuffers[0] with { StrideInBytes = 16 };
        vertexAttributes[0] = vertexAttributes[0] with { OffsetInBytes = 4 };
        blendTargets[0] = blendTargets[0] with { Enable = true };
        context.PipelineCache.ProcessQueue();

        PipelineHandle pipeline = context.PipelineCache.GetPipeline(ticket, PipelineNeed.Required);
        GraphicsPipelineDesc desc = GraphicsDesc(device, pipeline);

        Assert.Equal(Format.Rgba8Unorm, Assert.Single(desc.ColorFormats));
        Assert.Equal(12u, Assert.Single(desc.VertexBuffers).StrideInBytes);
        Assert.Equal(0u, Assert.Single(desc.VertexAttributes).OffsetInBytes);
        Assert.False(Assert.Single(desc.Blend.Targets).Enable);

        context.PipelineCache.Release(ref ticket);
        context.PipelineCache.Retire(ulong.MaxValue);
        device.Destroy(layout);
    }

    private static ComputeState State(Shader shader, PipelineLayoutHandle layout, string name)
        => new()
        {
            Name = name,
            Shader = shader,
            EntryPoint = "main",
            Layout = layout,
        };

    private static GraphicsState Graphics(Shader shader, PipelineLayoutHandle layout, Format format)
        => new()
        {
            Name = $"Pipeline State Graphics {format}",
            VertexShader = shader,
            VertexEntry = "VSMain",
            PixelShader = shader,
            PixelEntry = "PSMain",
            Layout = layout,
            ColorFormats = [format],
        };

    private static Shader ComputeShader(string name)
        => ComputeShader(name, $"{name}-main");

    private static Shader ComputeShader(string name, string version)
        => new(
            name,
            [
                new ShaderVariant(
                    "dxil",
                    new byte[] { 1, 2, 3, 4 },
                    "main",
                    ShaderStage.Compute,
                    version),
            ],
            [],
            [],
            []);

    private static Shader MissingShader(string name)
        => new(name, [], [], [], []);

    private static Shader GraphicsShader(string name)
        => new(
            name,
            [
                new ShaderVariant(
                    "dxil",
                    new byte[] { 1, 2, 3, 4 },
                    "VSMain",
                    ShaderStage.Vertex,
                    $"{name}-vs"),
                new ShaderVariant(
                    "dxil",
                    new byte[] { 1, 2, 3, 4 },
                    "PSMain",
                    ShaderStage.Pixel,
                    $"{name}-ps"),
            ],
            [],
            [],
            []);

    private static GraphicsPipelineDesc GraphicsDesc(IDevice device, PipelineHandle pipeline)
    {
        object store = device.GetType()
            .GetField("Pipelines", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .GetValue(device)!;
        object record = store.GetType()
            .GetMethod("Get", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)!
            .Invoke(store, [pipeline, "Pipeline"])!;
        return (GraphicsPipelineDesc)record.GetType()
            .GetProperty("GraphicsDesc", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)!
            .GetValue(record)!;
    }

    private static TestIssueSink CaptureIssues(RenderContext context)
    {
        var sink = new TestIssueSink();
        context.PipelineIssueSink = sink;
        return sink;
    }

    private sealed class TestIssueSink : PipelineIssueSink
    {
        private readonly List<PipelineIssue> _issues = [];
        private readonly Dictionary<string, int> _keys = new(StringComparer.Ordinal);

        public void Add(PipelineIssue issue)
        {
            string key = string.Join(
                "|",
                issue.Result,
                issue.Need,
                issue.Status,
                issue.Site,
                issue.Source,
                issue.Name,
                issue.Owner,
                issue.Error);
            if (_keys.TryGetValue(key, out int index))
            {
                PipelineIssue current = _issues[index];
                _issues[index] = current with { Count = current.Count + issue.Count };
                return;
            }

            _keys.Add(key, _issues.Count);
            _issues.Add(issue);
        }

        public PipelineIssue[] Take()
        {
            PipelineIssue[] issues = _issues.ToArray();
            _issues.Clear();
            _keys.Clear();
            return issues;
        }
    }

    private sealed class TestSource(
        ComputeState state,
        string owner,
        string name = "Test Source",
        PipelineNeed need = PipelineNeed.Required) : IPipelineSource
    {
        private ComputeState _state = state;
        private string _owner = owner;
        private readonly PipelineNeed _need = need;
        private readonly string _name = name;

        public string Name => _name;
        public uint Version { get; private set; }
        public int CollectCount { get; private set; }

        public void Set(ComputeState state, string owner)
        {
            _state = state;
            _owner = owner;
            Version++;
        }

        public void Collect(PipelineCollector collector)
        {
            CollectCount++;
            collector.AddCompute(_state, _owner, _need);
        }
    }
}
