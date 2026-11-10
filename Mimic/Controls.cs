// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Mimic;

/// <summary>The glowing green dot with a short label ("LOCAL", "Private workspace").</summary>
public sealed class PrivacyBadge : StackPanel
{
    private readonly TextBlock label = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 25 };

    public string Text { get => label.Text; set => label.Text = value; }
    public double TextSize { get => label.FontSize; set { label.FontSize = value; label.LineHeight = value * 1.55; } }
    public bool Spacing0 { get => label.CharacterSpacing == 0; set => label.CharacterSpacing = value ? 0 : 25; }

    public PrivacyBadge()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 7;
        var dot = new Ellipse { Width = 7, Height = 7 };
        var host = new Canvas();
        var holder = new Grid { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        holder.Children.Add(host);
        holder.Children.Add(dot);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.LineHeight = label.FontSize * 1.55;
        Children.Add(holder);
        Children.Add(label);
        void Paint()
        {
            dot.Fill = Palette.Brush(this, "SecureDot");
            label.Foreground = Palette.Brush(this, "SecureText");
        }
        Loaded += (_, _) => { Paint(); Glow.Attach(host, dot, "SecureDotGlow", 9); };
        ActualThemeChanged += (_, _) => Paint();
    }
}

/// <summary>A radio row drawn like the browser's: a small ring, red when chosen.</summary>
public sealed class ChoiceRadio : StackPanel
{
    private readonly Ellipse ring = new() { Width = 13, Height = 13, StrokeThickness = 1 };
    private readonly Ellipse dot = new() { Width = 7, Height = 7 };
    private readonly TextBlock label = new() { FontSize = 13, LineHeight = 20.15, TextWrapping = TextWrapping.Wrap };
    private bool isChecked;

    public string Kind { get; set; } = "";
    public string Text { get => label.Text; set => label.Text = value; }
    public event EventHandler? Chosen;

    public bool IsChecked
    {
        get => isChecked;
        set { isChecked = value; Paint(); }
    }

    public ChoiceRadio()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 12;
        Margin = new Thickness(0, 6, 0, 0);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var holder = new Grid { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0) };
        holder.Children.Add(ring);
        holder.Children.Add(dot);
        Children.Add(holder);
        Children.Add(label);
        Tapped += (_, _) => Chosen?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) => Paint();
        ActualThemeChanged += (_, _) => Paint();
    }

    private void Paint()
    {
        label.Foreground = Palette.Brush(this, "Ink");
        var dark = ActualTheme == ElementTheme.Dark;
        ring.Fill = new SolidColorBrush(Palette.Parse(dark ? "#3B3B3B" : "#FFFFFF"));
        ring.Stroke = isChecked ? Palette.Brush(this, "Red") : new SolidColorBrush(Palette.Parse(dark ? "#858585" : "#767676"));
        ring.StrokeThickness = isChecked ? 2 : 1;
        dot.Fill = Palette.Brush(this, "Red");
        dot.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>One of the two "Project" tabs above the workspace.</summary>
public sealed class TabHeader : Grid
{
    private readonly Border frame = new() { CornerRadius = new CornerRadius(9, 9, 0, 0), BorderThickness = new Thickness(1) };
    private readonly Rectangle underline = new() { Height = 2, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Rectangle shadowShape = new() { RadiusX = 9, RadiusY = 9 };
    private readonly Canvas shadowHost = new();
    private readonly Ellipse runningDot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 1), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock label = new() { FontSize = 12, LineHeight = 18.6, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock timer = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform lift = new();
    private readonly Storyboard pulse = new() { RepeatBehavior = RepeatBehavior.Forever };
    private bool hovered;

    public bool Active { get; private set; }
    public bool Running { get; private set; }
    public bool Added { get; private set; }
    public bool Finished { get; private set; }
    public event EventHandler? Chosen;

    public string TimerText { get => timer.Text; set => timer.Text = value; }

    public TabHeader(string text)
    {
        MinWidth = 132;
        MaxWidth = 240;
        RenderTransform = lift;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        label.Text = text;

        var content = new Grid { Padding = new Thickness(12, 9, 12, 13), ColumnSpacing = 18 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(runningDot);
        left.Children.Add(label);
        content.Children.Add(left);
        SetColumn(timer, 1);
        content.Children.Add(timer);

        Children.Add(shadowHost);
        Children.Add(shadowShape);
        Children.Add(frame);
        Children.Add(content);
        Children.Add(underline);

        var fade = new DoubleAnimation { From = 1, To = 0.35, Duration = TimeSpan.FromSeconds(0.6), AutoReverse = true };
        Storyboard.SetTarget(fade, runningDot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        pulse.Children.Add(fade);

        Tapped += (_, _) => Chosen?.Invoke(this, EventArgs.Empty);
        PointerEntered += (_, _) => { hovered = true; Paint(); };
        PointerExited += (_, _) => { hovered = false; Paint(); };
        Loaded += (_, _) => { Glow.Attach(shadowHost, shadowShape, "#331D2143", 7, 5); Paint(); };
        ActualThemeChanged += (_, _) => Paint();
    }

    public void SetState(bool? active = null, bool? running = null, bool? added = null, bool? finished = null)
    {
        Active = active ?? Active;
        Running = running ?? Running;
        Added = added ?? Added;
        Finished = finished ?? Finished;
        Paint();
    }

    private void Paint()
    {
        var accent = Finished ? "Green" : Added ? "Cyan" : null;
        label.Foreground = Palette.Brush(this, accent ?? (Active || hovered ? "Ink" : "Muted"));
        label.FontWeight = Active ? FontWeights.Bold : FontWeights.Normal;
        timer.Foreground = Palette.Brush(this, Running ? "Red" : "Blue");
        frame.Background = Active ? Palette.Brush(this, "Card") : null;
        frame.BorderBrush = Active ? Palette.Brush(this, "Line") : null;
        shadowShape.Fill = Active ? Palette.Brush(this, "Card") : null;
        shadowHost.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        underline.Fill = Palette.Brush(this, accent ?? "Red");
        underline.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
        lift.Y = Active ? -2 : 0;
        runningDot.Fill = Palette.Brush(this, "Red");
        if (Running && runningDot.Visibility == Visibility.Collapsed)
        {
            runningDot.Visibility = Visibility.Visible;
            pulse.Begin();
        }
        else if (!Running && runningDot.Visibility == Visibility.Visible)
        {
            pulse.Stop();
            runningDot.Visibility = Visibility.Collapsed;
        }
    }
}

/// <summary>A row in the sidebar library.</summary>
public sealed class LibraryRow : Grid
{
    private readonly Border frame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly Rectangle marker = new() { Width = 2, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, LineHeight = 17.55, TextWrapping = TextWrapping.Wrap };
    private readonly TranslateTransform nudge = new();
    private readonly string colorKey;
    private readonly bool clickable;
    private bool hovered;
    private bool current;

    public string? VideoId { get; }
    public event EventHandler? Chosen;

    public bool Current
    {
        get => current;
        set { current = value; Paint(); }
    }

    public LibraryRow(string? videoId, string text, string detail, string colorKey, bool clickable)
    {
        VideoId = videoId;
        this.colorKey = colorKey;
        this.clickable = clickable;
        Margin = new Thickness(0, 3, 0, 3);
        RenderTransform = nudge;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        title.Text = text;
        var small = new TextBlock { Text = detail, FontSize = 11, FontWeight = FontWeights.SemiBold, LineHeight = 14.85, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, Tag = "detail" };
        var stack = new StackPanel { Padding = new Thickness(16, 11, 12, 10) };
        stack.Children.Add(title);
        stack.Children.Add(small);
        Children.Add(frame);
        Children.Add(stack);
        Children.Add(marker);
        if (clickable)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            Tapped += (_, _) => Chosen?.Invoke(this, EventArgs.Empty);
            PointerEntered += (_, _) => { hovered = true; Paint(); };
            PointerExited += (_, _) => { hovered = false; Paint(); };
        }
        void PaintAll() { small.Foreground = Palette.Brush(this, "Muted"); Paint(); }
        Loaded += (_, _) => PaintAll();
        ActualThemeChanged += (_, _) => PaintAll();
    }

    private void Paint()
    {
        title.Foreground = Palette.Brush(this, colorKey);
        marker.Fill = Palette.Brush(this, "Red");
        marker.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
        frame.Background = current ? Palette.Brush(this, "Card") : hovered ? new SolidColorBrush(Palette.Parse("#9EFFFDF8")) : null;
        frame.BorderBrush = current ? new SolidColorBrush(Palette.Parse("#1A1D2143")) : null;
        nudge.X = hovered && clickable ? 2 : 0;
    }
}
