namespace SomeEngine.Rhi;

public enum Backend
{
    Null,
    D3D12,
    Vulkan,
    Metal,
}

public enum QueueType
{
    Graphics,
    Compute,
    Copy,
}

public enum Format
{
    Unknown,
    R8Unorm,
    R8UInt,
    R16UInt,
    R16Float,
    Rg8Unorm,
    Rg16UInt,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Bgra8Unorm,
    Bgra8UnormSrgb,
    Rgb10A2Unorm,
    Rgba16Float,
    R32UInt,
    R32Float,
    Rg16Float,
    Rg32Float,
    Rgb32Float,
    Rgba32Float,
    D32Float,
    D24UnormS8UInt,
    Bc1RgbaUnorm,
    Bc1RgbaUnormSrgb,
    Bc2RgbaUnorm,
    Bc2RgbaUnormSrgb,
    Bc3RgbaUnorm,
    Bc3RgbaUnormSrgb,
    Bc4RUnorm,
    Bc5RgUnorm,
    Bc6HUFloat,
    Bc7RgbaUnorm,
    Bc7RgbaUnormSrgb,
}

public enum ResourceDimension
{
    Texture1D,
    Texture2D,
    Texture3D,
    TextureCube,
}

public enum TextureViewDimension
{
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMultisampled,
    Texture2DMultisampledArray,
    TextureCube,
    TextureCubeArray,
    Texture3D,
}

public enum MemoryClass
{
    DeviceLocal,
    CpuUpload,
    CpuReadback,
}

public enum ResourceOwnership
{
    Committed,
    Placed,
    Swapchain,
    External,
}

public enum MemoryHeapKind
{
    Buffer,
    Texture,
    RenderTargetOrDepthStencil,
    Mixed,
}

[Flags]
public enum MemoryHeapFlags
{
    None = 0,
    AllowAliasing = 1 << 0,
}

[Flags]
public enum BindFlags
{
    None = 0,
    VertexBuffer = 1 << 0,
    IndexBuffer = 1 << 1,
    ConstantBuffer = 1 << 2,
    ShaderResource = 1 << 3,
    UnorderedAccess = 1 << 4,
    RenderTarget = 1 << 5,
    DepthStencil = 1 << 6,
    IndirectArgument = 1 << 7,
    CopySource = 1 << 8,
    CopyDestination = 1 << 9,
}

public enum ResourceState
{
    Undefined,
    Common,
    GenericRead,
    Present,
    VertexBuffer,
    IndexBuffer,
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
    RenderTarget,
    DepthRead,
    DepthWrite,
    CopySource,
    CopyDestination,
    ResolveSource,
    ResolveDestination,
    IndirectArgument,
    QueryResolve,
}

public enum ViewKind
{
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
    RenderTarget,
    DepthStencil,
}

[Flags]
public enum ShaderStageFlags
{
    None = 0,
    Vertex = 1 << 0,
    Pixel = 1 << 1,
    Compute = 1 << 2,
    Hull = 1 << 3,
    Domain = 1 << 4,
    Geometry = 1 << 5,
    Amplification = 1 << 6,
    Mesh = 1 << 7,
    RayGeneration = 1 << 8,
    AnyHit = 1 << 9,
    ClosestHit = 1 << 10,
    Miss = 1 << 11,
    Intersection = 1 << 12,
    Callable = 1 << 13,
    AllGraphics = Vertex | Pixel | Hull | Domain | Geometry | Amplification | Mesh,
    AllRayTracing = RayGeneration | AnyHit | ClosestHit | Miss | Intersection | Callable,
    All = AllGraphics | Compute | AllRayTracing,
}

public enum ShaderStage
{
    Vertex,
    Pixel,
    Compute,
    Hull,
    Domain,
    Geometry,
    Amplification,
    Mesh,
    RayGeneration,
    AnyHit,
    ClosestHit,
    Miss,
    Intersection,
    Callable,
}

public enum ShaderBytecodeFormat
{
    Dxil,
    Spirv,
    MetalLibrary,
}

public enum BindingType
{
    None,
    ConstantBuffer,
    StorageBufferRead,
    StorageBufferReadWrite,
    RawBufferRead,
    RawBufferReadWrite,
    TextureRead,
    TextureReadWrite,
    Sampler,
    AccelerationStructure,
}

[Flags]
public enum BindingFlags
{
    None = 0,
    PartiallyBound = 1 << 0,
    Bindless = 1 << 1,
    DynamicOffset = 1 << 2,
}

internal enum PipelineKind
{
    Graphics,
    Compute,
    Mesh,
    RayTracing,
}

public enum PrimitiveTopology
{
    TriangleList,
    TriangleStrip,
    LineList,
    PointList,
    PatchList,
}

public enum CullMode
{
    None,
    Front,
    Back,
}

public enum FillMode
{
    Solid,
    Wireframe,
}

public enum CompareOp
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

public enum IndexFormat
{
    UInt16,
    UInt32,
}

public enum LoadOp
{
    Load,
    Clear,
    DontCare,
}

public enum StoreOp
{
    Store,
    DontCare,
}

public enum MapMode
{
    Write,
    Read,
}

public enum FilterMode
{
    Nearest,
    Linear,
}

public enum MipmapMode
{
    Nearest,
    Linear,
}

public enum AddressMode
{
    ClampToEdge,
    Repeat,
    MirrorRepeat,
    ClampToBorder,
}

public enum BorderColor
{
    TransparentBlack,
    OpaqueBlack,
    OpaqueWhite,
}

public enum VertexInputRate
{
    Vertex,
    Instance,
}

[Flags]
public enum ColorWriteMask
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha,
}

public enum BlendFactor
{
    Zero,
    One,
    SourceColor,
    OneMinusSourceColor,
    DestinationColor,
    OneMinusDestinationColor,
    SourceAlpha,
    OneMinusSourceAlpha,
    DestinationAlpha,
    OneMinusDestinationAlpha,
}

public enum BlendOp
{
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max,
}

[Flags]
public enum FormatSupport
{
    None = 0,
    ShaderSample = 1 << 0,
    RenderTarget = 1 << 1,
    DepthStencil = 1 << 2,
    UnorderedAccess = 1 << 3,
    CopySource = 1 << 4,
    CopyDestination = 1 << 5,
    VertexAttribute = 1 << 6,
    Present = 1 << 7,
}

[Flags]
public enum SampleCountFlags
{
    None = 0,
    Count1 = 1 << 0,
    Count2 = 1 << 1,
    Count4 = 1 << 2,
    Count8 = 1 << 3,
    Count16 = 1 << 4,
}

public enum QueryType
{
    Timestamp,
    Occlusion,
    PipelineStatistics,
}

public enum ColorSpace
{
    Sdr,
    ScRgbLinear,
    Hdr10,
}

public enum SwapchainMode
{
    Windowed,
    BorderlessFullscreen,
    ExclusiveFullscreen,
}

[Flags]
public enum BindingSetFlags
{
    None = 0,
    Mutable = 1 << 0,
}

public enum AccelerationStructureKind
{
    BottomLevel,
    TopLevel,
}

[Flags]
public enum AccelBuildFlags
{
    None = 0,
    PreferFastTrace = 1 << 0,
    PreferFastBuild = 1 << 1,
    AllowUpdate = 1 << 2,
    AllowCompaction = 1 << 3,
}

public enum AccelCopyMode
{
    Clone,
    Compact,
}

public enum AccelGeomKind
{
    Triangles,
    Aabbs,
    Instances,
}

[Flags]
public enum AccelGeomFlags
{
    None = 0,
    Opaque = 1 << 0,
    NoDuplicateAnyHitInvocation = 1 << 1,
}

public enum RtGroupKind
{
    General,
    TrianglesHitGroup,
    ProceduralHitGroup,
}

public enum AliasingResourceKind
{
    None,
    Buffer,
    Texture,
}

public enum ErrorCode
{
    InvalidDescriptor,
    InvalidHandle,
    UnsupportedFeature,
    BackendFailure,
    DeviceLost,
    ValidationFailure,
}
