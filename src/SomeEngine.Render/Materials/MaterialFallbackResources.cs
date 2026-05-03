using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Materials;

public sealed class MaterialFallbackResources : IDisposable
{
    private ITexture? _whiteTexture;
    private ITexture? _blackTexture;
    private ITexture? _flatNormalTexture;
    private IBuffer? _defaultBuffer;
    private ISampler? _defaultSampler;
    private bool _initialized;

    public MaterialResourceFallbacks Fallbacks { get; private set; } = new();

    public void EnsureInitialized(RenderContext context)
    {
        if (_initialized)
            return;

        var device = context.Device;
        if (device == null)
            return;

        _whiteTexture = CreateTexture(context, "MaterialFallback.White", [255, 255, 255, 255]);
        _blackTexture = CreateTexture(context, "MaterialFallback.Black", [0, 0, 0, 255]);
        _flatNormalTexture = CreateTexture(context, "MaterialFallback.FlatNormal", [128, 128, 255, 255]);
        _defaultBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "MaterialFallback.DefaultBuffer",
                Size = 16,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            },
            null
        );
        _defaultSampler = device.CreateSampler(
            new SamplerDesc
            {
                Name = "MaterialFallback.LinearWrapSampler",
                MinFilter = FilterType.Linear,
                MagFilter = FilterType.Linear,
                MipFilter = FilterType.Linear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
            }
        );

        Fallbacks = new MaterialResourceFallbacks
        {
            WhiteTexture = _whiteTexture?.GetDefaultView(TextureViewType.ShaderResource),
            BlackTexture = _blackTexture?.GetDefaultView(TextureViewType.ShaderResource),
            FlatNormalTexture = _flatNormalTexture?.GetDefaultView(TextureViewType.ShaderResource),
            DefaultBufferView = _defaultBuffer?.GetDefaultView(BufferViewType.ShaderResource),
            DefaultConstantBuffer = _defaultBuffer,
            DefaultSampler = _defaultSampler,
        };

        _initialized = true;
    }

    public void Dispose()
    {
        _defaultSampler?.Dispose();
        _defaultBuffer?.Dispose();
        _flatNormalTexture?.Dispose();
        _blackTexture?.Dispose();
        _whiteTexture?.Dispose();
        _defaultSampler = null;
        _defaultBuffer = null;
        _flatNormalTexture = null;
        _blackTexture = null;
        _whiteTexture = null;
        Fallbacks = new MaterialResourceFallbacks();
        _initialized = false;
    }

    private static ITexture? CreateTexture(RenderContext context, string name, byte[] rgba)
    {
        GCHandle handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var texture = context.Device?.CreateTexture(
                new TextureDesc
                {
                    Name = name,
                    Type = ResourceDimension.Tex2d,
                    Width = 1,
                    Height = 1,
                    Format = TextureFormat.RGBA8_UNorm,
                    Usage = Usage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                },
                new TextureData
                {
                    SubResources =
                    [
                        new TextureSubResData
                        {
                            Data = handle.AddrOfPinnedObject(),
                            Stride = 4,
                        },
                    ],
                }
            );

            if (texture != null && context.ImmediateContext != null)
            {
                context.ImmediateContext.TransitionResourceStates(
                [
                    new StateTransitionDesc
                    {
                        Resource = texture,
                        OldState = ResourceState.Unknown,
                        NewState = ResourceState.ShaderResource,
                        Flags = StateTransitionFlags.UpdateState,
                    },
                ]);
            }

            return texture;
        }
        finally
        {
            handle.Free();
        }
    }
}
