using Diligent;
using Friflo.Engine.ECS;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public sealed class StencilConfigAuthoring : IMaterialAuthoring
{
    public string AttributeName => "StencilConfig";

    public void Apply(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        Apply(entity, GetArgs(attribute), variantIndex, shader);
    }

    internal static void Apply(Entity entity, ReadOnlySpan<string> args, int variantIndex, ShaderAsset shader)
    {
        byte stencilRef = args.Length > 0 && byte.TryParse(args[0], out var parsedRef)
            ? parsedRef
            : (byte)0;
        string compareName = args.Length > 1 ? args[1] : "always";
        string passName = args.Length > 2 ? args[2] : "keep";

        if (entity.TryGetComponent<StencilState>(out _))
        {
            ref var stencil = ref entity.GetComponent<StencilState>();
            stencil.Ref = stencilRef;
            stencil.Compare = ParseCompare(compareName);
            stencil.PassOp = ParsePassOp(passName);
        }
        else
        {
            entity.AddComponent(new StencilState
            {
                Ref = stencilRef,
                Compare = ParseCompare(compareName),
                PassOp = ParsePassOp(passName),
            });
        }
    }

    private static ComparisonFunction ParseCompare(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "never" => ComparisonFunction.Never,
            "less" => ComparisonFunction.Less,
            "equal" => ComparisonFunction.Equal,
            "lessequal" => ComparisonFunction.LessEqual,
            "greater" => ComparisonFunction.Greater,
            "notequal" => ComparisonFunction.NotEqual,
            "greaterequal" => ComparisonFunction.GreaterEqual,
            _ => ComparisonFunction.Always,
        };
    }

    private static StencilOp ParsePassOp(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "zero" => StencilOp.Zero,
            "replace" => StencilOp.Replace,
            "incr_sat" => StencilOp.IncrSat,
            "decr_sat" => StencilOp.DecrSat,
            "invert" => StencilOp.Invert,
            "incr_wrap" => StencilOp.IncrWrap,
            "decr_wrap" => StencilOp.DecrWrap,
            _ => StencilOp.Keep,
        };
    }

    private static string[] GetArgs(AttributeReflection attribute)
    {
        var args = new string[attribute.ArgumentCount];
        for (uint i = 0; i < attribute.ArgumentCount; i++)
        {
            args[i] = attribute.GetArgumentValueString(i);
        }

        return args;
    }
}
