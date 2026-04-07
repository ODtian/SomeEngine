# Diligent 全平台 Binding-Based Shader 资源绑定

## 背景

Slang 编译器修饰（mangle）shader 变量名。编译后 bytecode 中的变量名与 Slang 反射 API 给出的原始名不一致，但 **(binding, set/space/group)** 在两边一致——是唯一稳定标识符。

Diligent 当前全链路依赖**名字匹配**，需要增加一条完全独立的 **binding-based** 路径，零字符串开销。

## 设计原则

- **Signature 级全有或全无**：所有 Resource 都有 Register+Space → BindMode；否则 → NameMode
- **三元组 key**：`(Register, Space, ResourceType)`。D3D12 的 `t0`/`s0`/`b0`/`u0` 共享 register=0 但 type 不同
- **零字符串开销**：BindMode 下无 strcmp、无 string hash、无 TMap 构建
- **完全向后兼容**：`Register = ~0u` 表示未指定，走原有名字逻辑

## 语义映射

| 概念 | D3D12 | Vulkan | WebGPU | OpenGL |
|------|-------|--------|--------|--------|
| Register | ShaderRegister (t/s/b/u) | binding | BindIndex | binding layout |
| Space | RegisterSpace | descriptor set | BindGroup | 0 (固定) |

## 改动清单

### 1. 公共接口/结构

#### `PipelineResourceSignature.h` — `PipelineResourceDesc`

```diff
 struct PipelineResourceDesc
 {
     const Char*          Name          DEFAULT_INITIALIZER(nullptr);
+    Uint32               Register      DEFAULT_INITIALIZER(~0u);
+    Uint32               Space         DEFAULT_INITIALIZER(~0u);
     SHADER_TYPE          ShaderStages  DEFAULT_INITIALIZER(SHADER_TYPE_UNKNOWN);
     SHADER_RESOURCE_TYPE ResourceType  ...;
     ...
 };
```

#### `Shader.h` — `ShaderResourceDesc`

```diff
 struct ShaderResourceDesc
 {
     const Char*          Name      = nullptr;
     SHADER_RESOURCE_TYPE Type      = SHADER_RESOURCE_TYPE_UNKNOWN;
     Uint32               ArraySize = 0;
+    Uint32               Register  = ~0u;
+    Uint32               Space     = ~0u;
 };
```

#### `ShaderResourceBinding.h` — 新增 API

```cpp
virtual IShaderResourceVariable* DILIGENT_CALL_TYPE GetVariableByBinding(
    SHADER_TYPE          ShaderType,
    SHADER_RESOURCE_TYPE ResourceType,
    Uint32               Register,
    Uint32               Space) = 0;
```

### 2. 核心基类

#### `PipelineResourceSignatureBase.hpp`

- 新增 `bool m_UseBindingBasedMatching` flag，创建时自动判定
- 新增 `FindResourceByBinding(ShaderStage, ResourceType, Register, Space)`：线性扫描 `m_Desc.Resources[]` 做整数比较
- `FindResource(Stage, Name)` 不变

#### `PipelineResourceSignatureBase.cpp`

- `ValidatePipelineResourceSignatureDesc()`：BindMode 下放宽 `Name != nullptr` 校验

#### `PipelineStateBase.hpp`

- `GetResourceAttribution()` 新增重载，接受 `(Register, Space, ResourceType)` 参数
- 各后端 PSO 创建 `ValidateShaderResources` 中：先按 Name 查，失败时用 bytecode 反射的 (BindPoint, Space, ResType) 走新重载

#### `ShaderResourceBindingBase.hpp`

- 实现 `GetVariableByBinding`：同 `GetVariableByName` 的 stage→MgrInd 逻辑，调 `ShaderVariableManager::GetVariable(Type, Register, Space)`

#### `ShaderResourceVariableBase.hpp`

- `GetResourceDesc()` 填充 `Register`/`Space`

### 3. D3D12 后端

#### `PipelineResourceSignatureD3D12Impl.cpp`

- `UpdateShaderResourceBindingMap()`：BindMode 下**跳过** TMap 构建（不需要字符串 map）

#### `PipelineStateD3D12Impl.cpp`

- `ValidateShaderResources()`：`GetResourceAttribution(Attribs.Name, ShaderType)` 失败时，fallback 到 `GetResourceAttribution(Attribs.BindPoint, Attribs.Space, Attribs.GetShaderResourceType(), ShaderType)`

#### `DXCompiler.cpp` — `RemapResourceBindings()`

- BindMode 下改用 `GetResourceBindingDesc(Index)` 按索引遍历 D3D12 反射（而非 `GetResourceBindingDescByName`）
- 构建 `ExtResMap` 时由 `(SrcBindPoint, SrcSpace, ResType)` 匹配 Signature 资源
- 下游 `PatchResourceDeclaration` 已经用 `(SrcBindPoint, SrcSpace, ResType)` 匹配，无需修改
- `PatchResourceDeclarationRT`（RT shader）仍依赖名字搜 DXIL 文本，暂不支持

#### `ShaderVariableManagerD3D12.hpp/.cpp`

- 新增 `GetVariable(SHADER_RESOURCE_TYPE Type, Uint32 Register, Uint32 Space)` 重载

### 4. Vulkan 后端

#### `PipelineStateVkImpl.cpp`

- `GetResourceAttribution` 调用处添加 binding/set fallback

#### `ShaderVariableManagerVk.hpp/.cpp`

- 新增 `GetVariable(Type, Register, Space)` 重载

### 5. WebGPU 后端

#### `PipelineStateWebGPUImpl.cpp`

- `GetResourceAttribution` 调用处添加 BindIndex/BindGroup fallback

#### `ShaderVariableManagerWebGPU.hpp/.cpp`

- 新增 `GetVariable(Type, Register, Space)` 重载

### 6. OpenGL 后端

#### `PipelineStateGLImpl.cpp`

- `GetResourceAttribution` 调用处添加 binding/0 fallback

#### `ShaderVariableManagerGL.hpp/.cpp`

- 新增 `GetVariable(Type, Register, Space)` 重载

### 7. .NET Binding

- `PipelineResourceDesc`、`ShaderResourceDesc` struct 改动需重新生成 SharpGen bindings
- `IShaderResourceBinding` 接口新增 `GetVariableByBinding` 方法

## BindMode 开销对比

| 环节 | NameMode (现有) | BindMode (新) |
|------|----------------|---------------|
| FindResource | strcmp × N | uint32 比较 × N |
| GetResourceAttribution | FindResource(Name) | FindResourceByBinding(Reg, Space, Type) |
| D3D12 TMap | `unordered_map<string, BindInfo>` | 跳过 |
| D3D12 RemapResourceBindings | `GetResourceBindingDescByName` | `GetResourceBindingDesc(Index)` |
| D3D12 DXIL patching | 已用 (BindPoint, Space, Type) | 同上 |
| SRB GetVariable | strcmp × N | uint32 比较 × N |
| PipelineResourceDesc.Name | 必须非空 | 可为空 (debug label) |

## 限制

- **RT shader 不支持 BindMode**：`PatchResourceDeclarationRT` 依赖名字搜 DXIL 文本
- **全有或全无**：一个 Signature 内所有资源必须同时提供 Register+Space，不支持混合
- **多 Signature + BaseRegisterSpace**：BindMode 下用户提供的 Space 会被 Diligent 加上 BaseRegisterSpace 偏移，需确保用户理解

## 验证计划

1. 编译 DiligentCore 全平台
2. 现有 `PipelineResourceSignatureBaseTest.cpp` 中增加 BindMode 测试
3. SomeEngine 中使用 Slang 反射的 (binding, set) 创建 Signature + SRB 绑定
