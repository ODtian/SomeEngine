using System.Runtime.InteropServices;

namespace SomeEngine.Core.Jobs.Internal;

[StructLayout(LayoutKind.Sequential)]
internal struct JobCounter
{
    public int Value;
    public int Version;
    public int FirstDependent; // 0 = null, -1 = Sentinel (Finished), >0 = Index
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobDependencyNode
{
    public JobId Job;
    public int Next; // 0 = null
}
