using SomeEngine.Render.Data;

[assembly: GpuInstanceHeaderField(
    "BVHRootIndex",
    GpuInstanceHeaderFieldType.UInt32,
    0)]
[assembly: GpuInstanceHeaderField(
    "MaterialSlotOffset",
    GpuInstanceHeaderFieldType.UInt32,
    1)]
[assembly: GpuInstanceHeaderField(
    "InstanceDataOffset",
    GpuInstanceHeaderFieldType.UInt32,
    2)]
[assembly: GpuInstanceHeaderField(
    "InstanceDataFlags",
    GpuInstanceHeaderFieldType.UInt32,
    3)]
[assembly: GpuInstanceHeaderField(
    "BoundsExpansionWorld",
    GpuInstanceHeaderFieldType.Float32,
    4)]
[assembly: GpuInstanceDataFlag(
    "MaterialOverride",
    0)]
