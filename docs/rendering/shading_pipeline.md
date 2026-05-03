# Compute Shade Pipeline 设计文档

> **状态：✅ 全 Dynamic 隐式签名方案已实施（BATCH-08）**

---

## 1. 概要

VisBuffer Resolve + Compute Material Shading，参考 UE5 Nanite Compute Shade Binning。

当前管线：

`VisBuffer → ShadeBin (Count/Reserve/Scatter) → MaterialPSOGroup 分组 → Per-Bin MaterialShade Dispatch → Color`

---

## 2. Shade Binning（3-Pass Counting Sort）

### Pass 1: Count
- 全屏 CS，每线程读 VisBuffer 像素
- 查询 `MaterialSlotBuffer[offset + localMaterialIndex].ShadingBin`
- 对每个 Bin `InterlockedAdd` 累计像素数 → `BinCounts[]`

### Pass 2: Reserve
- 对 `BinCounts[]` 做前缀和
- 计算每 Bin 在全局输出数组中的起始偏移 → `BinOffsets[]`

### Pass 3: Scatter
- 再次全屏 CS，每像素按 Bin ID + `BinOffsets[]` 写入像素坐标
- 输出：紧凑的 `PixelCoordBuffer[]`，按 Bin 分段排列

### 实现文件

| 文件 | 职责 |
|------|------|
| `ClusterShadeBinningPass.cs` | ShadeBinningResources + Count/Reserve/Scatter 三个 pass 类 |
| `ClusterShade.cs` | 静态编排器，组合 ShadeBin + MaterialShade |
| `cluster_shade_binning.slang` | GPU shader |

---

## 3. Material Shade Dispatch

对每个 Shade Bin dispatch 一次 CS：

1. 读 `PixelCoordBuffer` → `(x, y)`
2. 读 VisBuffer → `VisibleClusterIndex + TriangleID`
3. 反查 PageHeap 顶点，计算重心坐标
4. 插值 UV/Normal/Tangent
5. 材质求值（PBR: BaseColor/Normal/Metallic/Roughness）
6. 写入 `OutputColor` UAV

### 全 Dynamic 隐式签名绑定（BATCH-08）

所有资源使用单一 `PipelineResourceLayoutDesc { DefaultVariableType = Dynamic }` + Diligent 隐式反射。不再有显式 `IPipelineResourceSignature`。

每个 shader group（连续同 shader 的 bin）持有 **1 个 SRB**，每次 dispatch 前通过 `Set()` 绑定所有资源（per-pass + per-material），利用 Diligent 的 ring buffer 自动回收描述符。

```
PSO 创建: per-shader-group，通过 GlobalPsoCache 缓存
SRB 创建: per-group 一次（存入 MaterialPSOGroup.SRB）
每帧执行: Set(所有资源) → CommitShaderResources → DispatchComputeIndirect
描述符回收: ring buffer 自动回收，无需手动 Dispose
```

#### Immutable Sampler

`MaterialSampler` 烘入 `PipelineResourceLayoutDesc.ImmutableSamplers`，运行时零 sampler 描述符开销。材质运行时 API 不再提供 sampler override；需要修改 sampler 时应调整管线签名/PSO 定义。

### 泛型着色管线

```slang
void CSShade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate>(
    uniform TVE vertexEval, uniform TMaterial material, uint3 tid)
```

- `IVertexEvaluate`：顶点变形（Static/Wave/Skinned）
- `ISurfaceEvaluate`：材质求值（StandardPBR/Unlit/Custom）

### 实现文件

| 文件 | 职责 |
|------|------|
| `ClusterMaterialShadePass.cs` | Per-bin dispatch，绑定 per-pass + per-material 资源到 group SRB |
| `ClusterShade.cs` | PSO 分组构建、pass 编排、`StaticPSOInit.Once` 初始化 |
| `MaterialPSOGroup.cs` | 纯 CPU break-on-change 分组逻辑 + SRB 持有 + IDisposable |
| `cluster_shade_material.slang` | 标准 PBR 着色 |
| `cluster_shade_unlit.slang` | Unlit 着色 |
| `cluster_shade_pipeline.slang` | 泛型着色管线共享代码 |
| `vertex_evaluate.slang` | IVertexEvaluate 接口 + 实现 |

---

## 4. 样板代码基础设施

### StaticPSOInit

所有 PSO 类的 `EnsureInitialized` 使用 `StaticPSOInit.Once(ref bool, Lock, Action)` 消除手写 double-check 样板。

### SRBPool

所有 PSO 类的 SRB 池使用 `SRBPool` 类封装 `ConcurrentBag<IShaderResourceBinding>`，提供 `Rent(pso)` / `Return(srb)` 接口。

---

## 5. 当前状态

- [x] ShadeBin 3-pass（Count / Reserve / Scatter）
- [x] `ClusterShade` 静态编排器替代原 `ClusterShadeBinStage` / `ClusterShadeStage`
- [x] `MaterialPSOGroup.ComputeShaderGroups()` 按 shader break-on-change 分组
- [x] 全 Dynamic 隐式签名绑定（BATCH-08：消灭 Sig0/Sig1 双签名）
- [x] Dynamic SRB per-group（BATCH-08：per-dispatch Set + Commit）
- [x] Immutable Sampler（BATCH-08：MaterialSampler 烘入 layout）
- [x] StaticPSOInit + SRBPool 样板消除（BATCH-08：覆盖全部 PSO 类）
- [ ] 1x1 vs 2x2 Quad 双模式（当前统一 1x1）
- [ ] 更多 `ISurfaceEvaluate` 实现（SSS、Refraction 等）
- [ ] Bindless 描述符索引（Dynamic SRB 为中间态）
