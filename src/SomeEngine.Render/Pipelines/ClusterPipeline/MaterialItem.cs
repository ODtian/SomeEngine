using SomeEngine.Assets;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct MaterialItem
{
    public Handle<Material> Handle { get; init; }
    public Material? Material { get; init; }
    public uint PassVersion { get; init; }
    public uint BindingVersion { get; init; }
    public MaterialState State { get; init; }
    public PassShader Shade { get; init; }
    public PassShader ShadeCache { get; init; }
    public PassShader Sw { get; init; }
    public PassShader SwCache { get; init; }
    public PassShader Vs { get; init; }
    public PassShader VsCache { get; init; }
    public PassShader Ps { get; init; }
    public PassShader Deform { get; init; }

    public bool HasRaster(bool cache)
        => !(cache ? SwCache : Sw).IsEmpty
            || (!(cache ? VsCache : Vs).IsEmpty && !Ps.IsEmpty);

    public bool HasShade(bool cache)
        => !(cache ? ShadeCache : Shade).IsEmpty;

    public bool HasDeform()
        => !Deform.IsEmpty;

    public bool HasCache()
        => !ShadeCache.IsEmpty
            || !SwCache.IsEmpty
            || !VsCache.IsEmpty;

    public MaterialState RasterState(bool cache)
        => State;

    public MaterialState DrawState(bool cache)
        => State;

    public MaterialState ShadePassState(bool cache)
        => State;

    public float BoundsExpansion()
        => State.BoundsExpansion;
}
