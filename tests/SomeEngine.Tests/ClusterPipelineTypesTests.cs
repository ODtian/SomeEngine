using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests;

public class ClusterPipelineTypesTests
{
    [Test]
    public void ClusterDrawConfig_CanCarryVisibleClusterMetaHandle()
    {
        var meta = new RenderGraphHandle(7);
        var config = ClusterDrawConfig.Opaque() with
        {
            VisibleClusterMeta = meta,
        };

        Assert.That(config.VisibleClusterMeta.IsValid, Is.True);
        Assert.That(config.VisibleClusterMeta.Index, Is.EqualTo(meta.Index));
    }

    [Test]
    public void ClusterDrawConfig_DefaultOpaque_DoesNotForceMetaHandle()
    {
        var config = ClusterDrawConfig.Opaque();

        Assert.That(config.VisibleClusterMeta.IsValid, Is.False);
    }
}