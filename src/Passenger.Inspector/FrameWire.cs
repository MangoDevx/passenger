// The app's frames as Passenger's inspector inside an Avalonia app tells them, through an EventSource of its own
// (Frames.cs), and as Passenger's profiler reads them from a trace (Passenger.Profiler's UiFrames), compiled into both and
// the tests: the provider a session enables, each event's number, and each payload's name, which is the name of the
// writer's parameter that gives it, so a payload renamed at one end reads as nothing at the other.
namespace Passenger.Inspector;

internal static class FrameWire
{
    public const string Provider = "Passenger-Avalonia";

    /// <summary>A control described, once a recording: <see cref="Id"/>, <see cref="Type"/>, <see cref="FullType"/>,
    /// <see cref="Name"/>, <see cref="TemplatedBy"/>, <see cref="File"/>, <see cref="Line"/>.</summary>
    public const int ControlEvent = 1;

    /// <summary>A control's layout in a frame: <see cref="FrameNumber"/>, <see cref="ControlId"/>, <see cref="Measures"/>,
    /// <see cref="MeasureMs"/>, <see cref="MeasureSelfMs"/>, <see cref="Arranges"/>, <see cref="ArrangeMs"/>,
    /// <see cref="ArrangeSelfMs"/>.</summary>
    public const int LayoutEvent = 2;

    /// <summary>A frame's work on the UI thread: <see cref="FrameNumber"/>, <see cref="DurationMs"/>, <see cref="EndedMsAgo"/>,
    /// <see cref="MeasurePasses"/>, <see cref="ArrangePasses"/>, <see cref="MeasureMs"/>, <see cref="ArrangeMs"/>,
    /// <see cref="RenderMs"/>, <see cref="Measures"/>, <see cref="Arranges"/>.</summary>
    public const int FrameEvent = 3;

    /// <summary>The compositor's drawing of a window: <see cref="UpdateMs"/>, <see cref="RenderMs"/>.</summary>
    public const int CompositedEvent = 4;

    public const string Id = "id";
    public const string Type = "type";
    public const string FullType = "fullType";
    public const string Name = "name";
    public const string TemplatedBy = "templatedBy";
    public const string File = "file";
    public const string Line = "line";
    public const string FrameNumber = "frame";
    public const string ControlId = "control";
    public const string Measures = "measures";
    public const string MeasureMs = "measureMs";
    public const string MeasureSelfMs = "measureSelfMs";
    public const string Arranges = "arranges";
    public const string ArrangeMs = "arrangeMs";
    public const string ArrangeSelfMs = "arrangeSelfMs";
    public const string DurationMs = "durationMs";
    public const string EndedMsAgo = "endedMsAgo";
    public const string MeasurePasses = "measurePasses";
    public const string ArrangePasses = "arrangePasses";
    public const string RenderMs = "renderMs";
    public const string UpdateMs = "updateMs";
}
