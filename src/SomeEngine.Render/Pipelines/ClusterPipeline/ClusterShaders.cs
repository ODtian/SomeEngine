using SomeEngine.Assets;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public readonly record struct ClusterShaders(
    Handle<Shader> BvhPatch,
    Handle<Shader> Traverse,
    Handle<Shader> Cull,
    Handle<Shader> Binning,
    Handle<Shader> Draw,
    Handle<Shader> DepthMerge,
    Handle<Shader> HiZ,
    Handle<Shader> ShadeBinning,
    Handle<Shader> Resolve,
    Handle<Shader> Temporal);
