using SomeEngine.Core.Jobs;

namespace SomeEngine.Core.ECS;

public class SystemContext
{
    /// <summary>
    /// The dependency handle for the current frame's job chain.
    /// Systems should use this to schedule their jobs and update it with the new handle.
    /// </summary>
    public JobHandle GlobalDependency { get; set; }
}
