using System.Runtime.InteropServices;
using System.IO;

namespace SomeEngine.Rhi.D3D12;

internal static class PixEvents
{
    private const ulong EventColor = 0xff2d7dd2;
    private static readonly Lazy<Api?> ApiInstance = new(LoadApi);
    private static readonly string? EnabledValue = Environment.GetEnvironmentVariable("SOMEENGINE_PIX_MARKERS");
    private static readonly bool Enabled =
        string.Equals(EnabledValue, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EnabledValue, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EnabledValue, "yes", StringComparison.OrdinalIgnoreCase);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate void BeginListEvent(IntPtr commandList, ulong color, string formatString);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EndListEvent(IntPtr commandList);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate void SetListMarker(IntPtr commandList, ulong color, string formatString);

    public static bool IsEnabled => Enabled;

    public static bool IsAvailable => ApiInstance.Value != null;

    public static void BeginEvent(IntPtr commandList, string name)
    {
        var api = ApiInstance.Value;
        if (api == null)
            return;
        api.BeginEvent(commandList, EventColor, name);
    }

    public static void EndEvent(IntPtr commandList)
    {
        var api = ApiInstance.Value;
        if (api == null)
            return;
        api.EndEvent(commandList);
    }

    public static void SetMarker(IntPtr commandList, string name)
    {
        var api = ApiInstance.Value;
        if (api == null)
            return;
        api.SetMarker(commandList, EventColor, name);
    }

    private static Api? LoadApi()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (TryLoad(candidate, out Api? api))
                return api;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string[] names =
        [
            "WinPixEventRuntime.dll",
            "WinPixEventRuntime_OneCore.dll",
            "WinPixEventRuntimeNetCore.dll",
        ];

        foreach (string name in names)
            yield return name;

        foreach (string name in names)
            yield return Path.Combine(AppContext.BaseDirectory, name);

        foreach (string installDirectory in EnumeratePixDirs())
        {
            foreach (string name in names)
                yield return Path.Combine(installDirectory, name);
        }
    }

    private static IEnumerable<string> EnumeratePixDirs()
    {
        string filesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(filesRoot))
            yield break;

        string pixRoot = Path.Combine(filesRoot, "Microsoft PIX");
        if (!Directory.Exists(pixRoot))
            yield break;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(pixRoot);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        for (int index = directories.Length - 1; index >= 0; index--)
            yield return directories[index];
    }

    private static bool TryLoad(string libraryName, out Api? api)
    {
        api = null;
        if (!NativeLibrary.TryLoad(libraryName, out IntPtr module))
            return false;

        if (!TryGetExport(module, "PIXBeginEventOnCommandList", out BeginListEvent? begin)
            || !TryGetExport(module, "PIXEndEventOnCommandList", out EndListEvent? end)
            || !TryGetExport(module, "PIXSetMarkerOnCommandList", out SetListMarker? marker))
        {
            NativeLibrary.Free(module);
            return false;
        }

        api = new Api(module, begin!, end!, marker!);
        return true;
    }

    private static bool TryGetExport<TDelegate>(IntPtr module, string name, out TDelegate? result)
        where TDelegate : Delegate
    {
        result = null;
        if (!NativeLibrary.TryGetExport(module, name, out IntPtr address))
            return false;

        result = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        return true;
    }

    private sealed record Api(
        IntPtr Module,
        BeginListEvent BeginEvent,
        EndListEvent EndEvent,
        SetListMarker SetMarker);
}
