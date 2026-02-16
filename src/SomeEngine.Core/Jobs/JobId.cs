namespace SomeEngine.Core.Jobs;

public readonly struct JobId
{
    public readonly ushort TypeId;
    public readonly int Index; // DataStore Index
    public readonly int CounterId; // JobCounter Index

    public JobId(ushort typeId, int index, int counterId)
    {
        TypeId = typeId;
        Index = index;
        CounterId = counterId;
    }
}
