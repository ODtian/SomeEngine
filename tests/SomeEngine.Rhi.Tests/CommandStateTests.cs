namespace SomeEngine.Rhi.Tests;

public sealed class CommandStateTests
{
    [Fact]
    public void SetBufferUpdatesExistingTransition()
    {
        var state = default(CommandState);
        try
        {
            var buffer = new BufferHandle(1, 1);

            state.SetBuffer(buffer, ResourceState.Common, ResourceState.CopyDestination);
            state.SetBuffer(buffer, ResourceState.Common, ResourceState.UnorderedAccess);

            var transition = Assert.Single(state.CopyBuffers());
            Assert.Equal(buffer, transition.Buffer);
            Assert.Equal(ResourceState.Common, transition.OriginalState);
            Assert.Equal(ResourceState.UnorderedAccess, transition.FinalState);
            Assert.True(transition.Commit);
        }
        finally
        {
            state.Dispose();
        }
    }

    [Fact]
    public void SetBufferFromUpdatesSpilledTransition()
    {
        var state = default(CommandState);
        try
        {
            for (uint id = 1; id <= 12; id++)
                state.SetBufferFrom(new BufferHandle(id, 1), ResourceState.Common, ResourceState.CopyDestination);

            var buffer = new BufferHandle(11, 1);
            state.SetBufferFrom(buffer, ResourceState.CopySource, ResourceState.UnorderedAccess);

            var transitions = state.CopyBuffers();
            Assert.Equal(12, transitions.Length);

            var transition = Assert.Single(transitions, item => item.Buffer == buffer);
            Assert.Equal(ResourceState.Common, transition.OriginalState);
            Assert.Equal(ResourceState.UnorderedAccess, transition.FinalState);
            Assert.True(transition.Commit);
        }
        finally
        {
            state.Dispose();
        }
    }

    [Fact]
    public void ApplyBufferStoresCommittedTransitionAsCurrent()
    {
        var state = default(CommandState);
        try
        {
            var buffer = new BufferHandle(1, 1);
            var transition = new BufferTransition(
                buffer,
                ResourceState.Common,
                ResourceState.CopySource,
                Commit: true);

            state.ApplyBuffer(transition, ResourceState.Common, "buffer");

            var stored = Assert.Single(state.CopyBuffers());
            Assert.Equal(ResourceState.Common, stored.OriginalState);
            Assert.Equal(ResourceState.CopySource, stored.FinalState);
            Assert.False(stored.Commit);
        }
        finally
        {
            state.Dispose();
        }
    }
}
