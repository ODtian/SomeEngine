using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public class SimpleMeshRenderPass(RenderContext context) : RenderPass("SimpleMeshPass"), IDisposable
{
    private readonly RenderContext _context = context;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private IBuffer? _vb;
    private IBuffer? _ib;
    private IBuffer? _cb;
    private int _indexCount;
    private bool _initialized;
    private static readonly ulong[] offsets = new[] { 0ul };

    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Matrix4x4 WorldViewProj;
        public Vector4 Color;
    }

    public void Init()
    {
        if (_initialized)
            return;

        CreateMeshBuffers();
        CreatePSO();
        _initialized = true;
    }

    private void CreateMeshBuffers()
    {
        var (vertices, indices, _) = PrimitiveMeshGenerator.CreateIcoSphere(3);
        _indexCount = indices.Length;

        var device = _context.Device;

        var vertData = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertData[i * 3 + 0] = vertices[i].X;
            vertData[i * 3 + 1] = vertices[i].Y;
            vertData[i * 3 + 2] = vertices[i].Z;
        }
        var vbDesc = new BufferDesc
        {
            Name = "SimpleMesh VB",
            Usage = Usage.Default,
            BindFlags = BindFlags.VertexBuffer,
            Size = (ulong)(vertData.Length * sizeof(float)),
        };
        _vb = device!.CreateBuffer(vbDesc);
        _context.ImmediateContext!.TransitionResourceStates([
            new StateTransitionDesc {
                Resource = _vb,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.CopyDest,
                Flags = StateTransitionFlags.UpdateState
            }
        ]);
        _context.ImmediateContext!.UpdateBuffer(
            _vb,
            0,
            vertData,
            ResourceStateTransitionMode.Verify
        );

        var ibDesc = new BufferDesc
        {
            Name = "SimpleMesh IB",
            Usage = Usage.Default,
            BindFlags = BindFlags.IndexBuffer,
            Size = (ulong)(indices.Length * sizeof(uint)),
        };
        _ib = device!.CreateBuffer(ibDesc);
        _context.ImmediateContext!.TransitionResourceStates([
            new StateTransitionDesc {
                Resource = _ib,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.CopyDest,
                Flags = StateTransitionFlags.UpdateState
            }
        ]);
        _context.ImmediateContext!.UpdateBuffer(
            _ib,
            0,
            indices,
            ResourceStateTransitionMode.Verify
        );

        _context.ImmediateContext!.TransitionResourceStates([
            new StateTransitionDesc {
                Resource = _vb,
                OldState = ResourceState.CopyDest,
                NewState = ResourceState.VertexBuffer,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _ib,
                OldState = ResourceState.CopyDest,
                NewState = ResourceState.IndexBuffer,
                Flags = StateTransitionFlags.UpdateState
            }
        ]);

        var cbDesc = new BufferDesc
        {
            Name = "SimpleMesh Constants",
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            Size = (ulong)Marshal.SizeOf<Constants>(),
        };
        _cb = device!.CreateBuffer(cbDesc);
    }

    private void CreatePSO()
    {
        var device = _context.Device!;

        // Import Slang shader
        string slangPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "Shaders",
            "simple_mesh.slang"
        );

        if (!File.Exists(slangPath))
        {
            // Fallback to source directory if running from build output but assets not copied
            slangPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/simple_mesh.slang"
                )
            );
        }

        var shaderAsset = SlangShaderImporter.Import(slangPath);

        using var vs = shaderAsset.CreateShader(_context, "VSMain");
        using var ps = shaderAsset.CreateShader(_context, "PSMain");

        var layoutElements = new[]
        {
            new LayoutElement
            {
                InputIndex = 0,
                BufferSlot = 0,
                NumComponents = 3,
                ValueType = Diligent.ValueType.Float32,
                IsNormalized = false,
            },
        };

        var psoDesc = new GraphicsPipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "SimpleMesh PSO",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable,
                    Variables = shaderAsset.GetResourceVariables(
                        _context,
                        name =>
                        {
                            if (
                                name.Contains("Uniforms")
                                || name == "Uniforms"
                                || name == "Constants"
                                || name == "g_Constants"
                            )
                                return Diligent.ShaderResourceVariableType.Static;
                            return null;
                        }
                    ),
                },
            },
            GraphicsPipeline = new GraphicsPipelineDesc
            {
                NumRenderTargets = 1,
                RTVFormats = [_context.SwapChain!.GetDesc().ColorBufferFormat],
                DSVFormat = TextureFormat.D32_Float,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc
                {
                    CullMode = CullMode.Back,
                    FrontCounterClockwise = true,
                },
                DepthStencilDesc = new DepthStencilStateDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                },
                InputLayout = new InputLayoutDesc { LayoutElements = layoutElements },
            },
            Vs = vs,
            Ps = ps,
        };

        _pso = device.CreateGraphicsPipelineState(psoDesc);

        if (_pso == null)
            throw new Exception("Failed to create SimpleMesh PSO");

        _pso!.GetStaticVariableByName(ShaderType.Vertex, "g_Constants")
            ?.Set(_cb!, SetShaderResourceFlags.None);
        _srb = _pso!.CreateShaderResourceBinding(true);
    }

    public override void Execute(RenderContext context, RenderGraphContext? graphContext)
    {
        if (!_initialized)
            Init();

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var swapChain = _context.SwapChain;
        if (swapChain == null)
            return;
        var rtv = swapChain.GetCurrentBackBufferRTV();
        var dsv = _context.DepthBufferDSV!;
        if (rtv == null || dsv == null)
            return;

        ctx.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Verify);
        ctx.SetPipelineState(_pso);
        ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Verify);

        {
            float time = (float)(DateTime.Now.Ticks / 10000000.0);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4,
                swapChain.GetDesc().Width / (float)swapChain.GetDesc().Height,
                0.1f,
                100f
            );
            var world = Matrix4x4.CreateRotationY(time) * Matrix4x4.CreateRotationX(time * 0.5f);
            var wvp = world * view * proj;

            unsafe
            {
                IntPtr pData = ctx.MapBuffer(_cb, MapType.Write, MapFlags.Discard);
                Constants* pConsts = (Constants*)pData;
                pConsts->WorldViewProj = Matrix4x4.Transpose(wvp);
                pConsts->Color = new Vector4(1, 0, 0, 1);
                ctx.UnmapBuffer(_cb, MapType.Write);
            }
        }

        ctx.SetVertexBuffers(
            0,
            [_vb],
            offsets,
            ResourceStateTransitionMode.Verify,
            SetVertexBuffersFlags.None
        );
        ctx.SetIndexBuffer(_ib, 0, ResourceStateTransitionMode.Verify);

        var drawAttrs = new DrawIndexedAttribs
        {
            NumIndices = (uint)_indexCount,
            IndexType = Diligent.ValueType.UInt32,
            Flags = DrawFlags.VerifyAll,
        };
        ctx.DrawIndexed(drawAttrs);
    }

    public void Dispose()
    {
        _pso?.Dispose();
        _srb?.Dispose();
        _vb?.Dispose();
        _ib?.Dispose();
        _cb?.Dispose();
    }
}
