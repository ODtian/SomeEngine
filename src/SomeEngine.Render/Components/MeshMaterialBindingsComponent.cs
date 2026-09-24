using System;
using SomeEngine.Assets;
using SomeEngine.Render.Materials;
using SomeECS.Core.Components;

namespace SomeEngine.Render.Components;

public struct MeshMaterialBindings : IComponent
{
    public ReadOnlyMemory<Handle<Material>> Materials;
}
