using System;
using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 材质资产层 + 运行时 source。
/// </summary>
public class Material : IDisposable
{
    public AssetGuid AssetGuid { get; set; }

    /// <summary>名称（调试/序列化用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>所有贴图与 Buffer 参数。</summary>
    public ShaderParamBag Params { get; private set; } = new();

    public MaterialScalarRegionLayout ScalarRegionLayout { get; private set; } = MaterialScalarRegionLayout.Empty;

    public int ScalarRegionByteSize => ScalarRegionLayout.ByteSize;

    /// <summary>该材质拥有的所有 pass 实体。</summary>
    public Entity[] PassEntities { get; internal set; } = [];

    internal EntityStore? PassStore { get; set; }

    /// <summary>Revision used by extract/prepare caches to detect material-side changes.</summary>
    public uint Version { get; private set; } = 1;

    public void Touch()
    {
        unchecked
        {
            Version++;
        }
    }

    /// <summary>
    /// 克隆此材质。新实例 Params 独立副本，共享底层 GPU 资源引用。
    /// </summary>
    public Material Instantiate()
    {
        var passStore = PassStore ?? throw new InvalidOperationException("Material is not attached to a pass entity store.");
        var inst = new Material
        {
            AssetGuid = AssetGuid,
            Name = Name + " (Instance)",
            Params = Params.Clone(),
            ScalarRegionLayout = ScalarRegionLayout,
        };

        if (PassEntities.Length > 0)
        {
            Entity[] clonedPasses = new Entity[PassEntities.Length];
            for (int i = 0; i < PassEntities.Length; i++)
            {
                Entity target = passStore.CreateEntity();
                Entity source = PassEntities[i];
                if (!source.IsNull)
                {
                    EntityStore.CopyEntity(source, target);
                }

                target.AddComponent(new Pipelines.MaterialRef { Owner = inst });
                clonedPasses[i] = target;
            }

            inst.PassEntities = clonedPasses;
        }
        else
        {
            var entity = passStore.CreateEntity();
            entity.AddComponent(new Pipelines.MaterialRef { Owner = inst });

            inst.PassEntities = [entity];
        }
        inst.PassStore = passStore;
        inst.Touch();

        return inst;
    }

    public void SetTexture(string name, ITextureView? view)
    {
        Params.Set(name, view);
        Touch();
    }

    public void SetScalarRegionLayout(MaterialScalarRegionLayout layout)
    {
        ScalarRegionLayout = layout ?? MaterialScalarRegionLayout.Empty;
        Touch();
    }

    public void WriteScalarRegion(Span<byte> destination)
        => ScalarRegionLayout.Write(Params, destination);

    /// <summary>设置 Buffer 参数。</summary>
    public void SetBuffer(string name, IBufferView? view)
    {
        Params.Set(name, view);
        Touch();
    }

    public void Dispose()
    {
        PassStore = null;

        if (PassEntities.Length > 0)
        {
            foreach (Entity passEntity in PassEntities)
            {
                if (!passEntity.IsNull && passEntity.StoreOwnership == StoreOwnership.attached)
                {
                    passEntity.DeleteEntity();
                }
            }
        }

        Params.Dispose();
    }
}
