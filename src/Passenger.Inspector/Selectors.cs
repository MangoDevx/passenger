using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;

namespace Passenger.Inspector;

/// <summary>Why a style does not apply: its reason in words, or null when every part matches; and whether it is about this
/// control at all (a style for another type, or for a part of another name, is not, and is left out).</summary>
internal readonly record struct Explained(string? Why, bool Relevant);

/// <summary>Says which part of a style's selector fails on a control and why, from the selector's text, which is Avalonia's
/// public form of it (its selector classes are its own): a class not in Classes, a pseudo-class not active, a parent or
/// ancestor that does not match, a property with another value. Whether a style applies is Avalonia's to say; this only
/// names the part that keeps it from applying.</summary>
internal static partial class Selectors
{
    public static Explained Explain(Style style, StyledElement control, IStyleHost? host)
    {
        if (style.Selector is null)
        {
            // A style with no selector styles its host alone.
            return ReferenceEquals(control, host) ? new Explained(null, true) : new Explained("it styles its host alone", false);
        }
        Node parsed;
        try
        {
            // Its own text, ^ for its parent's, unless Avalonia already wrote it with the parent's in place.
            parsed = new Parser(style.Selector.ToString()).List();
        }
        catch (FormatException)
        {
            return new Explained(null, true);
        }
        return Match(parsed, control, style.Parent, subject: true);
    }

    private abstract record Node;

    /// <summary>Selectors any one of which matches: "a, b".</summary>
    private sealed record Either(IReadOnlyList<Node> Choices) : Node;

    /// <summary>Compounds joined by combinators, the last the control itself: ' ' a descendant, '>' a child, 't' a template's part.</summary>
    private sealed record Chain(IReadOnlyList<Compound> Parts, IReadOnlyList<char> Combinators) : Node;

    private sealed record Compound(string Text, bool Nesting, Node? Group, string? Type, bool OrDerived, string? Name, IReadOnlyList<Test> Tests);

    private abstract record Test(string Text);

    private sealed record HasClass(string Text, string Class) : Test(Text);

    private sealed record IsNot(string Text, Node Argument) : Test(Text);

    private sealed record HasValue(string Text, string? Owner, string Property, string Value) : Test(Text);

    private sealed record NthChild(string Text, int Step, int Offset, bool FromLast) : Test(Text);

    private static Explained Match(Node node, StyledElement control, IStyle? parent, bool subject)
    {
        switch (node)
        {
            case Either either:
                List<Explained> each = [.. either.Choices.Select(c => Match(c, control, parent, subject))];
                if (each.Any(e => e.Why is null))
                {
                    return new Explained(null, true);
                }
                List<Explained> relevant = [.. each.Where(e => e.Relevant)];
                return relevant.Count == 0 ? new Explained(each[0].Why, false) : new Explained(string.Join("; and ", relevant.Select(e => e.Why)), true);
            case Chain chain:
                return MatchChain(chain, chain.Parts.Count - 1, control, parent, subject ? "it" : null);
            default:
                return new Explained(null, true);
        }
    }

    /// <summary>A chain matched from part <paramref name="index"/> leftward, the part on <paramref name="control"/>; who it
    /// is in words ("it", "its templated parent Button"), or null inside a test that has no words of its own.</summary>
    private static Explained MatchChain(Chain chain, int index, StyledElement control, IStyle? parent, string? who)
    {
        Explained here = MatchCompound(chain.Parts[index], control, parent, who ?? "it");
        if (here.Why is not null)
        {
            // Another type or another part's name on the control itself: the style is about something else.
            return here with { Relevant = here.Relevant || index != chain.Parts.Count - 1 };
        }
        if (index == 0)
        {
            return here;
        }
        string left = string.Join(string.Empty, chain.Parts.Take(index).Select((p, i) => i == 0 ? p.Text : Joiner(chain.Combinators[i - 1]) + p.Text));
        switch (chain.Combinators[index - 1])
        {
            case 't':
                if (control.TemplatedParent is not StyledElement templated)
                {
                    return new Explained($"{Who(who)} is not part of a control's template (`/template/`)", true);
                }
                Explained there = MatchChain(chain, index - 1, templated, parent, $"its templated parent {Describe(templated)}");
                // A template of another kind of control: the style is about that kind's parts, not this one's.
                return there.Why is null ? there : there with { Relevant = there.Relevant && SameKind(chain.Parts[index - 1], templated) };
            case '>':
                if ((control as ILogical).LogicalParent is not StyledElement logicalParent)
                {
                    return new Explained($"{Who(who)} has no logical parent to match `{left}`", true);
                }
                Explained up = MatchChain(chain, index - 1, logicalParent, parent, $"its logical parent {Describe(logicalParent)}");
                return up.Why is null ? up : up with { Relevant = true };
            default:
                for (ILogical? at = (control as ILogical).LogicalParent; at is not null; at = at.LogicalParent)
                {
                    if (at is StyledElement ancestor && MatchChain(chain, index - 1, ancestor, parent, "it").Why is null)
                    {
                        return new Explained(null, true);
                    }
                }
                return new Explained($"no logical ancestor of {Who(who, asObject: true)} matches `{left}`", true);
        }
    }

    private static string Joiner(char combinator) => combinator switch
    {
        't' => " /template/ ",
        '>' => " > ",
        _ => " ",
    };

    private static bool SameKind(Compound part, StyledElement control) => part.Type is null || TypeMatches(part, control);

    private static string Who(string? who, bool asObject = false) => who is null or "it" ? (asObject ? "it" : "It") : who;

    private static string Describe(StyledElement element) =>
        element.Name is { Length: > 0 } name ? $"{Words.TypeName(element.GetType())}#{name}" : Words.TypeName(element.GetType());

    private static Explained MatchCompound(Compound part, StyledElement control, IStyle? parent, string who)
    {
        string its = who == "it" ? "its" : $"{who}'s";
        string subject = who == "it" ? "it" : who;
        if (part.Nesting && parent is not null)
        {
            Explained outer = parent switch
            {
                ControlTheme { TargetType: { } target } => target.IsAssignableFrom(control.StyleKey)
                    ? new Explained(null, true)
                    : new Explained($"{subject} is a {Words.TypeName(control.StyleKey)}, not the {Words.TypeName(target)} its theme styles", false),
                Style { Selector: { } selector } style => Match(new Parser(selector.ToString()).List(), control, style.Parent, subject: who == "it"),
                _ => new Explained(null, true),
            };
            if (outer.Why is not null)
            {
                return outer;
            }
        }
        if (part.Group is { } group && Match(group, control, parent, subject: false) is { Why: { } groupWhy })
        {
            return new Explained($"{subject} matches none of `{part.Text}`: {groupWhy}", true);
        }
        if (part.Type is not null && !TypeMatches(part, control))
        {
            return new Explained($"{subject} is a {Words.TypeName(control.StyleKey)}, not {(part.OrDerived ? "derived from " : "a ")}{part.Type}", false);
        }
        if (part.Name is not null && control.Name != part.Name)
        {
            return new Explained(control.Name is { Length: > 0 } named ? $"{its} Name is {named}, not {part.Name}" : $"{subject} has no Name, not {part.Name}", false);
        }
        List<string> failing = [];
        foreach (Test test in part.Tests)
        {
            if (Fails(test, control, parent, its, subject) is { } why)
            {
                failing.Add(why);
            }
        }
        return failing.Count == 0 ? new Explained(null, true) : new Explained(string.Join(", and ", failing), true);
    }

    private static bool TypeMatches(Compound part, StyledElement control)
    {
        for (Type? type = control.StyleKey; type is not null; type = type.BaseType)
        {
            if (Words.TypeName(type) == part.Type)
            {
                return true;
            }
            if (!part.OrDerived)
            {
                return false;
            }
        }
        return false;
    }

    private static string? Fails(Test test, StyledElement control, IStyle? parent, string its, string subject)
    {
        switch (test)
        {
            case HasClass has when has.Class.StartsWith(':'):
                return control.Classes.Contains(has.Class) ? null : $"`{has.Text}` is not active on {(subject == "it" ? "it" : subject)}";
            case HasClass has:
                return control.Classes.Contains(has.Class) ? null : $"`{has.Text}` is not in {its} Classes";
            case IsNot not:
                return Match(not.Argument, control, parent, subject: false).Why is null ? $"{subject} matches what `{not.Text}` rules out" : null;
            case HasValue value:
                AvaloniaProperty? property = value.Owner is null
                    ? AvaloniaPropertyRegistry.Instance.FindRegistered(control, value.Property)
                    : AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(control.GetType())
                        .FirstOrDefault(p => p.Name == value.Property && Words.TypeName(p.OwnerType) == value.Owner);
                if (property is null)
                {
                    return $"{subject} has no property {value.Property} for `{value.Text}`";
                }
                string actual = Convert.ToString(control.GetValue(property), CultureInfo.InvariantCulture) ?? string.Empty;
                return string.Equals(actual, value.Value, StringComparison.OrdinalIgnoreCase) ? null : $"{its} {value.Property} is {(actual.Length == 0 ? "empty" : actual)}, not {value.Value}";
            case NthChild nth:
                if ((control as ILogical).LogicalParent is not IChildIndexProvider siblings || siblings.GetChildIndex(control) is not (>= 0 and var index))
                {
                    return $"{subject} is not among numbered children for `{nth.Text}`";
                }
                int position = index + 1;
                if (nth.FromLast)
                {
                    position = siblings.TryGetTotalCount(out int count) ? count - index : -1;
                }
                int rest = position - nth.Offset;
                bool matches = nth.Step == 0 ? rest == 0 : rest == 0 || (Math.Sign(rest) == Math.Sign(nth.Step) && rest % nth.Step == 0);
                return matches ? null : $"{subject} is child {position}, which `{nth.Text}` does not take";
            default:
                return null;
        }
    }

    /// <summary>Avalonia's selector syntax as its selectors write themselves (and as XAML gives them): types and :is(),
    /// #name, .class, :pseudo, :not(), :nth-child(), [Property=value], ^, (a, b), and the space, &gt; and /template/
    /// combinators.</summary>
    private sealed partial class Parser(string text)
    {
        private int at;

        public Node List()
        {
            Node node = Choices();
            Skip();
            return at == text.Length ? node : throw new FormatException($"Unread selector text at {at}: {text}");
        }

        private Node Choices()
        {
            List<Node> choices = [Chained()];
            while (Peek(','))
            {
                at++;
                Skip();
                choices.Add(Chained());
            }
            return choices.Count == 1 ? choices[0] : new Either(choices);
        }

        private Chain Chained()
        {
            List<Compound> parts = [Part()];
            List<char> combinators = [];
            while (true)
            {
                int before = at;
                Skip();
                if (at >= text.Length || Peek(',') || Peek(')'))
                {
                    break;
                }
                if (Peek('>'))
                {
                    at++;
                    Skip();
                    combinators.Add('>');
                }
                else if (string.CompareOrdinal(text, at, "/template/", 0, 10) == 0)
                {
                    at += 10;
                    Skip();
                    combinators.Add('t');
                }
                else if (at > before)
                {
                    combinators.Add(' ');
                }
                else
                {
                    throw new FormatException($"Unexpected '{text[at]}' at {at}: {text}");
                }
                parts.Add(Part());
            }
            return new Chain(parts, combinators);
        }

        private Compound Part()
        {
            int start = at;
            bool nesting = false;
            Node? group = null;
            string? type = null;
            bool derived = false;
            string? name = null;
            List<Test> tests = [];
            if (Peek('^'))
            {
                at++;
                nesting = true;
            }
            if (Peek('('))
            {
                at++;
                group = Choices();
                Expect(')');
            }
            if (at < text.Length && (char.IsLetter(text[at]) || text[at] == '_'))
            {
                type = TypeName();
            }
            while (at < text.Length)
            {
                int testStart = at;
                char c = text[at];
                if (c == '#')
                {
                    at++;
                    name = Identifier();
                }
                else if (c == '.')
                {
                    at++;
                    string cls = Identifier();
                    tests.Add(new HasClass(text[testStart..at], cls));
                }
                else if (c == '[')
                {
                    int close = text.IndexOf(']', at);
                    if (close < 0)
                    {
                        throw new FormatException($"No ] after {at}: {text}");
                    }
                    Match property = Property().Match(text[(at + 1)..close]);
                    if (!property.Success)
                    {
                        throw new FormatException($"Unread property test at {at}: {text}");
                    }
                    at = close + 1;
                    tests.Add(new HasValue(text[testStart..at], property.Groups["owner"].Success ? property.Groups["owner"].Value : null,
                        property.Groups["name"].Value, property.Groups["value"].Value.Trim()));
                }
                else if (c == ':')
                {
                    at++;
                    string pseudo = Identifier();
                    switch (pseudo)
                    {
                        case "is":
                            Expect('(');
                            type = TypeName();
                            derived = true;
                            Expect(')');
                            break;
                        case "not":
                            Expect('(');
                            Node argument = Choices();
                            Expect(')');
                            tests.Add(new IsNot(text[testStart..at], argument));
                            break;
                        case "nth-child" or "nth-last-child":
                            Expect('(');
                            int close = text.IndexOf(')', at);
                            if (close < 0)
                            {
                                throw new FormatException($"No ) after {at}: {text}");
                            }
                            (int step, int offset) = Nth(text[at..close].Trim());
                            at = close + 1;
                            tests.Add(new NthChild(text[testStart..at], step, offset, pseudo == "nth-last-child"));
                            break;
                        default:
                            tests.Add(new HasClass(text[testStart..at], ":" + pseudo));
                            break;
                    }
                }
                else
                {
                    break;
                }
            }
            if (at == start)
            {
                throw new FormatException($"Nothing to read at {at}: {text}");
            }
            return new Compound(text[start..at], nesting, group, type, derived, name, tests);
        }

        /// <summary>A type, its XML namespace prefix (local|MyControl) left off, as a type's selector writes its name alone.</summary>
        private string TypeName()
        {
            string name = Identifier();
            if (Peek('|'))
            {
                at++;
                name = Identifier();
            }
            return name;
        }

        private string Identifier()
        {
            int start = at;
            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '-'))
            {
                at++;
            }
            return at > start ? text[start..at] : throw new FormatException($"No name at {start}: {text}");
        }

        private static (int Step, int Offset) Nth(string argument)
        {
            if (argument == "odd")
            {
                return (2, 1);
            }
            if (argument == "even")
            {
                return (2, 0);
            }
            Match nth = NthPattern().Match(argument.Replace(" ", string.Empty, StringComparison.Ordinal));
            if (!nth.Success)
            {
                throw new FormatException($"Unread :nth-child argument: {argument}");
            }
            if (!nth.Groups["n"].Success)
            {
                return (0, int.Parse(nth.Groups["offset"].Value, CultureInfo.InvariantCulture));
            }
            string step = nth.Groups["step"].Value;
            int stepValue = step is "" or "+" ? 1 : step == "-" ? -1 : int.Parse(step, CultureInfo.InvariantCulture);
            int offset = nth.Groups["offset"].Success && nth.Groups["offset"].Value.Length > 0 ? int.Parse(nth.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
            return (stepValue, offset);
        }

        private bool Peek(char c) => at < text.Length && text[at] == c;

        private void Expect(char c)
        {
            Skip();
            if (!Peek(c))
            {
                throw new FormatException($"Expected '{c}' at {at}: {text}");
            }
            at++;
        }

        private void Skip()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
        }

        [GeneratedRegex(@"^\s*(?:\((?<owner>[\w]+)\.(?<name>[\w]+)\)|(?<name>[\w]+))\s*=(?<value>.*)$")]
        private static partial Regex Property();

        [GeneratedRegex(@"^(?:(?<step>[+-]?\d*)(?<n>n)(?<offset>[+-]\d+)?|(?<offset>[+-]?\d+))$")]
        private static partial Regex NthPattern();
    }
}
