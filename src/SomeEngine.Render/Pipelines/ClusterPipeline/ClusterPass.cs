namespace SomeEngine.Render.Pipelines;

internal enum ClusterPass
{
    Shade,
    CachedShade,
    SwRaster,
    CachedSwRaster,
    VsRaster,
    CachedVsRaster,
    PsRaster,
    DeformEval,
}
