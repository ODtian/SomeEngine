using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data
{
    /// <summary>
    /// Runtime mirror of MeshPageHeader (44 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PageHeader
    {
        public uint ClusterCount;
        public uint TotalVertexCount;
        public uint TotalTriangleCount;
        public float QuantOriginX;

        public uint ClustersOffset;
        public uint PositionsOffset;
        public uint AttributesOffset;
        public uint IndicesOffset;

        public float QuantOriginY;
        public float QuantOriginZ;
        public float QuantStep;
    }
}
