using System.Numerics;
using Friflo.Engine.ECS;
using SomeEngine.Core.Math;

namespace SomeEngine.Core.ECS.Components;

public struct LocalTransform : IComponent
{
    public TransformQvvs Value;
}

public struct WorldTransform : IComponent
{
    public Matrix4x4 Matrix; // 最终渲染通常需要矩阵
    public TransformQvvs Qvvs; // 物理或其他系统可能需要世界空间的 QVVS
}
