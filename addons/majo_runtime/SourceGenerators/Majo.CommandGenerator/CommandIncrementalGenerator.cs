#nullable enable

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Majo.CommandGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class CommandIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Majo.Runtime.Dialogue.Commands.CommandAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => CommandParser.Parse(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(classDeclarations, static (spc, model) =>
        {
            if (model is null)
            {
                return;
            }

            foreach (var diag in model.Diagnostics)
            {
                spc.ReportDiagnostic(diag);
            }

            if (model.HasErrors)
            {
                return;
            }

            var source = CommandEmitter.Emit(model);
            spc.AddSource($"{model.ContainingNamespace}.{model.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
}
