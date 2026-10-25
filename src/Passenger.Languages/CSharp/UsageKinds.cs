using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>What a usage of a symbol does there, by which usages are grouped.</summary>
public enum UsageKind
{
    Declaration,
    Write,
    Read,
    Invocation,
    Import,
    Other,
}

/// <summary>Tells from a file's syntax what each usage of a symbol is: its declaration, a write (assigned, incremented,
/// passed out or by reference), an invocation, a using directive's, a read of a value, or a type's use. The language
/// server says only where usages are.</summary>
public static class UsageKinds
{
    /// <summary>Each usage's kind, by where it starts in the file's syntax.</summary>
    public static IReadOnlyList<UsageKind> Classify(SyntaxNode root, IReadOnlyList<int> offsets) =>
        [.. offsets.Select(offset => Classify(root, offset))];

    private static UsageKind Classify(SyntaxNode root, int offset)
    {
        SyntaxToken token = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1)));
        if (token.Parent is not { } parent)
        {
            return UsageKind.Other;
        }
        if (IsDeclaredName(parent, token))
        {
            return UsageKind.Declaration;
        }
        if (parent.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
        {
            return UsageKind.Import;
        }
        if (parent is not SimpleNameSyntax name)
        {
            return UsageKind.Other;
        }
        // The whole expression the name ends: a.b.Name, or the name alone.
        ExpressionSyntax expression = name;
        while (expression.Parent is MemberAccessExpressionSyntax access && access.Name == expression
            || expression.Parent is MemberBindingExpressionSyntax binding && binding.Name == expression
            || expression.Parent is QualifiedNameSyntax qualified && qualified.Right == expression)
        {
            expression = (ExpressionSyntax)expression.Parent;
        }
        if (expression.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == expression)
        {
            expression = conditional;
        }
        return expression.Parent switch
        {
            InvocationExpressionSyntax invocation when invocation.Expression == expression => UsageKind.Invocation,
            AssignmentExpressionSyntax assignment when assignment.Left == expression => UsageKind.Write,
            PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                when expression.Parent.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
                    or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression => UsageKind.Write,
            ArgumentSyntax argument when argument.RefKindKeyword.Kind() is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword => UsageKind.Write,
            _ when IsInType(expression) => UsageKind.Other,
            _ => UsageKind.Read,
        };
    }

    /// <summary>Whether the name at an offset is declared with its value there: a variable or field with an initializer, a
    /// foreach variable, a pattern's or an out argument's designation: highlighted as a write of the symbol, where the
    /// language server calls every declaration a plain use.</summary>
    public static bool DeclaresWithValue(SyntaxNode root, int offset)
    {
        SyntaxToken token = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1)));
        return token.SpanStart == offset && token.Parent switch
        {
            VariableDeclaratorSyntax variable => variable.Identifier == token && variable.Initializer is not null,
            ForEachStatementSyntax loop => loop.Identifier == token,
            SingleVariableDesignationSyntax designation => designation.Identifier == token,
            _ => false,
        };
    }

    /// <summary>Whether the token is the name a declaration gives (a type's, a member's, a local's, a parameter's).</summary>
    private static bool IsDeclaredName(SyntaxNode parent, SyntaxToken token) => parent switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier == token,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier == token,
        MethodDeclarationSyntax method => method.Identifier == token,
        ConstructorDeclarationSyntax constructor => constructor.Identifier == token,
        PropertyDeclarationSyntax property => property.Identifier == token,
        EventDeclarationSyntax @event => @event.Identifier == token,
        VariableDeclaratorSyntax variable => variable.Identifier == token,
        ParameterSyntax parameter => parameter.Identifier == token,
        EnumMemberDeclarationSyntax member => member.Identifier == token,
        TypeParameterSyntax typeParameter => typeParameter.Identifier == token,
        SingleVariableDesignationSyntax designation => designation.Identifier == token,
        ForEachStatementSyntax loop => loop.Identifier == token,
        LocalFunctionStatementSyntax function => function.Identifier == token,
        _ => false,
    };

    /// <summary>Whether a name stands for a type where one is written (a declaration's type, a base list, a cast).</summary>
    private static bool IsInType(ExpressionSyntax expression) => expression.Parent is
        VariableDeclarationSyntax or ParameterSyntax or BaseTypeSyntax or TypeArgumentListSyntax or ObjectCreationExpressionSyntax
        or CastExpressionSyntax or PropertyDeclarationSyntax or MethodDeclarationSyntax or TypeOfExpressionSyntax or ArrayTypeSyntax
        or NullableTypeSyntax or AttributeSyntax or DefaultExpressionSyntax;
}
