using Friflo.Engine.ECS;
using SomeEngine.Assets;

namespace SomeEngine.Render.Components;

public struct RenderSourceEntity : IComponent
{
    public int SourceEntityId;
}

public struct RenderMaterialSlotBinding : IComponent
{
    public int InstanceIndex;
    public int LocalMaterialSlot;
    public uint BVHRootIndex;
    public float BoundsExpansion;
    public AssetGuid MaterialAssetGuid;
    public int PassIndex;
}
