using SomeEngine.Assets.Schema;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Data;

public class MeshPageManager(ClusterResourceManager resourceManager)
{
    public void RegisterAsset(MeshAsset asset)
    {
        resourceManager.AddMesh(asset);
    }
}
