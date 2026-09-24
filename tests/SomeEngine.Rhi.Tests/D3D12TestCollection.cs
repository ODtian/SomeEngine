using Xunit;

namespace SomeEngine.Rhi.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class D3D12TestCollection
{
    public const string Name = "D3D12";
}
