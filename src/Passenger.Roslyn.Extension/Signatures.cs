using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Rename;

namespace Passenger.Roslyn.Extension;

/// <summary>Change Signature: a method's or constructor's parameters added, removed, reordered, renamed or
/// retyped, its return type and name changed, through every declaration in its hierarchy (what it overrides and
/// implements, and what overrides and implements it) and every call, each call's arguments following their parameters
/// and passing a new one's value. Parameters and the method are renamed by Roslyn's Renamer, which resolves conflicts.</summary>
internal static class Signatures
{
    private static readonly string[] Modifiers = ["", "ref", "out", "in", "params", "this", "ref readonly", "scoped", "scoped ref", "this ref", "ref this", "this in", "in this"];

    public static async Task<Signature> DescribeAsync(Document document, Place at, CancellationToken cancel)
    {
        (IMethodSymbol? method, string? problem) = await FindAsync(document, at, cancel).ConfigureAwait(false);
        if (method is not null && (await DeclarationsAsync(method, cancel).ConfigureAwait(false)).FirstOrDefault() is { } declaration)
        {
            return new Signature(null, method.MethodKind == MethodKind.Constructor ? "constructor" : "method", method.ContainingType.Name,
                string.Join(" ", declaration.Modifiers.Select(m => m.Text)),
                method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name,
                declaration is MethodDeclarationSyntax named ? named.ReturnType.ToString() : string.Empty,
                [.. declaration.ParameterList.Parameters.Select(ParameterOf)], method.IsExtensionMethod);
        }
        problem ??= $"Unable to change signature of {Describe(method!)}: it is declared as a record's or a type's parameters";
        return new Signature(problem, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], false);
    }

    public static async Task<Refactored> ChangeAsync(Document document, SignatureChange change, CancellationToken cancel)
    {
        (IMethodSymbol? method, string? problem) = await FindAsync(document, change.At, cancel).ConfigureAwait(false);
        if (method is null)
        {
            return Changes.Refused(problem!);
        }
        if ((await DeclarationsAsync(method, cancel).ConfigureAwait(false)).FirstOrDefault() is not { } declaration)
        {
            return Changes.Refused($"Unable to change signature of {Describe(method)}: it is declared as a record's or a type's parameters");
        }
        IReadOnlyList<Parameter> original = [.. declaration.ParameterList.Parameters.Select(ParameterOf)];
        if (Problem(method, original, change) is { } wrong)
        {
            return Changes.Refused(wrong);
        }
        Solution before = document.Project.Solution;
        List<IMethodSymbol> family = await FamilyAsync(method, before, cancel).ConfigureAwait(false);
        if (family.FirstOrDefault(m => !m.Locations.Any(l => l.IsInSource)) is { } outside)
        {
            bool implements = outside.ContainingType.TypeKind == TypeKind.Interface;
            return Changes.Refused($"Change Signature is applied to {Describe(method)} that also {(implements ? "implements" : "overrides")} {Describe(outside)} "
                + $"that will not be {(implements ? "converted" : "changed")}. Please resolve the conflict manually.");
        }
        foreach (IMethodSymbol member in family)
        {
            if ((await DeclarationsAsync(member, cancel).ConfigureAwait(false)).Count == 0)
            {
                return Changes.Refused($"Unable to change signature of {Describe(member)}: it is declared as a record's or a type's parameters");
            }
        }
        List<MethodId> members = [.. family.Select(m => Identify(before, m))];
        Solution solution = await RenameParametersAsync(before, members, change, cancel).ConfigureAwait(false);
        SyntaxAnnotation marker = new();
        solution = await RestructureAsync(solution, members, Identify(before, method), original, change, marker, cancel).ConfigureAwait(false);
        if (method.MethodKind == MethodKind.Ordinary && change.Name != method.Name)
        {
            solution = await RenameMethodAsync(solution, marker, change.Name, cancel).ConfigureAwait(false);
        }
        return await Changes.BetweenAsync(before, solution, cancel).ConfigureAwait(false);
    }

    /// <summary>The method or constructor at a place: the one named there, declared or called (a constructor by the type
    /// a new names, or by base and this), else the one whose declaration holds the caret.</summary>
    private static async Task<(IMethodSymbol? Method, string? Problem)> FindAsync(Document document, Place at, CancellationToken cancel)
    {
        int offset = await Changes.OffsetAsync(document, at, cancel).ConfigureAwait(false);
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxToken token = Changes.TokenAt(root, offset);
        ISymbol? found = null;
        if (token.Parent is { } parent)
        {
            if (Called(parent) is { } call)
            {
                found = model.GetSymbolInfo(call, cancel) is { } info ? info.Symbol ?? info.CandidateSymbols.FirstOrDefault() : null;
            }
            else if (parent is BaseMethodDeclarationSyntax declared)
            {
                found = model.GetDeclaredSymbol(declared, cancel);
            }
        }
        found ??= token.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() is { } around ? model.GetDeclaredSymbol(around, cancel) : null;
        if (found is not IMethodSymbol method || method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.ReducedExtension))
        {
            return (null, "Change Signature changes a method or a constructor: put the caret on one, or on a call of one");
        }
        method = (method.ReducedFrom ?? method).OriginalDefinition;
        if (!method.Locations.Any(l => l.IsInSource))
        {
            return (null, $"Unable to change signature of {Describe(method)}: it is not declared in this solution's code");
        }
        return (method, null);
    }

    /// <summary>The call a name or keyword is the callee of: an invocation, an object creation (by its type's name or
    /// new), a constructor initializer's base or this, or a primary constructor's base type.</summary>
    private static SyntaxNode? Called(SyntaxNode name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node
            || node.Parent is MemberAccessExpressionSyntax access && access.Name == node
            || node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node
            || node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node)
        {
            node = node.Parent;
        }
        return node switch
        {
            BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax => node,
            _ => node.Parent switch
            {
                InvocationExpressionSyntax invocation when invocation.Expression == node => invocation,
                ObjectCreationExpressionSyntax creation when creation.Type == node => creation,
                PrimaryConstructorBaseTypeSyntax primary when primary.Type == node => primary,
                _ => null,
            },
        };
    }

    /// <summary>The method's declarations that carry its parameters (a partial method's two): none for one declared by a
    /// record's or type's own parameters.</summary>
    private static async Task<IReadOnlyList<BaseMethodDeclarationSyntax>> DeclarationsAsync(IMethodSymbol method, CancellationToken cancel)
    {
        List<BaseMethodDeclarationSyntax> declarations = [];
        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) is BaseMethodDeclarationSyntax declaration)
            {
                declarations.Add(declaration);
            }
        }
        return declarations;
    }

    private static Parameter ParameterOf(ParameterSyntax parameter) => new(
        string.Join(" ", parameter.Modifiers.Select(m => m.Text)),
        parameter.Type?.ToString() ?? string.Empty,
        parameter.Identifier.ValueText,
        parameter.Default?.Value.ToString() ?? string.Empty);

    /// <summary>Why the signature asked for cannot be made; null when it can.</summary>
    private static string? Problem(IMethodSymbol method, IReadOnlyList<Parameter> original, SignatureChange change)
    {
        if (method.MethodKind == MethodKind.Ordinary && !Changes.IsName(change.Name))
        {
            return change.Name.Trim().Length == 0 ? "Method name can not be empty" : $"'{change.Name}' is not a valid method name";
        }
        if (method.MethodKind == MethodKind.Ordinary && !IsType(change.ReturnType))
        {
            return "The return type is invalid";
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<int> kept = [];
        bool optional = false;
        for (int i = 0; i < change.Parameters.Count; i++)
        {
            ParameterChange parameter = change.Parameters[i];
            if (parameter.From >= original.Count || (parameter.From >= 0 && !kept.Add(parameter.From)))
            {
                return "A parameter is listed twice";
            }
            if (parameter.Name.Trim().Length == 0)
            {
                return "Parameter name can not be empty";
            }
            if (!Changes.IsName(parameter.Name))
            {
                return "A parameter has an invalid name";
            }
            if (!names.Add(parameter.Name))
            {
                return "Parameters should have different names";
            }
            if (!IsType(parameter.Type))
            {
                return "A parameter has an invalid type";
            }
            if (!Modifiers.Contains(Normalized(parameter.Modifier)))
            {
                return $"'{parameter.Modifier}' is not a parameter modifier";
            }
            if (!IsExpression(parameter.DefaultValue) || !IsExpression(parameter.CallValue))
            {
                return "Error in default value expression";
            }
            if (Normalized(parameter.Modifier) == "params" && i != change.Parameters.Count - 1)
            {
                return $"'{parameter.Name}' parameter must appear after all other parameters";
            }
            if (parameter.DefaultValue.Trim().Length > 0)
            {
                optional = true;
            }
            else if (optional && Normalized(parameter.Modifier) != "params")
            {
                return "Optional parameters must appear after all regular parameters";
            }
        }
        if (method.IsExtensionMethod && (change.Parameters.Count == 0 || change.Parameters[0].From != 0))
        {
            return $"The first parameter of the extension method '{method.Name}' must stay first";
        }
        return null;
    }

    private static string Normalized(string modifier) => string.Join(" ", modifier.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsType(string text) => text.Trim().Length > 0 && !SyntaxFactory.ParseTypeName(text).ContainsDiagnostics;

    private static bool IsExpression(string text) => text.Trim().Length == 0 || !SyntaxFactory.ParseExpression(text).ContainsDiagnostics;

    /// <summary>The method and every method its signature is tied to: what it overrides and implements, and what overrides
    /// and implements those, as far as they go both ways. A constructor stands alone.</summary>
    private static async Task<List<IMethodSymbol>> FamilyAsync(IMethodSymbol method, Solution solution, CancellationToken cancel)
    {
        HashSet<IMethodSymbol> family = new(SymbolEqualityComparer.Default);
        Queue<IMethodSymbol> next = new([method]);
        while (next.TryDequeue(out IMethodSymbol? member))
        {
            member = member.OriginalDefinition;
            if (!family.Add(member) || member.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }
            if (member.OverriddenMethod is { } overridden)
            {
                next.Enqueue(overridden);
            }
            foreach (ISymbol implemented in await SymbolFinder.FindImplementedInterfaceMembersAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
            {
                if (implemented is IMethodSymbol each)
                {
                    next.Enqueue(each);
                }
            }
            if (!member.Locations.Any(l => l.IsInSource))
            {
                continue;
            }
            foreach (ISymbol overriding in await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
            {
                if (overriding is IMethodSymbol each)
                {
                    next.Enqueue(each);
                }
            }
            if (member.ContainingType.TypeKind == TypeKind.Interface || member.IsAbstract || member.IsVirtual)
            {
                foreach (ISymbol implementation in await SymbolFinder.FindImplementationsAsync(member, solution, cancellationToken: cancel).ConfigureAwait(false))
                {
                    if (implementation is IMethodSymbol each)
                    {
                        next.Enqueue(each);
                    }
                }
            }
        }
        return [.. family];
    }

    /// <summary>A member found again in a later solution: its documentation comment id in the project declaring it,
    /// which renaming its parameters leaves as it was.</summary>
    private sealed record MethodId(ProjectId Project, string Id);

    private static MethodId Identify(Solution solution, IMethodSymbol method) =>
        new(solution.GetDocumentId(method.Locations.First(l => l.IsInSource).SourceTree)!.ProjectId,
            DocumentationCommentId.CreateDeclarationId(method) ?? throw new InvalidOperationException($"{method.Name} has no documentation comment id"));

    private static async Task<IMethodSymbol?> ResolveAsync(Solution solution, MethodId member, CancellationToken cancel) =>
        await solution.GetProject(member.Project)!.GetCompilationAsync(cancel).ConfigureAwait(false) is { } compilation
            ? DocumentationCommentId.GetFirstSymbolForDeclarationId(member.Id, compilation) as IMethodSymbol
            : null;

    /// <summary>Each kept parameter given its new name in every declaration of the hierarchy, its uses and the calls'
    /// named arguments with it: first to a name of its own, so names that trade places never meet.</summary>
    private static async Task<Solution> RenameParametersAsync(Solution solution, IReadOnlyList<MethodId> members, SignatureChange change, CancellationToken cancel)
    {
        List<(MethodId Method, int Ordinal, string Name)> renames = [];
        foreach (MethodId member in members)
        {
            if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
            {
                continue;
            }
            foreach (ParameterChange parameter in change.Parameters.Where(p => p.From >= 0 && p.From < method.Parameters.Length))
            {
                if (method.Parameters[parameter.From].Name != parameter.Name)
                {
                    renames.Add((member, parameter.From, parameter.Name));
                }
            }
        }
        SymbolRenameOptions options = new(RenameOverloads: false, RenameInStrings: false, RenameInComments: true, RenameFile: false);
        foreach (bool final in new[] { false, true })
        {
            foreach ((MethodId member, int ordinal, string name) in renames)
            {
                if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
                {
                    continue;
                }
                // The same passing name for a parameter in every declaration, so a rename Roslyn carries to another finds it done.
                string to = final ? name : $"passenger__{ordinal}__{name}";
                if (method.Parameters[ordinal].Name != to)
                {
                    solution = await Renamer.RenameSymbolAsync(solution, method.Parameters[ordinal], options, to, cancel).ConfigureAwait(false);
                }
            }
        }
        return solution;
    }

    /// <summary>Every declaration's parameters and return type as asked, and every call's arguments in the new order with
    /// the new parameters' values, each file through one editor of its own.</summary>
    private static async Task<Solution> RestructureAsync(Solution solution, IReadOnlyList<MethodId> members, MethodId main, IReadOnlyList<Parameter> original,
        SignatureChange change, SyntaxAnnotation marker, CancellationToken cancel)
    {
        Dictionary<DocumentId, DocumentEditor> editors = [];
        async Task<DocumentEditor> EditorOf(Document document)
        {
            if (!editors.TryGetValue(document.Id, out DocumentEditor? editor))
            {
                editor = await DocumentEditor.CreateAsync(document, cancel).ConfigureAwait(false);
                editors[document.Id] = editor;
            }
            return editor;
        }

        HashSet<(DocumentId, Microsoft.CodeAnalysis.Text.TextSpan)> calls = [];
        IMethodSymbol? primary = await ResolveAsync(solution, main, cancel).ConfigureAwait(false);
        foreach (MethodId member in members)
        {
            if (await ResolveAsync(solution, member, cancel).ConfigureAwait(false) is not { } method)
            {
                continue;
            }
            bool first = SymbolEqualityComparer.Default.Equals(method, primary);
            foreach (BaseMethodDeclarationSyntax declaration in await DeclarationsAsync(method, cancel).ConfigureAwait(false))
            {
                Document document = solution.GetDocument(declaration.SyntaxTree)!;
                DocumentEditor editor = await EditorOf(document).ConfigureAwait(false);
                bool marked = first;
                first = false;
                editor.ReplaceNode(declaration, (current, _) =>
                {
                    BaseMethodDeclarationSyntax now = (BaseMethodDeclarationSyntax)current;
                    now = now.WithParameterList(Parameters(now.ParameterList, original, change));
                    if (now is MethodDeclarationSyntax named && Normalized(change.ReturnType) != Normalized(named.ReturnType.ToString()) && change.ReturnType.Trim().Length > 0)
                    {
                        now = named.WithReturnType(SyntaxFactory.ParseTypeName(change.ReturnType.Trim()).WithTriviaFrom(named.ReturnType));
                    }
                    return marked ? now.WithAdditionalAnnotations(marker) : now;
                });
            }
            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(method, solution, cancel).ConfigureAwait(false))
            {
                foreach (ReferenceLocation place in referenced.Locations)
                {
                    if (place.IsImplicit || !place.Location.IsInSource || place.Document is SourceGeneratedDocument || !calls.Add((place.Document.Id, place.Location.SourceSpan)))
                    {
                        continue;
                    }
                    SyntaxNode root = (await place.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                    if (Called(root.FindNode(place.Location.SourceSpan, getInnermostNodeForTie: true)) is not { } call)
                    {
                        continue;
                    }
                    SemanticModel model = (await place.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
                    if (Plan(model, call, method, change, cancel) is { } plan)
                    {
                        DocumentEditor editor = await EditorOf(place.Document).ConfigureAwait(false);
                        editor.ReplaceNode(call, (current, _) => WithArguments(current, plan));
                    }
                }
            }
        }
        foreach ((DocumentId id, DocumentEditor editor) in editors)
        {
            solution = solution.WithDocumentSyntaxRoot(id, editor.GetChangedRoot());
        }
        return solution;
    }

    /// <summary>A declaration's parameters as asked: each kept one as it is in this declaration, given the type, modifier
    /// or default value asked for where it differs from the method's own; each new one as written. Each place keeps the
    /// spacing the parameter there had.</summary>
    private static ParameterListSyntax Parameters(ParameterListSyntax list, IReadOnlyList<Parameter> original, SignatureChange change)
    {
        List<ParameterSyntax> parameters = [];
        foreach (ParameterChange asked in change.Parameters)
        {
            ParameterSyntax made;
            if (asked.From >= 0 && asked.From < list.Parameters.Count)
            {
                ParameterSyntax kept = list.Parameters[asked.From];
                Parameter was = original[asked.From];
                bool retyped = Normalized(asked.Type) != Normalized(was.Type) || Normalized(asked.Modifier) != Normalized(was.Modifier)
                    || asked.DefaultValue.Trim() != was.DefaultValue.Trim();
                made = retyped
                    ? Parse(asked.Modifier, asked.Type, kept.Identifier.ValueText, asked.DefaultValue).WithAttributeLists(kept.AttributeLists)
                    : kept;
            }
            else
            {
                made = Parse(asked.Modifier, asked.Type, asked.Name, asked.DefaultValue);
            }
            int at = parameters.Count;
            made = made.WithLeadingTrivia(at < list.Parameters.Count ? list.Parameters[at].GetLeadingTrivia() : default)
                .WithTrailingTrivia(at < list.Parameters.Count ? list.Parameters[at].GetTrailingTrivia() : default);
            parameters.Add(made);
        }
        return list.WithParameters(SyntaxFactory.SeparatedList(parameters, Separators(list.Parameters.GetSeparators().ToList(), parameters.Count)));
    }

    private static ParameterSyntax Parse(string modifier, string type, string name, string defaultValue)
    {
        string text = $"({Normalized(modifier)} {type.Trim()} {name}{(defaultValue.Trim().Length > 0 ? " = " + defaultValue.Trim() : string.Empty)})";
        return SyntaxFactory.ParseParameterList(text).Parameters[0];
    }

    /// <summary>A list's commas: those it had, then new ones followed by a space.</summary>
    private static IEnumerable<SyntaxToken> Separators(List<SyntaxToken> had, int items)
    {
        for (int i = 0; i < items - 1; i++)
        {
            yield return i < had.Count ? had[i] : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);
        }
    }

    /// <summary>What a call passes in the new signature: for each place, the argument it already passes for that
    /// parameter (by its index in the call, several for a params array spread out), or the value a new parameter is given;
    /// every argument after a parameter left to its default is named. Null for a call whose arguments do not all bind,
    /// which is left as it is.</summary>
    private static List<Passed>? Plan(SemanticModel model, SyntaxNode call, IMethodSymbol method, SignatureChange change, CancellationToken cancel)
    {
        if (ArgumentsOf(call) is not { } list || model.GetOperation(call, cancel) is not { } operation)
        {
            return null;
        }
        ImmutableArray<IArgumentOperation> arguments = operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => operation.DescendantsAndSelf().OfType<IInvocationOperation>().FirstOrDefault(i => i.Syntax == call)?.Arguments
                ?? operation.DescendantsAndSelf().OfType<IObjectCreationOperation>().FirstOrDefault()?.Arguments ?? [],
        };
        int[] bound = [.. Enumerable.Repeat(-1, list.Arguments.Count)];
        bool reduced = false;
        foreach (IArgumentOperation argument in arguments)
        {
            int ordinal = argument.Parameter?.Ordinal ?? -1;
            if (argument.Syntax is ArgumentSyntax syntax && list.Arguments.IndexOf(syntax) is >= 0 and int index)
            {
                bound[index] = ordinal;
            }
            else if (argument.ArgumentKind == ArgumentKind.ParamArray && argument.Value is IArrayCreationOperation { Initializer: { } spread })
            {
                foreach (IOperation element in spread.ElementValues)
                {
                    int index2 = list.Arguments.IndexOf(a => a.Expression == element.Syntax);
                    if (index2 >= 0)
                    {
                        bound[index2] = ordinal;
                    }
                }
            }
            else if (argument.ArgumentKind == ArgumentKind.Explicit && ordinal == 0 && method.IsExtensionMethod && argument.Syntax is not ArgumentSyntax)
            {
                reduced = true;
            }
        }
        if (bound.Contains(-1))
        {
            return null;
        }
        List<Passed> plan = [];
        bool named = false;
        foreach (ParameterChange parameter in change.Parameters)
        {
            if (reduced && parameter.From == 0)
            {
                continue;
            }
            if (parameter.From >= 0)
            {
                int[] passed = [.. Enumerable.Range(0, bound.Length).Where(i => bound[i] == parameter.From)];
                if (passed.Length == 0)
                {
                    named = true;
                    continue;
                }
                bool spread = passed.Length > 1 || method.Parameters[parameter.From].IsParams;
                foreach (int index in passed)
                {
                    plan.Add(new Passed(index, null, named && !spread ? parameter.Name : null));
                }
            }
            else if (parameter.CallValue.Trim().Length > 0)
            {
                plan.Add(new Passed(-1, parameter.CallValue.Trim(), named ? parameter.Name : null));
            }
            else
            {
                named = true;
            }
        }
        return plan;
    }

    /// <summary>One argument of a call in the new signature: the one at an index of the call as it was, or a new value;
    /// named when it must be.</summary>
    private sealed record Passed(int Index, string? Value, string? Name);

    private static BaseArgumentListSyntax? ArgumentsOf(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax invocation => invocation.ArgumentList,
        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList ?? SyntaxFactory.ArgumentList(),
        ConstructorInitializerSyntax initializer => initializer.ArgumentList,
        PrimaryConstructorBaseTypeSyntax primary => primary.ArgumentList,
        _ => null,
    };

    private static SyntaxNode WithArguments(SyntaxNode call, List<Passed> plan)
    {
        BaseArgumentListSyntax list = ArgumentsOf(call)!;
        List<ArgumentSyntax> arguments = [];
        foreach (Passed passed in plan)
        {
            ArgumentSyntax argument = passed.Value is { } value
                ? SyntaxFactory.Argument(SyntaxFactory.ParseExpression(value))
                : list.Arguments[passed.Index];
            if (passed.Name is { } name && argument.NameColon is null)
            {
                argument = argument.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name)).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithExpression(argument.Expression.WithoutLeadingTrivia());
            }
            int at = arguments.Count;
            argument = argument.WithLeadingTrivia(at < list.Arguments.Count ? list.Arguments[at].GetLeadingTrivia() : default)
                .WithTrailingTrivia(at < list.Arguments.Count ? list.Arguments[at].GetTrailingTrivia() : default);
            arguments.Add(argument);
        }
        SeparatedSyntaxList<ArgumentSyntax> made = SyntaxFactory.SeparatedList(arguments, Separators(list.Arguments.GetSeparators().ToList(), arguments.Count));
        return call switch
        {
            InvocationExpressionSyntax invocation => invocation.WithArgumentList(invocation.ArgumentList.WithArguments(made)),
            ObjectCreationExpressionSyntax creation => creation.WithArgumentList((creation.ArgumentList ?? SyntaxFactory.ArgumentList()).WithArguments(made)),
            ImplicitObjectCreationExpressionSyntax creation => creation.WithArgumentList(creation.ArgumentList.WithArguments(made)),
            ConstructorInitializerSyntax initializer => initializer.WithArgumentList(initializer.ArgumentList.WithArguments(made)),
            PrimaryConstructorBaseTypeSyntax primary => primary.WithArgumentList(primary.ArgumentList.WithArguments(made)),
            _ => call,
        };
    }

    /// <summary>The method marked in its first declaration renamed, with its hierarchy and calls, by Roslyn's Renamer.</summary>
    private static async Task<Solution> RenameMethodAsync(Solution solution, SyntaxAnnotation marker, string name, CancellationToken cancel)
    {
        foreach (Document document in solution.Projects.SelectMany(p => p.Documents))
        {
            SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
            if (!root.ContainsAnnotations || root.GetAnnotatedNodes(marker).FirstOrDefault() is not { } declaration)
            {
                continue;
            }
            SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
            if (model.GetDeclaredSymbol(declaration, cancel) is IMethodSymbol method)
            {
                SymbolRenameOptions options = new(RenameOverloads: false, RenameInStrings: false, RenameInComments: true, RenameFile: false);
                return await Renamer.RenameSymbolAsync(solution, method, options, name, cancel).ConfigureAwait(false);
            }
        }
        return solution;
    }

    private static string Describe(IMethodSymbol method) => method.MethodKind == MethodKind.Constructor
        ? $"constructor '{method.ContainingType.Name}'"
        : $"method '{method.ContainingType.Name}.{method.Name}'";
}
