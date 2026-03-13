using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

/// <summary>
/// 渲染特性：组织一组相关的 RenderPass 并管理它们的共享资源。
/// Feature 是 RenderGraph 和 RenderPass 之间的中间抽象层。
/// </summary>
public interface IRenderFeature : IDisposable
{
    /// <summary>Feature 的名称，用于调试和日志。</summary>
    string Name { get; }

    /// <summary>
    /// 一次性初始化。用于创建 PSO、编译 Shader 等长生命周期对象。
    /// </summary>
    void Initialize(RenderContext context);

    /// <summary>
    /// 每帧调用。Feature 在此方法中向 RenderGraph 注册所有需要的 pass 和创建资源。
    /// Feature 间通过 graph.GetResourceHandle(name) 获取其他 Feature 创建的资源。
    /// </summary>
    void AddPasses(RenderGraph graph);
}
