# Slang Integration Plan

## 目标
在 `SomeEngine` 中集成 Slang 着色器语言支持，并与现有的 Diligent Engine 渲染后端无缝协作。

## 现状分析
- **RHI**: 使用 `DiligentGraphics.DiligentEngine.Core` (DiligentCore)。
- **Bindings**: 引入 `SlangShaderSharp` (作为子模块) 提供 Slang 的 C# 绑定。
- **Asset Pipeline**: `SomeEngine.Assets` 负责资源导入。
- **Render System**: `SomeEngine.Render` 负责渲染逻辑。

## 架构设计

### 1. Slang 绑定 (`SlangShaderSharp`)
使用 [SlangShaderSharp](https://github.com/DominicMaas/SlangShaderSharp) 作为绑定库。
- **Location**: `external/SlangShaderSharp` (Submodule).
- **Strategy**:
  - 本地构建 `SlangShaderSharp` 并打包为 NuGet (`local-nugets/`)。
  - 在 `SomeEngine` 项目中引用该 NuGet 包。
- **功能**:
  - 提供完整的 Slang API 访问 (`ISlangGlobalSession`, `ICompileRequest` 等)。
  - 自动包含并加载 Native 二进制 (`slang.dll`, `slang-glslang.dll`)，支持跨平台 (win-x64, linux-x64, osx-arm64等)。

### 2. 资源导入 (`SomeEngine.Assets`)
扩展资产管线以支持 `.slang` 文件。
- **Importer**: `SlangShaderImporter`
- **Input**: `.slang` 源文件。
- **Process**:
  - 使用 `SlangShaderSharp` 编译着色器。
  - 根据目标平台生成 SPIR-V (Vulkan) 和 DXIL (D3D12) 二进制代码 (Blob)。
  - 生成反射信息 (可选，Diligent 自带反射，但 Slang 反射可辅助复杂的 Resource Binding)。
- **Output**: `ShaderAsset` (包含不同后端的字节码变体)。

### 3. 渲染集成 (`SomeEngine.Render`)
修改渲染系统以加载和使用 Slang 编译的着色器。
- **Shader Loading**:
  - `ShaderSystem` 根据当前 RHI 后端 (D3D12/Vulkan) 选择合适的 Blob。
  - 创建 `Diligent.IShader` 对象。
- **Resource Binding**:
  - 确保 Slang 的 `ParameterBlock` 和 `Binding` 模型与 Diligent 的 `PipelineResourceLayout` 兼容。
  - 建议使用 Slang 的 `[[vk::binding(x, y)]]` 或 `register(x, spacey)` 来明确绑定槽位，以匹配 Diligent 的预设。

## 实施步骤 (Tasks)

### Task 1: 引入与构建绑定 (`SlangShaderSharp`)
- [x] 添加子模块 `external/SlangShaderSharp`。
- [x] 确保 `nuget.config` 包含 `local-nugets` 源。
- [x] 构建并打包 `SlangShaderSharp` 到 `local-nugets`。
  - Command: `dotnet pack external/SlangShaderSharp/src/SlangShaderSharp.csproj -o local-nugets`
- [x] 在 `SomeEngine.Assets` 和 `SomeEngine.Render` 中引用该包。

### Task 2: 资产管线集成 (`SomeEngine.Assets`)
- [x] 创建 `SlangShaderImporter`。
- [x] 定义 `ShaderAsset` 数据结构 (支持序列化/反序列化)。
- [x] 实现编译流程：Source -> Slang Compile -> Bytecode -> Asset。

### Task 3: 渲染层支持 (`SomeEngine.Render`)
- [x] 扩展 `RenderContext` 或 `ShaderFactory` 以支持从 `ShaderAsset` 创建 `IShader`。
- [x] 编写测试用例：编译一个简单的 `.slang` 着色器并在 Diligent 中加载运行。

### Task 4: 高级特性 (Optional/Future)
- [ ] 实现热重载 (Hot Reload) 支持。
- [ ] 集成 Slang 的模块化系统 (Modules)。
- [ ] 支持 Slang 的泛型着色器 (Generics)。

## 依赖
- `SlangShaderSharp` (NuGet package from local build)
- Native binaries (Managed automatically by SlangShaderSharp package)

## 注意事项
- Diligent 对 DXIL 和 SPIR-V 的支持非常完善，直接传入 Bytecode 即可。
- 需要注意 Coordinate System 和 Matrix Layout 的差异。Slang 默认可能是 Column-Major。Diligent 也是 Column-Major。
