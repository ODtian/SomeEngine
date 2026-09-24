using System.Text;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.RHI;

internal static class ShaderKeys
{
    public static string Name(Shader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        return string.IsNullOrWhiteSpace(shader.Name) ? nameof(Shader) : shader.Name;
    }

    public static string Version(Shader shader, params string[] entryPoints)
    {
        ArgumentNullException.ThrowIfNull(shader);
        var builder = new StringBuilder(Name(shader));

        for (int i = 0; i < entryPoints.Length; i++)
        {
            string entry = entryPoints[i];
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            builder.Append('|');
            builder.Append(entry);
            builder.Append(':');

            bool matched = false;
            foreach (ShaderVariant variant in shader.Variants)
            {
                if (!string.Equals(variant.EntryPoint, entry, StringComparison.Ordinal))
                    continue;

                if (matched)
                    builder.Append(',');
                builder.Append(variant.Backend);
                builder.Append('/');
                builder.Append(variant.Stage);
                builder.Append('/');
                builder.Append(variant.ContentHash);
                matched = true;
            }

            if (!matched)
                builder.Append("missing");
        }

        return builder.ToString();
    }
}
