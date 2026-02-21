# 项目结构

```text
d:\SomeEngine
├── .github/                 # Github配置
├── assets/                  # 资源目录
│   ├── Shaders/             # Slang着色器源文件
│   └── ...
├── docs/                    # 文档
├── src/                     # 源代码
│   ├── SomeEngine.Core/     # 核心模块
│   │   ├── ECS/             # 基于 Friflo.Engine.ECS 的扩展与封装
│   │   ├── Jobs/            # 高性能 Job System (参考 WickedEngine)
│   │   └── Math/            # 核心数学库与 QVVS 坐标系统 (参考 Latios)
│   ├── SomeEngine.Render/   # 渲染管线
│   │   ├── RHI/             # DiligentCore RHI 封装
│   │   ├── Graph/           # Render Graph 实现
│   │   ├── Pipeline/        # 渲染管线 (Cluster, HiZ, Nanite-like, VirtualShadowMap)
│   │   ├── Lighting/        # 光照系统 (PBR, RayTraced DI, Binning)
│   │   └── Utils/           # 软光栅与辅助工具
│   ├── SomeEngine.Physics/  # 物理系统 (基于 AVBD, 参考 Latios Psyshock)
│   │   ├── Collision/       # 碰撞检测
│   │   └── Solver/          # 约束解算 (GPU加速)
│   ├── SomeEngine.Animation/ # 动画系统 (参考 Latios Kinemation)
│   │   ├── Compression/     # 动画压缩
│   │   ├── Skinning/        # 蒙皮 (Linear, Quaternion)
│   │   └── Control/         # Physics Control, IK, Rigging
│   ├── SomeEngine.Assets/   # 资产管线
│   │   ├── Importers/       # 资产导入
│   │   └── Streaming/       # 流式加载管理
│   ├── SomeEngine.UI/       # UI 系统
│   │   ├── Debug/           # IMGUI 调试 UI
│   │   └── InGame/          # Avalonia 游戏内 UI 集成
│   ├── SomeEngine.Editor/   # 编辑器 (Avalonia 宿主)
│   └── SomeEngine.Runtime/  # 运行时入口与游戏循环
├── tests/                   # 单元测试与集成测试
├── samples/                 # 示例工程
├── external/                # 第三方库 (Git Submodules 或 DLLs)
├── SomeEngine.sln           # 解决方案文件
└── log.md                   # 开发日志
```
