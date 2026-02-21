using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
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
    public uint InstanceCount;
    public Vector2 Pad;
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

    private ShaderAsset? _cullAsset;
    private ShaderAsset? _drawAsset;
    private ShaderAsset? _copyAsset;
    private ShaderAsset? _debugSphereAsset;

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

        // Slang Shader Import
        string cullSlangPath = Path.Combine(AppContext.BaseDirectory, "assets", "Shaders", "cluster_cull.slang");
        if (!File.Exists(cullSlangPath))
        {
             // Fallback: search up from bin/Debug/net10.0/.../src/SomeEngine.Runtime/../../../../assets
             // BaseDirectory: .../SomeEngine.Runtime/bin/Debug/net10.0/
             // Go up 5 levels to root: ../../../../..
             cullSlangPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/cluster_cull.slang"));
        }
        
        _cullAsset = SlangShaderImporter.Import(cullSlangPath);
        using var cs = _cullAsset.CreateShader(_context, "main");
        cppsCi.Cs = cs;

        // Resources
        cppsCi.PSODesc.ResourceLayout.DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable;
        
        // Use reflected variables instead of manual definition
        cppsCi.PSODesc.ResourceLayout.Variables = _cullAsset.GetResourceVariables(_context);

        _computePSO = device.CreateComputePipelineState(cppsCi);
        if (_computePSO != null)
        {
            // Bind Static Uniform by name using reflection extension
            _computePSO.GetStaticVariable(_context, _cullAsset, ShaderType.Compute, "CullingUniformsBuffer")
                ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);
            _computeSRB = _computePSO.CreateShaderResourceBinding(true);
        }

        // 3. Draw Pipeline (Solid)
        GraphicsPipelineStateCreateInfo psCi = new GraphicsPipelineStateCreateInfo();
        psCi.PSODesc.Name = "Cluster Draw PSO";
        psCi.PSODesc.PipelineType = PipelineType.Graphics;

        // Render Target formats (Should match SwapChain)
        psCi.GraphicsPipeline.NumRenderTargets = 1;
        psCi.GraphicsPipeline.RTVFormats = new[] { TextureFormat.RGBA8_UNorm };
        psCi.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;

        // Input Layout
        // Using Vertex Pulling (Manual Fetch), so NO Input Layout
        psCi.GraphicsPipeline.InputLayout.LayoutElements = new LayoutElement[0];
        psCi.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.TriangleList;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        psCi.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = true;

        // Shaders
        string drawSlangPath = Path.Combine(AppContext.BaseDirectory, "assets", "Shaders", "cluster_draw.slang");
        if (!File.Exists(drawSlangPath))
        {
             drawSlangPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/Shaders/cluster_draw.slang"));
        }
        
        _drawAsset = SlangShaderImporter.Import(drawSlangPath);
        using var vs = _drawAsset.CreateShader(_context, "VSMain");
        using var ps = _drawAsset.CreateShader(_context, "PSMain");
        
        psCi.Vs = vs;
        psCi.Ps = ps;

        psCi.PSODesc.ResourceLayout.DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable;
        psCi.PSODesc.ResourceLayout.Variables = _drawAsset.GetResourceVariables(_context);

        _drawPSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawPSO != null)
        {
            _drawPSO.GetStaticVariable(_context, _drawAsset, ShaderType.Vertex, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawPSO.GetStaticVariable(_context, _drawAsset, ShaderType.Pixel, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawSRB = _drawPSO.CreateShaderResourceBinding(true);
        }

        // 4. Draw Pipeline (Wireframe)
        psCi.PSODesc.Name = "Cluster Draw Wireframe PSO";
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None; // Optional: show backfaces in wireframe
        
        _drawWireframePSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawWireframePSO != null)
        {
             _drawWireframePSO.GetStaticVariable(_context, _drawAsset, ShaderType.Vertex, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
             _drawWireframePSO.GetStaticVariable(_context, _drawAsset, ShaderType.Pixel, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
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
        using var psOverdraw = _drawAsset.CreateShader(_context, "PSOverdraw");
        psCi.Ps = psOverdraw;

        _drawOverdrawPSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawOverdrawPSO != null)
        {
             _drawOverdrawPSO.GetStaticVariable(_context, _drawAsset, ShaderType.Vertex, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
             _drawOverdrawPSO.GetStaticVariable(_context, _drawAsset, ShaderType.Pixel, "DrawUniformsBuffer")?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawOverdrawSRB = _drawOverdrawPSO.CreateShaderResourceBinding(true);
        }

        // 6. Debug Pipelines
        // Debug Indirect Args Buffer (Reuse argsDesc)
        var debugArgsDesc = argsDesc;
        debugArgsDesc.Name = "Debug Indirect Args Buffer";
        _debugIndirectArgsBuffer = device.CreateBuffer(debugArgsDesc);

        // Re-create creationAttrs for HLSL debug shaders
        using var debugShaderFactory = _context.Factory?.CreateDefaultShaderSourceStreamFactory("assets/Shaders");
        ShaderCreateInfo creationAttrs = new ShaderCreateInfo {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderSourceStreamFactory = debugShaderFactory
        };

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

        string copySlangPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_args_copy.cs.hlsl"));
        _copyAsset = SlangShaderImporter.Import(copySlangPath);
        using var copyCS = _copyAsset.CreateShader(_context, "main");
        dcCi.Cs = copyCS;

        dcCi.PSODesc.ResourceLayout.DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable;
        dcCi.PSODesc.ResourceLayout.Variables = _copyAsset.GetResourceVariables(_context);

        _debugCopyPSO = device.CreateComputePipelineState(dcCi);
        if (_debugCopyPSO != null)
        {   
            _debugCopyPSO.GetStaticVariable(_context, _copyAsset, ShaderType.Compute, "CopyUniforms")
                ?.Set(_copyUniformBuffer, SetShaderResourceFlags.None);
            _debugCopySRB = _debugCopyPSO.CreateShaderResourceBinding(true);
        }

        // Debug Sphere PSO
        // Reuse psCi (GraphicsPipelineStateCreateInfo)
        // Reset necessary fields
        psCi.PSODesc.Name = "Debug Sphere PSO";
        // ... (rest of psCi setup)
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

        string debugSpherePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_sphere.hlsl"));
        _debugSphereAsset = SlangShaderImporter.Import(debugSpherePath);
        using var debugVS = _debugSphereAsset.CreateShader(_context, "VSMain");
        using var debugPS = _debugSphereAsset.CreateShader(_context, "PSMain");
        psCi.Vs = debugVS;
        psCi.Ps = debugPS;

        psCi.PSODesc.ResourceLayout.DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable;
        psCi.PSODesc.ResourceLayout.Variables = _debugSphereAsset.GetResourceVariables(_context);
        
        _debugSpherePSO = device.CreateGraphicsPipelineState(psCi);
        if (_debugSpherePSO != null)
        {
            _debugSpherePSO.GetStaticVariable(_context, _debugSphereAsset, ShaderType.Vertex, "DrawUniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
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
        _computeSRB.GetVariable(_context, _cullAsset, ShaderType.Compute, "PageBuffer")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        
        _computeSRB.GetVariable(_context, _cullAsset, ShaderType.Compute, "IndirectDrawArgs")
            ?.Set(_indirectArgsBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            
        _computeSRB.GetVariable(_context, _cullAsset, ShaderType.Compute, "RequestBuffer")
            ?.Set(_drawCountBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        if (_transformSystem.GlobalTransformBuffer != null)
        {
             _computeSRB.GetVariable(_context, _cullAsset, ShaderType.Compute, "InstanceData")
                ?.Set(_transformSystem.GlobalTransformBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }

        ctx.SetPipelineState(_computePSO);
        ctx.CommitShaderResources(_computeSRB, ResourceStateTransitionMode.Transition);

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
                    ForcedLODLevel = _forcedLODLevel,
                    InstanceCount = (uint)_transformSystem.Count,
                    Pad = Vector2.Zero
                };

                ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);

                // Dispatch
                uint groupSize = 64;
                // Check DispatchComputeAttribs syntax
                if (_transformSystem.Count > 0)
                {
                    ctx.DispatchCompute(new DispatchComputeAttribs {
                        ThreadGroupCountX = (page.ClusterCount + groupSize - 1) / groupSize,
                        ThreadGroupCountY = (uint)_transformSystem.Count,
                        ThreadGroupCountZ = 1
                    });
                    totalDispatches++;
                }

                if (totalDispatches >= _maxDraws)
                    break;
            }
            if (totalDispatches >= _maxDraws)
                break;
        }
        // Transition IndirectArgs to IndirectArgument state
        ctx.TransitionResourceStates( [new StateTransitionDesc{
            Resource = _indirectArgsBuffer,
            OldState = ResourceState.Unknown,
            NewState = ResourceState.IndirectArgument,
            Flags = StateTransitionFlags.UpdateState
        }]);
        // ctx.TransitionResourceStates(1, [new StateTransitionDesc(_indirectArgsBuffer, ResourceState.Unknown, ResourceState.IndirectArgument, StateTransitionFlags.UpdateState)]);

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

        // Bind PageHeap to VS
        currentDrawSRB.GetVariable(_context, _drawAsset, ShaderType.Vertex, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        // Bind Instances
        if (_transformSystem != null && _transformSystem.GlobalTransformBuffer != null)
        {
            currentDrawSRB.GetVariable(_context, _drawAsset, ShaderType.Vertex, "Instances")
                ?.Set(_transformSystem.GlobalTransformBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }

        // Bind RequestBuffer to VS
        currentDrawSRB.GetVariable(_context, _drawAsset, ShaderType.Vertex, "RequestBuffer")
            ?.Set(_drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        // Bind PageTable to VS
        currentDrawSRB.GetVariable(_context, _drawAsset, ShaderType.Vertex, "PageTable")
            ?.Set(pageTable.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        ctx.CommitShaderResources(currentDrawSRB, ResourceStateTransitionMode.Transition);

        // Execute Indirect (Single Draw)
        DrawIndirectAttribs drawAttrs = new DrawIndirectAttribs {
            Flags = DrawFlags.VerifyAll,
            AttribsBuffer = _indirectArgsBuffer,
            DrawArgsOffset = 0
        };
        ctx.DrawIndirect(drawAttrs);

        // 3. Debug Draw
        if (DebugSpheresEnabled && _debugCopySRB != null && _debugSphereSRB != null && _debugCopyPSO != null && _debugSpherePSO != null)
        {
            // 1. Copy Args
            var copyMap = ctx.MapBuffer<CopyUniforms>(_copyUniformBuffer, MapType.Write, MapFlags.Discard);
            copyMap[0] = new CopyUniforms { SphereVertexCount = 1536 }; // 16x16x2x3 = 1536 for Lat-Long Sphere
            ctx.UnmapBuffer(_copyUniformBuffer, MapType.Write);

            _debugCopySRB.GetVariable(_context, _copyAsset, ShaderType.Compute, "IndirectArgs")
                ?.Set(_indirectArgsBuffer?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
             _debugCopySRB.GetVariable(_context, _copyAsset, ShaderType.Compute, "DebugArgs")
                ?.Set(_debugIndirectArgsBuffer?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            
            ctx.SetPipelineState(_debugCopyPSO);
            ctx.CommitShaderResources(_debugCopySRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

            // 2. Draw Spheres
            ctx.SetPipelineState(_debugSpherePSO);
            
            _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "DrawUniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "PageHeap")
                ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "PageTable")
                ?.Set(pageTable.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "RequestBuffer")
                ?.Set(_drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            
            ctx.CommitShaderResources(_debugSphereSRB, ResourceStateTransitionMode.Transition);

            DrawIndirectAttribs debugDrawAttrs = new DrawIndirectAttribs {
                Flags = DrawFlags.None,
                AttribsBuffer = _debugIndirectArgsBuffer,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Transition,
                DrawArgsOffset = 0,
                DrawCount = 1
            };
            ctx.DrawIndirect(debugDrawAttrs);
        }
    }
}
