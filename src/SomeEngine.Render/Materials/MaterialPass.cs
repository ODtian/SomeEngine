using SomeEngine.Assets;

namespace SomeEngine.Render.Materials;

public readonly record struct MaterialPass(
    string Target,
    Handle<Shader> Shader,
    string EntryPoint,
    MaterialState State);
