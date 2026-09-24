using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS;
using SomeEngine.Core.Math;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeECS.Core;
using SomeECS.Core.Entities;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;

namespace SomeEngine.Tests.Pipelines;

public sealed class MaterialItemsTests
{
    [Fact]
    public void Add_UpdatesOnlyChangedMaterialRows()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> first = StoreMaterial(assets, shader, "First", 1.25f);
        Handle<Material> second = StoreMaterial(assets, shader, "Second", 3.5f);
        Handle<Material> stable = StoreMaterial(assets, shader, "Stable", 2.25f);
        EntityId firstEntity = AddRenderInstance(renderWorld.World, sourceIndex: 0, first);
        EntityId stableEntity = AddRenderInstance(renderWorld.World, sourceIndex: 1, stable);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);

        AddFrame(graph, materials, renderWorld);
        uint stableOffset = HeaderU32(materials.Headers, 1, InstanceHeaderLayout.SlotOffset);
        ClearDirty(renderWorld.World, firstEntity);
        ClearDirty(renderWorld.World, stableEntity);

        renderWorld.World.AddOrSet(firstEntity, new RenderMaterials { Materials = new[] { second } });
        AddFrame(graph, materials, renderWorld);

        Assert.Equal(3.5f, HeaderFloat(materials.Headers, 0, InstanceHeaderLayout.BoundsExpansionWorld));
        Assert.Equal(2.25f, HeaderFloat(materials.Headers, 1, InstanceHeaderLayout.BoundsExpansionWorld));
        Assert.Equal(stableOffset, HeaderU32(materials.Headers, 1, InstanceHeaderLayout.SlotOffset));
        MaterialBin[] bins = (MaterialBin[])Bins(materials, "_sw");
        MaterialBin changedBin = Assert.Single(bins, bin => bin.MaterialHandle == second);
        Assert.True(changedBin.PipelineState.IsValid);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache!.GetStatus(changedBin.PipelineState));
        Assert.True((renderWorld.World.Read<InstanceDirty>(firstEntity).Flags & InstanceDirtyFlags.Header) != 0);
        Assert.False(renderWorld.World.Has<InstanceDirty>(stableEntity));
    }

    [Fact]
    public void Add_RebuildsWhenMaterialChanges()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "Changing", 1.25f);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);

        AddFrame(graph, materials, renderWorld);
        Assert.Equal(1.25f, HeaderFloat(materials.Headers, 0, InstanceHeaderLayout.BoundsExpansionWorld));

        assets.Get(material).SetPasses(
        [
            new MaterialPass(
                "cluster.raster.sw",
                shader,
                "Main",
                MaterialState.Default with { BoundsExpansion = 4.75f }),
        ]);
        AddFrame(graph, materials, renderWorld);

        Assert.Equal(4.75f, HeaderFloat(materials.Headers, 0, InstanceHeaderLayout.BoundsExpansionWorld));
    }

    [Fact]
    public void Add_KeepsBinsForScalarOnlyChange()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "ScalarOnly", 1.25f);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);

        AddFrame(graph, materials, renderWorld);
        object bins = Bins(materials, "_sw");

        Material value = assets.Get(material);
        value.Roughness = 0.5f;
        value.TouchScalars();
        AddFrame(graph, materials, renderWorld);

        Assert.Same(bins, Bins(materials, "_sw"));
    }

    [Fact]
    public void Add_KeepsFailedMaterialTicket()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreEmptyShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "Owner", 1.25f);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);

        AddFrame(graph, materials, renderWorld);

        PipelineTicket ticket = Assert.Single((MaterialBin[])Bins(materials, "_sw")).PipelineState;
        Assert.True(ticket.IsValid);
        Assert.Equal(PipelineStatus.Failed, context.PipelineCache!.GetStatus(ticket));
        PipelineWarmup warmup = context.PipelineCache!.Warmup([ticket], 0);

        Assert.Equal(1, warmup.Requested);
        Assert.Equal(0, warmup.Processed);
        Assert.Equal(1, warmup.Failed);
        Assert.Equal(1, warmup.OptionalFailed);
        Assert.False(warmup.BudgetLimited);
        Assert.Contains("has no bytecode", context.PipelineCache.GetError(ticket)!.Message);
    }

    [Fact]
    public void Add_UsesWarmupSources()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "Budgeted", 1.25f);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);
        using PipelineSourceLease source = context.AddPipelineSource(materials);

        graph.BeginFrame();
        materials.PrepareFrame(renderWorld, useCache: false);
        materials.RecordFrame(graph);

        PipelineTicket ticket = Assert.Single((MaterialBin[])Bins(materials, "_sw")).PipelineState;
        Assert.True(ticket.IsValid);
        Assert.Equal(PipelineStatus.Queued, context.PipelineCache!.GetStatus(ticket));

        PipelineWarmup warmup = context.PipelineCache.Warmup([ticket], 0);
        Assert.Equal(1, warmup.Requested);
        Assert.Equal(0, warmup.Processed);
        Assert.Equal(1, warmup.Queued);
        Assert.Equal(1, warmup.OptionalQueued);
        Assert.True(warmup.BudgetLimited);

        PipelineWarmup sourceWarmup = context.WarmupSources(1);
        Assert.Equal(1, sourceWarmup.Requested);
        Assert.Equal(1, sourceWarmup.Processed);
        Assert.Equal(1, sourceWarmup.Ready);
        Assert.True(sourceWarmup.ReadyToUse);
        Assert.Equal(PipelineStatus.Ready, context.PipelineCache.GetStatus(ticket));
        Assert.Equal(0, context.WarmupSources(1).Processed);
    }

    [Fact]
    public void Add_UpdatesPipelineSourceVersion()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "Versioned", 1.25f);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            context.GraphicsDevice!);
        IPipelineSource source = materials;

        uint before = source.Version;
        AddFrame(graph, materials, renderWorld);

        Assert.True(source.Version > before);
        var collector = new PipelineCollector();
        source.Collect(collector);
        Assert.True(collector.Count > 0);
    }

    [Fact]
    public void Add_KeepsSlotUploadsOwnedByMaterialVariant()
    {
        using RenderContext context = RenderContext.CreateHeadless();
        using AssetStore assets = new();
        using RenderGraph graph = new();
        RenderWorld renderWorld = new();
        IDevice device = context.GraphicsDevice!;
        IQueue queue = context.GraphicsQueue!;
        Handle<Shader> shader = StoreShader(assets);
        Handle<Material> material = StoreMaterial(assets, shader, "Variant", 1.25f, includeCached: true);
        AddRenderInstance(renderWorld.World, sourceIndex: 0, material);
        using MaterialItems materials = new(
            context,
            assets,
            device);

        string[] firstUncached = ExecuteFrame(graph, materials, renderWorld, useCache: false, device, queue);
        string[] firstCached = ExecuteFrame(graph, materials, renderWorld, useCache: true, device, queue);
        string[] secondUncached = ExecuteFrame(graph, materials, renderWorld, useCache: false, device, queue);
        string[] secondCached = ExecuteFrame(graph, materials, renderWorld, useCache: true, device, queue);

        Assert.Contains("SlotBuffer Upload", firstUncached);
        Assert.Contains("SlotBuffer Upload", firstCached);
        Assert.DoesNotContain("SlotBuffer Upload", secondUncached);
        Assert.DoesNotContain("SlotBuffer Upload", secondCached);
    }

    private static void AddFrame(RenderGraph graph, MaterialItems materials, RenderWorld renderWorld)
    {
        graph.BeginFrame();
        materials.PrepareFrame(renderWorld, useCache: false);
        materials.RecordFrame(graph);
    }

    private static string[] ExecuteFrame(
        RenderGraph graph,
        MaterialItems materials,
        RenderWorld renderWorld,
        bool useCache,
        IDevice device,
        IQueue queue)
    {
        graph.BeginFrame();
        materials.PrepareFrame(renderWorld, useCache);
        SlotFrame slots = materials.RecordFrame(graph);
        graph.AddRasterPass(
            "Read SlotBuffer",
            builder => builder.Read(slots.Buffer, ResourceState.ShaderResource),
            _ => { });
        graph.Execute(device, queue);
        return RenderGraphTestHelpers.ExecutedPassNames(graph);
    }

    private static object Bins(MaterialItems materials, string field)
        => field switch
        {
            "_shade" => materials.Shade,
            "_sw" => materials.Sw,
            "_draw" => materials.Draw,
            "_deform" => materials.Deform,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

    private static EntityId AddRenderInstance(
        World world,
        int sourceIndex,
        Handle<Material> material)
    {
        EntityId source = world.CreateEntity();
        EntityId entity = world.CreateEntity();
        world.Add(entity, new RenderSourceEntity { SourceEntity = source });
        world.Add(entity, new RenderMaterials { Materials = new[] { material } });
        InstanceMarks.Write(
            world,
            entity,
            new RenderInstance
            {
                SourceEntity = source,
                InstanceIndex = sourceIndex,
                Transform = GpuTransform.FromQvvs(TransformQvvs.Identity),
                PrevTransform = GpuTransform.FromQvvs(TransformQvvs.Identity),
                Mesh = new Handle<Mesh>(sourceIndex + 1, 1),
                BoundsExpansion = 0.5f,
            },
            InstanceDirtyFlags.All);
        return entity;
    }

    private static Handle<Shader> StoreShader(AssetStore assets)
        => StoreShader(assets, new byte[] { 1, 2, 3, 4 });

    private static Handle<Shader> StoreEmptyShader(AssetStore assets)
        => StoreShader(assets, []);

    private static Handle<Shader> StoreShader(AssetStore assets, byte[] bytecode)
    {
        AssetGuid guid = AssetGuid.New();
        ShaderAsset asset = new()
        {
            AssetGuid = guid.ToFlatString(),
            Name = "cluster_material_test",
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "dxil",
                    Stage = AssetShaderStage.Compute,
                    EntryPoint = "Main",
                    Data = bytecode,
                    ContentHash = "cluster-material-test-main",
                },
            ],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "dxil",
                    EntryPoint = "Main",
                    Stage = AssetShaderStage.Compute,
                    Reflection = new ShaderReflectionData { Resources = [] },
                },
            ],
            Reflections = [],
            Metadata = new ShaderMetadata
            {
                Tags = [],
                MaterialBindings = [],
                MaterialScalarLayouts = [],
            },
        };
        return assets.Add(guid, RuntimeAssetLoader.LoadShader(asset));
    }

    private static Handle<Material> StoreMaterial(
        AssetStore assets,
        Handle<Shader> shader,
        string name,
        float bounds,
        bool includeCached = false)
    {
        AssetGuid guid = AssetGuid.New();
        var material = new Material { Name = name };
        var passes = new List<MaterialPass>
        {
            new MaterialPass(
                "cluster.raster.sw",
                shader,
                "Main",
                MaterialState.Default with { BoundsExpansion = bounds }),
        };
        if (includeCached)
        {
            passes.Add(
                new MaterialPass(
                    "cluster.raster.sw.cached",
                    shader,
                    "Main",
                    MaterialState.Default with { BoundsExpansion = bounds }));
            passes.Add(
                new MaterialPass(
                    "cluster.deform.eval",
                    shader,
                    "Main",
                    MaterialState.Default with { BoundsExpansion = bounds }));
        }

        material.SetPasses(passes.ToArray());
        return assets.Add(guid, material);
    }

    private static uint HeaderU32(InstanceHeaderData data, int index, int offset)
    {
        byte[] header = new byte[InstanceHeaderLayout.StrideBytes];
        data.Write(index, header);
        return InstanceHeaderLayout.ReadU32(header, offset);
    }

    private static float HeaderFloat(InstanceHeaderData data, int index, int offset)
    {
        byte[] header = new byte[InstanceHeaderLayout.StrideBytes];
        data.Write(index, header);
        return InstanceHeaderLayout.ReadFloat32(header, offset);
    }

    private static void ClearDirty(World world, EntityId entity)
    {
        if (world.Has<InstanceDirty>(entity))
            world.Remove<InstanceDirty>(entity);
    }
}
