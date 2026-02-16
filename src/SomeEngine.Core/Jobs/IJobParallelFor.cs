namespace SomeEngine.Core.Jobs;

public interface IJobParallelFor
{
    void Execute(int index);
}
