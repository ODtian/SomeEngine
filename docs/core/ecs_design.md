# ECS 设计

## 概述

SomeEngine 的 ECS 基于 [**friflo Engine ECS**](https://github.com/friflo/Friflo.Engine.ECS)，一个高性能的 Archetype-based Entity Component System。

> **Authoring note:** 通用的 authoring/runtime/extractor 关系见 [ecs_authoring.md](ecs_authoring.md)。

## 核心类型

### GameWorld

全局入口，持有 `EntityStore`、`SystemRoot`、`SystemContext`。每帧调用 `Update(deltaTime)` 驱动所有 System。

> **代码**：[GameWorld.cs](file:///f:/SomeEngine/src/SomeEngine.Core/ECS/GameWorld.cs)

### Components

| Component | 用途 | 字段 |
|---|---|---|
| `LocalTransform` | 实体局部变换 | `TransformQvvs Value` |
| `WorldTransform` | 计算后的世界变换 | `Matrix4x4 Matrix`, `TransformQvvs Qvvs` |
| `TransformDepth` | 层级深度（HierarchySystem 维护） | `int Value` |
| `MeshInstanceComponent` | Mesh 实例引用 | `AssetGuid MeshAssetGuid` |
| `MaterialOverrideComponent` | 材质覆盖 | `AssetGuid MaterialAssetGuid` |
| `HierarchyMarker` | 层级标记 | (tag component) |

### Systems

#### HierarchySystem

维护 `TransformDepth`：
1. 为缺少 `TransformDepth` 的 `LocalTransform` 实体补 depth=0
2. 孤立实体（无 TreeNode）→ depth=0
3. 树根节点递归设置子节点深度

#### TransformSystem

按 depth 分层并行计算 `WorldTransform`：
- depth=0 → `WorldTransform = LocalTransform`
- depth>0 → `WorldTransform = Combine(parent.World, local)`

使用 friflo 的 `IJobChunk` 批量并行处理每个 depth 层级。

## 数据流

```
LocalTransform (用户设置)
    ↓ HierarchySystem (维护 depth)
TransformDepth
    ↓ TransformSystem (按 depth 并行)
WorldTransform.Matrix → GPU GpuTransform buffer
```

## 和渲染管线的连接

`ClusterUploadStage` 从 ECS 查询 `WorldTransform` + `MeshInstanceComponent`，构建 `GpuTransform[]` 和 `GpuInstanceHeader[]` 上传到 GPU。
