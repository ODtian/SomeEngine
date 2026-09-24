using System.Collections.ObjectModel;
using System.Numerics;

namespace SomeEngine.Render.Frame;

public enum TemporalValidationPreset
{
    Off,
    ResolveOnly,
    JitterResolve,
    StableHistory,
    HighRejection,
}

public readonly record struct TemporalPresetState(
    bool TemporalResolveEnabled,
    bool TemporalJitterEnabled,
    TemporalResolveSettings Settings);

public static class TemporalValidationPresets
{
    private static readonly TemporalValidationPreset[] s_defaultSequence =
    [
        TemporalValidationPreset.Off,
        TemporalValidationPreset.ResolveOnly,
        TemporalValidationPreset.JitterResolve,
        TemporalValidationPreset.StableHistory,
        TemporalValidationPreset.HighRejection,
    ];

    public static IReadOnlyList<TemporalValidationPreset> DefaultSequence { get; } =
        new ReadOnlyCollection<TemporalValidationPreset>(s_defaultSequence);

    public static TemporalPresetState GetState(TemporalValidationPreset preset)
    {
        return preset switch
        {
            TemporalValidationPreset.Off => new(
                TemporalResolveEnabled: false,
                TemporalJitterEnabled: false,
                TemporalResolveSettings.Default),

            TemporalValidationPreset.ResolveOnly => new(
                TemporalResolveEnabled: true,
                TemporalJitterEnabled: false,
                TemporalResolveSettings.Default),

            TemporalValidationPreset.JitterResolve => new(
                TemporalResolveEnabled: true,
                TemporalJitterEnabled: true,
                TemporalResolveSettings.Default),

            TemporalValidationPreset.StableHistory => new(
                TemporalResolveEnabled: true,
                TemporalJitterEnabled: true,
                new TemporalResolveSettings(
                    HistoryWeight: 0.96f,
                    NeighborhoodClampScale: 0.06f,
                    NeighborhoodClampMin: 0.008f,
                    MotionRejectionScale: 0.18f)),

            TemporalValidationPreset.HighRejection => new(
                TemporalResolveEnabled: true,
                TemporalJitterEnabled: true,
                new TemporalResolveSettings(
                    HistoryWeight: 0.80f,
                    NeighborhoodClampScale: 0.05f,
                    NeighborhoodClampMin: 0.012f,
                    MotionRejectionScale: 0.75f)),

            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown temporal validation preset."),
        };
    }

    public static string DisplayName(TemporalValidationPreset preset)
    {
        return preset switch
        {
            TemporalValidationPreset.Off => "Off",
            TemporalValidationPreset.ResolveOnly => "Resolve Only",
            TemporalValidationPreset.JitterResolve => "Jitter + Resolve",
            TemporalValidationPreset.StableHistory => "Stable History",
            TemporalValidationPreset.HighRejection => "High Rejection",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown temporal validation preset."),
        };
    }

    public static string ArtifactName(TemporalValidationPreset preset)
    {
        return preset switch
        {
            TemporalValidationPreset.Off => "off",
            TemporalValidationPreset.ResolveOnly => "resolve-only",
            TemporalValidationPreset.JitterResolve => "jitter-resolve",
            TemporalValidationPreset.StableHistory => "stable-history",
            TemporalValidationPreset.HighRejection => "high-rejection",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown temporal validation preset."),
        };
    }

}

public readonly record struct TemporalCaptureOptions
{
    public const int DefaultWarmupFrames = 48;
    public const int DefaultCaptureFramesPerPreset = 1;

    public TemporalCaptureOptions(int warmupFrames, int captureFramesPerPreset)
    {
        if (warmupFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(warmupFrames), warmupFrames, "Temporal validation warmup cannot be negative.");
        if (captureFramesPerPreset <= 0)
            throw new ArgumentOutOfRangeException(nameof(captureFramesPerPreset), captureFramesPerPreset, "Temporal validation must capture at least one frame per preset.");

        WarmupFrames = warmupFrames;
        CaptureFramesPerPreset = captureFramesPerPreset;
    }

    public int WarmupFrames { get; }
    public int CaptureFramesPerPreset { get; }

    public static TemporalCaptureOptions Default { get; } =
        new(DefaultWarmupFrames, DefaultCaptureFramesPerPreset);
}

public readonly record struct TemporalStep(
    bool IsComplete,
    TemporalValidationPreset Preset,
    int PresetIndex,
    int FrameInPreset,
    int CaptureIndex,
    bool ApplyPreset,
    bool CaptureAfterRender,
    string CaptureName)
{
    public static TemporalStep Complete { get; } =
        new(
            IsComplete: true,
            Preset: TemporalValidationPreset.Off,
            PresetIndex: -1,
            FrameInPreset: 0,
            CaptureIndex: 0,
            ApplyPreset: false,
            CaptureAfterRender: false,
            CaptureName: string.Empty);
}

public sealed class TemporalValidationSession
{
    private readonly TemporalCaptureOptions _options;
    private readonly TemporalValidationPreset[] _presets;
    private int _presetIndex;
    private int _frameInPreset;
    private int _captureIndex;

    public TemporalValidationSession(
        TemporalCaptureOptions options,
        IEnumerable<TemporalValidationPreset>? presets = null)
    {
        _options = options;
        _presets = (presets ?? TemporalValidationPresets.DefaultSequence).ToArray();
        if (_presets.Length == 0)
            throw new ArgumentException("Temporal validation requires at least one preset.", nameof(presets));
    }

    public bool IsComplete => _presetIndex >= _presets.Length;
    public int PresetCount => _presets.Length;

    public TemporalStep BeginFrame()
    {
        if (IsComplete)
            return TemporalStep.Complete;

        TemporalValidationPreset preset = _presets[_presetIndex];
        bool capture = _frameInPreset >= _options.WarmupFrames
            && _captureIndex < _options.CaptureFramesPerPreset;
        string captureName = capture
            ? $"{_presetIndex:D2}-{TemporalValidationPresets.ArtifactName(preset)}-{_captureIndex:D2}"
            : string.Empty;

        return new TemporalStep(
            IsComplete: false,
            Preset: preset,
            PresetIndex: _presetIndex,
            FrameInPreset: _frameInPreset,
            CaptureIndex: _captureIndex,
            ApplyPreset: _frameInPreset == 0,
            CaptureAfterRender: capture,
            CaptureName: captureName);
    }

    public void CompleteFrame(TemporalStep step)
    {
        if (step.IsComplete)
            return;
        if (IsComplete)
            throw new InvalidOperationException("Temporal validation session is already complete.");
        if (step.PresetIndex != _presetIndex || step.FrameInPreset != _frameInPreset)
            throw new InvalidOperationException("Temporal validation step does not match the active session state.");

        if (step.CaptureAfterRender)
            _captureIndex++;

        _frameInPreset++;
        if (_captureIndex >= _options.CaptureFramesPerPreset)
        {
            _presetIndex++;
            _frameInPreset = 0;
            _captureIndex = 0;
        }
    }
}

public readonly record struct TemporalCameraSample(
    Vector3 Position,
    Vector3 Target,
    Matrix4x4 View);

public static class TemporalCameraPath
{
    public static TemporalCameraSample Sample(int frameInPreset)
    {
        if (frameInPreset < 0)
            throw new ArgumentOutOfRangeException(nameof(frameInPreset), frameInPreset, "Temporal validation frame cannot be negative.");

        float t = frameInPreset / 60.0f;
        var target = new Vector3(0.0f, 0.0f, 0.35f);
        var position = new Vector3(
            MathF.Sin(t * 1.1f) * 0.42f,
            MathF.Sin(t * 0.7f) * 0.08f,
            -3.0f + MathF.Cos(t * 0.5f) * 0.18f);

        return new TemporalCameraSample(
            position,
            target,
            Matrix4x4.CreateLookAt(position, target, Vector3.UnitY));
    }
}

public readonly record struct TemporalDiffMetrics(
    int Width,
    int Height,
    double MeanAbsoluteRgb,
    byte MaxChannelDelta,
    double ChangedPixelRatio);

public static class TemporalImageDiff
{
    public static TemporalDiffMetrics ComputeRgba32(
        ReadOnlySpan<byte> baselineRgba,
        ReadOnlySpan<byte> candidateRgba,
        int width,
        int height,
        byte changedPixelThreshold = 1)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Image width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Image height must be positive.");

        int expectedBytes = checked(width * height * 4);
        if (baselineRgba.Length != expectedBytes)
            throw new ArgumentException($"Baseline image must contain {expectedBytes} RGBA bytes.", nameof(baselineRgba));
        if (candidateRgba.Length != expectedBytes)
            throw new ArgumentException($"Candidate image must contain {expectedBytes} RGBA bytes.", nameof(candidateRgba));

        long absoluteRgbSum = 0;
        int maxChannelDelta = 0;
        int changedPixels = 0;
        int pixelCount = width * height;

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            bool changed = false;
            for (int channel = 0; channel < 3; channel++)
            {
                int delta = Math.Abs(candidateRgba[offset + channel] - baselineRgba[offset + channel]);
                absoluteRgbSum += delta;
                maxChannelDelta = Math.Max(maxChannelDelta, delta);
                changed |= delta > changedPixelThreshold;
            }

            if (changed)
                changedPixels++;
        }

        return new TemporalDiffMetrics(
            width,
            height,
            absoluteRgbSum / (pixelCount * 3.0),
            (byte)maxChannelDelta,
            changedPixels / (double)pixelCount);
    }
}
