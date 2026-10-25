using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Passenger.App.MSBuild;

/// <summary>An attribute an MSBuild element takes, with its documentation and the values it is known to take.</summary>
public sealed record SchemaAttribute(string Name, string? Documentation, IReadOnlyList<string> Values);

/// <summary>An element of MSBuild's schemas: a structural element (Project, PropertyGroup, Target), a property, an item
/// type, a piece of an item's metadata or a task, with its documentation, attributes, the values its text takes, and what
/// may go inside it (named elements, and any property, item or task).</summary>
public sealed class SchemaElement(string name, string? documentation, IReadOnlyList<SchemaAttribute> attributes, IReadOnlyList<string> values,
    Func<(IReadOnlyList<SchemaElement> Children, bool Properties, bool Items, bool Tasks)> content)
{
    private readonly Lazy<(IReadOnlyList<SchemaElement> Children, bool Properties, bool Items, bool Tasks)> inside = new(content);

    public string Name { get; } = name;

    public string? Documentation { get; } = documentation;

    public IReadOnlyList<SchemaAttribute> Attributes { get; } = attributes;

    public IReadOnlyList<string> Values { get; } = values;

    public IReadOnlyList<SchemaElement> Children => inside.Value.Children;

    public bool TakesProperties => inside.Value.Properties;

    public bool TakesItems => inside.Value.Items;

    public bool TakesTasks => inside.Value.Tasks;

    public SchemaAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SchemaElement? Child(string name) => Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>MSBuild's own schemas (Microsoft.Build.Core.xsd and Microsoft.Build.CommonTypes.xsd from dotnet/msbuild,
/// MIT), carried since the .NET SDK does not: read once, on first use, into the structural elements reached from
/// Project, and the properties, item types (with their metadata) and tasks each substitution group declares, with a few
/// the schemas lack (central package management's, the SDK's own items).</summary>
public sealed partial class MSBuildSchema
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    private static readonly Lazy<MSBuildSchema> Read = new(() => new MSBuildSchema());

    private readonly Dictionary<string, XElement> complexTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> simpleTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, SchemaElement> made = [];

    /// <summary>The schemas, read the first time they are asked for: a caller on the UI thread asks off it.</summary>
    public static MSBuildSchema Instance => Read.Value;

    public SchemaElement Project { get; }

    /// <summary>The structural elements by name: those Project holds, and those they hold in turn.</summary>
    public IReadOnlyDictionary<string, SchemaElement> Structure { get; }

    public IReadOnlyDictionary<string, SchemaElement> Properties { get; }

    public IReadOnlyDictionary<string, SchemaElement> Items { get; }

    public IReadOnlyDictionary<string, SchemaElement> Tasks { get; }

    private MSBuildSchema()
    {
        XElement[] schemas = [Load("Microsoft.Build.Core.xsd"), Load("Microsoft.Build.CommonTypes.xsd")];
        foreach (XElement schema in schemas)
        {
            // Those under xs:redefine restate a type of Core's only to loosen it: Core's stands.
            foreach (XElement declared in schema.Elements())
            {
                if (declared.Attribute("name")?.Value is not { } name)
                {
                    continue;
                }
                Dictionary<string, XElement>? into = declared.Name.LocalName switch
                {
                    "complexType" => complexTypes,
                    "simpleType" => simpleTypes,
                    "group" => groups,
                    _ => null,
                };
                into?.TryAdd(name, declared);
            }
        }
        Dictionary<string, SchemaElement> properties = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SchemaElement> items = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SchemaElement> tasks = new(StringComparer.OrdinalIgnoreCase);
        SchemaElement? project = null;
        foreach (XElement declared in schemas.SelectMany(s => s.Elements(Xs + "element")))
        {
            if (declared.Attribute("name")?.Value is not { } name || declared.Attribute("abstract")?.Value == "true")
            {
                continue;
            }
            Dictionary<string, SchemaElement>? into = declared.Attribute("substitutionGroup")?.Value switch
            {
                "msb:Property" => properties,
                "msb:Item" => items,
                "msb:Task" => tasks,
                _ => null,
            };
            if (into is not null)
            {
                into.TryAdd(name, Element(declared));
            }
            else if (name == "Project")
            {
                project = Element(declared);
            }
        }
        foreach (SchemaElement own in OwnItems())
        {
            items.TryAdd(own.Name, own);
        }
        Project = project ?? throw new InvalidDataException("MSBuild's schema declares no Project element");
        Dictionary<string, SchemaElement> structure = new(StringComparer.OrdinalIgnoreCase) { ["Project"] = Project };
        Queue<SchemaElement> walking = new([Project]);
        while (walking.TryDequeue(out SchemaElement? element))
        {
            foreach (SchemaElement child in element.Children)
            {
                if (structure.TryAdd(child.Name, child))
                {
                    walking.Enqueue(child);
                }
            }
        }
        Structure = structure;
        Properties = properties;
        Items = items;
        Tasks = tasks;
    }

    private static XElement Load(string name)
    {
        using Stream stream = typeof(MSBuildSchema).Assembly.GetManifestResourceStream($"MSBuild/{name}")
            ?? throw new InvalidDataException($"The app carries no {name}");
        return XDocument.Load(stream).Root!;
    }

    /// <summary>A declaration as an element: its documentation (its own, else its type's), attributes, values and content,
    /// made once, under a lock, since an element's content is read lazily by whichever thread asks first.</summary>
    private SchemaElement Element(XElement declared)
    {
        lock (made)
        {
            return Made(declared);
        }
    }

    private SchemaElement Made(XElement declared)
    {
        if (made.TryGetValue(declared, out SchemaElement? known))
        {
            return known;
        }
        string type = Local(declared.Attribute("type")?.Value);
        XElement? complex = declared.Element(Xs + "complexType") ?? complexTypes.GetValueOrDefault(type);
        XElement? simple = declared.Element(Xs + "simpleType") ?? simpleTypes.GetValueOrDefault(type);
        string? documentation = Documentation(declared) ?? (complex is null ? null : Documentation(complex));
        IReadOnlyList<SchemaAttribute> attributes = complex is null ? [] : Attributes(complex);
        IReadOnlyList<string> values = simple is null ? [] : Values(simple);
        SchemaElement element = new(declared.Attribute("name")!.Value, documentation, attributes, values, () => complex is null ? ([], false, false, false) : Content(complex));
        made[declared] = element;
        return element;
    }

    /// <summary>A complex type's attributes, those of the type it extends first.</summary>
    private List<SchemaAttribute> Attributes(XElement complex)
    {
        List<SchemaAttribute> found = [];
        foreach (XElement derived in complex.Elements().Where(e => e.Name.LocalName is "complexContent" or "simpleContent").SelectMany(e => e.Elements()))
        {
            if (complexTypes.GetValueOrDefault(Local(derived.Attribute("base")?.Value)) is { } based)
            {
                found.AddRange(Attributes(based));
            }
            found.AddRange(derived.Elements(Xs + "attribute").Select(Attribute).OfType<SchemaAttribute>());
        }
        found.AddRange(complex.Elements(Xs + "attribute").Select(Attribute).OfType<SchemaAttribute>());
        // A derived type's restatement (Include on PackageReference) wins over its base's.
        return [.. found.AsEnumerable().Reverse().DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Reverse()];
    }

    private SchemaAttribute? Attribute(XElement attribute)
    {
        if (attribute.Attribute("name")?.Value is not { } name || attribute.Attribute("use")?.Value == "prohibited")
        {
            return null;
        }
        XElement? simple = attribute.Element(Xs + "simpleType") ?? simpleTypes.GetValueOrDefault(Local(attribute.Attribute("type")?.Value));
        return new SchemaAttribute(name, Documentation(attribute), simple is null ? [] : Values(simple));
    }

    /// <summary>What a complex type's content model lets in: its named elements, and whether any property, item or task.</summary>
    private (IReadOnlyList<SchemaElement>, bool, bool, bool) Content(XElement complex)
    {
        List<SchemaElement> children = [];
        bool properties = false;
        bool items = false;
        bool tasks = false;
        HashSet<XElement> seen = [];
        Walk(complex);
        return ([.. children.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)], properties, items, tasks);

        void Walk(XElement model)
        {
            if (!seen.Add(model))
            {
                return;
            }
            foreach (XElement part in model.Elements())
            {
                switch (part.Name.LocalName)
                {
                    case "element" when part.Attribute("ref")?.Value is { } reference:
                        properties |= reference == "msb:Property";
                        items |= reference == "msb:Item";
                        tasks |= reference == "msb:Task";
                        break;
                    case "element" when part.Attribute("name") is not null:
                        children.Add(Element(part));
                        break;
                    case "group" when groups.GetValueOrDefault(Local(part.Attribute("ref")?.Value)) is { } group:
                        Walk(group);
                        break;
                    case "complexContent" or "simpleContent":
                        foreach (XElement derived in part.Elements())
                        {
                            if (complexTypes.GetValueOrDefault(Local(derived.Attribute("base")?.Value)) is { } based)
                            {
                                Walk(based);
                            }
                            Walk(derived);
                        }
                        break;
                    case "sequence" or "choice" or "all":
                        Walk(part);
                        break;
                }
            }
        }
    }

    private static List<string> Values(XElement simple) =>
        [.. simple.Descendants(Xs + "enumeration").Select(e => e.Attribute("value")?.Value).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>An annotation's words, its localisation comment left out and its white space run together.</summary>
    private static string? Documentation(XElement declared) =>
        declared.Element(Xs + "annotation")?.Element(Xs + "documentation") is { } documentation
            && Space().Replace(string.Concat(documentation.Nodes().OfType<XText>().Select(t => t.Value)), " ").Trim() is { Length: > 0 } words
            ? words
            : null;

    private static string Local(string? qualified) => qualified is null ? string.Empty : qualified[(qualified.IndexOf(':') + 1)..];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>Items the schemas lack that every SDK-style project meets: central package management's versions, a
    /// package downloaded without being referenced, one referenced by every project, and analyzers' extra files.</summary>
    private IEnumerable<SchemaElement> OwnItems()
    {
        SchemaAttribute condition = new("Condition", "Optional expression evaluated to determine whether the items should be evaluated", []);
        SchemaAttribute include = new("Include", "Name of the package", []);
        SchemaAttribute version = new("Version", "Version of the package", []);
        SchemaAttribute label = new("Label", "Optional expression. Used to identify or order system and user elements", []);
        SchemaAttribute update = new("Update", "Semi-colon separated list of packages to be updated with the metadata contained in this element", []);
        SchemaAttribute remove = new("Remove", "Semi-colon separated list of packages to remove from the existing list contents", []);
        SchemaElement Item(string name, string documentation, params SchemaAttribute[] own) =>
            new(name, documentation, [condition, .. own, label], [], () => ([], false, false, false));
        yield return Item("PackageVersion", "A package's version under central package management (Directory.Packages.props), which the projects' PackageReference items take", include, version, update, remove);
        yield return Item("GlobalPackageReference", "A package every project under central package management references, as a development dependency", include, version, update, remove);
        yield return Item("PackageDownload", "A package restore downloads without the project referencing it", include, version, update, remove);
        yield return Item("AdditionalFiles", "A file passed to the project's analyzers and source generators",
            new SchemaAttribute("Include", "Semi-colon separated list of files (wildcards are allowed)", []),
            new SchemaAttribute("Exclude", "Semi-colon separated list of files (wildcards are allowed) to exclude from the Include list", []), update, remove);
    }
}
