namespace SomeEngine.Render;

internal static class PresentSyncInterval
{
    public static uint Parse(string name, string value)
    {
        if (!TryParse(value, out uint interval))
            throw new ArgumentException($"{name} requires an integer value from 0 to 4.");

        return interval;
    }

    public static bool TryParse(string? value, out uint interval)
        => uint.TryParse(value, out interval) && interval <= 4;
}
