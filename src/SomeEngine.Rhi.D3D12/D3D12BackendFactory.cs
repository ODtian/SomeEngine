using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace SomeEngine.Rhi.D3D12;

internal sealed class D3D12Factory : IBackendFactory
{
    public Backend Backend => Backend.D3D12;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<AdapterInfo>();

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(debug: false);
        var adapters = new List<AdapterInfo>();
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                adapter = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(index, GpuPreference.HighPerformance);
                if (IsHardwareAdapter(adapter))
                    adapters.Add(ToAdapterInfo(adapter));
            }
            catch (SharpGenException)
            {
                break;
            }
            finally
            {
                adapter?.Dispose();
            }
        }

        return adapters;
    }

    public IDevice CreateDevice(DeviceDesc desc)
    {
        if (desc.Backend != Backend.D3D12)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"D3D12 backend factory cannot create backend {desc.Backend}.");
        if (!OperatingSystem.IsWindows())
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 backend requires Windows.");

        bool enableValidation = desc.EnableValidation;
        if (enableValidation)
        {
            EnableDebugLayer();
        }

        var factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(enableValidation);
        IDXGIAdapter1? selected = null;
        try
        {
            var candidates = SelectAdapterCandidates(factory, desc.AdapterName);
            try
            {
                ID3D12Device nativeDevice = CreateDevice(candidates, out selected);
                var device = new D3D12Device(
                    factory,
                    selected,
                    nativeDevice,
                    ToAdapterInfo(selected),
                    enableValidation,
                    new D3D12Policy());
                return device;
            }
            finally
            {
                DisposeAdapters(candidates);
            }
        }
        catch
        {
            selected?.Dispose();
            factory.Dispose();
            throw;
        }
    }

    private static List<IDXGIAdapter1> SelectAdapterCandidates(IDXGIFactory6 factory, string adapterName)
    {
        var candidates = new List<IDXGIAdapter1>();
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                adapter = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(index, GpuPreference.HighPerformance);
                if (!IsHardwareAdapter(adapter))
                {
                    adapter.Dispose();
                    continue;
                }

                if (string.IsNullOrWhiteSpace(adapterName) || adapter.Description1.Description.Contains(adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(adapter);
                    adapter = null;
                }

                adapter?.Dispose();
            }
            catch (SharpGenException)
            {
                break;
            }
        }

        string suffix = string.IsNullOrWhiteSpace(adapterName) ? string.Empty : $" matching '{adapterName}'";
        if (candidates.Count == 0)
            throw new RhiException(ErrorCode.BackendFailure, $"No hardware adapter{suffix} was found.");
        return candidates;
    }

    private static ID3D12Device CreateDevice(List<IDXGIAdapter1> candidates, out IDXGIAdapter1 selected)
    {
        RhiException? lastFailure = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            try
            {
                var nativeDevice = Vortice.Direct3D12.D3D12.D3D12CreateDevice<ID3D12Device>(candidates[i], FeatureLevel.Level_11_0);
                selected = candidates[i];
                candidates.RemoveAt(i);
                return nativeDevice;
            }
            catch (SharpGenException ex)
            {
                lastFailure = new RhiException(ErrorCode.BackendFailure, $"Failed to create a D3D12 device for adapter '{candidates[i].Description1.Description}': {ex.Message}");
            }
        }

        throw lastFailure ?? new RhiException(ErrorCode.BackendFailure, "No D3D12-capable adapter was found.");
    }

    private static void DisposeAdapters(List<IDXGIAdapter1> adapters)
    {
        foreach (var adapter in adapters)
            adapter.Dispose();
        adapters.Clear();
    }

    private static bool IsHardwareAdapter(IDXGIAdapter1 adapter)
    {
        var desc = adapter.Description1;
        return !desc.Flags.HasFlag(AdapterFlags.Software);
    }

    private static void EnableDebugLayer()
    {
        try
        {
            using var debug = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface<ID3D12Debug>();
            debug.EnableDebugLayer();
        }
        catch (SharpGenException ex)
        {
            throw new RhiException(ErrorCode.UnsupportedFeature, $"D3D12 validation was requested but the debug layer is unavailable: {ex.Message}");
        }
    }

    private static AdapterInfo ToAdapterInfo(IDXGIAdapter1 adapter)
    {
        var desc = adapter.Description1;
        return new AdapterInfo
        {
            Name = desc.Description,
            Backend = Backend.D3D12,
            DedicatedVideoMemory = ToU64(desc.DedicatedVideoMemory),
            DedicatedSystemMemory = ToU64(desc.DedicatedSystemMemory),
            SharedSystemMemory = ToU64(desc.SharedSystemMemory),
            IsSoftware = desc.Flags.HasFlag(AdapterFlags.Software),
            IsIntegrated = ToU64(desc.DedicatedVideoMemory) == 0,
            VendorId = desc.VendorId,
            DeviceId = desc.DeviceId,
        };
    }

    private static ulong ToU64(PointerUSize value)
    {
        nuint native = value;
        return native;
    }
}
