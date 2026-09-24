using System.Runtime.CompilerServices;
using SomeEngine.Core.Collections;

namespace SomeEngine.Core.Tests.Collections;

public class InlineListTests
{
    [Fact]
    public void InlineList_InvalidStorageWithoutInlineArray_FailsFast()
    {
        InlineList<object, NotInlineStorage> list = default;

        var ex = Assert.Throws<InvalidOperationException>(() => list.Add(new object()));

        Assert.Contains("InlineArrayAttribute", ex.Message);
    }

    [Fact]
    public void InlineList_InlineArrayCapacity_ComesFromStorageType()
    {
        InlineList<object, Inline2<object>> list = default;

        list.Add(new object());
        list.Add(new object());
        list.Add(new object());

        Assert.Equal(3, list.Count);
        Assert.Equal(3, list.AsSpan().Length);
    }

    [Fact]
    public void InlineList_ElementTypeMismatch_FailsFast()
    {
        InlineList<object, WrongElementStorage> list = default;

        var ex = Assert.Throws<InvalidOperationException>(() => list.Add(new object()));

        Assert.Contains("exactly one instance field", ex.Message);
    }

    private struct NotInlineStorage
    {
    }

    [InlineArray(2)]
    private struct Inline2<T>
    {
        private T _element0;
    }

    [InlineArray(1)]
    private struct WrongElementStorage
    {
        private int _element0;
    }
}

