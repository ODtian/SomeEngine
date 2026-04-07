# 项目结构

```text
f:\SomeEngine
├── .agents/                 # AI Agent 配置
├── assets/                  # 资源目录
│   └── Shaders/             # Slang 着色器源文件（~35 active .slang）
├── docs/                    # 文档（按领域组织）
│   ├── README.md            # 文档索引
│   ├── core/                # 核心模块文档
│   ├── rendering/           # 渲染管线文档
│   ├── materials/           # 材质系统文档
│   ├── assets/              # 资产管线文档
│   ├── rhi/                 # RHI 封装文档
│   ├── future/              # 远期计划文档
│   └── archive/             # 旧版归档文档
├── external/                # 第三方库（Git Submodules）
│   ├── DiligentCore/        # RHI
│   ├── Friflo.Engine.ECS/   # ECS 框架
│   ├── SlangShaderSharp/    # Slang C# 绑定
│   └── meshoptimizer/       # Mesh 优化器
├── src/                     # 源代码
│   ├── SomeEngine.Core/     # 核心模块
│   │   ├── ECS/             # Friflo ECS 扩展与封装
│   │   ├── Jobs/            # 高性能 Job System
│   │   └── Math/            # QVVS 坐标系统
│   ├── SomeEngine.Render/   # 渲染系统
│   │   ├── Graph/           # Render Graph（自动 barrier + DCE + 拓扑排序）
│   │   ├── Materials/       # 材质系统（BinQueue + BinSpace + TagStore）
│   │   ├── Pipelines/       # 渲染管线
│   │   │   └── ClusterRender/  # GPU-Driven Cluster Rendering
│   │   │       └── Stages/     # 10 个 Stage 编排类
│   │   ├── Data/            # GPU 数据结构
│   │   ├── RHI/             # DiligentCore RHI 封装
│   │   ├── Systems/         # ECS 渲染系统（Transform/Instance 同步）
│   │   └── Assets/          # 材质资产加载
│   ├── SomeEngine.Assets/   # 资产管线
│   │   ├── Importers/       # 资产导入（Slang + Mesh + ClusterBuilder）
│   │   ├── Pipeline/        # 序列化（FlatBuffer）
│   │   ├── Data/            # 数据结构（GPUCluster + MeshPageLayout）
│   │   └── Meta/            # GUID Manifest 系统
│   ├── SomeEngine.Generators/ # 源代码生成器
│   ├── SomeEngine.Editor/   # 编辑器（Avalonia 宿主）
│   ├── SomeEngine.Runtime/  # 运行时入口
│   ├── SomeEngine.UI/       # UI 系统（未启动）
│   ├── SomeEngine.Physics/  # 物理系统（未启动）
│   └── SomeEngine.Animation/ # 动画系统（未启动）
├── tests/                   # 单元测试
├── samples/                 # 示例
├── tools/                   # 工具
├── SomeEngine.slnx          # 解决方案文件
└── log.md                   # 开发日志
```
