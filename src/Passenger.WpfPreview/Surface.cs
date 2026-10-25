using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Passenger.WpfPreview;

/// <summary>What a file's root is drawn on, never shown. A window is drawn as itself, in the window WPF makes for it before
/// it shows one, which is never shown, so what its content finds of it as the app runs is found (an ancestor binding of
/// its type, <c>Window.GetWindow</c>, its own style, template and triggers): its client area at its size (its Width and
/// Height, else d:DesignWidth and d:DesignHeight), under a title bar as tall as the system's caption. A page is the content
/// of a window of the previewer's own, with no frame, as an app's page is in its window. Any other element is drawn as
/// itself on the window colour, at its own size, else its design size, else the size its content asks.</summary>
internal sealed partial class Surface
{
    /// <summary>A window or page whose size says nothing, at the size the project's template gives a new window.</summary>
    private static readonly Size Unsized = new(800, 450);

    /// <summary>What <c>Window.Show</c> does before it shows a window: its Visibility made Visible without showing it, as the
    /// window's content is laid out, and counts as visible, only while its window is.</summary>
    private static readonly MethodInfo? MarkShown =
        typeof(Window).GetMethod("UpdateVisibilityProperty", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Visibility)]);

    /// <summary>What WPF attaches to a window given a WindowChrome as the app runs, and leaves to the designer in
    /// design mode.</summary>
    private static readonly Type? ChromeWorker = typeof(WindowChrome).Assembly.GetType("System.Windows.Shell.WindowChromeWorker");

    private readonly Window? window;
    private HwndSource? host;
    private Border? bar;
    private Size client;

    private Surface(object loaded, FrameworkElement drawn, Window? window)
    {
        Loaded = loaded;
        Drawn = drawn;
        this.window = window;
    }

    /// <summary>The object the file made.</summary>
    public object Loaded { get; }

    /// <summary>What is laid out and rendered: the window (the file's, or the one a page is in), else the border an element
    /// is on.</summary>
    public FrameworkElement Drawn { get; }

    /// <summary>How far down the frame what is drawn starts: the title bar's height, a whole number of the frame's pixels.</summary>
    public double Top { get; private set; }

    /// <summary>The frame's size in the design's units, once laid out: the title bar and what is drawn below it.</summary>
    public Size Size => new(client.Width, Top + client.Height);

    /// <summary>The surface for a file's root; null when it draws nothing (a style, a flow document).</summary>
    public static Surface? For(object root, LooseXaml loose) => root switch
    {
        Window window => OfWindow(window, loose),
        Page page => OfPage(page, loose),
        FrameworkElement element => new Surface(element, OfElement(element, element.Width, element.Height, loose), null),
        UIElement element => new Surface(element, OfElement(element, double.NaN, double.NaN, loose), null),
        _ => null,
    };

    /// <summary>A window drawn as it is when shown at first, whatever state it asks to start in.</summary>
    private static Surface OfWindow(Window window, LooseXaml loose)
    {
        if (window.WindowState != WindowState.Normal)
        {
            window.WindowState = WindowState.Normal;
        }
        SizeToContent fits = window.SizeToContent;
        if (!fits.HasFlag(SizeToContent.Width) && Given(window.Width) is null)
        {
            window.Width = loose.DesignWidth ?? Unsized.Width;
        }
        if (!fits.HasFlag(SizeToContent.Height) && Given(window.Height) is null)
        {
            window.Height = loose.DesignHeight ?? Unsized.Height;
        }
        return new Surface(window, window, window);
    }

    /// <summary>A page in a window with no frame, no style of the app's (an app's page is in a window of its own class) and
    /// no navigation: the page's WindowTitle, WindowWidth and WindowHeight go to it as they go to the app's window.</summary>
    private static Surface OfPage(Page page, LooseXaml loose)
    {
        Window holder = new()
        {
            Style = Unstyled<Window>(),
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Width = Given(page.Width) ?? loose.DesignWidth ?? Unsized.Width,
            Height = Given(page.Height) ?? loose.DesignHeight ?? Unsized.Height,
        };
        holder.Content = page;
        return new Surface(page, holder, holder);
    }

    private static Border OfElement(UIElement element, double width, double height, LooseXaml loose) => new()
    {
        Style = Unstyled<Border>(),
        Background = SystemColors.WindowBrush,
        Width = double.IsNaN(width) && loose.DesignWidth is { } designWidth ? designWidth : double.NaN,
        Height = double.IsNaN(height) && loose.DesignHeight is { } designHeight ? designHeight : double.NaN,
        Child = element,
    };

    /// <summary>Puts what is drawn in a window that is never shown, so it is laid out, styled and loaded as an app's content
    /// is: a window in its own, an element in one of the previewer's. Throws when a window cannot be drawn unshown.</summary>
    public void Place()
    {
        if (window is null)
        {
            host = Host(Drawn);
            return;
        }
        if (window.Visibility == Visibility.Visible)
        {
            // WPF has queued showing it, which a window closed before the dispatcher runs never does.
            window.Close();
            throw new InvalidOperationException("Something sets the window's Visibility (a style's setter), which would show it, and the preview never shows a window");
        }
        if (MarkShown is null)
        {
            window.Close();
            throw new InvalidOperationException("This WPF does not let the previewer lay a window out without showing it");
        }
        KeepChrome(window);
        // As asked, before the system keeps the window within its screens.
        double width = window.Width;
        double height = window.Height;
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource source = HwndSource.FromHwnd(handle);
        source.AddHook(Unbounded);
        if (!double.IsNaN(width) || !double.IsNaN(height))
        {
            Matrix toDevice = source.CompositionTarget.TransformToDevice;
            GetWindowRect(handle, out Box made);
            int across = double.IsNaN(width) ? made.Right - made.Left : (int)Math.Round(width * toDevice.M11);
            int down = double.IsNaN(height) ? made.Bottom - made.Top : (int)Math.Round(height * toDevice.M22);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, across, down, NoMove | NoZOrder | NoActivate);
        }
        MarkShown.Invoke(window, [Visibility.Visible]);
        source.RootVisual = window;
    }

    /// <summary>A window's WindowChrome put to work as WPF puts it as the app runs, which in design mode it leaves to
    /// the designer to draw: so the chrome makes the window's client area what the app's is (the whole window,
    /// usually), and no title bar is drawn over content that draws its own.</summary>
    private static void KeepChrome(Window window)
    {
        if (WindowChrome.GetWindowChrome(window) is not { } chrome)
        {
            return;
        }
        const BindingFlags Statics = BindingFlags.Public | BindingFlags.Static;
        MethodInfo? attached = ChromeWorker?.GetMethod("GetWindowChromeWorker", Statics, [typeof(Window)]);
        MethodInfo? attach = ChromeWorker?.GetMethod("SetWindowChromeWorker", Statics, [typeof(Window), ChromeWorker]);
        MethodInfo? apply = ChromeWorker?.GetMethod("SetWindowChrome", BindingFlags.Public | BindingFlags.Instance, [typeof(WindowChrome)]);
        if (ChromeWorker is null || attached is null || attach is null || apply is null)
        {
            Console.Error.WriteLine("This WPF does not let the previewer put a window's WindowChrome to work in design mode: it is drawn in the system's frame");
            return;
        }
        if (attached.Invoke(null, [window]) is not null)
        {
            // The project's code took the window out of design mode, and WPF attached one itself.
            return;
        }
        object worker = Activator.CreateInstance(ChromeWorker, nonPublic: true)!;
        attach.Invoke(null, [window, worker]);
        apply.Invoke(worker, [chrome]);
    }

    /// <summary>Laid out again, at the size its bindings and loading have given it, its title bar a whole number of pixels
    /// high at the scale it is drawn at.</summary>
    public void Lay(double scale)
    {
        if (window is null)
        {
            if (host is null)
            {
                Drawn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Drawn.Arrange(new Rect(Drawn.DesiredSize));
            }
            Drawn.UpdateLayout();
            client = new Size(Drawn.ActualWidth, Drawn.ActualHeight);
            return;
        }
        window.UpdateLayout();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { CompositionTarget: { } target })
        {
            client = default;
            return;
        }
        Matrix fromDevice = target.TransformFromDevice;
        GetWindowRect(handle, out Box outer);
        GetClientRect(handle, out Box inner);
        Corner origin = default;
        ClientToScreen(handle, ref origin);
        client = new Size(inner.Right * fromDevice.M11, inner.Bottom * fromDevice.M22);
        // The caption: what the system draws above the client area beyond the frame below it.
        int caption = origin.Y - outer.Top - (outer.Bottom - origin.Y - inner.Bottom);
        Top = 0;
        bar = null;
        if (caption > 0)
        {
            Top = Math.Max(1, Math.Round(caption * fromDevice.M22 * scale)) / scale;
            bar = TitleBar(window.Title, client.Width, Top);
        }
    }

    /// <summary>The frame at the scale it was laid out for, as a PNG whose pixels are at 96 DPI (the pane sizes it by the
    /// scale): the title bar's rows, then what is drawn's.</summary>
    public byte[] Render(double scale)
    {
        int width = (int)Math.Ceiling(client.Width * scale);
        int top = (int)Math.Round(Top * scale);
        int below = (int)Math.Ceiling(client.Height * scale);
        int height = top + below;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        if (bar is not null)
        {
            Copy(bar, width, top, scale, pixels, 0);
        }
        Copy(Drawn, width, below, scale, pixels, top * stride);
        BitmapSource plain = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        PngBitmapEncoder png = new();
        png.Frames.Add(BitmapFrame.Create(plain));
        using MemoryStream bytes = new();
        png.Save(bytes);
        return bytes.ToArray();
    }

    /// <summary>The drawing let go: the popup an element was laid out in destroyed, the window drawn (never shown) closed.</summary>
    public void Close()
    {
        if (host is not null)
        {
            host.RootVisual = null;
            host.Dispose();
            host = null;
        }
        if (window is not null)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine($"A preview's window did not close: {error.Message}");
            }
        }
    }

    private static void Copy(Visual visual, int width, int height, double scale, byte[] pixels, int offset)
    {
        if (width < 1 || height < 1)
        {
            return;
        }
        RenderTargetBitmap rendered = new(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.CopyPixels(Int32Rect.Empty, pixels, width * 4, offset);
    }

    /// <summary>A title bar like the system's, laid out alone: it is drawn, never part of what the window holds.</summary>
    private static Border TitleBar(string? title, double width, double height)
    {
        Border bar = new()
        {
            Width = width,
            Height = height,
            Style = Unstyled<Border>(),
            Background = SystemColors.ControlLightLightBrush,
            BorderBrush = SystemColors.ControlLightBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock
            {
                Text = title,
                Style = Unstyled<TextBlock>(),
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = SystemFonts.CaptionFontFamily,
                FontSize = SystemFonts.CaptionFontSize,
                Foreground = SystemColors.ControlTextBrush,
            },
        };
        bar.Measure(new Size(width, height));
        bar.Arrange(new Rect(0, 0, width, height));
        bar.UpdateLayout();
        return bar;
    }

    /// <summary>An element in a window of its own that is never shown; none when the system gives no such window, and the
    /// element is laid out alone.</summary>
    private static HwndSource? Host(FrameworkElement surface)
    {
        try
        {
            HwndSourceParameters parameters = new("Passenger WPF preview")
            {
                // A popup never shown, off every screen, never activated and never in the taskbar.
                WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x08000080,
                PositionX = -32000,
                PositionY = -32000,
                Width = 1,
                Height = 1,
            };
            return new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = surface };
        }
        catch (Win32Exception error)
        {
            Console.Error.WriteLine($"No window to lay the preview out in: {error.Message}");
            return null;
        }
    }

    /// <summary>Lets the window be as large as it asks, which the system would keep within its screens.</summary>
    private static IntPtr Unbounded(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int GetMinMaxInfo = 0x0024;
        if (message == GetMinMaxInfo)
        {
            Limits limits = Marshal.PtrToStructure<Limits>(lParam);
            limits.MaxTrackSize = new Corner { X = short.MaxValue, Y = short.MaxValue };
            Marshal.StructureToPtr(limits, lParam, false);
        }
        return IntPtr.Zero;
    }

    private static double? Given(double size) => double.IsNaN(size) || double.IsInfinity(size) ? null : size;

    /// <summary>A style of its own, so the app's implicit styles leave the previewer's own parts as they are.</summary>
    private static Style Unstyled<T>() => new(typeof(T));

    private const int NoMove = 0x0002;
    private const int NoZOrder = 0x0004;
    private const int NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Corner
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>MINMAXINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        public Corner Reserved;
        public Corner MaxSize;
        public Corner MaxPosition;
        public Corner MinTrackSize;
        public Corner MaxTrackSize;
    }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, int flags);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr handle, out Box box);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr handle, out Box box);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr handle, ref Corner point);
}
