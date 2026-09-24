using System.Collections.Generic;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    private sealed class GraphScratch
    {
        public bool[] Needed = [];
        public bool[] LiveRes = [];
        public bool[] LivePass = [];
        public bool[] Buffers = [];
        public bool[] Written = [];
        public bool[][] Textures = [];
        public List<int>[] Readers = [];
        public int[] Indegrees = [];
        public int[] Order = [];
        public int[] Batches = [];
        public List<int>[] Edges = [];
        public readonly ReadyList Ready = new();
        public readonly Dictionary<EdgeKey, int> EdgeMap = new();
        public readonly Dictionary<QueueLinkKey, int> QueueLinks = [];
        public readonly List<int> AliasCandidates = [];
        public readonly List<AliasBlock> AliasBlocks = [];
        public readonly BarrierState Barriers = new();
        private int[] _resolveMarks = [];
        private int _resolveStamp;

        public void BeginCull(int resourceCount, int passCount)
        {
            EnsureResources(resourceCount);
            EnsurePasses(passCount);
            Array.Clear(Needed, 0, resourceCount);
            Array.Clear(LiveRes, 0, resourceCount);
            Array.Clear(LivePass, 0, passCount);
            for (int resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
            {
                bool[]? bits = Textures[resourceIndex];
                if (bits != null)
                    Array.Clear(bits, 0, bits.Length);
            }
        }

        public void BeginInitialization(int resourceCount)
        {
            EnsureResources(resourceCount);
            Array.Clear(Buffers, 0, resourceCount);
        }

        public void BeginDependencies(int resourceCount)
        {
            EnsureResources(resourceCount);
            EdgeMap.Clear();
            for (int resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
                Readers[resourceIndex].Clear();
        }

        public void BeginTopology(int passCount)
        {
            EnsurePasses(passCount);
            Array.Clear(Indegrees, 0, passCount);
            Array.Clear(Order, 0, passCount);
            Ready.Clear();
            for (int passSlot = 0; passSlot < passCount; passSlot++)
                Edges[passSlot].Clear();
        }

        public void BeginQueues(int passCount)
        {
            EnsurePasses(passCount);
            Array.Fill(Batches, -1, 0, passCount);
            QueueLinks.Clear();
        }

        public void BeginAliases()
        {
            AliasCandidates.Clear();
            AliasBlocks.Clear();
        }

        public void BeginWrites(int resourceCount)
        {
            EnsureResources(resourceCount);
            Array.Clear(Written, 0, resourceCount);
        }

        public bool[] TextureBits(int resourceIndex, int count)
        {
            bool[] bits = EnsureTextureBits(resourceIndex, count);
            Array.Clear(bits, 0, count);
            return bits;
        }

        public bool[] EnsureTextureBits(int resourceIndex, int count)
        {
            bool[]? bits = Textures[resourceIndex];
            if (bits == null || bits.Length < count)
            {
                bits = new bool[count];
                Textures[resourceIndex] = bits;
            }

            return bits;
        }

        public void BeginResolve(int resourceCount)
        {
            if (_resolveMarks.Length < resourceCount)
                _resolveMarks = new int[resourceCount];

            _resolveStamp++;
            if (_resolveStamp != int.MaxValue)
                return;

            Array.Clear(_resolveMarks, 0, _resolveMarks.Length);
            _resolveStamp = 1;
        }

        public bool MarkResolve(int resourceIndex)
        {
            if (_resolveMarks[resourceIndex] == _resolveStamp)
                return false;

            _resolveMarks[resourceIndex] = _resolveStamp;
            return true;
        }

        private void EnsureResources(int count)
        {
            if (Needed.Length < count)
                Array.Resize(ref Needed, count);
            if (LiveRes.Length < count)
                Array.Resize(ref LiveRes, count);
            if (Buffers.Length < count)
                Array.Resize(ref Buffers, count);
            if (Written.Length < count)
                Array.Resize(ref Written, count);
            if (Textures.Length < count)
                Array.Resize(ref Textures, count);
            if (Readers.Length < count)
            {
                int oldCount = Readers.Length;
                Array.Resize(ref Readers, count);
                for (int resourceIndex = oldCount; resourceIndex < count; resourceIndex++)
                    Readers[resourceIndex] = [];
            }
        }

        private void EnsurePasses(int count)
        {
            if (LivePass.Length < count)
                Array.Resize(ref LivePass, count);
            if (Indegrees.Length < count)
                Array.Resize(ref Indegrees, count);
            if (Order.Length < count)
                Array.Resize(ref Order, count);
            if (Batches.Length < count)
                Array.Resize(ref Batches, count);
            if (Edges.Length < count)
            {
                int oldCount = Edges.Length;
                Array.Resize(ref Edges, count);
                for (int passSlot = oldCount; passSlot < count; passSlot++)
                    Edges[passSlot] = [];
            }
        }
    }

    private readonly record struct EdgeKey(
        int Source,
        int Target,
        int Resource,
        int InVersion,
        int OutVersion);

    private readonly record struct QueueLinkKey(
        int Source,
        int Target);

    private sealed class ReadyList
    {
        private readonly List<int> _values = [];

        public int Count => _values.Count;

        public int Min => _values[0];

        public void Add(int value)
        {
            int index = _values.BinarySearch(value);
            if (index >= 0)
                return;

            _values.Insert(~index, value);
        }

        public void Remove(int value)
        {
            int index = _values.BinarySearch(value);
            if (index >= 0)
                _values.RemoveAt(index);
        }

        public void Clear()
            => _values.Clear();

        public List<int>.Enumerator GetEnumerator()
            => _values.GetEnumerator();
    }
}
