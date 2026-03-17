using System.Collections.Generic;
using System.Reflection;
using Diligent;
using NSubstitute;
using NUnit.Framework;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Tests;

[TestFixture]
public class RenderGraphTests
{
    private class EmptyData
    {
        public object? UserData;
    }

    [Test]
    public void TestPlacedResourceAliasing()
    {
        using var graph = new RenderGraph();
        var desc = new TextureDesc
        {
            Width = 1024,
            Height = 1024,
            Format = TextureFormat.RGBA8_UNorm,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.RenderTarget,
        };

        var tex1 = graph.CreateTexture("Tex1", desc);
        var tex2 = graph.CreateTexture("Tex2", desc);
        var tex3 = graph.CreateTexture("Tex3", desc);

        // Pass 1: Writes tex1
        graph.AddPass<EmptyData>(
            "Pass1",
            (builder, data) =>
            {
                builder.Write(tex1);
            },
            (ctx, data) => { }
        );

        // Pass 2: Reads tex1, Writes tex2
        graph.AddPass<EmptyData>(
            "Pass2",
            (builder, data) =>
            {
                builder.Read(tex1);
                builder.Write(tex2);
            },
            (ctx, data) => { }
        );

        // Pass 3: Reads tex2, Writes tex3
        graph.AddPass<EmptyData>(
            "Pass3",
            (builder, data) =>
            {
                builder.Read(tex2);
                builder.Write(tex3);
            },
            (ctx, data) => { }
        );

        // Tex1: First=0, Last=1
        // Tex2: First=1, Last=2
        // Tex3: First=2, Last=2
        // Tex1 and Tex3 have non-overlapping lifetimes (Tex1 ends at Pass 1, Tex3 starts at Pass 2).

        RenderGraph.GetMemoryRequirementsDelegate mockGetReqs = (name, kind, texDesc, bufDesc) =>
        {
            return new MemoryRequirements
            {
                Size = 1024 * 1024,
                Alignment = 256,
                MemoryTypeBits = 0xFFFFFFFF,
            };
        };

        graph.Compile(null, mockGetReqs);

        ulong offset1 = GetMemoryOffset(graph, tex1);
        ulong offset2 = GetMemoryOffset(graph, tex2);
        ulong offset3 = GetMemoryOffset(graph, tex3);

        Assert.That(
            offset1,
            Is.EqualTo(offset3),
            "Lifetimes don't overlap, should alias to same offset"
        );
        Assert.That(
            offset1,
            Is.Not.EqualTo(offset2),
            "Lifetimes overlap, should have different offsets"
        );
        Assert.That(
            offset2,
            Is.Not.EqualTo(offset3),
            "Lifetimes overlap, should have different offsets"
        );
    }

    [Test]
    public void TestPlacedResourceAliasing_NoDevice()
    {
        using var graph = new RenderGraph();
        var desc = new TextureDesc
        {
            Width = 1024,
            Height = 1024,
            Format = TextureFormat.RGBA8_UNorm,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.RenderTarget,
        };

        var tex1 = graph.CreateTexture("Tex1", desc);

        graph.AddPass<EmptyData>(
            "Pass1",
            (builder, data) =>
            {
                builder.Write(tex1);
            },
            (ctx, data) => { }
        );

        // Compile without device
        graph.Compile(null);

        ulong offset1 = GetMemoryOffset(graph, tex1);
        Assert.That(
            offset1,
            Is.EqualTo(ulong.MaxValue),
            "Should not allocate placed resource without device"
        );
    }

    [Test]
    public void TestPerMipBarriers()
    {
        using var graph = new RenderGraph();

        // Create a HiZ-like texture with 4 mip levels
        var hizDesc = new TextureDesc
        {
            Width = 512,
            Height = 512,
            MipLevels = 4,
            Format = TextureFormat.R32_Float,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };

        var depthDesc = new TextureDesc
        {
            Width = 512,
            Height = 512,
            Format = TextureFormat.D32_Float,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource,
        };

        var hDepth = graph.CreateTexture("Depth", depthDesc);
        var hHiZ = graph.CreateTexture("HiZ", hizDesc);

        // Pass 0: Build mip 0 from depth
        graph.AddPass<EmptyData>(
            "HiZ Mip0",
            (builder, data) =>
            {
                builder.Read(hDepth, ResourceState.ShaderResource);
                builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
            },
            (ctx, data) => { }
        );

        // Pass 1: Downsample mip 0 -> mip 1
        graph.AddPass<EmptyData>(
            "HiZ Downsample Mip1",
            (builder, data) =>
            {
                builder.Read(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
                builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(1));
            },
            (ctx, data) => { }
        );

        // Pass 2: Downsample mip 1 -> mip 2
        graph.AddPass<EmptyData>(
            "HiZ Downsample Mip2",
            (builder, data) =>
            {
                builder.Read(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(1));
                builder.Write(hHiZ, ResourceState.UnorderedAccess, SubResourceRange.Mip(2));
            },
            (ctx, data) => { }
        );

        graph.MarkOutput(hHiZ);
        graph.Compile(null);

        // Verify: Each downsample pass should get per-mip UAV barriers
        var compiledPasses = GetCompiledPasses(graph);
        var executionOrder = GetExecutionOrder(graph);

        Assert.That(executionOrder.Count, Is.GreaterThanOrEqualTo(3));

        // Check pass 1 (Downsample Mip1) barriers
        var pass1 = compiledPasses[executionOrder[1]];
        Assert.That(
            pass1.PreBarriers.Count,
            Is.GreaterThan(0),
            "Downsample Mip1 should have barriers"
        );

        // Since both src and dst are UAV on the same resource, we expect UAV flush barriers
        // The barrier should reference mip 0 and/or mip 1 (either individually or as a merged range)
        bool hasUavBarrier = false;
        foreach (var b in pass1.PreBarriers)
        {
            if (
                b.OldState == ResourceState.UnorderedAccess
                && b.NewState == ResourceState.UnorderedAccess
            )
            {
                hasUavBarrier = true;
                break;
            }
        }
        Assert.That(hasUavBarrier, Is.True, "Should have UAV flush barrier for mip chain");

        // Check pass 2 (Downsample Mip2) barriers
        var pass2 = compiledPasses[executionOrder[2]];
        Assert.That(
            pass2.PreBarriers.Count,
            Is.GreaterThan(0),
            "Downsample Mip2 should have barriers"
        );
    }

    [Test]
    public void TestWholeResourceBarriersStillWork()
    {
        using var graph = new RenderGraph();
        var desc = new TextureDesc
        {
            Width = 256,
            Height = 256,
            Format = TextureFormat.RGBA8_UNorm,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };

        var texSrc = graph.CreateTexture("SrcTex", desc);
        var texDst = graph.CreateTexture("DstTex", desc);

        // Pass 1: Write SrcTex as RenderTarget
        graph.AddPass<EmptyData>(
            "Write",
            (builder, data) =>
            {
                builder.Write(texSrc, ResourceState.RenderTarget);
            },
            (ctx, data) => { }
        );

        // Pass 2: Read SrcTex as ShaderResource, Write DstTex
        graph.AddPass<EmptyData>(
            "ReadAndWrite",
            (builder, data) =>
            {
                builder.Read(texSrc, ResourceState.ShaderResource);
                builder.Write(texDst, ResourceState.RenderTarget);
            },
            (ctx, data) => { }
        );

        graph.MarkOutput(texDst);
        graph.Compile(null);

        // Should compile without error and have barriers
        var compiledPasses = GetCompiledPasses(graph);
        var executionOrder = GetExecutionOrder(graph);

        Assert.That(executionOrder.Count, Is.EqualTo(2));

        // The ReadAndWrite pass should have a RT->SRV barrier for SrcTex
        var readPass = compiledPasses[executionOrder[1]];
        Assert.That(readPass.PreBarriers.Count, Is.GreaterThan(0));

        bool hasRtToSrvBarrier = false;
        foreach (var b in readPass.PreBarriers)
        {
            if (b.NewState == ResourceState.ShaderResource)
            {
                hasRtToSrvBarrier = true;
                break;
            }
        }
        Assert.That(hasRtToSrvBarrier, Is.True, "Should have RT -> SRV barrier");
    }

    // ── RenderFeature Tests ──

    private class TestFeature : IRenderFeature
    {
        public string Name => _name;
        public bool AddPassesCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        public int AddPassesCallOrder { get; private set; }
        public List<string> PassNames { get; } = [];

        private readonly string _name;
        private readonly Action<RenderGraph>? _addPassesAction;
        private static int _callCounter;

        public TestFeature(string name, Action<RenderGraph>? addPassesAction = null)
        {
            _name = name;
            _addPassesAction = addPassesAction;
        }

        public void Initialize(RenderContext context) { }

        public void AddPasses(RenderGraph graph)
        {
            AddPassesCalled = true;
            AddPassesCallOrder = ++_callCounter;
            _addPassesAction?.Invoke(graph);
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }

        public static void ResetCallCounter() => _callCounter = 0;
    }

    [Test]
    public void TestFeatureAddPassesCalledDuringCompile()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("TestFeature");
        graph.AddFeature(feature);

        // AddPasses should not be called before Compile
        Assert.That(feature.AddPassesCalled, Is.False);

        graph.Compile(null);

        // AddPasses should be called during Compile
        Assert.That(feature.AddPassesCalled, Is.True);
    }

    [Test]
    public void TestFeaturePassesParticipateInCompile()
    {
        using var graph = new RenderGraph();

        var desc = new TextureDesc
        {
            Width = 256,
            Height = 256,
            Format = TextureFormat.RGBA8_UNorm,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };

        bool passExecuted = false;

        var feature = new TestFeature(
            "TestFeature",
            g =>
            {
                var tex = g.CreateTexture("FeatureTex", desc);
                g.AddPass<EmptyData>(
                    "FeaturePass",
                    (builder, data) => builder.Write(tex),
                    (ctx, data) => passExecuted = true
                );
                g.MarkOutput(tex);
            }
        );

        graph.AddFeature(feature);
        graph.Compile(null);

        var executionOrder = GetExecutionOrder(graph);
        var compiledPasses = GetCompiledPasses(graph);

        Assert.That(
            executionOrder.Count,
            Is.EqualTo(1),
            "Feature pass should be in execution order"
        );
        Assert.That(compiledPasses[executionOrder[0]].Name, Is.EqualTo("FeaturePass"));
        Assert.That(compiledPasses[executionOrder[0]].Active, Is.True);
    }

    [Test]
    public void TestMultipleFeaturesOrdering()
    {
        using var graph = new RenderGraph();
        TestFeature.ResetCallCounter();

        var desc = new TextureDesc
        {
            Width = 256,
            Height = 256,
            Format = TextureFormat.RGBA8_UNorm,
            Type = ResourceDimension.Tex2d,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };

        var featureA = new TestFeature(
            "FeatureA",
            g =>
            {
                var tex = g.CreateTexture("TexA", desc);
                g.AddPass<EmptyData>(
                    "PassA",
                    (builder, data) => builder.Write(tex),
                    (ctx, data) => { }
                );
                g.MarkOutput(tex);
            }
        );

        var featureB = new TestFeature(
            "FeatureB",
            g =>
            {
                var tex = g.CreateTexture("TexB", desc);
                g.AddPass<EmptyData>(
                    "PassB",
                    (builder, data) => builder.Write(tex),
                    (ctx, data) => { }
                );
                g.MarkOutput(tex);
            }
        );

        graph.AddFeature(featureA);
        graph.AddFeature(featureB);
        graph.Compile(null);

        // FeatureA should be called before FeatureB
        Assert.That(featureA.AddPassesCallOrder, Is.LessThan(featureB.AddPassesCallOrder));

        var executionOrder = GetExecutionOrder(graph);
        Assert.That(executionOrder.Count, Is.EqualTo(2));
    }

    [Test]
    public void TestFeatureDisposedOnGraphDispose()
    {
        var feature = new TestFeature("TestFeature");

        {
            var graph = new RenderGraph();
            graph.AddFeature(feature);
            Assert.That(feature.DisposeCalled, Is.False);
            graph.Dispose();
        }

        Assert.That(feature.DisposeCalled, Is.True);
    }

    [Test]
    public void TestRemoveFeature()
    {
        using var graph = new RenderGraph();
        var feature = new TestFeature("TestFeature");

        graph.AddFeature(feature);
        graph.RemoveFeature(feature);
        graph.Compile(null);

        Assert.That(feature.AddPassesCalled, Is.False, "Removed feature should not be called");
    }

    // ── Reflection helpers ──

    private ulong GetMemoryOffset(RenderGraph graph, RenderGraphHandle handle)
    {
        var placementsField = typeof(RenderGraph).GetField(
            "_placements",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var placements = placementsField!.GetValue(graph) as System.Array;

        var indexProp = typeof(RenderGraphHandle).GetProperty(
            "Index",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        int index = (int)indexProp!.GetValue(handle)!;

        if (placements == null || index >= placements.Length)
            return ulong.MaxValue;

        var placement = placements.GetValue(index)!;
        var offsetField = placement
            .GetType()
            .GetField("Offset", BindingFlags.Public | BindingFlags.Instance);
        return (ulong)offsetField!.GetValue(placement)!;
    }

    // Helper to access compiled barriers via reflection
    private record CompiledPassInfo(string Name, bool Active, List<BarrierInfo> PreBarriers);

    private record BarrierInfo(
        ResourceState OldState,
        ResourceState NewState,
        uint FirstMipLevel,
        uint MipLevelCount,
        uint FirstArraySlice,
        uint ArraySliceCount
    );

    private List<CompiledPassInfo> GetCompiledPasses(RenderGraph graph)
    {
        var field = typeof(RenderGraph).GetField(
            "_compiledPasses",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var passes = (System.Collections.IList)field!.GetValue(graph)!;
        var result = new List<CompiledPassInfo>();

        foreach (var pass in passes)
        {
            var passType = pass!.GetType();
            var name = (string)
                passType
                    .GetProperty("Pass")!
                    .GetValue(pass)!
                    .GetType()
                    .GetProperty("Name")!
                    .GetValue(passType.GetProperty("Pass")!.GetValue(pass)!)!;
            var active = (bool)passType.GetProperty("Active")!.GetValue(pass)!;
            var preBarriers = (System.Collections.IList)
                passType.GetProperty("PreBarriers")!.GetValue(pass)!;

            var barriers = new List<BarrierInfo>();
            foreach (var barrier in preBarriers)
            {
                var bType = barrier!.GetType();
                barriers.Add(
                    new BarrierInfo(
                        (ResourceState)bType.GetProperty("OldState")!.GetValue(barrier)!,
                        (ResourceState)bType.GetProperty("NewState")!.GetValue(barrier)!,
                        (uint)bType.GetProperty("FirstMipLevel")!.GetValue(barrier)!,
                        (uint)bType.GetProperty("MipLevelCount")!.GetValue(barrier)!,
                        (uint)bType.GetProperty("FirstArraySlice")!.GetValue(barrier)!,
                        (uint)bType.GetProperty("ArraySliceCount")!.GetValue(barrier)!
                    )
                );
            }

            result.Add(new CompiledPassInfo(name, active, barriers));
        }

        return result;
    }

    private List<int> GetExecutionOrder(RenderGraph graph)
    {
        var field = typeof(RenderGraph).GetField(
            "_executionOrder",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        return (List<int>)field!.GetValue(graph)!;
    }
}
