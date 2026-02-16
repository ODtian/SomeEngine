namespace SomeEngine.Core.Jobs;

public static class JobHandleExtensions
{
    public static void Complete(this JobHandle handle)
    {
        JobSystem.Wait(handle);
    }
}
