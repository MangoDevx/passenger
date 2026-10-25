using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>The ring round what has the keys, every control's (ControlThemes.axaml's focus adorner): two pixels just
/// outside the control's edge, its corners the control's own and the ring's width rounder, so it is centred on what it
/// surrounds; a check box's or radio button's round its box alone, not round its words too.</summary>
public sealed class FocusRing : Control
{
    /// <summary>How broad the ring is, outside the edge it surrounds.</summary>
    public const double RingWidth = 2;

    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<FocusRing, IBrush?>(nameof(Brush));

    static FocusRing()
    {
        AffectsRender<FocusRing>(BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<FocusRing>(false);
    }

    public FocusRing()
    {
        // The ring stands outside the edge it surrounds, where the adorner layer would cut it off.
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>What the ring surrounds, in its own coordinates (the control it adorns), and that edge's corner radius.</summary>
    public (Rect Edge, double Radius) Surrounds()
    {
        Visual? adorned = AdornerLayer.GetAdornedElement(this);
        Rect whole = new(Bounds.Size);
        if (adorned is CheckBox or RadioButton)
        {
            // Fluent's box is NormalRectangle, its round button OuterEllipse.
            Control? box = adorned.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Name is "NormalRectangle" or "OuterEllipse" && c.IsEffectivelyVisible);
            if (box is not null && box.TranslatePoint(default, adorned) is { } at)
            {
                Rect edge = new(at, box.Bounds.Size);
                double radius = box switch
                {
                    Border border => border.CornerRadius.TopLeft,
                    Ellipse => Math.Min(edge.Width, edge.Height) / 2,
                    _ => 0,
                };
                return (edge, radius);
            }
        }
        return (whole, adorned is TemplatedControl templated ? templated.CornerRadius.TopLeft : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        InvalidateVisual();
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush)
        {
            return;
        }
        (Rect edge, double radius) = Surrounds();
        // The stroke's middle a half ring out, so its inner side meets the edge.
        Rect ring = edge.Inflate(RingWidth / 2);
        double round = radius + (RingWidth / 2);
        context.DrawRectangle(null, new Pen(brush, RingWidth), new RoundedRect(ring, round));
    }
}
