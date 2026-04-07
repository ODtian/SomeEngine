using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests;

public class ClusterPipelineTypesTests
{
    [Fact]
    public void ClusterDrawConfig_CanCarryVisibleClusterMetaHandle()
    {
        var meta = new RenderGraphHandle(7);
        var config = ClusterDrawConfig.Opaque() with
        {
            VisibleClusterMeta = meta,
        };

        Assert.True(config.VisibleClusterMeta.IsValid);
        Assert.Equal(meta.Index, config.VisibleClusterMeta.Index);
    }

    [Fact]
    public void ClusterDrawConfig_DefaultOpaque_DoesNotForceMetaHandle()
    {
        var config = ClusterDrawConfig.Opaque();

        Assert.False(config.VisibleClusterMeta.IsValid);
    }
}