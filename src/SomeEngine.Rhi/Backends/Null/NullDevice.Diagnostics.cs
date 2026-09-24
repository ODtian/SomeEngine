using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Rhi.Backends.Null;

internal sealed partial class NullDevice
{
    internal void ReportBarriers(int textures, int buffers, int aliases)
    {
        Profiler.Barriers("Null", textures, buffers, aliases);
    }

    internal void ReportDependencies(int count)
    {
        Profiler.BarrierDependencies("Null", count);
    }
}
