using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Passenger.Inspector;

/// <summary>Where Passenger's debugger stops a WPF app for the inspector, by the same names as in an Avalonia app's
/// (<see cref="Wire.Changed"/>, <see cref="Wire.Failed"/>), so one function breakpoint serves either: on <see cref="Changed"/>
/// one per property breakpoint, its condition <c>breakpoint == id</c> and the person's own after it; on
/// <see cref="BindingFailed"/> one while the XAML Binding Errors breakpoint is on. Non-user code, so Just My Code shows the
/// code below that made the change or the binding fail as where the app stopped.</summary>
public static class Breaks
{
    /// <summary>A watched property changed: <c>newValue</c>, <c>oldValue</c>, <c>control</c>, <c>property</c>, and
    /// <c>change</c>, what the IDE says of the stop; <c>setAt</c>, where an Avalonia app's style that set it is written, is
    /// null here, as WPF's value source names no style's place.</summary>
    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Changed(int breakpoint, DependencyObject control, DependencyProperty property, object? oldValue, object? newValue, string change, string? setAt)
    {
        // Held for the debugger, which reads them where the program stops.
        GC.KeepAlive(control);
        GC.KeepAlive(property);
        GC.KeepAlive(oldValue);
        GC.KeepAlive(newValue);
        GC.KeepAlive(change);
        GC.KeepAlive(setAt);
        _ = breakpoint;
    }

    /// <summary>A binding failed, called on the thread that evaluated it as WPF says so, so the stack is the one that failed
    /// (the window's XAML being loaded, the code that set its DataContext or raised PropertyChanged).</summary>
    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BindingFailed(BindingError bindingError) => GC.KeepAlive(bindingError);
}

/// <summary>A binding that failed, as the app stops at it: Passenger's <c>$bindingError</c> (in Variables, and in the
/// breakpoint's condition and log message). The element and the property it sets, its path and the part of the path
/// that failed, the element's DataContext, WPF's message, and where the element is written in XAML.</summary>
[DebuggerDisplay("{Description,nq}")]
public sealed class BindingError
{
    internal BindingError(DependencyObject? control, DependencyProperty? property, object? dataContext, BindingFailure failure, string said)
    {
        Control = control;
        Property = property;
        Path = failure.Path;
        FailedPart = failure.ErrorPoint;
        DataContext = dataContext;
        Message = failure.Message;
        Xaml = failure.Place is { } place ? $"{System.IO.Path.GetFileName(place.File)}:{place.Line}:{place.Column}" : null;
        Description = failure.Description;
        Said = said;
    }

    public DependencyObject? Control { get; }

    public DependencyProperty? Property { get; }

    /// <summary>The binding's path as its XAML writes it.</summary>
    public string Path { get; }

    /// <summary>The part of the path WPF could not read, when it says which.</summary>
    public string? FailedPart { get; }

    /// <summary>The element's DataContext as the binding failed.</summary>
    public object? DataContext { get; }

    public string Message { get; }

    /// <summary>The element in its XAML, "MainWindow.xaml:8:20", when WPF recorded it: its compiler's line.</summary>
    public string? Xaml { get; }

    /// <summary>"TextBlock#Caption.Text to Nmae: BindingExpression path error: 'Nmae' property not found on ...".</summary>
    public string Description { get; }

    /// <summary>The failure as the protocol writes it (<see cref="BindingFailure"/>), which Passenger reads at the stop.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Said { get; }
}
