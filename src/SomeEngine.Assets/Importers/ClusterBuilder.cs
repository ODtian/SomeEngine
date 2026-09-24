using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Importers;

public sealed class ClusterBuilderOptions
{
    public bool GenerateMissingTangents { get; init; }
}

public struct ClusterLodConfig
{
    public int MaxVertices;
    public int MinTriangles;
    public int MaxTriangles;
    public bool PartitionSpatial;
    public bool PartitionSort;
    public int PartitionSize;
    public bool ClusterSpatial;
    public float ClusterFillWeight;
    public float ClusterSplitFactor;
    public float SimplifyRatio;
    public float SimplifyThreshold;
    public float SimplifyErrorFactorSloppy;
    public float SimplifyErrorEdgeLimit;
    public bool SimplifyPermissive;
    public bool SimplifyFallbackPermissive;
    public bool SimplifyFallbackSloppy;
    public bool SimplifyRegularize;
    public bool OptimizeBounds;
    public bool OptimizeClusters;

    public static ClusterLodConfig GetDefault(int maxTriangles = 124)
    {
        return new ClusterLodConfig
        {
            MaxVertices = 64,
            MinTriangles = maxTriangles / 3,
            MaxTriangles = maxTriangles,
            PartitionSpatial = true,
            PartitionSort = false,
            PartitionSize = 16,
            ClusterSpatial = false,
            ClusterFillWeight = 0.5f,
            ClusterSplitFactor = 2.0f,
            SimplifyRatio = 0.5f,
            SimplifyThreshold = 0.85f,
            SimplifyErrorFactorSloppy = 2.0f,
            SimplifyErrorEdgeLimit = 0.0f,
            SimplifyPermissive = true,
            SimplifyFallbackPermissive = false,
            SimplifyFallbackSloppy = true,
            SimplifyRegularize = false,
            OptimizeBounds = true,
            OptimizeClusters = true,
        };
    }
}

public static class ClusterBuilder
{
    private const int MaxVerticesPerMeshlet = 64;
    private const int MaxTrianglesPerMeshlet = 124;
    private const float ConeWeight = 0.0f;
    private const int GroupSize = 4;
    private const float SimplifyRatio = 0.5f;
    private const int PageSize = 128 * 1024; // 128KB
    private const int PageHeaderSize = MeshPageHeader.Size;
    private const int MaxEncodedTriangleStart = ushort.MaxValue;

    private struct BuilderMeshlet
    {
        public int IndicesOffset;
        public int IndicesCount;
        public int Level;
        public float Error;
        public float ParentError;
        public int GroupId;
        public int ParentGroupId;
        public Vector3 Center;
        public float Radius;
        public Vector3 LodCenter;
        public float LodRadius;
        public Vector3 SelfLodCenter;
        public float SelfLodRadius;
        public int VertexCount;

        public byte Mat0;
        public byte Mat1;
        public byte Mat2;
        public byte Range0End;
        public byte Range1End;

        // VRB batch info (packed uint, see BuildVrb)
        public uint VRBBatchInfo;
    }

    private struct ClusterLodBounds
    {
        public Vector3 Center;
        public float Radius;
        public float Error;
    }

    private struct MeshPageInfo
    {
        public uint ClusterCount;
        public uint TotalVertexCount;
        public uint TotalTriangleCount;
        public uint ClustersOffset;
        public uint PositionsOffset;
        public uint AttributesOffset;
        public uint IndicesOffset;
        public long FileOffset;
    }

    private struct ClusterInfo
    {
        public Vector3 BoundMin;
        public Vector3 BoundMax;
        public Vector4 LODSphere; // xyz: center, w: radius
        public float LODError;
        public uint PageIndex;
        public uint ClusterStart;
        public int ParentGroupId;
    }

    // Morton Code Helpers
    private static uint ExpandBits(uint v)
    {
        v = (v * 0x00010001u) & 0xFF0000FFu;
        v = (v * 0x00000101u) & 0x0F00F00Fu;
        v = (v * 0x00000011u) & 0xC30C30C3u;
        v = (v * 0x00000005u) & 0x49249249u;
        return v;
    }

    private static uint Morton3D(Vector3 p)
    {
        p.X = Math.Min(Math.Max(p.X * 1024.0f, 0.0f), 1023.0f);
        p.Y = Math.Min(Math.Max(p.Y * 1024.0f, 0.0f), 1023.0f);
        p.Z = Math.Min(Math.Max(p.Z * 1024.0f, 0.0f), 1023.0f);
        return ExpandBits((uint)p.X) * 4 + ExpandBits((uint)p.Y) * 2 + ExpandBits((uint)p.Z);
    }

    private static List<ClusterBVHNode> BuildBvh(List<ClusterInfo> clusters)
    {
        var nodes = new List<ClusterBVHNode>();
        if (clusters.Count == 0)
            return nodes;

        // Clusters are already sorted by Morton Code in ProcessRaw

        // 1. Create Leaf Nodes
        var currentLevelIndices = new List<int>();
        int i = 0;

        while (i < clusters.Count)
        {
            // Group clusters by ParentGroupId and PageIndex
            uint currentPage = clusters[i].PageIndex;
            int currentParent = clusters[i].ParentGroupId;

            int count = 0;
            while (
                i + count < clusters.Count
                && clusters[i + count].PageIndex == currentPage
                && clusters[i + count].ParentGroupId == currentParent
                && count < 128
            ) // Reasonable leaf size, but still grouping same parent
            {
                count++;
            }

            var node = new ClusterBVHNode();
            Vector3 bMin = new Vector3(float.MaxValue);
            Vector3 bMax = new Vector3(float.MinValue);

            for (int k = 0; k < count; ++k)
            {
                var c = clusters[i + k];
                bMin = Vector3.Min(bMin, c.BoundMin);
                bMax = Vector3.Max(bMax, c.BoundMax);
            }

            node.BoundMin = new Vector4(bMin, 0);
            node.BoundMax = new Vector4(bMax, 0);

            // Leaf node represents the parent state for LOD cutting
            // It MUST contain the LOD information of the shared parent of this cluster group

            node.LODSphere = clusters[i].LODSphere;
            node.LODError = clusters[i].LODError;

            // Leaf Node Pointer: Temporary PageIndex for Load-time resolution

            uint pageIndex = clusters[i].PageIndex;
            uint clusterStart = clusters[i].ClusterStart;
            node.ChildPointer = pageIndex;
            node.SetLeafData(clusterStart, (uint)count);
            node.NodeType = 1;

            nodes.Add(node);
            currentLevelIndices.Add(nodes.Count - 1);

            i += count;
        }

        // 2. Build Internal Levels
        while (currentLevelIndices.Count > 1) // Loop until we have a single root
        {
            var nextLevelIndices = new List<int>();
            for (int j = 0; j < currentLevelIndices.Count; j += 16)
            {
                int count = Math.Min(16, currentLevelIndices.Count - j);
                var node = new ClusterBVHNode();

                // Internal Node Pointer: Index in BVH Buffer

                node.ChildPointer = (uint)currentLevelIndices[j];
                node.ChildCount = (uint)count;
                node.NodeType = 0;

                Vector3 bMin = new Vector3(float.MaxValue);
                Vector3 bMax = new Vector3(float.MinValue);
                float maxError = 0;
                Vector3 centerSum = Vector3.Zero;

                for (int k = 0; k < count; ++k)
                {
                    var child = nodes[currentLevelIndices[j + k]];
                    var childMin = new Vector3(
                        child.BoundMin.X,
                        child.BoundMin.Y,
                        child.BoundMin.Z
                    );
                    var childMax = new Vector3(
                        child.BoundMax.X,
                        child.BoundMax.Y,
                        child.BoundMax.Z
                    );
                    bMin = Vector3.Min(bMin, childMin);
                    bMax = Vector3.Max(bMax, childMax);
                    maxError = Math.Max(maxError, child.LODError);
                    centerSum += new Vector3(
                        child.LODSphere.X,
                        child.LODSphere.Y,
                        child.LODSphere.Z
                    );
                }

                Vector3 center = centerSum / count;
                float maxRadius = 0;
                for (int k = 0; k < count; ++k)
                {
                    var child = nodes[currentLevelIndices[j + k]];
                    float d =
                        Vector3.Distance(
                            center,
                            new Vector3(child.LODSphere.X, child.LODSphere.Y, child.LODSphere.Z)
                        ) + child.LODSphere.W;
                    maxRadius = Math.Max(maxRadius, d);
                }

                node.BoundMin = new Vector4(bMin, 0);
                node.BoundMax = new Vector4(bMax, 0);
                node.LODSphere = new Vector4(center.X, center.Y, center.Z, maxRadius);
                node.LODError = maxError;

                nodes.Add(node);
                nextLevelIndices.Add(nodes.Count - 1);
            }
            currentLevelIndices = nextLevelIndices;
        }

        return nodes;
    }

    public static MeshAsset Process(string filePath, Func<string, AssetGuid>? materialGuidResolver = null)
    {
        var model = ModelRoot.Load(filePath);
        var mesh = model.LogicalMeshes[0];
        IReadOnlyList<MeshMaterialSlot> materialSlots = mesh.Primitives
            .Select(primitive => new MeshMaterialSlot(materialGuidResolver?.Invoke(primitive.Material?.Name ?? string.Empty) ?? AssetGuid.Empty))
            .ToArray();
        return ProcessMesh(mesh, materialSlots, mesh.Name ?? "Unnamed");
    }

    public static MeshAsset ProcessMesh(
        Mesh mesh,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        string name,
        ClusterBuilderOptions? options = null)
    {
        static float[] ReadFloats(Accessor accessor)
        {
            return accessor.Dimensions switch
            {
                DimensionType.SCALAR => accessor.AsScalarArray().ToArray(),
                DimensionType.VEC2 => ReadVector2Array(accessor),
                DimensionType.VEC3 => ReadVector3Array(accessor),
                DimensionType.VEC4 => ReadVector4Array(accessor),
                _ => throw new NotSupportedException($"Unsupported accessor dimension: {accessor.Dimensions}"),
            };
        }

        static float[] ReadVector2Array(Accessor accessor)
        {
            var values = accessor.AsVector2Array();
            float[] result = new float[checked(values.Count * 2)];
            for (int i = 0; i < values.Count; i++)
            {
                Vector2 value = values[i];
                int offset = i * 2;
                result[offset + 0] = value.X;
                result[offset + 1] = value.Y;
            }

            return result;
        }

        static float[] ReadVector3Array(Accessor accessor)
        {
            var values = accessor.AsVector3Array();
            float[] result = new float[checked(values.Count * 3)];
            for (int i = 0; i < values.Count; i++)
            {
                Vector3 value = values[i];
                int offset = i * 3;
                result[offset + 0] = value.X;
                result[offset + 1] = value.Y;
                result[offset + 2] = value.Z;
            }

            return result;
        }

        static float[] ReadVector4Array(Accessor accessor)
        {
            var values = accessor.AsVector4Array();
            float[] result = new float[checked(values.Count * 4)];
            for (int i = 0; i < values.Count; i++)
            {
                Vector4 value = values[i];
                int offset = i * 4;
                result[offset + 0] = value.X;
                result[offset + 1] = value.Y;
                result[offset + 2] = value.Z;
                result[offset + 3] = value.W;
            }

            return result;
        }

        var allPos = new List<Vector3>();
        var allIndices = new List<uint>();
        
        var templatePrimitive = mesh.Primitives[0];
        var combinedAttributes = new Dictionary<string, List<float>>();
        var attrDefinitions = new List<(string Name, int Dimension, ValueType TargetType, byte NumComponents, bool Normalized)>();

        foreach (var key in templatePrimitive.VertexAccessors.Keys)
        {
            if (key == "POSITION") continue;
            var accessor = templatePrimitive.GetVertexAccessor(key);
            int dimension = accessor.Dimensions switch {
                DimensionType.SCALAR => 1, DimensionType.VEC2 => 2, DimensionType.VEC3 => 3, DimensionType.VEC4 => 4, _ => 1,
            };
            ValueType targetType = ValueType.Float32;
            bool normalized = accessor.Normalized;
            if (key == "NORMAL" || key == "TANGENT") { targetType = ValueType.Int8; normalized = true; }
            else if (key.StartsWith("TEXCOORD")) { targetType = ValueType.Float16; }
            else if (key.StartsWith("COLOR")) { targetType = ValueType.UInt8; normalized = true; }
            else if (key.StartsWith("JOINTS")) { targetType = ValueType.UInt16; }
            else if (key.StartsWith("WEIGHTS")) { targetType = ValueType.UInt8; normalized = true; }

            attrDefinitions.Add((key, dimension, targetType, (byte)dimension, normalized));
            combinedAttributes[key] = new List<float>();
        }

        var combinedMaterialIndices = new List<float>();
        var normalizedMaterialSlots = new List<MeshMaterialSlot>();
        uint vertexOffset = 0;

        for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
        {
            var primitive = mesh.Primitives[primIdx];
            var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
            allPos.AddRange(positions);

            var indices16 = primitive.GetIndexAccessor().AsIndicesArray();
            for (int i = 0; i < indices16.Count; i++)
            {
                allIndices.Add((uint)(indices16[i] + vertexOffset));
            }

            foreach (var def in attrDefinitions)
            {
                // Fallback to zeros if primitive missing attribute (though invalid GLTF normally)
                if (primitive.VertexAccessors.TryGetValue(def.Name, out var accessor))
                {
                    combinedAttributes[def.Name].AddRange(ReadFloats(accessor));
                }
                else
                {
                    combinedAttributes[def.Name].AddRange(new float[positions.Length * def.Dimension]);
                }
            }

            normalizedMaterialSlots.Add(
                primIdx < materialSlots.Count
                    ? materialSlots[primIdx]
                    : new MeshMaterialSlot(AssetGuid.Empty));

            for (int i = 0; i < positions.Length; i++)
            {
                combinedMaterialIndices.Add(primIdx);
            }

            vertexOffset += (uint)positions.Length;
        }

        var rawAttributes = new List<RawAttribute>();
        foreach (var def in attrDefinitions)
        {
            rawAttributes.Add(new RawAttribute(def.Name, combinedAttributes[def.Name].ToArray(), def.Dimension, def.TargetType, def.NumComponents, def.Normalized));
        }

        Vector3[]? positionArray = null;
        uint[]? indexArray = null;
        if (options?.GenerateMissingTangents == true
            && !rawAttributes.Any(static attribute => attribute.Name == "TANGENT"))
        {
            positionArray = allPos.ToArray();
            indexArray = allIndices.ToArray();
            rawAttributes.Add(GenerateTangentAttribute(positionArray, indexArray, rawAttributes, name));
        }

        // Add _MATERIAL_INDEX. Values are integer floats (0.0f, 1.0f...). We will store them as UInt8.
        rawAttributes.Add(new RawAttribute("_MATERIAL_INDEX", combinedMaterialIndices.ToArray(), 1, ValueType.UInt8, 1, false));
        static int AttributeOrder(string name) => name switch
        {
            "NORMAL" => 0,
            "TANGENT" => 1,
            _ when name.StartsWith("TEXCOORD") => 2,
            _ when name.StartsWith("COLOR") => 3,
            _ when name.StartsWith("JOINTS") => 4,
            _ when name.StartsWith("WEIGHTS") => 5,
            "_MATERIAL_INDEX" => 98,
            _ => 6,
        };
        rawAttributes.Sort((a, b) =>
        {
            int orderA = AttributeOrder(a.Name);
            int orderB = AttributeOrder(b.Name);
            if (orderA != orderB) return orderA.CompareTo(orderB);
            return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return ProcessRaw(positionArray ?? allPos.ToArray(), rawAttributes, indexArray ?? allIndices.ToArray(), normalizedMaterialSlots, name);
    }

    private static RawAttribute GenerateTangentAttribute(
        Vector3[] positions,
        uint[] indices,
        IReadOnlyList<RawAttribute> rawAttributes,
        string meshName)
    {
        RawAttribute normal = FindRequiredAttribute(rawAttributes, "NORMAL", meshName);
        RawAttribute uv = FindRequiredAttribute(rawAttributes, "TEXCOORD_0", meshName);
        if (normal.Dimension != 3 || normal.Data.Length != positions.Length * 3)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': NORMAL must be vec3 and match vertex count.");
        }

        if (uv.Dimension != 2 || uv.Data.Length != positions.Length * 2)
        {
            throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': TEXCOORD_0 must be vec2 and match vertex count.");
        }

        Vector3[] tan1 = new Vector3[positions.Length];
        Vector3[] tan2 = new Vector3[positions.Length];
        for (int index = 0; index < indices.Length; index += 3)
        {
            int i0 = checked((int)indices[index + 0]);
            int i1 = checked((int)indices[index + 1]);
            int i2 = checked((int)indices[index + 2]);
            if ((uint)i0 >= positions.Length || (uint)i1 >= positions.Length || (uint)i2 >= positions.Length)
            {
                throw new InvalidOperationException(
                    $"Cannot generate TANGENT for mesh '{meshName}': triangle index references a missing vertex.");
            }

            Vector3 p0 = positions[i0];
            Vector3 p1 = positions[i1];
            Vector3 p2 = positions[i2];
            Vector2 w0 = ReadVector2(uv.Data, i0);
            Vector2 w1 = ReadVector2(uv.Data, i1);
            Vector2 w2 = ReadVector2(uv.Data, i2);

            float x1 = p1.X - p0.X;
            float x2 = p2.X - p0.X;
            float y1 = p1.Y - p0.Y;
            float y2 = p2.Y - p0.Y;
            float z1 = p1.Z - p0.Z;
            float z2 = p2.Z - p0.Z;
            float s1 = w1.X - w0.X;
            float s2 = w2.X - w0.X;
            float t1 = w1.Y - w0.Y;
            float t2 = w2.Y - w0.Y;
            float denominator = s1 * t2 - s2 * t1;
            if (denominator == 0.0f)
            {
                throw new InvalidOperationException(
                    $"Cannot generate TANGENT for mesh '{meshName}': triangle {index / 3} has degenerate TEXCOORD_0 parameterization.");
            }

            float r = 1.0f / denominator;
            Vector3 sdir = new(
                (t2 * x1 - t1 * x2) * r,
                (t2 * y1 - t1 * y2) * r,
                (t2 * z1 - t1 * z2) * r);
            Vector3 tdir = new(
                (s1 * x2 - s2 * x1) * r,
                (s1 * y2 - s2 * y1) * r,
                (s1 * z2 - s2 * z1) * r);

            tan1[i0] += sdir;
            tan1[i1] += sdir;
            tan1[i2] += sdir;
            tan2[i0] += tdir;
            tan2[i1] += tdir;
            tan2[i2] += tdir;
        }

        float[] tangents = new float[positions.Length * 4];
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 n = ReadVector3(normal.Data, vertex);
            if (n.LengthSquared() == 0.0f)
            {
                throw new InvalidOperationException(
                    $"Cannot generate TANGENT for mesh '{meshName}': vertex {vertex} has zero NORMAL.");
            }

            n = Vector3.Normalize(n);
            Vector3 tangent = tan1[vertex] - n * Vector3.Dot(n, tan1[vertex]);
            if (tangent.LengthSquared() == 0.0f)
            {
                throw new InvalidOperationException(
                    $"Cannot generate TANGENT for mesh '{meshName}': vertex {vertex} has no non-zero tangent contribution.");
            }

            tangent = Vector3.Normalize(tangent);
            float handedness = Vector3.Dot(Vector3.Cross(n, tangent), tan2[vertex]) < 0.0f
                ? -1.0f
                : 1.0f;
            int offset = vertex * 4;
            tangents[offset + 0] = tangent.X;
            tangents[offset + 1] = tangent.Y;
            tangents[offset + 2] = tangent.Z;
            tangents[offset + 3] = handedness;
        }

        return new RawAttribute("TANGENT", tangents, 4, ValueType.Int8, 4, true);
    }

    private static RawAttribute FindRequiredAttribute(
        IReadOnlyList<RawAttribute> rawAttributes,
        string name,
        string meshName)
        => rawAttributes.FirstOrDefault(attribute => attribute.Name == name)
            ?? throw new InvalidOperationException(
                $"Cannot generate TANGENT for mesh '{meshName}': required attribute '{name}' is missing.");

    private static Vector2 ReadVector2(float[] values, int vertex)
    {
        int offset = checked(vertex * 2);
        return new Vector2(values[offset + 0], values[offset + 1]);
    }

    private static Vector3 ReadVector3(float[] values, int vertex)
    {
        int offset = checked(vertex * 3);
        return new Vector3(values[offset + 0], values[offset + 1], values[offset + 2]);
    }

    public static MeshAsset ProcessRaw(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        string name)
    {
        List<string> regionNames = materialSlots
            .Select(static (slot, index) => $"region_{index}")
            .ToList();
        return ProcessRaw(
            rawPos,
            rawAttributes,
            rawIndices,
            regionNames,
            name);
    }

    private static void BuildClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        var locks = ArrayPool<byte>.Shared.Rent(positions.Length);
        var remap = ArrayPool<uint>.Shared.Rent(positions.Length);

        try
        {
            var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
            Meshopt.GeneratePositionRemap(
                remap.AsSpan(0, positions.Length),
                posSpan,
                (nuint)Unsafe.SizeOf<Vector3>()
            );

            Clusterize(config, indices, positions, materialIndicesArray, clusters, globalIndices);
            int nextGroupId = 0;

            for (int i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                // We need indices for bounds.
                // To avoid allocation, we loop.
                // But BoundsCompute takes Span.
                // We can use CollectionsMarshal.AsSpan(globalIndices).Slice(...)
                // BUT we added to globalIndices in Clusterize, so it might have
                // reallocated. It is safe to take span here as we are not adding
                // now.
                var currentSpan = CollectionsMarshal
                    .AsSpan(globalIndices)
                    .Slice(c.IndicesOffset, c.IndicesCount);

                var b = BoundsCompute(positions, currentSpan, 0);
                c.Center = b.Center;
                c.Radius = b.Radius;
                c.LodCenter = b.Center;
                c.LodRadius = b.Radius;
                c.SelfLodCenter = b.Center;
                c.SelfLodRadius = b.Radius;
                c.Error = 0;
                c.Level = 0;
                c.GroupId = nextGroupId++;
                clusters[i] = c;
            }

            var pending = new List<int>();
            for (int i = 0; i < clusters.Count; i++)
                pending.Add(i);

            int depth = 0;
            var groupOffsets = new List<int>();
            var mergedIndices = new List<uint>();
            var simplifiedIndices = new List<uint>();

            while (pending.Count > 1)
            {
                Partition(
                    config,
                    positions,
                    clusters,
                    globalIndices,
                    pending,
                    remap.AsSpan(0, positions.Length),
                    groupOffsets
                );

                LockBoundary(
                    locks.AsSpan(0, positions.Length),
                    clusters,
                    globalIndices,
                    pending,
                    groupOffsets,
                    remap.AsSpan(0, positions.Length)
                );

                var nextPending = new List<int>();
                var pendingSpan = CollectionsMarshal.AsSpan(pending);

                for (int g = 0; g < groupOffsets.Count - 1; g++)
                {
                    int start = groupOffsets[g];
                    int count = groupOffsets[g + 1] - start;
                    var group = pendingSpan.Slice(start, count);

                    mergedIndices.Clear();

                    var currentGlobalSpan = CollectionsMarshal.AsSpan(globalIndices);
                    foreach (int idx in group)
                    {
                        var c = clusters[idx];
                        var cInds = currentGlobalSpan.Slice(c.IndicesOffset, c.IndicesCount);
                        for (int k = 0; k < cInds.Length; k++)
                            mergedIndices.Add(cInds[k]);
                    }

                    int targetSize = (int)((mergedIndices.Count / 3) * config.SimplifyRatio) * 3;
                    var groupBounds = BoundsMerge(clusters, group);

                    float error = 0;
                    simplifiedIndices.Clear();
                    Simplify(
                        config,
                        positions,
                        CollectionsMarshal.AsSpan(mergedIndices),
                        locks.AsSpan(0, positions.Length),
                        targetSize,
                        out error,
                        simplifiedIndices
                    );

                    if (simplifiedIndices.Count > mergedIndices.Count * config.SimplifyThreshold)
                    {
                        foreach (int idx in group)
                        {
                            var c = clusters[idx];
                            c.ParentError = float.MaxValue;
                            clusters[idx] = c;
                        }
                        continue;
                    }

                    float groupError = groupBounds.Error + error;
                    int thisGroupId = nextGroupId++;

                    foreach (int idx in group)
                    {
                        var c = clusters[idx];
                        c.ParentError = groupError;
                        c.ParentGroupId = thisGroupId;
                        c.LodCenter = groupBounds.Center;
                        c.LodRadius = groupBounds.Radius;
                        clusters[idx] = c;
                    }

                    int newClustersStart = clusters.Count;

                    // Clusterize adds to globalIndices, invalidating
                    // currentGlobalSpan!
                    Clusterize(
                        config,
                        CollectionsMarshal.AsSpan(simplifiedIndices),
                        positions,
                        materialIndicesArray,
                        clusters,
                        globalIndices
                    );

                    int newClustersEnd = clusters.Count;

                    for (int k = newClustersStart; k < newClustersEnd; k++)
                    {
                        var sc = clusters[k];

                        // Compute tight geometry bounds for the new parent cluster

                        var cInds = CollectionsMarshal
                            .AsSpan(globalIndices)
                            .Slice(sc.IndicesOffset, sc.IndicesCount);
                        var b = BoundsCompute(positions, cInds, 0);

                        sc.Level = depth + 1;
                        sc.Center = b.Center;
                        sc.Radius = b.Radius;
                        sc.Error = groupError;
                        sc.GroupId = thisGroupId;
                        sc.LodCenter = groupBounds.Center;
                        sc.LodRadius = groupBounds.Radius;
                        sc.SelfLodCenter = groupBounds.Center;
                        sc.SelfLodRadius = groupBounds.Radius;
                        clusters[k] = sc;
                        nextPending.Add(k);
                    }
                }
                pending = nextPending;
                depth++;
            }

            if (pending.Count == 1)
            {
                var c = clusters[pending[0]];
                c.ParentError = float.MaxValue;
                clusters[pending[0]] = c;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(locks);
            ArrayPool<uint>.Shared.Return(remap);
        }
    }

    public static MeshAsset ProcessRaw(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        List<string> regionNames,
        string name
    )
    {
        string tempFile = Path.GetTempFileName();
        using var fs = new FileStream(
            tempFile,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose
        );
        byte[] reusablePageBuffer = ArrayPool<byte>.Shared.Rent(PageSize + 65536);

        // 1. Meshopt Logic
        var remap = ArrayPool<uint>.Shared.Rent(rawPos.Length);
        Vector3[]? pPos = null;
        uint[]? pInd = null;
        List<RawAttribute>? pAttributes = null;
        List<float[]>? pAttributeBuffers = null;

        try
        {
            nuint vertexCount = BuildRemap(
                remap.AsSpan(0, rawPos.Length),
                rawIndices.AsSpan(),
                rawPos,
                rawAttributes);

            pPos = ArrayPool<Vector3>.Shared.Rent((int)vertexCount);
            pInd = ArrayPool<uint>.Shared.Rent(rawIndices.Length);

            // Manual Vertex Remap
            for (int oldIndex = 0; oldIndex < rawPos.Length; oldIndex++)
            {
                uint newIndex = remap[oldIndex];
                if (newIndex == uint.MaxValue || newIndex >= vertexCount)
                    continue;

                pPos[newIndex] = rawPos[oldIndex];
            }

            // Remap Indices
            Meshopt.RemapIndexBuffer(
                pInd.AsSpan(0, rawIndices.Length),
                rawIndices.AsSpan(),
                remap.AsSpan(0, rawPos.Length)
            );

            // Remap Attributes
            pAttributes = new List<RawAttribute>();
            pAttributeBuffers = new List<float[]>();

            foreach (var attr in rawAttributes)
            {
                var newData = ArrayPool<float>.Shared.Rent((int)vertexCount * attr.Dimension);
                pAttributeBuffers.Add(newData);

                int dim = attr.Dimension;
                var srcData = attr.Data;

                for (int oldIndex = 0; oldIndex < rawPos.Length; oldIndex++)
                {
                    uint newIndex = remap[oldIndex];
                    if (newIndex == uint.MaxValue || newIndex >= vertexCount)
                        continue;

                    int srcBase = oldIndex * dim;
                    int dstBase = (int)newIndex * dim;

                    for (int k = 0; k < dim; ++k)
                        newData[dstBase + k] = srcData[srcBase + k];
                }
                pAttributes.Add(
                    new RawAttribute(
                        attr.Name,
                        newData,
                        attr.Dimension,
                        attr.TargetType,
                        attr.NumComponents,
                        attr.Normalized
                    )
                );
            }

            Meshopt.OptimizeVertexCache(
                pInd.AsSpan(0, rawIndices.Length),
                pInd.AsSpan(0, rawIndices.Length),
                vertexCount
            );

            var finalAttributes = pAttributes;
            // pPos is finalPositions (array)

            // Build Cluster LOD hierarchy
            var allMeshlets = new List<BuilderMeshlet>();
            var globalIndices = new List<uint>();
            
            var matIndexAttr = pAttributes.FirstOrDefault(a => a.Name == "_MATERIAL_INDEX");
            float[] materialIndicesArray = matIndexAttr?.Data ?? new float[vertexCount];

            BuildClusterLod(
                ClusterLodConfig.GetDefault() with
                {
                    ClusterSpatial = true,
                },
                new ReadOnlySpan<Vector3>(pPos, 0, (int)vertexCount),
                new ReadOnlySpan<uint>(pInd, 0, rawIndices.Length),
                materialIndicesArray,
                allMeshlets,
                globalIndices
            );

            // Remove internal attribute before serialization
            if (matIndexAttr != null)
            {
                finalAttributes.Remove(matIndexAttr);
            }

            // Compute Bounds for Morton Code + Global Quantization
            Vector3 sceneMin = new Vector3(float.MaxValue);
            Vector3 sceneMax = new Vector3(float.MinValue);
            for (int i = 0; i < (int)vertexCount; ++i)
            {
                sceneMin = Vector3.Min(sceneMin, pPos[i]);
                sceneMax = Vector3.Max(sceneMax, pPos[i]);
            }
            Vector3 sceneExtent = sceneMax - sceneMin;
            sceneExtent = Vector3.Max(sceneExtent, new Vector3(1e-6f));

            // Global Quantization: power-of-2 step size for watertight vertex decode
            float maxExtent = Math.Max(sceneExtent.X, Math.Max(sceneExtent.Y, sceneExtent.Z));
            float quantStep = MathF.Pow(2, MathF.Ceiling(MathF.Log2(maxExtent / 65535f)));
            if (quantStep < 1e-12f) quantStep = 1e-12f; // Safety floor
            Vector3 quantOrigin = sceneMin;

            // Sort clusters by PageIndex, then ParentGroupId, then Morton Code
            // This ensures consistent grouping in the BVH leaf nodes
            allMeshlets.Sort(
                (a, b) =>
                {
                    // Note: We don't have PageIndex here yet, but clusters are already
                    // in the order they will be assigned to pages if we don't sort here.
                    // Actually, the page generation loop uses the order of allMeshlets.
                    // So we should sort by ParentGroupId first to group them for BVH.

                    if (a.ParentGroupId != b.ParentGroupId)
                        return a.ParentGroupId.CompareTo(b.ParentGroupId);

                    uint codeA = Morton3D((a.LodCenter - sceneMin) / sceneExtent);
                    uint codeB = Morton3D((b.LodCenter - sceneMin) / sceneExtent);
                    return codeA.CompareTo(codeB);
                }
            );

            // 2. Page Generation & Quantization
            var pagesDataList = new List<MeshPageInfo>();
            var clusterInfos = new List<ClusterInfo>();

            var currentClusters = new List<GPUCluster>();
            var currentPositions = new List<ushort>();
            var currentStreams = new List<byte>[finalAttributes.Count];
            for (int s = 0; s < finalAttributes.Count; s++)
                currentStreams[s] = new List<byte>();
            var currentIndices = new List<byte>();

            int currentBytes = PageHeaderSize;

            // Build Layout
            var descriptors = new List<VertexAttributeDescriptor>();
            int vertexStride = 0;
            for (int ai = 0; ai < finalAttributes.Count; ai++)
            {
                var attr = finalAttributes[ai];
                var desc = new VertexAttributeDescriptor
                {
                    Name = attr.Name,
                    Type = attr.TargetType,
                    NumComponents = attr.NumComponents,
                    IsNormalized = attr.Normalized,
                    StreamIndex = (ushort)ai,
                };
                int size = desc.GetSize();
                vertexStride += size;
                descriptors.Add(desc);
            }
            void FlushPage()
            {
                if (currentClusters.Count == 0)
                    return;

                uint clustersOffset = (uint)PageHeaderSize;
                int clustersSize = currentClusters.Count * Unsafe.SizeOf<GPUCluster>();

                uint positionsOffset = clustersOffset + (uint)clustersSize;
                int positionsSize = currentPositions.Count * sizeof(ushort);

                uint attributesOffset = positionsOffset + (uint)positionsSize;
                int attrsSize = 0;
                for (int s = 0; s < currentStreams.Length; s++)
                    attrsSize += currentStreams[s].Count;

                uint indicesOffset = attributesOffset + (uint)attrsSize;
                int indicesSize = currentIndices.Count;

                int totalSize = (int)indicesOffset + indicesSize;
                if (totalSize > reusablePageBuffer.Length)
                {
                    throw new Exception(
                        $"Page buffer overflow: {totalSize} > {reusablePageBuffer.Length}"
                    );
                }

                Array.Clear(reusablePageBuffer, 0, totalSize);
                var span = new Span<byte>(reusablePageBuffer);

                ref var header = ref Unsafe.As<byte, MeshPageHeader>(ref span[0]);
                header.ClusterCount = (uint)currentClusters.Count;
                header.TotalVertexCount = (uint)(currentPositions.Count / 3);
                header.TotalTriangleCount = (uint)(currentIndices.Count / 3);
                header.QuantOriginX = quantOrigin.X;
                header.ClustersOffset = clustersOffset;
                header.PositionsOffset = positionsOffset;
                header.AttributesOffset = attributesOffset;
                header.IndicesOffset = indicesOffset;
                header.QuantOriginY = quantOrigin.Y;
                header.QuantOriginZ = quantOrigin.Z;
                header.QuantStep = quantStep;

                MemoryMarshal
                    .Cast<GPUCluster, byte>(CollectionsMarshal.AsSpan(currentClusters))
                    .CopyTo(span.Slice((int)clustersOffset, clustersSize));
                MemoryMarshal
                    .Cast<ushort, byte>(CollectionsMarshal.AsSpan(currentPositions))
                    .CopyTo(span.Slice((int)positionsOffset, positionsSize));
                int streamWriteOffset = (int)attributesOffset;
                for (int s = 0; s < currentStreams.Length; s++)
                {
                    var streamSpan = CollectionsMarshal.AsSpan(currentStreams[s]);
                    streamSpan.CopyTo(span.Slice(streamWriteOffset, streamSpan.Length));
                    streamWriteOffset += streamSpan.Length;
                }
                CollectionsMarshal
                    .AsSpan(currentIndices)
                    .CopyTo(span.Slice((int)indicesOffset, indicesSize));

                fs.Write(reusablePageBuffer, 0, totalSize);

                pagesDataList.Add(
                    new MeshPageInfo
                    {
                        ClusterCount = (uint)currentClusters.Count,
                        TotalVertexCount = (uint)(currentPositions.Count / 3),
                        TotalTriangleCount = (uint)(currentIndices.Count / 3),
                        ClustersOffset = clustersOffset,
                        PositionsOffset = positionsOffset,
                        AttributesOffset = attributesOffset,
                        IndicesOffset = indicesOffset,
                        FileOffset = fs.Position - totalSize,
                    }
                );

                currentClusters.Clear();
                currentPositions.Clear();
                for (int s = 0; s < currentStreams.Length; s++)
                    currentStreams[s].Clear();
                currentIndices.Clear();
                currentBytes = PageHeaderSize;
            }

            var usedMap = new Dictionary<uint, ushort>(MaxVerticesPerMeshlet);
            var localPos = new List<ushort>(MaxVerticesPerMeshlet * 3);
            var localIndices = new List<byte>(MaxTrianglesPerMeshlet * 3);
            var localStreamBytes = new List<byte>[finalAttributes.Count];
            for (int s = 0; s < finalAttributes.Count; s++)
                localStreamBytes[s] = new List<byte>(MaxVerticesPerMeshlet * descriptors[s].GetSize());

            var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

            foreach (var m in allMeshlets)
            {
                int vCount = 0;
                var mIndices = globalIndicesSpan.Slice(m.IndicesOffset, m.IndicesCount);

                usedMap.Clear();
                localPos.Clear();
                localIndices.Clear();
                for (int s = 0; s < finalAttributes.Count; s++)
                    localStreamBytes[s].Clear();

                // --- Phase 1: Compute IntBase (min global integer coord in this cluster) ---
                int minGx = int.MaxValue, minGy = int.MaxValue, minGz = int.MaxValue;
                int maxGx = int.MinValue, maxGy = int.MinValue, maxGz = int.MinValue;

                foreach (var globalIdx in mIndices)
                {
                    Vector3 p = pPos[(int)globalIdx];

                    int gx = (int)MathF.Round((p.X - quantOrigin.X) / quantStep);
                    int gy = (int)MathF.Round((p.Y - quantOrigin.Y) / quantStep);
                    int gz = (int)MathF.Round((p.Z - quantOrigin.Z) / quantStep);
                    minGx = Math.Min(minGx, gx);
                    minGy = Math.Min(minGy, gy);
                    minGz = Math.Min(minGz, gz);
                    maxGx = Math.Max(maxGx, gx);
                    maxGy = Math.Max(maxGy, gy);
                    maxGz = Math.Max(maxGz, gz);
                }

                int clusterIntBaseX = minGx;
                int clusterIntBaseY = minGy;
                int clusterIntBaseZ = minGz;
                Vector3 decodedMin = new(float.MaxValue);
                Vector3 decodedMax = new(float.MinValue);

                // --- Phase 2: Encode vertices as local u16 offsets from IntBase ---
                foreach (var globalIdx in mIndices)
                {
                    if (!usedMap.TryGetValue(globalIdx, out ushort localIdx))
                    {
                        localIdx = (ushort)vCount;
                        usedMap[globalIdx] = localIdx;
                        vCount++;

                        Vector3 p = pPos[(int)globalIdx];
                        int gx = (int)MathF.Round((p.X - quantOrigin.X) / quantStep);
                        int gy = (int)MathF.Round((p.Y - quantOrigin.Y) / quantStep);
                        int gz = (int)MathF.Round((p.Z - quantOrigin.Z) / quantStep);

                        int lx = gx - clusterIntBaseX;
                        int ly = gy - clusterIntBaseY;
                        int lz = gz - clusterIntBaseZ;
                        if ((uint)lx > ushort.MaxValue || (uint)ly > ushort.MaxValue || (uint)lz > ushort.MaxValue)
                            throw new InvalidOperationException(
                                $"Cluster local quantized position exceeds encoded range: ({lx}, {ly}, {lz}).");

                        ushort qx = (ushort)lx;
                        ushort qy = (ushort)ly;
                        ushort qz = (ushort)lz;

                        localPos.Add(qx);
                        localPos.Add(qy);
                        localPos.Add(qz);
                        var decoded = new Vector3(
                            gx * quantStep + quantOrigin.X,
                            gy * quantStep + quantOrigin.Y,
                            gz * quantStep + quantOrigin.Z);
                        decodedMin = Vector3.Min(decodedMin, decoded);
                        decodedMax = Vector3.Max(decodedMax, decoded);

                        for (int i = 0; i < finalAttributes.Count; ++i)
                        {
                            PackAttribute(localStreamBytes[i], finalAttributes[i], (int)globalIdx);
                        }
                    }
                    localIndices.Add((byte)localIdx);
                }

                // ── VRB full simulation: mirrors shader DeduplicateVertIndexes ──
                // ─── VRB Batch Validation ───
                // Validate using actual batch boundaries from VRBBatchInfo (variable-size batches),
                // NOT the old fixed 32-tri windows. Batches beyond the encoded 5 are slow residual
                // (allowed to exceed 32 unique verts).
                {
                    int triCount = localIndices.Count / 3;
                    var idxSpan = CollectionsMarshal.AsSpan(localIndices);
                    uint vrb = m.VRBBatchInfo;
                    int encodedBatchCount = (int)((vrb >> 25) & 0x7) + 1;

                    int batchStart = 0;
                    for (int bi = 0; bi < encodedBatchCount; bi++)
                    {
                        int batchTriCount = (int)((vrb >> (bi * 5)) & 0x1F) + 1;
                        int batchEnd = batchStart + batchTriCount;
                        if (batchEnd > triCount) batchEnd = triCount;

                        // 1. baseVertIndex = min local index in batch
                        uint baseVert = 255;
                        for (int ti = batchStart; ti < batchEnd; ti++)
                        {
                            baseVert = Math.Min(baseVert, (uint)idxSpan[ti * 3 + 0]);
                            baseVert = Math.Min(baseVert, (uint)idxSpan[ti * 3 + 1]);
                            baseVert = Math.Min(baseVert, (uint)idxSpan[ti * 3 + 2]);
                        }

                        // 2. Build usedVertMask (64-bit bitmask)
                        ulong usedMask = 0;
                        for (int ti = batchStart; ti < batchEnd; ti++)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                uint rebased = (uint)idxSpan[ti * 3 + c] - baseVert;
                                if (rebased >= 64)
                                    throw new Exception(
                                        $"[VRB] SPAN: rebased={rebased} (>=64) batch {bi}, " +
                                        $"cluster tris={triCount} verts={vCount}");
                                usedMask |= 1UL << (int)rebased;
                            }
                        }

                        int numUnique = BitOperations.PopCount(usedMask);
                        if (numUnique > 32)
                            throw new Exception(
                                $"[VRB] UNIQUE VERT OVERFLOW: batch {bi} [{batchStart}..{batchEnd}) " +
                                $"has {numUnique} unique local verts (max 32). " +
                                $"cluster tris={triCount} verts={vCount}.");

                        batchStart = batchEnd;
                    }
                    // Remaining tris beyond encoded batches = slow residual, no VRB constraint
                }

                int clusterSize = Unsafe.SizeOf<GPUCluster>();
                int vSize = localPos.Count * 2;
                int aSize = 0;
                for (int s = 0; s < finalAttributes.Count; s++)
                    aSize += localStreamBytes[s].Count;
                int iSize = localIndices.Count;
                int totalAdded = clusterSize + vSize + aSize + iSize;

                if (currentBytes + totalAdded > PageSize
                    || currentIndices.Count > MaxEncodedTriangleStart)
                {
                    FlushPage();
                }

                uint vStart = (uint)(currentPositions.Count / 3);
                uint tStart = (uint)currentIndices.Count;
                if (vStart > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"Cluster page vertex start exceeds encoded range: {vStart} > {ushort.MaxValue}");
                if (tStart > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"Cluster page triangle start exceeds encoded range: {tStart} > {ushort.MaxValue}");
                if (currentClusters.Count > 0xFFF)
                    throw new InvalidOperationException(
                        $"Cluster page cluster start exceeds BVH leaf encoding range: {currentClusters.Count} > 4095");

                currentPositions.AddRange(localPos);
                for (int s = 0; s < finalAttributes.Count; s++)
                    currentStreams[s].AddRange(localStreamBytes[s]);
                currentIndices.AddRange(localIndices);
                currentBytes += totalAdded;

                // --- Pack CenterOffset and RadiusQuant for culling ---
                Vector3 center = m.Center;
                float radius = m.Radius;
                if (radius < 1e-6f) radius = quantStep;

                // CenterOffset in global integer grid, relative to IntBase
                int centerGx = (int)MathF.Round((center.X - quantOrigin.X) / quantStep);
                int centerGy = (int)MathF.Round((center.Y - quantOrigin.Y) / quantStep);
                int centerGz = (int)MathF.Round((center.Z - quantOrigin.Z) / quantStep);
                ushort centerOffX = (ushort)Math.Clamp(centerGx - clusterIntBaseX, 0, 65535);
                ushort centerOffY = (ushort)Math.Clamp(centerGy - clusterIntBaseY, 0, 65535);
                ushort centerOffZ = (ushort)Math.Clamp(centerGz - clusterIntBaseZ, 0, 65535);

                // RadiusQuant: round UP to be conservative
                // Add half-diagonal quantization error to compensate for center quantization
                float centerQuantError = quantStep * MathF.Sqrt(3.0f) * 0.5f;
                ushort radiusQuant = (ushort)Math.Clamp(
                    (int)MathF.Ceiling((radius + centerQuantError) / quantStep), 1, 65535);

                // LODError → float16
                ushort lodErrorHalf = BitConverter.HalfToUInt16Bits((Half)m.Error);

                uint packedCounts = (uint)vCount
                    | ((uint)(localIndices.Count / 3) << 8)
                    | ((uint)(byte)m.Level << 16);

                uint packedMaterials = (uint)m.Mat0
                    | ((uint)m.Mat1 << 8)
                    | ((uint)m.Mat2 << 16);
                
                uint packedRanges = (uint)m.Range0End
                    | ((uint)m.Range1End << 8);

                currentClusters.Add(
                    new GPUCluster
                    {
                        IntBaseX = clusterIntBaseX,
                        IntBaseY = clusterIntBaseY,
                        IntBaseZ = clusterIntBaseZ,
                        PackedCenterXY = GPUCluster.PackU16(centerOffX, centerOffY),
                        LODCenter = m.SelfLodCenter,
                        LODRadius = m.SelfLodRadius,
                        PackedCenterZRadius = GPUCluster.PackU16(centerOffZ, radiusQuant),
                        LODErrorHalf = lodErrorHalf,
                        VertexStart = (ushort)vStart,
                        TriangleStart = (ushort)tStart,
                        GroupId = (short)m.GroupId,
                        PackedCounts = packedCounts,
                        PackedMaterials = packedMaterials,
                        PackedRanges = packedRanges,
                        MaterialTableOffset = 0xFFFFFFFF, // fast path (≤3 materials)
                        VRBBatchInfo = m.VRBBatchInfo,
                        BoundMin = decodedMin,
                        BoundMax = decodedMax,
                    }
                );

                clusterInfos.Add(
                    new ClusterInfo
                    {
                        BoundMin = decodedMin,
                        BoundMax = decodedMax,
                        LODSphere = new Vector4(
                            m.LodCenter.X,
                            m.LodCenter.Y,
                            m.LodCenter.Z,
                            m.LodRadius
                        ),
                        LODError = (float)(Half)m.ParentError,
                        PageIndex = (uint)pagesDataList.Count,
                        ClusterStart = (uint)(currentClusters.Count - 1),
                        ParentGroupId = m.ParentGroupId,
                    }
                );
            }

            FlushPage();

            var bvhNodes = BuildBvh(clusterInfos);
            long bvhOffset = fs.Position;
            var bvhSpan = CollectionsMarshal.AsSpan(bvhNodes);
            var bvhBytes = MemoryMarshal.Cast<ClusterBVHNode, byte>(bvhSpan);
            fs.Write(bvhBytes);

            var schemaAttrs = new SomeEngine.Assets.Schema.VertexAttribute[descriptors.Count];
            for (int i = 0; i < descriptors.Count; ++i)
            {
                schemaAttrs[i] = new SomeEngine.Assets.Schema.VertexAttribute()
                {
                    Name = descriptors[i].Name,
                    Type = (SomeEngine.Assets.Schema.ValueType)descriptors[i].Type,
                    Components = descriptors[i].NumComponents,
                    Normalized = descriptors[i].IsNormalized,
                    Offset = descriptors[i].StreamIndex,
                };
            }

            MeshRegion[] meshRegions = regionNames
                .Select(regionName => new MeshRegion { Name = regionName })
                .ToArray();

            var meshAsset = new MeshAsset
            {
                Name = name,
                Bounds = new SomeEngine.Assets.Schema.Bounds()
                {
                    Center = new SomeEngine.Assets.Schema.Vec3()
                    {
                        X = (sceneMin.X + sceneMax.X) * 0.5f,
                        Y = (sceneMin.Y + sceneMax.Y) * 0.5f,
                        Z = (sceneMin.Z + sceneMax.Z) * 0.5f,
                    },
                    Radius = maxExtent * 0.5f,
                },
                Payload = new byte[fs.Length],
                Attributes = schemaAttrs,
                BvhOffset = (ulong)bvhOffset,
                QuantOrigin = new SomeEngine.Assets.Schema.Vec3()
                {
                    X = quantOrigin.X,
                    Y = quantOrigin.Y,
                    Z = quantOrigin.Z,
                },
                QuantStep = quantStep,
                Regions = meshRegions,
            };

            fs.Seek(0, SeekOrigin.Begin);
            fs.ReadExactly(meshAsset.Payload.Value.Span);

            return meshAsset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(reusablePageBuffer);
            ArrayPool<uint>.Shared.Return(remap);
            if (pPos != null)
                ArrayPool<Vector3>.Shared.Return(pPos);
            if (pInd != null)
                ArrayPool<uint>.Shared.Return(pInd);
            if (pAttributeBuffers != null)
            {
                foreach (var buf in pAttributeBuffers)
                    ArrayPool<float>.Shared.Return(buf);
            }
        }
    }

    private static void PackAttribute(List<byte> output, RawAttribute attr, int index)
    {
        int baseIdx = index * attr.Dimension;

        for (int c = 0; c < attr.NumComponents; ++c)
        {
            float val = (c < attr.Dimension) ? attr.Data[baseIdx + c] : 0.0f;

            switch (attr.TargetType)
            {
                case ValueType.Int8:
                    if (attr.Normalized)
                        output.Add((byte)(sbyte)Math.Clamp(val * 127.0f, -128, 127));
                    else
                        output.Add((byte)(sbyte)Math.Clamp(val, -128, 127));
                    break;
                case ValueType.UInt8:
                    if (attr.Normalized)
                        output.Add((byte)Math.Clamp(val * 255.0f, 0, 255));
                    else
                        output.Add((byte)Math.Clamp(val, 0, 255));
                    break;
                case ValueType.Int16:
                    short s = attr.Normalized
                        ? (short)Math.Clamp(val * 32767.0f, -32768, 32767)
                        : (short)val;
                    output.Add((byte)(s & 0xFF));
                    output.Add((byte)((s >> 8) & 0xFF));
                    break;
                case ValueType.UInt16:
                    ushort us = attr.Normalized
                        ? (ushort)Math.Clamp(val * 65535.0f, 0, 65535)
                        : (ushort)val;
                    output.Add((byte)(us & 0xFF));
                    output.Add((byte)((us >> 8) & 0xFF));
                    break;
                case ValueType.Float16:
                    Half h = (Half)val;
                    ushort hs = BitConverter.HalfToUInt16Bits(h);
                    output.Add((byte)(hs & 0xFF));
                    output.Add((byte)((hs >> 8) & 0xFF));
                    break;
                case ValueType.Float32:
                    unsafe
                    {
                        uint u = *(uint*)&val;
                        output.Add((byte)(u & 0xFF));
                        output.Add((byte)((u >> 8) & 0xFF));
                        output.Add((byte)((u >> 16) & 0xFF));
                        output.Add((byte)((u >> 24) & 0xFF));
                    }
                    break;
                // TODO: Other types
            }
        }
    }

    private static unsafe nuint BuildRemap(
        Span<uint> destination,
        ReadOnlySpan<uint> indices,
        Vector3[] positions,
        IReadOnlyList<RawAttribute> attributes)
    {
        if (destination.Length < positions.Length)
            throw new ArgumentException("Vertex remap destination is smaller than the source vertex count.", nameof(destination));

        var streams = new MeshOptimizer.Stream[checked(attributes.Count + 1)];
        var handles = new GCHandle[streams.Length];

        try
        {
            handles[0] = GCHandle.Alloc(positions, GCHandleType.Pinned);
            streams[0] = new MeshOptimizer.Stream(
                handles[0].AddrOfPinnedObject().ToPointer(),
                (nuint)Unsafe.SizeOf<Vector3>(),
                (nuint)Unsafe.SizeOf<Vector3>());

            for (int i = 0; i < attributes.Count; i++)
            {
                RawAttribute attribute = attributes[i];
                int requiredLength = checked(positions.Length * attribute.Dimension);
                if (attribute.Data.Length < requiredLength)
                {
                    throw new ArgumentException(
                        $"Attribute '{attribute.Name}' has {attribute.Data.Length} values, but {requiredLength} are required for {positions.Length} vertices.",
                        nameof(attributes));
                }

                handles[i + 1] = GCHandle.Alloc(attribute.Data, GCHandleType.Pinned);
                nuint stride = checked((nuint)(attribute.Dimension * sizeof(float)));
                streams[i + 1] = new MeshOptimizer.Stream(
                    handles[i + 1].AddrOfPinnedObject().ToPointer(),
                    stride,
                    stride);
            }

            return Meshopt.GenerateVertexRemapMulti<byte>(
                destination,
                indices,
                (nuint)positions.Length,
                streams);
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsAllocated)
                    handles[i].Free();
            }
        }
    }

    private struct TempTri
    {
        public uint v0, v1, v2;
        public byte mat;
    }

    private static void EmitSplitMeshlet(
        ReadOnlySpan<TempTri> tris,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        // Build VRB batch info (linear scan, no reorder)
        uint vrbBatchInfo = BuildVrb(tris);

        int startIndex = globalIndices.Count;
        var uniqueMats = new List<byte>();
        int range0End = 0, range1End = 0;

        var uniqueVerts = new HashSet<uint>();

        for (int i = 0; i < tris.Length; i++)
        {
            ref readonly var t = ref tris[i];
            if (!uniqueMats.Contains(t.mat))
            {
                uniqueMats.Add(t.mat);
                if (uniqueMats.Count == 2) range0End = i;
                if (uniqueMats.Count == 3) range1End = i;
            }
            globalIndices.Add(t.v0);
            globalIndices.Add(t.v1);
            globalIndices.Add(t.v2);
            uniqueVerts.Add(t.v0);
            uniqueVerts.Add(t.v1);
            uniqueVerts.Add(t.v2);
        }

        if (uniqueMats.Count < 2) range0End = tris.Length;
        if (uniqueMats.Count < 3) range1End = tris.Length;

        clusters.Add(
            new BuilderMeshlet
            {
                IndicesOffset = startIndex,
                IndicesCount = tris.Length * 3,
                VertexCount = uniqueVerts.Count,
                GroupId = -1,
                ParentGroupId = -1,
                Mat0 = uniqueMats.Count > 0 ? uniqueMats[0] : (byte)0,
                Mat1 = uniqueMats.Count > 1 ? uniqueMats[1] : (byte)0,
                Mat2 = uniqueMats.Count > 2 ? uniqueMats[2] : (byte)0,
                Range0End = (byte)range0End,
                Range1End = (byte)range1End,
                VRBBatchInfo = vrbBatchInfo,
            }
        );
    }


    /// <summary>
    /// Build VRB batch info by linearly scanning triangles and closing batches
    /// when unique vertex count exceeds 32. Preserves meshopt ordering (no reorder).
    /// Returns a packed uint encoding up to 5 batch tri-counts.
    /// Triangles beyond the 5th batch are implicitly a "slow residual" at runtime.
    /// </summary>
    private static uint BuildVrb(ReadOnlySpan<TempTri> tris)
    {
        const int MaxUniqueVerts = 32;
        const int MaxBatchesEncoded = 5;

        Span<int> batchTriCounts = stackalloc int[MaxBatchesEncoded + 16]; // generous space
        int batchIndex = 0;
        int currentBatchTriCount = 0;
        var batchVerts = new HashSet<uint>();
        byte currentMat = tris[0].mat;

        for (int i = 0; i < tris.Length; i++)
        {
            ref readonly var tri = ref tris[i];

            // Close batch at material boundary (ensures each VRB batch is material-pure)
            if (tri.mat != currentMat && currentBatchTriCount > 0)
            {
                batchTriCounts[batchIndex++] = currentBatchTriCount;
                currentBatchTriCount = 0;
                batchVerts.Clear();
                currentMat = tri.mat;
            }

            // Test adding this triangle
            int newCount = batchVerts.Count;
            if (!batchVerts.Contains(tri.v0)) newCount++;
            if (!batchVerts.Contains(tri.v1)) newCount++;
            if (!batchVerts.Contains(tri.v2)) newCount++;

            if (newCount > MaxUniqueVerts && currentBatchTriCount > 0)
            {
                // Close current batch
                batchTriCounts[batchIndex++] = currentBatchTriCount;
                currentBatchTriCount = 0;
                batchVerts.Clear();

                // Re-add current triangle to new batch
                batchVerts.Add(tri.v0);
                batchVerts.Add(tri.v1);
                batchVerts.Add(tri.v2);
                currentBatchTriCount = 1;
            }
            else
            {
                batchVerts.Add(tri.v0);
                batchVerts.Add(tri.v1);
                batchVerts.Add(tri.v2);
                currentBatchTriCount++;
            }

            // Close batch at 32 tris
            if (currentBatchTriCount == 32)
            {
                batchTriCounts[batchIndex++] = currentBatchTriCount;
                currentBatchTriCount = 0;
                batchVerts.Clear();
            }
        }

        // Close final batch
        if (currentBatchTriCount > 0)
            batchTriCounts[batchIndex++] = currentBatchTriCount;

        // Encode: up to 5 batches in a uint
        int encodedCount = Math.Min(batchIndex, MaxBatchesEncoded);
        uint packed = 0;
        for (int i = 0; i < encodedCount; i++)
            packed |= (uint)(batchTriCounts[i] - 1) << (i * 5);
        packed |= (uint)(encodedCount - 1) << 25;

        return packed;
    }

    private static void Clusterize(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        if (indices.IsEmpty)
            return;

        nuint maxMeshlets = Meshopt.BuildMeshletsBound(
            (nuint)indices.Length,
            (nuint)config.MaxVertices,
            (nuint)config.MaxTriangles
        );

        var meshlets = ArrayPool<MeshOptimizer.Meshlet>.Shared.Rent((int)maxMeshlets);
        var meshletVertices = ArrayPool<uint>.Shared.Rent((int)maxMeshlets * config.MaxVertices);
        var meshletTriangles = ArrayPool<byte>.Shared.Rent(
            (int)maxMeshlets * config.MaxTriangles * 3
        );

        try
        {
            nuint meshletCount;
            var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
            var indSpan = indices;

            if (config.ClusterSpatial)
            {
                meshletCount = Meshopt.BuildMeshletsSpatial(
                    meshlets.AsSpan(),
                    meshletVertices.AsSpan(),
                    meshletTriangles.AsSpan(),
                    indSpan,
                    posSpan,
                    (nuint)Unsafe.SizeOf<Vector3>(),
                    (nuint)config.MaxVertices,
                    (nuint)config.MinTriangles,
                    (nuint)config.MaxTriangles,
                    config.ClusterFillWeight
                );
            }
            else
            {
                meshletCount = Meshopt.BuildMeshletsFlex(
                    meshlets.AsSpan(),
                    meshletVertices.AsSpan(),
                    meshletTriangles.AsSpan(),
                    indSpan,
                    posSpan,
                    (nuint)Unsafe.SizeOf<Vector3>(),
                    (nuint)config.MaxVertices,
                    (nuint)config.MinTriangles,
                    (nuint)config.MaxTriangles,
                    0.0f,
                    config.ClusterSplitFactor
                );
            }

            for (int i = 0; i < (int)meshletCount; i++)
            {
                ref var m = ref meshlets[i];
                if (config.OptimizeClusters)
                {
                    Meshopt.OptimizeMeshlet(
                        meshletVertices.AsSpan((int)m.vertex_offset, (int)m.vertex_count),
                        meshletTriangles.AsSpan((int)m.triangle_offset, (int)m.triangle_count * 3),
                        m.triangle_count,
                        m.vertex_count
                    );
                }

                var tris = new TempTri[m.triangle_count];
                for (uint t = 0; t < m.triangle_count; t++)
                {
                    int triOffset = (int)m.triangle_offset + (int)t * 3;
                    uint v0 = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 0]];
                    uint v1 = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 1]];
                    uint v2 = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 2]];
                    byte mat = (byte)materialIndicesArray[v0];
                    tris[t] = new TempTri { v0 = v0, v1 = v1, v2 = v2, mat = mat };
                }

                // Stable bucket grouping: preserves meshopt vertex cache order within each material.
                // Array.Sort is unstable and would destroy spatial locality even for single-material meshlets.
                {
                    var buckets = new List<TempTri>[256];
                    for (int bi = 0; bi < tris.Length; bi++)
                    {
                        byte bmat = tris[bi].mat;
                        buckets[bmat] ??= new List<TempTri>();
                        buckets[bmat].Add(tris[bi]);
                    }
                    int pos = 0;
                    for (int bm = 0; bm < 256; bm++)
                    {
                        if (buckets[bm] == null) continue;
                        foreach (var tri in buckets[bm])
                            tris[pos++] = tri;
                    }
                }


                var uniqueMats = new List<byte>();
                int currentChunkStart = 0;

                for (int t = 0; t < tris.Length; t++)
                {
                    if (!uniqueMats.Contains(tris[t].mat))
                    {
                        if (uniqueMats.Count == 3)
                        {
                            EmitSplitMeshlet(
                                new ReadOnlySpan<TempTri>(tris, currentChunkStart, t - currentChunkStart),
                                clusters,
                                globalIndices
                            );
                            uniqueMats.Clear();
                            currentChunkStart = t;
                        }
                        uniqueMats.Add(tris[t].mat);
                    }
                }

                if (currentChunkStart < tris.Length)
                {
                    EmitSplitMeshlet(
                        new ReadOnlySpan<TempTri>(tris, currentChunkStart, tris.Length - currentChunkStart),
                        clusters,
                        globalIndices
                    );
                }
            }
        }
        finally
        {
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Return(meshlets);
            ArrayPool<uint>.Shared.Return(meshletVertices);
            ArrayPool<byte>.Shared.Return(meshletTriangles);
        }
    }

    private static int Partition(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> pending,
        ReadOnlySpan<uint> remap,
        List<int> groupOffsets
    )
    {
        groupOffsets.Clear();
        if (pending.Count <= config.PartitionSize)
        {
            groupOffsets.Add(0);
            groupOffsets.Add(pending.Count);
            return 1;
        }

        int totalIndexCount = 0;
        var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

        for (int i = 0; i < pending.Count; i++)
        {
            var c = clusters[pending[i]];
            totalIndexCount += c.IndicesCount;
        }

        var clusterIndices = ArrayPool<uint>.Shared.Rent(totalIndexCount);
        var clusterCounts = ArrayPool<uint>.Shared.Rent(pending.Count);
        var clusterPart = ArrayPool<uint>.Shared.Rent(pending.Count);
        uint[]? partitionRemap = null;

        try
        {
            int offset = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var c = clusters[pending[i]];
                clusterCounts[i] = (uint)c.IndicesCount;
                var cIndices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                for (int j = 0; j < cIndices.Length; j++)
                    clusterIndices[offset++] = remap[(int)cIndices[j]];
            }

            nuint partitionCount;
            var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);

            partitionCount = Meshopt.PartitionClusters(
                clusterPart.AsSpan(0, pending.Count),
                clusterIndices.AsSpan(0, totalIndexCount),
                clusterCounts.AsSpan(0, pending.Count),
                config.PartitionSpatial ? posSpan : default,
                (nuint)Unsafe.SizeOf<Vector3>(),
                (nuint)config.PartitionSize
            );

            if (config.PartitionSort)
            {
                var partitionPoint = ArrayPool<float>.Shared.Rent((int)partitionCount * 3);
                partitionRemap = ArrayPool<uint>.Shared.Rent((int)partitionCount);
                try
                {
                    for (int i = 0; i < pending.Count; i++)
                    {
                        var center = clusters[pending[i]].Center;
                        uint partId = clusterPart[i];
                        partitionPoint[(int)partId * 3 + 0] = center.X;
                        partitionPoint[(int)partId * 3 + 1] = center.Y;
                        partitionPoint[(int)partId * 3 + 2] = center.Z;
                    }

                    Meshopt.SpatialSortRemap(
                        partitionRemap.AsSpan(0, (int)partitionCount),
                        partitionPoint.AsSpan(0, (int)partitionCount * 3),
                        (nuint)Unsafe.SizeOf<Vector3>()
                    );
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(partitionPoint);
                }
            }

            var sortedPending = ArrayPool<int>.Shared.Rent(pending.Count);
            var partitionSizes = ArrayPool<int>.Shared.Rent((int)partitionCount);
            Array.Clear(partitionSizes, 0, (int)partitionCount);

            for (int i = 0; i < pending.Count; i++)
            {
                uint partId = clusterPart[i];
                if (partitionRemap != null)
                    partId = partitionRemap[partId];
                partitionSizes[(int)partId]++;
            }

            var offsets = ArrayPool<int>.Shared.Rent((int)partitionCount);
            int runningOffset = 0;
            groupOffsets.Add(0);
            for (int i = 0; i < (int)partitionCount; i++)
            {
                offsets[i] = runningOffset;
                runningOffset += partitionSizes[i];
                groupOffsets.Add(runningOffset);
            }

            for (int i = 0; i < pending.Count; i++)
            {
                uint partId = clusterPart[i];
                if (partitionRemap != null)
                    partId = partitionRemap[partId];

                int dest = offsets[partId]++;
                sortedPending[dest] = pending[i];
            }

            new Span<int>(sortedPending, 0, pending.Count).CopyTo(
                CollectionsMarshal.AsSpan(pending)
            );

            ArrayPool<int>.Shared.Return(sortedPending);
            ArrayPool<int>.Shared.Return(partitionSizes);
            ArrayPool<int>.Shared.Return(offsets);
            if (partitionRemap != null)
                ArrayPool<uint>.Shared.Return(partitionRemap);

            return (int)partitionCount;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(clusterIndices);
            ArrayPool<uint>.Shared.Return(clusterCounts);
            ArrayPool<uint>.Shared.Return(clusterPart);
        }
    }

    private static void LockBoundary(
        Span<byte> locks,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> pending,
        List<int> groupOffsets,
        ReadOnlySpan<uint> remap
    )
    {
        const byte LockBit = 1 << 0;
        const byte SeenBit = 1 << 7;
        const byte SimplifyProtect = 2; // meshopt_SimplifyVertex_Protect

        for (int i = 0; i < locks.Length; i++)
            locks[i] &= unchecked((byte)~(LockBit | SeenBit));

        var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

        for (int g = 0; g < groupOffsets.Count - 1; g++)
        {
            int start = groupOffsets[g];
            int count = groupOffsets[g + 1] - start;
            var group = CollectionsMarshal.AsSpan(pending).Slice(start, count);

            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= (byte)((locks[(int)r] & SeenBit) >> 7);
                }
            }
            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= SeenBit;
                }
            }
        }

        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = (byte)((locks[i] & LockBit) | (locks[i] & SimplifyProtect));
        }
    }

    private static void Simplify(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks,
        int targetCount,
        out float error,
        List<uint> outputIndices
    )
    {
        if (targetCount >= indices.Length)
        {
            error = 0;
            foreach (var i in indices)
                outputIndices.Add(i);
            return;
        }

        var simplified = ArrayPool<uint>.Shared.Rent(indices.Length);
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);

        try
        {
            // Standard meshoptimizer SimplifyOptions values:
            // LockBorder = 1, Sparse = 2, ErrorAbsolute = 4, Regularize = 16,
            // Permissive = 32
            var options = SimplificationOptions.SimplifyLockBorder; // 1
            options |= (SimplificationOptions)2; // Sparse
            options |= (SimplificationOptions)4; // ErrorAbsolute
            if (config.SimplifyPermissive)
                options |= (SimplificationOptions)32;
            if (config.SimplifyRegularize)
                options |= (SimplificationOptions)16;

            nuint newCount = Meshopt.SimplifyWithAttributes(
                simplified.AsSpan(),
                indices,
                posSpan,
                (nuint)Unsafe.SizeOf<Vector3>(),
                null,
                0,
                null,
                0,
                locks,
                (nuint)targetCount,
                float.MaxValue,
                options,
                out error
            );

            if (newCount > (nuint)targetCount && config.SimplifyFallbackSloppy)
            {
                newCount = Meshopt.SimplifySloppy(
                    simplified.AsSpan(),
                    indices,
                    posSpan,
                    (nuint)Unsafe.SizeOf<Vector3>(),
                    (nuint)targetCount,
                    float.MaxValue,
                    out error
                );
                error *= config.SimplifyErrorFactorSloppy;
            }

            if (config.SimplifyErrorEdgeLimit > 0)
            {
                float maxEdgeSq = 0;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var va = positions[(int)indices[i + 0]];
                    var vb = positions[(int)indices[i + 1]];
                    var vc = positions[(int)indices[i + 2]];
                    float eab = Vector3.DistanceSquared(va, vb);
                    float eac = Vector3.DistanceSquared(va, vc);
                    float ebc = Vector3.DistanceSquared(vb, vc);
                    float emax = Math.Max(Math.Max(eab, eac), ebc);
                    float emin = Math.Min(Math.Min(eab, eac), ebc);
                    maxEdgeSq = Math.Max(maxEdgeSq, Math.Max(emin, emax / 4.0f));
                }
                error = Math.Min(
                    error,
                    (float)Math.Sqrt(maxEdgeSq) * config.SimplifyErrorEdgeLimit
                );
            }

            for (int i = 0; i < (int)newCount; ++i)
                outputIndices.Add(simplified[i]);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(simplified);
        }
    }

    private static ClusterLodBounds BoundsCompute(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float error
    )
    {
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        var b = Meshopt.ComputeClusterBounds(indices, posSpan, (nuint)Unsafe.SizeOf<Vector3>());

        Vector3 center;
        unsafe
        {
            center = *(Vector3*)b.center;
        }

        return new ClusterLodBounds
        {
            Center = center,
            Radius = b.radius,
            Error = error,
        };
    }

    private static ClusterLodBounds BoundsMerge(
        List<BuilderMeshlet> clusters,
        ReadOnlySpan<int> group
    )
    {
        var centers = ArrayPool<float>.Shared.Rent(group.Length * 3);
        var radii = ArrayPool<float>.Shared.Rent(group.Length);

        try
        {
            float maxError = 0;
            for (int i = 0; i < group.Length; i++)
            {
                var c = clusters[group[i]];
                centers[i * 3 + 0] = c.SelfLodCenter.X;
                centers[i * 3 + 1] = c.SelfLodCenter.Y;
                centers[i * 3 + 2] = c.SelfLodCenter.Z;
                radii[i] = c.SelfLodRadius;
                maxError = Math.Max(maxError, c.Error);
            }

            var merged = Meshopt.ComputeSphereBounds(
                centers.AsSpan(0, group.Length * 3),
                sizeof(float) * 3,
                radii.AsSpan(0, group.Length),
                sizeof(float)
            );

            Vector3 mergedCenter;
            unsafe
            {
                mergedCenter = *(Vector3*)merged.center;
            }
            return new ClusterLodBounds
            {
                Center = mergedCenter,
                Radius = merged.radius,
                Error = maxError,
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(centers);
            ArrayPool<float>.Shared.Return(radii);
        }
    }
}
