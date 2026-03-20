using System;
using System.Collections.Generic;
using Diligent;
using SomeEngine.Assets;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 材质注册表。封装 TagStore&lt;MaterialPass&gt;，管理 Material 注册/注销和 Tag 查询。
/// <para>
/// 注册 Material 时，为每个 MaterialPass 分配全局唯一 MaterialID。
/// </para>
/// </summary>
public sealed class MaterialRegistry : IDisposable
{
    private readonly TagStore<MaterialPass> _tagStore = new();
    private readonly List<Material> _materials = new();
    private readonly Dictionary<uint, MaterialPass> _passById = new();
    private uint _nextMaterialId;

    /// <summary>已注册 MaterialPass 总数。</summary>
    public uint MaterialCount => (uint)_passById.Count;

    /// <summary>Tag 版本号。</summary>
    public uint Version => _tagStore.Version;



    /// <summary>所有已注册的 Material。</summary>
    public IReadOnlyList<Material> Materials => _materials;

    /// <summary>
    /// 注册 Material。为其每个 MaterialPass 分配 ID 并注册到 TagStore。
    /// </summary>
    public void Register(Material material)
    {
        _materials.Add(material);

        foreach (var pass in material.Passes)
        {
            pass.MaterialID = _nextMaterialId++;
            _passById[pass.MaterialID] = pass;
            _tagStore.Register(pass);

            // 自动从 Shader 推导管线 tag
            if (pass.Shader?.Metadata?.PipelineTags != null)
            {
                foreach (var tagName in pass.Shader.Metadata.PipelineTags)
                {
                    MaterialTagResolver.ApplyTag(this, pass, tagName, 0);
                }
            }
        }

        // 自动推导多 Pass tag

        if (material.Passes.Length > 1)
        {
            var primary = material.Passes[0];
            _tagStore.SetTag(primary, new MultiPassTag
            {
                OverlayCount = (byte)(material.Passes.Length - 1)
            });

            for (int i = 1; i < material.Passes.Length; i++)
            {
                _tagStore.SetTag(material.Passes[i], new OverlayTag
                {
                    LayerIndex = (byte)(i - 1),
                    PrimaryPass = primary,
                });
            }
        }
    }



    /// <summary>
    /// 注销 Material。移除其所有 MaterialPass。
    /// </summary>
    public void Unregister(Material material)
    {

        foreach (var pass in material.Passes)
        {
            _tagStore.Unregister(pass);
            _passById.Remove(pass.MaterialID);
        }
        _materials.Remove(material);
    }

    /// <summary>按 ID 查找 MaterialPass。</summary>
    public MaterialPass? GetPass(uint materialId)
    {
        return _passById.TryGetValue(materialId, out var pass) ? pass : null;
    }

    /// <summary>按名称查找 Material。</summary>
    public Material? GetMaterial(string name)
    {
        foreach (var mat in _materials)
        {
            if (mat.Name == name) return mat;
        }
        return null;
    }

    // ── Tag API（委托给 TagStore） ──

    public void SetTag<TTag>(MaterialPass pass, TTag value = default!) where TTag : struct, IMaterialTag
        => _tagStore.SetTag(pass, value);

    public TTag? GetTag<TTag>(MaterialPass pass) where TTag : struct, IMaterialTag
        => _tagStore.GetTag<TTag>(pass);

    public bool HasTag<TTag>(MaterialPass pass) where TTag : struct, IMaterialTag
        => _tagStore.HasTag<TTag>(pass);

    public MaterialPass[] Query<T1>() where T1 : struct, IMaterialTag
        => _tagStore.Query<T1>();

    public MaterialPass[] Query<T1, T2>() where T1 : struct, IMaterialTag where T2 : struct, IMaterialTag
        => _tagStore.Query<T1, T2>();

    /// <summary>获取所有 MaterialPass。</summary>
    public MaterialPass[] GetAllPasses() => _tagStore.GetAll();

    public bool RemoveTag<TTag>(MaterialPass pass) where TTag : struct, IMaterialTag
        => _tagStore.RemoveTag<TTag>(pass);

    public void CopyAllTags(MaterialPass from, MaterialPass to)
        => _tagStore.CopyAllTags(from, to);

    public void RemoveAllTags(MaterialPass pass)
        => _tagStore.RemoveAllTags(pass);

    public void Dispose()
    {
        foreach (var mat in _materials)
            mat.Dispose();
        _materials.Clear();
        _passById.Clear();

    }
}
