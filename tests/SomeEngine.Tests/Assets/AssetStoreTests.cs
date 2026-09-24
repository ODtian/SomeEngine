using System.Threading;
using SomeEngine.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Tests.Assets;

public class AssetStoreTests
{
    [Fact]
    public void Add_ReplacesGuid_AndInvalidatesOld()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        var first = new Material { Name = "First" };
        var second = new Material { Name = "Second" };

        ulong initialVersion = store.GetVersion<Material>();
        Handle<Material> firstHandle = store.Add(guid, first);
        ulong firstVersion = store.GetVersion<Material>();
        Handle<Material> secondHandle = store.Add(guid, second);
        ulong secondVersion = store.GetVersion<Material>();

        Assert.True(firstVersion > initialVersion);
        Assert.True(secondVersion > firstVersion);
        Assert.Equal(firstHandle.Id, secondHandle.Id);
        Assert.NotEqual(firstHandle.Generation, secondHandle.Generation);
        Assert.False(store.TryGet(firstHandle, out _));
        Assert.True(store.TryFind(guid, out Handle<Material> found));
        Assert.Equal(secondHandle, found);
        Assert.Same(second, store.Get(secondHandle));
    }

    [Fact]
    public void GenericStore_ReplacesGuid_AndInvalidatesOld()
    {
        using var store = new AssetStore<Material>();
        AssetGuid guid = AssetGuid.New();
        var first = new Material { Name = "First" };
        var second = new Material { Name = "Second" };

        ulong initialVersion = store.Version;
        Handle<Material> firstHandle = store.Add(guid, first);
        ulong firstVersion = store.Version;
        Handle<Material> secondHandle = store.Add(guid, second);
        ulong secondVersion = store.Version;

        Assert.True(firstVersion > initialVersion);
        Assert.True(secondVersion > firstVersion);
        Assert.Equal(firstHandle.Id, secondHandle.Id);
        Assert.NotEqual(firstHandle.Generation, secondHandle.Generation);
        Assert.False(store.TryGet(firstHandle, out _));
        Assert.True(store.TryFind(guid, out Handle<Material> found));
        Assert.Equal(secondHandle, found);
        Assert.Same(second, store.Get(secondHandle));
    }

    [Fact]
    public void MeshBindings_StoreRuntimeHandles()
    {
        Handle<Material> material = new(12, 1);
        MeshMaterialBindings bindings = new() { Materials = new[] { material } };

        Assert.Equal(material, bindings.Materials.Span[0]);
    }

    [Fact]
    public async Task Request_LoadsAsset_AndStoresHandle()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();

        Handle<Material> handle = await store.Request(
            guid,
            static (_, _) => new Material { Name = "Requested" });

        Assert.True(handle.IsValid);
        Assert.True(store.TryFind(guid, out Handle<Material> found));
        Assert.Equal(handle, found);
        Assert.True(store.TryGet(handle, out Material? material));
        Assert.Equal("Requested", material!.Name);
    }

    [Fact]
    public async Task Request_DeduplicatesConcurrentGuid()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        int calls = 0;
        var source = new TaskCompletionSource<Material?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Handle<Material>> first = store.Request(
            guid,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return source.Task;
            });

        Task<Handle<Material>> second = store.Request(
            guid,
            static (_, _) => Task.FromResult<Material?>(new Material { Name = "Duplicate" }));

        source.SetResult(new Material { Name = "Loaded" });
        Handle<Material>[] handles = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(handles[0], handles[1]);
        Assert.True(store.TryGet(handles[0], out Material? material));
        Assert.Equal("Loaded", material!.Name);
    }

    [Fact]
    public async Task Request_ReturnsReadyHandle()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        Handle<Material> ready = store.Add(guid, new Material { Name = "Ready" });
        Func<AssetGuid, CancellationToken, Material?> load =
            static (_, _) => throw new InvalidOperationException("Loader should not run for ready assets.");

        Handle<Material> requested = await store.Request(guid, load);

        Assert.Equal(ready, requested);
    }
}
