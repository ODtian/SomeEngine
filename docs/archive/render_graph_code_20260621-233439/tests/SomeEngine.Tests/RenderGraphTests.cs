using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using ReflectionFlags = System.Reflection.BindingFlags;
using System.Threading.Tasks;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests;

public class RenderGraphTests
{
    [Fact]
    public void Context_DoesNotExposeDevice()
    {
        Assert.Null(typeof(RenderGraphContext).GetProperty("Device"));
        Assert.Null(typeof(RenderGraphContext).GetMethod("GetBindingSet", ReflectionFlags.Instance | ReflectionFlags.Public));
        Assert.Empty(typeof(RenderGraphContext).GetConstructors(ReflectionFlags.Instance | ReflectionFlags.Public));
        Assert.Empty(typeof(RenderPassCommands).GetConstructors(ReflectionFlags.Instance | ReflectionFlags.Public));
    }

    [Fact]
    public void SetFinalState_IsNotPublicAuthoringSurface()
    {
        Assert.Null(typeof(RenderGraph).GetMethod("SetFinalState", ReflectionFlags.Instance | ReflectionFlags.Public));
        Assert.NotNull(typeof(RenderGraph).GetMethod("ExtractTexture", ReflectionFlags.Instance | ReflectionFlags.Public));
        Assert.NotNull(typeof(RenderGraph).GetMethod("ExtractBuffer", ReflectionFlags.Instance | ReflectionFlags.Public));
    }

    [Fact]
    public void Builder_UseAliasesAccess()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("UseAliasBuffer", RawUavBufferDesc());

        graph.AddRasterPass(
            "Use Alias",
            builder => builder.Write(
                buffer,
                ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(buffer, ResourceState.ShaderResource);

        graph.Compile();

        Assert.Contains("Use Alias", RenderGraphTestHelpers.ExecutedPassNames(graph));
    }

    [Fact]
    public void ClearBindSets_ReplacesOldApi()
    {
        Assert.Null(typeof(RenderGraph).GetMethod("ClearBindingCache"));
        Assert.NotNull(typeof(RenderGraph).GetMethod(nameof(RenderGraph.ClearBindSets)));
    }

    [Fact]
    public void Compile_AugmentsGraphCreatedTextureBindFlagsForDeclaredStates()
    {
        using var graph = new RenderGraph();
        var desc = RgbaDesc(bindFlags: BindFlags.None);

        var source = graph.CreateTexture("CopySource", desc);
        var target = graph.CreateTexture("CopyTarget", desc);

        WriteTexture(graph, source, "InitializeSource", ResourceState.RenderTarget);
        graph.AddRasterPass(
            "Copy",
            builder =>
            {
                builder.Read(source, ResourceState.CopySource);
                builder.Write(target, ResourceState.CopyDestination);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.CopyDestination);

        graph.Compile();

        Assert.True((graph.GetTextureDesc(source).BindFlags & BindFlags.CopySource) != 0);
        Assert.True((graph.GetTextureDesc(target).BindFlags & BindFlags.CopyDestination) != 0);
    }

    [Fact]
    public void Compile_AugmentsGraphCreatedBufferBindFlagsForDeclaredStates()
    {
        using var graph = new RenderGraph();
        var source = graph.CreateBuffer(
            "Source",
            new BufferDesc
            {
                SizeInBytes = 64,
                BindFlags = BindFlags.None,
                Raw = true,
            });
        var temp = graph.CreateBuffer("Temp", RawUavBufferDesc());
        var target = graph.CreateBuffer("Target", RawUavBufferDesc());

        graph.AddRasterPass(
            "InitializeSource",
            builder => builder.Write(source, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "ReadShaderResource",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(temp, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.AddRasterPass(
            "ReadIndirect",
            builder =>
            {
                builder.Read(source, ResourceState.IndirectArgument);
                builder.Read(temp, ResourceState.ShaderResource);
                builder.Write(target, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.UnorderedAccess);

        graph.Compile();

        var flags = graph.GetBufferDesc(source).BindFlags;
        Assert.True((flags & BindFlags.ShaderResource) != 0);
        Assert.True((flags & BindFlags.IndirectArgument) != 0);
    }

    [Fact]
    public void AddPassRollbackRestoresResourceBindFlagsWhenSetupFails()
    {
        using var graph = new RenderGraph();
        var texture = graph.CreateTexture("Texture", RgbaDesc(BindFlags.None));
        var buffer = graph.CreateBuffer("Buffer", RawUavBufferDesc() with { BindFlags = BindFlags.None });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "Failing Setup",
            builder =>
            {
                builder.Read(texture, ResourceState.CopySource);
                builder.Write(buffer, ResourceState.CopyDestination);
                throw new InvalidOperationException("setup failed");
            },
            _ => { }));

        Assert.Contains("setup failed", ex.Message);
        Assert.Equal(BindFlags.None, graph.GetTextureDesc(texture).BindFlags);
        Assert.Equal(BindFlags.None, graph.GetBufferDesc(buffer).BindFlags);
    }

    [Fact]
    public void Compile_KeepsExactBufferReadStatesAcrossPasses()
    {
        using var graph = new RenderGraph();
        var source = graph.CreateBuffer(
            "Source",
            new BufferDesc
            {
                SizeInBytes = 64,
                BindFlags = BindFlags.None,
                Raw = true,
            });

        graph.AddRasterPass(
            "InitializeSource",
            builder => builder.Write(source, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "ReadShaderResource",
            builder =>
            {
                builder.SideEffect();
                builder.Read(source, ResourceState.ShaderResource);
            },
            _ => { });
        graph.AddRasterPass(
            "ReadIndirect",
            builder =>
            {
                builder.SideEffect();
                builder.Read(source, ResourceState.IndirectArgument);
            },
            _ => { });

        graph.Compile();

        Assert.Equal(1, RenderGraphTestHelpers.RequiredTransitionCount(graph, "ReadShaderResource"));
        Assert.Equal(1, RenderGraphTestHelpers.RequiredTransitionCount(graph, "ReadIndirect"));
        var flags = graph.GetBufferDesc(source).BindFlags;
        Assert.True((flags & BindFlags.ShaderResource) != 0);
        Assert.True((flags & BindFlags.IndirectArgument) != 0);
    }

    [Fact]
    public void Builder_UsesReflectedBindingRules()
    {
        using var graph = new RenderGraph();
        var constants = graph.CreateBuffer(
            "ReflectedConstants",
            RawUavBufferDesc(16) with { BindFlags = BindFlags.None },
            [0, 0, 0, 0]);
        var readOnly = graph.CreateBuffer(
            "ReflectedRead",
            RawUavBufferDesc(16) with { BindFlags = BindFlags.None },
            [1, 2, 3, 4]);
        var readWrite = graph.CreateBuffer(
            "ReflectedWrite",
            RawUavBufferDesc(16) with { BindFlags = BindFlags.None });

        var constantBinding = new ReflectedBinding(
            "Constants",
            Set: 0,
            Binding: 0,
            BindingType.ConstantBuffer,
            ShaderStageFlags.Compute);
        var readBinding = new ReflectedBinding(
            "ReadOnly",
            Set: 0,
            Binding: 1,
            BindingType.StorageBufferRead,
            ShaderStageFlags.Compute);
        var writeBinding = new ReflectedBinding(
            "ReadWrite",
            Set: 0,
            Binding: 2,
            BindingType.StorageBufferReadWrite,
            ShaderStageFlags.Compute);

        graph.AddRasterPass(
            "Initialize ReflectedWrite",
            builder => builder.Write(readWrite, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Reflected Bindings",
            builder =>
            {
                builder.SideEffect();
                builder.Use(constantBinding, constants);
                builder.Use(readBinding, readOnly);
                builder.Use(writeBinding, readWrite);
            },
            _ => { });

        graph.Compile();

        Assert.True((graph.GetBufferDesc(constants).BindFlags & BindFlags.ConstantBuffer) != 0);
        Assert.True((graph.GetBufferDesc(readOnly).BindFlags & BindFlags.ShaderResource) != 0);
        Assert.True((graph.GetBufferDesc(readWrite).BindFlags & BindFlags.UnorderedAccess) != 0);
        AssertRegisteredPassNames(graph, "Initialize ReflectedWrite", "Reflected Bindings");
    }

    [Fact]
    public void Builder_ReflectedReadWriteRejectsUninitializedTransient()
    {
        using var graph = new RenderGraph();
        var readWrite = graph.CreateBuffer(
            "ReflectedWrite",
            RawUavBufferDesc(16) with { BindFlags = BindFlags.None });
        var writeBinding = new ReflectedBinding(
            "ReadWrite",
            Set: 0,
            Binding: 0,
            BindingType.StorageBufferReadWrite,
            ShaderStageFlags.Compute);

        graph.AddRasterPass(
            "Reflected ReadWrite",
            builder =>
            {
                builder.SideEffect();
                builder.Use(writeBinding, readWrite);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Compile());

        Assert.Contains("Reflected ReadWrite", ex.Message);
        Assert.Contains("ReflectedWrite", ex.Message);
        Assert.Contains("before any live pass initializes", ex.Message);
    }

    [Fact]
    public void Builder_RejectsSamplerAsResourceUse()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("SamplerUseTarget", RawUavBufferDesc());
        var sampler = new ReflectedBinding(
            "LinearSampler",
            Set: 0,
            Binding: 0,
            BindingType.Sampler,
            ShaderStageFlags.Compute);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            graph.AddRasterPass(
                "Invalid Sampler Use",
                builder => builder.Use(sampler, buffer),
                _ => { });
        });

        Assert.Contains("does not declare a RenderGraph resource use", ex.Message);
    }

    [Fact]
    public void Compile_AcceptsPerMipReadWriteDependencies()
    {
        using var graph = new RenderGraph();
        var hiz = graph.CreateTexture("HiZ", MipUavDesc());
        var depth = graph.CreateTexture("Depth", DepthDesc());

        WriteTexture(graph, depth, "Depth Write", ResourceState.DepthWrite);
        graph.AddRasterPass(
            "HiZ Mip0",
            builder =>
            {
                builder.Read(depth, ResourceState.ShaderResource);
                builder.Write(hiz, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
            },
            _ => { });
        graph.AddRasterPass(
            "HiZ Downsample Mip1",
            builder =>
            {
                builder.Read(hiz, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
                builder.Write(hiz, ResourceState.UnorderedAccess, SubResourceRange.Mip(1));
            },
            _ => { });
        graph.AddRasterPass(
            "HiZ Downsample Mip2",
            builder =>
            {
                builder.Read(hiz, ResourceState.UnorderedAccess, SubResourceRange.Mip(1));
                builder.Write(hiz, ResourceState.UnorderedAccess, SubResourceRange.Mip(2));
            },
            _ => { });
        graph.SetFinalState(hiz, ResourceState.UnorderedAccess);

        graph.Compile();

        AssertRegisteredPassNames(
            graph,
            "Depth Write",
            "HiZ Mip0",
            "HiZ Downsample Mip1",
            "HiZ Downsample Mip2");
    }

    [Fact]
    public void Compile_KeepsDepthClearBeforeDepthTestedDrawAndHiZRead()
    {
        using var graph = new RenderGraph();
        var depth = graph.CreateTexture("SceneDepth", DepthDesc());
        var hiz = graph.CreateTexture("HiZ", MipUavDesc());

        graph.AddRasterPass(
            "Clear Main Depth",
            builder => builder.Write(depth, ResourceState.DepthWrite),
            _ => { });
        graph.AddRasterPass(
            "Cluster Draw",
            builder => builder.ReadWrite(depth, ResourceState.DepthWrite),
            _ => { });
        graph.AddRasterPass(
            "HiZ Mip0",
            builder =>
            {
                builder.Read(depth, ResourceState.ShaderResource);
                builder.Write(hiz, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.SetFinalState(hiz, ResourceState.UnorderedAccess);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Clear Main Depth", "Cluster Draw", "HiZ Mip0");
    }

    [Fact]
    public void Compile_AcceptsWholeResourceStateTransitions()
    {
        using var graph = new RenderGraph();
        var desc = RgbaDesc(BindFlags.ShaderResource | BindFlags.RenderTarget);
        var source = graph.CreateTexture("SrcTex", desc);
        var target = graph.CreateTexture("DstTex", desc);

        WriteTexture(graph, source, "Write", ResourceState.RenderTarget);
        graph.AddRasterPass(
            "ReadAndWrite",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(target, ResourceState.RenderTarget);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Write", "ReadAndWrite");
    }

    [Fact]
    public void Compile_CullsPassesThatDoNotReachRoots()
    {
        using var graph = new RenderGraph();
        var unused = graph.CreateTexture("Unused", RgbaDesc(BindFlags.RenderTarget));
        var source = graph.CreateTexture("Source", RgbaDesc(BindFlags.RenderTarget | BindFlags.ShaderResource));
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        WriteTexture(graph, unused, "Unused Write", ResourceState.RenderTarget);
        WriteTexture(graph, source, "Source Write", ResourceState.RenderTarget);
        graph.AddRasterPass(
            "Target Write",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(target, ResourceState.RenderTarget);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Source Write", "Target Write");
    }

    [Fact]
    public void Compile_BuildsGraphShape()
    {
        using var graph = new RenderGraph();
        var unused = graph.CreateTexture("Unused", RgbaDesc(BindFlags.RenderTarget));
        var source = graph.CreateTexture("Source", RgbaDesc(BindFlags.RenderTarget | BindFlags.ShaderResource));
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        graph.AddRasterPass(
            "Unused Write",
            builder => builder.Write(unused, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Source Write",
            builder => builder.Write(source, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Target Write",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(target, ResourceState.RenderTarget);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Source Write", "Target Write");
        Assert.Equal(1, RenderGraphTestHelpers.DependencyCount(graph));
        Assert.Single(RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: false));
    }

    [Fact]
    public void Compile_ReusesGraphCacheWhenNamesChange()
    {
        using var graph = new RenderGraph();

        BuildFrame(graph, "Target");
        Assert.Equal(1, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, "Target");
        Assert.Equal(1, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, "OtherTarget");
        Assert.Equal(1, CacheCount(graph));

        static void BuildFrame(RenderGraph graph, string name)
        {
            var target = graph.CreateTexture(name, RgbaDesc(BindFlags.RenderTarget));
            WriteTexture(graph, target, "Target Write", ResourceState.RenderTarget);
            graph.SetFinalState(target, ResourceState.RenderTarget);
            graph.Compile();
        }
    }

    [Fact]
    public void Compile_ReusesGraphCacheForAlternatingSchemas()
    {
        using var graph = new RenderGraph();

        BuildFrame(graph, size: 32);
        Assert.Equal(1, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, size: 64);
        Assert.Equal(2, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, size: 32);
        Assert.Equal(2, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, size: 64);
        Assert.Equal(2, CacheCount(graph));

        static void BuildFrame(RenderGraph graph, uint size)
        {
            var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget, size));
            WriteTexture(graph, target, "Target Write", ResourceState.RenderTarget);
            graph.SetFinalState(target, ResourceState.RenderTarget);
            graph.Compile();
        }
    }

    [Fact]
    public void Compile_CacheHitMaterializesSharedGraph()
    {
        using var graph = new RenderGraph();

        BuildFrame(graph);
        CompileGraph(graph);

        graph.BeginFrame();
        BuildFrame(graph);
        object firstHit = CompileGraph(graph);

        graph.BeginFrame();
        BuildFrame(graph);
        object secondHit = CompileGraph(graph);

        Assert.Equal(1, CacheCount(graph));
        Assert.Same(Field(firstHit, "Uses"), Field(secondHit, "Uses"));
        Assert.Same(Field(firstHit, "PassSlots"), Field(secondHit, "PassSlots"));
        Assert.NotSame(Field(firstHit, "Transitions"), Field(secondHit, "Transitions"));
        Assert.NotSame(Field(firstHit, "FinalTransitions"), Field(secondHit, "FinalTransitions"));
        Assert.NotSame(Field(firstHit, "Resources"), Field(secondHit, "Resources"));

        static void BuildFrame(RenderGraph graph)
        {
            var target = graph.CreateTexture("CachedTarget", RgbaDesc(BindFlags.RenderTarget));
            graph.AddRasterPass(
                "Cached Write",
                builder => builder.Write(target, ResourceState.RenderTarget),
                _ => { });
            graph.SetFinalState(target, ResourceState.RenderTarget);
        }

        static object CompileGraph(RenderGraph graph)
        {
            var method = typeof(RenderGraph).GetMethod(
                "CompileGraph",
                ReflectionFlags.NonPublic | ReflectionFlags.Instance);
            return method!.Invoke(graph, [false, false])!;
        }

        static object Field(object target, string name)
            => target.GetType()
                .GetField(name, ReflectionFlags.Public | ReflectionFlags.Instance)!
                .GetValue(target)!;
    }

    [Fact]
    public void Compile_TracksGraphCacheEvictions()
    {
        using var graph = new RenderGraph();

        for (int i = 0; i < 65; i++)
        {
            if (i != 0)
                graph.BeginFrame();
            BuildFrame(graph, checked((uint)(32 + i)));
        }

        Assert.Equal(64, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, size: 33);

        Assert.Equal(64, CacheCount(graph));

        static void BuildFrame(RenderGraph graph, uint size)
        {
            var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget, size));
            WriteTexture(graph, target, "Target Write", ResourceState.RenderTarget);
            graph.SetFinalState(target, ResourceState.RenderTarget);
            graph.Compile();
        }
    }

    [Fact]
    public void Compile_ClearsGraphCacheWhenFeaturesChange()
    {
        using var graph = new RenderGraph();

        BuildFrame(graph, "Target");
        Assert.Equal(1, CacheCount(graph));

        graph.AddFeature(new TestFeature("Dirty Feature"));

        Assert.Equal(0, CacheCount(graph));

        graph.BeginFrame();
        BuildFrame(graph, "Target");
        Assert.Equal(1, CacheCount(graph));

        static void BuildFrame(RenderGraph graph, string name)
        {
            var target = graph.CreateTexture(name, RgbaDesc(BindFlags.RenderTarget));
            graph.AddRasterPass(
                "Target Write",
                builder => builder.Write(target, ResourceState.RenderTarget),
                _ => { });
            graph.SetFinalState(target, ResourceState.RenderTarget);
            graph.Compile();
        }
    }

    [Fact]
    public void PassParameters_CopyResources()
    {
        var layout = new BindingLayoutHandle(7, 1);
        var resources = new[]
        {
            BindingResourceDesc.Buffer(0, new BufferViewHandle(1, 1)),
        };

        var parameters = new PassParameters(layout, resources);
        resources[0] = BindingResourceDesc.Buffer(1, new BufferViewHandle(2, 1));

        Assert.Equal(layout, parameters.Layout);
        Assert.Equal(0u, parameters.ResourceSpan[0].Binding);
        Assert.Equal(new BufferViewHandle(1, 1), parameters.ResourceSpan[0].BufferView);
        Assert.Equal(new PassParameters(layout, parameters.ResourceSpan).Hash, parameters.Hash);
    }

    [Fact]
    public void Compile_RebuildsFromCurrentRoots()
    {
        using var graph = new RenderGraph();
        var optional = graph.CreateTexture("Optional", RgbaDesc(BindFlags.RenderTarget));
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        WriteTexture(graph, optional, "Optional Write", ResourceState.RenderTarget);
        WriteTexture(graph, target, "Target Write", ResourceState.RenderTarget);
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Target Write");

        graph.SetFinalState(optional, ResourceState.RenderTarget);
        graph.Compile();

        AssertRegisteredPassNames(graph, "Optional Write", "Target Write");
    }

    [Fact]
    public void Compile_ClearsActiveCompile()
    {
        using var graph = new RenderGraph();
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        WriteTexture(graph, target, "Target Write", ResourceState.RenderTarget);
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        var field = typeof(RenderGraph).GetField(
            "_activeCompile",
            ReflectionFlags.Instance | ReflectionFlags.NonPublic);
        Assert.Null(field!.GetValue(graph));
    }

    [Fact]
    public void Compile_ReusesResultForDump()
    {
        using var graph = new RenderGraph();
        int setupCount = 0;

        graph.AddRasterPass(
            "Counted",
            builder =>
            {
                setupCount++;
                builder.SideEffect();
            },
            _ => { });

        graph.Compile();
        _ = graph.DumpText();

        Assert.Equal(1, setupCount);
    }

    [Fact]
    public void Compile_DropsResultAfterChange()
    {
        using var graph = new RenderGraph();
        int firstSetupCount = 0;
        int secondSetupCount = 0;

        graph.AddRasterPass(
            "First",
            builder =>
            {
                firstSetupCount++;
                builder.SideEffect();
            },
            _ => { });

        graph.Compile();

        graph.AddRasterPass(
            "Second",
            builder =>
            {
                secondSetupCount++;
                builder.SideEffect();
            },
            _ => { });
        _ = graph.DumpText();

        Assert.Equal(2, firstSetupCount);
        Assert.Equal(1, secondSetupCount);
    }

    [Fact]
    public void Compile_TreatsExportAsCullRoot()
    {
        using var graph = new RenderGraph();
        var exported = graph.CreateTexture("Exported", RgbaDesc(BindFlags.RenderTarget));
        var unused = graph.CreateTexture("Unused", RgbaDesc(BindFlags.RenderTarget));

        WriteTexture(graph, exported, "Write Exported", ResourceState.RenderTarget);
        WriteTexture(graph, unused, "Write Unused", ResourceState.RenderTarget);
        graph.ExtractTexture(exported, ResourceState.RenderTarget, (_, _) => { });

        graph.Compile();

        AssertRegisteredPassNames(graph, "Write Exported");
    }

    [Fact]
    public void SetFinalState_RejectsConflictingExportState()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Exported", RawUavBufferDesc());

        graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            graph.SetFinalState(buffer, ResourceState.ShaderResource));

        Assert.Contains("Exported", ex.Message);
        Assert.Contains("UnorderedAccess", ex.Message);
        Assert.Contains("ShaderResource", ex.Message);
    }

    [Fact]
    public void ExtractBuffer_RejectsConflictingFinalState()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("FinalBuffer", RawUavBufferDesc());

        graph.SetFinalState(buffer, ResourceState.ShaderResource);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { }));

        Assert.Contains("FinalBuffer", ex.Message);
        Assert.Contains("ShaderResource", ex.Message);
        Assert.Contains("UnorderedAccess", ex.Message);
    }

    [Fact]
    public void FinalStateDedup()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("SharedFinal", RawUavBufferDesc());

        graph.AddRasterPass(
            "Write SharedFinal",
            builder => builder.Write(buffer, ResourceState.CopyDestination),
            _ => { });
        graph.SetFinalState(buffer, ResourceState.ShaderResource);
        graph.ExtractBuffer(buffer, ResourceState.ShaderResource, (_, _) => { });

        graph.Compile();

        AssertRegisteredPassNames(graph, "Write SharedFinal");
        Assert.Equal(1, RenderGraphTestHelpers.FinalTransitionCount(graph));
        Assert.Equal(1, RenderGraphTestHelpers.FinalTransitionTotal(graph));
    }

    [Fact]
    public void Compile_DoesNotKeepPreviousWriterForWriteOnlySideOutput()
    {
        using var graph = new RenderGraph();
        var sideOutput = graph.CreateTexture("SideOutput", RgbaDesc(BindFlags.RenderTarget));
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        graph.AddRasterPass(
            "Previous Side Writer",
            builder => builder.Write(sideOutput, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Target Writer",
            builder =>
            {
                builder.Write(target, ResourceState.RenderTarget);
                builder.Write(sideOutput, ResourceState.RenderTarget);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Target Writer");
    }

    [Fact]
    public void Compile_DoesNotKeepPreviousWriterForWriteOnlyRootOverwrite()
    {
        using var graph = new RenderGraph();
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));

        graph.AddRasterPass(
            "Previous Target Writer",
            builder => builder.Write(target, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Final Target Writer",
            builder => builder.Write(target, ResourceState.RenderTarget),
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        string dump = graph.DumpText();

        AssertRegisteredPassNames(graph, "Final Target Writer");
        Assert.Contains(
            "Previous Target Writer - declared writes do not feed final state, extraction, side effect, or downstream dependency",
            dump);
    }

    [Fact]
    public void Compile_CullsOnlyOverwrittenTextureSubresourceWriter()
    {
        using var graph = new RenderGraph();
        var target = graph.CreateTexture("Target", MipUavDesc(mipLevels: 2));

        graph.AddRasterPass(
            "Previous Mip0 Writer",
            builder => builder.Write(target, ResourceState.UnorderedAccess, SubResourceRange.Mip(0)),
            _ => { });
        graph.AddRasterPass(
            "Mip1 Writer",
            builder => builder.Write(target, ResourceState.UnorderedAccess, SubResourceRange.Mip(1)),
            _ => { });
        graph.AddRasterPass(
            "Final Mip0 Writer",
            builder => builder.Write(target, ResourceState.UnorderedAccess, SubResourceRange.Mip(0)),
            _ => { });
        graph.SetFinalState(target, ResourceState.UnorderedAccess);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Mip1 Writer", "Final Mip0 Writer");
    }

    [Fact]
    public void Compile_CullsReadOnlyPassWithoutSideEffect()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("ReadOnlySource", RawUavBufferDesc());

        graph.AddRasterPass(
            "Read Only",
            builder => builder.Read(buffer, ResourceState.ShaderResource),
            _ => { });

        graph.Compile();

        AssertRegisteredPassNames(graph);
    }

    [Fact]
    public void Compile_CullsImportedWriteWithoutOutput()
    {
        using var graph = new RenderGraph();
        var imported = graph.ImportBuffer(
            "ImportedOnly",
            new BufferHandle(42, 1),
            RawUavBufferDesc(),
            new ImportDesc(ResourceState.Common)
            {
                AllowWrite = true,
            });

        graph.AddRasterPass(
            "Write Imported",
            builder => builder.Write(imported, ResourceState.UnorderedAccess),
            _ => { });

        graph.Compile();

        AssertRegisteredPassNames(graph);
    }

    [Fact]
    public void ImportRejectsWrite()
    {
        using var graph = new RenderGraph();
        var imported = graph.ImportBuffer(
            "ReadOnlyImport",
            new BufferHandle(42, 1),
            RawUavBufferDesc(),
            new ImportDesc(ResourceState.Common));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "Write Import",
            builder => builder.Write(imported, ResourceState.UnorderedAccess),
            _ => { }));

        Assert.Contains("Write Import", ex.Message);
        Assert.Contains("ReadOnlyImport", ex.Message);
        Assert.Contains(nameof(ImportDesc.AllowWrite), ex.Message);
    }

    [Theory]
    [InlineData(ResourceState.GenericRead, false, BindFlags.ShaderResource, nameof(ImportDesc.InitialState), "GenericRead is not valid for textures")]
    [InlineData((ResourceState)9876, false, BindFlags.ShaderResource, nameof(ImportDesc.InitialState), "not defined")]
    [InlineData(ResourceState.ShaderResource, false, BindFlags.RenderTarget, nameof(ImportDesc.InitialState), "requires bind flag ShaderResource")]
    [InlineData(ResourceState.RenderTarget, false, BindFlags.ShaderResource, nameof(ImportDesc.InitialState), "requires bind flag RenderTarget")]
    [InlineData(ResourceState.GenericRead, true, BindFlags.ShaderResource, nameof(ImportDesc.FinalState), "GenericRead is not valid for textures")]
    [InlineData((ResourceState)9876, true, BindFlags.ShaderResource, nameof(ImportDesc.FinalState), "not defined")]
    [InlineData(ResourceState.ShaderResource, true, BindFlags.RenderTarget, nameof(ImportDesc.FinalState), "requires bind flag ShaderResource")]
    [InlineData(ResourceState.RenderTarget, true, BindFlags.ShaderResource, nameof(ImportDesc.FinalState), "requires bind flag RenderTarget")]
    public void ImportTexture_RejectsInvalidBoundaryState(
        ResourceState invalidState,
        bool finalState,
        BindFlags bindFlags,
        string propertyName,
        string message)
    {
        using var graph = new RenderGraph();
        var desc = RgbaDesc(bindFlags);

        var ex = Assert.Throws<ArgumentException>(() => graph.ImportTexture(
            finalState ? "InvalidFinalTexture" : "InvalidInitialTexture",
            new TextureHandle(42, 1),
            desc,
            finalState
                ? new ImportDesc(ResourceState.Common)
                {
                    FinalState = invalidState,
                }
                : new ImportDesc(invalidState)));

        Assert.Contains(propertyName, ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportBuffer_AcceptsGenericReadBoundaryStateWithReadBindFlag(bool finalState)
    {
        using var graph = new RenderGraph();
        var desc = RawUavBufferDesc() with { BindFlags = BindFlags.ShaderResource };

        var imported = graph.ImportBuffer(
            finalState ? "GenericReadFinalBuffer" : "GenericReadInitialBuffer",
            new BufferHandle(42, 1),
            desc,
            finalState
                ? new ImportDesc(ResourceState.ShaderResource)
                {
                    FinalState = ResourceState.GenericRead,
                }
                : new ImportDesc(ResourceState.GenericRead));

        Assert.True(imported.IsValid);
    }

    [Theory]
    [InlineData((ResourceState)9876, false, nameof(ImportDesc.InitialState), "not defined")]
    [InlineData(ResourceState.GenericRead, false, nameof(ImportDesc.InitialState), "requires at least one read bind flag")]
    [InlineData(ResourceState.RenderTarget, false, nameof(ImportDesc.InitialState), "not valid for buffers")]
    [InlineData(ResourceState.ShaderResource, false, nameof(ImportDesc.InitialState), "requires bind flag ShaderResource")]
    [InlineData((ResourceState)9876, true, nameof(ImportDesc.FinalState), "not defined")]
    [InlineData(ResourceState.GenericRead, true, nameof(ImportDesc.FinalState), "requires at least one read bind flag")]
    [InlineData(ResourceState.RenderTarget, true, nameof(ImportDesc.FinalState), "not valid for buffers")]
    [InlineData(ResourceState.ShaderResource, true, nameof(ImportDesc.FinalState), "requires bind flag ShaderResource")]
    public void ImportBuffer_RejectsInvalidBoundaryState(
        ResourceState invalidState,
        bool finalState,
        string propertyName,
        string message)
    {
        using var graph = new RenderGraph();
        var desc = RawUavBufferDesc();

        var ex = Assert.Throws<ArgumentException>(() => graph.ImportBuffer(
            finalState ? "InvalidFinalBuffer" : "InvalidInitialBuffer",
            new BufferHandle(42, 1),
            desc,
            finalState
                ? new ImportDesc(ResourceState.UnorderedAccess)
                {
                    FinalState = invalidState,
                }
                : new ImportDesc(invalidState)));

        Assert.Contains(propertyName, ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Compile_KeepsSideEffectPass()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("SideEffectSource", RawUavBufferDesc(), [0]);

        graph.AddRasterPass(
            "Side Effect",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });

        graph.Compile();

        AssertRegisteredPassNames(graph, "Side Effect");
    }

    [Fact]
    public void Compile_RejectsLiveReadBeforeTransientWrite()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Uninitialized", RawUavBufferDesc());

        graph.AddRasterPass(
            "Read Uninitialized",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Compile());

        Assert.Contains("Read Uninitialized", ex.Message);
        Assert.Contains("Uninitialized", ex.Message);
        Assert.Contains("before any live pass initializes", ex.Message);
    }

    [Fact]
    public void Compile_RejectsReadBeforeWriteEvenWithNonUndefinedBufferInitialState()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer(
            "UninitializedWithState",
            new BufferDesc
            {
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            });

        graph.AddRasterPass(
            "Read Uninitialized With State",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Compile());

        Assert.Contains("Read Uninitialized With State", ex.Message);
        Assert.Contains("UninitializedWithState", ex.Message);
        Assert.Contains("before any live pass initializes", ex.Message);
    }

    [Fact]
    public void Compile_RejectsReadBeforeWriteEvenWithNonUndefinedTextureInitialState()
    {
        using var graph = new RenderGraph();
        var texture = graph.CreateTexture(
            "UninitializedTextureWithState",
            RgbaDesc(BindFlags.ShaderResource) with { InitialState = ResourceState.ShaderResource });

        graph.AddRasterPass(
            "Read Texture With State",
            builder =>
            {
                builder.SideEffect();
                builder.Read(texture, ResourceState.ShaderResource);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Compile());

        Assert.Contains("Read Texture With State", ex.Message);
        Assert.Contains("UninitializedTextureWithState", ex.Message);
        Assert.Contains("before any live pass initializes", ex.Message);
    }

    [Fact]
    public void Compile_DedupsRepeatedReads()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Shared", RawUavBufferDesc());

        graph.AddRasterPass(
            "Write",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Read Twice",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });
        graph.ExtractBuffer(buffer, ResourceState.ShaderResource, (_, _) => { });

        graph.Compile();

        Assert.Equal(1, RenderGraphTestHelpers.DependencyCount(graph));
        Assert.Equal(0, RenderGraphTestHelpers.ResolveCount(graph, "Read Twice"));
    }

    [Fact]
    public void DumpText_ReportsCompiledGraphLifetimes()
    {
        using var graph = new RenderGraph();
        var unused = graph.CreateTexture("Unused", RgbaDesc(BindFlags.RenderTarget));
        var source = graph.CreateTexture("Source", RgbaDesc(BindFlags.RenderTarget | BindFlags.ShaderResource));
        var target = graph.CreateTexture("Target", RgbaDesc(BindFlags.RenderTarget));
        var readback = graph.CreateBuffer("Readback", RawUavBufferDesc(), [0]);

        graph.AddRasterPass(
            "Unused Write",
            builder => builder.Write(unused, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Source Write",
            builder => builder.Write(source, ResourceState.RenderTarget),
            _ => { });
        graph.AddRasterPass(
            "Target Write",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(target, ResourceState.RenderTarget);
            },
            _ => { });
        graph.AddRasterPass(
            "Readback Marker",
            builder =>
            {
                builder.SideEffect();
                builder.Read(readback, ResourceState.ShaderResource);
            },
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        string dump = graph.DumpText();

        Assert.Contains("Passes: 3", dump);
        Assert.Contains("Dependencies: 1", dump);
        Assert.Contains("0: Source Write", dump);
        Assert.Contains("1: Target Write", dump);
        Assert.Contains("2: Readback Marker", dump);
        Assert.Contains("SideEffect", dump);
        Assert.Contains("Keep: writes needed resource 'Source'", dump);
        Assert.Contains("Keep: writes needed resource 'Target'", dump);
        Assert.Contains("Keep: side effect", dump);
        Assert.Contains(
            "Unused Write - declared writes do not feed final state, extraction, side effect, or downstream dependency",
            dump);
        Assert.Contains("0:Source Write -> 1:Target Write: Source v1 RAW", dump);
        Assert.Contains("WriteOnly: Source v1 RenderTarget all", dump);
        Assert.Contains("ReadOnly: Source v1 ShaderResource all <- 0:Source Write", dump);
        Assert.Contains("WriteOnly: Target v1 RenderTarget all", dump);
        Assert.Contains("ReadOnly: Readback v0 ShaderResource all", dump);
        Assert.Contains("Source Texture Transient Live [0..1] uses=2 v1", dump);
        Assert.Contains("Target Texture Transient Live [1..1] uses=1 v1 FinalState", dump);
        Assert.Contains("Readback Buffer Transient Live [2..2] uses=1 v0", dump);
        Assert.Contains("Unused Texture Transient Culled [root] uses=0 v0", dump);
    }

    [Fact]
    public void DumpText_ReportsReadWriteVersions()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Counter", RawUavBufferDesc());

        graph.AddRasterPass(
            "Initialize",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Increment",
            builder => builder.ReadWrite(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { });

        string dump = graph.DumpText();

        Assert.Contains("Dependencies: 1", dump);
        Assert.Contains("0:Initialize -> 1:Increment: Counter v1->v2 RAW|WAW", dump);
        Assert.Contains("WriteOnly: Counter v1 UnorderedAccess all", dump);
        Assert.Contains("ReadWrite: Counter v1->v2 UnorderedAccess all <- 0:Initialize", dump);
        Assert.Contains("Counter Buffer Transient Live [0..1] uses=2 v2 Exported", dump);
    }

    [Fact]
    public void DumpText_ReportsTransitions()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Counter", RawUavBufferDesc());
        var hiZ = graph.CreateTexture("HiZ", MipUavDesc());

        graph.AddRasterPass(
            "Initialize",
            builder =>
            {
                builder.Write(buffer, ResourceState.UnorderedAccess);
                builder.Write(hiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
            },
            _ => { });
        graph.AddRasterPass(
            "Use Counter",
            builder =>
            {
                builder.ReadWrite(buffer, ResourceState.UnorderedAccess);
                builder.Read(hiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
                builder.Write(hiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(1));
            },
            _ => { });
        graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { });

        string dump = graph.DumpText();

        Assert.Contains("Required Transitions", dump);
        Assert.Contains("Counter: Undefined -> UnorderedAccess all", dump);
        Assert.Contains("Counter: UAV dependency all", dump);
        Assert.Contains("HiZ: Undefined -> UnorderedAccess mip=0 slice=0", dump);
        Assert.Contains("HiZ: UAV dependency mip=0 slice=0", dump);
        Assert.Contains("HiZ: Undefined -> UnorderedAccess mip=1 slice=0", dump);
    }

    [Fact]
    public void Compile_BuildsTransitions()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Counter", RawUavBufferDesc());

        graph.AddRasterPass(
            "Initialize",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Increment",
            builder => builder.ReadWrite(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(buffer, ResourceState.ShaderResource);

        graph.Compile();

        Assert.Equal(1, RenderGraphTestHelpers.RequiredTransitionCount(graph, "Initialize"));
        Assert.Equal(1, RenderGraphTestHelpers.RequiredTransitionCount(graph, "Increment"));
        Assert.Equal(1, RenderGraphTestHelpers.FinalTransitionCount(graph));
    }

    [Fact]
    public void Compile_CompressesBufferPassEpilogueToFinalState()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("EpilogueBuffer", RawUavBufferDesc());

        graph.AddRasterPass(
            "Buffer Epilogue",
            builder =>
            {
                builder.Write(buffer, ResourceState.UnorderedAccess);
                builder.ReadWrite(buffer, ResourceState.UnorderedAccess);
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });

        graph.Compile();

        Assert.Equal(1, RenderGraphTestHelpers.PassEpilogueCount(graph, "Buffer Epilogue"));
    }

    [Fact]
    public void SetFinalState_KeepsResourceLiveWithoutPresentingIt()
    {
        using var graph = new RenderGraph();
        var target = graph.CreateTexture(
            "RootTarget",
            RgbaDesc(BindFlags.RenderTarget) with { InitialState = ResourceState.Undefined });

        graph.AddRasterPass(
            "Write Root",
            builder => builder.Write(target, ResourceState.RenderTarget),
            _ => { });
        graph.SetFinalState(target, ResourceState.RenderTarget);

        graph.Compile();

        AssertRegisteredPassNames(graph, "Write Root");
        Assert.Equal(0, RenderGraphTestHelpers.RootResolveCount(graph));
        Assert.Equal(0, RenderGraphTestHelpers.FinalTransitionCount(graph));
    }

    [Fact]
    public void Compile_BuildsRootResolveForImportedFinalizationWithoutProducer()
    {
        using var graph = new RenderGraph();
        var imported = graph.ImportBuffer(
            "RootResolveImported",
            new BufferHandle(42, 1),
            new BufferDesc
            {
                Name = "RootResolveImported",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
                Raw = true,
            },
            new ImportDesc(ResourceState.CopyDestination));

        graph.SetFinalState(imported, ResourceState.ShaderResource);
        graph.Compile();

        Assert.Equal(1, RenderGraphTestHelpers.RootResolveCount(graph));
        Assert.Equal(1, RenderGraphTestHelpers.FinalTransitionCount(graph));
    }

    [Fact]
    public void Compile_BuildsResourceResolves()
    {
        using var graph = new RenderGraph();
        var future = graph.CreateBuffer("Future", RawUavBufferDesc());

        graph.AddRasterPass(
            "Observe Before Use",
            builder => builder.SideEffect(),
            _ => { });
        graph.AddRasterPass(
            "Use Future",
            builder => builder.Write(future, ResourceState.UnorderedAccess),
            _ => { });
        graph.ExtractBuffer(future, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(0, RenderGraphTestHelpers.ResolveCount(graph, "Observe Before Use"));
        Assert.Equal(1, RenderGraphTestHelpers.ResolveCount(graph, "Use Future"));
        Assert.Equal(0, RenderGraphTestHelpers.RootResolveCount(graph));
    }

    [Fact]
    public void BufferCopyBatch_DedupesRepeatedHandleDeclarations()
    {
        using var graph = new RenderGraph();
        var source = graph.CreateBuffer(
            "CopyBatchSource",
            new BufferDesc
            {
                Name = "CopyBatchSource",
                SizeInBytes = 64,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var destinationA = graph.CreateBuffer(
            "CopyBatchDestinationA",
            new BufferDesc
            {
                Name = "CopyBatchDestinationA",
                SizeInBytes = 64,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var destinationB = graph.CreateBuffer(
            "CopyBatchDestinationB",
            new BufferDesc
            {
                Name = "CopyBatchDestinationB",
                SizeInBytes = 64,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        BufferCopyPasses.AddCopyBatch(
            graph,
            "Copy Batch",
            [
                new BufferCopyRequest(source, destinationA, 0, 0, 16),
                new BufferCopyRequest(source, destinationA, 16, 16, 16),
                new BufferCopyRequest(source, destinationB, 32, 0, 16),
            ]);
        graph.ExtractBuffer(destinationA, ResourceState.CopyDestination, (_, _) => { });
        graph.ExtractBuffer(destinationB, ResourceState.CopyDestination, (_, _) => { });

        graph.Compile();

        Assert.Equal(3, UseCount(graph, "Copy Batch"));
    }

    [Fact]
    public void Compile_BuildsQueueBatches()
    {
        using var graph = new RenderGraph();
        var a = graph.CreateBuffer("A", RawUavBufferDesc());
        var b = graph.CreateBuffer("B", RawUavBufferDesc());
        var c = graph.CreateBuffer("C", RawUavBufferDesc());
        var d = graph.CreateBuffer("D", RawUavBufferDesc());

        graph.AddComputePass(
            "Compute A",
            builder => builder.Write(a, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddComputePass(
            "Compute B",
            builder =>
            {
                builder.Read(a, ResourceState.ShaderResource);
                builder.Write(b, ResourceState.UnorderedAccess);
            },
            (_, _) => { });
        graph.AddRasterPass(
            "Command C",
            builder =>
            {
                builder.Read(b, ResourceState.ShaderResource);
                builder.Write(c, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.AddComputePass(
            "Compute D",
            builder =>
            {
                builder.Read(c, ResourceState.ShaderResource);
                builder.Write(d, ResourceState.UnorderedAccess);
            },
            (_, _) => { });
        graph.ExtractBuffer(d, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        AssertRegisteredPassNames(graph, "Compute A", "Compute B", "Command C", "Compute D");
        Assert.Equal(
            ["Mixed:Graphics:0:4"],
            RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Equal(
            ["Compute:Compute:0:2", "Command:Graphics:2:1", "Compute:Compute:3:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true));
        Assert.Equal(
            ["0:1:Compute->Graphics", "1:2:Graphics->Compute"],
            RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));

        string dump = graph.DumpText();
        Assert.Contains("QueueBatches: 1", dump);
        Assert.Contains("QueueLinks: 0", dump);
        Assert.Contains("FrameQueue: Graphics", dump);
        Assert.Contains("FrameWaits: None", dump);
        Assert.Contains("0: Mixed queue=Graphics inputLinks=0 outputLinks=0 waitQueues=None signalQueues=None slots=0..3 passes=Compute A, Compute B, Command C, Compute D", dump);
        Assert.Contains("Queue Links", dump);
        Assert.Contains("  <none>", dump);

        string asyncDump = graph.DumpText(asyncCompute: true, asyncCopy: false);
        Assert.Contains("QueueBatches: 3", asyncDump);
        Assert.Contains("QueueLinks: 2", asyncDump);
        Assert.Contains("FrameWaits: Graphics", asyncDump);
        Assert.Contains("0:Compute slots=0..1 -> 1:Graphics slots=2..2", asyncDump);
        Assert.Contains("1:Graphics slots=2..2 -> 2:Compute slots=3..3", asyncDump);
    }

    [Fact]
    public void BeginFrameCallback_RecordsSetupEveryFrame()
    {
        using var graph = new RenderGraph();
        int setupCount = 0;
        int byteCount = 4;

        void Record(RenderGraph g)
        {
            var destination = g.CreateBuffer(
                "UploadDestination",
                new BufferDesc
                {
                    SizeInBytes = 64,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            byte[] data = new byte[byteCount];
            BufferUploadPasses.AddUploadPass(
                g,
                "UploadDeclarationPass",
                destination,
                0,
                data);
            g.AddRasterPass(
                "UploadDeclarationObserver",
                builder =>
                {
                    setupCount++;
                    builder.SideEffect();
                },
                _ => { });
        }

        graph.BeginFrame(Record);
        graph.Compile();

        byteCount = 8;
        graph.BeginFrame(Record);
        graph.Compile();

        Assert.Equal(2, setupCount);
    }

    [Fact]
    public async Task CompileAsync_UsesRecordedInput()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame(static graph =>
        {
            var buffer = graph.CreateBuffer("CompileAsyncBuffer", RawUavBufferDesc());
            graph.AddRasterPass(
                "CompileAsyncPass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });
        await graph.CompileAsync();

        Assert.Contains("CompileAsyncPass", graph.DumpText());
    }

    [Fact]
    public async Task CompileAsync_PreservesInitializedBufferSemantics()
    {
        using var graph = new RenderGraph();
        RenderGraphHandle initialized = default;

        graph.BeginFrame(g =>
        {
            initialized = g.CreateBuffer(
                "CompileAsyncInitialized",
                new BufferDesc
                {
                    SizeInBytes = 4,
                    BindFlags = BindFlags.ShaderResource,
                    InitialState = ResourceState.ShaderResource,
                },
                [1, 2, 3, 4]);
            g.AddRasterPass(
                "Observe Initialized",
                builder =>
                {
                    builder.SideEffect();
                    builder.Read(initialized, ResourceState.ShaderResource);
                },
                _ => { });
        });

        await graph.CompileAsync();

        Assert.Contains("Observe Initialized", graph.DumpText());
        Assert.False(RenderGraphTestHelpers.ResourceReusable(graph, initialized));
    }

    [Fact]
    public async Task BeginFrame_JoinsCompletedBackgroundCompile()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame(static graph =>
        {
            var buffer = graph.CreateBuffer("FirstBackgroundBuffer", RawUavBufferDesc());
            graph.AddRasterPass(
                "FirstBackgroundPass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });
        await graph.CompileAsync();

        graph.BeginFrame(static graph =>
        {
            var buffer = graph.CreateBuffer("SecondBackgroundBuffer", RawUavBufferDesc());
            graph.AddRasterPass(
                "SecondBackgroundPass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });

        Assert.Contains("SecondBackgroundPass", graph.DumpText());
    }

    [Fact]
    public async Task CompileAsync_BlocksMutationUntilOwnerBoundary()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame(static graph =>
        {
            var buffer = graph.CreateBuffer("PendingBackgroundBuffer", RawUavBufferDesc());
            graph.AddRasterPass(
                "PendingBackgroundPass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });
        await graph.CompileAsync();

        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.CreateBuffer("MutationBeforeJoin", RawUavBufferDesc()));

        Assert.Contains("cannot mutate the live graph", ex.Message);
        Assert.Contains("background compilation", ex.Message);

        graph.Compile();
        graph.CreateBuffer("MutationAfterJoin", RawUavBufferDesc());
    }

    [Fact]
    public async Task CompileAsync_RejectsUnrecordedFeatures()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("AsyncFeature", g =>
        {
            var buffer = g.CreateBuffer("AsyncFeatureBuffer", RawUavBufferDesc());
            g.AddRasterPass(
                "AsyncFeaturePass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });
        graph.AddFeature(feature);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.CompileAsync());

        Assert.Contains("requires render features to be recorded", ex.Message);
        Assert.False(feature.AddPassesCalled);
    }

    [Fact]
    public void BackgroundCompileActive_RejectsLiveGraphMutation()
    {
        using var graph = new RenderGraph();
        var active = typeof(RenderGraph).GetField(
            "_backgroundCompileActive",
            ReflectionFlags.NonPublic | ReflectionFlags.Instance)!;

        active.SetValue(graph, 1);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                graph.CreateBuffer("IllegalDuringCompile", RawUavBufferDesc()));

            Assert.Contains("cannot mutate the live graph", ex.Message);
            Assert.Contains("background compilation", ex.Message);
        }
        finally
        {
            active.SetValue(graph, 0);
        }
    }

    [Fact]
    public void Compile_GroupsIndependentAsyncQueues()
    {
        using var graph = new RenderGraph();
        var a = graph.CreateBuffer("AsyncGroupA", RawUavBufferDesc());
        var b = graph.CreateBuffer("AsyncGroupB", RawUavBufferDesc());
        var c = graph.CreateBuffer("AsyncGroupC", RawUavBufferDesc());

        graph.AddComputePass(
            "Async Group A",
            builder => builder.Write(a, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddRasterPass(
            "Async Group B",
            builder => builder.Write(b, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddComputePass(
            "Async Group C",
            builder => builder.Write(c, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(a, ResourceState.UnorderedAccess, (_, _) => { });
        graph.ExtractBuffer(b, ResourceState.UnorderedAccess, (_, _) => { });
        graph.ExtractBuffer(c, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Mixed:Graphics:0:3"],
            RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Equal(
            ["Compute:Compute:0:2", "Command:Graphics:2:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
    }

    [Fact]
    public void Compile_StartsIndependentAsyncQueue()
    {
        using var graph = new RenderGraph();
        var graphics = graph.CreateBuffer("AsyncStartGraphics", RawUavBufferDesc());
        var compute = graph.CreateBuffer("AsyncStartCompute", RawUavBufferDesc());

        graph.AddRasterPass(
            "Async Start Graphics",
            builder => builder.Write(graphics, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddComputePass(
            "Async Start Compute",
            builder => builder.Write(compute, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(graphics, ResourceState.UnorderedAccess, (_, _) => { });
        graph.ExtractBuffer(compute, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Mixed:Graphics:0:2"],
            RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Equal(
            ["Compute:Compute:0:1", "Command:Graphics:1:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
    }

    [Fact]
    public void Compile_ExtendsAliasLifetimesAcrossIndependentAsyncQueues()
    {
        using var graph = new RenderGraph();
        var compute = graph.CreateBuffer("AsyncAliasCompute", RawUavBufferDesc());
        var graphics = graph.CreateBuffer("AsyncAliasGraphics", RawUavBufferDesc());

        graph.AddComputePass(
            "Async Alias Compute",
            builder => builder.Write(compute, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddRasterPass(
            "Async Alias Graphics",
            builder => builder.Write(graphics, ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(compute, ResourceState.UnorderedAccess);
        graph.SetFinalState(graphics, ResourceState.UnorderedAccess);

        Assert.Equal((0, 0), RenderGraphTestHelpers.ResourceLifetime(graph, compute));
        Assert.Equal((1, 1), RenderGraphTestHelpers.ResourceLifetime(graph, graphics));
        Assert.Equal((0, 1), RenderGraphTestHelpers.ResourceLifetime(graph, compute, asyncCompute: true));
        Assert.Equal((0, 1), RenderGraphTestHelpers.ResourceLifetime(graph, graphics, asyncCompute: true));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
    }

    [Fact]
    public void Compile_KeepsEffectOrder()
    {
        using var graph = new RenderGraph();
        var a = graph.CreateBuffer("EffectOrderA", RawUavBufferDesc());
        var c = graph.CreateBuffer("EffectOrderC", RawUavBufferDesc());

        graph.AddComputePass(
            "Effect Order A",
            builder => builder.Write(a, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddRasterPass(
            "Effect Order B",
            builder => builder.SideEffect(),
            _ => { });
        graph.AddComputePass(
            "Effect Order C",
            builder => builder.Write(c, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(a, ResourceState.UnorderedAccess, (_, _) => { });
        graph.ExtractBuffer(c, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Compute:Compute:0:1", "Command:Graphics:1:1", "Compute:Compute:2:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true));

        using var blocked = new RenderGraph();
        var blockedBuffer = blocked.CreateBuffer("EffectOrderBlocked", RawUavBufferDesc());
        blocked.AddRasterPass(
            "Effect Order First",
            builder => builder.SideEffect(),
            _ => { });
        blocked.AddComputePass(
            "Effect Order Async",
            builder => builder.Write(blockedBuffer, ResourceState.UnorderedAccess),
            (_, _) => { });
        blocked.ExtractBuffer(blockedBuffer, ResourceState.UnorderedAccess, (_, _) => { });
        blocked.Compile();

        Assert.Equal(
            ["Command:Graphics:0:1", "Compute:Compute:1:1"],
            RenderGraphTestHelpers.QueueBatches(blocked, asyncCompute: true));
    }

    [Fact]
    public void Compile_MergesCommandBatches()
    {
        using var graph = new RenderGraph();
        var a = graph.CreateBuffer("CommandBatchA", RawUavBufferDesc());
        var b = graph.CreateBuffer("CommandBatchB", RawUavBufferDesc());
        var c = graph.CreateBuffer("CommandBatchC", RawUavBufferDesc());

        graph.AddRasterPass(
            "Command Batch A",
            builder => builder.Write(a, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Command Batch B",
            builder =>
            {
                builder.Read(a, ResourceState.ShaderResource);
                builder.Write(b, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.AddRasterPass(
            "Command Batch C",
            builder =>
            {
                builder.Read(b, ResourceState.ShaderResource);
                builder.Write(c, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.ExtractBuffer(c, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Command:Graphics:0:3"],
            RenderGraphTestHelpers.QueueBatches(graph));
    }

    [Fact]
    public void Compile_MergesGraphicsQueueModes()
    {
        using var graph = new RenderGraph();
        var source = graph.CreateBuffer(
            "MixedCopySource",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var copied = graph.CreateBuffer(
            "MixedCopyTarget",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.Undefined,
            });
        var computed = graph.CreateBuffer("MixedComputeTarget", RawUavBufferDesc());
        var output = graph.CreateBuffer("MixedCommandTarget", RawUavBufferDesc());

        BufferCopyPasses.AddCopyPass(graph, "Mixed Copy", source, copied, 0, 0, 4);
        graph.AddComputePass(
            "Mixed Compute",
            builder =>
            {
                builder.Read(copied, ResourceState.ShaderResource);
                builder.Write(computed, ResourceState.UnorderedAccess);
            },
            (_, _) => { });
        graph.AddRasterPass(
            "Mixed Command",
            builder =>
            {
                builder.Read(computed, ResourceState.ShaderResource);
                builder.Write(output, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.ExtractBuffer(output, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Mixed:Graphics:0:3"],
            RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Equal(
            ["Copy:Copy:0:1", "Compute:Compute:1:1", "Command:Graphics:2:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true, asyncCopy: true));
        Assert.Equal(
            ["0:1:Copy->Compute", "1:2:Compute->Graphics"],
            RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true, asyncCopy: true));
        Assert.Equal(
            ["Mixed:Compute:0:2", "Command:Graphics:2:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true, asyncCopy: false));
        Assert.Equal(
            ["0:1:Compute->Graphics"],
            RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true, asyncCopy: false));
    }

    [Fact]
    public void Compile_KeepsGraphicsStateCopyOnGraphicsWithoutCopyQueue()
    {
        using var graph = new RenderGraph();
        var source = graph.CreateTexture(
            "GraphicsCopySource",
            RgbaDesc(BindFlags.RenderTarget | BindFlags.CopySource));
        var target = graph.CreateTexture(
            "GraphicsCopyTarget",
            RgbaDesc(BindFlags.CopyDestination | BindFlags.ShaderResource));
        var output = graph.CreateBuffer("GraphicsCopyOutput", RawUavBufferDesc());

        graph.AddRasterPass(
            "Write Copy Source",
            builder => builder.Write(source, ResourceState.RenderTarget),
            _ => { });
        TextureCopyPasses.AddCopyPass(graph, "Copy Graphics Source", source, target);
        graph.AddComputePass(
            "Read Copied Texture",
            builder =>
            {
                builder.Read(target, ResourceState.ShaderResource);
                builder.Write(output, ResourceState.UnorderedAccess);
            },
            (_, _) => { });
        graph.ExtractBuffer(output, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.Equal(
            ["Mixed:Graphics:0:2", "Compute:Compute:2:1"],
            RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true, asyncCopy: false));
        Assert.Equal(
            ["0:1:Graphics->Compute"],
            RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true, asyncCopy: false));
    }

    [Fact]
    public void Compile_BuildsResourceRecords()
    {
        using var graph = new RenderGraph();
        var reusable = graph.CreateBuffer(
            "Reusable",
            new BufferDesc
            {
                SizeInBytes = 64,
                BindFlags = BindFlags.None,
                Raw = true,
            });
        var initialized = graph.CreateBuffer(
            "Initialized",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            },
            [1, 2, 3, 4]);

        graph.AddRasterPass(
            "Write Reusable",
            builder => builder.Write(reusable, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Read Initialized",
            builder =>
            {
                builder.SideEffect();
                builder.Read(initialized, ResourceState.ShaderResource);
            },
            _ => { });
        graph.ExtractBuffer(reusable, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.False(RenderGraphTestHelpers.ResourceReusable(graph, reusable));
        Assert.False(RenderGraphTestHelpers.ResourceReusable(graph, initialized));
        Assert.True((RenderGraphTestHelpers.BufferPoolBindFlags(graph, reusable) & BindFlags.UnorderedAccess) != 0);
        Assert.Equal(64ul, RenderGraphTestHelpers.BufferPoolSize(graph, reusable));

        string dump = graph.DumpText();
        Assert.Contains("Allocation: Dedicated size=64", dump);
        Assert.Contains("Allocation: Dedicated size=4", dump);
    }

    [Fact]
    public void Compile_BuildsRetentionRecords()
    {
        using var graph = new RenderGraph();
        var retained = graph.CreateBuffer("Retained", RawUavBufferDesc());
        var exported = graph.CreateBuffer("Exported", RawUavBufferDesc());
        var imported = graph.ImportBuffer(
            "Imported",
            new BufferHandle(42, 1),
            new BufferDesc
            {
                Name = "Imported",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                Raw = true,
            },
            new ImportDesc(ResourceState.ShaderResource));

        graph.AddRasterPass(
            "Write Retained",
            builder => builder.Write(retained, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Write Exported",
            builder => builder.Write(exported, ResourceState.UnorderedAccess),
            _ => { });
        graph.AddRasterPass(
            "Read Imported",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            _ => { });
        graph.SetFinalState(retained, ResourceState.ShaderResource);
        graph.ExtractBuffer(exported, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Compile();

        Assert.True(RenderGraphTestHelpers.ResourceRetained(graph, retained));
        Assert.False(RenderGraphTestHelpers.ResourceRetained(graph, exported));
        Assert.False(RenderGraphTestHelpers.ResourceRetained(graph, imported));
        Assert.True(RenderGraphTestHelpers.ResourceExported(graph, exported));
        Assert.True(RenderGraphTestHelpers.ResourceFinalState(graph, retained));
        Assert.True(RenderGraphTestHelpers.ResourceFinalState(graph, exported));
        Assert.False(RenderGraphTestHelpers.ResourceFinalState(graph, imported));
        Assert.Equal(1, RenderGraphTestHelpers.RetainCount(graph));

        string dump = graph.DumpText();
        Assert.Contains("Retained Buffer Transient Live", dump);
        Assert.Contains("FinalState", dump);
        Assert.Contains("Exported Buffer Transient Live [1..1] uses=1 v1 Exported FinalState", dump);
        Assert.Contains("Imported Buffer Imported Live", dump);
        Assert.Contains("Imported Buffer Imported Live [2..2] uses=1 v0", dump);
        Assert.Contains("Allocation: Imported", dump);
    }

    [Fact]
    public void DumpText_ReportsWriteAfterReadDependency()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Counter", RawUavBufferDesc(), [0]);

        graph.AddRasterPass(
            "Read Current",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });
        graph.AddRasterPass(
            "Overwrite",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { });

        string dump = graph.DumpText();

        Assert.Contains("Dependencies: 1", dump);
        Assert.Contains("0:Read Current -> 1:Overwrite: Counter v0->v1 WAR", dump);
    }

    [Fact]
    public void DumpText_ReportsWriteAfterWriteDependency()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("Counter", RawUavBufferDesc());

        graph.AddRasterPass(
            "Clear",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.AddRasterPass(
            "Overwrite",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });
        graph.ExtractBuffer(buffer, ResourceState.UnorderedAccess, (_, _) => { });

        string dump = graph.DumpText();

        Assert.Contains("Dependencies: 1", dump);
        Assert.Contains("0:Clear -> 1:Overwrite: Counter v1->v2 WAW", dump);
    }

    private sealed class TestFeature : IRenderFeature
    {
        private static int s_callCounter;
        private readonly string _name;
        private readonly Action<RenderGraph>? _addPassesAction;

        public TestFeature(string name, Action<RenderGraph>? addPassesAction = null)
        {
            _name = name;
            _addPassesAction = addPassesAction;
        }

        public string Name => _name;
        public bool AddPassesCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        public int AddPassesCallOrder { get; private set; }

        public void AddPasses(RenderGraph graph)
        {
            AddPassesCalled = true;
            AddPassesCallOrder = ++s_callCounter;
            _addPassesAction?.Invoke(graph);
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }

        public static void ResetCallCounter() => s_callCounter = 0;
    }

    [Fact]
    public void Compile_AppliesRegisteredFeatures()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("TestFeature");
        graph.AddFeature(feature);

        Assert.False(feature.AddPassesCalled);

        graph.BeginFrame();
        graph.Compile();

        Assert.True(feature.AddPassesCalled);
    }

    [Fact]
    public void FeaturePassesParticipateInCompile()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature(
            "TestFeature",
            g =>
            {
                var texture = g.CreateTexture("FeatureTex", RgbaDesc());
                WriteTexture(g, texture, "FeaturePass");
                g.SetFinalState(texture, ResourceState.RenderTarget);
            });

        graph.AddFeature(feature);
        graph.BeginFrame();
        graph.Compile();

        AssertRegisteredPassNames(graph, "FeaturePass");
    }

    [Fact]
    public void MultipleFeaturesAreAppliedInRegistrationOrder()
    {
        using var graph = new RenderGraph();
        TestFeature.ResetCallCounter();

        var featureA = new TestFeature(
            "FeatureA",
            g =>
            {
                var texture = g.CreateTexture("TexA", RgbaDesc());
                WriteTexture(g, texture, "PassA");
                g.SetFinalState(texture, ResourceState.RenderTarget);
            });
        var featureB = new TestFeature(
            "FeatureB",
            g =>
            {
                var texture = g.CreateTexture("TexB", RgbaDesc());
                WriteTexture(g, texture, "PassB");
                g.SetFinalState(texture, ResourceState.RenderTarget);
            });

        graph.AddFeature(featureA);
        graph.AddFeature(featureB);
        graph.BeginFrame();
        graph.Compile();

        Assert.True(featureA.AddPassesCallOrder < featureB.AddPassesCallOrder);
        AssertRegisteredPassNames(graph, "PassA", "PassB");
    }

    [Fact]
    public void FeatureDisposedOnGraphDispose()
    {
        var feature = new TestFeature("TestFeature");

        var graph = new RenderGraph();
        graph.AddFeature(feature);
        Assert.False(feature.DisposeCalled);
        graph.Dispose();

        Assert.True(feature.DisposeCalled);
    }

    [Fact]
    public void BeginFrameCallback_RecordsMatchingShapeOnce()
    {
        using var graph = new RenderGraph();
        int recordCount = 0;
        int setupCount = 0;

        graph.BeginFrame(g =>
        {
            recordCount++;
            var texture = g.CreateTexture("FrameTexture", RgbaDesc());
            g.AddRasterPass(
                "FramePass",
                builder =>
                {
                    setupCount++;
                    builder.Write(texture, ResourceState.RenderTarget);
                },
                _ => { });
            g.SetFinalState(texture, ResourceState.RenderTarget);
        });
        graph.Compile();

        Assert.Equal(1, recordCount);
        Assert.Equal(1, setupCount);
        AssertRegisteredPassNames(graph, "FramePass");

        graph.BeginFrame(g =>
        {
            recordCount++;
            var texture = g.CreateTexture("FrameTexture", RgbaDesc());
            g.AddRasterPass(
                "FramePass",
                builder =>
                {
                    setupCount++;
                    builder.Write(texture, ResourceState.RenderTarget);
                },
                _ => { });
            g.SetFinalState(texture, ResourceState.RenderTarget);
        });
        graph.Compile();

        Assert.Equal(2, recordCount);
        Assert.Equal(2, setupCount);
        AssertRegisteredPassNames(graph, "FramePass");
    }

    [Fact]
    public void BeginFrameCallback_RecordsChangingShapeOnce()
    {
        using var graph = new RenderGraph();
        bool includeExtra = false;
        int recordCount = 0;
        int setupCount = 0;

        void Record(RenderGraph g)
        {
            recordCount++;

            var texture = g.CreateTexture("FrameShapeTexture", RgbaDesc());
            g.AddRasterPass(
                "FrameShapePass",
                builder =>
                {
                    setupCount++;
                    builder.Write(texture, ResourceState.RenderTarget);
                },
                _ => { });
            g.SetFinalState(texture, ResourceState.RenderTarget);

            if (!includeExtra)
                return;

            var extra = g.CreateTexture("FrameShapeExtra", RgbaDesc());
            g.AddRasterPass(
                "FrameShapeExtraPass",
                builder =>
                {
                    setupCount++;
                    builder.Write(extra, ResourceState.RenderTarget);
                },
                _ => { });
            g.SetFinalState(extra, ResourceState.RenderTarget);
        }

        graph.BeginFrame(Record);
        graph.Compile();

        includeExtra = true;
        graph.BeginFrame(Record);
        graph.Compile();

        Assert.Equal(2, recordCount);
        Assert.Equal(3, setupCount);
        AssertRegisteredPassNames(graph, "FrameShapePass", "FrameShapeExtraPass");
    }

    [Fact]
    public void RemovedFeatureIsNotAppliedDuringCompile()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("TestFeature");

        graph.AddFeature(feature);
        graph.RemoveFeature(feature);
        graph.Compile();

        Assert.False(feature.AddPassesCalled);
    }

    [Fact]
    public void Compile_RejectsUnrecordedFeatures()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("CompileFeature", g =>
        {
            var buffer = g.CreateBuffer("CompileFeatureBuffer", RawUavBufferDesc());
            g.AddRasterPass(
                "CompileFeaturePass",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(buffer, ResourceState.UnorderedAccess);
                },
                _ => { });
        });
        graph.AddFeature(feature);

        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.Compile());

        Assert.Contains("requires render features to be recorded", ex.Message);
        Assert.False(feature.AddPassesCalled);
    }

    [Fact]
    public void ExtractTexture_InvokesSinkAfterExecuteWithRhiDevice()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        bool sinkCalled = false;
        TextureHandle exported = default;
        ResourceState finalState = ResourceState.Undefined;

        graph.BeginFrame();
        var handle = graph.CreateTexture(
            "ExportedTexture",
            RgbaDesc() with { InitialState = ResourceState.Common });
        graph.ExtractTexture(
            handle,
            ResourceState.Common,
            (texture, state) =>
            {
                sinkCalled = true;
                exported = texture;
                finalState = state;
            });

        graph.Execute(device, queue);

        Assert.True(sinkCalled);
        Assert.True(exported.IsValid);
        Assert.Equal(ResourceState.Common, finalState);
    }

    [Fact]
    public void TryRetireSubmittedFrames_RetiresSubmittedFrameBeforeResize()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var swapchainHandle = device.CreateSwapchain(new SwapchainDesc
        {
            Name = "RenderGraph resize",
            Width = 64,
            Height = 32,
            Format = Format.Bgra8Unorm,
            BufferCount = 2,
        });
        var swapchain = device.GetSwapchain(swapchainHandle);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        TextureHandle backBuffer = swapchain.CurrentTexture;
        TextureViewHandle backBufferView = swapchain.CurrentRenderTargetView;
        TextureDesc backBufferDesc = device.GetTextureDesc(backBuffer);
        RenderGraphHandle output = graph.ImportTexture(
            "BackBuffer",
            backBuffer,
            backBufferDesc,
            new ImportDesc(ResourceState.Present)
            {
                FinalState = ResourceState.Present,
                AllowWrite = true,
            },
            [backBufferView]);
        graph.AddRasterPass(
            "Clear BackBuffer",
            builder => builder.Write(output, ResourceState.RenderTarget),
            context =>
            {
                var rtv = context.GetTextureView(output, ViewKind.RenderTarget, backBufferDesc.Format);
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, checked((int)backBufferDesc.Width), checked((int)backBufferDesc.Height)),
                    ColorAttachments =
                    [
                        new ColorAttachmentDesc
                        {
                            View = rtv,
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                            ClearColor = Color.Black,
                        },
                    ],
                });
                pass.End();
            });
        graph.Execute(device, queue);

        Assert.Equal(1, PendingFrameCount(graph));
        Assert.True(graph.TryRetireSubmittedFrames());
        Assert.Equal(0, PendingFrameCount(graph));

        swapchain.Resize(80, 40);
        Assert.Equal(80u, swapchain.Width);
        Assert.Equal(40u, swapchain.Height);

        device.Destroy(swapchainHandle);
    }

    [Fact]
    public void ExtractTexture_RejectsStaleHandleAfterBeginFrame()
    {
        using var graph = new RenderGraph();
        var stale = graph.CreateTexture("OldFrameTexture", RgbaDesc());

        graph.BeginFrame();

        bool sinkCalled = false;
        Assert.Throws<InvalidOperationException>(() =>
            graph.ExtractTexture(stale, ResourceState.RenderTarget, (_, _) => sinkCalled = true));
        Assert.False(sinkCalled);
    }

    [Fact]
    public void Compile_RejectsStaleHandleUseInPassSetup()
    {
        using var graph = new RenderGraph();
        var stale = graph.CreateBuffer("OldBuffer", RawUavBufferDesc());

        graph.BeginFrame();

        var live = graph.CreateBuffer("LiveBuffer", RawUavBufferDesc());
        Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "DeadStalePass",
            builder => builder.Write(stale, ResourceState.UnorderedAccess),
            _ => { }));
        graph.AddRasterPass(
            "LivePass",
            builder => builder.Write(live, ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(live, ResourceState.UnorderedAccess);
    }

    [Fact]
    public void Compile_ClearsFrameScratchWhenFrameShrinksAndRegrows()
    {
        using var graph = new RenderGraph();
        var mipTexture = graph.CreateTexture("MipScratchTexture", MipUavDesc());
        graph.AddRasterPass(
            "WriteMip0",
            builder => builder.Write(
                mipTexture,
                ResourceState.UnorderedAccess,
                SubResourceRange.Mip(0)),
            _ => { });
        graph.SetFinalState(mipTexture, ResourceState.UnorderedAccess);

        graph.Compile();

        graph.BeginFrame();
        var smallLive = graph.CreateBuffer("SmallLive", RawUavBufferDesc());
        graph.AddRasterPass(
            "SmallLivePass",
            builder => builder.Write(smallLive, ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(smallLive, ResourceState.UnorderedAccess);

        graph.Compile();
        AssertRegisteredPassNames(graph, "SmallLivePass");

        graph.BeginFrame();
        var regrowLive = graph.CreateBuffer("RegrowLive", RawUavBufferDesc(96));
        graph.CreateTexture("RegrowTexture", MipUavDesc());
        graph.AddRasterPass(
            "RegrowLivePass",
            builder => builder.Write(regrowLive, ResourceState.UnorderedAccess),
            _ => { });
        graph.SetFinalState(regrowLive, ResourceState.UnorderedAccess);

        graph.Compile();
        AssertRegisteredPassNames(graph, "RegrowLivePass");
    }

    [Fact]
    public void ResourceNamesAreDebugLabelsNotIdentity()
    {
        using var graph = new RenderGraph();
        var first = graph.CreateTexture("SharedName", RgbaDesc(BindFlags.RenderTarget));
        var second = graph.CreateTexture("SharedName", RgbaDesc(BindFlags.RenderTarget));

        Assert.NotEqual(first, second);

        graph.AddRasterPass("WriteFirstSharedName", builder => builder.Write(first, ResourceState.RenderTarget), _ => { });
        graph.AddRasterPass("WriteSecondSharedName", builder => builder.Write(second, ResourceState.RenderTarget), _ => { });
        graph.SetFinalState(first, ResourceState.RenderTarget);
        graph.SetFinalState(second, ResourceState.RenderTarget);

        Assert.Equal(
            ["WriteFirstSharedName", "WriteSecondSharedName"],
            ExecutedPassNames(graph));
    }

    [Fact]
    public void Blackboard_StoresTypedFrameData()
    {
        using var graph = new RenderGraph();
        var shared = graph.CreateBuffer("SharedByType", RawUavBufferDesc());

        Assert.Throws<ArgumentNullException>(() => graph.Blackboard.Set<string?>(null));
        graph.Blackboard.Set(new DebugShared(shared));

        Assert.True(graph.Blackboard.TryGet(out DebugShared stored));
        Assert.Equal(shared, stored.Handle);
    }

    [Fact]
    public void Blackboard_ClearsOnBeginFrame()
    {
        using var graph = new RenderGraph();
        graph.Blackboard.Set(new DebugShared(graph.CreateBuffer("SharedByType", RawUavBufferDesc())));

        graph.BeginFrame();

        Assert.False(graph.Blackboard.TryGet<DebugShared>(out _));
    }

    [Fact]
    public void StaleHandleCannotAliasSameIndexResourceInNextFrame()
    {
        using var graph = new RenderGraph();
        var stale = graph.CreateTexture("OldIndexZero", RgbaDesc());

        graph.BeginFrame();
        graph.CreateTexture("NewIndexZero", RgbaDesc());

        Assert.Throws<InvalidOperationException>(() => graph.SetFinalState(stale, ResourceState.RenderTarget));
    }

    [Fact]
    public void CreateTextureAcceptsTransientLifetime()
    {
        using var graph = new RenderGraph();
        var texture = graph.CreateTexture(
            "TransientTexture",
            RgbaDesc(),
            SomeEngine.Render.Graph.ResourceLifetime.Transient);

        graph.AddRasterPass(
            "Use Transient Texture",
            builder => builder.Write(texture, ResourceState.RenderTarget),
            _ => { });
        graph.SetFinalState(texture, ResourceState.RenderTarget);

        graph.Compile();

        Assert.True(RenderGraphTestHelpers.ResourceReusable(graph, texture));
    }

    [Fact]
    public void HandleCannotCrossGraphBoundaryEvenAtSameGeneration()
    {
        using var first = new RenderGraph();
        using var second = new RenderGraph();
        var foreign = first.CreateBuffer("ForeignBuffer", RawUavBufferDesc());

        Assert.Throws<InvalidOperationException>(() => second.AddRasterPass(
            "Foreign Handle Pass",
            builder => builder.Write(foreign, ResourceState.UnorderedAccess),
            _ => { }));
    }

    [Fact]
    public void BuilderCannotMutateGraphAfterOwningSetupReturns()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("BuilderLifetimeBuffer", RawUavBufferDesc());
        RenderGraphBuilder captured = default;

        graph.AddRasterPass(
            "Capture Builder",
            builder =>
            {
                captured = builder;
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(
            () => captured.Write(buffer, ResourceState.UnorderedAccess));

        Assert.Contains("RenderGraphBuilder is only valid", ex.Message);
    }

    [Fact]
    public void BuilderCannotMutateAnotherPassSetup()
    {
        using var graph = new RenderGraph();
        var buffer = graph.CreateBuffer("BuilderOwnerBuffer", RawUavBufferDesc());
        RenderGraphBuilder captured = default;

        graph.AddRasterPass(
            "Owner Setup",
            builder =>
            {
                captured = builder;
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "Wrong Setup",
            _ => captured.SideEffect(),
            _ => { }));

        Assert.Contains("RenderGraphBuilder is only valid", ex.Message);
    }

    [Fact]
    public void BuilderCannotMutateSamePassIndexInNextFrameSetup()
    {
        using var graph = new RenderGraph();
        RenderGraphBuilder captured = default;

        graph.AddRasterPass(
            "Original Index Zero",
            builder =>
            {
                captured = builder;
                builder.SideEffect();
            },
            _ => { });
        graph.BeginFrame();

        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "New Index Zero",
            _ => captured.SideEffect(),
            _ => { }));

        Assert.Contains("RenderGraphBuilder is only valid", ex.Message);
    }

    [Fact]
    public void AddPassNonGenericRunsSetupDuringCompile()
    {
        using var graph = new RenderGraph();
        bool setupCalled = false;
        var buffer = graph.CreateBuffer("TestBuffer", RawUavBufferDesc());

        graph.AddRasterPass(
            "NonGenericPass",
            builder =>
            {
                builder.Write(buffer, ResourceState.UnorderedAccess);
                setupCalled = true;
            },
            _ => { });
        graph.SetFinalState(buffer, ResourceState.UnorderedAccess);

        graph.Compile();

        Assert.True(setupCalled);
    }

    [Fact]
    public void AddPassGenericDataIsResetBeforeSetupReplay()
    {
        using var graph = new RenderGraph();
        var rasterBuffer = graph.CreateBuffer("RasterBuffer", RawUavBufferDesc());
        var copyBuffer = graph.CreateBuffer("CopyBuffer", RawUavBufferDesc());
        var computeBuffer = graph.CreateBuffer("ComputeBuffer", RawUavBufferDesc());
        int rasterSetupCount = 0;
        int copySetupCount = 0;
        int computeSetupCount = 0;

        graph.AddRasterPass<List<RenderGraphHandle>>(
            "Raster Data Reset",
            (builder, data) =>
            {
                rasterSetupCount++;
                Assert.Empty(data);
                data.Add(rasterBuffer);
                builder.Write(data[0], ResourceState.UnorderedAccess);
            },
            (_, data) => Assert.Single(data));
        graph.AddCopyPass<List<RenderGraphHandle>>(
            "Copy Data Reset",
            (builder, data) =>
            {
                copySetupCount++;
                Assert.Empty(data);
                data.Add(copyBuffer);
                builder.Write(data[0], ResourceState.CopyDestination);
            },
            (_, data) => Assert.Single(data));
        graph.AddComputePass<List<RenderGraphHandle>>(
            "Compute Data Reset",
            (builder, data) =>
            {
                computeSetupCount++;
                Assert.Empty(data);
                data.Add(computeBuffer);
                builder.Write(data[0], ResourceState.UnorderedAccess);
            },
            (_, _, data) => Assert.Single(data));
        graph.SetFinalState(rasterBuffer, ResourceState.UnorderedAccess);
        graph.SetFinalState(copyBuffer, ResourceState.CopyDestination);
        graph.SetFinalState(computeBuffer, ResourceState.UnorderedAccess);

        graph.Compile();

        graph.CreateBuffer("Trigger Declaration Replay", RawUavBufferDesc());

        Assert.Equal(2, rasterSetupCount);
        Assert.Equal(2, copySetupCount);
        Assert.Equal(2, computeSetupCount);
    }

    [Fact]
    public void Compile_RejectsResourceCreationDuringPassSetup()
    {
        using var graph = new RenderGraph();
        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "Mutate Resources",
            _ => graph.CreateTexture("IllegalTexture", RgbaDesc()),
            _ => { }));

        Assert.Contains("CreateTexture", ex.Message);
        Assert.Contains("pass setup", ex.Message);
        Assert.Contains("Mutate Resources", ex.Message);
    }

    [Fact]
    public void Compile_RejectsPassCreationDuringPassSetup()
    {
        using var graph = new RenderGraph();
        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddRasterPass(
            "Mutate Passes",
            _ => graph.AddRasterPass("Illegal Pass", _ => { }, _ => { }),
            _ => { }));

        Assert.Contains("AddRasterPass", ex.Message);
        Assert.Contains("pass setup", ex.Message);
        Assert.Contains("Mutate Passes", ex.Message);
    }

    private static void AssertRegisteredPassNames(RenderGraph graph, params string[] expected)
        => Assert.Equal(expected, RenderGraphTestHelpers.ExecutedPassNames(graph));

    private static int CacheCount(RenderGraph graph)
    {
        object compiler = typeof(RenderGraph)
            .GetField("_compiler", ReflectionFlags.Instance | ReflectionFlags.NonPublic)!
            .GetValue(graph)!;
        object cache = compiler.GetType()
            .GetField("GraphCache", ReflectionFlags.Instance | ReflectionFlags.NonPublic | ReflectionFlags.Public)!
            .GetValue(compiler)!;
        object entries = cache.GetType()
            .GetField("_entries", ReflectionFlags.Instance | ReflectionFlags.NonPublic)!
            .GetValue(cache)!;
        return ((System.Collections.ICollection)entries).Count;
    }

    private static int PendingFrameCount(RenderGraph graph)
    {
        object executor = typeof(RenderGraph)
            .GetField("_executor", ReflectionFlags.Instance | ReflectionFlags.NonPublic)!
            .GetValue(graph)!;
        object pendingFrames = executor.GetType()
            .GetField("PendingFrames", ReflectionFlags.Instance | ReflectionFlags.NonPublic | ReflectionFlags.Public)!
            .GetValue(executor)!;
        return ((System.Collections.ICollection)pendingFrames).Count;
    }

    private static TextureDesc RgbaDesc(BindFlags bindFlags = BindFlags.ShaderResource, uint size = 32)
        => new()
        {
            Dimension = ResourceDimension.Texture2D,
            Width = size,
            Height = size,
            Format = Format.Rgba8Unorm,
            BindFlags = bindFlags,
        };

    private static TextureDesc DepthDesc(uint size = 512)
        => new()
        {
            Dimension = ResourceDimension.Texture2D,
            Width = size,
            Height = size,
            Format = Format.D32Float,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        };

    private static TextureDesc MipUavDesc(uint size = 64, uint mipLevels = 4)
        => new()
        {
            Dimension = ResourceDimension.Texture2D,
            Width = size,
            Height = size,
            MipLevels = mipLevels,
            Format = Format.R32Float,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };

    private static BufferDesc RawUavBufferDesc(ulong size = 64)
        => new()
        {
            SizeInBytes = size,
            BindFlags = BindFlags.UnorderedAccess,
            Raw = true,
        };

    private readonly record struct DebugShared(RenderGraphHandle Handle);
}
