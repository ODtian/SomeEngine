using System.Text;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    public string DumpText()
        => DumpText(asyncCompute: false, asyncCopy: false);

    public string DumpText(bool asyncCompute, bool asyncCopy)
    {
        ThrowIfDisposed();
        var compile = CompileGraph(asyncCompute, asyncCopy);
        return DumpText(compile);
    }

    private string DumpText(CompiledGraph compile)
    {
        var text = new StringBuilder();
        int liveResourceCount = 0;
        for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
        {
            if (compile.Resources[resourceIndex].Live)
                liveResourceCount++;
        }

        text.AppendLine("RenderGraph");
        text.Append("Passes: ").Append(compile.Count).AppendLine();
        text.Append("QueueBatches: ").Append(compile.QueueBatches.Count).AppendLine();
        text.Append("QueueLinks: ").Append(compile.QueueLinks.Count).AppendLine();
        text.Append("Queues: ").Append(QueueText(compile.Queues)).AppendLine();
        text.Append("FrameQueue: ").Append(compile.FrameQueue).AppendLine();
        text.Append("FrameWaits: ").Append(QueueText(compile.FrameWaits)).AppendLine();
        text.Append("FinalWork: ").Append(compile.FinalWork).AppendLine();
        text.Append("BatchSignal: ").Append(compile.BatchSignal).AppendLine();
        text.Append("Dependencies: ").Append(compile.Dependencies.Count).AppendLine();
        text.Append("Resources: ").Append(liveResourceCount).Append(" live / ").Append(_resources.Count).AppendLine(" total");
        text.AppendLine();

        text.AppendLine("Passes");
        if (compile.Count == 0)
        {
            text.AppendLine("  <none>");
        }
        else
        {
            for (int passSlot = 0; passSlot < compile.Count; passSlot++)
                AppendPassDump(text, compile, passSlot);
        }

        text.AppendLine();
        text.AppendLine("Culled Passes");
        AppendCulledPasses(text, compile);

        text.AppendLine();
        text.AppendLine("Queue Batches");
        if (compile.QueueBatches.Count == 0)
        {
            text.AppendLine("  <none>");
        }
        else
        {
            for (int batchIndex = 0; batchIndex < compile.QueueBatches.Count; batchIndex++)
                AppendBatchDump(text, compile, batchIndex);
        }

        text.AppendLine();
        text.AppendLine("Queue Links");
        if (compile.QueueLinks.Count == 0)
        {
            text.AppendLine("  <none>");
        }
        else
        {
            for (int linkIndex = 0; linkIndex < compile.QueueLinks.Count; linkIndex++)
                AppendLinkDump(text, compile, compile.QueueLinks[linkIndex]);
        }

        text.AppendLine();
        text.AppendLine("Dependencies");
        if (compile.Dependencies.Count == 0)
        {
            text.AppendLine("  <none>");
        }
        else
        {
            for (int dependencyIndex = 0; dependencyIndex < compile.Dependencies.Count; dependencyIndex++)
                AppendDependencyDump(text, compile, compile.Dependencies[dependencyIndex]);
        }

        text.AppendLine();
        text.AppendLine("Resources");
        if (_resources.Count == 0)
        {
            text.AppendLine("  <none>");
        }
        else
        {
            for (int resourceIndex = 0; resourceIndex < _resources.Count; resourceIndex++)
                AppendResourceDump(text, compile, resourceIndex);
        }

        return text.ToString();
    }

    private void AppendPassDump(StringBuilder text, CompiledGraph compile, int passSlot)
    {
        int passIndex = compile.Passes[passSlot];
        text.Append("  ")
            .Append(passSlot)
            .Append(": ")
            .Append(_passes[passIndex].Name)
            .AppendLine();
        if (_passes[passIndex].SideEffect)
            text.AppendLine("    SideEffect");
        if (compile.PassKeepReasons[passIndex] is { Length: > 0 } reason)
            text.Append("    Keep: ").AppendLine(reason);

        var uses = compile.Uses[passIndex];
        if (uses.Count == 0)
        {
            text.AppendLine("    Uses: <none>");
            text.AppendLine("    Required Transitions: <none>");
            return;
        }

        for (int useIndex = 0; useIndex < uses.Count; useIndex++)
        {
            var use = uses[useIndex];
            var resource = _resources[use.ResourceIndex];
            text.Append("    ")
                .Append(AccessText(use.Access))
                .Append(": ")
                .Append(resource.Name)
                .Append(" ")
                .Append(VersionText(use))
                .Append(" ")
                .Append(StateText(use.EntryState, use.ExitState))
                .Append(" ")
                .Append(RangeText(use.Range));
            if (use.SourcePass >= 0)
            {
                int sourceSlot = compile.SlotOf(use.SourcePass);
                text.Append(" <- ")
                    .Append(sourceSlot)
                    .Append(":")
                    .Append(_passes[use.SourcePass].Name);
            }

            text.AppendLine();
        }

        AppendTransitions(text, compile.Transitions[passIndex]);
    }

    private void AppendCulledPasses(StringBuilder text, CompiledGraph compile)
    {
        bool wrote = false;
        for (int passIndex = 0; passIndex < _passes.Count; passIndex++)
        {
            if (compile.LivePasses[passIndex])
                continue;

            wrote = true;
            text.Append("  ")
                .Append(passIndex)
                .Append(": ")
                .Append(_passes[passIndex].Name)
                .Append(" - ")
                .Append(compile.PassCullReasons[passIndex] ?? "not reachable from any cull root")
                .AppendLine();
        }

        if (!wrote)
            text.AppendLine("  <none>");
    }

    private void AppendBatchDump(StringBuilder text, CompiledGraph compile, int batchIndex)
    {
        var batch = compile.QueueBatches[batchIndex];
        int lastSlot = checked(batch.StartSlot + batch.Count - 1);
        text.Append("  ")
            .Append(batchIndex)
            .Append(": ")
            .Append(batch.Kind)
            .Append(" queue=")
            .Append(batch.Queue)
            .Append(" inputLinks=")
            .Append(batch.InputLinks)
            .Append(" outputLinks=")
            .Append(batch.OutputLinks)
            .Append(" waitQueues=")
            .Append(QueueText(batch.WaitQueues))
            .Append(" signalQueues=")
            .Append(QueueText(batch.SignalQueues))
            .Append(" slots=");
        if (batch.StartSlot == lastSlot)
        {
            text.Append(batch.StartSlot);
        }
        else
        {
            text.Append(batch.StartSlot)
                .Append("..")
                .Append(lastSlot);
        }

        text.Append(" passes=");
        for (int passSlot = batch.StartSlot; passSlot <= lastSlot; passSlot++)
        {
            if (passSlot > batch.StartSlot)
                text.Append(", ");
            text.Append(_passes[compile.Passes[passSlot]].Name);
        }

        text.AppendLine();
    }

    private void AppendLinkDump(StringBuilder text, CompiledGraph compile, QueueLink link)
    {
        var source = compile.QueueBatches[link.SourceBatch];
        var target = compile.QueueBatches[link.TargetBatch];
        text.Append("  ")
            .Append(link.SourceBatch)
            .Append(":")
            .Append(link.SourceQueue)
            .Append(" slots=")
            .Append(source.StartSlot)
            .Append("..")
            .Append(checked(source.StartSlot + source.Count - 1))
            .Append(" -> ")
            .Append(link.TargetBatch)
            .Append(":")
            .Append(link.TargetQueue)
            .Append(" slots=")
            .Append(target.StartSlot)
            .Append("..")
            .Append(checked(target.StartSlot + target.Count - 1))
            .Append(" dependencies=")
            .Append(link.DependencyCount)
            .Append(" hazards=")
            .Append(HazardText(link.Hazards))
            .AppendLine();
    }

    private void AppendTransitions(StringBuilder text, TransitionBatch transitions)
    {
        int countBefore = text.Length;
        bool wroteTransition = false;
        text.AppendLine("    Required Transitions");
        IReadOnlyList<ResourceTransition> requiredTransitions = transitions.RequiredTransitionSource;
        for (int transitionIndex = 0; transitionIndex < requiredTransitions.Count; transitionIndex++)
        {
            var transition = requiredTransitions[transitionIndex];
            var resource = _resources[transition.ResourceIndex];
            AppendTransitionLine(text, resource.Name, transition.Before, transition.After, transition.Range);
            wroteTransition = true;
        }

        if (!wroteTransition)
        {
            text.Length = countBefore;
            text.AppendLine("    Required Transitions: <none>");
        }
    }

    private static void AppendTransitionLine(
        StringBuilder text,
        string name,
        ResourceState before,
        ResourceState after,
        SomeEngine.Rhi.SubresourceRange range)
    {
        if (before == ResourceState.UnorderedAccess && after == ResourceState.UnorderedAccess)
        {
            text.Append("      ")
                .Append(name)
                .Append(": UAV dependency ")
                .Append(RangeText(range))
                .AppendLine();
            return;
        }

        text.Append("      ")
            .Append(name)
            .Append(": ")
            .Append(before)
            .Append(" -> ")
            .Append(after)
            .Append(" ")
            .Append(RangeText(range))
            .AppendLine();
    }

    private void AppendDependencyDump(StringBuilder text, CompiledGraph compile, PassDependency dependency)
    {
        int sourceSlot = compile.SlotOf(dependency.SourcePass);
        int targetSlot = compile.SlotOf(dependency.TargetPass);
        text.Append("  ")
            .Append(sourceSlot)
            .Append(":")
            .Append(_passes[dependency.SourcePass].Name)
            .Append(" -> ")
            .Append(targetSlot)
            .Append(":")
            .Append(_passes[dependency.TargetPass].Name)
            .Append(": ")
            .Append(_resources[dependency.ResourceIndex].Name)
            .Append(" ")
            .Append(DependencyVersionText(dependency))
            .Append(" ")
            .Append(HazardText(dependency.Hazards))
            .AppendLine();
    }

    private void AppendResourceDump(StringBuilder text, CompiledGraph compile, int resourceIndex)
    {
        var resource = _resources[resourceIndex];
        var record = compile.Resources[resourceIndex];
        text.Append("  ")
            .Append(resourceIndex)
            .Append(": ")
            .Append(resource.Name)
            .Append(" ")
            .Append(resource.Kind)
            .Append(" ")
            .Append(resource.Imported ? "Imported" : "Transient")
            .Append(" ")
            .Append(record.Live ? "Live" : "Culled")
            .Append(" ")
            .Append(LifetimeText(record))
            .Append(" uses=")
            .Append(record.UseCount)
            .Append(" v")
            .Append(record.Version);

        if (record.Exported)
            text.Append(" Exported");
        if (record.FinalState)
            text.Append(" FinalState");

        text.AppendLine();
        AppendAllocationDump(text, resource, record);
    }

    private static void AppendAllocationDump(StringBuilder text, Resource resource, ResourceRecord record)
    {
        if (!record.Live)
            return;

        text.Append("    Allocation: ");
        if (resource.Imported)
        {
            text.AppendLine("Imported");
            return;
        }

        if (record.Aliased)
        {
            text.Append("Aliased heap=")
                .Append(record.AliasHeap)
                .Append(" offset=")
                .Append(record.AliasOffset)
                .Append(" size=")
                .Append(record.AliasSize);
        }
        else
        {
            text.Append(record.Reusable ? "Pooled" : "Dedicated");
        }
        if (resource.Kind == ResourceKind.Buffer)
        {
            var key = record.BufferKey;
            text.Append(" size=")
                .Append(key.SizeInBytes)
                .Append(" memory=")
                .Append(key.Memory)
                .Append(" bind=")
                .Append(key.BindFlags)
                .AppendLine();
            return;
        }

        var textureKey = record.TextureKey;
        text.Append(" size=")
            .Append(textureKey.Width)
            .Append("x")
            .Append(textureKey.Height);
        if (textureKey.Depth > 1)
            text.Append("x").Append(textureKey.Depth);

        text.Append(" mips=")
            .Append(textureKey.MipLevels)
            .Append(" array=")
            .Append(textureKey.ArraySize)
            .Append(" samples=")
            .Append(textureKey.SampleCount)
            .Append(" format=")
            .Append(textureKey.Format)
            .Append(" memory=")
            .Append(textureKey.Memory)
            .Append(" bind=")
            .Append(textureKey.BindFlags)
            .AppendLine();
    }

    private static string AccessText(RenderGraphAccess access)
        => access switch
        {
            RenderGraphAccess.ReadOnly => "ReadOnly",
            RenderGraphAccess.WriteOnly => "WriteOnly",
            RenderGraphAccess.ReadWrite => "ReadWrite",
            _ => "Use",
        };

    private static string StateText(ResourceState entryState, ResourceState exitState)
        => entryState == exitState
            ? entryState.ToString()
            : $"{entryState}->{exitState}";

    private static string VersionText(ResourceUse use)
    {
        bool reads = Reads(use.Access);
        bool writes = Writes(use.Access);
        if (reads && writes)
            return $"v{use.InVersion}->v{use.OutVersion}";
        if (writes)
            return $"v{use.OutVersion}";
        if (reads)
            return $"v{use.InVersion}";
        return "v?";
    }

    private static string DependencyVersionText(PassDependency dependency)
        => dependency.OutVersion > dependency.InVersion
            ? $"v{dependency.InVersion}->v{dependency.OutVersion}"
            : $"v{dependency.InVersion}";

    private static string HazardText(Hazards hazards)
    {
        var text = new StringBuilder();
        AppendHazard(text, hazards, Hazards.Raw, "RAW");
        AppendHazard(text, hazards, Hazards.Waw, "WAW");
        AppendHazard(text, hazards, Hazards.War, "WAR");
        return text.Length == 0 ? "None" : text.ToString();
    }

    private static string QueueText(QueueMask queues)
    {
        var text = new StringBuilder();
        AppendQueue(text, queues, QueueMask.Graphics, "Graphics");
        AppendQueue(text, queues, QueueMask.Compute, "Compute");
        AppendQueue(text, queues, QueueMask.Copy, "Copy");
        return text.Length == 0 ? "None" : text.ToString();
    }

    private static void AppendHazard(StringBuilder text, Hazards hazards, Hazards flag, string label)
    {
        if ((hazards & flag) == 0)
            return;

        if (text.Length > 0)
            text.Append("|");
        text.Append(label);
    }

    private static void AppendQueue(StringBuilder text, QueueMask queues, QueueMask flag, string label)
    {
        if ((queues & flag) == 0)
            return;

        if (text.Length > 0)
            text.Append("|");
        text.Append(label);
    }

    private static string LifetimeText(ResourceRecord record)
        => record.FirstPass >= 0
            ? $"[{record.FirstPass}..{record.LastPass}]"
            : "[root]";

    private static string RangeText(SubResourceRange range)
    {
        if (range.FirstMipLevel == 0
            && range.MipLevelCount == uint.MaxValue
            && range.FirstArraySlice == 0
            && range.ArraySliceCount == uint.MaxValue)
        {
            return "all";
        }

        return $"mip={RangePart(range.FirstMipLevel, range.MipLevelCount)} slice={RangePart(range.FirstArraySlice, range.ArraySliceCount)}";
    }

    private static string RangePart(uint first, uint count)
        => count == uint.MaxValue
            ? $"{first}..*"
            : count == 0
                ? $"{first}..<empty>"
            : $"{first}..{first + count - 1}";

    private static string RangeText(SomeEngine.Rhi.SubresourceRange range)
    {
        if (range.FirstMip == 0
            && range.MipCount == uint.MaxValue
            && range.FirstSlice == 0
            && range.SliceCount == uint.MaxValue)
        {
            return "all";
        }

        if (range.MipCount == 1 && range.SliceCount == 1)
            return $"mip={range.FirstMip} slice={range.FirstSlice}";

        return $"mip={RangePart(range.FirstMip, range.MipCount)} slice={RangePart(range.FirstSlice, range.SliceCount)}";
    }
}
