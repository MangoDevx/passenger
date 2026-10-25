using System.Globalization;
using Passenger.Languages.CSharp;

namespace Passenger.App.CSharp;

/// <summary>A template's variable as Edit variables shows it: its name in the text, the macro its value comes from
/// where the template goes (none leaves it empty), the macro's argument (a constant's value, a date's format), and
/// whether Tab stops at it to be typed over.</summary>
public sealed record TemplateVariable(string Name, string Macro = "", string Parameter = "", bool Editable = true);

/// <summary>A live template: its shortcut, what it says it is, where it may be typed, its text and its variables. One
/// template may be typed by its shortcut (Generation), offered by Surround With, or both; a built-in one is known by
/// its <see cref="Key"/>, the shortcut it ships with, however the person changes it.</summary>
public sealed record LiveTemplate(string Abbreviation, string Description, TemplateScope Scope, string Text, IReadOnlyList<TemplateVariable> Variables)
{
    /// <summary>What the settings know it by: a built-in one's shortcut as shipped, the person's own a name of its own.</summary>
    public string Key { get; init; } = Abbreviation;

    /// <summary>Whether it ships with Passenger, rather than being one the person added.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>Whether it is in use: one turned off is neither expanded nor offered.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether its shortcut and Tab, or completion, expand it.</summary>
    public bool Live { get; init; } = true;

    /// <summary>Where Surround With offers it around the code chosen, none when it does not.</summary>
    public SurroundScope Surround { get; init; }

    /// <summary>The digit that chooses it in Surround With's list.</summary>
    public char? Mnemonic { get; init; }

    /// <summary>Reformat: its lines indented as the code around it, a tab its indentation's unit; otherwise it goes in
    /// as written.</summary>
    public bool Reformat { get; init; } = true;

    /// <summary>The variables in its text, in the order Tab visits them: those it lists, then any others as they appear.</summary>
    public IReadOnlyList<TemplateVariable> VariablesInText()
    {
        List<string> named = LiveTemplates.Names(Text);
        return [.. Variables.Where(v => named.Contains(v.Name)), .. named.Where(n => Variables.All(v => v.Name != n)).Select(n => new TemplateVariable(n))];
    }

    /// <summary>Whether it is the same template in every part a person can change.</summary>
    public bool SameAs(LiveTemplate other) =>
        Abbreviation == other.Abbreviation && Description == other.Description && Scope == other.Scope && Text == other.Text
        && Variables.SequenceEqual(other.Variables) && Enabled == other.Enabled && Live == other.Live && Surround == other.Surround
        && Mnemonic == other.Mnemonic && Reformat == other.Reformat;
}

/// <summary>Availability: the places a template is offered. Generation uses the places a shortcut is typed (statements,
/// type members, types and namespaces), Surround With those code is chosen in (statements, an expression, whole lines,
/// types, anywhere).</summary>
[Flags]
public enum TemplateAvailability
{
    None = 0,
    Statements = 1,
    TypeMembers = 2,
    TypesAndNamespaces = 4,
    Expressions = 8,
    LineStart = 16,
    Everywhere = 32,
}

/// <summary>Use in: typed by its shortcut, offered by Surround With, or both.</summary>
public enum TemplateUse
{
    Generation,
    Surround,
    Both,
}

/// <summary>Where a template goes, for its macros: the type and namespace it goes in, the file and the line (from 1).</summary>
public readonly record struct TemplateSite(string? TypeName, string? Namespace, string? FilePath, int Line);

/// <summary>A macro a variable's value may come from: its name, as the template keeps it, what Choose macro shows for
/// it, the name of its one argument when it takes one, and its value where the template goes.</summary>
public sealed record TemplateMacro(string Name, string Description, string? Parameter, Func<TemplateSite, string, string?> Value);

/// <summary>The C# live templates used every day: the shortcut then Tab, or picked from completion; and the surround
/// templates, offered by Surround With. The built-in ones are here; what a person changed or added is kept in the
/// settings as differences from them (<see cref="Settings.TemplateChange"/>), so a built-in one a later Passenger adds
/// or improves arrives with it.</summary>
public static class LiveTemplates
{
    /// <summary>The template syntax's own variables, which are no variable to fill.</summary>
    private static readonly string[] Reserved = ["END", "SELECTION", "SELSTART", "SELEND"];

    /// <summary>The macros that need nothing but the file and where the template goes, by the names templates
    /// keep.</summary>
    public static IReadOnlyList<TemplateMacro> Macros { get; } =
    [
        new("constant", "Constant value", "Constant value", (_, value) => value),
        new("typeName", "Containing type name", null, (site, _) => site.TypeName),
        new("getCurrentNamespace", "Containing namespace", null, (site, _) => site.Namespace),
        new("getFileName", "Current file name", null, (site, _) => site.FilePath is { } path ? Path.GetFileName(path) : null),
        new("getFileNameWithoutExtension", "Current file name without extension", null,
            (site, _) => site.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : null),
        new("getCurrentDate", "Current date in specified format", "format", (_, format) => Formatted(DateTime.Now, format, "d")),
        new("getCurrentTime", "Current date and time in specified format", "format", (_, format) => Formatted(DateTime.Now, format, "g")),
        new("getUserName", "Short name of the current user", null, (_, _) => Environment.UserName),
        new("guid", "New GUID", null, (_, _) => Guid.NewGuid().ToString()),
        new("lineNumber", "Current line number", null, (site, _) => site.Line.ToString(CultureInfo.InvariantCulture)),
    ];

    private static TemplateVariable[] V(params TemplateVariable[] variables) => variables;

    private static TemplateVariable C(string name, string value) => new(name, "constant", value);

    private const SurroundScope Statements = SurroundScope.Statements;

    public static IReadOnlyList<LiveTemplate> BuiltIn { get; } = [.. new LiveTemplate[]
    {
        new("for", "Simple \"for\" loop", TemplateScope.Statement, "for (int $INDEX$ = 0; $INDEX$ < $UPPER$; $INDEX$++)\n{\n\t$SELECTION$$END$\n}", V(C("INDEX", "i"), C("UPPER", "length")))
            { Surround = Statements, Mnemonic = '3' },
        new("forr", "Reverse 'for' loop", TemplateScope.Statement, "for (int $INDEX$ = $MAX$ - 1; $INDEX$ >= 0; $INDEX$--)\n{\n\t$END$\n}", V(C("INDEX", "i"), C("MAX", "length"))),
        new("foreach", "foreach block", TemplateScope.Statement, "foreach (var $VARIABLE$ in $COLLECTION$)\n{\n\t$SELECTION$$END$\n}", V(C("VARIABLE", "item"), C("COLLECTION", "collection")))
            { Surround = Statements },
        new("if", "if statement", TemplateScope.Statement, "if ($CONDITION$)\n{\n\t$SELECTION$$END$\n}", V(C("CONDITION", "true"))) { Surround = Statements, Mnemonic = '1' },
        new("else", "else statement", TemplateScope.Statement, "else\n{\n\t$END$\n}", V()),
        new("while", "while loop", TemplateScope.Statement, "while ($CONDITION$)\n{\n\t$SELECTION$$END$\n}", V(C("CONDITION", "true"))) { Surround = Statements, Mnemonic = '2' },
        new("do", "do...while loop", TemplateScope.Statement, "do\n{\n\t$SELECTION$$END$\n} while ($CONDITION$);", V(C("CONDITION", "true"))) { Surround = Statements, Mnemonic = '4' },
        new("switch", "switch statement", TemplateScope.Statement, "switch ($EXPRESSION$)\n{\n\t$END$\n}", V(C("EXPRESSION", "expression"))),
        new("try", "try catch", TemplateScope.Statement,
            "try\n{\n\t$SELECTION$\n}\ncatch ($EXCEPTION$ $EX_NAME$)\n{\n\t$SELSTART$Console.WriteLine($EX_NAME$);\n\tthrow;$SELEND$\n}",
            V(C("EXCEPTION", "Exception"), C("EX_NAME", "e"))) { Surround = Statements, Mnemonic = '8' },
        new("tryf", "try finally", TemplateScope.Statement, "try\n{\n\t$SELECTION$\n}\nfinally\n{\n\t$END$\n}", V()) { Surround = Statements, Mnemonic = '9' },
        new("using", "using statement", TemplateScope.Statement, "using ($RESOURCE$)\n{\n\t$SELECTION$$END$\n}", V(C("RESOURCE", "resource"))) { Surround = Statements },
        new("lock", "lock statement", TemplateScope.Statement, "lock ($EXPRESSION$)\n{\n\t$SELECTION$$END$\n}", V(C("EXPRESSION", "this"))) { Surround = Statements, Mnemonic = '5' },
        new("cw", "Console.WriteLine", TemplateScope.Statement, "Console.WriteLine($END$);", V()),
        new("thr", "throw new", TemplateScope.Statement, "throw new $EXCEPTION$($END$);", V(C("EXCEPTION", "InvalidOperationException"))),
        new("prop", "Property", TemplateScope.Member, "public $TYPE$ $NAME$ { get; set; }$END$", V(C("TYPE", "int"), C("NAME", "Name"))),
        new("propg", "Property with a 'get' accessor", TemplateScope.Member, "public $TYPE$ $NAME$ { get; }$END$", V(C("TYPE", "int"), C("NAME", "Name"))),
        // The type's own name, not to be typed over.
        new("ctor", "Constructor", TemplateScope.Member, "public $TYPE$($END$)\n{\n}", V(new TemplateVariable("TYPE", "typeName", Editable: false))),
        new("svm", "'void Main' method", TemplateScope.Member, "static void Main(string[] args)\n{\n\t$END$\n}", V()),
        new("sim", "'int Main' method", TemplateScope.Member, "static int Main(string[] args)\n{\n\t$END$\n\treturn 0;\n}", V()),
        new("class", "Class", TemplateScope.Type, "public class $NAME$\n{\n\t$END$\n}", V(C("NAME", "Name"))),
        new("record", "Record", TemplateScope.Type, "public record $NAME$($END$);", V(C("NAME", "Name"))),
        new("interface", "Interface", TemplateScope.Type, "public interface $NAME$\n{\n\t$END$\n}", V(C("NAME", "IName"))),
        new("struct", "Struct", TemplateScope.Type, "public struct $NAME$\n{\n\t$END$\n}", V(C("NAME", "Name"))),
        new("enum", "Enum", TemplateScope.Type, "public enum $NAME$\n{\n\t$END$\n}", V(C("NAME", "Name"))),

        // The surround templates not typed here by a shortcut.
        new("cast", "cast", TemplateScope.None, "(($TYPE$)$SELECTION$)", V()) { Live = false, Surround = SurroundScope.Expression, Mnemonic = '0' },
        new("()", "parentheses", TemplateScope.None, "($SELECTION$$END$)", V()) { Live = false, Surround = SurroundScope.Everywhere, Mnemonic = '6' },
        new("{}", "braces", TemplateScope.None, "{\n\t$SELECTION$$END$\n}", V()) { Live = false, Surround = SurroundScope.Everywhere, Mnemonic = '7' },
        new("#if", "#if directive", TemplateScope.None, "#if $EXPRESSION$\n$SELECTION$$END$\n#endif", V(C("EXPRESSION", "true")))
            { Live = false, Surround = SurroundScope.Lines },
        new("#region", "#region directive", TemplateScope.None, "#region $NAME$\n$SELECTION$$END$\n#endregion", V(C("NAME", "MyRegion")))
            { Live = false, Surround = SurroundScope.Lines },
        new("namespace", "namespace declaration", TemplateScope.None, "namespace $NAME$\n{\n\t$END$$SELECTION$\n}", V(C("NAME", "MyNamespace")))
            { Live = false, Surround = SurroundScope.Types },
    }.Select(t => t with { BuiltIn = true })];

    /// <summary>XAML's Surround With: the XML <c>tag</c> (the name to type, the caret then in its start tag) and the
    /// layout elements a XAML view is wrapped in, each in its start tag once made; those that hold one child only
    /// offered around one element, Avalonia's Panel only in Avalonia's XAML.</summary>
    public static IReadOnlyList<XamlSurround> Xaml { get; } =
    [
        new(new LiveTemplate("tag", "tag", TemplateScope.None, "<$TAG$$END$>\n\t$SELECTION$\n</$TAG$>", V(C("TAG", "Grid"))) { Live = false, Mnemonic = '1', BuiltIn = true }),
        Panel("Grid", '2'),
        Panel("StackPanel", '3'),
        Panel("Border", '4') with { OneChild = true },
        Panel("ScrollViewer", '5') with { OneChild = true },
        Panel("DockPanel", '6'),
        Panel("WrapPanel", '7'),
        Panel("Canvas", '8'),
        Panel("Viewbox", '9') with { OneChild = true },
        Panel("Panel", '0') with { AvaloniaOnly = true },
    ];

    private static XamlSurround Panel(string element, char mnemonic) =>
        new(new LiveTemplate(element, $"Surround with <{element}>", TemplateScope.None, $"<{element}$END$>\n\t$SELECTION$\n</{element}>", V())
            { Live = false, Mnemonic = mnemonic, BuiltIn = true });

    /// <summary>What a template's availability is and what it is used in, read from where it is offered.</summary>
    public static (TemplateAvailability Availability, TemplateUse Use) Availability(LiveTemplate template)
    {
        TemplateAvailability where = TemplateAvailability.None;
        if (template.Live)
        {
            where |= (template.Scope & TemplateScope.Statement) != 0 ? TemplateAvailability.Statements : 0;
            where |= (template.Scope & TemplateScope.Member) != 0 ? TemplateAvailability.TypeMembers : 0;
            where |= (template.Scope & TemplateScope.Type) != 0 ? TemplateAvailability.TypesAndNamespaces : 0;
        }
        where |= (template.Surround & SurroundScope.Statements) != 0 ? TemplateAvailability.Statements : 0;
        where |= (template.Surround & SurroundScope.Expression) != 0 ? TemplateAvailability.Expressions : 0;
        where |= (template.Surround & SurroundScope.Lines) != 0 ? TemplateAvailability.LineStart : 0;
        where |= (template.Surround & SurroundScope.Types) != 0 ? TemplateAvailability.TypesAndNamespaces : 0;
        where |= (template.Surround & SurroundScope.Everywhere) != 0 ? TemplateAvailability.Everywhere : 0;
        TemplateUse use = !template.Live ? TemplateUse.Surround : template.Surround != SurroundScope.None ? TemplateUse.Both : TemplateUse.Generation;
        return (where, use);
    }

    /// <summary>A template offered where an availability says, in what it is used in; a place one use has no meaning
    /// for (Surround With in a type member, a shortcut at a line's start) is none for that use.</summary>
    public static LiveTemplate Available(LiveTemplate template, TemplateAvailability where, TemplateUse use)
    {
        TemplateScope scope = TemplateScope.None;
        SurroundScope surround = SurroundScope.None;
        if (use != TemplateUse.Surround)
        {
            scope |= where.HasFlag(TemplateAvailability.Statements) ? TemplateScope.Statement : 0;
            scope |= where.HasFlag(TemplateAvailability.TypeMembers) ? TemplateScope.Member : 0;
            scope |= where.HasFlag(TemplateAvailability.TypesAndNamespaces) ? TemplateScope.Type : 0;
        }
        if (use != TemplateUse.Generation)
        {
            surround |= where.HasFlag(TemplateAvailability.Statements) ? SurroundScope.Statements : 0;
            surround |= where.HasFlag(TemplateAvailability.Expressions) ? SurroundScope.Expression : 0;
            surround |= where.HasFlag(TemplateAvailability.LineStart) ? SurroundScope.Lines : 0;
            surround |= where.HasFlag(TemplateAvailability.TypesAndNamespaces) ? SurroundScope.Types : 0;
            surround |= where.HasFlag(TemplateAvailability.Everywhere) ? SurroundScope.Everywhere : 0;
        }
        return template with { Live = use != TemplateUse.Surround, Scope = scope, Surround = surround };
    }

    /// <summary>The variables a template's text names, in the order they first appear, without the syntax's own.</summary>
    public static List<string> Names(string text)
    {
        List<string> names = [];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$')
            {
                continue;
            }
            int close = text.IndexOf('$', i + 1);
            if (close < 0)
            {
                break;
            }
            string name = text[(i + 1)..close];
            if (name.Length > 0 && !Reserved.Contains(name) && !names.Contains(name))
            {
                names.Add(name);
            }
            i = close;
        }
        return names;
    }

    /// <summary>A date as a macro's format asks, its fallback for none or one that does not parse.</summary>
    private static string Formatted(DateTime when, string format, string fallback)
    {
        try
        {
            return when.ToString(format.Length == 0 ? fallback : format, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return when.ToString(fallback, CultureInfo.CurrentCulture);
        }
    }
}

/// <summary>A XAML surround template: whether it may hold one child only, and whether only Avalonia's XAML has it.</summary>
public sealed record XamlSurround(LiveTemplate Template, bool OneChild = false, bool AvaloniaOnly = false);

/// <summary>The templates in force: the live and surround templates with the person's changes, and the postfix
/// templates turned on, with whether completion lists them.</summary>
public sealed class TemplateSet
{
    public TemplateSet(IReadOnlyList<LiveTemplate> live, IReadOnlyList<PostfixTemplate> postfix, bool postfixInCompletion = true)
    {
        All = live;
        Postfix = postfix;
        PostfixInCompletion = postfixInCompletion;
    }

    /// <summary>Passenger's own, as shipped.</summary>
    public static TemplateSet Default { get; } = new(LiveTemplates.BuiltIn, PostfixTemplates.All);

    /// <summary>The templates as the settings leave them: no postfix template at all while Postfix Completion is off.</summary>
    public static TemplateSet Of(Settings.AppSettings settings) => new(Settings.TemplateChange.Applied(settings.LiveTemplates),
        settings.PostfixCompletion ? [.. PostfixTemplates.All.Where(t => settings.PostfixTemplates.GetValueOrDefault(t.Name, true))] : [],
        settings.PostfixInCompletion);

    /// <summary>Every live and surround template, those turned off too.</summary>
    public IReadOnlyList<LiveTemplate> All { get; }

    /// <summary>The postfix templates turned on.</summary>
    public IReadOnlyList<PostfixTemplate> Postfix { get; }

    /// <summary>Show postfix templates in completion autopopup: whether the list after a dot offers them, which Tab
    /// expands either way.</summary>
    public bool PostfixInCompletion { get; }

    /// <summary>The templates that may go where a scope allows.</summary>
    public IEnumerable<LiveTemplate> In(TemplateScope scope) => All.Where(t => t.Enabled && t.Live && (t.Scope & scope) != 0);

    /// <summary>What Surround With offers around code standing in these scopes: those with a digit by it (1 to 9, then
    /// 0), then the rest by name.</summary>
    public IReadOnlyList<LiveTemplate> Around(SurroundScope scope) =>
        [.. All.Where(t => t.Enabled && (t.Surround & scope) != 0)
            .OrderBy(t => t.Mnemonic is { } digit ? (digit == '0' ? 10 : digit - '0') : 11)
            .ThenBy(t => t.Abbreviation, StringComparer.Ordinal)];
}
