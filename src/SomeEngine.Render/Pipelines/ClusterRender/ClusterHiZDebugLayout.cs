namespace SomeEngine.Render.Pipelines;

internal static class ClusterHiZDebugLayout
{
    public const uint MaxSamples = 4096;
    public const uint HeaderBytes = 128;
    public const uint HeaderWords = HeaderBytes / sizeof(uint);
    public const uint SampleStrideBytes = 80;
    public const uint BufferBytes = HeaderBytes + MaxSamples * SampleStrideBytes;

    public const int SampleCountWord = 0;
    public const int Phase1InputWord = 1;
    public const int Phase1LodRejectedWord = 2;
    public const int Phase1NoHistoryWord = 3;
    public const int Phase1TestedWord = 4;
    public const int Phase1DeferredWord = 5;
    public const int Phase1DrawnWord = 6;
    public const int Phase2InputWord = 7;
    public const int Phase2LodRejectedWord = 8;
    public const int Phase2TestedWord = 9;
    public const int Phase2CulledWord = 10;
    public const int Phase2DrawnWord = 11;
    public const int HiZDisabledWord = 12;
    public const int SampleOverflowWord = 13;
    public const int Phase1DepthVisibleWord = 14;
    public const int Phase2DepthVisibleWord = 15;
    public const int SampleCapacityWord = 18;
    public const int SampleStrideBytesWord = 19;
    public const int HeaderBytesWord = 20;
}
