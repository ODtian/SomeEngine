# HW/SW 光栅化融合 实现计划

## 背景

SW raster 写 `DepthUAV`（R32_UINT），HiZ build 读真实 `depthTarget` → Phase1 深度无法反馈 HiZ → Phase2 occlusion cull 失效，所有三角形都在 Phase2 画。融合 HW/SW 后，大三角形走 HW，小三角形走 SW，深度统一写 R32_UINT UAV，CS 合并回 depth target 供 HiZ 使用。

## 已完成部分

### 1. VRB Per-Batch Binning（本次会话前半段）

| 文件 | 改动 |
|------|------|
| [ClusterBuilder.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/ClusterBuilder.cs) | `Array.Sort` → 稳定桶分组，保留 meshopt 顶点顺序 |
| [cluster_binning.slang](file:///f:/SomeEngine/assets/Shaders/cluster_binning.slang) | 加 `PageHeap` 绑定 + `VRBDecoded` 解码器 + Count/Scatter 发射 per-VRB-batch entries |
| [sw_raster.slang](file:///f:/SomeEngine/assets/Shaders/sw_raster.slang) | 删除 while 循环 → 单 pass，从 `binEntry.y` 读取 `[rangeStart, rangeEnd)` |
| [ClusterBinningPass.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/ClusterBinningPass.cs) | `BindSRB` + Count/Scatter pass 加 `HPageHeap` |
| [ClusterRasterBinStage.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterRasterBinStage.cs) | `AddPasses` 加 `hPageHeap` 参数 + buffer 增大 6x |
| [ClusterHiZStage.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterHiZStage.cs) | P1/P2 调用传入 `globals.PageHeap` |

### 2. Cull 阶段 SW/HW 分离

| 文件 | 改动 |
|------|------|
| [cluster_cull.slang](file:///f:/SomeEngine/assets/Shaders/cluster_cull.slang) | 新增 `EstimateScreenArea(cluster, t)` + `SW_THRESHOLD` 常量。`AppendVisible(isSW)` SW 写 DrawArgs[4] 从前、HW 写 DrawArgs[8] 从尾。`AppendVisiblePhase2(isSW)` 同理用 Phase2DrawArgs[4]/[8]。三个入口点均计算投影面积 |
| [ClusterGraphPasses.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/ClusterGraphPasses.cs) | DrawArgs 清零扩展到 5 uint，含 hwCount |

> [!NOTE]
> 上述改动均已编译通过（0 错误）。但 binning 和 dispatch 尚未适配 SW/HW 双路径，当前只有 SW 部分会被画出。

---

## 待实现部分

### 3. Binning 适配 SW/HW 双路径

#### [MODIFY] [cluster_binning.slang](file:///f:/SomeEngine/assets/Shaders/cluster_binning.slang)

当前 binning 只处理 SW 部分（per-VRB-batch entries）。需要扩展：

- **`CSBinningPrepare`**：从 DrawArgs 读取 `swCount`（DrawArgs[4]）和 `hwCount`（DrawArgs[8]），写入 `BinningUniforms`
- **`CSBinningCount`**：
  - SW 区间 `[0, swCount)`：沿用现有 per-VRB-batch 逻辑
  - HW 区间 `[maxCandidates-hwCount, maxCandidates)`：每个 cluster = 1 entry
  - `RasterBinMeta` 增加 `BinSWCount` 和 `BinHWCount` 字段
- **`CSBinningScatter`**：
  - 每个 bin 内 SW entries 从 `binOffset` 前向写，HW entries 从 `binOffset + binCapacity - 1` 后向写
- **输出格式不变**：`BinnedClusterIndexBuffer[i] = uint2(visibleIndex, packedRange)`

#### [MODIFY] [ClusterBinningPass.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/ClusterBinningPass.cs)

- `BinningUniforms` 增加 `SWCount` 和 `HWCount` 字段（从 DrawArgs 读取后 CPU 上传或在 Prepare shader 中写入）

#### [MODIFY] [ClusterRasterBinStage.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterRasterBinStage.cs)

- `ClusterRasterBinOutput` 增加 `BinnedHWDrawArgs`（HW 部分的 DrawIndirect 参数）  
- 或：在 `RasterBinMeta` 中存储 `BinSWCount`/`BinHWCount`，dispatch 时分别引用

---

### 4. HiZ Stage 统一 dispatch

#### [MODIFY] [ClusterHiZStage.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterHiZStage.cs)

- 移除 `if (UseSWRaster)` 分支
- Phase1/Phase2 内同时 dispatch：
  1. SW raster（DispatchCompute per-VRB-batch，写 VisBuffer + DepthUAV）
  2. HW raster（DrawIndexedIndirect per-bin，写同一个 VisBuffer + DepthUAV）
- 两者共享同一个 VisBuffer（R32_UINT）和 DepthUAV（R32_UINT）

---

### 5. HW Raster 改为 UAV Atomic 写入

#### [MODIFY] HW raster shader（cluster_draw vertex/pixel shader）

当前 HW raster 走真实深度 buffer。统一方案下需改为：
- Pixel shader 用 `InterlockedMax` 写 `DepthUAV`（与 SW raster 相同方式）
- 同时写 `VisBuffer`（已有 R32_UINT RT 模式）
- **不再绑定真实 depth target**（或仅用于 early-Z reject 但不写入）

> [!WARNING]
> HW raster 的 UAV atomic 写入性能可能比原生 depth test 差。Profile 后如果不可接受，可回退为 HW 写真实 depth + SW 写 DepthUAV + CS merge 方案。

---

### 6. CS Depth Merge

#### [NEW] depth_merge.slang

```
DepthUAV (R32_UINT, reversed Z bits) → DepthTarget (D32_Float)
```

- 全屏 CS，每像素读 DepthUAV → `asfloat` → 写 depth target
- 在 Phase1 Raster 后、HiZ Build 前执行
- 如果 Phase2 也需要 HiZ（Full2Phase 模式），Phase2 后再执行一次

---

### 7. Binning Dispatch 参数生成

#### [NEW or MODIFY] CSBinningFinalize

在 scatter 之后增加一步：为每个 bin 生成 HW DrawIndirect args：
```
BinnedHWDrawArgs[bin] = {
    VertexCountPerInstance = 3,           // 或三角形顶点数
    InstanceCount = RasterBinMeta[bin].BinHWCount,
    StartVertexLocation = 0,
    StartInstanceLocation = RasterBinMeta[bin].HWOffset,
}
```

SW 部分沿用现有 `BinnedDrawArgs`（DispatchCompute indirect args）。

---

## 数据布局

### VisibleClusters 数组

```
                  ← swCount →              ← hwCount →
[ SW₀ SW₁ ... SWₙ |  ... gap ...  | HWₘ ... HW₁ HW₀ ]
  ↑ front                                      back ↑
  DrawArgs[4]                              DrawArgs[8]
```

### BinnedClusterIndexBuffer（per bin）

```
BinOffset → [ SW_entry₀ ... SW_entryₙ | HW_entryₘ ... HW_entry₀ ] ← BinOffset + BinCapacity
              ↑ BinSWCount                  BinHWCount ↑
```

## 验证

1. 编译通过
2. 渲染正确（无破洞、无闪烁）
3. HiZ 正常工作（Phase1 画大部分，Phase2 仅补充）
4. Debug readback 确认 swCount/hwCount 合理分布
5. 大三角形走 HW、小三角形走 SW（调 SW_THRESHOLD 观察）
