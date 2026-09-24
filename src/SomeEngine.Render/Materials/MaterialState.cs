using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

public enum SurfaceMode
{
    Opaque,
    Masked,
    Translucent,
}

public enum StencilOp
{
    Keep,
    Zero,
    Replace,
    IncrementSaturate,
    DecrementSaturate,
    Invert,
    IncrementWrap,
    DecrementWrap,
}

public readonly record struct MaterialState
{
    public SurfaceMode Surface { get; init; }
    public bool TwoSided { get; init; }
    public int OverlayLayer { get; init; }
    public float BoundsExpansion { get; init; }
    public byte StencilRef { get; init; }
    public CompareOp StencilCompare { get; init; }
    public StencilOp StencilPass { get; init; }

    public static MaterialState Default => new()
    {
        Surface = SurfaceMode.Opaque,
        StencilCompare = CompareOp.Always,
        StencilPass = StencilOp.Keep,
    };
}
