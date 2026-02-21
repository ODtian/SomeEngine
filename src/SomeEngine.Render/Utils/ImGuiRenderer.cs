using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using ImGuiNET;
using SomeEngine.Render.RHI;
using System.Runtime.CompilerServices;
using SomeEngine.Assets.Importers;
using System.IO;

namespace SomeEngine.Render.Utils;

public unsafe class ImGuiRenderer : IDisposable
{
    private readonly RenderContext _context;
    private IBuffer? _vertexBuffer;
    private IBuffer? _indexBuffer;
    private IBuffer? _uniformBuffer;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private ITexture? _fontTexture;
    private ITextureView? _fontTextureView;
    private ISampler? _sampler;

    private int _vertexBufferSize = 10000;
    private int _indexBufferSize = 10000;

    public ImGuiRenderer(RenderContext context)
    {
        _context = context;
        Init();
    }

    private void Init()
    {
        var device = _context.Device;
        if (device == null)
            return;

        // Initialize ImGui
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        // Create Font Texture
        byte *pixels;
        int width, height, bytesPerPixel;
        io.Fonts.GetTexDataAsRGBA32(
            out pixels, out width, out height, out bytesPerPixel
        );

        TextureDesc texDesc = new TextureDesc {
            Name = "ImGui Font Texture",
            Type = ResourceDimension.Tex2d,
            Width = (uint)width,
            Height = (uint)height,
            Format = TextureFormat.RGBA8_UNorm,
            Usage = Usage.Immutable,
            BindFlags = BindFlags.ShaderResource
        };

        TextureData data = new TextureData {
            SubResources = [new TextureSubResData {
                Data = (IntPtr)pixels, Stride = (ulong)(width * bytesPerPixel)
            }]
        };

        _fontTexture = device.CreateTexture(texDesc, data);
        TextureViewDesc viewDesc = new TextureViewDesc {
            ViewType = TextureViewType.ShaderResource,
            Name = "ImGui Font Texture SRV",
            Format = texDesc.Format
        };
        _fontTextureView = _fontTexture.CreateView(viewDesc);

        SamplerDesc samDesc = new SamplerDesc {
            MinFilter = FilterType.Linear,
            MagFilter = FilterType.Linear,
            MipFilter = FilterType.Linear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap
        };
        _sampler = device.CreateSampler(samDesc);

        io.Fonts.SetTexID((IntPtr)1);

        // Create Uniform Buffer
        BufferDesc ubDesc = new BufferDesc {
            Name = "ImGui Uniform Buffer",
            Size = (ulong)Marshal.SizeOf<Matrix4x4>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _uniformBuffer = device.CreateBuffer(ubDesc);

        CreatePSO();
    }

    private void CreatePSO()
    {
        var device = _context.Device;
        if (device == null)
            return;

        string shaderPath = Path.Combine(
            AppContext.BaseDirectory, "../../../../../../assets/Shaders/imgui.hlsl"
        );

        // Use standard Diligent HLSL shader creation
        var shaderCI = new ShaderCreateInfo {
            SourceLanguage = ShaderSourceLanguage.Hlsl,
            ShaderCompiler = ShaderCompiler.Dxc,
            FilePath = shaderPath,
            ShaderSourceStreamFactory =
                _context.Factory?.CreateDefaultShaderSourceStreamFactory(
                    "assets/Shaders"
                )
        };

        shaderCI.Desc.Name = "ImGui VS";
        shaderCI.Desc.ShaderType = ShaderType.Vertex;
        shaderCI.EntryPoint = "VSMain";
        using var vs = device.CreateShader(shaderCI, out _);

        shaderCI.Desc.Name = "ImGui PS";
        shaderCI.Desc.ShaderType = ShaderType.Pixel;
        shaderCI.EntryPoint = "PSMain";
        using var ps = device.CreateShader(shaderCI, out _);

        GraphicsPipelineStateCreateInfo psoCI =
            new GraphicsPipelineStateCreateInfo();
        psoCI.PSODesc.Name = "ImGui PSO";
        psoCI.PSODesc.ResourceLayout.DefaultVariableType =
            ShaderResourceVariableType.Mutable;

        // Use standard Diligent variable description instead of
        // shaderAsset.GetResourceVariables
        psoCI.PSODesc.ResourceLayout.Variables = [
            new ShaderResourceVariableDesc {
                Name = "UniformBuffer",
                ShaderStages = ShaderType.Vertex,
                Type = ShaderResourceVariableType.Static
            },
            new ShaderResourceVariableDesc {
                Name = "g_Texture",
                ShaderStages = ShaderType.Pixel,
                Type = ShaderResourceVariableType.Mutable
            },
            new ShaderResourceVariableDesc {
                Name = "g_Texture_sampler",
                ShaderStages = ShaderType.Pixel,
                Type = ShaderResourceVariableType.Mutable
            }
        ];

        psoCI.GraphicsPipeline.NumRenderTargets = 1;
        psoCI.GraphicsPipeline.RTVFormats =
            [_context.SwapChain!.GetDesc().ColorBufferFormat];
        psoCI.GraphicsPipeline.DSVFormat =
            _context.SwapChain!.GetDesc().DepthBufferFormat;
        psoCI.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.TriangleList;

        psoCI.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        psoCI.GraphicsPipeline.DepthStencilDesc.DepthEnable = false;

        // Blend state for Alpha Blending
        var blendDesc = new BlendStateDesc();
        blendDesc.RenderTargets[0].BlendEnable = true;
        blendDesc.RenderTargets[0].SrcBlend = BlendFactor.SrcAlpha;
        blendDesc.RenderTargets[0].DestBlend = BlendFactor.InvSrcAlpha;
        blendDesc.RenderTargets[0].BlendOp = BlendOperation.Add;
        blendDesc.RenderTargets[0].SrcBlendAlpha = BlendFactor.One;
        blendDesc.RenderTargets[0].DestBlendAlpha = BlendFactor.InvSrcAlpha;
        blendDesc.RenderTargets[0].BlendOpAlpha = BlendOperation.Add;
        psoCI.GraphicsPipeline.BlendDesc = blendDesc;

        psoCI.GraphicsPipeline.InputLayout.LayoutElements = [
            new LayoutElement {
                InputIndex = 0,
                BufferSlot = 0,
                NumComponents = 2,
                ValueType = Diligent.ValueType.Float32,
                IsNormalized = false
            },
            new LayoutElement {
                InputIndex = 1,
                BufferSlot = 0,
                NumComponents = 2,
                ValueType = Diligent.ValueType.Float32,
                IsNormalized = false
            },
            new LayoutElement {
                InputIndex = 2,
                BufferSlot = 0,
                NumComponents = 4,
                ValueType = Diligent.ValueType.UInt8,
                IsNormalized = true
            }
        ];

        psoCI.Vs = vs;
        psoCI.Ps = ps;

        _pso = device.CreateGraphicsPipelineState(psoCI) ??
               throw new Exception(
                   "[ImGuiRenderer] Failed to create PSO: " +
                   "CreateGraphicsPipelineState returned null."
               );

        _pso.GetStaticVariableByName(ShaderType.Vertex, "UniformBuffer")
            ?.Set(_uniformBuffer, SetShaderResourceFlags.None);
        _srb = _pso.CreateShaderResourceBinding(true);

        // Name-based binding
        _srb.GetVariableByName(ShaderType.Pixel, "g_Texture")
            ?.Set(_fontTextureView, SetShaderResourceFlags.None);
        _srb.GetVariableByName(ShaderType.Pixel, "g_Texture_sampler")
            ?.Set(_sampler, SetShaderResourceFlags.None);
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        var device = _context.Device;
        if (device == null)
            return;

        if (_vertexBuffer == null || _vertexBufferSize < vertexCount)
        {
            _vertexBuffer?.Dispose();
            _vertexBufferSize = (int)(vertexCount * 1.5);
            BufferDesc vbDesc = new BufferDesc {
                Name = "ImGui Vertex Buffer",
                Size = (ulong)(_vertexBufferSize * sizeof(ImDrawVert)),
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            _vertexBuffer = device.CreateBuffer(vbDesc);
        }

        if (_indexBuffer == null || _indexBufferSize < indexCount)
        {
            _indexBuffer?.Dispose();
            _indexBufferSize = (int)(indexCount * 1.5);
            BufferDesc ibDesc = new BufferDesc {
                Name = "ImGui Index Buffer",
                Size = (ulong)(_indexBufferSize * sizeof(ushort)),
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.IndexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            _indexBuffer = device.CreateBuffer(ibDesc);
        }
    }

    public void Render(IDeviceContext context, ImDrawDataPtr drawData)
    {
        if (drawData.NativePtr == null || drawData.TotalVtxCount == 0)
            return;

        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);

        // Update Projection Matrix
        var io = ImGui.GetIO();
        float L = drawData.DisplayPos.X;
        float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float T = drawData.DisplayPos.Y;
        float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

        Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(L, R, B, T, 1.0f, -1.0f);

        // Transpose matrix for HLSL (Column-Major data expectation)
        mvp = Matrix4x4.Transpose(mvp);

        var mappedUniforms = context.MapBuffer<Matrix4x4>(
            _uniformBuffer, MapType.Write, MapFlags.Discard
        );
        mappedUniforms[0] = mvp;
        context.UnmapBuffer(_uniformBuffer, MapType.Write);

        // Update VB/IB
        var mappedVb = context.MapBuffer<ImDrawVert>(
            _vertexBuffer, MapType.Write, MapFlags.Discard
        );
        var mappedIb =
            context.MapBuffer<ushort>(_indexBuffer, MapType.Write, MapFlags.Discard);

        int vtxOffset = 0;
        int idxOffset = 0;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var cmdList = drawData.CmdLists[i];

            var vtxSpan = new Span<ImDrawVert>(
                (void *)cmdList.VtxBuffer.Data, cmdList.VtxBuffer.Size
            );
            vtxSpan.CopyTo(mappedVb.Slice(vtxOffset));

            var idxSpan = new Span<ushort>(
                (void *)cmdList.IdxBuffer.Data, cmdList.IdxBuffer.Size
            );
            idxSpan.CopyTo(mappedIb.Slice(idxOffset));

            vtxOffset += cmdList.VtxBuffer.Size;
            idxOffset += cmdList.IdxBuffer.Size;
        }

        context.UnmapBuffer(_vertexBuffer, MapType.Write);
        context.UnmapBuffer(_indexBuffer, MapType.Write);

        // Draw
        context.SetPipelineState(_pso);
        context.CommitShaderResources(_srb, ResourceStateTransitionMode.Transition);

        ulong[] offsets = [0];
        IBuffer[] vbs = [_vertexBuffer!];
        context.SetVertexBuffers(
            0,
            vbs,
            offsets,
            ResourceStateTransitionMode.Transition,
            SetVertexBuffersFlags.None
        );
        context.SetIndexBuffer(
            _indexBuffer!, 0, ResourceStateTransitionMode.Transition
        );

        vtxOffset = 0;
        idxOffset = 0;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var cmdList = drawData.CmdLists[i];
            for (int j = 0; j < cmdList.CmdBuffer.Size; j++)
            {
                var cmd = cmdList.CmdBuffer[j];
                if (cmd.UserCallback != IntPtr.Zero)
                {
                    // Handle callback
                }
                else
                {
                    var rect = new Rect {
                        Left = (int)cmd.ClipRect.X,
                        Top = (int)cmd.ClipRect.Y,
                        Right = (int)cmd.ClipRect.Z,
                        Bottom = (int)cmd.ClipRect.W
                    };
                    var scDesc = _context.SwapChain!.GetDesc();
                    context.SetScissorRects([rect], scDesc.Width, scDesc.Height);

                    DrawIndexedAttribs drawAttrs = new DrawIndexedAttribs {
                        IndexType = Diligent.ValueType.UInt16,
                        NumIndices = cmd.ElemCount,
                        FirstIndexLocation = (uint)(idxOffset + cmd.IdxOffset),
                        BaseVertex = (uint)(vtxOffset + (int)cmd.VtxOffset),
                        Flags = DrawFlags.VerifyAll
                    };
                    context.DrawIndexed(drawAttrs);
                }
            }
            vtxOffset += cmdList.VtxBuffer.Size;
            idxOffset += cmdList.IdxBuffer.Size;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _srb?.Dispose();
        _srb = null;

        _pso?.Dispose();
        _pso = null;

        _fontTextureView?.Dispose();
        _fontTextureView = null;

        _fontTexture?.Dispose();
        _fontTexture = null;

        _sampler?.Dispose();
        _sampler = null;

        _vertexBuffer?.Dispose();
        _vertexBuffer = null;

        _indexBuffer?.Dispose();
        _indexBuffer = null;

        _uniformBuffer?.Dispose();
        _uniformBuffer = null;

        if (ImGui.GetCurrentContext() != IntPtr.Zero)
            ImGui.DestroyContext();
    }
}
