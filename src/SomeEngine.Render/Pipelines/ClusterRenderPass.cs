using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShaderResourceVariableType = Diligent.ShaderResourceVariableType;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
struct CullingUniforms
{
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos;
    public float LodThreshold; // Moved here to match shader

    public float LodScale;
    public uint PageOffset; // Moved here to match shader
    public uint PageID;
    public uint BaseInstanceID;

    public int ForcedLODLevel;
    public uint InstanceCount;
    public uint DebugMode;
    public uint VisualiseBVH;
    public int DebugBVHDepth;
    public uint CurrentDepth;
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


public enum ClusterDebugMode : uint
{
    None = 0,
    ClusterID = 1,
    LODLevel = 2,
    TriangleCount = 3,
    Wireframe = 4,
    Overdraw = 5,
    BVH = 6,
    Occlusion = 7
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
    private IShaderResourceBinding? _drawWireframeSRB; // Need separate SRB if PSO is
                                                       // different
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

    // BVH Resources
    public bool UseBVH { get; set; } = true;
    private IBuffer? _queueA;
    private IBuffer? _queueB;
    private IBuffer? _argsA; // Dispatch Args + Count
    private IBuffer? _argsB;
    private IBuffer? _visibleClustersBuffer; // Replaces _drawCountBuffer in BVH mode
    private IBuffer? _candidateClustersBuffer;
    private IBuffer? _candidateArgsBuffer;
    private IBuffer? _candidateCountBuffer;
    private IBuffer? _readbackBuffer;

    private struct PendingReadback
    {
        public uint Offset;
        public Action<uint[]> Callback;
    }
    private readonly Queue<PendingReadback> _pendingReadbacks =
        new Queue<PendingReadback>();
    private uint _readbackOffset = 0;

    private IPipelineState? _bvhTraversePSO;
    private IPipelineState? _cullPSO;
    private IShaderResourceBinding? _cullSRB;
    private IPipelineState? _cullUpdateArgsPSO;
    private IShaderResourceBinding? _cullUpdateArgsSRB;
    private IShaderResourceBinding? _bvhTraverseSRB_A;    // Input A, Output B
    private IShaderResourceBinding? _bvhTraverseSRB_B;    // Input B, Output A
    private IShaderResourceBinding? _bvhTraverseSRB_Init; // Initial Dispatch
    
    // Debug BVH Resources
    private IBuffer? _bvhDebugBuffer; // StructuredBuffer<AABB>
    private IBuffer? _bvhDebugCountBuffer; // IndirectArgsBuffer
    private ShaderAsset? _bvhDebugAsset;
    private IPipelineState? _bvhDebugPSO;
    private IShaderResourceBinding? _bvhDebugSRB;

    private bool _initialized = false;
    
    public ClusterDebugMode DebugMode { get; set; } = ClusterDebugMode.None;
    public bool VisualiseBVH { get; set; } = false;
    public int DebugBVHDepth { get; set; } = -1;

    public bool DebugClusterID
    {
        get => DebugMode == ClusterDebugMode.ClusterID;
        set { if (value) DebugMode = ClusterDebugMode.ClusterID; else if (DebugMode == ClusterDebugMode.ClusterID) DebugMode = ClusterDebugMode.None; }
    }

    public bool DebugLOD
    {
        get => DebugMode == ClusterDebugMode.LODLevel;
        set { if (value) DebugMode = ClusterDebugMode.LODLevel; else if (DebugMode == ClusterDebugMode.LODLevel) DebugMode = ClusterDebugMode.None; }
    }
    
    public bool BypassCulling {
        get; set;
    } = false;                       // Default to false to enable LOD logic
    private uint _maxDraws = 100000; // Cap for now

    private Matrix4x4 _view =
        Matrix4x4.CreateLookAt(new Vector3(0, 0, -3), Vector3.Zero, Vector3.UnitY);
    private Matrix4x4 _proj = Matrix4x4.CreatePerspectiveFieldOfView(
        MathF.PI / 4.0f, 16.0f / 9.0f, 0.1f, 1000.0f
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
        _queueA?.Dispose();
        _queueB?.Dispose();
        _argsA?.Dispose();
        _argsB?.Dispose();
        _visibleClustersBuffer?.Dispose();
        _readbackBuffer?.Dispose();
        _bvhTraverseSRB_A?.Dispose();
        _bvhTraverseSRB_B?.Dispose();
        _bvhTraverseSRB_Init?.Dispose();
        _bvhTraversePSO?.Dispose();
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

        BufferDesc bvhDebugDesc = new BufferDesc {
            Name = "BVH Debug Buffer",
            Size = (ulong)(32 * _maxDraws), // AABB struct size 32
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 32
        };
        _bvhDebugBuffer = device.CreateBuffer(bvhDebugDesc);

        BufferDesc bvhDebugCountDesc = new BufferDesc {
            Name = "BVH Debug Count Buffer",
            Size = 256, // Indirect Args + Count
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw, 
        };
        _bvhDebugCountBuffer = device.CreateBuffer(bvhDebugCountDesc);

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
        string cullSlangPath = Path.Combine(
            AppContext.BaseDirectory, "assets", "Shaders", "cluster_cull.slang"
        );
        if (!File.Exists(cullSlangPath))
        {
            // Fallback: search up from
            // bin/Debug/net10.0/.../src/SomeEngine.Runtime/../../../../assets
            // BaseDirectory: .../SomeEngine.Runtime/bin/Debug/net10.0/
            // Go up 5 levels to root: ../../../../..
            cullSlangPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_cull.slang"
            ));
        }

        _cullAsset = SlangShaderImporter.Import(cullSlangPath);
        using var cs = _cullAsset.CreateShader(_context, "main");
        cppsCi.Cs = cs;

        // Resources
        cppsCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;

        // Use reflected variables instead of manual definition
        cppsCi.PSODesc.ResourceLayout.Variables = _cullAsset.GetResourceVariables(
            _context,
            (name, cat) => (name.Contains("Uniforms") || name == "Uniforms" ||
                            name == "Constants" || name == "g_Constants" ||
                            cat == ShaderResourceCategory.ConstantBuffer)
                               ? Diligent.ShaderResourceVariableType.Static
                               : null
        );

        _computePSO = device.CreateComputePipelineState(cppsCi);
        if (_computePSO != null)
        {
            // Bind Static Uniform by name using reflection extension
            _computePSO
                .GetStaticVariable(
                    _context, _cullAsset, ShaderType.Compute, "Uniforms"
                )
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
        string drawSlangPath = Path.Combine(
            AppContext.BaseDirectory, "assets", "Shaders", "cluster_draw.slang"
        );
        if (!File.Exists(drawSlangPath))
        {
            drawSlangPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../assets/Shaders/cluster_draw.slang"
            ));
        }

        _drawAsset = SlangShaderImporter.Import(drawSlangPath);
        using var vs = _drawAsset.CreateShader(_context, "VSMain");
        using var ps = _drawAsset.CreateShader(_context, "PSMain");

        psCi.Vs = vs;
        psCi.Ps = ps;

        psCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        psCi.PSODesc.ResourceLayout.Variables = _drawAsset.GetResourceVariables(
            _context,
            (name, cat) => (name.Contains("Uniforms") || name == "Uniforms" ||
                            name == "Constants" || name == "g_Constants" ||
                            cat == ShaderResourceCategory.ConstantBuffer)
                               ? Diligent.ShaderResourceVariableType.Static
                               : null
        );

        _drawPSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawPSO != null)
        {
            _drawPSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Vertex, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawPSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Pixel, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawSRB = _drawPSO.CreateShaderResourceBinding(true);
        }

        // 4. Draw Pipeline (Wireframe)
        psCi.PSODesc.Name = "Cluster Draw Wireframe PSO";
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode =
            CullMode.None; // Optional: show backfaces in wireframe

        _drawWireframePSO = device.CreateGraphicsPipelineState(psCi);
        if (_drawWireframePSO != null)
        {
            _drawWireframePSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Vertex, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawWireframePSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Pixel, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawWireframeSRB = _drawWireframePSO.CreateShaderResourceBinding(true);
        }

        // 5. Draw Pipeline (Overdraw)
        psCi.PSODesc.Name = "Cluster Draw Overdraw PSO";
        psCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
        psCi.GraphicsPipeline.RasterizerDesc.CullMode =
            CullMode.Back; // Still cull backfaces? Yes, usually.
        psCi.GraphicsPipeline.DepthStencilDesc.DepthEnable =
            false; // Disable depth test to see all layers
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
            _drawOverdrawPSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Vertex, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawOverdrawPSO
                .GetStaticVariable(
                    _context, _drawAsset, ShaderType.Pixel, "Uniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _drawOverdrawSRB = _drawOverdrawPSO.CreateShaderResourceBinding(true);
        }

        // 6. Debug Pipelines
        // Debug Indirect Args Buffer (Reuse argsDesc)
        var debugArgsDesc = argsDesc;
        debugArgsDesc.Name = "Debug Indirect Args Buffer";
        _debugIndirectArgsBuffer = device.CreateBuffer(debugArgsDesc);

        // Re-create creationAttrs for HLSL debug shaders
        using var debugShaderFactory =
            _context.Factory?.CreateDefaultShaderSourceStreamFactory(
                "assets/Shaders"
            );
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

        string copySlangPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../assets/Shaders/debug_args_copy.cs.hlsl"
        ));
        _copyAsset = SlangShaderImporter.Import(copySlangPath);
        using var copyCS = _copyAsset.CreateShader(_context, "main");
        dcCi.Cs = copyCS;

        dcCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        dcCi.PSODesc.ResourceLayout.Variables = _copyAsset.GetResourceVariables(
            _context,
            (name, cat) => (name.Contains("Uniforms") || name == "Uniforms" ||
                            name == "Constants" || name == "g_Constants" ||
                            cat == ShaderResourceCategory.ConstantBuffer)
                               ? Diligent.ShaderResourceVariableType.Static
                               : null
        );

        _debugCopyPSO = device.CreateComputePipelineState(dcCi);
        if (_debugCopyPSO != null)
        {
            _debugCopyPSO
                .GetStaticVariable(
                    _context, _copyAsset, ShaderType.Compute, "CopyUniforms"
                )
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
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend =
            BlendFactor.SrcAlpha;
        psCi.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend =
            BlendFactor.InvSrcAlpha;

        string debugSpherePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../assets/Shaders/debug_sphere.hlsl"
        ));
        _debugSphereAsset = SlangShaderImporter.Import(debugSpherePath);
        using var debugVS = _debugSphereAsset.CreateShader(_context, "VSMain");
        using var debugPS = _debugSphereAsset.CreateShader(_context, "PSMain");
        psCi.Vs = debugVS;
        psCi.Ps = debugPS;

        psCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        psCi.PSODesc.ResourceLayout.Variables =
            _debugSphereAsset.GetResourceVariables(
                _context,
                (name, cat) => (name.Contains("Uniforms") || name == "Uniforms" ||
                                name == "Constants" || name == "g_Constants" ||
                                cat == ShaderResourceCategory.ConstantBuffer)
                                   ? Diligent.ShaderResourceVariableType.Static
                                   : null
            );

        _debugSpherePSO = device.CreateGraphicsPipelineState(psCi);
        if (_debugSpherePSO != null)
        {
            _debugSpherePSO
                .GetStaticVariable(
                    _context, _debugSphereAsset, ShaderType.Vertex, "DrawUniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _debugSphereSRB = _debugSpherePSO.CreateShaderResourceBinding(true);
        }

        if (UseBVH)
        {
            InitBVH(device);
        }

        _initialized = true;
    }

    private IPipelineState? _bvhUpdateArgsPSO;
    private IShaderResourceBinding? _bvhUpdateArgsSRB_A;
    private IShaderResourceBinding? _bvhUpdateArgsSRB_B;

    public override void Execute(
        RenderContext context, RenderGraphContext graphContext
    )
    {
        if (!_initialized)
            Init();

        if (UseBVH && _bvhTraversePSO != null)
        {
            ExecuteBVH(context, graphContext);
            return;
        }

        if (_computeSRB == null || _drawSRB == null || _computePSO == null ||
            _drawPSO == null || _indirectArgsBuffer == null ||
            _drawCountBuffer == null || _cullingUniformBuffer == null ||
            _drawUniformBuffer == null || _drawWireframePSO == null ||
            _drawWireframeSRB == null || _drawOverdrawPSO == null ||
            _drawOverdrawSRB == null)
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
        _computeSRB
            .GetVariable(_context, _cullAsset, ShaderType.Compute, "PageBuffer")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        _computeSRB
            .GetVariable(
                _context, _cullAsset, ShaderType.Compute, "IndirectDrawArgs"
            )
            ?.Set(
                _indirectArgsBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        _computeSRB
            .GetVariable(_context, _cullAsset, ShaderType.Compute, "RequestBuffer")
            ?.Set(
                _drawCountBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        if (_transformSystem.GlobalTransformBuffer != null)
        {
            _computeSRB
                .GetVariable(
                    _context, _cullAsset, ShaderType.Compute, "InstanceData"
                )
                ?.Set(
                    _transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
        }

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
                    LodThreshold = _lodThreshold,
                    LodScale = _lodScale,
                    PageOffset = page.Offset,
                    PageID = page.PageID,
                    BaseInstanceID = 0,
                    ForcedLODLevel = _forcedLODLevel,
                    InstanceCount = (uint)_transformSystem.Count,
                    DebugMode = 0,
                };

                ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);

                // Dispatch
                uint groupSize = 64;
                // Check DispatchComputeAttribs syntax
                if (_transformSystem.Count > 0)
                {
                    ctx.DispatchCompute(new DispatchComputeAttribs {
                        ThreadGroupCountX =
                            (page.ClusterCount + groupSize - 1) / groupSize,
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
        ctx.TransitionResourceStates([new StateTransitionDesc {
            Resource = _indirectArgsBuffer,
            OldState = ResourceState.Unknown,
            NewState = ResourceState.IndirectArgument,
            Flags = StateTransitionFlags.UpdateState
        }]);
        // ctx.TransitionResourceStates(1, [new
        // StateTransitionDesc(_indirectArgsBuffer, ResourceState.Unknown,
        // ResourceState.IndirectArgument, StateTransitionFlags.UpdateState)]);

        // 2. Draw

        var drawUniformSpan = ctx.MapBuffer<DrawUniforms>(
            _drawUniformBuffer, MapType.Write, MapFlags.Discard
        );
        drawUniformSpan[0] = new DrawUniforms {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = _clusterManager.PageCount,
            DebugMode = DebugClusterID ? 1u : 0u,
            Pad = Vector2.Zero
        };
        ctx.UnmapBuffer(_drawUniformBuffer, MapType.Write);

        IPipelineState         ? currentDrawPSO;
        IShaderResourceBinding ? currentDrawSRB;

        if (OverdrawEnabled)
        {
            currentDrawPSO = _drawOverdrawPSO;
            currentDrawSRB = _drawOverdrawSRB;
            // Clear to black for additive blending to work correctly
            // Note: We cannot easily clear the render target here as it is bound by
            // the caller (RenderGraph). We assume the user toggling Overdraw
            // understands the output might need a dark background. Or we could
            // perform a clear if we had access to the RTV.
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
        currentDrawSRB
            .GetVariable(_context, _drawAsset, ShaderType.Vertex, "PageHeap")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        // Bind Instances
        if (_transformSystem != null &&
            _transformSystem.GlobalTransformBuffer != null)
        {
            currentDrawSRB
                .GetVariable(_context, _drawAsset, ShaderType.Vertex, "Instances")
                ?.Set(
                    _transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
        }

        // Bind RequestBuffer to VS
        currentDrawSRB
            .GetVariable(_context, _drawAsset, ShaderType.Vertex, "RequestBuffer")
            ?.Set(
                _drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        // Bind PageTable to VS
        currentDrawSRB
            .GetVariable(_context, _drawAsset, ShaderType.Vertex, "PageTable")
            ?.Set(
                pageTable.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        ctx.CommitShaderResources(
            currentDrawSRB, ResourceStateTransitionMode.Transition
        );

        // Execute Indirect (Single Draw)
        DrawIndirectAttribs drawAttrs = new DrawIndirectAttribs {
            Flags = DrawFlags.VerifyAll,
            AttribsBuffer = _indirectArgsBuffer,
            DrawArgsOffset = 0
        };
        ctx.DrawIndirect(drawAttrs);

        // 3. Debug Draw
        if (DebugSpheresEnabled && _debugCopySRB != null &&
            _debugSphereSRB != null && _debugCopyPSO != null &&
            _debugSpherePSO != null)
        {
            // 1. Copy Args
            var copyMap = ctx.MapBuffer<CopyUniforms>(
                _copyUniformBuffer, MapType.Write, MapFlags.Discard
            );
            copyMap[0] = new CopyUniforms {
                SphereVertexCount = 1536
            }; // 16x16x2x3 = 1536 for Lat-Long Sphere
            ctx.UnmapBuffer(_copyUniformBuffer, MapType.Write);

            _debugCopySRB
                .GetVariable(
                    _context, _copyAsset, ShaderType.Compute, "IndirectArgs"
                )
                ?.Set(
                    _indirectArgsBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _debugCopySRB
                .GetVariable(_context, _copyAsset, ShaderType.Compute, "DebugArgs")
                ?.Set(
                    _debugIndirectArgsBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );

            ctx.SetPipelineState(_debugCopyPSO);
            ctx.CommitShaderResources(
                _debugCopySRB, ResourceStateTransitionMode.Transition
            );
            ctx.DispatchCompute(new DispatchComputeAttribs {
                ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1
            });

            // 2. Draw Spheres
            ctx.SetPipelineState(_debugSpherePSO);

            _debugSphereSRB
                .GetVariable(
                    _context, _debugSphereAsset, ShaderType.Vertex, "DrawUniforms"
                )
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _debugSphereSRB
                .GetVariable(
                    _context, _debugSphereAsset, ShaderType.Vertex, "PageHeap"
                )
                ?.Set(
                    pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _debugSphereSRB
                .GetVariable(
                    _context, _debugSphereAsset, ShaderType.Vertex, "PageTable"
                )
                ?.Set(
                    pageTable.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _debugSphereSRB
                .GetVariable(
                    _context, _debugSphereAsset, ShaderType.Vertex, "RequestBuffer"
                )
                ?.Set(
                    _drawCountBuffer.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );

            ctx.CommitShaderResources(
                _debugSphereSRB, ResourceStateTransitionMode.Transition
            );

            DrawIndirectAttribs debugDrawAttrs = new DrawIndirectAttribs {
                Flags = DrawFlags.None,
                AttribsBuffer = _debugIndirectArgsBuffer,
                AttribsBufferStateTransitionMode =
                    ResourceStateTransitionMode.Transition,
                DrawArgsOffset = 0,
                DrawCount = 1
            };
            ctx.DrawIndirect(debugDrawAttrs);
        }
    }

    private void InitBVH(IRenderDevice device)
    {
        // 1. Queues
        BufferDesc qDesc = new BufferDesc {
            Name = "BVH Queue",
            Size = 256 * 1024 * 4, // 256k items * 4 bytes (1MB)
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            Mode = BufferMode.Structured,
            ElementByteStride = 4
        };
        _queueA = device.CreateBuffer(qDesc);
        _queueB = device.CreateBuffer(qDesc);

        // 2. Indirect Args
        BufferDesc argsDesc = new BufferDesc {
            Name = "BVH Indirect Args",
            Size = 16,
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs |
                        BindFlags.ShaderResource,
            Mode = BufferMode.Raw, // ByteAddressBuffer
            ElementByteStride = 4
        };
        _argsA = device.CreateBuffer(argsDesc);
        _argsB = device.CreateBuffer(argsDesc);

        // 3. Visible Clusters Buffer
        BufferDesc reqDesc = new BufferDesc {
            Name = "Visible Clusters Buffer",
            Size = (ulong)(_maxDraws * 16), // 4 uints * maxDraws
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16
        };
        _visibleClustersBuffer = device.CreateBuffer(reqDesc);

        // 3.5 Candidate Clusters Buffer
        BufferDesc candidateDesc = new BufferDesc {
            Name = "Candidate Clusters Buffer",
            Size = (ulong)(_maxDraws * 8), // 2 uints * maxDraws (PageID, ClusterID)
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 8
        };
        _candidateClustersBuffer = device.CreateBuffer(candidateDesc);

        // 3.6 Candidate Args
        BufferDesc candArgsDesc = new BufferDesc {
            Name = "Candidate Args",
            Size = 16,
            Usage = Usage.Default,
            BindFlags =
                BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs |
                BindFlags
                    .ShaderResource, // IndirectDrawArgs allows dispatch indirect
            Mode = BufferMode.Raw,   // ByteAddressBuffer
            ElementByteStride = 4
        };
        _candidateArgsBuffer = device.CreateBuffer(candArgsDesc);

        // 3.7 Candidate Count
        BufferDesc countDesc = new BufferDesc {
            Name = "Candidate Count",
            Size = 4,
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4
        };
        _candidateCountBuffer = device.CreateBuffer(countDesc);

        _readbackBuffer?.Dispose();
        _readbackBuffer = device.CreateBuffer(new BufferDesc {
            Name = "BVH Debug Readback",
            Size = 4096,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        // 4. PSO
        ComputePipelineStateCreateInfo psoCi = new ComputePipelineStateCreateInfo();
        psoCi.PSODesc.Name = "BVH Traverse PSO";
        psoCi.PSODesc.PipelineType = PipelineType.Compute;

        string shaderPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../assets/Shaders/cluster_bvh_traverse.slang"
        ));
        var shaderAsset = SlangShaderImporter.Import(shaderPath);
        using var cs = shaderAsset.CreateShader(_context, "main");
        psoCi.Cs = cs;

        psoCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        psoCi.PSODesc.ResourceLayout.Variables = shaderAsset.GetResourceVariables(_context, (name, cat) => 
        {
            if (name.Contains("Uniforms") || 
                name == "Uniforms" ||
                name == "Constants" ||
                name == "g_Constants" ||
                cat == ShaderResourceCategory.ConstantBuffer ||
                name == "GlobalBVH" || 
                name == "PageTable" || 
                name == "PageHeap" || 
                name == "CandidateClusters" || 
                name == "CandidateCount" ||
                name == "DebugAABBs" ||
                name == "DebugAABBCount")
                return Diligent.ShaderResourceVariableType.Static;
            return null;
        });

        _bvhTraversePSO = device.CreateComputePipelineState(psoCi);

        if (_bvhTraversePSO != null)
        {
            // Static Bindings
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "GlobalBVH"
                )
                ?.Set(
                    _clusterManager.GlobalBVHBuffer?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "PageTable"
                )
                ?.Set(
                    _clusterManager.PageTableBuffer?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "PageHeap"
                )
                ?.Set(
                    _clusterManager.PageHeap?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "Uniforms"
                )
                ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "CandidateClusters"
                )
                ?.Set(
                    _candidateClustersBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "CandidateCount"
                )
                ?.Set(
                    _candidateCountBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "DebugAABBs"
                )
                ?.Set(
                    _bvhDebugBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _bvhTraversePSO
                .GetStaticVariable(
                    _context, shaderAsset, ShaderType.Compute, "DebugAABBCount"
                )
                ?.Set(
                    _bvhDebugCountBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );

            // Create SRBs
            _bvhTraverseSRB_A = _bvhTraversePSO.CreateShaderResourceBinding(true);
            _bvhTraverseSRB_A
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "Queue_Current"
                )
                ?.Set(
                    _queueA.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_A
                .GetVariable(_context, shaderAsset, ShaderType.Compute, "Queue_Next")
                ?.Set(
                    _queueB.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_A
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "NextDispatchArgs"
                )
                ?.Set(
                    _argsB.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_A
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "CurrentDispatchArgs"
                )
                ?.Set(
                    _argsA.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_A
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "CurrentDispatchArgs"
                )
                ?.Set(
                    _argsA.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_B = _bvhTraversePSO.CreateShaderResourceBinding(true);
            _bvhTraverseSRB_B
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "Queue_Current"
                )
                ?.Set(
                    _queueB.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_B
                .GetVariable(_context, shaderAsset, ShaderType.Compute, "Queue_Next")
                ?.Set(
                    _queueA.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_B
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "NextDispatchArgs"
                )
                ?.Set(
                    _argsA.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _bvhTraverseSRB_B
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "CurrentDispatchArgs"
                )
                ?.Set(
                    _argsB.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
        }

        // 5. Update Args PSO
        ComputePipelineStateCreateInfo upPsoCi =
            new ComputePipelineStateCreateInfo();
        upPsoCi.PSODesc.Name = "BVH Update Args PSO";
        upPsoCi.PSODesc.PipelineType = PipelineType.Compute;
        using var upCs = shaderAsset.CreateShader(_context, "UpdateArgs");
        upPsoCi.Cs = upCs;
        upPsoCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        upPsoCi.PSODesc.ResourceLayout.Variables = shaderAsset.GetResourceVariables(
            _context,
            (name, cat) => (name.Contains("Uniforms") || name == "Uniforms" ||
                            name == "Constants" || name == "g_Constants" ||
                            cat == ShaderResourceCategory.ConstantBuffer)
                               ? Diligent.ShaderResourceVariableType.Static
                               : null
        );

        _bvhUpdateArgsPSO = device.CreateComputePipelineState(upPsoCi);

        if (_bvhUpdateArgsPSO != null)
        {
            _bvhUpdateArgsSRB_A =
                _bvhUpdateArgsPSO.CreateShaderResourceBinding(true);
            _bvhUpdateArgsSRB_A
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "NextDispatchArgs"
                )
                .Set(
                    _argsA.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );

            _bvhUpdateArgsSRB_B =
                _bvhUpdateArgsPSO.CreateShaderResourceBinding(true);
            _bvhUpdateArgsSRB_B
                .GetVariable(
                    _context, shaderAsset, ShaderType.Compute, "NextDispatchArgs"
                )
                .Set(
                    _argsB.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
        }

        // 6. Cull PSO
        ComputePipelineStateCreateInfo cullPsoCi =
            new ComputePipelineStateCreateInfo();
        cullPsoCi.PSODesc.Name = "Cluster Cull PSO";
        cullPsoCi.PSODesc.PipelineType = PipelineType.Compute;

        string cullShaderPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../assets/Shaders/cluster_cull.slang"
        ));
        var cullShaderAsset = SlangShaderImporter.Import(cullShaderPath);
        using var cullCs = cullShaderAsset.CreateShader(_context, "main");
        cullPsoCi.Cs = cullCs;

        cullPsoCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        cullPsoCi.PSODesc.ResourceLayout.Variables = cullShaderAsset.GetResourceVariables(_context, (name, cat) => 
        {
            if (name.Contains("Uniforms") || 
                name == "Uniforms" ||
                name == "PageTable" || 
                name == "PageHeap" || 
                name == "CandidateClusters" || 
                name == "CandidateArgs" || 
                name == "CandidateCount" ||
                name == "DrawArgs" ||
                name == "VisibleClusters")
                return Diligent.ShaderResourceVariableType.Static;
            return null;
        });

        _cullPSO = device.CreateComputePipelineState(cullPsoCi);
        if (_cullPSO != null)
        {
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "PageTable"
                )
                ?.Set(
                    _clusterManager.PageTableBuffer?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "PageHeap"
                )
                ?.Set(
                    _clusterManager.PageHeap?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "Uniforms"
                )
                ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);

            _cullPSO
                .GetStaticVariable(
                    _context,
                    cullShaderAsset,
                    ShaderType.Compute,
                    "CandidateClusters"
                )
                ?.Set(
                    _candidateClustersBuffer?.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "CandidateCount"
                )
                ?.Set(
                    _candidateCountBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "DrawArgs"
                )
                ?.Set(
                    _indirectArgsBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _cullPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "VisibleClusters"
                )
                ?.Set(
                    _visibleClustersBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );

            _cullSRB = _cullPSO.CreateShaderResourceBinding(true);
        }

        // 7. Update Cull Args PSO
        ComputePipelineStateCreateInfo upCullPsoCi =
            new ComputePipelineStateCreateInfo();
        upCullPsoCi.PSODesc.Name = "Cull Update Args PSO";
        upCullPsoCi.PSODesc.PipelineType = PipelineType.Compute;
        using var upCullCs =
            cullShaderAsset.CreateShader(_context, "UpdateIndirectArgs");
        upCullPsoCi.Cs = upCullCs;
        upCullPsoCi.PSODesc.ResourceLayout.DefaultVariableType =
            Diligent.ShaderResourceVariableType.Mutable;
        upCullPsoCi.PSODesc.ResourceLayout.Variables =
            cullShaderAsset.GetResourceVariables(
                _context,
                (name, cat) => (name == "CandidateArgs" || name == "CandidateCount")
                                   ? Diligent.ShaderResourceVariableType.Static
                                   : null
            );

        _cullUpdateArgsPSO = device.CreateComputePipelineState(upCullPsoCi);
        if (_cullUpdateArgsPSO != null)
        {
            _cullUpdateArgsPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "CandidateCount"
                )
                ?.Set(
                    _candidateCountBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );
            _cullUpdateArgsPSO
                .GetStaticVariable(
                    _context, cullShaderAsset, ShaderType.Compute, "CandidateArgs"
                )
                ?.Set(
                    _candidateArgsBuffer?.GetDefaultView(
                        BufferViewType.UnorderedAccess
                    ),
                    SetShaderResourceFlags.None
                );

            _cullUpdateArgsSRB =
                _cullUpdateArgsPSO.CreateShaderResourceBinding(true);
        }

        // 8. BVH Debug PSO
        GraphicsPipelineStateCreateInfo debugPsoCi = new GraphicsPipelineStateCreateInfo();
        debugPsoCi.PSODesc.Name = "BVH Debug AABB PSO";
        debugPsoCi.PSODesc.PipelineType = PipelineType.Graphics;
        debugPsoCi.GraphicsPipeline.NumRenderTargets = 1;
        debugPsoCi.GraphicsPipeline.RTVFormats = new[] { TextureFormat.RGBA8_UNorm };
        debugPsoCi.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;
        debugPsoCi.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.LineList; // AABB is drawn as lines? Or custom VS?
        // debug_aabb.slang: VS emits vertices based on BoxIndices (lines). 
        // 24 indices -> 12 lines. PrimitiveTopology.LineList is correct.
        // Wait, BoxIndices has 24 entries. 24 vertices.
        // If it's LineList, it expects pairs of vertices.
        // Let's check BoxIndices in debug_aabb.slang:
        // 0,1, 1,2, 2,3, 3,0 (Bottom square lines) -> 8 indices -> 4 lines
        // 4,5, 5,6, 6,7, 7,4 (Top square lines) -> 8 indices -> 4 lines
        // 0,4, 1,5, 2,6, 3,7 (Vertical lines) -> 8 indices -> 4 lines
        // Total 24 indices -> 12 lines. Correct.
        
        debugPsoCi.GraphicsPipeline.InputLayout.LayoutElements = new LayoutElement[0];
        debugPsoCi.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        debugPsoCi.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid; // Lines are solid
        debugPsoCi.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        debugPsoCi.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;

        debugPsoCi.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        debugPsoCi.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.SrcAlpha;
        debugPsoCi.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.InvSrcAlpha;

        string debugAABBPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../assets/Shaders/debug_aabb.slang"
        ));
        _bvhDebugAsset = SlangShaderImporter.Import(debugAABBPath);
        using var debugVs = _bvhDebugAsset.CreateShader(_context, "VSMain");
        using var debugPs = _bvhDebugAsset.CreateShader(_context, "PSMain");
        debugPsoCi.Vs = debugVs;
        debugPsoCi.Ps = debugPs;

        debugPsoCi.PSODesc.ResourceLayout.DefaultVariableType = Diligent.ShaderResourceVariableType.Mutable;
        debugPsoCi.PSODesc.ResourceLayout.Variables = _bvhDebugAsset.GetResourceVariables(_context, (name, cat) => 
        {
            if (name.Contains("Uniforms") || name == "Uniforms" || name == "DebugAABBs")
                return Diligent.ShaderResourceVariableType.Static;
            return null;
        });

        _bvhDebugPSO = device.CreateGraphicsPipelineState(debugPsoCi);
        if (_bvhDebugPSO != null)
        {
             _bvhDebugPSO.GetStaticVariable(_context, _bvhDebugAsset, ShaderType.Vertex, "Uniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
             _bvhDebugPSO.GetStaticVariable(_context, _bvhDebugAsset, ShaderType.Vertex, "DebugAABBs")
                ?.Set(_bvhDebugBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
             
             _bvhDebugSRB = _bvhDebugPSO.CreateShaderResourceBinding(true);
        }
    }

    private void ProcessReadbacks(IDeviceContext ctx)
    {
        if (_readbackBuffer == null || _pendingReadbacks.Count == 0)
            return;

        var map =
            ctx.MapBuffer<uint>(_readbackBuffer, MapType.Read, MapFlags.DoNotWait);
        // Note: MapBuffer returns Span<T>, so we check if it is empty to simulate
        // null check. Or if the binding returns null for failure. Based on
        // Diligent.SharpGen, MapBuffer returns Span. If failed (DoNotWait), it might
        // return empty span or throw. Assuming DoNotWait failure results in a
        // specific condition. However, standard Pattern is try-catch or check
        // length. If the span is empty, we assume resource is busy.
        if (map.Length == 0)
            return;

        try
        {
            while (_pendingReadbacks.Count > 0)
            {
                var req = _pendingReadbacks.Peek();
                // Since we successfully mapped the buffer, we assume ALL pending
                // operations (FIFO) are done. This is a simplification. If we use a
                // ring buffer, we need to be careful. But with a simple linear
                // allocator that resets on empty, if we can map, we can read.

                int idx = (int)(req.Offset / 4);
                uint[] data = new uint[4];
                // Check bounds
                if (idx + 3 < map.Length)
                {
                    data[0] = map[idx];
                    data[1] = map[idx + 1];
                    data[2] = map[idx + 2];
                    data[3] = map[idx + 3];
                    req.Callback(data);
                }
                _pendingReadbacks.Dequeue();
            }
        }
        finally
        {
            ctx.UnmapBuffer(_readbackBuffer, MapType.Read);
        }

        // Reset if empty
        if (_pendingReadbacks.Count == 0)
            _readbackOffset = 0;
    }

    private void EnqueueReadback(
        IDeviceContext ctx, IBuffer? src, uint srcOffset, Action<uint[]> callback
    )
    {
        if (_readbackBuffer == null || src == null)
            return;

        uint size = 16;
        if (_readbackOffset + size > _readbackBuffer.GetDesc().Size)
        {
            return;
        }

        ctx.CopyBuffer(
            src,
            srcOffset,
            ResourceStateTransitionMode.Transition,
            _readbackBuffer,
            _readbackOffset,
            size,
            ResourceStateTransitionMode.Transition
        );

        _pendingReadbacks.Enqueue(
            new PendingReadback { Offset = _readbackOffset, Callback = callback }
        );

        _readbackOffset += size;
        if (_readbackOffset % 16 != 0)
            _readbackOffset += (16 - (_readbackOffset % 16));
    }

    private void ExecuteBVH(RenderContext context, RenderGraphContext graphContext)
    {
        var ctx = context.ImmediateContext ??
                  throw new InvalidOperationException("ImmediateContext is null");

        ProcessReadbacks(ctx);

        // 0. Update Uniforms
        var viewProj = _view * _proj;
        var viewProjT = Matrix4x4.Transpose(viewProj);
        var viewT = Matrix4x4.Transpose(_view);

        // Update Draw Uniforms at the start to ensure backing memory is allocated
        // for the frame
        {
            var drawUniformSpan = ctx.MapBuffer<DrawUniforms>(
                _drawUniformBuffer, MapType.Write, MapFlags.Discard
            );
            uint drawDebugMode = 0;
            if (DebugClusterID)
                drawDebugMode = 1;
            else if (DebugLOD)
                drawDebugMode = 2;

            drawUniformSpan[0] = new DrawUniforms {
                ViewProj = viewProjT,
                View = viewT,
                PageTableSize = _clusterManager.PageCount,
                DebugMode = drawDebugMode,
                Pad = Vector2.Zero
            };
            ctx.UnmapBuffer(_drawUniformBuffer, MapType.Write);
        }

        // 1. Setup
        Span<uint> resetDrawArgs = [372, 0, 0, 0];
        ctx.UpdateBuffer(
            _indirectArgsBuffer,
            0,
            resetDrawArgs,
            ResourceStateTransitionMode.Transition
        );

        // Reset Candidate Args and Count
        Span<uint> resetCandArgs = [1, 1, 1, 0];
        ctx.UpdateBuffer(
            _candidateArgsBuffer,
            0,
            resetCandArgs,
            ResourceStateTransitionMode.Transition
        );
        Span<uint> resetCandCount = [0];
        ctx.UpdateBuffer(
            _candidateCountBuffer,
            0,
            resetCandCount,
            ResourceStateTransitionMode.Transition
        );

        // Reset Debug Args
        Span<uint> resetDebugArgs = [24, 0, 0, 0]; // 24 vertices per box, 0 instances
        if (_bvhDebugCountBuffer != null)
        {
            ctx.UpdateBuffer(
                _bvhDebugCountBuffer,
                0,
                resetDebugArgs,
                ResourceStateTransitionMode.Transition
            );
        }

        ctx.TransitionResourceStates([
            new StateTransitionDesc {
                Resource = _candidateArgsBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _candidateCountBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _candidateClustersBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _bvhDebugCountBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _bvhDebugBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            }
        ]);
        // DrawArgs will be updated via UAV in Traverse pass
        // ctx.TransitionResourceStates(new[] { new StateTransitionDesc { Resource =
        // _indirectArgsBuffer, OldState = ResourceState.Unknown, NewState =
        // ResourceState.UnorderedAccess, Flags = StateTransitionFlags.UpdateState }
        // });

        // Reset Initial Args (ArgsA)
        Span<uint> resetArgsA = [1, 1, 1, 1]; // 1 Group, 1 Item
        ctx.UpdateBuffer(
            _argsA, 0, resetArgsA, ResourceStateTransitionMode.Transition
        );

        // Upload Roots to QueueA
        var roots = new List<uint>();
        foreach (var root in _clusterManager.MeshBVHRoots.Values)
            roots.Add(root);

        if (roots.Count == 0)
        {
            Console.WriteLine("[BVH] No roots found in ClusterManager!");
            return;
        }

        uint rootIndex = roots[0];
        ctx.UpdateBuffer(
            _queueA, 0, [rootIndex], ResourceStateTransitionMode.Transition
        );

        // Reset ArgsB
        Span<uint> resetArgsB = [0, 1, 1, 0];
        ctx.UpdateBuffer(
            _argsB, 0, resetArgsB, ResourceStateTransitionMode.Transition
        );

        // Culling Uniforms
        {
            var uniforms = new CullingUniforms {
                ViewProj = viewProjT,
                CameraPos = _cameraPos,
                LodThreshold = _lodThreshold,
                LodScale = _lodScale,
                PageOffset = 0,
                PageID = 0,
                BaseInstanceID = 0,
                ForcedLODLevel = _forcedLODLevel,
                InstanceCount = 0,
                DebugMode = BypassCulling ? 1u : 0u,
                VisualiseBVH = VisualiseBVH ? 1u : 0u,
                DebugBVHDepth = DebugBVHDepth,
                CurrentDepth = 0
            };
            var mapped = ctx.MapBuffer<CullingUniforms>(
                _cullingUniformBuffer, MapType.Write, MapFlags.Discard
            );
            mapped[0] = uniforms;
            ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);
        }

        // 2. Loop
        var currentArgs = _argsA;
        var nextArgs = _argsB;
        var currentSRB = _bvhTraverseSRB_A;
        var nextUpdateSRB = _bvhUpdateArgsSRB_B; // Updates B

        // Initial transition for ArgsA (IndirectArgument) and ArgsB
        // (UnorderedAccess)

        int maxDepth = 32;

        for (int i = 0; i < maxDepth; ++i)
        {
            // Update CurrentDepth in Uniforms
            {
                var uniforms = new CullingUniforms {
                    ViewProj = viewProjT,
                    CameraPos = _cameraPos,
                    LodThreshold = _lodThreshold,
                    LodScale = _lodScale,
                    PageOffset = 0,
                    PageID = 0,
                    BaseInstanceID = 0,
                    ForcedLODLevel = _forcedLODLevel,
                    InstanceCount = 0,
                    DebugMode = BypassCulling ? 1u : 0u,
                    VisualiseBVH = VisualiseBVH ? 1u : 0u,
                    DebugBVHDepth = DebugBVHDepth,
                    CurrentDepth = (uint)i
                };
                var mapped = ctx.MapBuffer<CullingUniforms>(_cullingUniformBuffer, MapType.Write, MapFlags.Discard);
                mapped[0] = uniforms;
                ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);
            }

            // Traverse
            ctx.SetPipelineState(_bvhTraversePSO);
            ctx.CommitShaderResources(
                currentSRB, ResourceStateTransitionMode.Transition
            );
            ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs {
                AttribsBuffer = currentArgs,
                AttribsBufferStateTransitionMode =
                    ResourceStateTransitionMode.Transition
            });

            // Update Args for Next Pass
            ctx.SetPipelineState(_bvhUpdateArgsPSO);
            ctx.CommitShaderResources(
                nextUpdateSRB, ResourceStateTransitionMode.Transition
            );
            ctx.DispatchCompute(new DispatchComputeAttribs {
                ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1
            });

            // nextArgs is now ready to be used as currentArgs in next pass

            // Swap
            var tempArgs = currentArgs;
            currentArgs = nextArgs;
            nextArgs = tempArgs;

            var tempSRB = currentSRB;
            currentSRB = (currentSRB == _bvhTraverseSRB_A) ? _bvhTraverseSRB_B
                                                           : _bvhTraverseSRB_A;

            nextUpdateSRB = (nextUpdateSRB == _bvhUpdateArgsSRB_B)
                                ? _bvhUpdateArgsSRB_A
                                : _bvhUpdateArgsSRB_B;

            // DEBUG: Readback counts
            if (i < 5)
            {
                int level = i;
                EnqueueReadback(
                    ctx,
                    currentArgs,
                    0,
                    (data) => Console.WriteLine(
                        $"[BVH Level {level}] Dispatch: {data[0]} groups, {data[3]} items"
                    )
                );
            }

            // Clear the NEW nextArgs (which was old currentArgs in IndirectArgument
            // state)
            ctx.UpdateBuffer(
                nextArgs, 0, resetArgsB, ResourceStateTransitionMode.Transition
            );
            ctx.TransitionResourceStates(new[] { new StateTransitionDesc {
                Resource = nextArgs,
                OldState = ResourceState.Unknown,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState
            } });
        }

        // 2.5 Culling Pass
        ctx.SetPipelineState(_cullUpdateArgsPSO);
        ctx.CommitShaderResources(
            _cullUpdateArgsSRB, ResourceStateTransitionMode.Transition
        );
        ctx.DispatchCompute(new DispatchComputeAttribs {
            ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1
        });

        ctx.TransitionResourceStates(new[] {
            new StateTransitionDesc {
                Resource = _candidateArgsBuffer,
                OldState = ResourceState.UnorderedAccess,
                NewState = ResourceState.IndirectArgument,
                Flags = StateTransitionFlags.UpdateState
            },
            new StateTransitionDesc {
                Resource = _candidateClustersBuffer,
                OldState = ResourceState.UnorderedAccess,
                NewState = ResourceState.ShaderResource,
                Flags = StateTransitionFlags.UpdateState
            }
        });

        ctx.SetPipelineState(_cullPSO);
        ctx.CommitShaderResources(_cullSRB, ResourceStateTransitionMode.Transition);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs {
            AttribsBuffer = _candidateArgsBuffer,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None
        });

        // 3. Draw
        // DEBUG: Readback visible count
        EnqueueReadback(
            ctx,
            _indirectArgsBuffer,
            0,
            (data) => Console.WriteLine(
                $"[BVH Final] Visible Clusters: {data[1]} (InstanceCount in DrawArgs)"
            )
        );

        // Transition DrawArgs back to IndirectArgument for DrawIndirect

        IPipelineState? currentDrawPSO = _drawPSO;
        IShaderResourceBinding? currentDrawSRB = _drawSRB;

        if (OverdrawEnabled)
        {
            currentDrawPSO = _drawOverdrawPSO;
            currentDrawSRB = _drawOverdrawSRB;
        }
        else if (WireframeEnabled)
        {
            currentDrawPSO = _drawWireframePSO;
            currentDrawSRB = _drawWireframeSRB;
        }

        if (currentDrawSRB != null)
        {
            currentDrawSRB
                .GetVariable(context, _drawAsset, ShaderType.Vertex, "RequestBuffer")
                .Set(
                    _visibleClustersBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            if (_transformSystem.GlobalTransformBuffer != null)
                currentDrawSRB
                    .GetVariable(context, _drawAsset, ShaderType.Vertex, "Instances")
                    .Set(
                        _transformSystem.GlobalTransformBuffer.GetDefaultView(
                            BufferViewType.ShaderResource
                        ),
                        SetShaderResourceFlags.None
                    );

            currentDrawSRB.GetVariable(context, _drawAsset, ShaderType.Vertex, "PageHeap")
                .Set(
                    _clusterManager.PageHeap.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
            currentDrawSRB.GetVariable(context, _drawAsset, ShaderType.Vertex, "PageTable")
                .Set(
                    _clusterManager.PageTableBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
        }

        if (currentDrawPSO != null)
        {
            ctx.SetPipelineState(currentDrawPSO);
            ctx.CommitShaderResources(currentDrawSRB, ResourceStateTransitionMode.Transition);

            ctx.DrawIndirect(new DrawIndirectAttribs {
                AttribsBuffer = _indirectArgsBuffer,
                DrawArgsOffset = 0,
                Flags = DrawFlags.VerifyAll,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Transition
            });
        }

        if (VisualiseBVH && _bvhDebugPSO != null && _bvhDebugSRB != null)
        {
             ctx.TransitionResourceStates(new[] {
                 new StateTransitionDesc { Resource = _bvhDebugCountBuffer, OldState = ResourceState.UnorderedAccess, NewState = ResourceState.IndirectArgument, Flags = StateTransitionFlags.UpdateState },
                 new StateTransitionDesc { Resource = _bvhDebugBuffer, OldState = ResourceState.UnorderedAccess, NewState = ResourceState.ShaderResource, Flags = StateTransitionFlags.UpdateState }
             });

             ctx.SetPipelineState(_bvhDebugPSO);
             ctx.CommitShaderResources(_bvhDebugSRB, ResourceStateTransitionMode.Transition);
             
             ctx.DrawIndirect(new DrawIndirectAttribs {
                 AttribsBuffer = _bvhDebugCountBuffer,
                 DrawArgsOffset = 0,
                 Flags = DrawFlags.VerifyAll, 
                 AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None // Already transitioned
             });
        }

        if (DebugSpheresEnabled && _debugCopySRB != null && _debugSphereSRB != null && _debugCopyPSO != null && _debugSpherePSO != null)
        {
             // Update RequestBuffer binding for BVH
             _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "RequestBuffer")
                 ?.Set(_visibleClustersBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
             
             // Update PageHeap and PageTable binding for BVH
             _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "PageHeap")
                 ?.Set(_clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
             _debugSphereSRB.GetVariable(_context, _debugSphereAsset, ShaderType.Vertex, "PageTable")
                 ?.Set(_clusterManager.PageTableBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

             // Transition IndirectArgs to UAV for Copy
             ctx.TransitionResourceStates(new[] {
                 new StateTransitionDesc { Resource = _indirectArgsBuffer, OldState = ResourceState.Unknown, NewState = ResourceState.UnorderedAccess, Flags = StateTransitionFlags.UpdateState }
             });

             // Copy Args
             var copyMap = ctx.MapBuffer<CopyUniforms>(_copyUniformBuffer, MapType.Write, MapFlags.Discard);
             copyMap[0] = new CopyUniforms { SphereVertexCount = 1536 };
             ctx.UnmapBuffer(_copyUniformBuffer, MapType.Write);

             _debugCopySRB.GetVariable(_context, _copyAsset, ShaderType.Compute, "IndirectArgs")
                 ?.Set(_indirectArgsBuffer?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
             _debugCopySRB.GetVariable(_context, _copyAsset, ShaderType.Compute, "DebugArgs")
                 ?.Set(_debugIndirectArgsBuffer?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

             ctx.SetPipelineState(_debugCopyPSO);
             ctx.CommitShaderResources(_debugCopySRB, ResourceStateTransitionMode.Transition);
             ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

             // Draw Spheres
             ctx.SetPipelineState(_debugSpherePSO);
             ctx.CommitShaderResources(_debugSphereSRB, ResourceStateTransitionMode.Transition);
             ctx.DrawIndirect(new DrawIndirectAttribs {
                 Flags = DrawFlags.None,
                 AttribsBuffer = _debugIndirectArgsBuffer,
                 AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Transition,
                 DrawArgsOffset = 0,
                 DrawCount = 1
             });
        }
    }
}
