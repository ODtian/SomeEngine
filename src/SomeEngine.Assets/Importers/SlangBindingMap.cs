using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class SlangBindingMap
{
    private const byte Unknown = 0;
    private const byte ConstantBuffer = 1;
    private const byte TextureSrv = 2;
    private const byte BufferSrv = 3;
    private const byte TextureUav = 4;
    private const byte BufferUav = 5;
    private const byte Sampler = 6;
    private const byte InputAttachment = 7;
    private const byte AccelStruct = 8;

    public readonly record struct ResourceKey(
        string Name,
        uint Binding,
        uint Space,
        byte ResourceType,
        Schema.ShaderBindingType BindingType);

    public readonly record struct SpaceKey(SlangParameterCategory Category, uint Space);

    public static void Fill(
        Dictionary<ResourceKey, uint> resourceMap,
        Schema.ShaderReflectionData dest)
    {
        dest.Resources ??= [];

        foreach (var kvp in resourceMap
            .OrderBy(static kvp => kvp.Key.Space)
            .ThenBy(static kvp => kvp.Key.Binding)
            .ThenBy(static kvp => kvp.Key.ResourceType)
            .ThenBy(static kvp => kvp.Key.BindingType)
            .ThenBy(static kvp => kvp.Key.Name, StringComparer.Ordinal))
        {
            dest.Resources.Add(
                new Schema.ShaderResourceReflection
                {
                    Name = kvp.Key.Name,
                    Stages = kvp.Value,
                    Binding = kvp.Key.Binding,
                    Space = kvp.Key.Space,
                    ResourceType = kvp.Key.ResourceType,
                    BindingType = kvp.Key.BindingType,
                });
        }
    }

    public static Schema.ShaderReflectionData Create(Dictionary<ResourceKey, uint> resourceMap)
    {
        var reflectionData = new Schema.ShaderReflectionData { Resources = [] };
        Fill(resourceMap, reflectionData);
        return reflectionData;
    }

    public static void Merge(
        Dictionary<ResourceKey, uint> destination,
        Dictionary<ResourceKey, uint> source)
    {
        foreach (var kvp in source)
        {
            destination[kvp.Key] = destination.GetValueOrDefault(kvp.Key) | kvp.Value;
        }
    }

    public static Dictionary<SpaceKey, uint> Next(
        Dictionary<ResourceKey, uint> resources,
        string backend)
    {
        var result = new Dictionary<SpaceKey, uint>();
        foreach (ResourceKey resource in resources.Keys)
        {
            var key = new SpaceKey(BindSpace(backend, resource.ResourceType), resource.Space);
            uint nextBinding = resource.Binding + 1;
            if (!result.TryGetValue(key, out uint existing) || nextBinding > existing)
            {
                result[key] = nextBinding;
            }
        }

        return result;
    }

    public static void Collect(
        VariableLayoutReflection varLayout,
        Schema.ShaderStage stage,
        Dictionary<ResourceKey, uint> resources,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases = null,
        HashSet<string>? skipNames = null)
    {
        Visit(varLayout, 0, 0, false);

        void Visit(
            VariableLayoutReflection layout,
            uint baseBinding,
            uint baseSpace,
            bool fieldBinding)
        {
            if (layout == VariableLayoutReflection.Null
                || TryAdd(layout, baseBinding, baseSpace, fieldBinding))
            {
                return;
            }

            TypeLayoutReflection typeLayout = layout.TypeLayout.UnwrapArray();
            if (typeLayout == TypeLayoutReflection.Null)
            {
                return;
            }

            uint childBaseBinding = fieldBinding ? baseBinding : layout.BindingIndex;
            uint childBaseSpace = fieldBinding
                ? baseSpace
                : GetSpace(layout, SlangParameterCategory.DescriptorTableSlot, 0);
            for (uint i = 0; i < typeLayout.FieldCount; i++)
            {
                Visit(typeLayout.GetFieldByIndex(i), childBaseBinding, childBaseSpace, true);
            }
        }

        bool TryAdd(
            VariableLayoutReflection layout,
            uint baseBinding,
            uint baseSpace,
            bool fieldBinding)
        {
            var resourceInfo = ResourceInfo(layout, out _);
            if (resourceInfo.ResourceType == Unknown || string.IsNullOrEmpty(layout.Name))
            {
                return resourceInfo.ResourceType != Unknown;
            }

            if (skipNames?.Contains(layout.Name) == true)
            {
                return true;
            }

            SlangParameterCategory bindingNamespace = BindSpace(
                backend,
                layout,
                resourceInfo.ResourceType);
            uint space = GetSpace(layout, bindingNamespace, baseSpace);
            uint relativeBinding = fieldBinding
                ? baseBinding + layout.BindingIndex
                : layout.BindingIndex;
            uint binding = relativeBinding;

            if (entryBases != null)
            {
                var namespaceKey = new SpaceKey(bindingNamespace, space);
                entryBases.TryGetValue(namespaceKey, out uint baseEntryPointBinding);
                binding = baseEntryPointBinding + relativeBinding;
            }

            var key = new ResourceKey(
                layout.Name,
                binding,
                space,
                resourceInfo.ResourceType,
                resourceInfo.BindingType);
            resources[key] = resources.GetValueOrDefault(key) | StageFlags(stage);
            return true;
        }
    }

    private static SlangParameterCategory BindSpace(
        string backend,
        VariableLayoutReflection layout,
        byte resourceType)
    {
        return layout.Category == SlangParameterCategory.DescriptorTableSlot
            ? SlangParameterCategory.DescriptorTableSlot
            : BindSpace(backend, resourceType);
    }

    private static SlangParameterCategory BindSpace(
        string backend,
        byte resourceType)
    {
        if (string.Equals(backend, "spirv", StringComparison.Ordinal))
        {
            return SlangParameterCategory.DescriptorTableSlot;
        }

        return resourceType switch
        {
            ConstantBuffer => SlangParameterCategory.ConstantBuffer,
            TextureSrv or BufferSrv or InputAttachment or AccelStruct => SlangParameterCategory.ShaderResource,
            TextureUav or BufferUav => SlangParameterCategory.UnorderedAccess,
            Sampler => SlangParameterCategory.SamplerState,
            _ => SlangParameterCategory.None,
        };
    }

    private static (byte ResourceType, Schema.ShaderBindingType BindingType) ResourceInfo(
        VariableLayoutReflection layout,
        out SlangParameterCategory category)
    {
        category = Normalize(layout.Category);
        TypeReflection type = layout.Type.UnwrapArray();
        SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;

        var reflectedResource = category switch
        {
            SlangParameterCategory.ConstantBuffer
            or SlangParameterCategory.PushConstantBuffer =>
                (ConstantBuffer, Schema.ShaderBindingType.ConstantBuffer),
            SlangParameterCategory.SamplerState =>
                (Sampler, Schema.ShaderBindingType.Sampler),
            SlangParameterCategory.Subpass =>
                (InputAttachment, Schema.ShaderBindingType.TextureRead),
            SlangParameterCategory.ShaderResource => ReadOnly(shape),
            SlangParameterCategory.UnorderedAccess => ReadWrite(shape),
            _ => (Unknown, Schema.ShaderBindingType.None),
        };

        if (reflectedResource.Item1 != Unknown)
        {
            return reflectedResource;
        }

        switch (type.Kind)
        {
            case SlangTypeKind.ConstantBuffer:
                category = SlangParameterCategory.ConstantBuffer;
                return (ConstantBuffer, Schema.ShaderBindingType.ConstantBuffer);
            case SlangTypeKind.SamplerState:
                category = SlangParameterCategory.SamplerState;
                return (Sampler, Schema.ShaderBindingType.Sampler);
            case SlangTypeKind.Resource:
            case SlangTypeKind.TextureBuffer:
            case SlangTypeKind.ShaderStorageBuffer:
                bool isWrite = IsWrite(type.ResourceAccess);
                category = isWrite
                    ? SlangParameterCategory.UnorderedAccess
                    : SlangParameterCategory.ShaderResource;
                return isWrite ? ReadWrite(shape) : ReadOnly(shape);
            default:
                return (Unknown, Schema.ShaderBindingType.None);
        }
    }

    private static SlangParameterCategory Normalize(SlangParameterCategory category)
        => category == SlangParameterCategory.DescriptorTableSlot
            ? SlangParameterCategory.Uniform
            : category;

    private static uint GetSpace(
        VariableLayoutReflection layout,
        SlangParameterCategory category,
        uint fallback)
    {
        uint space = checked((uint)layout.GetBindingSpace(category));
        if (space == 0 && layout.BindingSpace != 0)
        {
            space = layout.BindingSpace;
        }

        return space == 0 ? fallback : space;
    }

    private static bool IsWrite(SlangResourceAccess access)
        => access is SlangResourceAccess.ReadWrite
            or SlangResourceAccess.RasterOrdered
            or SlangResourceAccess.Append
            or SlangResourceAccess.Consume
            or SlangResourceAccess.Write;

    private static (byte ResourceType, Schema.ShaderBindingType BindingType) ReadOnly(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferSrv, Schema.ShaderBindingType.RawBufferRead),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferSrv, Schema.ShaderBindingType.StorageBufferRead),
            SlangResourceShape.AccelerationStructure =>
                (AccelStruct, Schema.ShaderBindingType.AccelerationStructure),
            SlangResourceShape.TextureSubpass =>
                (InputAttachment, Schema.ShaderBindingType.TextureRead),
            _ => (TextureSrv, Schema.ShaderBindingType.TextureRead),
        };

    private static (byte ResourceType, Schema.ShaderBindingType BindingType) ReadWrite(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferUav, Schema.ShaderBindingType.RawBufferReadWrite),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferUav, Schema.ShaderBindingType.StorageBufferReadWrite),
            _ => (TextureUav, Schema.ShaderBindingType.TextureReadWrite),
        };

    private static uint StageFlags(Schema.ShaderStage stage)
        => stage switch
        {
            Schema.ShaderStage.Vertex => 0x01,
            Schema.ShaderStage.Pixel => 0x02,
            Schema.ShaderStage.Geometry => 0x04,
            Schema.ShaderStage.Hull => 0x08,
            Schema.ShaderStage.Domain => 0x10,
            Schema.ShaderStage.Compute => 0x20,
            Schema.ShaderStage.Amplification => 0x40,
            Schema.ShaderStage.Mesh => 0x80,
            Schema.ShaderStage.RayGen => 0x100,
            Schema.ShaderStage.RayMiss => 0x200,
            Schema.ShaderStage.RayClosestHit => 0x400,
            Schema.ShaderStage.RayAnyHit => 0x800,
            Schema.ShaderStage.RayIntersection => 0x1000,
            Schema.ShaderStage.Callable => 0x2000,
            _ => 0,
        };
}
