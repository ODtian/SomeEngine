using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Buffers;
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
        return new ClusterLodConfig {
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

    private static void BuildClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
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

            Clusterize(config, indices, positions, clusters, globalIndices);
            int nextGroupId = 0;
            var globalSpan = CollectionsMarshal.AsSpan(
                globalIndices
            ); // Only valid if list doesn't resize?
            // WARNING: globalIndices grows inside the loop. The span will be
            // invalidated. We must re-get the span or access via List indexer.
            // Accessing via list indexer is safe.

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
                var currentSpan = CollectionsMarshal.AsSpan(globalIndices)
                                      .Slice(c.IndicesOffset, c.IndicesCount);

                var b = BoundsCompute(positions, currentSpan, 0);
                c.Center = b.Center;
                c.Radius = b.Radius;
                c.LodCenter = b.Center;
                c.LodRadius = b.Radius;
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
                        var cInds =
                            currentGlobalSpan.Slice(c.IndicesOffset, c.IndicesCount);
                        for (int k = 0; k < cInds.Length; k++)
                            mergedIndices.Add(cInds[k]);
                    }

                    int targetSize =
                        (int)((mergedIndices.Count / 3) * config.SimplifyRatio) * 3;
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

                    if (simplifiedIndices.Count >
                        mergedIndices.Count * config.SimplifyThreshold)
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
                        clusters,
                        globalIndices
                    );

                    int newClustersEnd = clusters.Count;

                    for (int k = newClustersStart; k < newClustersEnd; k++)
                    {
                        var sc = clusters[k];
                        sc.Level = depth + 1;
                        sc.Center = groupBounds.Center;
                        sc.Radius = groupBounds.Radius;
                        sc.Error = groupError;
                        sc.GroupId = thisGroupId;
                        sc.LodCenter = groupBounds.Center;
                        sc.LodRadius = groupBounds.Radius;
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
            nuint vertexCount = Meshopt.GenerateVertexRemap(
                remap.AsSpan(0, rawPos.Length), rawIndices.AsSpan(), rawPos.AsSpan()
            );

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
                var newData =
                    ArrayPool<float>.Shared.Rent((int)vertexCount * attr.Dimension);
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
                pAttributes.Add(new RawAttribute(
                    attr.Name,
                    newData,
                    attr.Dimension,
                    attr.TargetType,
                    attr.NumComponents,
                    attr.Normalized
                ));
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

            BuildClusterLod(
                ClusterLodConfig.GetDefault() with { ClusterSpatial = true },
                new ReadOnlySpan<Vector3>(pPos, 0, (int)vertexCount),
                new ReadOnlySpan<uint>(pInd, 0, rawIndices.Length),
                allMeshlets,
                globalIndices
            );

            // Sort clusters
            allMeshlets.Sort((a, b) => {
                int c = a.Level.CompareTo(b.Level);
                if (c != 0) return c;
                return a.ParentGroupId.CompareTo(b.ParentGroupId);
            });

            // 2. Page Generation & Quantization
            var pagesDataList = new List<MeshPageInfo>();

            var currentClusters = new List<GPUCluster>();
            var currentPositions = new List<ushort>();
            var currentAttrs = new List<byte>();
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

                uint clustersOffset = PageHeaderSize;
                int clustersSize =
                    currentClusters.Count * Unsafe.SizeOf<GPUCluster>();

                uint positionsOffset = clustersOffset + (uint)clustersSize;
                int positionsSize = currentPositions.Count * sizeof(ushort);

                uint attributesOffset = positionsOffset + (uint)positionsSize;
                int attrsSize = currentAttrs.Count;

                uint indicesOffset = attributesOffset + (uint)attrsSize;
                int indicesSize = currentIndices.Count;

                int totalSize = (int)indicesOffset + indicesSize;
                if (totalSize > reusablePageBuffer.Length)
                {
                    throw new Exception(
                        $"Page buffer overflow: {totalSize} > {reusablePageBuffer.Length}"
                    );
                }

                Array.Clear(
                    reusablePageBuffer, 0, totalSize
                ); // Only clear used part or optimize? Clear all is safer.
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
                    .Cast<GPUCluster, byte>(
                        CollectionsMarshal.AsSpan(currentClusters)
                    )
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

            var usedMap = new Dictionary<uint, ushort>(MaxVerticesPerMeshlet);
            var localPos = new List<ushort>(MaxVerticesPerMeshlet * 3);
            var localIndices = new List<byte>(MaxTrianglesPerMeshlet * 3);
            var localAttrBytes =
                new List<byte>(MaxVerticesPerMeshlet * vertexStride);

            var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

            foreach (var m in allMeshlets)
            {
                int vCount = 0;
                var mIndices =
                    globalIndicesSpan.Slice(m.IndicesOffset, m.IndicesCount);

                usedMap.Clear();
                localPos.Clear();
                localIndices.Clear();
                localAttrBytes.Clear();

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

                        Vector3 p = pPos[(int)globalIdx];
                        Vector3 rel = (p - center) / radius;
                        ushort qx =
                            (ushort)((Math.Clamp(rel.X, -1f, 1f) * 0.5f + 0.5f) *
                                     65535f);
                        ushort qy =
                            (ushort)((Math.Clamp(rel.Y, -1f, 1f) * 0.5f + 0.5f) *
                                     65535f);
                        ushort qz =
                            (ushort)((Math.Clamp(rel.Z, -1f, 1f) * 0.5f + 0.5f) *
                                     65535f);

                        localPos.Add(qx);
                        localPos.Add(qy);
                        localPos.Add(qz);

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
                ) { Center =
                        new SomeEngine.Assets.Schema.Vec3() { X = 0, Y = 0, Z = 0 },
                    Radius = 0 },
                Payload = new byte[fs.Length],
                Attributes = schemaAttrs
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

    private static void Clusterize(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
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

        var meshlets =
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Rent((int)maxMeshlets);
        var meshletVertices =
            ArrayPool<uint>.Shared.Rent((int)maxMeshlets * config.MaxVertices);
        var meshletTriangles =
            ArrayPool<byte>.Shared.Rent((int)maxMeshlets * config.MaxTriangles * 3);

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
                        meshletVertices.AsSpan(
                            (int)m.vertex_offset, (int)m.vertex_count
                        ),
                        meshletTriangles.AsSpan(
                            (int)m.triangle_offset, (int)m.triangle_count * 3
                        ),
                        m.triangle_count,
                        m.vertex_count
                    );
                }

                int startIndex = globalIndices.Count;
                int count = (int)m.triangle_count * 3;

                // Instead of adding one by one, we can batch add if List supports it
                // or ensure capacity List<T> doesn't expose span-based add easily
                // without CollectionsMarshal We'll resize globalIndices manually if
                // needed or just loop. Or better: ensure capacity.

                // Calculate required capacity
                // int required = startIndex + count;
                // if (globalIndices.Capacity < required) globalIndices.Capacity =
                // required; Wait, Capacity setter might copy.

                // We'll just loop for now, optimization of list add is secondary to
                // algorithm structure. Or use CollectionsMarshal to get span to the
                // end.

                // Using CollectionsMarshal for zero-copy add:
                // globalIndices.EnsureCapacity(globalIndices.Count + count);
                // But List doesn't expose size easily.

                for (uint t = 0; t < m.triangle_count; t++)
                {
                    int triOffset = (int)m.triangle_offset + (int)t * 3;
                    globalIndices.Add(
                        meshletVertices
                        [(int)m.vertex_offset + meshletTriangles[triOffset + 0]]
                    );
                    globalIndices.Add(
                        meshletVertices
                        [(int)m.vertex_offset + meshletTriangles[triOffset + 1]]
                    );
                    globalIndices.Add(
                        meshletVertices
                        [(int)m.vertex_offset + meshletTriangles[triOffset + 2]]
                    );
                }

                clusters.Add(new BuilderMeshlet {
                    IndicesOffset = startIndex,
                    IndicesCount = count,
                    VertexCount = (int)m.vertex_count,
                    GroupId = -1,
                    ParentGroupId = -1
                });
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
                var cIndices =
                    globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
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
                var partitionPoint =
                    ArrayPool<float>.Shared.Rent((int)partitionCount * 3);
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

            new Span<int>(sortedPending, 0, pending.Count)
                .CopyTo(CollectionsMarshal.AsSpan(pending));

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
            locks[i] &= unchecked((byte) ~(LockBit | SeenBit));

        var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

        for (int g = 0; g < groupOffsets.Count - 1; g++)
        {
            int start = groupOffsets[g];
            int count = groupOffsets[g + 1] - start;
            var group = CollectionsMarshal.AsSpan(pending).Slice(start, count);

            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices =
                    globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= (byte)((locks[(int)r] & SeenBit) >> 7);
                }
            }
            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices =
                    globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
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
            // outputIndices.AddRange(indices); // Add range span...
            // Assuming we want to copy indices to output.
            var span = CollectionsMarshal.AsSpan(outputIndices);
            // Wait, AddRange(Span) is not available on List<T> standard
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
            options |= (SimplificationOptions)2;                    // Sparse
            options |= (SimplificationOptions)4;                    // ErrorAbsolute
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
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float error
    )
    {
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        var b = Meshopt.ComputeClusterBounds(
            indices, posSpan, (nuint)Unsafe.SizeOf<Vector3>()
        );

        Vector3 center;
        unsafe
        {
            center = *(Vector3 *)b.center;
        }

        return new ClusterLodBounds {
            Center = center, Radius = b.radius, Error = error
        };
    }

    private static ClusterLodBounds BoundsMerge(
        List<BuilderMeshlet> clusters, ReadOnlySpan<int> group
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
                centers[i * 3 + 0] = c.LodCenter.X;
                centers[i * 3 + 1] = c.LodCenter.Y;
                centers[i * 3 + 2] = c.LodCenter.Z;
                radii[i] = c.LodRadius;
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
                mergedCenter = *(Vector3 *)merged.center;
            }
            return new ClusterLodBounds {
                Center = mergedCenter, Radius = merged.radius, Error = maxError
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(centers);
            ArrayPool<float>.Shared.Return(radii);
        }
    }
}
