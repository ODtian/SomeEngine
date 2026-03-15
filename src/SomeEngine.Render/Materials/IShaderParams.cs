using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 可组合的 shader 参数容器接口。
/// 实现类通过 [ShaderParam] 标注字段，源生成器自动生成 ApplyToSRB()。
/// 管线参数、材质参数、可复用效果参数块都实现此接口。
/// </summary>
public interface IShaderParams
{
    void ApplyToSRB(IShaderResourceBinding srb);
}
