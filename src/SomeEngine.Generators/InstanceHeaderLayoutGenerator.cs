using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SomeEngine.Generators;

[Generator]
public sealed class InstanceHeaderLayoutGenerator : IIncrementalGenerator
{
    private const string HeaderFieldAttributeName = "SomeEngine.Render.Data.GpuInstanceHeaderFieldAttribute";
    private const string DataFlagAttributeName = "SomeEngine.Render.Data.GpuInstanceDataFlagAttribute";
    private const int HeaderStrideAlignment = 16;
    private const int MinHeaderStrideBytes = 32;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (spc, compilation) =>
            {
                LayoutInfo? layout = BuildLayout(compilation);
                if (layout is null)
                {
                    return;
                }

                spc.AddSource("InstanceHeaderLayout.g.cs", GenerateCSharp(layout.Value));
            });
    }

    private static LayoutInfo? BuildLayout(Compilation compilation)
    {
        ImmutableArray<AttributeData> attributes = compilation.Assembly.GetAttributes();
        var fields = ImmutableArray.CreateBuilder<FieldInfo>();
        var flags = ImmutableArray.CreateBuilder<FlagInfo>();

        foreach (AttributeData attribute in attributes)
        {
            string? attributeName = attribute.AttributeClass?.ToDisplayString();
            if (string.Equals(attributeName, HeaderFieldAttributeName, StringComparison.Ordinal))
            {
                FieldInfo? field = ReadField(attribute);
                if (field is not null)
                {
                    fields.Add(field.Value);
                }
            }
            else if (string.Equals(attributeName, DataFlagAttributeName, StringComparison.Ordinal))
            {
                FlagInfo? flag = ReadFlag(attribute);
                if (flag is not null)
                {
                    flags.Add(flag.Value);
                }
            }
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var orderedFields = fields
            .OrderBy(static field => field.Order)
            .ThenBy(static field => field.CSharpName, StringComparer.Ordinal)
            .ToImmutableArray();
        var orderedFlags = flags
            .OrderBy(static flag => flag.Bit)
            .ThenBy(static flag => flag.CSharpName, StringComparer.Ordinal)
            .ToImmutableArray();

        int offset = 0;
        var placedFields = ImmutableArray.CreateBuilder<PlacedFieldInfo>();
        foreach (FieldInfo field in orderedFields)
        {
            int size = GetFieldSize(field.Type);
            placedFields.Add(new PlacedFieldInfo(
                field.CSharpName,
                field.SlangName,
                field.LoadFunctionSuffix,
                field.Type,
                offset,
                size));
            offset += size;
        }

        int strideBytes = AlignUp(Math.Max(MinHeaderStrideBytes, offset), HeaderStrideAlignment);
        ulong hash = ComputeLayoutHash(placedFields, orderedFlags, strideBytes);
        return new LayoutInfo(placedFields.ToImmutable(), orderedFlags, strideBytes, hash);
    }

    private static FieldInfo? ReadField(AttributeData attribute)
    {
        ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
        if (args.Length != 3)
        {
            return null;
        }

        if (args[0].Value is not string csharpName
            || args[1].Value is not int type
            || args[2].Value is not int order)
        {
            return null;
        }

        string slangName = GetNamedString(attribute, "SlangName") ?? ToHeaderFieldSlangName(csharpName);
        string loadFunctionSuffix = GetNamedString(attribute, "LoadFunctionSuffix") ?? ToLoadFunctionSuffix(csharpName);

        if (string.IsNullOrWhiteSpace(csharpName)
            || string.IsNullOrWhiteSpace(slangName)
            || string.IsNullOrWhiteSpace(loadFunctionSuffix))
        {
            return null;
        }

        return new FieldInfo(csharpName, slangName, loadFunctionSuffix, (HeaderFieldType)type, order);
    }

    private static FlagInfo? ReadFlag(AttributeData attribute)
    {
        ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
        if (args.Length != 2)
        {
            return null;
        }

        if (args[0].Value is not string csharpName
            || args[1].Value is not int bit)
        {
            return null;
        }

        string slangName = GetNamedString(attribute, "SlangName") ?? ToDataFlagSlangName(csharpName);

        if (string.IsNullOrWhiteSpace(csharpName) || string.IsNullOrWhiteSpace(slangName) || bit < 0 || bit >= 32)
        {
            return null;
        }

        return new FlagInfo(csharpName, slangName, bit);
    }

    private static string? GetNamedString(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
        {
            if (string.Equals(named.Key, name, StringComparison.Ordinal))
            {
                return named.Value.Value as string;
            }
        }

        return null;
    }

    private static string ToHeaderFieldSlangName(string csharpName)
        => "IH_" + ToUpperSnake(csharpName);

    private static string ToDataFlagSlangName(string csharpName)
        => "INSTANCE_DATA_FLAG_" + ToUpperSnake(csharpName);

    private static string ToLoadFunctionSuffix(string csharpName)
    {
        const string implicitPrefix = "Instance";
        if (csharpName.StartsWith(implicitPrefix, StringComparison.Ordinal)
            && csharpName.Length > implicitPrefix.Length)
        {
            return csharpName.Substring(implicitPrefix.Length);
        }

        return csharpName;
    }

    private static string ToUpperSnake(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (i > 0 && ShouldInsertUnderscore(value, i))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    private static bool ShouldInsertUnderscore(string value, int index)
    {
        char previous = value[index - 1];
        char current = value[index];
        if (previous == '_' || current == '_')
        {
            return false;
        }

        if (char.IsUpper(current))
        {
            if (char.IsLower(previous) || char.IsDigit(previous))
            {
                return true;
            }

            return char.IsUpper(previous)
                && index + 1 < value.Length
                && char.IsLower(value[index + 1]);
        }

        return char.IsDigit(current) && char.IsLetter(previous);
    }

    private static string GenerateCSharp(LayoutInfo layout)
    {
        string slangSource = GenerateSlang(layout);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace SomeEngine.Render.Data;");
        sb.AppendLine();
        sb.AppendLine("[System.Flags]");
        sb.AppendLine("public enum GpuInstanceDataFlags : uint");
        sb.AppendLine("{");
        sb.AppendLine("    None = 0,");
        foreach (FlagInfo flag in layout.Flags)
        {
            sb.Append("    ");
            sb.Append(flag.CSharpName);
            sb.Append(" = 1u << ");
            sb.Append(flag.Bit.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(",");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("public static partial class InstanceHeaderLayout");
        sb.AppendLine("{");
        foreach (PlacedFieldInfo field in layout.Fields)
        {
            sb.Append("    public const int ");
            sb.Append(field.CSharpName);
            sb.Append(" = ");
            sb.Append(field.Offset.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(";");
        }

        sb.AppendLine();
        sb.Append("    public const int StrideBytes = ");
        sb.Append(layout.StrideBytes.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine(";");
        sb.Append("    public const ulong LayoutHash = 0x");
        sb.Append(layout.Hash.ToString("X16", CultureInfo.InvariantCulture));
        sb.AppendLine("ul;");
        sb.AppendLine();
        sb.Append("    public const string SlangSource = ");
        sb.Append(ToCSharpStringLiteral(slangSource));
        sb.AppendLine(";");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateSlang(LayoutInfo layout)
    {
        uint lowHash = (uint)layout.Hash;
        uint highHash = (uint)(layout.Hash >> 32);

        var sb = new StringBuilder();
        sb.AppendLine("#ifndef INSTANCE_HEADER_LAYOUT_SLANG");
        sb.AppendLine("#define INSTANCE_HEADER_LAYOUT_SLANG");
        sb.AppendLine();
        sb.Append("static const uint INSTANCE_HEADER_STRIDE_BYTES = ");
        sb.Append(layout.StrideBytes.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine(";");
        sb.Append("static const uint2 INSTANCE_HEADER_LAYOUT_HASH = uint2(0x");
        sb.Append(lowHash.ToString("X8", CultureInfo.InvariantCulture));
        sb.Append("u, 0x");
        sb.Append(highHash.ToString("X8", CultureInfo.InvariantCulture));
        sb.AppendLine("u);");
        sb.AppendLine();

        foreach (PlacedFieldInfo field in layout.Fields)
        {
            sb.Append("static const uint ");
            sb.Append(field.SlangName);
            sb.Append(" = ");
            sb.Append(field.Offset.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(";");
        }

        if (layout.Flags.Length > 0)
        {
            sb.AppendLine();
            foreach (FlagInfo flag in layout.Flags)
            {
                sb.Append("static const uint ");
                sb.Append(flag.SlangName);
                sb.Append(" = 1u << ");
                sb.Append(flag.Bit.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(";");
            }
        }

        sb.AppendLine();
        sb.AppendLine("uint InstanceHeaderLoadU32(ByteAddressBuffer headers, uint instanceID, uint byteOffset)");
        sb.AppendLine("{");
        sb.AppendLine("    return headers.Load(instanceID * INSTANCE_HEADER_STRIDE_BYTES + byteOffset);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("float InstanceHeaderLoadF32(ByteAddressBuffer headers, uint instanceID, uint byteOffset)");
        sb.AppendLine("{");
        sb.AppendLine("    return asfloat(InstanceHeaderLoadU32(headers, instanceID, byteOffset));");
        sb.AppendLine("}");

        foreach (PlacedFieldInfo field in layout.Fields)
        {
            sb.AppendLine();
            string returnType = field.Type == HeaderFieldType.Float32 ? "float" : "uint";
            string loader = field.Type == HeaderFieldType.Float32 ? "InstanceHeaderLoadF32" : "InstanceHeaderLoadU32";
            sb.Append(returnType);
            sb.Append(" LoadInstance");
            sb.Append(field.LoadFunctionSuffix);
            sb.AppendLine("(ByteAddressBuffer headers, uint instanceID)");
            sb.AppendLine("{");
            sb.Append("    return ");
            sb.Append(loader);
            sb.Append("(headers, instanceID, ");
            sb.Append(field.SlangName);
            sb.AppendLine(");");
            sb.AppendLine("}");
        }

        sb.AppendLine();
        sb.AppendLine("#endif");
        return sb.ToString();
    }

    private static string ToCSharpStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 32);
        sb.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static int GetFieldSize(HeaderFieldType type)
        => type switch
        {
            HeaderFieldType.UInt32 => 4,
            HeaderFieldType.Float32 => 4,
            _ => 4,
        };

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    private static ulong ComputeLayoutHash(
        IEnumerable<PlacedFieldInfo> fields,
        IEnumerable<FlagInfo> flags,
        int strideBytes)
    {
        ulong hash = 14695981039346656037ul;
        HashString(ref hash, "InstanceHeaderLayout/v1");
        HashUInt32(ref hash, (uint)strideBytes);

        foreach (PlacedFieldInfo field in fields)
        {
            HashString(ref hash, field.CSharpName);
            HashString(ref hash, field.SlangName);
            HashUInt32(ref hash, (uint)field.Type);
            HashUInt32(ref hash, (uint)field.Offset);
            HashUInt32(ref hash, (uint)field.Size);
        }

        foreach (FlagInfo flag in flags)
        {
            HashString(ref hash, flag.CSharpName);
            HashString(ref hash, flag.SlangName);
            HashUInt32(ref hash, (uint)flag.Bit);
        }

        return hash;
    }

    private static void HashString(ref ulong hash, string value)
    {
        foreach (char ch in value)
        {
            HashByte(ref hash, (byte)(ch & 0xFF));
            HashByte(ref hash, (byte)(ch >> 8));
        }
        HashByte(ref hash, 0);
    }

    private static void HashUInt32(ref ulong hash, uint value)
    {
        HashByte(ref hash, (byte)value);
        HashByte(ref hash, (byte)(value >> 8));
        HashByte(ref hash, (byte)(value >> 16));
        HashByte(ref hash, (byte)(value >> 24));
    }

    private static void HashByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
    }

    private readonly struct LayoutInfo
    {
        public LayoutInfo(
            ImmutableArray<PlacedFieldInfo> fields,
            ImmutableArray<FlagInfo> flags,
            int strideBytes,
            ulong hash)
        {
            Fields = fields;
            Flags = flags;
            StrideBytes = strideBytes;
            Hash = hash;
        }

        public ImmutableArray<PlacedFieldInfo> Fields { get; }
        public ImmutableArray<FlagInfo> Flags { get; }
        public int StrideBytes { get; }
        public ulong Hash { get; }
    }

    private readonly struct FieldInfo
    {
        public FieldInfo(
            string csharpName,
            string slangName,
            string loadFunctionSuffix,
            HeaderFieldType type,
            int order)
        {
            CSharpName = csharpName;
            SlangName = slangName;
            LoadFunctionSuffix = loadFunctionSuffix;
            Type = type;
            Order = order;
        }

        public string CSharpName { get; }
        public string SlangName { get; }
        public string LoadFunctionSuffix { get; }
        public HeaderFieldType Type { get; }
        public int Order { get; }
    }

    private readonly struct PlacedFieldInfo
    {
        public PlacedFieldInfo(
            string csharpName,
            string slangName,
            string loadFunctionSuffix,
            HeaderFieldType type,
            int offset,
            int size)
        {
            CSharpName = csharpName;
            SlangName = slangName;
            LoadFunctionSuffix = loadFunctionSuffix;
            Type = type;
            Offset = offset;
            Size = size;
        }

        public string CSharpName { get; }
        public string SlangName { get; }
        public string LoadFunctionSuffix { get; }
        public HeaderFieldType Type { get; }
        public int Offset { get; }
        public int Size { get; }
    }

    private readonly struct FlagInfo
    {
        public FlagInfo(string csharpName, string slangName, int bit)
        {
            CSharpName = csharpName;
            SlangName = slangName;
            Bit = bit;
        }

        public string CSharpName { get; }
        public string SlangName { get; }
        public int Bit { get; }
    }

    private enum HeaderFieldType
    {
        UInt32 = 0,
        Float32 = 1,
    }
}
