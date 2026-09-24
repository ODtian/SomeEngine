using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class SlangEntryMeta
{
    public sealed record Attr(string Name, List<string> Args);

    public static List<Attr> Read(FunctionReflection function)
    {
        var attributes = new List<Attr>();
        if (function == FunctionReflection.Null)
        {
            return attributes;
        }

        for (uint attributeIndex = 0; attributeIndex < function.AttributeCount; attributeIndex++)
        {
            var attribute = function.GetAttribute(attributeIndex);
            if (attribute == AttributeReflection.Null || string.IsNullOrEmpty(attribute.Name))
            {
                continue;
            }

            var args = new List<string>((int)attribute.ArgumentCount);
            for (uint argIndex = 0; argIndex < attribute.ArgumentCount; argIndex++)
            {
                args.Add(attribute.GetArgumentValueString(argIndex));
            }

            attributes.Add(new Attr(attribute.Name, args));
        }

        return attributes;
    }

    public static Schema.ShaderEntryPointMetadata? Create(
        int variantIndex,
        IReadOnlyList<Attr> attributes)
        => null;
}
