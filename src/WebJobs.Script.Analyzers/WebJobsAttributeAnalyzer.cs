using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Microsoft.Azure.Functions.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class WebJobsAttributeAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(DiagnosticDescriptors.IllegalFunctionName); } }

        public static void VerifyWebJobsLoaded()
        {
            // Check if WebJobs types can be loaded
            var jobHost = new JobHost(new OptionsWrapper<JobHostOptions>(new JobHostOptions()), null);
        }

        public override void Initialize(AnalysisContext context)
        {
            // https://stackoverflow.com/questions/62638455/analyzer-with-code-fix-project-template-is-broken
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            VerifyWebJobsLoaded();

            // Analyze method signatures.
            context.RegisterSyntaxNodeAction(AnalyzeMethodDeclarationNode, SyntaxKind.MethodDeclaration);

            // Hook compilation to get the assemblies' references and build the WebJob tooling interfaces.
            context.RegisterCompilationStartAction(AnalyzeCompilation);
        }

        private void AnalyzeCompilation(CompilationStartAnalysisContext context)
        {
            var compilation = context.Compilation;

            // cast to PortableExecutableReference which has a file path
            var references = compilation.References.OfType<PortableExecutableReference>().ToArray();
            var webJobsPath = (from reference in references
                               where IsWebJobsSdk(reference)
                               select reference.FilePath).SingleOrDefault();

            if (webJobsPath == null)
            {
                return; // Not a WebJobs project.
            }
        }

        private bool IsWebJobsSdk(PortableExecutableReference reference)
        {
            if (reference.FilePath.EndsWith("Microsoft.Azure.WebJobs.dll"))
            {
                return true;
            }
            return false;
        }

        // This is called extremely frequently
        // Analyze the method signature to validate binding attributes + types on the parameters
        private void AnalyzeMethodDeclarationNode(SyntaxNodeAnalysisContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;

            CheckForFunctionNameAttributeAndReport(context, methodDecl);
        }

        // First argument to the FunctionName ctor.
        private string GetFunctionNameFromAttribute(SemanticModel semantics, AttributeSyntax attributeSyntax)
        {
            if (attributeSyntax.ArgumentList.Arguments.Count == 0)
            {
                return null;
            }

            var firstArg = attributeSyntax.ArgumentList.Arguments[0];
            var val = semantics.GetConstantValue(firstArg.Expression);

            return val.Value as string;
        }

        // Quick check for [FunctionName] attribute on a method.
        // Reports a diagnostic if the name doesn't meet requirements.
        private void CheckForFunctionNameAttributeAndReport(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax methodDeclarationSyntax)
        {
            foreach (var attrListSyntax in methodDeclarationSyntax.AttributeLists)
            {
                foreach (AttributeSyntax attributeSyntax in attrListSyntax.Attributes)
                {
                    // Perf - Can we get the name without doing a symbol resolution?
                    var test = attributeSyntax.GetText();
                    var symAttributeCtor = context.SemanticModel.GetSymbolInfo(attributeSyntax).Symbol;
                    if (symAttributeCtor != null)
                    {
                        var attrType = symAttributeCtor.ContainingType;
                        if (attrType.Name != nameof(FunctionNameAttribute))
                        {
                            return;
                        }

                        // Validate the FunctionName
                        var functionName = GetFunctionNameFromAttribute(context.SemanticModel, attributeSyntax);

                        bool match = FunctionNameAttribute.FunctionNameValidationRegex.IsMatch(functionName);
                        if (!match)
                        {
                            var error = Diagnostic.Create(DiagnosticDescriptors.IllegalFunctionName, attributeSyntax.GetLocation(), functionName);
                            context.ReportDiagnostic(error);
                        }

                        return;
                    }
                }
            }
        }
    }
}
