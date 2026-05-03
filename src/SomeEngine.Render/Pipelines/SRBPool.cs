using System;
using System.Collections.Concurrent;
using Diligent;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Eliminates the ConcurrentBag + Rent/Return boilerplate across static PSO classes.
/// </summary>
internal sealed class SRBPool : IDisposable
{
    private readonly ConcurrentBag<IShaderResourceBinding> _pool = [];

    public IShaderResourceBinding Rent(IPipelineState pso)
        => _pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    public void Return(IShaderResourceBinding srb) => _pool.Add(srb);

    public void Dispose()
    {
        while (_pool.TryTake(out IShaderResourceBinding? srb))
        {
            srb.Dispose();
        }
    }
}
