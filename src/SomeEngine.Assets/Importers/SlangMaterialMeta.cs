using System.Text;
using System.Text.RegularExpressions;
using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static partial class SlangMaterialMeta
{
    public const byte Texture = 0;
    public const byte Sampler = 1;
    public const byte Buffer = 2;
    public const byte Unknown = 255;

    public static void BindEntry(
        VariableLayoutReflection parameter,
        Schema.ShaderMetadata metadata,
        HashSet<string>? resourceNames = null)
    {
        if (parameter == VariableLayoutReflection.Null)
        {
            return;
        }

        TypeLayoutReflection typeLayout = parameter.TypeLayout.UnwrapArray();
        if (typeLayout.Kind != SlangTypeKind.Struct)
        {
            return;
        }

        BindType(typeLayout, metadata, resourceNames);
    }

    public static void AddBinding(
        Schema.ShaderMetadata metadata,
        string? name,
        byte resourceType)
    {
        if (resourceType == Unknown || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        metadata.MaterialBindings ??= [];
        foreach (Schema.ShaderMaterialBinding existing in metadata.MaterialBindings)
        {
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (existing.ResourceType != resourceType)
            {
                throw new InvalidOperationException(
                    $"Shader material binding '{name}' was reflected with conflicting resource types {existing.ResourceType} and {resourceType}.");
            }

            return;
        }

        metadata.MaterialBindings.Add(
            new Schema.ShaderMaterialBinding
            {
                Name = name,
                ResourceType = resourceType,
            });
    }

    public static List<string> ScalarTypes(
        DeclReflection root,
        string source,
        IReadOnlyList<DependencyEntryData> dependencies,
        string projectRoot)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddType(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        void Visit(DeclReflection decl)
        {
            if (decl == DeclReflection.Null)
            {
                return;
            }

            if (decl.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = decl.Type;
                if (type != TypeReflection.Null && HasAttribute(type, "MaterialScalars"))
                {
                    AddType(type.Name);
                }
            }

            for (int i = 0; i < decl.ChildrenCount; i++)
            {
                Visit(decl.GetChild((uint)i));
            }
        }

        Visit(root);
        ReadTypes(source, AddType);
        foreach (DependencyEntryData dependency in dependencies)
        {
            string path = DependencyPath(projectRoot, dependency.RelativePath);
            if (File.Exists(path))
            {
                ReadTypes(File.ReadAllText(path), AddType);
            }
        }

        return result;
    }

    public static void ScalarLayouts(
        ShaderReflection reflection,
        IReadOnlyList<string> materialScalarTypes,
        Schema.ShaderMetadata metadata)
    {
        if (reflection == ShaderReflection.Null || materialScalarTypes.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            metadata.MaterialScalarLayouts?.Select(static layout => layout.Name ?? string.Empty)
                ?? [],
            StringComparer.Ordinal);

        metadata.MaterialScalarLayouts ??= [];

        foreach (string typeName in materialScalarTypes)
        {
            if (string.IsNullOrWhiteSpace(typeName) || !existing.Add(typeName))
            {
                continue;
            }

            TypeReflection? maybeLayoutType = reflection.FindTypeByName(typeName);
            if (maybeLayoutType == null)
            {
                continue;
            }

            TypeLayoutReflection? maybeLayout = reflection.GetTypeLayout(
                maybeLayoutType.Value,
                LayoutRules.Default);
            if (maybeLayout == null)
            {
                continue;
            }

            AddScalarLayout(metadata, typeName, maybeLayout.Value);
        }
    }

    public static byte ResourceType(TypeReflection type)
    {
        if (type == TypeReflection.Null)
        {
            return Unknown;
        }

        type = type.UnwrapArray();
        SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;

        return type.Kind switch
        {
            SlangTypeKind.SamplerState => Sampler,
            SlangTypeKind.ConstantBuffer => Buffer,
            SlangTypeKind.Resource
            or SlangTypeKind.TextureBuffer
            or SlangTypeKind.ShaderStorageBuffer =>
                shape is SlangResourceShape.StructuredBuffer
                    or SlangResourceShape.ByteAddressBuffer
                    or SlangResourceShape.TextureBuffer
                    ? Buffer
                    : Texture,
            _ => Unknown,
        };
    }

    private static void BindType(
        TypeLayoutReflection typeLayout,
        Schema.ShaderMetadata metadata,
        HashSet<string>? resourceNames)
    {
        if (typeLayout == TypeLayoutReflection.Null)
        {
            return;
        }

        for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
        {
            VariableLayoutReflection field = typeLayout.GetFieldByIndex(fieldIndex);
            if (field == VariableLayoutReflection.Null || string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            byte resourceType = ResourceType(field.Type);
            if (resourceType != Unknown)
            {
                AddBinding(metadata, field.Name, resourceType);
                resourceNames?.Add(field.Name);
                continue;
            }

            BindType(field.TypeLayout.UnwrapArray(), metadata, resourceNames);
        }
    }

    private static void AddScalarLayout(
        Schema.ShaderMetadata metadata,
        string typeName,
        TypeLayoutReflection typeLayout)
    {
        uint payloadSize = checked((uint)typeLayout.GetSize(SlangParameterCategory.Uniform));
        var fields = new List<Schema.ShaderMaterialScalarField>((int)typeLayout.FieldCount);
        uint baseOffset = uint.MaxValue;
        uint maxFieldEnd = 0;

        for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
        {
            VariableLayoutReflection fieldLayout = typeLayout.GetFieldByIndex(fieldIndex);
            if (fieldLayout == VariableLayoutReflection.Null || string.IsNullOrWhiteSpace(fieldLayout.Name))
            {
                continue;
            }

            TypeLayoutReflection fieldTypeLayout = fieldLayout.TypeLayout;
            uint fieldOffset = checked((uint)fieldLayout.GetOffset(SlangParameterCategory.Uniform));
            uint fieldSize = checked((uint)fieldTypeLayout.GetSize(SlangParameterCategory.Uniform));
            if (fieldSize == 0)
            {
                fieldSize = checked((uint)fieldTypeLayout.GetStride(SlangParameterCategory.Uniform));
            }

            uint fieldEnd = fieldOffset + fieldSize;
            baseOffset = Math.Min(baseOffset, fieldOffset);
            maxFieldEnd = Math.Max(maxFieldEnd, fieldEnd);

            fields.Add(
                new Schema.ShaderMaterialScalarField
                {
                    Name = fieldLayout.Name,
                    Offset = fieldOffset,
                    Size = fieldSize,
                    RowCount = fieldTypeLayout.RowCount,
                    ColumnCount = fieldTypeLayout.ColumnCount,
                    ScalarType = checked((byte)fieldTypeLayout.ScalarType),
                });
        }

        if (payloadSize == 0)
        {
            payloadSize = maxFieldEnd;
        }

        if (baseOffset != uint.MaxValue && baseOffset > 0)
        {
            if (payloadSize >= maxFieldEnd)
            {
                payloadSize -= baseOffset;
            }

            payloadSize = Math.Max(payloadSize, maxFieldEnd - baseOffset);
            for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
            {
                Schema.ShaderMaterialScalarField field = fields[fieldIndex];
                field.Offset -= baseOffset;
                fields[fieldIndex] = field;
            }
        }

        metadata.MaterialScalarLayouts ??= [];
        metadata.MaterialScalarLayouts.Add(
            new Schema.ShaderMaterialScalarLayout
            {
                Name = typeName,
                Size = payloadSize,
                Fields = fields,
            });
    }

    private static void ReadTypes(
        string source,
        Action<string> addType)
    {
        foreach (Match match in MaterialScalarsRegex().Matches(source))
        {
            if (match.Groups.Count > 1)
            {
                addType(match.Groups[1].Value);
            }
        }
    }

    private static bool HasAttribute(TypeReflection type, string name)
    {
        for (uint i = 0; i < type.AttributeCount; i++)
        {
            AttributeReflection attribute = type.GetAttribute(i);
            if (attribute != AttributeReflection.Null
                && string.Equals(attribute.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DependencyPath(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }

    [GeneratedRegex(
        @"\[\s*MaterialScalars(?:\s*\([^\)]*\))?\s*\]\s*struct\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex MaterialScalarsRegex();
}
