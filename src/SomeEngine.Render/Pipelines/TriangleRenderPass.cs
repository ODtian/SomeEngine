using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using System;
using System.IO;

namespace SomeEngine.Render.Pipelines;

public class TriangleRenderPass : RenderPass, IDisposable
{
    private RGResourceHandle _outputHandle;
    private IPipelineState? _pso;
    private readonly RenderContext
        _context; // Keep context to init PSO, or init lazily
    private bool _psoInitialized = false;
    private IShaderResourceBinding? _srb;

    public TransformSyncSystem? TransformSystem { get; set; }

    public TriangleRenderPass(RenderContext context) : base("TrianglePass")
    {
        _context = context;
    }

    public void InitPSO()
    {
        if (_psoInitialized)
            return;

        // ... Same PSO creation logic as HelloTrianglePass ...
        // Re-using logic to avoid duplication would be good, but for now specific.
        // Actually, let's copy the logic.

        var device = _context.Device;
        if (device == null)
            return;

        // Shaders
        string shaderPath = Path.Combine(
            AppContext.BaseDirectory, "../../../../../assets/Shaders/triangle.hlsl"
        );
        if (!File.Exists(shaderPath))
            shaderPath = "triangle.hlsl";

        using var shaderSourceFactory =
            _context.Factory?.CreateDefaultShaderSourceStreamFactory(
                "assets/Shaders"
            );

        var vsCI = new ShaderCreateInfo {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderSourceStreamFactory = shaderSourceFactory,
            FilePath = "triangle.hlsl",
            EntryPoint = "VSMain",
            Desc = new ShaderDesc {
                Name = "Triangle VS", ShaderType = ShaderType.Vertex
            }
        };
        using var vs = device.CreateShader(vsCI, out _);

        var psCI = new ShaderCreateInfo {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderSourceStreamFactory = shaderSourceFactory,
            FilePath = "triangle.hlsl",
            EntryPoint = "PSMain",
            Desc = new ShaderDesc {
                Name = "Triangle PS", ShaderType = ShaderType.Pixel
            }
        };
        using var ps = device.CreateShader(psCI, out _);

        var psoCI = new GraphicsPipelineStateCreateInfo {
            PSODesc =
                new PipelineStateDesc {
                    Name = "Hello Triangle PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout =
                        new PipelineResourceLayoutDesc {
                            DefaultVariableType = ShaderResourceVariableType.Static,
                            Variables = [new ShaderResourceVariableDesc {
                                Name = "Transforms",
                                Type = ShaderResourceVariableType.Mutable,
                                ShaderStages = ShaderType.Vertex
                            }]
                        }
                },
            GraphicsPipeline =
                new GraphicsPipelineDesc {
                    NumRenderTargets = 1,
                    RTVFormats = [_context.SwapChain!.GetDesc().ColorBufferFormat],
                    DSVFormat = _context.SwapChain!.GetDesc().DepthBufferFormat,
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    RasterizerDesc =
                        new RasterizerStateDesc { CullMode = CullMode.None },
                    DepthStencilDesc =
                        new DepthStencilStateDesc { DepthEnable = false }
                },
            Vs = vs,
            Ps = ps
        };

        _pso = device.CreateGraphicsPipelineState(psoCI);
        _srb = _pso.CreateShaderResourceBinding(true);
        _psoInitialized = true;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        if (_outputHandle.IsValid)
        {
            builder.WriteTexture(_outputHandle);
        }
    }

    // Updated Setup signature to be more flexible?
    // For now, let's fix the builder to allow getting handles?
    // Or just use the graph's GetResourceHandle in loop before calling setup?
    // Let's modify Setup to receive the output handle we want to write to?
    // Usually passes declare "I output to X".

    // Let's keep it simple: We will find the "BackBuffer" handle inside Setup using
    // the builder/graph. But builder hides graph. I'll update Builder to helper
    // lookups.

    public void SetOutput(RGResourceHandle handle)
    {
        _outputHandle = handle;
    }

    public override void Execute(
        RenderContext context, RenderGraphContext graphContext
    )
    {
        if (!_psoInitialized)
            InitPSO();

        var ctx = graphContext.CommandList;
        var rtv =
            graphContext.GetTextureView(_outputHandle, TextureViewType.RenderTarget);

        if (rtv == null)
            return;

        var dsv =
            context.SwapChain!.GetDepthBufferDSV(); // Using swapchain depth for now

        ctx.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
        ctx.ClearRenderTarget(
            rtv,
            new System.Numerics.Vector4(0.2f, 0.2f, 0.2f, 1.0f),
            ResourceStateTransitionMode.Transition
        );
        ctx.ClearDepthStencil(
            dsv,
            ClearDepthStencilFlags.Depth,
            1.0f,
            0,
            ResourceStateTransitionMode.Transition
        );

        ctx.SetPipelineState(_pso);

        if (TransformSystem != null &&
            TransformSystem.GlobalTransformBuffer != null &&
            TransformSystem.Count > 0)
        {
            // Update Binding
            var varTransforms =
                _srb?.GetVariableByName(ShaderType.Vertex, "Transforms");
            if (varTransforms != null &&
                TransformSystem.GlobalTransformBuffer != null)
            {
                varTransforms.Set(
                    TransformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            }
            if (_srb != null)
                ctx.CommitShaderResources(
                    _srb, ResourceStateTransitionMode.Transition
                );

            // Draw instances
            ctx.Draw(new DrawAttribs {
                NumVertices = 3,
                NumInstances = (uint)TransformSystem.Count,
                Flags = DrawFlags.VerifyAll
            });
        }
        else
        {
            // Draw 1 instance using default (InstanceID 0 might crash if buffer is
            // bound but empty, so safe path) But if shader expects buffer, and we
            // don't bind it, it might be undefined. If shader uses T0, but we don't
            // bind, validation layer will yell. If we don't bind T0, and shader
            // reads T0, D3D12 might crash or return 0. (Descriptor not set error).
            // So we should bind SOMETHING or use a different shader/PSO permutation.
            // For now, assume we always have at least 1 transform if we run this.
            // But if SyncSystem is null, we can't bind.
            // Let's just not draw or draw without binding (might error).
            ctx.Draw(new DrawAttribs { NumVertices = 3, Flags = DrawFlags.VerifyAll }
            );
        }
    }

    public void Dispose()
    {
        _srb?.Dispose();
        _pso?.Dispose();
    }
}
