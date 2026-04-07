// wave_queue.slang — Wave-Level Generic Task Distribution
// Zero LDS, zero barriers. Pure register + wave intrinsics.
//
// Usage:
//   struct MyTask : IWaveTask { ... };
//   MyTask task = { ... };
//   WaveQueue::Distribute(task);

#ifndef WAVE_QUEUE_SLANG
#define WAVE_QUEUE_SLANG

/// Generic interface for wave-distributed tasks.
/// Each lane declares how many sub-tasks it produces via GetTaskCount().
/// The distributor maps consumer lanes to producer lanes and calls ExecuteTask().
interface IWaveTask
{
    /// Number of sub-tasks this lane produces.
    [mutating] uint GetTaskCount();

    /// Execute one sub-task.
    /// @param srcLane   The producer lane index. Use WaveReadLaneAt(field, srcLane) to pull data.
    /// @param localIdx  Which sub-task within the producer (0 .. GetTaskCount()-1).
    [mutating] void ExecuteTask(uint srcLane, uint localIdx, bool bActive);
};

struct WaveQueue
{
    // Find position of the n-th set bit (0-indexed) in a 32-bit mask.
    // Binary search from MSB, O(log2(32)) = 5 steps.
    static uint FindNthSetBit(uint mask, int n)
    {
        int last = int(countbits(mask)) - n - 1;
        uint p = 16u;
        p += (countbits(mask >> p) <= uint(last)) ? -8u : 8u;
        p += (countbits(mask >> p) <= uint(last)) ? -4u : 4u;
        p += (countbits(mask >> p) <= uint(last)) ? -2u : 2u;
        p += (countbits(mask >> p) <= uint(last)) ? -1u : 1u;
        p  = (countbits(mask >> p) == uint(last)) ? (p - 1u) : p;
        return p;
    }

    /// Distribute sub-tasks across all lanes in the current wave.
    /// Handles empty producers (myCount==0) via compact index mapping.
    static void Distribute<T : IWaveTask>(inout T task)
    {
        uint myCount  = task.GetTaskCount();
        uint myOffset = WavePrefixSum(myCount);
        uint totalTasks = WaveActiveSum(myCount);

        if (totalTasks == 0)
            return;

        uint laneIdx  = WaveGetLaneIndex();
        uint waveSize = WaveGetLaneCount();

        uint nonEmptyMask = WaveActiveBallot(myCount > 0).x;
        uint numProducers = countbits(nonEmptyMask);
        uint maxCompact   = numProducers > 0 ? numProducers - 1 : 0;

        uint compactHead = 0;
        uint workHead    = 0;

        while (workHead < totalTasks)
        {
            // Each producer marks its last output slot in this batch
            uint bmask = 0;
            if (myCount > 0)
            {
                int lastSlot = int(myOffset + myCount - 1) - int(workHead);
                if (lastSlot >= 0 && uint(lastSlot) < waveSize)
                    bmask = 1u << uint(lastSlot);
            }
            uint boundaryBits = WaveActiveBitOr(bmask);

            // Consumer: find which producer owns this task
            uint taskIdx    = workHead + laneIdx;
            uint lowerMask  = (laneIdx > 0) ? ((1u << laneIdx) - 1u) : 0u;
            uint prodOffset = countbits(boundaryBits & lowerMask);
            uint compactIdx = compactHead + prodOffset;
            uint srcLane    = FindNthSetBit(nonEmptyMask, int(min(compactIdx, maxCompact)));

            uint srcStart = WaveReadLaneAt(myOffset, srcLane);
            uint localIdx = taskIdx - srcStart;

            // Execute unconditionally — WaveReadLaneAt inside ExecuteTask
            // requires all lanes active to avoid UNDEFINED reads.
            task.ExecuteTask(srcLane, localIdx, taskIdx < totalTasks);

            workHead    += waveSize;
            compactHead += countbits(boundaryBits);
        }
    }
};

#endif // WAVE_QUEUE_SLANG
