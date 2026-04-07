# Shader Asset Reflection Backup (2026-02-26)

This file backs up the binding-set based shader variable reflection logic before removal.

## assets/Schema/shader_asset.fbs (original section)

```fbs
table ShaderResourceReflection {
    name: string;
    category: ShaderResourceCategory;
    binding: uint = 4294967295;
    set: uint = 4294967295;
    stages: uint; // Diligent SHADER_TYPE bitmask
}
```

## src/SomeEngine.Assets/Importers/SlangShaderImporter.cs (original reflection key/assign)

```csharp
var backendResourceMaps =
    new Dictionary<
        string,
        Dictionary<
            (string Name, uint Set, uint Binding),
            (HashSet<ShaderStage> Stages, ShaderResourceCategory Category)
        >
    >();

// ...

private static void FinalizeReflection(
    Dictionary<
        (string Name, uint Set, uint Binding),
        (HashSet<ShaderStage> Stages, ShaderResourceCategory Category)
    > resourceMap,
    ShaderReflectionData dest
)
{
    dest.Resources ??= new List<ShaderResourceReflection>();
    foreach (var kvp in resourceMap)
    {
        uint stageMask = 0;
        foreach (var s in kvp.Value.Stages)
        {
            stageMask |= GetDiligentStageFlags(s);
        }

        dest.Resources.Add(
            new ShaderResourceReflection
            {
                Name = kvp.Key.Name,
                Set = kvp.Key.Set,
                Binding = kvp.Key.Binding,
                Category = kvp.Value.Category,
                Stages = stageMask,
            }
        );
    }
}

private static void CollectResourceFromVar(
    VariableLayoutReflection varLayout,
    ShaderStage stage,
    Dictionary<
        (string Name, uint Set, uint Binding),
        (HashSet<ShaderStage> Stages, ShaderResourceCategory Category)
    > resources
)
{
    // ...

    uint binding = varLayout.BindingIndex;
    uint set = varLayout.BindingSpace;

    Console.WriteLine(
        $"[Slang Reflection] Detected resource: {name} Stage: {stage} Set: {set} Binding: {binding} Category: {category}"
    );

    if (!resources.TryGetValue((name, set, binding), out var entry))
    {
        var mappedCategory = MapCategory(varLayout);

        entry = (new HashSet<ShaderStage>(), mappedCategory);
        resources[(name, set, binding)] = entry;
    }
    entry.Stages.Add(stage);
}
```

## src/SomeEngine.Render/RHI/ShaderExtensions.cs (original set/binding use)

```csharp
var mergedVariables =
    new Dictionary<(uint Set, uint Binding, int Class), ShaderResourceVariableDesc>();

// ...

var key = (r.Set, r.Binding, regClass);

var desc = new ShaderResourceVariableDesc
{
    Name = r.Name,
    Type = varType.Value,
    ShaderStages = (Diligent.ShaderType)r.Stages,
    Binding = r.Binding,
    Set = r.Set,
    ResourceType = type,
};
```
