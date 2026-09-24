using SomeEngine.Render.Graph;

namespace SomeEngine.Render.Pipelines;

public readonly record struct FrameOutputs(
    RenderGraphHandle SceneColor,
    RenderGraphHandle PostSceneColor,
    RenderGraphHandle MotionVectors,
    RenderGraphHandle SceneDepth);
