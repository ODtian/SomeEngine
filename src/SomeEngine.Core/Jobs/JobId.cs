namespace SomeEngine.Core.Jobs;

public readonly struct JobId(ushort typeId, int index, int counterId)
{
    public readonly ushort TypeId = typeId;
    public readonly int Index = index; // DataStore Index
    public readonly int CounterId = counterId; // JobCounter Index
}
