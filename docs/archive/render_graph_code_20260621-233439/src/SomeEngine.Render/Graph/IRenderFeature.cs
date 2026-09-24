namespace SomeEngine.Render.Graph;

public interface IRenderFeature : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Features exchange same-frame graph resources through typed pass data and RenderGraphBlackboard.
    /// </summary>
    void AddPasses(RenderGraph graph);
}
