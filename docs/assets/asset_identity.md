# 资产身份与来源

本文只记录当前实现中的事实来源。历史迁移说明放入 archive 或任务记录，不再作为运行时设计依据。

## 核心规则

| 数据 | 唯一事实来源 | 派生/校验 |
|---|---|---|
| Source 身份 | `<source>.meta` 中的 `SourceGuid` | manifest 只建立 SourceGuid 到路径的索引 |
| Import 产物身份 | `AssetGuid.FromSource(SourceGuid, SubAssetKey)` | `.asset` payload 和 `.asset.meta` 必须与该值一致 |
| 手写 asset 身份 | `.asset` payload 中的 `AssetGuid` | `AssetDatabase.Import()` 注册进 manifest |
| 运行时查找 | `AssetManifest` | `AssetDatabase.Load<T>(guid)` 会校验 payload guid 与 manifest guid |
| 导入过期判断 | `AssetMeta.ContentFingerprint + Dependencies + ImporterVersion` | 不再用旧 meta guid 决定 import 产物身份 |
| MaterialInstance parent | `MaterialInstanceAsset.ParentGuid` | loader 在加载边界把 parent GUID 转成 `Handle<Material>` |
| Mesh 材质绑定 | ECS authoring 的 `MeshMaterialBindings.Materials[]` | 数组下标就是 canonical local material slot |
| RenderWorld 材质提交 | `RenderMaterials.Materials[]` | `InstanceHeaderLayout.SlotOffset` 是 pipeline prepare 阶段派生结果 |
| Transform | `LocalTransform -> TransformSystem -> WorldTransform` | `TransformQvvs` 是数学值，不是 ECS component |

## AssetGuid

Source importer 产物的 AssetGuid 只由二元组决定：

```text
AssetGuid = AssetGuid.FromSource(SourceGuid, SubAssetKey)
```

这意味着重导入、移动源文件、修改内容、重建 `.asset.meta` 都不会改变产物 GUID。旧 `.asset.meta` 中的 GUID 只用于一致性校验；如果它与确定性 GUID 不一致，importer 会重新生成产物和 meta。

手写 `.material.asset` / `.materialinstance.asset` 等资产不经过 source importer。它们的身份来自 payload 自身，必须显式 `Import()` 后进入 manifest。

## Load 校验

`AssetDatabase.Load<T>(guid)` 的加载顺序：

```text
guid -> AssetManifest record -> provider.Create(guid, path) -> payload guid check -> typed cache
```

如果 provider 返回的对象实现 `IAsset`，payload 内的 `AssetGuid` 必须等于请求的 manifest guid。这样 manifest 与 asset payload 不会各自成为事实来源。

## Meta 分工

`SourceMeta`：

- 保存源文件稳定身份 `SourceGuid`
- 保存 importer 名称和 importer settings
- 提交到 VCS

`AssetMeta`：

- 保存 `SourceGuid`
- 保存 `SubAssetKey`
- 保存 `ContentFingerprint`
- 保存依赖文件 hash
- 保存 importer version
- 保存 AssetGuid 的一致性副本

`AssetMeta` 不再决定导入产物的 AssetGuid。它的职责是增量判断和校验。

## Manifest 分工

manifest 是运行时和工具查询索引：

- `AssetGuid -> AssetManifestRecord`
- `SourceGuid -> source path`
- `SourceGuid + SubAssetKey -> AssetGuid`
- `AssetGuid -> dependency AssetGuid[]`

manifest 不反推 GUID，不修正 payload，也不替代 `.meta` 做导入过期判断。

## Material Authoring

材质资产与 mesh 几何资产不互相持有运行时绑定关系：

```text
MeshAsset
  └─ geometry + region metadata

MaterialAsset
  └─ root params + pass entity snapshots

GameWorld source entity
  ├─ MeshInstance
  └─ MeshMaterialBindings.Materials[]
```

`MeshMaterialBindings.Materials[i]` 的 `i` 就是 local material slot。RenderWorld extract 会把 source entity 的 material handle 列表复制到 `RenderMaterials.Materials`，cluster material owner 再按 material 的 `MaterialPass` 展开 raster/shade/deform bins。

Cluster prepare 阶段按 instance 聚合 local slots，写入 pipeline-owned `SlotBuffer`，再把起始位置写入 instance header 的 `InstanceHeaderLayout.SlotOffset`。GPU header 是提交派生数据，不是 authoring 数据。

## Transform Authoring

ECS 中不再直接挂 `TransformQvvs`：

```text
LocalTransform.Value: TransformQvvs
        ↓ TransformSystem
WorldTransform.Qvvs / WorldTransform.Matrix
        ↓ InstanceSyncSystem
GpuTransform
```

`InstanceSyncSystem` 只读取 `WorldTransform + MeshInstance`。如果它挂在 `GameWorld.SystemRoot` 中，应传入同一个 `SystemContext`，以便在读取前完成 transform job 依赖。
