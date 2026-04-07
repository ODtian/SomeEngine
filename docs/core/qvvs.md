# QVVS 坐标系统

## 概述

QVVS (Quaternion-Vector-Vector-Scalar) 是 SomeEngine 的变换表示，灵感来自 [Latios Framework](https://github.com/Dreaming381/Latios-Framework)。相比传统 TRS (Translation-Rotation-Scale)，QVVS 分离了**各向同性缩放** (Scale) 和**各向异性拉伸** (Stretch)。

> **代码**：[TransformQvvs.cs](file:///f:/SomeEngine/src/SomeEngine.Core/Math/TransformQvvs.cs)

## 结构

```csharp
public struct TransformQvvs
{
    public Quaternion Rotation;  // Q — 旋转
    public Vector3 Position;     // V — 位移
    public Vector3 Stretch;      // V — 各向异性拉伸 (默认 1,1,1)
    public float Scale;          // S — 各向同性缩放 (默认 1.0)
}
```

## 变换组合

`Combine(parent, local)` 按以下顺序组合：

```
WorldPos = ParentPos + ParentRot × (ParentScale × ParentStretch × LocalPos)
WorldRot = ParentRot × LocalRot
WorldScale = ParentScale × LocalScale
WorldStretch = ParentStretch × LocalStretch
```

## 逆变换

`Inverse()` 计算逆映射（World → Local）：
- `invScale = 1 / Scale`
- `invStretch = 1 / Stretch`（含零保护）
- `invRotation = Quaternion.Inverse(Rotation)`
- `invPosition = invStretch × invScale × invRotation × (-Position)`

## 与 Matrix4x4 的转换

`ToMatrix()` 生成标准 4×4 矩阵，用于 GPU 上传：

```
Matrix = Scale(Stretch × Scale) × Rotate(Rotation) × Translate(Position)
```

## 和 ECS 的关系

- `LocalTransform.Value` 持有 QVVS（用户设置）
- `WorldTransform.Qvvs` 持有组合后的世界空间 QVVS
- `WorldTransform.Matrix` 持有 GPU 兼容的 4×4 矩阵
