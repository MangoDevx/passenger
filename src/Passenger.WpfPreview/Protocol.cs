using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// What Passenger and its WPF previewer say to each other, one definition for both ends (the app compiles this file
// too): one JSON object a line each way over the previewer's standard input and output. Passenger sends the file's text
// as it stands (WpfPreviewRequest) whenever it changes or the window's scale does; the previewer answers the newest it
// has read (WpfPreviewReply, by its sequence): a frame, the error that kept the XAML from loading, or why there is
// nothing to draw.
namespace Passenger.WpfPreview;

/// <summary>A XAML file to draw: its text as it stands, the pack URI it has in its project's assembly (what relative
/// URIs in it resolve against, as they would in the build), the application's definition (App.xaml) whose resources it
/// sees, and the scale the pane's window draws at.</summary>
public sealed record WpfPreviewRequest
{
    public required int Sequence { get; init; }

    public required string Path { get; init; }

    public required string Xaml { get; init; }

    public required string BaseUri { get; init; }

    public WpfPreviewSource? Application { get; init; }

    public double Scale { get; init; } = 1;
}

/// <summary>Another XAML file the drawing reads (App.xaml): its text, open or on disk, and its pack URI.</summary>
public sealed record WpfPreviewSource(string Path, string Xaml, string BaseUri);

/// <summary>The previewer's answer to the newest request it read. A frame is a PNG at 96 DPI of <see cref="Scale"/>
/// times the design's size, with the boxes of the file's elements drawn in it; an error says what the XAML did not load
/// for, with its line and column (from 1, as XML readers count the text sent) when it has them; nothing says why there
/// is nothing to draw.</summary>
public sealed record WpfPreviewReply
{
    public required string Kind { get; init; }

    public required int Sequence { get; init; }

    public byte[]? Png { get; init; }

    public double Scale { get; init; } = 1;

    public IReadOnlyList<WpfPreviewBox>? Elements { get; init; }

    public string? Message { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>What the frame was drawn without (App.xaml's resources, a design-time data context that did not load).</summary>
    public IReadOnlyList<WpfPreviewNote>? Notes { get; init; }
}

/// <summary>Something the frame was drawn without and why, with the line of the file's element it is about (from 1, as
/// XML readers count, in the text sent).</summary>
public sealed record WpfPreviewNote(string Message, int? Line = null);

/// <summary>An element of the file the frame draws: where its start tag is (line and column from 1, as XML readers count,
/// at its name) and its box in the frame, in the design's units (the frame's pixels over its scale).</summary>
public sealed record WpfPreviewBox(int Line, int Column, double X, double Y, double Width, double Height);

public static class WpfPreviewKinds
{
    public const string Frame = "frame";
    public const string Error = "error";
    public const string Nothing = "nothing";
}

public static class WpfPreviewJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Named, as the serializer names it by default, so each message's metadata can be had before the first message.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>A request's line as the previewer reads it.</summary>
    public static JsonTypeInfo<WpfPreviewRequest> Request { get; } = (JsonTypeInfo<WpfPreviewRequest>)Options.GetTypeInfo(typeof(WpfPreviewRequest));

    /// <summary>An answer's line as Passenger reads it.</summary>
    public static JsonTypeInfo<WpfPreviewReply> Reply { get; } = (JsonTypeInfo<WpfPreviewReply>)Options.GetTypeInfo(typeof(WpfPreviewReply));
}
