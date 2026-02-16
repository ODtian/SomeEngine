using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SharpGLTF.Schema2;
using MeshOptimizer;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Data;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Importers;

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
            OptimizeClusters = true
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
    private const int PageHeaderSize = 32;

    private class BuilderMeshlet
    {
        public uint[] Indices = Array.Empty<uint>();
        public int Level;
        public float Error;
        public float ParentError;
        public int GroupId = -1;
        public int ParentGroupId = -1;
        public Vector3 Center;
        public float Radius;
        public Vector3 LodCenter;
        public float LodRadius;
        public int VertexCount;
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

    public static MeshAsset Process(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var mesh = model.LogicalMeshes[0];
        var primitive = mesh.Primitives[0];

        // 1. Get Positions (Special)
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var rawPos = positions.ToArray();

        // 2. Get Indices
        var indices16 = primitive.GetIndexAccessor().AsIndicesArray();
        var rawIndices = new uint[indices16.Count];
        for (int i = 0; i < indices16.Count; i++)
            rawIndices[i] = indices16[i];

        // 3. Get Other Attributes
        var rawAttributes = new List<RawAttribute>();

        foreach (var key in primitive.VertexAccessors.Keys)
        {
            if (key == "POSITION")
                continue;

            var accessor = primitive.GetVertexAccessor(key);

            int dimension = accessor.Dimensions switch { DimensionType.SCALAR => 1,
                                                         DimensionType.VEC2 => 2,
                                                         DimensionType.VEC3 => 3,
                                                         DimensionType.VEC4 => 4,
                                                         _ => 1 };

            ValueType targetType = ValueType.Float32;
            bool normalized = accessor.Normalized;

            // Heuristic for default target types based on attribute name and usage
            if (key == "NORMAL" || key == "TANGENT")
            {
                // Pack normals/tangents to Int8 SNORM
                targetType = ValueType.Int8;
                normalized = true;
            }
            else if (key.StartsWith("TEXCOORD"))
            {
                // Texcoords usually fit in Half
                targetType = ValueType.Float16;
            }
            else if (key.StartsWith("COLOR"))
            {
                // Colors usually UInt8 UNORM
                targetType = ValueType.UInt8;
                normalized = true;
            }
            else if (key.StartsWith("JOINTS"))
            {
                // Joints indices usually integer
                targetType = ValueType.UInt16; // or UInt8 if < 256 bones
            }
            else if (key.StartsWith("WEIGHTS"))
            {
                // Weights usually normalized byte or short
                targetType = ValueType.UInt8;
                normalized = true;
            }

            var floatData = accessor.AsScalarArray();
            var data = new float[floatData.Count];
            floatData.CopyTo(data, 0);

            rawAttributes.Add(new RawAttribute(
                key, data, dimension, targetType, (byte)dimension, normalized
            ));
        }

        return ProcessRaw(rawPos, rawAttributes, rawIndices, mesh.Name ?? "Unnamed");
    }

    private static List<BuilderMeshlet> BuildClusterLod(
        ClusterLodConfig config,
        Vector3[] positions,
        uint[] indices
    )
    {
        var locks = new byte[positions.Length];
        var remap = new uint[positions.Length];
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions.AsSpan());

        unsafe
        {
            fixed (uint* pRemap = remap)
            fixed (float* pPositions = posSpan)
            {
                Meshopt.GeneratePositionRemap(pRemap, pPositions, (nuint)positions.Length, (nuint)Unsafe.SizeOf<Vector3>());
            }
        }

        var clusters = Clusterize(config, indices, positions);
        int nextGroupId = 0;
        foreach (var c in clusters)
        {
            var b = BoundsCompute(positions, c.Indices, 0);
            c.Center = b.Center;
            c.Radius = b.Radius;
            c.LodCenter = b.Center;
            c.LodRadius = b.Radius;
            c.Error = 0; // Initial clusters have no error
            c.Level = 0;
            c.GroupId = nextGroupId++; // Each Level 0 cluster gets its own GroupId
        }

        var pending = new List<int>();
        for (int i = 0; i < clusters.Count; i++) pending.Add(i);

        int depth = 0;
        while (pending.Count > 1)
        {
            var groups = Partition(config, positions, clusters, pending, remap);
            pending.Clear();

            LockBoundary(locks.AsSpan(), groups, clusters, remap);

            foreach (var group in groups)
            {
                var mergedIndices = new List<uint>();
                foreach (int idx in group) mergedIndices.AddRange(clusters[idx].Indices);

                int targetSize = (int)((mergedIndices.Count / 3) * config.SimplifyRatio) * 3;
                var groupBounds = BoundsMerge(clusters, group);

                float error = 0;
                var simplified = Simplify(config, positions, mergedIndices.ToArray(), locks, targetSize, out error);

                if (simplified.Length > mergedIndices.Count * config.SimplifyThreshold)
                {
                    // Terminal group
                    foreach (int idx in group)
                    {
                        clusters[idx].ParentError = float.MaxValue;
                    }
                    continue;
                }

                // Error is the maximum error of all child clusters plus the error introduced by this simplification step.
                float groupError = groupBounds.Error + error;
                
                int thisGroupId = nextGroupId++;

                foreach (int idx in group)
                {
                    clusters[idx].ParentError = groupError;
                    clusters[idx].ParentGroupId = thisGroupId;
                    
                    // LodCenter/Radius is the sphere used for ParentError evaluation.
                    // To match the parent's SelfError evaluation, it must be the groupBounds.
                    clusters[idx].LodCenter = groupBounds.Center;
                    clusters[idx].LodRadius = groupBounds.Radius;
                }

                var split = Clusterize(config, simplified, positions);
                foreach (var sc in split)
                {
                    sc.Level = depth + 1;
                    
                    // Center/Radius is used for culling AND for SelfError evaluation.
                    // For consistency with children, it must be the groupBounds.
                    sc.Center = groupBounds.Center;
                    sc.Radius = groupBounds.Radius;
                    
                    sc.Error = groupError;
                    sc.GroupId = thisGroupId;
                    
                    // Initial LodCenter/Radius for parents. 
                    // Will be overwritten when this parent is grouped into the next level.
                    sc.LodCenter = groupBounds.Center;
                    sc.LodRadius = groupBounds.Radius;
                    
                    clusters.Add(sc);
                    pending.Add(clusters.Count - 1);
                }
            }
            depth++;
        }

        if (pending.Count == 1)
        {
            clusters[pending[0]].ParentError = float.MaxValue;
        }

        return clusters;
    }

    public static MeshAsset ProcessRaw(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
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
        byte[] reusablePageBuffer = new byte[PageSize + 65536]; // Extra buffer

        // 1. Meshopt Logic
        var remap = new uint[rawPos.Length];

        // GenerateVertexRemap reorders vertices to be contiguous in order of
        // appearance
        nuint vertexCount = Meshopt.GenerateVertexRemap(
            remap.AsSpan(), rawIndices.AsSpan(), rawPos.AsSpan()
        );

        var pPos = new Vector3[(int)vertexCount];
        var pInd = new uint[rawIndices.Length];

        // Manual Vertex Remap (old vertex -> new vertex)
        for (int oldIndex = 0; oldIndex < rawPos.Length; oldIndex++)
        {
            uint newIndex = remap[oldIndex];
            if (newIndex == uint.MaxValue || newIndex >= vertexCount)
                continue;

            pPos[newIndex] = rawPos[oldIndex];
        }

        // Remap Indices
        Meshopt.RemapIndexBuffer(pInd.AsSpan(), rawIndices.AsSpan(), remap.AsSpan());

        // Remap Attributes
        var pAttributes = new List<RawAttribute>();
        foreach (var attr in rawAttributes)
        {
            var newData = new float[(int)vertexCount * attr.Dimension];
            int dim = attr.Dimension;
            var srcData = attr.Data;

            // Manual Scatter: old vertex -> new vertex
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
            pAttributes.Add(new RawAttribute(
                attr.Name,
                newData,
                attr.Dimension,
                attr.TargetType,
                attr.NumComponents,
                attr.Normalized
            ));
        }

        Meshopt.OptimizeVertexCache(pInd.AsSpan(), pInd.AsSpan(), vertexCount);

        // NOTE: keep fetch order optimization disabled for now to guarantee
        // deterministic/debug-correct geometry.
        var finalAttributes = pAttributes;

        var finalPositions = new List<Vector3>();
        for (int i = 0; i < (int)vertexCount; i++)
        {
            finalPositions.Add(pPos[i]);
        }

        // Build Cluster LOD hierarchy
        var allMeshlets = BuildClusterLod(ClusterLodConfig.GetDefault(), finalPositions.ToArray(), pInd);

        // Sort clusters by LODLevel and then ParentGroupId to keep groups together
        allMeshlets = allMeshlets.OrderBy(m => m.Level).ThenBy(m => m.ParentGroupId).ToList();

        // 2. Page Generation & Quantization
        var pagesDataList = new List<MeshPageInfo>();

        var currentClusters = new List<GPUCluster>();
        var currentPositions = new List<ushort>();
        var currentAttrs = new List<byte>(); // Changed to bytes
        var currentIndices = new List<byte>();

        int currentBytes = PageHeaderSize;

        // Build Layout
        var descriptors = new List<VertexAttributeDescriptor>();
        ushort currentOffset = 0;
        int vertexStride = 0;
        foreach (var attr in finalAttributes)
        {
            var desc = new VertexAttributeDescriptor {
                Name = attr.Name,
                Type = attr.TargetType,
                NumComponents = attr.NumComponents,
                IsNormalized = attr.Normalized,
                Offset = currentOffset
            };
            int size = desc.GetSize();
            vertexStride += size;
            currentOffset += (ushort)size;
            descriptors.Add(desc);
        }

        void FlushPage()
        {
            if (currentClusters.Count == 0)
                return;

            // Align offsets to 4 bytes?
            // Current structure assumes offsets are just sequential byte offsets
            // relative to page start.

            uint clustersOffset = PageHeaderSize;
            int clustersSize = currentClusters.Count * Unsafe.SizeOf<GPUCluster>();

            uint positionsOffset = clustersOffset + (uint)clustersSize;
            int positionsSize = currentPositions.Count * sizeof(ushort);

            uint attributesOffset = positionsOffset + (uint)positionsSize;
            int attrsSize = currentAttrs.Count;

            uint indicesOffset = attributesOffset + (uint)attrsSize;
            int indicesSize = currentIndices.Count;

            // Check buffer size
            int totalSize = (int)indicesOffset + indicesSize;
            if (totalSize > reusablePageBuffer.Length)
            {
                // Should not happen if page logic is correct, but safe to expand if
                // needed or throw
                throw new Exception(
                    $"Page buffer overflow: {totalSize} > {reusablePageBuffer.Length}"
                );
            }

            Array.Clear(reusablePageBuffer, 0, reusablePageBuffer.Length);
            var span = new Span<byte>(reusablePageBuffer);

            ref var header = ref Unsafe.As<byte, MeshPageHeader>(ref span[0]);
            header.ClusterCount = (uint)currentClusters.Count;
            header.TotalVertexCount = (uint)(currentPositions.Count / 3);
            header.TotalTriangleCount = (uint)(currentIndices.Count / 3);
            header.PageSize = (uint)totalSize;
            header.ClustersOffset = clustersOffset;
            header.PositionsOffset = positionsOffset;
            header.AttributesOffset = attributesOffset;
            header.IndicesOffset = indicesOffset;

            MemoryMarshal
                .Cast<GPUCluster, byte>(CollectionsMarshal.AsSpan(currentClusters))
                .CopyTo(span.Slice((int)clustersOffset, clustersSize));
            MemoryMarshal
                .Cast<ushort, byte>(CollectionsMarshal.AsSpan(currentPositions))
                .CopyTo(span.Slice((int)positionsOffset, positionsSize));
            CollectionsMarshal.AsSpan(currentAttrs)
                .CopyTo(span.Slice((int)attributesOffset, attrsSize));
            CollectionsMarshal.AsSpan(currentIndices)
                .CopyTo(span.Slice((int)indicesOffset, indicesSize));

            fs.Write(reusablePageBuffer, 0, totalSize);

            pagesDataList.Add(new MeshPageInfo {
                ClusterCount = (uint)currentClusters.Count,
                TotalVertexCount = (uint)(currentPositions.Count / 3),
                TotalTriangleCount = (uint)(currentIndices.Count / 3),
                ClustersOffset = clustersOffset,
                PositionsOffset = positionsOffset,
                AttributesOffset = attributesOffset,
                IndicesOffset = indicesOffset,
                FileOffset = fs.Position - totalSize
            });

            currentClusters.Clear();
            currentPositions.Clear();
            currentAttrs.Clear();
            currentIndices.Clear();
            currentBytes = PageHeaderSize;
        }

        foreach (var m in allMeshlets)
        {
            int vCount = 0;
            var mIndices = m.Indices;
            var usedMap = new Dictionary<uint, ushort>();
            var localPos = new List<ushort>();
            var localIndices = new List<byte>();

            var localAttrBytes = new List<byte>();

            Vector3 center = m.Center;
            float radius = m.Radius;
            if (radius < 1e-6f)
                radius = 1.0f;

            foreach (var globalIdx in mIndices)
            {
                if (!usedMap.TryGetValue(globalIdx, out ushort localIdx))
                {
                    localIdx = (ushort)vCount;
                    usedMap[globalIdx] = localIdx;
                    vCount++;

                    // 1. Pack Position (Quantized)
                    Vector3 p = finalPositions[(int)globalIdx];
                    Vector3 rel = (p - center) / radius;
                    ushort qx = (ushort)((Math.Clamp(rel.X, -1f, 1f) * 0.5f + 0.5f) *
                                         65535f);
                    ushort qy = (ushort)((Math.Clamp(rel.Y, -1f, 1f) * 0.5f + 0.5f) *
                                         65535f);
                    ushort qz = (ushort)((Math.Clamp(rel.Z, -1f, 1f) * 0.5f + 0.5f) *
                                         65535f);

                    localPos.Add(qx);
                    localPos.Add(qy);
                    localPos.Add(qz);

                    // 2. Pack Attributes (Generic)
                    for (int i = 0; i < finalAttributes.Count; ++i)
                    {
                        PackAttribute(
                            localAttrBytes, finalAttributes[i], (int)globalIdx
                        );
                    }
                }
                localIndices.Add((byte)localIdx);
            }

            int clusterSize = Unsafe.SizeOf<GPUCluster>();
            int vSize = localPos.Count * 2;
            int aSize = localAttrBytes.Count;
            int iSize = localIndices.Count;
            int totalAdded = clusterSize + vSize + aSize + iSize;

            if (currentBytes + totalAdded > PageSize)
            {
                FlushPage();
            }

            uint vStart = (uint)(currentPositions.Count / 3);
            uint tStart = (uint)currentIndices.Count;

            currentPositions.AddRange(localPos);
            currentAttrs.AddRange(localAttrBytes);
            currentIndices.AddRange(localIndices);
            currentBytes += totalAdded;

            currentClusters.Add(new GPUCluster {
                Center = m.Center,
                Radius = m.Radius,
                LodCenter = m.LodCenter,
                LodRadius = m.LodRadius,
                LODError = m.Error,
                ParentLODError = m.ParentError,
                VertexStart = vStart,
                TriangleStart = tStart,
                GroupId = m.GroupId,
                ParentGroupId = m.ParentGroupId,
                VertexCount = (byte)vCount,
                TriangleCount = (byte)(localIndices.Count / 3),
                LODLevel = (byte)m.Level,
                _Pad1 = 0
            });
        }

        FlushPage();

        // Serialize
        // Convert descriptors to Schema format
        var schemaAttrs =
            new SomeEngine.Assets.Schema.VertexAttribute[descriptors.Count];
        for (int i = 0; i < descriptors.Count; ++i)
        {
            schemaAttrs[i] = new SomeEngine.Assets.Schema.VertexAttribute(
            ) { Name = descriptors[i].Name,
                Type = (SomeEngine.Assets.Schema.ValueType)descriptors[i].Type,
                Components = descriptors[i].NumComponents,
                Normalized = descriptors[i].IsNormalized,
                Offset = descriptors[i].Offset };
        }

        var meshAsset = new MeshAsset {
            Name = name,
            Bounds = new SomeEngine.Assets.Schema.Bounds(
            ) { Center = new SomeEngine.Assets.Schema.Vec3(
                ) { X = 0, Y = 0, Z = 0 }, // TODO: Compute Global Bounds
                Radius = 0 },
            Payload = new byte[fs.Length],
            Attributes = schemaAttrs
        };

        fs.Seek(0, SeekOrigin.Begin);
        fs.ReadExactly(meshAsset.Payload.Value.Span);

        return meshAsset;
    }

    private static void PackAttribute(
        List<byte> output, RawAttribute attr, int index
    )
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
                    uint u = *(uint *)&val;
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

    private static List<BuilderMeshlet> Clusterize(
        ClusterLodConfig config,
        uint[] indices,
        Vector3[] positions
    )
    {
        if (indices.Length == 0) return new List<BuilderMeshlet>();

        nuint maxMeshlets = Meshopt.BuildMeshletsBound(
            (nuint)indices.Length, (nuint)config.MaxVertices, (nuint)config.MaxTriangles
        );

        var meshlets = new MeshOptimizer.Meshlet[maxMeshlets];
        var meshletVertices = new uint[(int)maxMeshlets * config.MaxVertices];
        var meshletTriangles = new byte[(int)maxMeshlets * config.MaxTriangles * 3];

        nuint meshletCount;
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions.AsSpan());
        var indSpan = indices.AsSpan();

        unsafe
        {
            fixed (MeshOptimizer.Meshlet* pMeshlets = meshlets)
            fixed (uint* pMeshletVertices = meshletVertices)
            fixed (byte* pMeshletTriangles = meshletTriangles)
            fixed (uint* pIndices = indSpan)
            fixed (float* pPositions = posSpan)
            {
                if (config.ClusterSpatial)
                {
                    meshletCount = Meshopt.BuildMeshletsSpatial(
                        pMeshlets, pMeshletVertices, pMeshletTriangles,
                        pIndices, (nuint)indSpan.Length,
                        pPositions, (nuint)positions.Length, (nuint)Unsafe.SizeOf<Vector3>(),
                        (nuint)config.MaxVertices, (nuint)config.MinTriangles, (nuint)config.MaxTriangles,
                        config.ClusterFillWeight
                    );
                }
                else
                {
                    meshletCount = Meshopt.BuildMeshletsFlex(
                        pMeshlets, pMeshletVertices, pMeshletTriangles,
                        pIndices, (nuint)indSpan.Length,
                        pPositions, (nuint)positions.Length, (nuint)Unsafe.SizeOf<Vector3>(),
                        (nuint)config.MaxVertices, (nuint)config.MinTriangles, (nuint)config.MaxTriangles,
                        0.0f, config.ClusterSplitFactor
                    );
                }
            }
        }

        var result = new List<BuilderMeshlet>();
        for (int i = 0; i < (int)meshletCount; i++)
        {
            ref var m = ref meshlets[i];
            if (config.OptimizeClusters)
            {
                Meshopt.OptimizeMeshlet(
                    meshletVertices.AsSpan((int)m.vertex_offset, (int)m.vertex_count),
                    meshletTriangles.AsSpan((int)m.triangle_offset, (int)m.triangle_count * 3),
                    m.triangle_count, m.vertex_count
                );
            }

            var mIndices = new uint[m.triangle_count * 3];
            for (uint t = 0; t < m.triangle_count; t++)
            {
                int triOffset = (int)m.triangle_offset + (int)t * 3;
                mIndices[t * 3 + 0] = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 0]];
                mIndices[t * 3 + 1] = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 1]];
                mIndices[t * 3 + 2] = meshletVertices[(int)m.vertex_offset + meshletTriangles[triOffset + 2]];
            }

            result.Add(new BuilderMeshlet
            {
                Indices = mIndices,
                VertexCount = (int)m.vertex_count
            });
        }
        return result;
    }

    private static List<List<int>> Partition(
        ClusterLodConfig config,
        Vector3[] positions,
        List<BuilderMeshlet> clusters,
        List<int> pending,
        uint[] remap
    )
    {
        if (pending.Count <= config.PartitionSize)
            return new List<List<int>> { new List<int>(pending) };

        int totalIndexCount = 0;
        foreach (int idx in pending) totalIndexCount += clusters[idx].Indices.Length;

        var clusterIndices = new uint[totalIndexCount];
        var clusterCounts = new uint[pending.Count];
        int offset = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            var cluster = clusters[pending[i]];
            clusterCounts[i] = (uint)cluster.Indices.Length;
            foreach (var idx in cluster.Indices)
                clusterIndices[offset++] = remap[idx];
        }

        var clusterPart = new uint[pending.Count];
        nuint partitionCount;
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions.AsSpan());

        unsafe
        {
            fixed (uint* pClusterPart = clusterPart)
            fixed (uint* pClusterIndices = clusterIndices)
            fixed (uint* pClusterCounts = clusterCounts)
            fixed (float* pPositions = posSpan)
            {
                partitionCount = Meshopt.PartitionClusters(
                    pClusterPart, pClusterIndices, (nuint)clusterIndices.Length,
                    pClusterCounts, (nuint)clusterCounts.Length,
                    config.PartitionSpatial ? pPositions : null, (nuint)remap.Length, (nuint)Unsafe.SizeOf<Vector3>(),
                    (nuint)config.PartitionSize
                );
            }
        }

        var partitions = new List<List<int>>((int)partitionCount);
        for (int i = 0; i < (int)partitionCount; i++) partitions.Add(new List<int>());

        uint[]? partitionRemap = null;
        if (config.PartitionSort)
        {
            var partitionPoint = new float[partitionCount * 3];
            for (int i = 0; i < pending.Count; i++)
            {
                var center = clusters[pending[i]].Center;
                partitionPoint[clusterPart[i] * 3 + 0] = center.X;
                partitionPoint[clusterPart[i] * 3 + 1] = center.Y;
                partitionPoint[clusterPart[i] * 3 + 2] = center.Z;
            }
            partitionRemap = new uint[partitionCount];
            Meshopt.SpatialSortRemap(partitionRemap.AsSpan(), partitionPoint.AsSpan(), (nuint)Unsafe.SizeOf<Vector3>());
        }

        for (int i = 0; i < pending.Count; i++)
        {
            uint partId = clusterPart[i];
            int finalPartId = partitionRemap == null ? (int)partId : (int)partitionRemap[partId];
            partitions[finalPartId].Add(pending[i]);
        }

        return partitions;
    }

    private static void LockBoundary(
        Span<byte> locks,
        List<List<int>> groups,
        List<BuilderMeshlet> clusters,
        uint[] remap
    )
    {
        // For each remapped vertex, use bit 7 as temporary storage to indicate that 
        // the vertex has been used by a different group previously
        const byte LockBit = 1 << 0;
        const byte SeenBit = 1 << 7;
        const byte SimplifyProtect = 2; // meshopt_SimplifyVertex_Protect

        for (int i = 0; i < locks.Length; i++)
            locks[i] &= unchecked((byte)~(LockBit | SeenBit));

        foreach (var group in groups)
        {
            foreach (int clusterIdx in group)
            {
                foreach (var v in clusters[clusterIdx].Indices)
                {
                    uint r = remap[v];
                    locks[(int)r] |= (byte)((locks[(int)r] & SeenBit) >> 7);
                }
            }
            foreach (int clusterIdx in group)
            {
                foreach (var v in clusters[clusterIdx].Indices)
                {
                    uint r = remap[v];
                    locks[(int)r] |= SeenBit;
                }
            }
        }

        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = (byte)((locks[i] & LockBit) | (locks[i] & SimplifyProtect));
        }
    }

    private static uint[] Simplify(
        ClusterLodConfig config,
        Vector3[] positions,
        uint[] indices,
        byte[] locks,
        int targetCount,
        out float error
    )
    {
        if (targetCount >= indices.Length)
        {
            error = 0;
            return indices;
        }

        var simplified = new uint[indices.Length];
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions.AsSpan());
        
        // Standard meshoptimizer SimplifyOptions values:
        // LockBorder = 1, Sparse = 2, ErrorAbsolute = 4, Regularize = 16, Permissive = 32
        var options = SimplificationOptions.SimplifyLockBorder; // 1
        options |= (SimplificationOptions)2; // Sparse
        options |= (SimplificationOptions)4; // ErrorAbsolute
        if (config.SimplifyPermissive) options |= (SimplificationOptions)32;
        if (config.SimplifyRegularize) options |= (SimplificationOptions)16;

        nuint newCount = Meshopt.SimplifyWithAttributes(
            simplified.AsSpan(),
            indices.AsSpan(),
            posSpan, (nuint)Unsafe.SizeOf<Vector3>(),
            null, 0, null, 0,
            locks.AsSpan(),
            (nuint)targetCount,
            float.MaxValue,
            options,
            out error
        );

        if (newCount > (nuint)targetCount && config.SimplifyFallbackSloppy)
        {
            // SimplifyFallback logic from clusterlod.h
            // For simplicity in C#, we use SimplifySloppy directly if available
            newCount = Meshopt.SimplifySloppy(
                simplified.AsSpan(),
                indices.AsSpan(),
                posSpan, (nuint)Unsafe.SizeOf<Vector3>(),
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
                var va = positions[indices[i + 0]];
                var vb = positions[indices[i + 1]];
                var vc = positions[indices[i + 2]];
                float eab = Vector3.DistanceSquared(va, vb);
                float eac = Vector3.DistanceSquared(va, vc);
                float ebc = Vector3.DistanceSquared(vb, vc);
                float emax = Math.Max(Math.Max(eab, eac), ebc);
                float emin = Math.Min(Math.Min(eab, eac), ebc);
                maxEdgeSq = Math.Max(maxEdgeSq, Math.Max(emin, emax / 4.0f));
            }
            error = Math.Min(error, (float)Math.Sqrt(maxEdgeSq) * config.SimplifyErrorEdgeLimit);
        }

        var result = new uint[newCount];
        simplified.AsSpan(0, (int)newCount).CopyTo(result);
        return result;
    }

    private static ClusterLodBounds BoundsCompute(Vector3[] positions, uint[] indices, float error)
    {
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions.AsSpan());
        var b = Meshopt.ComputeClusterBounds(indices.AsSpan(), posSpan, (nuint)Unsafe.SizeOf<Vector3>());
        
        Vector3 center;
        unsafe { center = *(Vector3*)b.center; }
        
        return new ClusterLodBounds { Center = center, Radius = b.radius, Error = error };
    }

    private static ClusterLodBounds BoundsMerge(List<BuilderMeshlet> clusters, List<int> group)
    {
        var centers = new float[group.Count * 3];
        var radii = new float[group.Count];
        float maxError = 0;
        for (int i = 0; i < group.Count; i++)
        {
            var c = clusters[group[i]];
            centers[i * 3 + 0] = c.LodCenter.X;
            centers[i * 3 + 1] = c.LodCenter.Y;
            centers[i * 3 + 2] = c.LodCenter.Z;
            radii[i] = c.LodRadius;
            maxError = Math.Max(maxError, c.Error);
        }

        MeshOptimizer.Bounds merged;
        unsafe
        {
            fixed (float* pCenters = centers)
            fixed (float* pRadii = radii)
            {
                merged = Meshopt.ComputeSphereBounds(
                    pCenters,
                    (nuint)group.Count,
                    (nuint)(sizeof(float) * 3),
                    pRadii,
                    (nuint)sizeof(float)
                );
            }
        }

        Vector3 mergedCenter;
        unsafe { mergedCenter = *(Vector3*)merged.center; }

        return new ClusterLodBounds { Center = mergedCenter, Radius = merged.radius, Error = maxError };
    }
}
