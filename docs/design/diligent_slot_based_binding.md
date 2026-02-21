# Diligent Core Slot-Based Binding API Design

## Background
Currently, Diligent Core enforces a **name-based** resource binding model. It automatically assigns binding indices and patches SPIR-V shaders to match. To support a **location-based (slot-based)** workflow (similar to native Vulkan/D3D12), we need to modify the source code to allow explicit binding specification and binding-based resource matching.

## Proposed API Changes

### 1. Structure Updates

#### `PipelineResourceDesc` (Explicit Signature)
Modify `d:\SomeEngine\external\DiligentCore\Graphics\GraphicsEngine\interface\PipelineResourceSignature.h`.
Used for high-performance, shared resource layouts.

```cpp
struct PipelineResourceDesc
{
    // ... existing members ...

    /// Explicit binding index. 
    /// If set to ~0u (default), the engine automatically assigns an index.
    Uint32 Binding DEFAULT_INITIALIZER(~0u);
    
    /// Explicit descriptor set index.
    /// If set to ~0u (default), the engine assigns set based on variable type.
    Uint32 Set     DEFAULT_INITIALIZER(~0u);
};
```

#### `ShaderResourceVariableDesc` (Implicit Signature)
Modify `d:\SomeEngine\external\DiligentCore\Graphics\GraphicsEngine\interface\PipelineState.h`.
Used for simple, shader-driven binding where `PipelineResourceSignature` is not manually created.

```cpp
struct ShaderResourceVariableDesc
{
    const Char* Name DEFAULT_INITIALIZER(nullptr);

    /// Explicit binding/set to match in shader instead of using Name.
    Uint32 Binding DEFAULT_INITIALIZER(~0u);
    Uint32 Set     DEFAULT_INITIALIZER(~0u);

    SHADER_TYPE                   ShaderStages DEFAULT_INITIALIZER(SHADER_TYPE_UNKNOWN);
    SHADER_RESOURCE_VARIABLE_TYPE Type         DEFAULT_INITIALIZER(SHADER_RESOURCE_VARIABLE_TYPE_STATIC);
    SHADER_VARIABLE_FLAGS         Flags        DEFAULT_INITIALIZER(SHADER_VARIABLE_FLAG_NONE);
};
```

### 2. Implementation Logic

#### A. Resource Matching (`PipelineStateVkImpl.cpp`)
**Current Behavior**: `GetResourceAttribution` searches exclusively by `Name`.
**New Behavior**:
1.  Parse Shader Reflection to get the **original** `binding` and `set` decoration from SPIR-V.
2.  In `GetResourceAttribution`, first attempt to find a resource where `Signature.Binding == Shader.Binding` and `Signature.Set == Shader.Set`.
3.  If no match is found by location, fallback to `Name` matching for backward compatibility.
4.  **Skip Patching**: If matched by location and values are identical, skip SPIR-V binary patching.

#### B. Runtime Binding API (`IShaderResourceBinding`)
Add methods to set resources via slots to avoid string hashing/comparison at runtime.

```cpp
// interface/ShaderResourceBinding.h
VIRTUAL IShaderResourceVariable* METHOD(GetVariableByBinding)(THIS_
                                                               SHADER_TYPE ShaderType,
                                                               Uint32      Binding,
                                                               Uint32      Set) PURE;
```

### 3. Usage Examples

#### Option A: High-Performance (Explicit)
```csharp
var resources = new PipelineResourceDesc[] {
    new PipelineResourceDesc { 
        Binding = 0, Set = 0, 
        VarType = ShaderResourceVariableType.Mutable,
        ResourceType = ShaderResourceType.BufferSrv 
    }
};
// Use in PipelineResourceSignatureDesc...
```

#### Option B: Simple (Implicit)
```csharp
cppsCi.PSODesc.ResourceLayout.Variables = new[] {
    new ShaderResourceVariableDesc {
        Binding = 0, Set = 0, 
        Type = ShaderResourceVariableType.Mutable
    }
};
```

```hlsl
// HLSL
// Names don't match, but bindings do
Texture2D    g_Tex  : register(t0); // Matches TextureA (binding 0)
cbuffer      g_Buff : register(b5); // Matches BufferB (binding 5)
```

## Benefits
- **Decoupling**: Shaders and C++ code don't need to agree on variable names, only on the "Contract" (Slot IDs).
- **Stability**: Renaming variables in Shader doesn't break C++ binding logic.
- **Portability**: Easier to port engines that already use slot-based systems (like Unity/UE) to Diligent.

## Feasibility Notes
- **Vulkan**: Straightforward. The `VkDescriptorSetLayoutBinding` struct takes a `binding` field directly.
- **D3D12**: Root Signature construction logic needs to ensure Root Parameters map to the correct registers. Diligent maps resources to Root Indices. We would need to ensure the `Register` in `D3D12_DESCRIPTOR_RANGE` matches the explicit binding.
- **D3D11/OpenGL**: These APIs use distinct register spaces per resource type (t0, b0, u0). Explicit bindings would need to be interpreted as these slot indices.

## Conclusion
This modification is feasible and aligns well with modern low-level API usage patterns while maintaining Diligent's cross-platform abstractions.
