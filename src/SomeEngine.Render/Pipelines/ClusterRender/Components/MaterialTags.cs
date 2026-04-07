using Friflo.Engine.ECS;

namespace SomeEngine.Render.Pipelines;

public struct Opaque : ITag;

public struct Masked : ITag;

public struct Translucent : ITag;

public struct TwoSided : ITag;
