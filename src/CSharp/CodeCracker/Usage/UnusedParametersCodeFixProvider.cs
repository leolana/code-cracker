﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCracker.Usage
{
    [ExportCodeFixProvider("CodeCrackerUnusedParametersCodeFixProvider", LanguageNames.CSharp), Shared]
    public class UnusedParametersCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> GetFixableDiagnosticIds()
        {
            return ImmutableArray.Create(UnusedParametersAnalyzer.DiagnosticId);
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task ComputeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var parameter = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<ParameterSyntax>().First();
            context.RegisterFix(CodeAction.Create("Remove unused parameter: '\{parameter.Identifier.ValueText}'", c => RemoveParameter(root, context.Document, parameter, c)), diagnostic);
        }

        private async Task<Solution> RemoveParameter(SyntaxNode root, Document document, ParameterSyntax parameter, CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;
            var parameterList = (ParameterListSyntax)parameter.Parent;
            var parameterPosition = parameterList.Parameters.IndexOf(parameter);
            var newParameterList = parameterList.WithParameters(parameterList.Parameters.Remove(parameter));
            var newSolution = solution;
            var foundDocument = false;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var method = (BaseMethodDeclarationSyntax)parameter.Parent.Parent;
            var methodSymbol = semanticModel.GetDeclaredSymbol(method);
            var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken).ConfigureAwait(false);
            var documentGroups = references.SelectMany(r => r.Locations).GroupBy(loc => loc.Document);
            foreach (var documentGroup in documentGroups)
            {
                var referencingDocument = documentGroup.Key;
                SyntaxNode locRoot;
                SemanticModel locSemanticModel;
                var replacingArgs = new Dictionary<SyntaxNode, SyntaxNode>();
                if (referencingDocument.Equals(document))
                {
                    locSemanticModel = semanticModel;
                    locRoot = root;
                    replacingArgs.Add(parameterList, newParameterList);
                    foundDocument = true;
                }
                else
                {
                    locSemanticModel = await referencingDocument.GetSemanticModelAsync(cancellationToken);
                    locRoot = await locSemanticModel.SyntaxTree.GetRootAsync(cancellationToken);
                }
                foreach (var loc in documentGroup)
                {
                    var methodIdentifier = locRoot.FindNode(loc.Location.SourceSpan);
                    var objectCreation = methodIdentifier.Parent as ObjectCreationExpressionSyntax;
                    var arguments = objectCreation != null
                        ? objectCreation.ArgumentList
                        : methodIdentifier.FirstAncestorOfType<InvocationExpressionSyntax>().ArgumentList;
                    var newArguments = arguments.WithArguments(arguments.Arguments.RemoveAt(parameterPosition));
                    replacingArgs.Add(arguments, newArguments);
                }
                var newLocRoot = locRoot.ReplaceNodes(replacingArgs.Keys, (original, rewritten) => replacingArgs[original]);
                newSolution = newSolution.WithDocumentSyntaxRoot(referencingDocument.Id, newLocRoot);
            }
            if (!foundDocument)
            {
                var newRoot = root.ReplaceNode(parameterList, newParameterList);
                var newDocument = document.WithSyntaxRoot(newRoot);
                newSolution = newSolution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }
            return newSolution;
        }
    }
}