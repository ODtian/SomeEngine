using System.Runtime.InteropServices;

namespace SomeEngine.Core.Diagnostics;

public static class CrashDialogPolicy
{
    public static void DisableUi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ErrorMode mode = GetErrorMode();
        SetErrorMode(
            mode
            | ErrorMode.SemFailCriticalErrors
            | ErrorMode.SemNoGpFaultErrorBox
            | ErrorMode.SemNoOpenFileErrorBox);

        try
        {
            _ = WerSetFlags(WerFaultReportingNoUi);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private const uint WerFaultReportingNoUi = 0x20;

    [DllImport("kernel32.dll")]
    private static extern ErrorMode GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern ErrorMode SetErrorMode(ErrorMode uMode);

    [DllImport("wer.dll")]
    private static extern int WerSetFlags(uint dwFlags);

    [Flags]
    private enum ErrorMode : uint
    {
        SemFailCriticalErrors = 0x0001,
        SemNoGpFaultErrorBox = 0x0002,
        SemNoOpenFileErrorBox = 0x8000,
    }
}
