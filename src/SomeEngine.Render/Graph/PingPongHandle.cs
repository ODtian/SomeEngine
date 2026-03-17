using Diligent;

namespace SomeEngine.Render.Graph;

/// <summary>
/// 通用 RenderGraph 资源 ping-pong。
/// 管理跨帧交替的两个同规格资源 handle（如 HiZ 纹理的 A/B 交替）。
/// </summary>
public class PingPongHandle
{
    private bool _flip;
    private bool _hasHistory;

    /// <summary>是否存在前一帧的有效数据。</summary>
    public bool HasHistory => _hasHistory;

    /// <summary>
    /// 准备当前帧和前一帧的纹理 handle。
    /// 每帧调用一次，在 <see cref="EndFrame"/> 之前。
    /// </summary>
    public void Prepare(
        RenderGraph graph, string name, TextureDesc desc,
        out RenderGraphHandle current, out RenderGraphHandle previous)
    {
        string nameA = $"{name}_A";
        string nameB = $"{name}_B";
        current = graph.CreateTexture(_flip ? nameA : nameB, desc with { Name = _flip ? nameA : nameB });
        previous = graph.CreateTexture(_flip ? nameB : nameA, desc with { Name = _flip ? nameB : nameA });
    }

    /// <summary>
    /// 准备当前帧和前一帧的 buffer handle。
    /// </summary>
    public void Prepare(
        RenderGraph graph, string name, BufferDesc desc,
        out RenderGraphHandle current, out RenderGraphHandle previous)
    {
        string nameA = $"{name}_A";
        string nameB = $"{name}_B";
        current = graph.CreateBuffer(_flip ? nameA : nameB, desc);
        previous = graph.CreateBuffer(_flip ? nameB : nameA, desc);
    }

    /// <summary>帧结束时调用，翻转 ping-pong 并标记有历史数据。</summary>
    public void EndFrame()
    {
        _flip = !_flip;
        _hasHistory = true;
    }

    /// <summary>重置 ping-pong 状态（如模式切换时需要丢弃历史）。</summary>
    public void Reset()
    {
        _flip = false;
        _hasHistory = false;
    }
}
