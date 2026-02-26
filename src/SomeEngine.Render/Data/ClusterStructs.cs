using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data
{
    /// <summary>
    /// Header for a streamed geometry page.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PageHeader
    {
        public uint ClusterCount;
        public uint TotalVertexCount;
        public uint TotalTriangleCount;
        public uint PageSize;

        public uint ClustersOffset;
        public uint PositionsOffset;
        public uint AttributesOffset;
        public uint IndicesOffset;
    }
}
