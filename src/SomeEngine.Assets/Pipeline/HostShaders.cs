using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class HostShaders
{
    public const string PostTonemapPath = "assets/Shaders/post_tonemap.slang";
    public const string ImGuiPath = "assets/Shaders/imgui.slang";

    public static ShaderAsset PostTonemap(AssetDatabase database)
        => Load(database, PostTonemapPath);

    public static ShaderAsset ImGui(AssetDatabase database)
        => Load(database, ImGuiPath);

    private static ShaderAsset Load(AssetDatabase database, string path)
    {
        ArgumentNullException.ThrowIfNull(database);
        AssetGuid? guid = database.Resolve(path);
        if (guid is not { IsEmpty: false } shaderGuid)
            throw new InvalidOperationException($"Required host shader '{path}' is not indexed.");

        return database.Load<ShaderAsset>(shaderGuid)
            ?? throw new InvalidOperationException($"Required host shader '{path}' references missing ShaderAsset '{shaderGuid}'.");
    }
}
