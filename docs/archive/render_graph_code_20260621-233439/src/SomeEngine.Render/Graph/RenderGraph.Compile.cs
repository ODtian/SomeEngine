using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    public void Compile(GraphQueues queues)
        => _compiler.Compile(queues);

    internal void Compile()
        => _compiler.Compile();

    public Task CompileAsync(
        bool asyncCompute = false,
        bool asyncCopy = false,
        CancellationToken cancellationToken = default)
        => _compiler.CompileAsync(asyncCompute, asyncCopy, cancellationToken);

    internal void CompileCurrentCore()
    {
        CompileCurrentCore(asyncCompute: false, asyncCopy: false);
    }

    internal void CompileCurrentCore(bool asyncCompute, bool asyncCopy)
    {
        ThrowIfDisposed();
        ThrowIfActive(nameof(Compile));
        WaitForBackgroundCompile();
        lock (_compiler.Gate)
        {
            PrepareCompile(ref asyncCompute, ref asyncCopy);
            if (HasCompile(asyncCompute, asyncCopy))
                return;

            CompiledGraph compile = CompileCore(asyncCompute, asyncCopy);
            SetCompile(compile, asyncCompute, asyncCopy);
        }
    }

    private void WaitForBackgroundCompile()
        => _compiler.WaitForBackgroundCompile();

    private void ThrowIfBackgroundCompile(string operation)
        => _compiler.ThrowIfBackgroundCompile(operation);

    private void PrepareCompile(ref bool asyncCompute, ref bool asyncCopy)
        => _compiler.PrepareCompile(ref asyncCompute, ref asyncCopy);

    private bool HasCompile(bool asyncCompute, bool asyncCopy)
        => _compiler.HasCompile(asyncCompute, asyncCopy);

    private void SetCompile(CompiledGraph compile, bool asyncCompute, bool asyncCopy)
        => _compiler.SetCompile(compile, asyncCompute, asyncCopy);

    private CompiledGraph CompileCore(bool asyncCompute, bool asyncCopy)
        => _compiler.CompileCore(asyncCompute, asyncCopy);

    private CompiledGraph CompileUncached(bool asyncCompute, bool asyncCopy)
        => _compiler.CompileUncached(asyncCompute, asyncCopy);

    private CompiledGraph CompileGraph(bool asyncCompute, bool asyncCopy)
        => _compiler.CompileGraph(asyncCompute, asyncCopy);

    private CompiledGraph UseCompile(CompiledGraph compile)
        => _compiler.UseCompile(compile);

    private GraphSchema CurrentSchema()
        => _compiler.CurrentSchema();

    private void MarkGraphChanged()
        => _compiler.MarkGraphChanged();

    private static QueueMask QueueBit(QueueType queue)
        => (QueueMask)(1 << QueueSlot(queue));

    private static bool Reads(RenderGraphAccess access)
        => (access & RenderGraphAccess.ReadOnly) != 0;

    private static bool Writes(RenderGraphAccess access)
        => (access & RenderGraphAccess.WriteOnly) != 0;

    private static bool IsWriteOnly(RenderGraphAccess access)
        => access == RenderGraphAccess.WriteOnly;

    private static bool HasBufferInitialData(RenderGraph.Resource resource)
        => resource.BufferHasInitialData
            || resource.BufferInitialData is { Length: > 0 };

    internal static RenderGraphAccess StateAccess(ResourceState state)
        => state is ResourceState.UnorderedAccess
            or ResourceState.RenderTarget
            or ResourceState.DepthWrite
            or ResourceState.CopyDestination
            or ResourceState.ResolveDestination
            ? RenderGraphAccess.WriteOnly
            : RenderGraphAccess.ReadOnly;

    private void BuildFrameSync(CompiledGraph compile)
    {
        // Queue waits are compiled before submit ordering consumes them:
        // compile.QueueWaits[targetBatch].Add(linkIndex);
        compile.FinalWork = compile.RootResolves.Count != 0 || !compile.FinalTransitions.IsEmpty;
        _compiler.BuildFrameSync(compile);
    }

    private static bool EnsureState(Resource resource, ResourceState state)
        => EnsureState(resource, state, allowTexturePresent: false);

    private static bool EnsureState(Resource resource, ResourceState state, bool allowTexturePresent)
    {
        if (resource.Kind == ResourceKind.Texture)
        {
            TextureDesc desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            BindFlags requiredFlags = RequiredBindFlags(resource.Kind, state, desc.BindFlags, allowTexturePresent);
            if ((desc.BindFlags & requiredFlags) == requiredFlags)
                return false;

            resource.TextureDesc = desc with
            {
                BindFlags = desc.BindFlags | requiredFlags,
            };
            return true;
        }

        BufferDesc bufferDesc = resource.BufferDesc
            ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
        BindFlags requiredBufferFlags = RequiredBindFlags(resource.Kind, state, bufferDesc.BindFlags, allowTexturePresent);
        if ((bufferDesc.BindFlags & requiredBufferFlags) == requiredBufferFlags)
            return false;

        resource.BufferDesc = bufferDesc with
        {
            BindFlags = bufferDesc.BindFlags | requiredBufferFlags,
        };
        return true;
    }

    private static void AddResourceSchema(List<int> values, Resource resource)
    {
        values.Add((int)resource.Kind);
        values.Add(resource.Imported ? 1 : 0);
        values.Add(resource.AllowWrite ? 1 : 0);
        values.Add((int)resource.Lifetime);
        values.Add((int)resource.CurrentState);
        values.Add(HasBufferInitialData(resource) ? 1 : 0);

        if (resource.Kind == ResourceKind.Texture)
        {
            TextureDesc desc = resource.TextureDesc
                ?? throw new InvalidOperationException($"RenderGraph texture '{resource.Name}' has no descriptor.");
            values.Add((int)desc.Dimension);
            values.Add(unchecked((int)desc.Width));
            values.Add(unchecked((int)desc.Height));
            values.Add(unchecked((int)desc.Depth));
            values.Add(unchecked((int)desc.MipLevels));
            values.Add(unchecked((int)desc.ArraySize));
            values.Add(unchecked((int)desc.SampleCount));
            values.Add((int)desc.Format);
            values.Add((int)desc.Memory);
            values.Add((int)desc.BindFlags);
            values.Add((int)desc.InitialState);
            values.Add(desc.OptimizedClearValue.HasValue ? 1 : 0);
            return;
        }

        BufferDesc bufferDesc = resource.BufferDesc
            ?? throw new InvalidOperationException($"RenderGraph buffer '{resource.Name}' has no descriptor.");
        values.Add(unchecked((int)bufferDesc.SizeInBytes));
        values.Add(unchecked((int)(bufferDesc.SizeInBytes >> 32)));
        values.Add((int)bufferDesc.Memory);
        values.Add((int)bufferDesc.BindFlags);
        values.Add(unchecked((int)bufferDesc.StrideInBytes));
        values.Add(bufferDesc.Raw ? 1 : 0);
        values.Add((int)bufferDesc.InitialState);
    }

    private static BindFlags RequiredBindFlags(
        ResourceKind kind,
        ResourceState state,
        BindFlags bindFlags,
        bool allowTexturePresent)
    {
        ResourceStateValidationResult requirement = kind == ResourceKind.Texture
            ? ResourceStateValidation.TextureRequirement(state, allowTexturePresent)
            : ResourceStateValidation.BufferRequirement(state);
        if (!requirement.IsValid)
        {
            string resourceKind = kind == ResourceKind.Texture ? "texture" : "buffer";
            throw new InvalidOperationException($"RenderGraph {resourceKind} state {state} {requirement.InvalidReason}.");
        }

        if (requirement.RequiredBindFlags == BindFlags.None)
            return BindFlags.None;

        if (!requirement.AcceptsAnyBindFlag)
            return requirement.RequiredBindFlags;

        if ((bindFlags & requirement.RequiredBindFlags) != 0)
            return BindFlags.None;

        throw new InvalidOperationException(
            $"RenderGraph buffer state {state} requires at least one read bind flag.");
    }

    internal static string ResultName(GraphHit hit)
        => hit switch
        {
            GraphHit.Recent => "Recent",
            GraphHit.Local => "Local",
            GraphHit.Shared => "Shared",
            _ => "Hit",
        };
}
