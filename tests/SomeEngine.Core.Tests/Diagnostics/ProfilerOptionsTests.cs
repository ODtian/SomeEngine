using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Core.Tests.Diagnostics;

public sealed class ProfilerOptionsTests
{
    private static readonly object ProfilerLock = new();

    [Fact]
    public void FromCommandLine_DefaultsToDisabled()
    {
        lock (ProfilerLock)
        {
            ProfilerOptions options = Profiler.ParseOptions([]);

            Assert.False(options.EnableTracy);
            Assert.False(options.ThrowOnUnavailable);
            Assert.Null(options.TracyNativeLibraryPath);
        }
    }

    [Fact]
    public void FromCommandLine_EnablesTracyFromSwitch()
    {
        lock (ProfilerLock)
        {
            ProfilerOptions options = Profiler.ParseOptions(
                ["--tracy", "--tracy-native", "custom-tracy.dll"]);

            Assert.True(options.EnableTracy);
            Assert.Equal("custom-tracy.dll", options.TracyNativeLibraryPath);
        }
    }

    [Fact]
    public void FromCommandLine_ProfileSwitchEnablesTracyBridgeOnly()
    {
        lock (ProfilerLock)
        {
            ProfilerOptions options = Profiler.ParseOptions(["--profile"]);

            Assert.True(options.EnableTracy);
        }
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerSwitchThrows()
    {
        lock (ProfilerLock)
        {
            Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--csharp-profile"]));
        }
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerOutputSwitchThrows()
    {
        lock (ProfilerLock)
        {
            Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--profile-output", "profile.txt"]));
        }
    }

    [Fact]
    public void FromCommandLine_ManagedProfilerDetailCountersSwitchThrows()
    {
        lock (ProfilerLock)
        {
            Assert.Throws<ArgumentException>(() => Profiler.ParseOptions(["--profile-detail-counters"]));
        }
    }

    [Fact]
    public void FromCommandLine_DisableSwitchOverridesEarlierProfileSwitch()
    {
        lock (ProfilerLock)
        {
            ProfilerOptions options = Profiler.ParseOptions(["--profile", "--no-profile"]);

            Assert.False(options.EnableTracy);
        }
    }

    [Fact]
    public void FromCommandLine_RequiredProfileEnablesAndRequiresNativeBackend()
    {
        lock (ProfilerLock)
        {
            ProfilerOptions options = Profiler.ParseOptions(["--profile-required"]);

            Assert.True(options.EnableTracy);
            Assert.True(options.ThrowOnUnavailable);
        }
    }
}
