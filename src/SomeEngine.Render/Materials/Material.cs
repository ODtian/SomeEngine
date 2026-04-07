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

    /// <summary>所有贴图/采样器参数。</summary>
    public ShaderParamBag Params { get; private set; } = new();

    /// <summary>唯一的渲染身份。1 Material = 1 Entity。</summary>
    public Entity Entity { get; internal set; }

    internal MaterialSystem? System { get; set; }

    /// <summary>
    /// 克隆此材质。新实例 Params 独立副本，共享底层 GPU 资源引用。
    /// </summary>
    public Material Instantiate()
    {
        var materialSystem = System ?? throw new InvalidOperationException("Material is not attached to a MaterialSystem.");
        var inst = new Material
        {
            AssetGuid = AssetGuid,
            Name = Name + " (Instance)",
            Params = Params.Clone(),
        };

        var entity = materialSystem.Store.CreateEntity();
        if (!Entity.IsNull && Entity.StoreOwnership == StoreOwnership.attached)
        {
            MaterialEntityUtility.CloneMaterialIdentity(inst, Entity, entity);
        }
        else
        {
            entity.AddComponent(new Pipelines.MaterialRef { Owner = inst });
        }

        inst.Entity = entity;
        inst.System = materialSystem;

        return inst;
    }

    public void SetTexture(string name, ITextureView? view)
    {
        Params.Set(name, view);
    }

    /// <summary>设置采样器参数。</summary>
    public void SetSampler(string name, ISampler? sampler)
    {
        Params.Set(name, sampler);
    }

    /// <summary>设置 Buffer 参数。</summary>
    public void SetBuffer(string name, IBufferView? view)
    {
        Params.Set(name, view);
    }

    public void Dispose()
    {
        System = null;

        if (!Entity.IsNull && Entity.StoreOwnership == StoreOwnership.attached)
        {
            Entity.DeleteEntity();
        }

        Params.Dispose();
    }
}
