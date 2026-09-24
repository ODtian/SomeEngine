using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace SomeEngine.Core.Diagnostics;

internal sealed class TracySink : IProfileSink
{
    private readonly TracyNativeApi _api;
    private readonly ConcurrentDictionary<SourceLocationKey, ulong> _sourceLocations = new();
    private readonly ConcurrentDictionary<string, IntPtr> _plotNames = new(StringComparer.Ordinal);
    private int _startupState;

    private TracySink(TracyNativeApi api)
    {
        _api = api;
        Status = new ProfilerStatus(
            IsRequested: true,
            HasActiveProfiler: true,
            TracyActive: true,
            TracyConnected: false,
            TracyNativeLibraryPath: api.LibraryPath,
            FailureReason: null);
    }

    public ProfilerStatus Status { get; private set; }
    public bool NeedsCounters => true;
    public bool NeedsDeviceTime => false;

    public static bool TryCreate(string? nativeLibraryPath, out TracySink sink, out string? failureReason)
    {
        sink = null!;
        if (!TracyNativeApi.TryLoad(nativeLibraryPath, out var api, out failureReason))
            return false;

        sink = new TracySink(api);
        return true;
    }

    public bool TryBeginScope(in ProfileScopeEvent ev, out ProfileToken token)
    {
        if (ev.Kind == ProfileScopeKind.CommandMarker)
        {
            token = default;
            return false;
        }

        return TryBegin(ev.Name, ev.MemberName, ev.FilePath, ev.LineNumber, out token);
    }

    public void EndScope(ProfileToken token)
    {
        if (token.Active != 0)
            _api.EndZone(new TracyZoneContext(checked((uint)token.Id), token.Active));
    }

    public void SetThreadName(in ThreadNameEvent ev)
    {
        EnsureStarted();
        string name = string.IsNullOrWhiteSpace(ev.Name) || ev.Name.Length <= 2
            ? "Managed Thread"
            : ev.Name;
        _api.SetThreadName(name);
    }

    public void FrameMark(in FrameMarkEvent ev)
    {
        EnsureStarted();
        if (string.IsNullOrWhiteSpace(ev.Name) || ev.Name.Length <= 2)
            _api.FrameMark();
        else
            _api.FrameMarkNamed(ev.Name);
    }

    public void QueueSubmit(in QueueSubmitEvent ev)
    {
    }

    public void Present(in PresentEvent ev)
    {
    }

    public void ValidationMessage(in ValidationMessageEvent ev)
    {
    }

    public void Count(in ProfileCount count)
    {
        EnsureStarted();
        if (string.IsNullOrWhiteSpace(count.Name) || count.Name.Length <= 2 || count.Amount == 0)
            return;

        IntPtr name = _plotNames.GetOrAdd(
            count.Name,
            static current => Marshal.StringToCoTaskMemUTF8(current));
        _api.PlotInt(name, count.Amount);
    }

    public void DeviceTime(in DeviceTimeEvent ev)
    {
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _startupState, 0) == 0)
            return;
        // The Tracy client owns native worker threads. Calling the manual-lifetime
        // shutdown from managed process teardown can assert or block after an
        // active capture disconnects; process exit tears the native client down.
    }

    private bool TryBegin(
        string name,
        string memberName,
        string filePath,
        int lineNumber,
        out ProfileToken token)
    {
        EnsureStarted();
        string source = string.IsNullOrWhiteSpace(filePath) || filePath.Length <= 2
            ? "Managed"
            : filePath;
        string function = string.IsNullOrWhiteSpace(memberName) || memberName.Length <= 2
            ? "Unknown"
            : memberName;
        string zoneName = string.IsNullOrWhiteSpace(name) || name.Length <= 2
            ? function
            : name;
        if (zoneName.Length <= 2)
            zoneName = "Managed Zone";

        var key = new SourceLocationKey(source, function, lineNumber, zoneName);
        ulong sourceLocation = _sourceLocations.GetOrAdd(
            key,
            static (current, api) => api.CreateSourceLocation(
                checked((uint)System.Math.Max(0, current.LineNumber)),
                current.FilePath,
                current.MemberName,
                current.ZoneName),
            _api);

        TracyZoneContext context = _api.BeginZone(sourceLocation);
        token = new ProfileToken(context.Id, context.Active);
        return context.Active != 0;
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _startupState) != 0)
        {
            RefreshConnectionStatus();
            return;
        }

        if (Interlocked.CompareExchange(ref _startupState, 1, 0) == 0)
            _api.StartupProfiler();

        RefreshConnectionStatus();
    }

    private void RefreshConnectionStatus()
    {
        bool connected = _api.IsConnected() != 0;
        if (Status.TracyConnected != connected)
            Status = Status with { TracyConnected = connected };
    }

    private readonly record struct SourceLocationKey(
        string FilePath,
        string MemberName,
        int LineNumber,
        string ZoneName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TracyZoneContext
    {
        public readonly uint Id;
        public readonly int Active;

        public TracyZoneContext(uint id, int active)
        {
            Id = id;
            Active = active;
        }
    }

    private sealed class TracyNativeApi
    {
        private const string DefaultNativeLibraryName = "SomeEngineTracy.Native";

        private readonly StartupProfilerDelegate _startupProfiler;
        private readonly ShutdownProfilerDelegate _shutdownProfiler;
        private readonly SrcLocDelegate _createSourceLocation;
        private readonly BeginZoneDelegate _beginZone;
        private readonly EndZoneDelegate _endZone;
        private readonly FrameMarkDelegate _frameMark;
        private readonly FrameNameDelegate _frameMarkNamed;
        private readonly ThreadNameDelegate _setThreadName;
        private readonly PlotIntDelegate _plotInt;
        private readonly IsConnectedDelegate _isConnected;

        private TracyNativeApi(
            IntPtr module,
            string libraryPath,
            StartupProfilerDelegate startupProfiler,
            ShutdownProfilerDelegate shutdownProfiler,
            SrcLocDelegate createSourceLocation,
            BeginZoneDelegate beginZone,
            EndZoneDelegate endZone,
            FrameMarkDelegate frameMark,
            FrameNameDelegate frameMarkNamed,
            ThreadNameDelegate setThreadName,
            PlotIntDelegate plotInt,
            IsConnectedDelegate isConnected)
        {
            Module = module;
            LibraryPath = libraryPath;
            _startupProfiler = startupProfiler;
            _shutdownProfiler = shutdownProfiler;
            _createSourceLocation = createSourceLocation;
            _beginZone = beginZone;
            _endZone = endZone;
            _frameMark = frameMark;
            _frameMarkNamed = frameMarkNamed;
            _setThreadName = setThreadName;
            _plotInt = plotInt;
            _isConnected = isConnected;
        }

        public IntPtr Module { get; }
        public string LibraryPath { get; }

        public static bool TryLoad(string? nativeLibraryPath, out TracyNativeApi api, out string? failureReason)
        {
            api = null!;
            string libraryName = string.IsNullOrWhiteSpace(nativeLibraryPath)
                ? DefaultNativeLibraryName
                : nativeLibraryPath;

            IntPtr module = IntPtr.Zero;
            string? loadedLibraryPath = null;
            List<string> failures = [];
            foreach (string candidate in EnumerateLibraryCandidates(libraryName))
            {
                try
                {
                    if (!NativeLibrary.TryLoad(candidate, out module))
                    {
                        failures.Add(candidate);
                        continue;
                    }

                    loadedLibraryPath = candidate;
                    break;
                }
                catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException)
                {
                    failures.Add($"{candidate}: {ex.Message}");
                }
            }

            if (module == IntPtr.Zero)
            {
                failureReason = $"Could not load native library '{libraryName}'. Tried: {string.Join("; ", failures)}.";
                return false;
            }

            try
            {
                api = new TracyNativeApi(
                    module,
                    loadedLibraryPath ?? libraryName,
                    GetExport<StartupProfilerDelegate>(module, "SomeEngineTracyStartupProfiler"),
                    GetExport<ShutdownProfilerDelegate>(module, "SomeEngineTracyShutdownProfiler"),
                    GetExport<SrcLocDelegate>(module, "SomeEngineTracyCreateSourceLocation"),
                    GetExport<BeginZoneDelegate>(module, "SomeEngineTracyBeginZone"),
                    GetExport<EndZoneDelegate>(module, "SomeEngineTracyEndZone"),
                    GetExport<FrameMarkDelegate>(module, "SomeEngineTracyFrameMark"),
                    GetExport<FrameNameDelegate>(module, "SomeEngineTracyFrameMarkNamed"),
                    GetExport<ThreadNameDelegate>(module, "SomeEngineTracySetThreadName"),
                    GetExport<PlotIntDelegate>(module, "SomeEngineTracyPlotInt"),
                    GetExport<IsConnectedDelegate>(module, "SomeEngineTracyIsConnected"));
                failureReason = null;
                return true;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or ArgumentException)
            {
                NativeLibrary.Free(module);
                failureReason = $"Native Tracy bridge '{libraryName}' is missing a required export: {ex.Message}";
                return false;
            }
        }

        private static IEnumerable<string> EnumerateLibraryCandidates(string libraryName)
        {
            yield return libraryName;

            string fileName = Path.GetFileName(libraryName);
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                yield return $"{libraryName}.dll";

            if (Path.IsPathRooted(libraryName))
                yield break;

            string baseDirectory = AppContext.BaseDirectory;
            yield return Path.Combine(baseDirectory, libraryName);
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(baseDirectory, $"{libraryName}.dll");
        }

        public void StartupProfiler() => _startupProfiler();

        public void ShutdownProfiler() => _shutdownProfiler();

        public ulong CreateSourceLocation(uint line, string source, string function, string name)
        {
            IntPtr sourcePtr = Marshal.StringToCoTaskMemUTF8(source);
            IntPtr functionPtr = Marshal.StringToCoTaskMemUTF8(function);
            IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                return _createSourceLocation(line, sourcePtr, functionPtr, namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(sourcePtr);
                Marshal.FreeCoTaskMem(functionPtr);
                Marshal.FreeCoTaskMem(namePtr);
            }
        }

        public TracyZoneContext BeginZone(ulong sourceLocation) => _beginZone(sourceLocation);

        public void EndZone(TracyZoneContext context) => _endZone(context);

        public void FrameMark() => _frameMark();

        public void FrameMarkNamed(string name)
        {
            IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                _frameMarkNamed(namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }

        public void SetThreadName(string name)
        {
            IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                _setThreadName(namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }

        public void PlotInt(IntPtr name, long value) => _plotInt(name, value);

        public int IsConnected() => _isConnected();

        private static TDelegate GetExport<TDelegate>(IntPtr module, string name)
            where TDelegate : Delegate
        {
            if (!NativeLibrary.TryGetExport(module, name, out IntPtr address))
                throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StartupProfilerDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownProfilerDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SrcLocDelegate(uint line, IntPtr source, IntPtr function, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate TracyZoneContext BeginZoneDelegate(ulong sourceLocation);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EndZoneDelegate(TracyZoneContext context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FrameMarkDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FrameNameDelegate(IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ThreadNameDelegate(IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlotIntDelegate(IntPtr name, long value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IsConnectedDelegate();
    }
}
