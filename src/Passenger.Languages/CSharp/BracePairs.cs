using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>A bracket and the one it pairs with, from the file's syntax: the parentheses, square brackets, braces and
/// type arguments' angle brackets, never one in a string or a comment. A node holds its own pair of each kind, so a
/// bracket's pair is found among its node's tokens rather than by counting across the file, and one whose pair is
/// missing as code is typed pairs with none.</summary>
internal static class BracePairs
{
    /// <summary>The bracket covering the character at an offset and its pair; null when that is no bracket, or one with
    /// no pair.</summary>
    public static BracePair? At(SyntaxNode root, int offset)
    {
        if (offset < 0 || offset >= root.FullSpan.End)
        {
            return null;
        }
        SyntaxToken token = root.FindToken(offset);
        if (!token.Span.Contains(offset) || Side(token) is not (1 or -1) || Pair(token, Side(token)) is not { } pair)
        {
            return null;
        }
        return new BracePair(Range(token), Range(pair));
    }

    /// <summary>1 for an opening bracket, -1 for a closing one, 0 for anything else; a skipped token is none.</summary>
    private static int Side(SyntaxToken token) => token.Parent is null or { IsStructuredTrivia: true } ? 0 : CSharpClassifier.Bracket(token);

    /// <summary>The bracket of the other side and the same kind among its node's own tokens: for an opener the last, for a
    /// closer the first, found from that end, so a brace around ten thousand members reads none of them.</summary>
    private static SyntaxToken? Pair(SyntaxToken token, int side)
    {
        SyntaxKind wanted = Opposite(token.Kind());
        ChildSyntaxList children = token.Parent!.ChildNodesAndTokens();
        IEnumerable<SyntaxNodeOrToken> from = side > 0 ? children.Reverse() : children;
        foreach (SyntaxNodeOrToken child in from)
        {
            if (child.IsToken && child.AsToken() == token)
            {
                return null;
            }
            if (child.IsToken && child.IsKind(wanted))
            {
                SyntaxToken found = child.AsToken();
                return found.IsMissing ? null : found;
            }
        }
        return null;
    }

    private static SyntaxKind Opposite(SyntaxKind kind) => kind switch
    {
        SyntaxKind.OpenParenToken => SyntaxKind.CloseParenToken,
        SyntaxKind.CloseParenToken => SyntaxKind.OpenParenToken,
        SyntaxKind.OpenBracketToken => SyntaxKind.CloseBracketToken,
        SyntaxKind.CloseBracketToken => SyntaxKind.OpenBracketToken,
        SyntaxKind.OpenBraceToken => SyntaxKind.CloseBraceToken,
        SyntaxKind.CloseBraceToken => SyntaxKind.OpenBraceToken,
        SyntaxKind.LessThanToken => SyntaxKind.GreaterThanToken,
        SyntaxKind.GreaterThanToken => SyntaxKind.LessThanToken,
        _ => SyntaxKind.None,
    };

    private static TextRange Range(SyntaxToken token) => new(token.SpanStart, token.Span.Length);
}
