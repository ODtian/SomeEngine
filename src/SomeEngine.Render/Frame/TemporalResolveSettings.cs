using System.Runtime.InteropServices;

namespace SomeEngine.Render.Frame;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TemporalResolveUniforms(
    float HistoryWeight,
    float NeighborhoodClampScale,
    float NeighborhoodClampMin,
    float MotionRejectionScale);

public readonly record struct TemporalResolveSettings(
    float HistoryWeight,
    float NeighborhoodClampScale,
    float NeighborhoodClampMin,
    float MotionRejectionScale)
{
    public const float MinHistoryWeight = 0.0f;
    public const float MaxHistoryWeight = 0.95f;
    public const float MinNeighborhoodClampScale = 0.0f;
    public const float MaxNeighborhoodClampScale = 2.0f;
    public const float MinNeighborhoodClampMin = 0.0f;
    public const float MaxNeighborhoodClampMin = 1.0f;
    public const float MinMotionRejectionScale = 0.0f;
    public const float MaxMotionRejectionScale = 4.0f;

    public static TemporalResolveSettings Default => new(
        HistoryWeight: 0.94f,
        NeighborhoodClampScale: 0.06f,
        NeighborhoodClampMin: 0.008f,
        MotionRejectionScale: 0.35f);

    public TemporalResolveUniforms ToUniforms()
        => new(
            Math.Clamp(HistoryWeight, MinHistoryWeight, MaxHistoryWeight),
            Math.Clamp(NeighborhoodClampScale, MinNeighborhoodClampScale, MaxNeighborhoodClampScale),
            Math.Clamp(NeighborhoodClampMin, MinNeighborhoodClampMin, MaxNeighborhoodClampMin),
            Math.Clamp(MotionRejectionScale, MinMotionRejectionScale, MaxMotionRejectionScale));
}
