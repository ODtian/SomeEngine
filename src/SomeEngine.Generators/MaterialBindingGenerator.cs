using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

/// <summary>
/// Incremental Source Generator that auto-generates ApplyToSRB() for IShaderParams implementations.
/// Scans fields/properties marked with [ShaderParam] and detects
/// composed IShaderParams fields for delegation.
/// </summary>
[Generator]
public class ShaderParamsGenerator : IIncrementalGenerator
{
    private const string ShaderParamAttr = "SomeEngine.Render.Materials.ShaderParamAttribute";

    private const string IShaderParamsType = "SomeEngine.Render.Materials.IShaderParams";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax cds &&
                    cds.Modifiers.Any(m => m.Text == "partial"),
                transform: static (ctx, _) => GetClassInfo(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!.Value);

        context.RegisterSourceOutput(candidates, static (spc, info) =>
        {
            var source = GenerateApplyToSRB(info);
            spc.AddSource($"{info.ClassName}_ShaderParams.g.cs", source);
        });
    }

    private static ShaderParamsClassInfo? GetClassInfo(GeneratorSyntaxContext ctx)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol symbol)
            return null;

        bool implementsIShaderParams = ImplementsInterface(symbol, IShaderParamsType);

        if (!implementsIShaderParams)
            return null;

        // Collect [ShaderParam] fields/properties (declared on THIS class only)
        var bindings = ImmutableArray.CreateBuilder<BindingInfo>();
        // Collect IShaderParams-typed fields for composition delegation
        var composedParams = ImmutableArray.CreateBuilder<string>();

        foreach (var member in symbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            // Check for IShaderParams-typed fields/properties (composition)
            ITypeSymbol? memberType = member switch
            {
                IFieldSymbol f => f.Type,
                IPropertySymbol p => p.Type,
                _ => null
            };

            if (memberType != null && ImplementsInterface(memberType, IShaderParamsType)
                && memberType.ToDisplayString() != IShaderParamsType)
            {
                string memberName = member is IFieldSymbol fs ? fs.Name
                    : member is IPropertySymbol ps ? ps.Name : "";
                if (!string.IsNullOrEmpty(memberName))
                {
                    // Don't treat [ShaderParam]-annotated fields as composed params
                    bool hasShaderParamAttr = member.GetAttributes()
                        .Any(a => a.AttributeClass?.ToDisplayString() == ShaderParamAttr);
                    if (!hasShaderParamAttr)
                        composedParams.Add(memberName);
                }
            }

            // Check for [ShaderParam] attribute
            string? shaderName = null;
            bool hasAttr = false;
            var stage = "Diligent.ShaderType.Compute";
            bool dynamic = false;

            foreach (var attr in member.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == ShaderParamAttr)
                {
                    hasAttr = true;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string name &&
                        !string.IsNullOrEmpty(name))
                    {
                        shaderName = name;
                    }

                    // Read named arguments
                    foreach (var namedArg in attr.NamedArguments)
                    {
                        if (namedArg.Key == "Stage" && namedArg.Value.Value is int stageVal)
                        {
                            stage = $"(Diligent.ShaderType){stageVal}";
                        }
                        else if (namedArg.Key == "Dynamic" && namedArg.Value.Value is bool dynVal)
                        {
                            dynamic = dynVal;
                        }
                    }
                    break;
                }
            }

            if (!hasAttr)
                continue;

            string fieldName;
            string typeName;
            if (member is IFieldSymbol field)
            {
                fieldName = field.Name;
                typeName = field.Type.ToDisplayString();
            }
            else if (member is IPropertySymbol prop)
            {
                fieldName = prop.Name;
                typeName = prop.Type.ToDisplayString();
            }
            else
            {
                continue;
            }

            shaderName ??= fieldName;

            var kind = typeName switch
            {
                "SomeEngine.Render.Materials.TextureSlot" => SlotKind.Texture,
                "SomeEngine.Render.Materials.BufferSlot" => SlotKind.Buffer,
                "SomeEngine.Render.Materials.SamplerSlot" => SlotKind.Sampler,
                _ => SlotKind.Unknown,  // Scalars — skip for now (future: ConstantBuffer)
            };

            if (kind == SlotKind.Unknown)
                continue;

            bindings.Add(new BindingInfo(fieldName, shaderName, kind, stage, dynamic));
        }

        if (bindings.Count == 0 && composedParams.Count == 0)
            return null;

        return new ShaderParamsClassInfo(
            symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            bindings.ToImmutable(),
            composedParams.ToImmutable());
    }


    private static bool ImplementsInterface(ITypeSymbol symbol, string interfaceName)
    {
        return symbol.AllInterfaces.Any(i => i.ToDisplayString() == interfaceName);
    }

    private static string GenerateApplyToSRB(ShaderParamsClassInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using Diligent;");
        sb.AppendLine();
        sb.AppendLine($"namespace {info.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"partial class {info.ClassName}");
        sb.AppendLine("{");

        sb.AppendLine("    public void ApplyToSRB(Diligent.IShaderResourceBinding srb)");
        sb.AppendLine("    {");

        sb.AppendLine();

        // Emit composed IShaderParams field delegations
        foreach (var composed in info.ComposedParams)
        {
            sb.AppendLine($"        {composed}.ApplyToSRB(srb);");
        }

        if (info.ComposedParams.Length > 0 && info.Bindings.Length > 0)
            sb.AppendLine();

        // Emit [ShaderParam] field bindings
        foreach (var binding in info.Bindings)
        {
            var flags = binding.Dynamic
                ? "Diligent.SetShaderResourceFlags.None"
                : "Diligent.SetShaderResourceFlags.AllowOverwrite";

            switch (binding.Kind)
            {
                case SlotKind.Texture:
                    sb.AppendLine($"        if ({binding.FieldName}.View is not null)");
                    sb.AppendLine($"            srb.GetVariableByName({binding.Stage}, \"{binding.ShaderName}\")");
                    sb.AppendLine($"                ?.Set({binding.FieldName}.View, {flags});");
                    sb.AppendLine();
                    break;

                case SlotKind.Buffer:
                    sb.AppendLine($"        if ({binding.FieldName}.View is not null)");
                    sb.AppendLine($"            srb.GetVariableByName({binding.Stage}, \"{binding.ShaderName}\")");
                    sb.AppendLine($"                ?.Set({binding.FieldName}.View, {flags});");
                    sb.AppendLine($"        else if ({binding.FieldName}.Buffer is not null)");
                    sb.AppendLine($"            srb.GetVariableByName({binding.Stage}, \"{binding.ShaderName}\")");
                    sb.AppendLine($"                ?.Set({binding.FieldName}.Buffer, {flags});");
                    sb.AppendLine();
                    break;

                case SlotKind.Sampler:
                    sb.AppendLine($"        if ({binding.FieldName}.Sampler is not null)");
                    sb.AppendLine($"            srb.GetVariableByName({binding.Stage}, \"{binding.ShaderName}\")");
                    sb.AppendLine($"                ?.Set({binding.FieldName}.Sampler, {flags});");
                    sb.AppendLine();
                    break;
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}

internal enum SlotKind
{
    Unknown,
    Texture,
    Buffer,
    Sampler,
}

internal readonly struct BindingInfo
{
    public readonly string FieldName;
    public readonly string ShaderName;
    public readonly SlotKind Kind;
    public readonly string Stage;
    public readonly bool Dynamic;

    public BindingInfo(string fieldName, string shaderName, SlotKind kind, string stage, bool dynamic)
    {
        FieldName = fieldName;
        ShaderName = shaderName;
        Kind = kind;
        Stage = stage;
        Dynamic = dynamic;
    }
}

internal readonly struct ShaderParamsClassInfo
{
    public readonly string Namespace;
    public readonly string ClassName;
    public readonly ImmutableArray<BindingInfo> Bindings;
    public readonly ImmutableArray<string> ComposedParams;

    public ShaderParamsClassInfo(
        string ns, string className,
        ImmutableArray<BindingInfo> bindings, ImmutableArray<string> composedParams)
    {
        Namespace = ns;
        ClassName = className;
        Bindings = bindings;
        ComposedParams = composedParams;
    }
}
