using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Rhi.D3D12;

internal sealed partial class D3D12Device
{
    internal void ReportBarriers(int textures, int buffers, int aliases)
    {
        Profiler.Barriers("D3D12", textures, buffers, aliases);
    }

    internal void ReportNative(int count)
    {
        Profiler.NativeBarriers("D3D12", count);
    }

    internal void ReportDependencies(int count)
    {
        Profiler.BarrierDependencies("D3D12", count);
    }

    internal void ReportDescriptors(int resources, int samplers)
    {
        Profiler.Descriptors("D3D12", resources, samplers);
    }
}
