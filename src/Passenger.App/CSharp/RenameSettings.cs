using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.CSharp;

/// <summary>What Rename's options were left at, in the store's refactorings.json: whether a type's rename renames the
/// files named after it (Also rename files to reflect this change) and whether a file's rename renames the type named
/// after it (Rename type to match the new file name), both on until turned off.</summary>
public sealed record RenameSettings : IStateDocument<RenameSettings>
{
    public bool RenameFiles { get; init; } = true;

    public bool RenameType { get; init; } = true;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public RenameSettings Clamped() => this;
}
