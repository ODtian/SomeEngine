using SomeEngine.Render.Frame;

namespace SomeEngine.Tests;

public class TemporalValidationTests
{
    [Fact]
    public void PresetsExposeStates()
    {
        TemporalPresetState off =
            TemporalValidationPresets.GetState(TemporalValidationPreset.Off);
        Assert.False(off.TemporalResolveEnabled);
        Assert.False(off.TemporalJitterEnabled);

        TemporalPresetState resolveOnly =
            TemporalValidationPresets.GetState(TemporalValidationPreset.ResolveOnly);
        Assert.True(resolveOnly.TemporalResolveEnabled);
        Assert.False(resolveOnly.TemporalJitterEnabled);
        Assert.Equal(TemporalResolveSettings.Default, resolveOnly.Settings);

        TemporalPresetState jitterResolve =
            TemporalValidationPresets.GetState(TemporalValidationPreset.JitterResolve);
        Assert.True(jitterResolve.TemporalResolveEnabled);
        Assert.True(jitterResolve.TemporalJitterEnabled);
        Assert.Equal(TemporalResolveSettings.Default, jitterResolve.Settings);
        Assert.InRange(jitterResolve.Settings.HistoryWeight, 0.92f, TemporalResolveSettings.MaxHistoryWeight);

        TemporalPresetState stable =
            TemporalValidationPresets.GetState(TemporalValidationPreset.StableHistory);
        Assert.True(stable.TemporalResolveEnabled);
        Assert.True(stable.TemporalJitterEnabled);
        Assert.Equal(0.96f, stable.Settings.HistoryWeight);
        Assert.Equal(0.18f, stable.Settings.MotionRejectionScale);

        TemporalPresetState rejection =
            TemporalValidationPresets.GetState(TemporalValidationPreset.HighRejection);
        Assert.True(rejection.TemporalResolveEnabled);
        Assert.True(rejection.TemporalJitterEnabled);
        Assert.Equal(0.80f, rejection.Settings.HistoryWeight);
        Assert.Equal(0.75f, rejection.Settings.MotionRejectionScale);
    }

    [Fact]
    public void SessionCapturesWarmup()
    {
        var session = new TemporalValidationSession(
            new TemporalCaptureOptions(warmupFrames: 2, captureFramesPerPreset: 1),
            [TemporalValidationPreset.Off, TemporalValidationPreset.ResolveOnly]);

        TemporalStep frame0 = session.BeginFrame();
        Assert.True(frame0.ApplyPreset);
        Assert.False(frame0.CaptureAfterRender);
        Assert.Equal(TemporalValidationPreset.Off, frame0.Preset);
        session.CompleteFrame(frame0);

        TemporalStep frame1 = session.BeginFrame();
        Assert.False(frame1.ApplyPreset);
        Assert.False(frame1.CaptureAfterRender);
        Assert.Equal(1, frame1.FrameInPreset);
        session.CompleteFrame(frame1);

        TemporalStep frame2 = session.BeginFrame();
        Assert.False(frame2.ApplyPreset);
        Assert.True(frame2.CaptureAfterRender);
        Assert.Equal("00-off-00", frame2.CaptureName);
        session.CompleteFrame(frame2);

        TemporalStep nextPreset = session.BeginFrame();
        Assert.True(nextPreset.ApplyPreset);
        Assert.False(nextPreset.CaptureAfterRender);
        Assert.Equal(TemporalValidationPreset.ResolveOnly, nextPreset.Preset);
        Assert.Equal(0, nextPreset.FrameInPreset);
    }

    [Fact]
    public void CameraPathMoves()
    {
        TemporalCameraSample frame10A = TemporalCameraPath.Sample(10);
        TemporalCameraSample frame10B = TemporalCameraPath.Sample(10);
        TemporalCameraSample frame30 = TemporalCameraPath.Sample(30);

        Assert.Equal(frame10A.Position, frame10B.Position);
        Assert.Equal(frame10A.Target, frame10B.Target);
        Assert.Equal(frame10A.View, frame10B.View);
        Assert.NotEqual(frame10A.Position, frame30.Position);
    }

    [Fact]
    public void DiffComputesMetrics()
    {
        byte[] baseline =
        [
            10, 20, 30, 255,
            100, 110, 120, 255,
        ];
        byte[] candidate =
        [
            20, 20, 30, 255,
            100, 113, 124, 255,
        ];

        TemporalDiffMetrics metrics =
            TemporalImageDiff.ComputeRgba32(baseline, candidate, width: 2, height: 1);

        Assert.Equal(2, metrics.Width);
        Assert.Equal(1, metrics.Height);
        Assert.Equal(17.0 / 6.0, metrics.MeanAbsoluteRgb, precision: 6);
        Assert.Equal(10, metrics.MaxChannelDelta);
        Assert.Equal(1.0, metrics.ChangedPixelRatio);
    }

    [Fact]
    public void DiffRejectsMismatch()
    {
        byte[] baseline = new byte[8];
        byte[] candidate = new byte[4];

        Assert.Throws<ArgumentException>(() =>
            TemporalImageDiff.ComputeRgba32(baseline, candidate, width: 2, height: 1));
    }
}
