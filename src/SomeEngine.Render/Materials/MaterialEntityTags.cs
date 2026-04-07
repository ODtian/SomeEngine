using Friflo.Engine.ECS;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Render.Materials;

public static class MaterialEntityTags
{
    public static bool Apply(Entity entity, string tagName)
    {
        switch (Normalize(tagName))
        {
            case "opaque":
                entity.AddTag<Opaque>();
                return true;
            case "masked":
                entity.AddTag<Masked>();
                return true;
            case "translucent":
                entity.AddTag<Translucent>();
                return true;
            case "twosided":
                entity.AddTag<TwoSided>();
                return true;
            default:
                return false;
        }
    }

    public static bool Remove(Entity entity, string tagName)
    {
        switch (Normalize(tagName))
        {
            case "opaque":
                entity.RemoveTag<Opaque>();
                return true;
            case "masked":
                entity.RemoveTag<Masked>();
                return true;
            case "translucent":
                entity.RemoveTag<Translucent>();
                return true;
            case "twosided":
                entity.RemoveTag<TwoSided>();
                return true;
            default:
                return false;
        }
    }

    private static string Normalize(string tagName)
    {
        return tagName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
    }
}
