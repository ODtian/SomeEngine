using Diligent;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
struct CullingUniforms
{
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos;
    public uint PageOffset;

    public float LodThreshold;
    public float LodScale;
    public uint PageID;
    public uint BaseInstanceID;
    public int ForcedLODLevel;
}

[StructLayout(LayoutKind.Sequential)]
struct DrawUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public uint PageTableSize;
    public uint DebugMode;
    public Vector2 Pad;
}

[StructLayout(LayoutKind.Sequential)]
struct CopyUniforms
{
    public uint SphereVertexCount;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

public class ClusterRenderPass : RenderPass, IDisposable
{
    private readonly RenderContext _context;
    private readonly TransformSyncSystem _transformSystem;
    private readonly ClusterResourceManager _clusterManager;

    private IPipelineState? _computePSO;
    private IShaderResourceBinding? _computeSRB;

    private IPipelineState? _drawPSO;
    private IPipelineState? _drawWireframePSO;
    private IPipelineState? _drawOverdrawPSO;
    private IShaderResourceBinding? _drawSRB; // Shared SRB if layout is same
    private IShaderResourceBinding? _drawWireframeSRB; // Need separate SRB if PSO is different
    private IShaderResourceBinding? _drawOverdrawSRB;

    private IPipelineState? _debugSpherePSO;
    private IShaderResourceBinding? _debugSphereSRB;

    private IPipelineState? _debugCopyPSO;
    private IShaderResourceBinding? _debugCopySRB;

    private IBuffer? _indirectArgsBuffer;
    private IBuffer? _debugIndirectArgsBuffer;
    private IBuffer? _drawCountBuffer;
    private IBuffer? _cullingUniformBuffer;
    private IBuffer? _drawUniformBuffer;
    private IBuffer? _copyUniformBuffer;

    private bool _initialized = false;
    public bool DebugClusterID { get; set; } = false;
    private uint _maxDraws = 100000; // Cap for now

    private Matrix4x4 _view = Matrix4x4.CreateLookAt(
        new Vector3(0, 0, -3),
        Vector3.Zero,
        Vector3.UnitY
    );
    private Matrix4x4 _proj = Matrix4x4.CreatePerspectiveFieldOfView(
        MathF.PI / 4.0f,
        16.0f / 9.0f,
        0.1f,
        1000.0f
    );
    private Vector3 _cameraPos = new Vector3(0, 0, -3);
    private float _lodThreshold = 1.0f;
    private float _lodScale = 500.0f;
    private int _forcedLODLevel = -1;

    public void Dispose()
    {
        _indirectArgsBuffer?.Dispose();
        _debugIndirectArgsBuffer?.Dispose();
        _drawCountBuffer?.Dispose();
        _cullingUniformBuffer?.Dispose();
        _drawUniformBuffer?.Dispose();
        _copyUniformBuffer?.Dispose();
        _computeSRB?.Dispose();
        _computePSO?.Dispose();
        _drawSRB?.Dispose();
        _drawPSO?.Dispose();
        _drawWireframeSRB?.Dispose();
        _drawWireframePSO?.Dispose();
        _drawOverdrawSRB?.Dispose();
        _drawOverdrawPSO?.Dispose();
        _debugSphereSRB?.Dispose();
        _debugSpherePSO?.Dispose();
        _debugCopySRB?.Dispose();
        _debugCopyPSO?.Dispose();
    }

    public bool WireframeEnabled { get; set; } = false;
    public bool OverdrawEnabled { get; set; } = false;
    public bool DebugSpheresEnabled { get; set; } = false;

    public ClusterRenderPass(
        RenderContext context,
        TransformSyncSystem transformSystem,
        ClusterResourceManager clusterManager
    )
        : base("ClusterPass")
    {
        _context = context;
        _transformSystem = transformSystem;
        _clusterManager = clusterManager;
    }

    public void SetCamera(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        in Vector3 cameraPos,
        float lodThreshold,
        float lodScale,
        int forcedLODLevel = -1
    )
    {
        _view = view;
        _proj = proj;
        _cameraPos = cameraPos;
        _lodThreshold = lodThreshold;
        _lodScale = lodScale;
        _forcedLODLevel = forcedLODLevel;
    }

    public void Init()
    {
        if (_initialized)
            return;

        var device = _context.Device;
        if (device == null)
            return;

        // 1. Create Buffers
        // Indirect Args Buffer: Only 1 element needed for Single Draw Instanced
        // Indirect Layout: { VertexCount, InstanceCount, StartVertex, StartInstance
        // }
        BufferDesc argsDesc = new BufferDesc {
            Name = "Indirect Args Buffer",
            Size = 256, // Plenty of space
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw, // For ByteAddressBuffer
        };
        _indirectArgsBuffer = device.CreateBuffer(argsDesc);

        // Request Buffer: Stores the list of visible clusters (PageID, ClusterID,
        // InstanceID)
        BufferDesc reqDesc = new BufferDesc {
            Name = "Request Buffer",
            Size = (ulong)(_maxDraws * 16), // 4 uints * maxDraws
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16
        };
        // Reuse _drawCountBuffer name? No, let's add a new field or reuse
        // _drawCountBuffer variable as _requestBuffer since we don't need a separate
        // count buffer anymore. Actually, let's keep it clean. I'll add
        // _requestBuffer to the class.
        _drawCountBuffer?.Dispose(); // Not used in this approach
        _drawCountBuffer = device.CreateBuffer(
            reqDesc
        ); // Using _drawCountBuffer variable to hold RequestBuffer to minimize code
           // changes in Dispose

        BufferDesc ubDesc = new BufferDesc {
            Name = "Culling Uniforms",
            Size = (ulong)Marshal.SizeOf<CullingUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _cullingUniformBuffer = device.CreateBuffer(ubDesc);

        BufferDesc dubDesc = new BufferDesc {
            Name = "Draw Uniforms",
            Size = 256, // SizeOf DrawUniforms (approx 64+64+4 aligned to 256)
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _drawUniformBuffer = device.CreateBuffer(dubDesc);

        // 2. Compute Pipeline
        ComputePipelineStateCreateInfo cppsCi = new ComputePipelineStateCreateInfo();
        cppsCi.PSODesc.Name = "Cluster Culling PSO";
        cppsCi.PSODesc.PipelineType = PipelineType.Compute;

        // Shader
        using var shaderFactory =
            _context.Factory?.CreateDefaultShaderSourceStreamFactory(
                "assets/Shaders"
            );
        ShaderCreateInfo creationAttrs = new ShaderCreateInfo {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            Desc =
                new ShaderDesc {
                    ShaderType = ShaderType.Compute, Name = "Cluster Culling CS"
                },
            EntryPoint = "main",
            FilePath = "cluster_cull.cs.hlsl",
            ShaderSourceStreamFactory = shaderFactory
        };
        var cs = device.CreateShader(creationAttrs, out _);
        cppsCi.Cs = cs;

        // Resources
        cppsCi.PSODesc.ResourceLayout.DefaultVariableType =
            ShaderResourceVariableType.Static;

        ShaderResourceVariableDesc[] vars = {
            new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "PageBuffer",
                Type = ShaderResourceVariableType.Mutable
            },
            new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "IndirectDrawArgs",
                Type = ShaderResourceVariableType.Mutable
            }, // Was DrawArgs
            new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "RequestBuffer",
                Type = ShaderResourceVariableType.Mutable
            },
            new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "CullingUniforms",
                Type = ShaderResourceVariableType.Static
            }
        };
        cppsCi.PSODesc.ResourceLayout.Variables = vars;

        cppsCi.PSODesc.ResourceLayout.DefaultVariableType =
            ShaderResourceVariableType.Mutable;

        _computePSO = device.CreateComputePipelineState(cppsCi);
        if (_computePSO != null)
        {
            // Bind Static Uniform
            _computePSO
                .GetStaticVariableByName(ShaderType.Compute, "CullingUniforms")
                ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);
            _computeSRB = _computePSO.CreateShaderResourceBinding(true);
        }

        // 3. Draw Pipeline (Solid)
        GraphicsPipelineStateCreateInfo psCi = new GraphicsPipelineStateCreateInfo();
        psCi.PSODesc.Name = "Cluster Draw PSO";
        psCi.PSODesc.PipelineType = PipelineType.Graphics;

        // Render Target formats (Should match SwapChain)
        psCi.GraphicsPipeline.NumRenderTargets = 1;
        psCi.GraphicsPipeline.RTVFormats[0] = TextureFormat.RGBA8_UNorm;
        psCi.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;

        // Input Layout
        // Using Vertex Pulling (Manual Fetch), so NO Input Layout
        psCi.GraphicsPipeline.InputLayout.LayoutElements = new LayoutElement[0];
        psCi.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.TriangleList;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = true;

        // Shaders
        creationAttrs.Desc.ShaderType = ShaderType.Vertex;
        creationAttrs.Desc.Name = "Cluster Draw VS";
        creationAttrs.EntryPoint = "VSMain";
        creationAttrs.FilePath = "cluster_draw.hlsl";
        var vs = device.CreateShader(creationAttrs, out _);
        psCi.Vs = vs;

        creationAttrs.Desc.ShaderType = ShaderType.Pixel;
        creationAttrs.Desc.Name = "Cluster Draw PS";
        creationAttrs.EntryPoint = "PSMain";
        creationAttrs.FilePath = "cluster_draw.hlsl";
        var ps = device.CreateShader(creationAttrs, out _);
        psCi.Ps = ps;

        psCi.PSODesc.ResourceLayout.DefaultVariableType =
            ShaderResourceVariableType.Mutable;

        _drawPSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawPSO != null)
        {
            _drawSRB = _drawPSO.CreateShaderResourceBinding(true);
        }

        // 4. Draw Pipeline (Wireframe)
        psCi.PSODesc.Name = "Cluster Draw Wireframe PSO";
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None; // Optional: show backfaces in wireframe
        
        _drawWireframePSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawWireframePSO != null)
        {
            _drawWireframeSRB = _drawWireframePSO.CreateShaderResourceBinding(true);
        }

        // 5. Draw Pipeline (Overdraw)
        psCi.PSODesc.Name = "Cluster Draw Overdraw PSO";
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back; // Still cull backfaces? Yes, usually.
        psCi.GraphicsPipeline.DepthStencilDesc.DepthEnable = false; // Disable depth test to see all layers
        psCi.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        
        // Additive Blending
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.One;
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.One;

        // Use PSOverdraw
        creationAttrs.EntryPoint = "PSOverdraw";
        var psOverdraw = device.CreateShader(creationAttrs, out _);
        psCi.Ps = psOverdraw;

        _drawOverdrawPSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawOverdrawPSO != null)
        {
            _drawOverdrawSRB = _drawOverdrawPSO.CreateShaderResourceBinding(true);
        }

        // 6. Debug Pipelines
        // Debug Indirect Args Buffer (Reuse argsDesc)
        _debugIndirectArgsBuffer = device.CreateBuffer(argsDesc);

        // Copy Uniforms Buffer
        BufferDesc cubDesc = new BufferDesc {
            Name = "Copy Uniforms",
            Size = 16,
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _copyUniformBuffer = device.CreateBuffer(cubDesc);

        // Debug Copy PSO
        ComputePipelineStateCreateInfo dcCi = new ComputePipelineStateCreateInfo();
        dcCi.PSODesc.Name = "Debug Copy PSO";
        dcCi.PSODesc.PipelineType = PipelineType.Compute;

        creationAttrs.Desc.ShaderType = ShaderType.Compute;
        creationAttrs.Desc.Name = "Debug Copy CS";
        creationAttrs.EntryPoint = "main";
        creationAttrs.FilePath = "debug_args_copy.cs.hlsl";
        var copyCS = device.CreateShader(creationAttrs, out _);
        dcCi.Cs = copyCS;

        dcCi.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Mutable;
        ShaderResourceVariableDesc[] copyVars = {
             new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "IndirectArgs",
                Type = ShaderResourceVariableType.Mutable
            },
            new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "DebugArgs",
                Type = ShaderResourceVariableType.Mutable
            },
             new ShaderResourceVariableDesc {
                ShaderStages = ShaderType.Compute,
                Name = "CopyUniforms",
                Type = ShaderResourceVariableType.Static
            }
        };
        dcCi.PSODesc.ResourceLayout.Variables = copyVars;

        _debugCopyPSO = device.CreateComputePipelineState(dcCi);
        if (_debugCopyPSO != null)
        {
            _debugCopyPSO.GetStaticVariableByName(ShaderType.Compute, "CopyUniforms")
                ?.Set(_copyUniformBuffer, SetShaderResourceFlags.None);
            _debugCopySRB = _debugCopyPSO.CreateShaderResourceBinding(true);
        }

        // Debug Sphere PSO
        // Reuse psCi (GraphicsPipelineStateCreateInfo)
        // Reset necessary fields
        psCi.PSODesc.Name = "Debug Sphere PSO";
        psCi.GraphicsPipeline.NumRenderTargets = 1;
        psCi.GraphicsPipeline.RTVFormats[0] = TextureFormat.RGBA8_UNorm;
        psCi.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;
        psCi.GraphicsPipeline.InputLayout.LayoutElements = new LayoutElement[0];
        psCi.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.TriangleList;
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.SrcAlpha;
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.InvSrcAlpha;

        creationAttrs.Desc.ShaderType = ShaderType.Vertex;
        creationAttrs.Desc.Name = "Debug Sphere VS";
        creationAttrs.EntryPoint = "VSMain";
        creationAttrs.FilePath = "debug_sphere.hlsl";
        var debugVS = device.CreateShader(creationAttrs, out _);
        psCi.Vs = debugVS;

        creationAttrs.Desc.ShaderType = ShaderType.Pixel;
        creationAttrs.Desc.Name = "Debug Sphere PS";
        creationAttrs.EntryPoint = "PSMain";
        creationAttrs.FilePath = "debug_sphere.hlsl";
        var debugPS = device.CreateShader(creationAttrs, out _);
        psCi.Ps = debugPS;

        psCi.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Mutable;
        
        _debugSpherePSO = device.CreateGraphicsPipelineState(psCi);
        if (_debugSpherePSO != null)
        {
            _debugSphereSRB = _debugSpherePSO.CreateShaderResourceBinding(true);
        }

        _initialized = true;
    }

    public override void Execute(
        RenderContext context, RenderGraphContext graphContext
    )
    {
        if (!_initialized)
            Init();
        if (_computeSRB == null || _drawSRB == null || _computePSO == null ||
            _drawPSO == null || _indirectArgsBuffer == null ||
            _drawCountBuffer == null || _cullingUniformBuffer == null ||
            _drawUniformBuffer == null || _drawWireframePSO == null || _drawWireframeSRB == null ||
            _drawOverdrawPSO == null || _drawOverdrawSRB == null)
            return;

        var device = context.Device;
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        // 0. Update Uniforms
        var viewProj = _view * _proj;
        var viewProjT = Matrix4x4.Transpose(viewProj);
        var viewT = Matrix4x4.Transpose(_view);

        // Reset Indirect Args: { VertexCount=372, InstanceCount=0, StartVertex=0,
        // StartInstance=0 }
        Span<uint> resetArgs = [372, 0, 0, 0]; // 372 = 124 triangles * 3

        ctx.UpdateBuffer(
            _indirectArgsBuffer, 0, resetArgs, ResourceStateTransitionMode.Transition
        );

        // 1. Dispatch Compute
        var pageHeap = _clusterManager.PageHeap;
        var pageTable = _clusterManager.PageTableBuffer;
        if (pageHeap == null || pageTable == null)
            return;

        // Bind Resources
        _computeSRB.GetVariableByName(ShaderType.Compute, "PageBuffer")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _computeSRB.GetVariableByName(ShaderType.Compute, "IndirectDrawArgs")
            ?.Set(
                _indirectArgsBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _computeSRB.GetVariableByName(ShaderType.Compute, "RequestBuffer")
            ?.Set(
                _drawCountBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            ); // _drawCountBuffer is RequestBuffer

        ctx.SetPipelineState(_computePSO);
        ctx.CommitShaderResources(
            _computeSRB, ResourceStateTransitionMode.Transition
        );

        // Loop over pages and dispatch
        uint totalDispatches = 0;

        // Map buffer once if possible? No, we need to change PageOffset per
        // dispatch.

        // Unless we use an array of offsets in Uniforms and Index into it using
        // SV_GroupID? But shader uses constant PageOffset. So we must update buffer
        // per dispatch.

        foreach (var pageList in _clusterManager.PageRegistry.Values)
        {
            foreach (var page in pageList)
            {
                var mappedDataSpan = ctx.MapBuffer<CullingUniforms>(
                    _cullingUniformBuffer, MapType.Write, MapFlags.Discard
                );
                mappedDataSpan[0] = new CullingUniforms {
                    ViewProj = viewProjT,
                    CameraPos = _cameraPos,
                    PageOffset = page.Offset,
                    LodThreshold = _lodThreshold,
                    LodScale = _lodScale,
                    PageID = page.PageID,
                    BaseInstanceID = 0,
                    ForcedLODLevel = _forcedLODLevel
                };

                ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);

                // Dispatch
                uint groupSize = 64;
                // Check DispatchComputeAttribs syntax
                ctx.DispatchCompute(new DispatchComputeAttribs {
                    ThreadGroupCountX =
                        (page.ClusterCount + groupSize - 1) / groupSize
                });

                totalDispatches++;
                if (totalDispatches >= _maxDraws)
                    break;
            }
            if (totalDispatches >= _maxDraws)
                break;
        }

        // 2. Draw

        var drawUniformSpan = ctx.MapBuffer<DrawUniforms>(
            _drawUniformBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        drawUniformSpan[0] = new DrawUniforms {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = _clusterManager.PageCount,
            DebugMode = DebugClusterID ? 1u : 0u,
            Pad = Vector2.Zero
        };
        ctx.UnmapBuffer(_drawUniformBuffer, MapType.Write);

        IPipelineState? currentDrawPSO;
        IShaderResourceBinding? currentDrawSRB;

        if (OverdrawEnabled)
        {
            currentDrawPSO = _drawOverdrawPSO;
            currentDrawSRB = _drawOverdrawSRB;
            // Clear to black for additive blending to work correctly
            // Note: We cannot easily clear the render target here as it is bound by the caller (RenderGraph).
            // We assume the user toggling Overdraw understands the output might need a dark background.
            // Or we could perform a clear if we had access to the RTV. 
            // context.ImmediateContext.ClearRenderTarget(..., new Vector4(0,0,0,1));
        }
        else if (WireframeEnabled)
        {
            currentDrawPSO = _drawWireframePSO;
            currentDrawSRB = _drawWireframeSRB;
        }
        else
        {
            currentDrawPSO = _drawPSO;
            currentDrawSRB = _drawSRB;
        }

        ctx.SetPipelineState(currentDrawPSO);

        // Bind Draw Uniforms
        currentDrawSRB.GetVariableByName(ShaderType.Vertex, "DrawUniforms")
            ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
        currentDrawSRB.GetVariableByName(ShaderType.Pixel, "DrawUniforms")
            ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);

        // Bind PageHeap to VS (Corrected name from PageBuffer)
        currentDrawSRB.GetVariableByName(ShaderType.Vertex, "PageHeap")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        // Bind Instances
        if (_transformSystem != null &&
            _transformSystem.GlobalTransformBuffer != null)
        {
            currentDrawSRB.GetVariableByName(ShaderType.Vertex, "Instances")
                ?.Set(
                    _transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
        }

        // Bind RequestBuffer to VS
        currentDrawSRB.GetVariableByName(ShaderType.Vertex, "RequestBuffer")
            ?.Set(
                _drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        // Bind PageTable to VS (Missing in previous code)
        currentDrawSRB.GetVariableByName(ShaderType.Vertex, "PageTable")
            ?.Set(
                pageTable.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        ctx.CommitShaderResources(currentDrawSRB, ResourceStateTransitionMode.Transition);

        // Execute Indirect (Single Draw)
        DrawIndirectAttribs drawAttrs = new DrawIndirectAttribs {
            Flags = DrawFlags.None,
            AttribsBuffer = _indirectArgsBuffer,
            DrawArgsOffset = 0,
            DrawCount = 1 // One "Instanced" Draw executing all clusters
        };
        ctx.DrawIndirect(drawAttrs);

        // 3. Debug Draw
        if (DebugSpheresEnabled && _debugCopySRB != null && _debugSphereSRB != null && _debugCopyPSO != null && _debugSpherePSO != null)
        {
            // 1. Copy Args
            var copyMap = ctx.MapBuffer<CopyUniforms>(_copyUniformBuffer, MapType.Write, MapFlags.Discard);
            copyMap[0] = new CopyUniforms { SphereVertexCount = 1536 }; // 16x16x2x3 = 1536 for Lat-Long Sphere
            ctx.UnmapBuffer(_copyUniformBuffer, MapType.Write);

            _debugCopySRB.GetVariableByName(ShaderType.Compute, "IndirectArgs")
                ?.Set(_indirectArgsBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
             _debugCopySRB.GetVariableByName(ShaderType.Compute, "DebugArgs")
                ?.Set(_debugIndirectArgsBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            
            ctx.SetPipelineState(_debugCopyPSO);
            ctx.CommitShaderResources(_debugCopySRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

            // 2. Draw Spheres
            ctx.SetPipelineState(_debugSpherePSO);
            
            _debugSphereSRB.GetVariableByName(ShaderType.Vertex, "DrawUniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariableByName(ShaderType.Vertex, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariableByName(ShaderType.Vertex, "PageTable")
                ?.Set(pageTable.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariableByName(ShaderType.Vertex, "RequestBuffer")
                ?.Set(_drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            
            ctx.CommitShaderResources(_debugSphereSRB, ResourceStateTransitionMode.Transition);

            DrawIndirectAttribs debugDrawAttrs = new DrawIndirectAttribs {
                Flags = DrawFlags.None,
                AttribsBuffer = _debugIndirectArgsBuffer,
                DrawArgsOffset = 0,
                DrawCount = 1
            };
            ctx.DrawIndirect(debugDrawAttrs);
        }
    }
}
