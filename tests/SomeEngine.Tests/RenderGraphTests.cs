using System.Collections.Generic;
using Diligent;
using NSubstitute;
using NUnit.Framework;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Tests;

[TestFixture]
public class RenderGraphTests
{
    private class EmptyData { public object? UserData; }

    [Test]
    public void TestPlacedResourceAliasing()
    {
        using var graph = new RenderGraph();
        var desc = new TextureDesc { Width = 1024, Height = 1024, Format = TextureFormat.RGBA8_UNorm, Type = ResourceDimension.Tex2d, BindFlags = BindFlags.RenderTarget };

        var tex1 = graph.CreateTexture("Tex1", desc);
        var tex2 = graph.CreateTexture("Tex2", desc);
        var tex3 = graph.CreateTexture("Tex3", desc);

        // Pass 1: Writes tex1
        graph.AddPass<EmptyData>("Pass1", (builder, data) =>
        {
            builder.WriteTexture(tex1);
        }, (ctx, data) => { });

        // Pass 2: Reads tex1, Writes tex2
        graph.AddPass<EmptyData>("Pass2", (builder, data) =>
        {
            builder.ReadTexture(tex1);
            builder.WriteTexture(tex2);
        }, (ctx, data) => { });

        // Pass 3: Reads tex2, Writes tex3
        graph.AddPass<EmptyData>("Pass3", (builder, data) =>
        {
            builder.ReadTexture(tex2);
            builder.WriteTexture(tex3);
        }, (ctx, data) => { });

        // Tex1: First=0, Last=1
        // Tex2: First=1, Last=2
        // Tex3: First=2, Last=2
        // Tex1 and Tex3 have non-overlapping lifetimes (Tex1 ends at Pass 1, Tex3 starts at Pass 2).

        RenderGraph.GetMemoryRequirementsDelegate mockGetReqs = (res) =>
        {
            return new MemoryRequirements { Size = 1024 * 1024, Alignment = 256, MemoryTypeBits = 0xFFFFFFFF };
        };

        graph.Compile(null, mockGetReqs);

        ulong offset1 = GetMemoryOffset(graph, tex1);
        ulong offset2 = GetMemoryOffset(graph, tex2);
        ulong offset3 = GetMemoryOffset(graph, tex3);

        Assert.That(offset1, Is.EqualTo(offset3), "Lifetimes don't overlap, should alias to same offset");
        Assert.That(offset1, Is.Not.EqualTo(offset2), "Lifetimes overlap, should have different offsets");
        Assert.That(offset2, Is.Not.EqualTo(offset3), "Lifetimes overlap, should have different offsets");
    }

    [Test]
    public void TestPlacedResourceAliasing_NoDevice()
    {
        using var graph = new RenderGraph();
        var desc = new TextureDesc { Width = 1024, Height = 1024, Format = TextureFormat.RGBA8_UNorm, Type = ResourceDimension.Tex2d, BindFlags = BindFlags.RenderTarget };

        var tex1 = graph.CreateTexture("Tex1", desc);

        graph.AddPass<EmptyData>("Pass1", (builder, data) =>
        {
            builder.WriteTexture(tex1);
        }, (ctx, data) => { });

        // Compile without device
        graph.Compile(null);

        ulong offset1 = GetMemoryOffset(graph, tex1);
        Assert.That(offset1, Is.EqualTo(ulong.MaxValue), "Should not allocate placed resource without device");
    }

    private ulong GetMemoryOffset(RenderGraph graph, RGResourceHandle handle)
    {
        var resourcesField = typeof(RenderGraph).GetField("_resources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var resources = (System.Collections.IList)resourcesField!.GetValue(graph)!;
        var res = resources[handle.Id];
        var prop = res!.GetType().GetProperty("MemoryOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prop != null)
            return (ulong)prop.GetValue(res)!;

        var field = res!.GetType().GetField("MemoryOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (ulong)field!.GetValue(res)!;
    }
}
