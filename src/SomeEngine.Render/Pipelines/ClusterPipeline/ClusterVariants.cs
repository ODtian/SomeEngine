namespace SomeEngine.Render.Pipelines;

internal static class ClusterVariants
{
    public static PassShader Sw(MaterialItem item, bool useCache)
        => useCache ? item.SwCache : item.Sw;

    public static PassShader Vs(MaterialItem item, bool useCache)
        => useCache ? item.VsCache : item.Vs;

    public static PassShader Shade(MaterialItem item, bool useCache)
        => useCache ? item.ShadeCache : item.Shade;
}
