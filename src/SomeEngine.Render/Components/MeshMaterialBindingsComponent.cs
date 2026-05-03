using Friflo.Engine.ECS;
using SomeEngine.Assets;

namespace SomeEngine.Render.Components;

public struct MeshMaterialBindings : IComponent
{
    public AssetGuid[] MaterialAssetGuids;
}
