namespace SomeEngine.Render.Pipelines;

public enum ClusterDebugMode
{
    None = 0,
    ClusterID = 1,
    LODLevel = 2,
    SWHWView = 3,
    ShadingBin = 7,
    Barycentric = 8,
    Normal = 9,
    UV = 10,
}

public enum HiZMode
{
    Phase1Only,
    Phase1ThenHiZ,
    Full2Phase,
}
