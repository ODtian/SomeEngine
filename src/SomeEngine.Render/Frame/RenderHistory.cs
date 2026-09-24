using SomeEngine.Render.Graph;

namespace SomeEngine.Render.Frame;

public static class RenderHistoryNames
{
    public const string HiZ = "HiZ";
    public const string TemporalSceneColor = "TemporalSceneColor";
    public const string TemporalMotionVectors = "TemporalMotionVectors";
    public const string TemporalSceneDepth = "TemporalSceneDepth";
}

public readonly record struct RenderHistoryTexture(
    RenderGraphHandle Current,
    RenderGraphHandle Previous,
    bool HasPrevious);
