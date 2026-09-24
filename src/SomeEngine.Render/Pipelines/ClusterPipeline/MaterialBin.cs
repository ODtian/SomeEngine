using SomeEngine.Assets;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class MaterialBin
{
    public IDevice? Device;
    public PipelineTicket PipelineState;
    internal PipelineRequest PipelineRequest;
    internal ShaderBindingTable? Bindings;

    public PassShader Compute;
    public PassShader Vertex;
    public PassShader Pixel;
    public Handle<Material> MaterialHandle;
    internal Material? Material;
    public ScalarLayout ScalarLayout = ScalarLayout.Empty;
    public MaterialState State;
    public RenderGraphHandle MaterialScalarRegion;
    internal MaterialBindings MaterialBindings;
    public int BinIndex;
    public int ArgsIndex;

    public void Check(string owner)
    {
        if (BinIndex < 0)
            throw new InvalidOperationException($"{owner} cluster bin index must be non-negative.");
        if (ArgsIndex < 0)
            throw new InvalidOperationException($"{owner} cluster args index must be non-negative.");
    }

    public void Dispose(PipelineCache store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Release(ref PipelineState);

        if (Device != null)
        {
            if (Bindings.HasValue)
                ShaderBindings.Destroy(Device, Bindings.Value);
        }

        Bindings = null;
        Material = null;
        MaterialScalarRegion = default;
        MaterialBindings = MaterialBindings.Empty;
        Device = null;
    }
}
