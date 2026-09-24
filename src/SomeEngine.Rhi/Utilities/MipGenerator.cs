using System.Buffers;
using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi.Utilities;

public sealed record MipGeneratorDesc
{
    public ShaderModuleDesc Texture2DShader { get; init; } = new();
    public ShaderModuleDesc Texture2DArrayShader { get; init; } = new();
    public uint ThreadGroupSizeX { get; init; } = 8;
    public uint ThreadGroupSizeY { get; init; } = 8;
    public uint ThreadGroupSizeZ { get; init; } = 1;
}

public sealed record GenerateMipsDesc
{
    public TextureHandle Texture { get; init; }
    public uint FirstSlice { get; init; }
    public uint SliceCount { get; init; } = uint.MaxValue;
    public uint MostDetailedMip { get; init; }
    public uint MipCount { get; init; } = uint.MaxValue;
    public ResourceState? InitialState { get; init; }
    public IReadOnlyList<ResourceState> InitialStates { get; init; } = Array.Empty<ResourceState>();
    public ResourceState FinalState { get; init; } = ResourceState.ShaderResource;
}

public sealed class MipGenerator : IDisposable
{
    private readonly IDevice _device;
    private readonly uint _threadGroupSizeX;
    private readonly uint _threadGroupSizeY;
    private readonly uint _threadGroupSizeZ;
    private readonly BindingLayoutHandle _bindingLayout;
    private readonly PipelineLayoutHandle _pipelineLayout;
    private readonly ShaderModuleHandle _texture2DShader;
    private readonly ShaderModuleHandle _texture2DArrayShader;
    private readonly PipelineHandle _texture2DPipeline;
    private readonly PipelineHandle _texture2DArrayPipeline;
    private readonly FlatDictionary<MipViewKey, TextureViewHandle> _viewCache = new(32);
    private bool _disposed;

    public MipGenerator(IDevice device, MipGeneratorDesc desc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(desc);

        _device = device;
        _threadGroupSizeX = RequirePositive(desc.ThreadGroupSizeX, nameof(desc.ThreadGroupSizeX));
        _threadGroupSizeY = RequirePositive(desc.ThreadGroupSizeY, nameof(desc.ThreadGroupSizeY));
        _threadGroupSizeZ = RequirePositive(desc.ThreadGroupSizeZ, nameof(desc.ThreadGroupSizeZ));

        _bindingLayout = _device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "MipGenerator.Bindings",
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.TextureRead, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.TextureReadWrite, Stages = ShaderStageFlags.Compute },
                ],
            });
        _pipelineLayout = _device.CreatePipelineLayout([_bindingLayout], name: "MipGenerator.PipelineLayout");

        _texture2DShader = CreateRequiredShader(desc.Texture2DShader, nameof(desc.Texture2DShader));
        _texture2DPipeline = CreatePipeline(_texture2DShader, "MipGenerator.Texture2D");

        if (!desc.Texture2DArrayShader.Bytecode.IsEmpty)
        {
            _texture2DArrayShader = CreateOptionalShader(desc.Texture2DArrayShader, nameof(desc.Texture2DArrayShader));
            _texture2DArrayPipeline = CreatePipeline(_texture2DArrayShader, "MipGenerator.Texture2DArray");
        }
    }

    public void GenerateMips(ICommandList list, GenerateMipsDesc desc)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(list);

        if (!desc.Texture.IsValid)
            throw new RhiException(ErrorCode.InvalidHandle, "Mip generation requires a valid texture.");
        if (!Enum.IsDefined(desc.FinalState))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation final state value {desc.FinalState} is not defined.");
        if (desc.InitialState.HasValue && !Enum.IsDefined(desc.InitialState.Value))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation initial state value {desc.InitialState.Value} is not defined.");
        if (desc.InitialState.HasValue && desc.InitialStates.Count != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation cannot specify both InitialState and InitialStates.");
        for (int index = 0; index < desc.InitialStates.Count; index++)
        {
            if (!Enum.IsDefined(desc.InitialStates[index]))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation initial state {index} value {desc.InitialStates[index]} is not defined.");
        }

        var texture = _device.GetTextureDesc(desc.Texture);
        ValidateTexture(texture);

        if (desc.MostDetailedMip >= texture.MipLevels)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation MostDetailedMip is outside the texture mip range.");

        uint mipCount = ResolveCount(desc.MipCount, texture.MipLevels, desc.MostDetailedMip, "Mip generation mip range");
        uint firstSlice;
        uint sliceCount;
        if (texture.Dimension == ResourceDimension.Texture3D)
        {
            if (desc.FirstSlice != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "3D mip generation does not use array slices.");
            firstSlice = 0;
            sliceCount = 1;
        }
        else
        {
            firstSlice = desc.FirstSlice;
            sliceCount = ResolveCount(desc.SliceCount, texture.ArraySize, desc.FirstSlice, "Mip generation slice range");
        }

        var mode = SelectMode(texture, firstSlice, sliceCount);
        var pipeline = PipelineForMode(mode);
        var viewDimension = ViewDim(mode);
        var localStates = CaptureStates(desc.Texture, texture, desc.MostDetailedMip, mipCount, firstSlice, sliceCount, desc.InitialState, desc.InitialStates);
        try
        {
            if (mipCount <= 1)
                return;

            uint lastMip = desc.MostDetailedMip + mipCount - 1;
            for (uint mip = desc.MostDetailedMip + 1; mip <= lastMip; mip++)
            {
                BarrierSlices(
                    list,
                    desc.Texture,
                    localStates,
                    desc.MostDetailedMip,
                    firstSlice,
                    sliceCount,
                    mip - 1,
                    firstSlice,
                    sliceCount,
                    ResourceState.ShaderResource);
                BarrierSlices(
                    list,
                    desc.Texture,
                    localStates,
                    desc.MostDetailedMip,
                    firstSlice,
                    sliceCount,
                    mip,
                    firstSlice,
                    sliceCount,
                    ResourceState.UnorderedAccess);

                var sourceView = GetTextureView(desc.Texture, texture, ViewKind.ShaderResource, viewDimension, mip - 1, firstSlice, sliceCount);
                var destinationView = GetTextureView(desc.Texture, texture, ViewKind.UnorderedAccess, viewDimension, mip, firstSlice, sliceCount);
                var pass = list.BeginComputePass(new ComputePassDesc { Name = "MipGenerator" });
                pass.SetPipeline(pipeline);
                pass.SetBindings(
                    0,
                    _bindingLayout,
                    [
                        new BindingResourceDesc { Binding = 0, ResourceType = BindingType.TextureRead, TextureView = sourceView },
                        new BindingResourceDesc { Binding = 1, ResourceType = BindingType.TextureReadWrite, TextureView = destinationView },
                    ]);

                uint width = MipExtent(texture.Width, mip);
                uint height = MipExtent(texture.Height, mip);
                pass.Dispatch(
                    CeilDiv(width, _threadGroupSizeX),
                    CeilDiv(height, _threadGroupSizeY),
                    CeilDiv(sliceCount, _threadGroupSizeZ));
                pass.End();
                SetStates(localStates, desc.MostDetailedMip, firstSlice, sliceCount, mip, firstSlice, sliceCount, ResourceState.UnorderedAccess);
            }

            for (uint mip = desc.MostDetailedMip; mip <= lastMip; mip++)
            {
                BarrierSlices(
                    list,
                    desc.Texture,
                    localStates,
                    desc.MostDetailedMip,
                    firstSlice,
                    sliceCount,
                    mip,
                    firstSlice,
                    sliceCount,
                    desc.FinalState);
            }
        }
        finally
        {
            ArrayPool<ResourceState>.Shared.Return(localStates);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var pair in _viewCache)
            _device.Destroy(pair.Value);
        _viewCache.Dispose();

        if (_texture2DArrayPipeline.IsValid)
            _device.Destroy(_texture2DArrayPipeline);
        if (_texture2DPipeline.IsValid)
            _device.Destroy(_texture2DPipeline);
        if (_texture2DArrayShader.IsValid)
            _device.Destroy(_texture2DArrayShader);
        if (_texture2DShader.IsValid)
            _device.Destroy(_texture2DShader);
        if (_pipelineLayout.IsValid)
            _device.Destroy(_pipelineLayout);
        if (_bindingLayout.IsValid)
            _device.Destroy(_bindingLayout);

        _disposed = true;
    }

    private ShaderModuleHandle CreateRequiredShader(ShaderModuleDesc desc, string label)
    {
        if (desc.Bytecode.IsEmpty)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} bytecode is required.");
        return CreateOptionalShader(desc, label);
    }

    private ShaderModuleHandle CreateOptionalShader(ShaderModuleDesc desc, string label)
    {
        if (desc.Stage != ShaderStage.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} must be a compute shader.");
        if (desc.Backend != _device.AdapterInfo.Backend)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} backend must match the device backend.");
        return _device.CreateShaderModule(desc);
    }

    private PipelineHandle CreatePipeline(ShaderModuleHandle shader, string name)
        => _device.CreateComputePipeline(new ComputePipelineDesc { Name = name, Layout = _pipelineLayout, ComputeShader = shader });

    private void ValidateTexture(TextureDesc texture)
    {
        if (texture.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation requires a single-sampled texture.");
        if (!texture.BindFlags.HasFlag(BindFlags.ShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation texture must have ShaderResource bind flag.");
        if (!texture.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation texture must have UnorderedAccess bind flag.");
        if (Validation.IsDepthFormat(texture.Format))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mip generation does not support depth/stencil formats.");

        var capabilities = _device.GetFormatCapabilities(texture.Format);
        if (!capabilities.Support.HasFlag(FormatSupport.ShaderSample))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation format {texture.Format} does not support shader sampling.");
        if (!capabilities.Support.HasFlag(FormatSupport.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation format {texture.Format} does not support unordered-access writes.");
    }

    private MipGeneratorMode SelectMode(TextureDesc texture, uint firstSlice, uint sliceCount)
    {
        return texture.Dimension switch
        {
            ResourceDimension.Texture2D when texture.ArraySize == 1 && firstSlice == 0 && sliceCount == 1 => MipGeneratorMode.Texture2D,
            ResourceDimension.Texture2D => RequirePipeline(_texture2DArrayPipeline, MipGeneratorMode.Texture2DArray, "2D array mip generation requires Texture2DArrayShader."),
            ResourceDimension.TextureCube => throw new RhiException(ErrorCode.UnsupportedFeature, "Mip generation for cube and cube-array textures is not supported by this utility."),
            ResourceDimension.Texture3D => throw new RhiException(ErrorCode.UnsupportedFeature, "Mip generation for 3D textures is not supported by this utility."),
            _ => throw new RhiException(ErrorCode.UnsupportedFeature, $"Mip generation does not support texture dimension {texture.Dimension}."),
        };
    }

    private PipelineHandle PipelineForMode(MipGeneratorMode mode)
    {
        return mode switch
        {
            MipGeneratorMode.Texture2D => _texture2DPipeline,
            MipGeneratorMode.Texture2DArray => _texture2DArrayPipeline,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation mode {mode} is not valid."),
        };
    }

    private static TextureViewDimension ViewDim(MipGeneratorMode mode)
    {
        return mode switch
        {
            MipGeneratorMode.Texture2D => TextureViewDimension.Texture2D,
            MipGeneratorMode.Texture2DArray => TextureViewDimension.Texture2DArray,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation mode {mode} is not valid."),
        };
    }

    private static MipGeneratorMode RequirePipeline(PipelineHandle pipeline, MipGeneratorMode mode, string message)
    {
        if (!pipeline.IsValid)
            throw new RhiException(ErrorCode.InvalidDescriptor, message);
        return mode;
    }

    private ResourceState[] CaptureStates(
        TextureHandle texture,
        TextureDesc desc,
        uint firstMip,
        uint mipCount,
        uint firstSlice,
        uint sliceCount,
        ResourceState? initialState,
        IReadOnlyList<ResourceState> initialStates)
    {
        int expectedInitialStateCount = checked((int)(mipCount * sliceCount));
        if (initialStates.Count != 0 && initialStates.Count != expectedInitialStateCount)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Mip generation InitialStates must contain exactly {expectedInitialStateCount} entries.");

        var states = ArrayPool<ResourceState>.Shared.Rent(expectedInitialStateCount);
        for (uint mip = 0; mip < mipCount; mip++)
        {
            for (uint slice = 0; slice < sliceCount; slice++)
            {
                uint actualMip = firstMip + mip;
                uint actualSlice = firstSlice + slice;
                int initialStateIndex = checked((int)(mip * sliceCount + slice));
                states[initialStateIndex] = initialStates.Count != 0
                    ? initialStates[initialStateIndex]
                    : initialState ?? _device.GetTextureState(texture, actualMip, actualSlice);
            }
        }

        return states;
    }

    private static void BarrierSlices(
        ICommandList list,
        TextureHandle texture,
        ResourceState[] states,
        uint firstMip,
        uint firstSlice,
        uint trackedSliceCount,
        uint mip,
        uint sliceStart,
        uint sliceCount,
        ResourceState after)
    {
        Span<TextureBarrier> barrier = stackalloc TextureBarrier[1];
        for (uint slice = sliceStart; slice < sliceStart + sliceCount; slice++)
        {
            int stateIndex = StateIndex(firstMip, firstSlice, trackedSliceCount, mip, slice);
            var before = states[stateIndex];
            if (before == after)
                continue;

            barrier[0] = new TextureBarrier(texture, before, after, new SubresourceRange(mip, 1, slice, 1));
            list.Barrier(
                barrier,
                ReadOnlySpan<BufferBarrier>.Empty);
            states[stateIndex] = after;
        }
    }

    private static void SetStates(
        ResourceState[] states,
        uint firstMip,
        uint firstSlice,
        uint trackedSliceCount,
        uint mip,
        uint sliceStart,
        uint sliceCount,
        ResourceState state)
    {
        for (uint slice = sliceStart; slice < sliceStart + sliceCount; slice++)
            states[StateIndex(firstMip, firstSlice, trackedSliceCount, mip, slice)] = state;
    }

    private TextureViewHandle GetTextureView(
        TextureHandle texture,
        TextureDesc textureDesc,
        ViewKind kind,
        TextureViewDimension dimension,
        uint mip,
        uint firstSlice,
        uint sliceCount)
    {
        var key = new MipViewKey(texture, kind, dimension, textureDesc.Format, mip, firstSlice, sliceCount);
        if (_viewCache.TryGetValue(key, out var cached))
            return cached;

        var view = _device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Name = $"MipGenerator.{kind}.Mip{mip}",
                Kind = kind,
                Dimension = dimension,
                Format = textureDesc.Format,
                FirstMip = mip,
                MipCount = 1,
                FirstSlice = firstSlice,
                SliceCount = sliceCount,
            });
        _viewCache.Add(key, view);
        return view;
    }

    private static int StateIndex(uint firstMip, uint firstSlice, uint trackedSliceCount, uint mip, uint slice)
        => checked((int)((mip - firstMip) * trackedSliceCount + (slice - firstSlice)));

    private static uint ResolveCount(uint requested, uint total, uint first, string label)
    {
        if (first >= total)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} starts outside the resource range.");
        uint count = requested == uint.MaxValue ? total - first : requested;
        if (count == 0 || count > total - first)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} is outside the resource range.");
        return count;
    }

    private static uint MipExtent(uint value, uint mip)
        => Math.Max(1u, value >> checked((int)mip));

    private static uint CeilDiv(uint value, uint divisor)
        => (value + divisor - 1) / divisor;

    private static uint RequirePositive(uint value, string name)
    {
        if (value == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{name} must be greater than zero.");
        return value;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MipGenerator));
    }

    private enum MipGeneratorMode
    {
        Texture2D,
        Texture2DArray,
    }

    private readonly record struct MipViewKey(
        TextureHandle Texture,
        ViewKind Kind,
        TextureViewDimension Dimension,
        Format Format,
        uint Mip,
        uint FirstSlice,
        uint SliceCount);
}
