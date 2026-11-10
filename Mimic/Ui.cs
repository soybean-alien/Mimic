// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Mimic;

/// <summary>Looks colours up by the names the stylesheet used, for the element's current theme.</summary>
public static class Palette
{
    public static Color Color(FrameworkElement element, string key)
    {
        if (key.StartsWith('#')) return Parse(key);
        var theme = element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        var themed = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        return (Color)themed[key];
    }

    public static SolidColorBrush Brush(FrameworkElement element, string key) => new(Color(element, key));

    public static Color Parse(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        if (hex.Length <= 7) value |= 0xFF000000;
        return Windows.UI.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>A button with no Fluent chrome: colours come from palette keys, as in the stylesheet.</summary>
public sealed class FlatButton : Button
{
    private bool hovered;

    public string? BgKey { get; set; }
    public string? BorderKey { get; set; }
    public string? FgKey { get; set; }
    public string? HoverBgKey { get; set; }
    public string? HoverBorderKey { get; set; }
    public string? HoverFgKey { get; set; }
    /// <summary>The stylesheet dims disabled buttons to 48%.</summary>
    public double DisabledOpacity { get; set; } = 0.48;

    public FlatButton()
    {
        DefaultStyleKey = typeof(FlatButton);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        Loaded += (_, _) =>
        {
            // The stylesheet's 1.55 line height is what gave every button its height.
            if (MinHeight == 0)
            {
                MinHeight = FontSize * 1.55 + Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom;
            }
            Apply();
        };
        ActualThemeChanged += (_, _) => Apply();
        PointerEntered += (_, _) => { hovered = true; Apply(); };
        PointerExited += (_, _) => { hovered = false; Apply(); };
        PointerCanceled += (_, _) => { hovered = false; Apply(); };
        IsEnabledChanged += (_, _) => Apply();
    }

    public void Apply()
    {
        var hot = hovered && IsEnabled;
        Background = Pick(hot ? HoverBgKey ?? BgKey : BgKey) ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderBrush = Pick(hot ? HoverBorderKey ?? BorderKey : BorderKey) ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var foreground = Pick(hot ? HoverFgKey ?? FgKey : FgKey);
        if (foreground != null) Foreground = foreground;
        Opacity = IsEnabled ? 1 : DisabledOpacity;
    }

    private SolidColorBrush? Pick(string? key) => key == null ? null : Palette.Brush(this, key);
}

/// <summary>CSS flex-wrap rows: children wrap, and ones marked Grow share what is left on their row.</summary>
public sealed class FlexWrapPanel : Panel
{
    public double Gap { get; set; } = 8;
    public double RowGap { get; set; } = -1;
    public VerticalAlignment RowAlignment { get; set; } = VerticalAlignment.Center;

    public static readonly DependencyProperty GrowProperty =
        DependencyProperty.RegisterAttached("Grow", typeof(double), typeof(FlexWrapPanel), new PropertyMetadata(0d));
    public static void SetGrow(UIElement element, double value) => element.SetValue(GrowProperty, value);
    public static double GetGrow(UIElement element) => (double)element.GetValue(GrowProperty);

    public static readonly DependencyProperty BasisProperty =
        DependencyProperty.RegisterAttached("Basis", typeof(double), typeof(FlexWrapPanel), new PropertyMetadata(double.NaN));
    public static void SetBasis(UIElement element, double value) => element.SetValue(BasisProperty, value);
    public static double GetBasis(UIElement element) => (double)element.GetValue(BasisProperty);

    /// <summary>Pushes this child and the ones after it to the right edge (margin-left: auto).</summary>
    public static readonly DependencyProperty PushRightProperty =
        DependencyProperty.RegisterAttached("PushRight", typeof(bool), typeof(FlexWrapPanel), new PropertyMetadata(false));
    public static void SetPushRight(UIElement element, bool value) => element.SetValue(PushRightProperty, value);
    public static bool GetPushRight(UIElement element) => (bool)element.GetValue(PushRightProperty);

    private sealed record Row(List<(UIElement Child, double Width)> Items, double Height);

    private List<Row> Layout(double available)
    {
        var rows = new List<Row>();
        var items = new List<(UIElement, double)>();
        double used = 0, height = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var basis = GetBasis(child);
            child.Measure(new Size(double.IsNaN(basis) ? available : Math.Min(available, Math.Max(basis, 0)), double.PositiveInfinity));
            var width = double.IsNaN(basis) ? child.DesiredSize.Width : Math.Min(basis, available);
            if (items.Count > 0 && used + Gap + width > available + 0.5)
            {
                rows.Add(Grow(items, height, available));
                items = new List<(UIElement, double)>();
                used = 0;
                height = 0;
            }
            used += (items.Count > 0 ? Gap : 0) + width;
            items.Add((child, width));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        if (items.Count > 0) rows.Add(Grow(items, height, available));
        return rows;
    }

    private Row Grow(List<(UIElement Child, double Width)> items, double height, double available)
    {
        var used = items.Sum(i => i.Width) + Gap * (items.Count - 1);
        var grow = items.Sum(i => GetGrow(i.Child));
        if (grow <= 0 || double.IsInfinity(available) || available <= used) return new Row(items, height);
        var spare = available - used;
        var grown = items.Select(i => (i.Child, i.Width + spare * GetGrow(i.Child) / grow)).ToList();
        double tallest = 0;
        foreach (var (child, width) in grown)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }
        return new Row(grown, tallest);
    }

    private double RowSpacing => RowGap >= 0 ? RowGap : Gap;

    private List<Row> measured = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = measured = Layout(availableSize.Width);
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Items.Sum(i => i.Width) + Gap * (r.Items.Count - 1));
        var height = rows.Sum(r => r.Height) + RowSpacing * Math.Max(0, rows.Count - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        // Rows come from the measure pass; measuring again here would make layout loop.
        foreach (var measuredRow in measured)
        {
            var row = measuredRow;
            var used = row.Items.Sum(i => i.Width) + Gap * (row.Items.Count - 1);
            var grow = row.Items.Sum(i => GetGrow(i.Child));
            if (grow > 0 && finalSize.Width > used + 0.5)
            {
                var spare = finalSize.Width - used;
                row = new Row(row.Items.Select(i => (i.Child, i.Width + spare * GetGrow(i.Child) / grow)).ToList(), row.Height);
                used = finalSize.Width;
            }
            double x = 0;
            var pushed = false;
            foreach (var (child, width) in row.Items)
            {
                if (!pushed && GetPushRight(child))
                {
                    x += Math.Max(0, finalSize.Width - used);
                    pushed = true;
                }
                var height = RowAlignment == VerticalAlignment.Stretch ? row.Height : child.DesiredSize.Height;
                var top = RowAlignment switch
                {
                    VerticalAlignment.Center => y + (row.Height - height) / 2,
                    VerticalAlignment.Bottom => y + row.Height - height,
                    _ => y,
                };
                child.Arrange(new Rect(x, top, width, height));
                x += width + Gap;
            }
            y += row.Height + RowSpacing;
        }
        return finalSize;
    }
}

/// <summary>
/// The thin scrollbar the stylesheet asked for (scrollbar-width: thin, a #bdb4a6 thumb):
/// always shown while the list can scroll, with a small arrow at each end.
/// </summary>
public static class ThinScroll
{
    private const double BarWidth = 12, ArrowZone = 12, ThumbWidth = 6;

    public static void Attach(ScrollViewer viewer)
    {
        var parent = (Grid)viewer.Parent;
        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        var fill = new SolidColorBrush(Palette.Parse("#BDB4A6"));
        var bar = new Canvas
        {
            Width = BarWidth, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(bar, Grid.GetRow(viewer));
        Grid.SetColumn(bar, Grid.GetColumn(viewer));
        Grid.SetColumnSpan(bar, Grid.GetColumnSpan(viewer));
        var up = new Polygon { Fill = fill, Points = { new Point(0, 4), new Point(3, 0), new Point(6, 4) } };
        var down = new Polygon { Fill = fill, Points = { new Point(0, 0), new Point(3, 4), new Point(6, 0) } };
        var thumb = new Rectangle { Width = ThumbWidth, RadiusX = 3, RadiusY = 3, Fill = fill };
        foreach (var part in new FrameworkElement[] { up, down, thumb })
        {
            Canvas.SetLeft(part, (BarWidth - ThumbWidth) / 2);
            bar.Children.Add(part);
        }
        Canvas.SetTop(up, 3);
        parent.Children.Add(bar);

        double Track() => viewer.ActualHeight - 2 * ArrowZone;
        double Length() => Math.Max(16, Track() * viewer.ViewportHeight / Math.Max(1, viewer.ExtentHeight));

        void Update()
        {
            if (viewer.ScrollableHeight <= 0.5 || Track() <= 16)
            {
                bar.Visibility = Visibility.Collapsed;
                return;
            }
            bar.Visibility = Visibility.Visible;
            thumb.Height = Length();
            Canvas.SetTop(thumb, ArrowZone + (Track() - Length()) * viewer.VerticalOffset / viewer.ScrollableHeight);
            Canvas.SetTop(down, viewer.ActualHeight - 7);
        }

        viewer.ViewChanged += (_, _) => Update();
        viewer.SizeChanged += (_, _) => Update();
        viewer.Loaded += (_, _) => Update();
        if (viewer.Content is FrameworkElement content) content.SizeChanged += (_, _) => Update();

        double? grabbedAt = null;
        double grabbedOffset = 0;
        thumb.PointerPressed += (_, e) =>
        {
            grabbedAt = e.GetCurrentPoint(bar).Position.Y;
            grabbedOffset = viewer.VerticalOffset;
            thumb.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        thumb.PointerMoved += (_, e) =>
        {
            if (grabbedAt is not { } start) return;
            var room = Track() - Length();
            if (room <= 0) return;
            var moved = e.GetCurrentPoint(bar).Position.Y - start;
            viewer.ChangeView(null, grabbedOffset + moved * viewer.ScrollableHeight / room, null, true);
        };
        thumb.PointerReleased += (_, e) => { grabbedAt = null; thumb.ReleasePointerCapture(e.Pointer); };
        thumb.PointerCaptureLost += (_, _) => grabbedAt = null;
        bar.PointerPressed += (_, e) =>
        {
            var y = e.GetCurrentPoint(bar).Position.Y;
            var top = Canvas.GetTop(thumb);
            var step = y < ArrowZone || y > viewer.ActualHeight - ArrowZone ? 40 : viewer.ViewportHeight * 0.875;
            if (y < top) viewer.ChangeView(null, viewer.VerticalOffset - step, null);
            else if (y > top + thumb.Height) viewer.ChangeView(null, viewer.VerticalOffset + step, null);
        };
    }
}

/// <summary>CSS box-shadow for a shape: a composition drop shadow drawn on a host placed behind it.</summary>
public static class Glow
{
    /// <summary>Returns a setter that changes the shadow's colour key later.</summary>
    public static Action<string> Attach(FrameworkElement host, Shape shape, string colorKey, float blur, float offsetY = 0)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = blur;
        shadow.Offset = new Vector3(0, offsetY, 0);
        var sprite = compositor.CreateSpriteVisual();
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);

        void Update()
        {
            if (shape.ActualWidth <= 0 || shape.ActualHeight <= 0) return;
            shadow.Color = Palette.Color(shape, colorKey);
            shadow.Mask = shape.GetAlphaMask();
            sprite.Size = new Vector2((float)shape.ActualWidth, (float)shape.ActualHeight);
            var origin = shape.TransformToVisual(host).TransformPoint(new Point(0, 0));
            sprite.Offset = new Vector3((float)origin.X, (float)origin.Y, 0);
        }

        shape.SizeChanged += (_, _) => Update();
        shape.Loaded += (_, _) => Update();
        shape.ActualThemeChanged += (_, _) => Update();
        Update();
        return key => { colorKey = key; Update(); };
    }
}

internal static class Native
{
    private const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30;
    private const int IDYES = 6;
    private const uint SND_ASYNC = 0x1, SND_MEMORY = 0x4, SND_NODEFAULT = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>A native yes/no warning box.</summary>
    public static bool Ask(IntPtr owner, string text, string caption) =>
        MessageBoxW(owner, text, caption, MB_YESNO | MB_ICONWARNING) == IDYES;

    public static void Focus(IntPtr window) => SetForegroundWindow(window);

    private static GCHandle chime;

    /// <summary>The soft two-note bell: 1046.5 Hz then 1396.9 Hz, each with a quiet second harmonic.</summary>
    public static void PlayChime()
    {
        try
        {
            if (!chime.IsAllocated) chime = GCHandle.Alloc(BuildChime(), GCHandleType.Pinned);
            PlaySound(chime.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch
        {
            // A blocked or unavailable audio device should not affect the completed job.
        }
    }

    private static byte[] BuildChime()
    {
        const int rate = 44100;
        var samples = new double[(int)(rate * 0.75)];
        foreach (var (offset, frequency) in new[] { (0.015, 1046.5), (0.175, 1396.9) })
        {
            foreach (var (multiple, volume) in new[] { (1.0, 0.042), (2.0, 0.009) })
            {
                var first = (int)(offset * rate);
                var last = Math.Min(samples.Length, (int)((offset + 0.54) * rate));
                for (var i = first; i < last; i++)
                {
                    var t = (i - first) / (double)rate;
                    // Exponential ramps, as the Web Audio gain node did: up in 12 ms, down by 520 ms.
                    var gain = t < 0.012
                        ? 0.0001 * Math.Pow(volume / 0.0001, t / 0.012)
                        : volume * Math.Pow(0.0001 / volume, Math.Min(1, (t - 0.012) / 0.508));
                    samples[i] += gain * Math.Sin(2 * Math.PI * frequency * multiple * t);
                }
            }
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataLength = samples.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataLength);
        foreach (var sample in samples) writer.Write((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }
}
