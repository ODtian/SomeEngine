using ImGuiNET;
using SomeEngine.Render.RHI;

namespace SomeEngine.Runtime;

internal sealed class PipelineIssueLog : PipelineIssueSink
{
    private readonly List<PipelineIssue> _issues = [];
    private readonly Dictionary<IssueKey, int> _keys = [];

    public void Add(PipelineIssue issue)
    {
        var key = new IssueKey(issue);
        if (_keys.TryGetValue(key, out int index))
        {
            PipelineIssue current = _issues[index];
            long count = (long)current.Count + issue.Count;
            _issues[index] = current with { Count = count > int.MaxValue ? int.MaxValue : (int)count };
            return;
        }

        _keys.Add(key, _issues.Count);
        _issues.Add(issue);
    }

    public void Draw()
    {
        if (_issues.Count == 0)
            return;

        if (!ImGui.TreeNode("Pipeline Issues"))
            return;

        int count = Math.Min(_issues.Count, 12);
        for (int i = 0; i < count; i++)
            ImGui.TextWrapped(Text(_issues[i]));
        if (_issues.Count > count)
            ImGui.TextDisabled($"+ {_issues.Count - count} more");
        ImGui.TreePop();
    }

    private readonly record struct IssueKey(
        PipelineIssueResult Result,
        PipelineNeed Need,
        PipelineStatus Status,
        string Site,
        string Source,
        string Name,
        string Owner,
        string? Error)
    {
        public IssueKey(PipelineIssue issue)
            : this(
                issue.Result,
                issue.Need,
                issue.Status,
                issue.Site,
                issue.Source,
                issue.Name,
                issue.Owner,
                issue.Error)
        {
        }
    }

    private static string Text(PipelineIssue issue)
    {
        string site = string.IsNullOrWhiteSpace(issue.Site) ? "unknown site" : issue.Site;
        string source = string.IsNullOrWhiteSpace(issue.Source) ? "unknown source" : issue.Source;
        string error = string.IsNullOrWhiteSpace(issue.Error) ? string.Empty : $" error={issue.Error}";
        return $"Pipeline {issue.Result}: {issue.Name} status={issue.Status} need={issue.Need} source={source} site={site} owner={issue.Owner} count={issue.Count}{error}";
    }

}
