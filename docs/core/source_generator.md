# Source Generator

## 概述

SomeEngine 使用 Roslyn Incremental Source Generator 自动生成 Shader Resource Binding (SRB) 代码，消除手写 `srb.GetVariableByName(...)` 的样板。

> **代码**：[MaterialBindingGenerator.cs](file:///f:/SomeEngine/src/SomeEngine.Generators/MaterialBindingGenerator.cs)、[MaterialTagDeserializerGenerator.cs](file:///f:/SomeEngine/src/SomeEngine.Generators/MaterialTagDeserializerGenerator.cs)

## ShaderParamsGenerator

### 触发条件

扫描所有实现 `IShaderParams` 接口的 `partial class`，收集带 `[ShaderParam]` 属性的字段。

### 生成内容

为每个匹配类生成 `ApplyToSRB(IShaderResourceBinding srb)` 方法：

```csharp
// 用户代码
public partial class MyShadeParams : IShaderParams
{
    [ShaderParam(Dynamic = true)] public TextureSlot Albedo;
    [ShaderParam(Dynamic = true)] public BufferSlot  Instances;
    [ShaderParam]                 public SamplerSlot LinearSampler;
}

// 生成代码 (MyShadeParams_ShaderParams.g.cs)
partial class MyShadeParams
{
    public void ApplyToSRB(IShaderResourceBinding srb)
    {
        if (Albedo.View is not null)
            srb.GetVariableByName(ShaderType.Compute, "Albedo")
                ?.Set(Albedo.View, SetShaderResourceFlags.None);

        if (Instances.View is not null)
            srb.GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(Instances.View, SetShaderResourceFlags.None);
        else if (Instances.Buffer is not null)
            srb.GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(Instances.Buffer, SetShaderResourceFlags.None);

        if (LinearSampler.Sampler is not null)
            srb.GetVariableByName(ShaderType.Compute, "LinearSampler")
                ?.Set(LinearSampler.Sampler, SetShaderResourceFlags.AllowOverwrite);
    }
}
```

### 支持的 Slot 类型

| SlotKind | C# 类型 | 绑定方式 |
|---|---|---|
| Texture | `TextureSlot` | `View` → SRV |
| Buffer | `BufferSlot` | `View` → SRV/UAV, fallback `Buffer` → CBV |
| Sampler | `SamplerSlot` | `Sampler` |

### ShaderParam 属性参数

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `name` (positional) | string | 字段名 | shader 中的变量名 |
| `Stage` | ShaderType | Compute | 目标 shader stage |
| `Dynamic` | bool | false | true → `None` flags, false → `AllowOverwrite` |

### 组合委托

如果 `IShaderParams` 类包含其他 `IShaderParams` 类型的字段（且未标 `[ShaderParam]`），自动生成委托调用：

```csharp
composedField.ApplyToSRB(srb);
```

## MaterialTagDeserializerGenerator

为 MaterialTag 相关类型生成反序列化逻辑（从 JSON/binary 恢复 Tag 实例）。

> **代码**：[MaterialTagDeserializerGenerator.cs](file:///f:/SomeEngine/src/SomeEngine.Generators/MaterialTagDeserializerGenerator.cs)
