namespace SomeEngine.Assets.Importers;

public sealed class GltfImporterSettings
{
    public const string DefaultLitMaterialTemplate = "assets/Materials/DefaultPBR.material.asset";
    public const string DefaultUnlitMaterialTemplate = "assets/Materials/TestUnlit_1.material.asset";

    public string LitMaterialTemplate { get; set; } = string.Empty;
    public string UnlitMaterialTemplate { get; set; } = string.Empty;
    public bool GenerateTangents { get; set; }

    public static GltfImporterSettings Default()
        => new()
        {
            LitMaterialTemplate = DefaultLitMaterialTemplate,
            UnlitMaterialTemplate = DefaultUnlitMaterialTemplate,
            GenerateTangents = false,
        };
}
