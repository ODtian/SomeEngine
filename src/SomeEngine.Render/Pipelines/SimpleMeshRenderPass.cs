using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public class SimpleMeshRenderPass : RenderPass, IDisposable
{
    private readonly RenderContext _context;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private IBuffer? _vb;
    private IBuffer? _ib;
    private IBuffer? _cb;
    private int _indexCount;
    private bool _initialized;

    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Matrix4x4 WorldViewProj;
        public Vector4 Color;
    }

    public SimpleMeshRenderPass(RenderContext context) : base("SimpleMeshPass")
    {
        _context = context;
    }

    public void Init()
    {
        if (_initialized) return;

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

        unsafe 
        {
            fixed (float* pVerts = vertData)
            {
                var vbDesc = new BufferDesc
                {
                    Name = "SimpleMesh VB",
                    Usage = Usage.Default,
                    BindFlags = BindFlags.VertexBuffer,
                    Size = (ulong)(vertData.Length * sizeof(float))
                };
                _vb = device.CreateBuffer(vbDesc);
                _context.ImmediateContext.UpdateBuffer(_vb, 0, vbDesc.Size, (IntPtr)pVerts, ResourceStateTransitionMode.Transition);
            }

            fixed (uint* pInds = indices)
            {
                var ibDesc = new BufferDesc
                {
                    Name = "SimpleMesh IB",
                    Usage = Usage.Default,
                    BindFlags = BindFlags.IndexBuffer,
                    Size = (ulong)(indices.Length * sizeof(uint))
                };
                _ib = device.CreateBuffer(ibDesc);
                _context.ImmediateContext.UpdateBuffer(_ib, 0, ibDesc.Size, (IntPtr)pInds, ResourceStateTransitionMode.Transition);
            }
        }

        var cbDesc = new BufferDesc
        {
            Name = "SimpleMesh Constants",
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            Size = (ulong)Marshal.SizeOf<Constants>()
        };
        _cb = device.CreateBuffer(cbDesc);
    }

    private void CreatePSO()
    {
        var device = _context.Device;
        
        using var shaderSourceFactory = _context.Factory?.CreateDefaultShaderSourceStreamFactory("assets/Shaders");
        
        var vsCI = new ShaderCreateInfo
        {
            FilePath = "simple_mesh.hlsl",
            EntryPoint = "VSMain",
            Desc = new ShaderDesc { Name = "SimpleMesh VS", ShaderType = ShaderType.Vertex },
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderSourceStreamFactory = shaderSourceFactory
        };
        using var vs = device.CreateShader(vsCI, out _);

        var psCI = new ShaderCreateInfo
        {
            FilePath = "simple_mesh.hlsl",
            EntryPoint = "PSMain",
            Desc = new ShaderDesc { Name = "SimpleMesh PS", ShaderType = ShaderType.Pixel },
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderSourceStreamFactory = shaderSourceFactory
        };
        using var ps = device.CreateShader(psCI, out _);

        var layoutElements = new[]
        {
            new LayoutElement
            {
                InputIndex = 0,
                BufferSlot = 0,
                NumComponents = 3,
                ValueType = Diligent.ValueType.Float32,
                IsNormalized = false
            }
        };

        var psoDesc = new GraphicsPipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "SimpleMesh PSO",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Static,
                    Variables = new[] 
                    { 
                        new ShaderResourceVariableDesc 
                        { 
                            Name = "Constants", 
                            Type = ShaderResourceVariableType.Static,
                            ShaderStages = ShaderType.Vertex | ShaderType.Pixel
                        } 
                    }
                }
            },
            GraphicsPipeline = new GraphicsPipelineDesc
            {
                NumRenderTargets = 1,
                RTVFormats = new[] { _context.SwapChain!.GetDesc().ColorBufferFormat },
                DSVFormat = _context.SwapChain!.GetDesc().DepthBufferFormat,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc { CullMode = CullMode.Back },
                DepthStencilDesc = new DepthStencilStateDesc { DepthEnable = true, DepthWriteEnable = true },
                InputLayout = new InputLayoutDesc { LayoutElements = layoutElements }
            },
            Vs = vs,
            Ps = ps
        };

        _pso = device.CreateGraphicsPipelineState(psoDesc);
        
        if (_pso == null)
            throw new Exception("Failed to create SimpleMesh PSO");

        _pso.GetStaticVariableByName(ShaderType.Vertex, "Constants")?.Set(_cb, SetShaderResourceFlags.None);
        _srb = _pso.CreateShaderResourceBinding(true);
    }

    public override void Execute(RenderContext context, RenderGraphContext? graphContext)
    {
        if (!_initialized) Init();

        var ctx = context.ImmediateContext;
        if (ctx == null) return;

        var swapChain = _context.SwapChain;
        var rtv = swapChain.GetCurrentBackBufferRTV();
        var dsv = swapChain.GetDepthBufferDSV();

        ctx.SetRenderTargets(new[] { rtv }, dsv, ResourceStateTransitionMode.Transition);
        ctx.SetPipelineState(_pso);
        ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Transition);

        {
            float time = (float)(DateTime.Now.Ticks / 10000000.0);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, swapChain.GetDesc().Width / (float)swapChain.GetDesc().Height, 0.1f, 100f);
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

        ctx.SetVertexBuffers(0, new[] { _vb }, new[] { 0ul }, ResourceStateTransitionMode.Transition, SetVertexBuffersFlags.None);
        ctx.SetIndexBuffer(_ib, 0, ResourceStateTransitionMode.Transition);

        var drawAttrs = new DrawIndexedAttribs
        {
            NumIndices = (uint)_indexCount,
            IndexType = Diligent.ValueType.UInt32,
            Flags = DrawFlags.VerifyAll
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
