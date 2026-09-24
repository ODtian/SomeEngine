using System.Runtime.InteropServices;

namespace SomeEngine.Runtime;

internal sealed class RenderDocCapture
{
    private const int ApiVersion = 10600;

    private readonly StartFrameCapture _start;
    private readonly EndFrameCapture _end;
    private readonly GetNumCaptures _captureCount;
    private readonly GetCapture _capture;
    private readonly SetCaptureFilePathTemplate _setPath;
    private readonly SetCaptureTitle _setTitle;
    private readonly uint _frame;
    private bool _active;

    private RenderDocCapture(RenderDocApi api, uint frame)
    {
        _start = Marshal.GetDelegateForFunctionPointer<StartFrameCapture>(api.StartFrameCapture);
        _end = Marshal.GetDelegateForFunctionPointer<EndFrameCapture>(api.EndFrameCapture);
        _captureCount = Marshal.GetDelegateForFunctionPointer<GetNumCaptures>(api.GetNumCaptures);
        _capture = Marshal.GetDelegateForFunctionPointer<GetCapture>(api.GetCapture);
        _setPath = Marshal.GetDelegateForFunctionPointer<SetCaptureFilePathTemplate>(api.SetCaptureFilePathTemplate);
        _setTitle = Marshal.GetDelegateForFunctionPointer<SetCaptureTitle>(api.SetCaptureTitle);
        _frame = frame;
    }

    public static RenderDocCapture? TryCreate(string template, uint frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        nint module = GetModuleHandle("renderdoc.dll");
        if (module == nint.Zero)
            return null;

        nint getApiAddress = GetProcAddress(module, "RENDERDOC_GetAPI");
        if (getApiAddress == nint.Zero)
            return null;

        var getApi = Marshal.GetDelegateForFunctionPointer<GetApi>(getApiAddress);
        if (getApi(ApiVersion, out nint apiAddress) != 1 || apiAddress == nint.Zero)
            return null;

        RenderDocApi api = Marshal.PtrToStructure<RenderDocApi>(apiAddress);
        if (api.StartFrameCapture == nint.Zero
            || api.EndFrameCapture == nint.Zero
            || api.GetNumCaptures == nint.Zero
            || api.GetCapture == nint.Zero
            || api.SetCaptureFilePathTemplate == nint.Zero
            || api.SetCaptureTitle == nint.Zero)
        {
            return null;
        }

        var capture = new RenderDocCapture(api, frame);
        string fullTemplate = Path.GetFullPath(template);
        string? directory = Path.GetDirectoryName(fullTemplate);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        capture._setPath(fullTemplate);
        return capture;
    }

    public bool ShouldCapture(uint frameIndex)
        => frameIndex + 1u == _frame;

    public void Begin(uint frameIndex)
    {
        if (_active || !ShouldCapture(frameIndex))
            return;

        _setTitle($"SomeEngine frame {frameIndex + 1u}");
        _start(nint.Zero, nint.Zero);
        _active = true;
        Console.WriteLine($"RenderDoc capture started for frame {frameIndex + 1u}.");
    }

    public void End(uint frameIndex)
    {
        if (!_active)
            return;

        uint ok = _end(nint.Zero, nint.Zero);
        _active = false;
        uint count = _captureCount();
        string path = count == 0 ? string.Empty : CapturePath(count - 1u);
        Console.WriteLine(
            ok == 1
                ? $"RenderDoc capture saved for frame {frameIndex + 1u}: {path}"
                : $"RenderDoc capture failed for frame {frameIndex + 1u}.");
    }

    private string CapturePath(uint index)
    {
        uint length = 0;
        _ = _capture(index, nint.Zero, ref length, out _);
        if (length == 0)
            return string.Empty;

        nint buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            uint ok = _capture(index, buffer, ref length, out _);
            return ok == 1 ? Marshal.PtrToStringUTF8(buffer) ?? string.Empty : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string procedureName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApi(int version, out nint apiPointers);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCaptureFilePathTemplate([MarshalAs(UnmanagedType.LPUTF8Str)] string pathTemplate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetNumCaptures();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetCapture(uint index, nint filename, ref uint pathLength, out ulong timestamp);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StartFrameCapture(nint device, nint window);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint EndFrameCapture(nint device, nint window);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCaptureTitle([MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderDocApi
    {
        public nint GetAPIVersion;
        public nint SetCaptureOptionU32;
        public nint SetCaptureOptionF32;
        public nint GetCaptureOptionU32;
        public nint GetCaptureOptionF32;
        public nint SetFocusToggleKeys;
        public nint SetCaptureKeys;
        public nint GetOverlayBits;
        public nint MaskOverlayBits;
        public nint RemoveHooks;
        public nint UnloadCrashHandler;
        public nint SetCaptureFilePathTemplate;
        public nint GetCaptureFilePathTemplate;
        public nint GetNumCaptures;
        public nint GetCapture;
        public nint TriggerCapture;
        public nint IsTargetControlConnected;
        public nint LaunchReplayUI;
        public nint SetActiveWindow;
        public nint StartFrameCapture;
        public nint IsFrameCapturing;
        public nint EndFrameCapture;
        public nint TriggerMultiFrameCapture;
        public nint SetCaptureFileComments;
        public nint DiscardFrameCapture;
        public nint ShowReplayUI;
        public nint SetCaptureTitle;
        public nint SetObjectAnnotation;
        public nint SetCommandAnnotation;
    }
}
