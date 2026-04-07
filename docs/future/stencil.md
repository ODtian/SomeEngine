# Stencil 支持设计

## 1. 背景

当前管线无 Stencil 支持：
- HW 路径 PSO 的 `StencilEnable = false`
- DSV 格式为 `D32_Float`（无 Stencil 位）
- SW 路径（Phase 3.5 后）Depth 存于 UAV，无硬件 Stencil

典型用例：描边（outline）、区域遮罩、贴花剔除、portal stencil。

## 2. 方案 S1：HW Stencil（短期推荐）

### DSV 格式切换

`D32_Float` → `D24_UNorm_S8_UInt`

- 深度精度 32 → 24 bit，对远距离略有影响但通常可接受
- 获得 8-bit Stencil 通道

### Stencil PSO 变体

在 `ClusterDrawPass.cs` 新增 PSO：

```csharp
// Stencil Write PSO
ci.GraphicsPipeline.DepthStencilDesc.StencilEnable = true;
ci.GraphicsPipeline.DepthStencilDesc.FrontFace.StencilPassOp = StencilOp.Replace;
ci.GraphicsPipeline.DepthStencilDesc.FrontFace.StencilFunc = ComparisonFunction.Always;
ci.GraphicsPipeline.DSVFormat = TextureFormat.D24_UNorm_S8_UInt;

// Stencil Test PSO
ci.GraphicsPipeline.DepthStencilDesc.FrontFace.StencilFunc = ComparisonFunction.Equal;
```

通过 `SetFrameData` 新增 `StencilMode` 枚举控制。

### 编排

```
Pre-Z + Stencil Write Pass (标记特定 Instance/Material)
→ Main Raster Pass with Stencil Test
→ Shade
```

### 新增文件

- `ClusterStencilConfig.cs`：Stencil 参数配置（ref 值、compare func、pass/fail op）

### 限制

仅 HW 路径可用。SW 路径无法访问 HW Stencil。

## 3. 方案 S2：软件 Stencil（长期推荐）

### 设计

额外 `RWTexture2D<uint>` 作为 Stencil UAV：

```slang
RWTexture2D<uint> StencilUAV;  // 8-bit stencil per pixel

// Stencil Write
StencilUAV[pixel] = stencilRef;

// Stencil Test
if (StencilUAV[pixel] != stencilRef) discard;
```

### 优势

- SW 和 HW 路径均可写入和测试
- 与 Phase 4 统一 UAV Depth 架构一致
- 不降低深度精度（DSV 保持 D32_Float 或完全移除 HW DSV）
- Stencil 操作在 CS 中完全可编程

### 整合

Stencil 操作作为可选 flag 编入 `IWaveTask` 或 raster 核心：

```slang
// SW Raster 中
if (passesDepthTest)
{
    if (stencilMode == STENCIL_WRITE)
        StencilUAV[pixel] = stencilRef;
    else if (stencilMode == STENCIL_TEST && StencilUAV[pixel] != stencilRef)
        return;  // stencil fail, skip write
    
    VisBuffer[pixel] = visData;
}
```

### 何时迁移

Phase 4 完成后，管线统一使用 UAV Depth + UAV VisBuffer + UAV Stencil，三者完全对等。此时方案 S1 自然过渡到 S2。

## 4. 推荐路线

| 阶段 | 方案 | 理由 |
|------|------|------|
| Phase 3 完成后 | S1（HW Stencil） | 最小侵入，直接改 PSO |
| Phase 4 完成后 | S2（软件 Stencil） | 与统一 UAV Depth 一致 |
