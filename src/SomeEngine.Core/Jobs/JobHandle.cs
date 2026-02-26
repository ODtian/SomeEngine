namespace SomeEngine.Core.Jobs;

public readonly struct JobHandle
{
    internal readonly int CounterId;
    internal readonly int Version;

    internal JobHandle(int counterId, int version)
    {
        CounterId = counterId;
        Version = version;
    }

    public bool IsCompleted => JobSystem.IsJobCompleted(this);
}
