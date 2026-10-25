using Avalonia.Controls;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>The Move to Another File and Move to Namespace dialogs for a type: the new file's name, or the namespace it
/// is to be in.</summary>
public sealed class MoveTypeDialog : RefactoringDialog
{
    private readonly bool toFile;
    private readonly TypeSource type;
    private readonly Func<string, Task<string?>> make;

    private MoveTypeDialog(TypeSource type, bool toFile, Func<string, Task<string?>> make)
        : base(toFile ? "Move to Another File" : "Move to Namespace", toFile ? "Specify a name of a file" : "Specify namespace",
            toFile
                ? $"Specify the name of a new file beside {type.FileName} to move {type.Kind} '{type.Name}' to"
                : $"Specify the namespace to move {type.Kind} '{type.Name}' to{(type.Namespace.Length > 0 ? $" from '{type.Namespace}'" : string.Empty)}; the code that uses it follows",
            480)
    {
        this.type = type;
        this.toFile = toFile;
        this.make = make;
        TargetBox = Field(toFile ? type.TargetFileName : type.Namespace);
        TargetBox.TextChanged += (_, _) => Changed();
        Options = Row(toFile ? "Target file name:" : "Target namespace:", TargetBox, 140);
        Changed();
        FocusOnOpen(TargetBox);
    }

    public static MoveTypeDialog ToFile(TypeSource type, Func<string, Task<string?>> make) => new(type, toFile: true, make);

    public static MoveTypeDialog ToNamespace(TypeSource type, Func<string, Task<string?>> make) => new(type, toFile: false, make);

    public TextBox TargetBox { get; }

    protected override string? Check()
    {
        string target = TargetBox.Text?.Trim() ?? string.Empty;
        if (toFile)
        {
            return target.Length == 0 ? "file name is empty"
                : !target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "The new file name extension is not valid for source code files"
                : string.Equals(target, type.FileName, PathNames.Comparison) ? $"'{type.Name}' is in {type.FileName} already"
                : null;
        }
        if (target.Length == 0)
        {
            return "Specify namespace";
        }
        if (target.Split('.').Any(p => !SyntaxFacts.IsValidIdentifier(p.Trim()) || SyntaxFacts.GetKeywordKind(p.Trim()) != SyntaxKind.None))
        {
            return $"'{target}' is not a valid namespace name";
        }
        return target == type.Namespace ? $"'{type.Name}' is in namespace '{target}' already" : null;
    }

    protected override Task<string?> MakeAsync() => make(TargetBox.Text?.Trim() ?? string.Empty);
}
