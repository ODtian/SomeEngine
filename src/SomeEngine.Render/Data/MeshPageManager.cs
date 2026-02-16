using SomeEngine.Assets.Schema;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Data;

public class MeshPageManager
{
    private readonly ClusterResourceManager _resourceManager;

    public MeshPageManager(ClusterResourceManager resourceManager)
    {
        _resourceManager = resourceManager;
    }

    public void RegisterAsset(MeshAsset asset)
    {
        _resourceManager.AddMesh(asset);
        _resourceManager.CommitPageTable();
    }
}
