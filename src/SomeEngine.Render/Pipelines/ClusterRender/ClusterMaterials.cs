using System;
using Diligent;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Cluster 管线材质系统工具类。
/// 纯静态辅助方法——不持有状态、不注册材质。
/// </summary>
public static class ClusterMaterials
{
    /// <summary>
    /// 为材质设置默认纹理槽（Albedo / Normal / ARM + Sampler）。
    /// </summary>
    public static void SetupDefaultSlots(
        Material mat, ITexture albedo, ITexture normal,
        ITexture arm, ISampler sampler)
    {
        mat.SetTexture("AlbedoMap", albedo.GetDefaultView(TextureViewType.ShaderResource)!);
        mat.SetTexture("NormalMap", normal.GetDefaultView(TextureViewType.ShaderResource)!);
        mat.SetTexture("ARMMap", arm.GetDefaultView(TextureViewType.ShaderResource)!);
        mat.SetSampler("MaterialSampler", sampler);
    }

    /// <summary>
    /// 创建 1×1 RGBA8 默认纹理并立即转入 ShaderResource 状态。
    /// </summary>
    public static ITexture CreateDefault1x1Texture(RenderContext context, string name, uint rgba)
    {
        var texDesc = new TextureDesc
        {
            Name = name,
            Type = ResourceDimension.Tex2d,
            Width = 1,
            Height = 1,
            Format = TextureFormat.RGBA8_UNorm,
            Usage = Usage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };
        var data = new TextureData
        {
            SubResources =
            [
                new TextureSubResData
                {
                    Data = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(
                        new[] { rgba }, 0),
                    Stride = 4,
                },
            ],
        };
        var tex =
            context.Device!.CreateTexture(texDesc, data)
            ?? throw new InvalidOperationException($"Failed to create texture: {name}");

        StateTransitionDesc transition = new StateTransitionDesc
        {
            Resource = tex,
            OldState = ResourceState.Unknown,
            NewState = ResourceState.ShaderResource,
            Flags = StateTransitionFlags.UpdateState,
        };
        context.ImmediateContext!.TransitionResourceStates([transition]);

        return tex;
    }
}
