using Friflo.Engine.ECS;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Materials;

public interface IMaterialAuthoring
{
    string AttributeName { get; }

    void Apply(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader);
}
