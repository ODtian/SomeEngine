using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Frame;

public readonly record struct FrameData(
    IDevice Device,
    TextureDesc BackBufferDesc,
    TextureHandle BackBuffer,
    TextureViewHandle BackBufferView);

public readonly record struct ViewData(
    uint Width,
    uint Height);

public readonly record struct SceneTextures(
    ViewData View,
    RenderGraphHandle OutputColor,
    RenderGraphHandle SceneColor,
    RenderGraphHandle SceneDepth,
    RenderGraphHandle MotionVectors = default,
    RenderGraphHandle PostSceneColor = default,
    RenderGraphHandle HiZ = default);
