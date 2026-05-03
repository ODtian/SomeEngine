using Diligent;
using NetEscapades.EnumGenerators;

[assembly: EnumExtensions<ComparisonFunction>(
    ExtensionClassNamespace = "SomeEngine.Render.Materials",
    IsInternal = true)]
[assembly: EnumExtensions<StencilOp>(
    ExtensionClassNamespace = "SomeEngine.Render.Materials",
    IsInternal = true)]

