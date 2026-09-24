using SomeEngine.Rhi;

namespace SomeEngine.Rhi.Tests;

public sealed class RhiSurfaceTests
{
    [Fact]
    public void DeviceCapsSplit()
    {
        string[] rootMethods =
        [
            nameof(IRtDevice.CreateRtPipeline),
            nameof(IRtDevice.GetRtSize),
            nameof(IRtDevice.GetRtId),
            nameof(IRtDevice.GetAccelSizes),
            nameof(IRtDevice.CreateAccelerationStructure),
            nameof(IQueryDevice.CreateQueryPool),
            nameof(ICacheDevice.CreatePipelineCache),
            nameof(ICacheDevice.GetPipelineData),
            nameof(IMemoryDevice.GetBufferReqs),
            nameof(IMemoryDevice.GetTextureReqs),
            nameof(IMemoryDevice.CreateMemoryHeap),
            nameof(IMemoryDevice.GetHeapDesc),
            nameof(IMemoryDevice.GetMemoryBudget),
            nameof(IMemoryDevice.CreatePlacedBuffer),
            nameof(IMemoryDevice.CreatePlacedTexture),
        ];

        foreach (string method in rootMethods)
            Assert.Null(typeof(IDevice).GetMethod(method));

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.NotNull(device.Get<IRtDevice>());
        Assert.NotNull(device.Get<IQueryDevice>());
        Assert.NotNull(device.Get<ICacheDevice>());
        Assert.NotNull(device.Get<IMemoryDevice>());
    }
}
