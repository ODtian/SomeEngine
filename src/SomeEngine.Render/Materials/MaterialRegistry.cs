using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 全局材质注册表。管理 ShaderType（PSO 缓存）和 Material 实例。
/// </summary>
public sealed class MaterialRegistry : IDisposable
{
    private readonly Dictionary<Type, MaterialShaderType> _typeMap = new();
    private readonly List<MaterialBase> _materials = new();
    private readonly Dictionary<MaterialShaderType, List<MaterialBase>> _byShaderType = new();
    private uint _nextID;

    /// <summary>
    /// 注册材质类型 → ShaderType 映射。
    /// PSO 由管线通过 Slang 特化编译后传入。
    /// </summary>
    public MaterialShaderType RegisterShaderType<TMaterial>(
        string name,
        IPipelineState mainPSO) where TMaterial : MaterialBase, new()
    {
        var slangName = new TMaterial().SlangStructName;
        var type = new MaterialShaderType(name, slangName, mainPSO);
        _typeMap[typeof(TMaterial)] = type;
        _byShaderType[type] = new List<MaterialBase>();
        return type;
    }

    /// <summary>创建材质实例（分配 ID + SRB）。</summary>
    public TMaterial CreateMaterial<TMaterial>(string name = "")
        where TMaterial : MaterialBase, new()
    {
        if (!_typeMap.TryGetValue(typeof(TMaterial), out var shaderType))
            throw new InvalidOperationException(
                $"ShaderType not registered for {typeof(TMaterial).Name}. " +
                $"Call RegisterShaderType<{typeof(TMaterial).Name}>() first.");

        var mat = new TMaterial
        {
            MaterialID = _nextID++,
            ShaderType = shaderType,
            SRB = shaderType.CreateSRB(),
            Name = name,
        };
        _materials.Add(mat);
        _byShaderType[shaderType].Add(mat);
        return mat;
    }

    /// <summary>通过 MaterialID 查询。</summary>
    public MaterialBase? GetMaterial(uint materialID)
        => materialID < _materials.Count ? _materials[(int)materialID] : null;

    /// <summary>已注册的所有 ShaderType。</summary>
    public IReadOnlyList<MaterialShaderType> ShaderTypes
        => [.. _byShaderType.Keys];

    /// <summary>获取指定 ShaderType 下的所有材质实例。</summary>
    public IReadOnlyList<MaterialBase> GetMaterialsByShaderType(MaterialShaderType type)
        => _byShaderType.TryGetValue(type, out var list) ? list : [];

    /// <summary>当前已分配的材质数量。</summary>
    public uint MaterialCount => _nextID;

    public void Dispose()
    {
        foreach (var m in _materials) m.Dispose();
        foreach (var t in _byShaderType.Keys) t.Dispose();
        _materials.Clear();
        _byShaderType.Clear();
        _typeMap.Clear();
    }
}
