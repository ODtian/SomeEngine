using System.Numerics;
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

    [Fact]
    public void ClusterDebugMode_SWHWView_MatchesResolveShaderMode()
    {
        Assert.Equal(3u, (uint)ClusterDebugMode.SWHWView);
    }

    [Fact]
    public void ClusterLimits_MaxBinnedEntriesPerCluster_CoversEncodedAndSlowVrbBatches()
    {
        uint slowTriangles = ClusterLimits.MaxClusterTriangles - ClusterLimits.MaxEncodedVRBBatches;
        uint expected = ClusterLimits.MaxEncodedVRBBatches + ((slowTriangles + 31) / 32);

        Assert.Equal(9u, expected);
        Assert.Equal(ClusterLimits.MaxBinnedEntriesPerCluster, expected);
    }

    [Fact]
    public void CullingUniforms_Create_WritesScreenSizeForSWHWSplit()
    {
        var uniforms = CullingUniforms.Create(
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            lodThreshold: 1.0f,
            lodScale: 500.0f,
            forcedLODLevel: -1,
            instanceCount: 1,
            bypassCulling: false,
            dumpHiZData: false,
            debugShowHiZAABBs: false,
            Matrix4x4.Identity,
            hasPrevHistory: false,
            hizMipCount: 0,
            hizInvSize: Vector2.Zero,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            quantStep: 1.0f,
            screenWidth: 1280,
            screenHeight: 720);

        Assert.Equal(1280u, uniforms.ScreenWidth);
        Assert.Equal(720u, uniforms.ScreenHeight);
    }
}
