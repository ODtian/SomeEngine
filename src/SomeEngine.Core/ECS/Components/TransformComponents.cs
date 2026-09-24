using SomeEngine.Core.Math;
using SomeECS.Core.Components;

namespace SomeEngine.Core.ECS.Components;

public struct LocalTransform : IComponent
{
    public TransformQvvs Value;
}

public struct WorldTransform : IComponent
{
    public TransformQvvs Qvvs;
}
