using System;

namespace SomeEngine.Assets;

public readonly struct Handle<T> : IEquatable<Handle<T>>
{
    public Handle(int id, int generation)
    {
        Id = id;
        Generation = generation;
    }

    public int Id { get; }
    public int Generation { get; }
    public bool IsValid => Id > 0 && Generation > 0;

    public bool Equals(Handle<T> other)
        => Id == other.Id && Generation == other.Generation;

    public override bool Equals(object? obj)
        => obj is Handle<T> other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Id, Generation);

    public static bool operator ==(Handle<T> left, Handle<T> right)
        => left.Equals(right);

    public static bool operator !=(Handle<T> left, Handle<T> right)
        => !left.Equals(right);

    public override string ToString()
        => IsValid ? $"{typeof(T).Name}#{Id}:{Generation}" : $"{typeof(T).Name}#Invalid";
}
