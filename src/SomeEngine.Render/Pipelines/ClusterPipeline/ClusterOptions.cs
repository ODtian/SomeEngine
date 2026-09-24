using System.Numerics;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterConfig
{
    public HiZMode HiZ { get; set; } = HiZMode.Phase1ThenHiZ;
    public ClusterDebugMode Debug { get; set; } = ClusterDebugMode.None;
    public bool UseVis { get; set; } = true;
    public bool BypassCull { get; set; }
    public bool UseSwRaster { get; set; } = true;
    public bool UseDeformCache { get; set; } = true;
    public ulong DeformCacheBytes { get; set; } = ClusterLimits.DefaultDeformBytes;
    public int MaterialPipelineBudget { get; set; } = 4;
    public uint DepthSliceCount { get; set; } = 16;
    public bool TemporalResolve { get; set; } = true;
    public bool TemporalJitter { get; set; } = true;
    public TemporalResolveSettings TemporalSettings { get; set; } = TemporalResolveSettings.Default;
    public Vector2 JitterPixels { get; set; }

    public bool UseHiZ()
        => HiZ != HiZMode.Phase1Only;

    public bool UsePhase2()
        => HiZ == HiZMode.Full2Phase;

    public bool BuildHiZ()
        => HiZ is HiZMode.Phase1ThenHiZ or HiZMode.Full2Phase;

    public bool ResolveDebug()
        => Debug is ClusterDebugMode.ClusterID
            or ClusterDebugMode.LODLevel
            or ClusterDebugMode.SWHWView;

    public bool UseShade()
        => UseVis && !ResolveDebug();

    public bool UseTemporal()
        => TemporalResolve && Debug == ClusterDebugMode.None;

    public bool UseDeform()
        => UseDeformCache;

    public bool UseSw()
        => UseSwRaster;

    public bool DebugSwHw()
        => Debug == ClusterDebugMode.SWHWView;

    public void Copy(ClusterConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        HiZ = source.HiZ;
        Debug = source.Debug;
        UseVis = source.UseVis;
        BypassCull = source.BypassCull;
        UseSwRaster = source.UseSwRaster;
        UseDeformCache = source.UseDeformCache;
        DeformCacheBytes = source.DeformCacheBytes;
        MaterialPipelineBudget = source.MaterialPipelineBudget;
        DepthSliceCount = source.DepthSliceCount;
        TemporalResolve = source.TemporalResolve;
        TemporalJitter = source.TemporalJitter;
        TemporalSettings = source.TemporalSettings;
        JitterPixels = source.JitterPixels;
    }
}

internal sealed class ClusterOptions
{
    private readonly ClusterConfig _edit = new();
    private readonly ClusterConfig _frame = new();
    private bool _sealed;

    public ClusterConfig Edit => _edit;

    public ClusterConfig Frame
    {
        get
        {
            if (!_sealed)
                throw new InvalidOperationException("Cluster options must be sealed before building Cluster passes.");

            return _frame;
        }
    }

    public void Seal()
    {
        _frame.Copy(_edit);
        _sealed = true;
    }
}
