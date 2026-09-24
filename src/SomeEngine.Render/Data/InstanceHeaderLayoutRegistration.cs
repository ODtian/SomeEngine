using SomeEngine.Render.Data;
using SomeEngine.Render.Components;

[assembly: HeaderField(
    "BVHRootIndex",
    HeaderFieldType.UInt32,
    0,
    Source = false)]
[assembly: HeaderField(
    "SlotOffset",
    HeaderFieldType.UInt32,
    1,
    Source = false)]
[assembly: HeaderField(
    "InstanceDataOffset",
    HeaderFieldType.UInt32,
    2,
    InstanceMember = nameof(RenderInstance.DataOffset))]
[assembly: HeaderField(
    "InstanceDataFlags",
    HeaderFieldType.UInt32,
    3,
    InstanceMember = nameof(RenderInstance.DataFlags))]
[assembly: HeaderField(
    "BoundsExpansionWorld",
    HeaderFieldType.Float32,
    4,
    InstanceMember = nameof(RenderInstance.BoundsExpansion))]
[assembly: InstanceFlag(
    "MaterialOverride",
    0)]
