// Mimic by soybean_alien
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Mimic.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Mimic;

/// <summary>One "Project" tab: the entry card, its job state, and the transcript or image it shows.</summary>
public sealed partial class PaneView : UserControl
{
    private static readonly Dictionary<string, string> Methods = new()
    {
        ["captions"] = "Written captions",
        ["auto-captions"] = "YouTube automatic captions",
        ["whisper"] = "Whisper",
        ["capture"] = "Whisper, from the window's sound",
        ["image"] = "Reverse prompt",
    };

    private readonly MainWindow window;
    private readonly int slot;
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer dots = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Storyboard sweep = new() { RepeatBehavior = RepeatBehavior.Forever };
    private readonly FlatButton playVideo, takeaways, copy, srt, txt, md, pdf, delete;
    private DateTime started;
    private int dotStep;
    private string activity = "Transcribing";
    private bool saveVideoCopy;
    private bool statusIsError;
    private bool editingTitle;
    private Action<string>? checkGlow;

    public TabHeader Tab { get; }
    internal TranscriptView? Current { get; private set; }
    public bool Running { get; private set; }

    private MimicBackend Backend => window.Backend;

    public PaneView(MainWindow window, int slot)
    {
        this.window = window;
        this.slot = slot;
        InitializeComponent();
        Tab = new TabHeader($"Project {slot + 1}");

        playVideo = ActionButton("Play video", (_, _) => { if (Current != null) OpenPlayer(Current, 0); });
        takeaways = ActionButton("Key takeaways", OnTakeaways);
        copy = ActionButton("Copy", OnCopy);
        srt = ActionButton("Save .srt", (_, _) => ExportAs("srt"));
        txt = ActionButton("Save .txt", (_, _) => ExportAs("txt"));
        md = ActionButton("Save .md", (_, _) => ExportAs("md"));
        pdf = ActionButton("Export PDF", OnPdf);
        delete = ActionButton("Delete", OnDelete);
        FlexWrapPanel.SetPushRight(delete, true);

        ticker.Tick += (_, _) => Tick();
        dots.Tick += (_, _) =>
        {
            dotStep = (dotStep + 1) % 4;
            SourceState.Text = activity + new[] { "", ".", ". .", ". . ." }[dotStep];
        };
        JobProgress.SizeChanged += (_, _) => StartSweep();
        Loaded += (_, _) =>
        {
            Glow.Attach(EntryShadowHost, EntryBg, "CardShadow", 25, 12);
            checkGlow = Glow.Attach(CheckGlowHost, CheckBoxShape, "#00000000", 8);
            ThinScroll.Attach(PassageScroll);
            PaintState();
        };
        ActualThemeChanged += (_, _) =>
        {
            PaintState();
            if (Current != null && !editingTitle) Show(Current);
        };
        TranscriptHeader.SizeChanged += (_, _) => FitTitle();
    }

    private FlatButton ActionButton(string text, RoutedEventHandler click)
    {
        var button = new FlatButton
        {
            Content = text, Padding = new Thickness(10, 6, 10, 6),
            BgKey = "Red", BorderKey = "Red", FgKey = "#1D2143", HoverBgKey = "RedDeep", HoverBorderKey = "RedDeep",
        };
        button.Click += click;
        Actions.Children.Add(button);
        return button;
    }

    // ---- Layout that the stylesheet tied to the window size ----

    public void ApplyViewport(double width, double height)
    {
        var compact = height <= 720;
        var intro = compact ? 34 : Math.Clamp(width * 0.033, 32, 47);
        IntroHeading.FontSize = intro;
        IntroHeading.LineHeight = intro * 1.03;
        Intro.Padding = new Thickness(2, 0, 2, compact ? 12 : 18);
        Entry.Padding = new Thickness(compact ? 14 : 19);
        var title = Math.Clamp(width * 0.028, 26, 38);
        Title.FontSize = title;
        Title.LineHeight = title * 1.12;
        TitleEditor.FontSize = Math.Clamp(width * 0.025, 24, 34);
        ApplyPanePadding(compact);
        FitTitle();
    }

    private bool compactHeight;

    private void ApplyPanePadding(bool compact)
    {
        compactHeight = compact;
        var showing = Transcript.Visibility == Visibility.Visible;
        PaneRoot.Padding = new Thickness(0, showing ? 15 : compact ? 16 : 25, 0, 0);
    }

    private void FitTitle()
    {
        var available = TranscriptHeader.ActualWidth - 4;
        if (available <= 0) return;
        Title.MaxWidth = Math.Max(120, Math.Min(900, available - TitleEdit.ActualWidth - 12));
        TitleEditor.Width = Math.Max(180, available - TitleSave.ActualWidth - TitleCancel.ActualWidth - 24);
    }

    // ---- Status, timer, running state ----

    public void SetStatus(string message, bool isError = false)
    {
        Status.Text = message;
        statusIsError = isError;
        Status.Foreground = Palette.Brush(this, isError ? "Error" : "Muted");
    }

    private static string StopwatchText(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

    private void Tick()
    {
        var text = StopwatchText((int)(DateTime.UtcNow - started).TotalSeconds);
        Stopwatch.Text = text;
        Tab.TimerText = text;
    }

    public void ShowFinish()
    {
        Finish.Visibility = Visibility.Visible;
        Finish.IsEnabled = true;
    }

    private void SetRunning(bool value, bool completed = false, string what = "Transcribing", bool showProgress = true)
    {
        Running = value;
        activity = what;
        SourceState.Text = value ? what : "Whisper handles every transcription";
        Go.IsEnabled = Url.IsEnabled = EmbeddedButton.IsEnabled = BrowseImage.IsEnabled = !value;
        Cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Cancel.IsEnabled = true;
        Finish.Visibility = Visibility.Collapsed;
        JobProgress.Visibility = value && showProgress ? Visibility.Visible : Visibility.Collapsed;
        if (value)
        {
            Tab.SetState(running: true, added: true, finished: false);
            started = DateTime.UtcNow;
            Tick();
            ticker.Start();
            dotStep = 0;
            dots.Start();
            StartSweep();
        }
        else
        {
            if (completed) Tab.SetState(running: false, added: true, finished: true);
            else Tab.SetState(running: false);
            ticker.Stop();
            dots.Stop();
            sweep.Stop();
            Tab.TimerText = "";
        }
        PaintState();
    }

    private void StartSweep()
    {
        sweep.Stop();
        if (JobProgress.Visibility != Visibility.Visible || JobProgress.ActualWidth <= 0) return;
        var track = JobProgress.ActualWidth;
        var bar = track * 0.34;
        JobProgressBar.Width = bar;
        JobProgress.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, track, 5) };
        sweep.Children.Clear();
        var move = new DoubleAnimation
        {
            From = -1.1 * bar, To = 3.2 * bar, Duration = TimeSpan.FromSeconds(1.35),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(move, JobProgressShift);
        Storyboard.SetTargetProperty(move, "X");
        sweep.Children.Add(move);
        sweep.Begin();
    }

    /// <summary>Colours that depend on the running state, the checkbox and the theme.</summary>
    private void PaintState()
    {
        var cream = new SolidColorBrush(Palette.Parse("#FFF8E9"));
        EntryBg.Fill = Palette.Brush(this, Running ? "Red" : "Card");
        EntryBg.Stroke = Running ? Palette.Brush(this, "Red") : new SolidColorBrush(Palette.Parse("#1C1D2143"));
        EntryLabel.Foreground = Running ? cream : Palette.Brush(this, "Ink");
        SourceState.Foreground = Running ? cream : Palette.Brush(this, "Muted");
        SourceState.FontWeight = Running ? FontWeights.Bold : FontWeights.Normal;
        EntryNote.Foreground = Palette.Brush(this, "Muted");
        Stopwatch.Foreground = Palette.Brush(this, Running ? "Red" : "Ink");
        Status.Foreground = Palette.Brush(this, statusIsError ? "Error" : "Muted");
        LocalCopyText.Foreground = Palette.Brush(this, saveVideoCopy ? "SecureText" : Running ? "Ink" : "Muted");
        CheckBoxShape.Fill = Palette.Brush(this, saveVideoCopy ? "SecureDot" : "PaperDeep");
        CheckBoxShape.Stroke = Palette.Brush(this, saveVideoCopy ? "SecureDot" : "Muted");
        CheckMark.Visibility = saveVideoCopy ? Visibility.Visible : Visibility.Collapsed;
        LocalCopyOption.Opacity = Running ? 0.7 : 1;
        checkGlow?.Invoke(saveVideoCopy ? "SecureDotGlow" : "#00000000");
    }

    private void OnLocalCopyTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Running) return;
        saveVideoCopy = !saveVideoCopy;
        PaintState();
    }

    // ---- Showing a transcript or an image ----

    private static string Duration(long seconds)
    {
        if (seconds <= 0) return "";
        long h = seconds / 3600, m = seconds % 3600 / 60;
        var s = (seconds % 60).ToString("00");
        return h > 0 ? $"{h}:{m:00}:{s}" : $"{m}:{s}";
    }

    public static string LibraryDetail(Video video) =>
        video.Method == "image"
            ? Methods["image"]
            : string.Join(" · ", new[] { video.Channel, Duration(video.DurationSeconds) }.Where(part => !string.IsNullOrEmpty(part)));

    private static string StatsDuration(long seconds)
    {
        var total = Math.Max(0, seconds);
        return $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}";
    }

    private void Show(TranscriptView view)
    {
        Current = view;
        editingTitle = false;
        var video = view.Video;
        var isImage = video.Method == "image";
        Title.Text = video.Title;
        Title.Visibility = TitleEdit.Visibility = Visibility.Visible;
        TitleEditor.Visibility = TitleSave.Visibility = TitleCancel.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(Tab, video.Title);
        if (!Running) Tab.SetState(added: true, finished: true);

        Meta.Inlines.Clear();
        void AddMeta(string label, string value)
        {
            if (Meta.Inlines.Count > 0) Meta.Inlines.Add(new Run { Text = "  ·  " });
            Meta.Inlines.Add(new Run { Text = label, Foreground = Palette.Brush(this, "Cyan") });
            Meta.Inlines.Add(new Run { Text = ": " });
            Meta.Inlines.Add(new Run { Text = value, Foreground = Palette.Brush(this, "Green") });
        }
        AddMeta("Source", !string.IsNullOrEmpty(video.Channel) ? video.Channel
            : video.Method == "capture" ? "Screen capture" : isImage ? "Image" : "Local video");
        if (!isImage)
        {
            AddMeta("Video length", StatsDuration(video.DurationSeconds));
            if (video.MediaHosts.Count > 0) AddMeta("Video hosted at", string.Join(", ", video.MediaHosts));
            var language = (video.Language ?? "").Trim();
            AddMeta("Language", language.Length > 0 ? language[..Math.Min(2, language.Length)].ToUpperInvariant() : "--");
        }
        AddMeta("Number of words", view.Words.ToString("N0"));
        if (!isImage)
        {
            var processing = Duration(video.TranscodeSeconds);
            AddMeta("Processing time", processing.Length > 0 ? processing : "—");
        }
        AddMeta("Model", Methods.TryGetValue(video.Method, out var method) ? method : video.Method);

        srt.Visibility = playVideo.Visibility = takeaways.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        takeaways.Content = string.IsNullOrEmpty(view.Takeaways) ? "Key takeaways" : "Refresh takeaways";
        Actions.InvalidateMeasure();

        PassageList.Children.Clear();
        if (isImage) ShowImage(view);
        else
        {
            if (!string.IsNullOrEmpty(view.Takeaways)) PassageList.Children.Add(TakeawayPanel(view.Takeaways));
            foreach (var passage in view.Passages) PassageList.Children.Add(PassageRow(view, passage));
        }

        Transcript.Visibility = Visibility.Visible;
        Intro.Visibility = Visibility.Collapsed;
        ApplyPanePadding(compactHeight);
        window.HighlightCurrent();
    }

    public void Hide()
    {
        Current = null;
        Transcript.Visibility = Visibility.Collapsed;
        Intro.Visibility = Visibility.Visible;
        ApplyPanePadding(compactHeight);
    }

    private TextBlock Text(string text, double size, string colorKey, double lineHeight = 0, bool serif = false, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Foreground = Palette.Brush(this, colorKey),
        };
        if (lineHeight > 0) block.LineHeight = lineHeight;
        if (serif) block.FontFamily = (FontFamily)Application.Current.Resources["Serif"];
        if (bold) block.FontWeight = FontWeights.Bold;
        return block;
    }

    private UIElement PassageRow(TranscriptView view, Passage passage)
    {
        var row = new Grid { ColumnSpacing = 15, Padding = new Thickness(10, 13, 10, 13) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var clock = new TextBlock { Text = passage.Clock, FontSize = 12, FontWeight = FontWeights.SemiBold };
        var time = new FlatButton
        {
            Content = clock, Padding = new Thickness(0, 3, 0, 3), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            FgKey = "Blue", HoverFgKey = "Red",
        };
        time.PointerEntered += (_, _) => clock.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        time.PointerExited += (_, _) => clock.TextDecorations = Windows.UI.Text.TextDecorations.None;
        ToolTipService.SetToolTip(time, "Play this section in Mimic");
        time.Click += (_, _) => OpenPlayer(view, passage.StartMs);
        row.Children.Add(time);

        var body = new StackPanel();
        if (!string.IsNullOrEmpty(passage.Title))
        {
            var heading = Text(passage.Title, 17, "RedDeep", 22.1, serif: true, bold: true);
            heading.CharacterSpacing = 10;
            heading.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(heading);
        }
        body.Children.Add(Text(passage.Text, 14, "BodyCopy", 21.7));
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        return new Border { BorderBrush = Palette.Brush(this, "Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
    }

    private UIElement Bullet(string text, double bottom, string colorKey)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, bottom) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = Text("•", 17, colorKey, 21.7);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var body = Text(text, 14, colorKey, 21.7);
        Grid.SetColumn(body, 1);
        row.Children.Add(mark);
        row.Children.Add(body);
        return row;
    }

    private UIElement TakeawayPanel(string takeawayText)
    {
        var content = new StackPanel();
        var heading = Text("Key takeaways", 18, "Cyan", 23.4, serif: true, bold: true);
        heading.Margin = new Thickness(0, 0, 0, 7);
        content.Children.Add(heading);
        var first = true;
        foreach (var line in takeawayText.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;
            if (Regex.IsMatch(text, @"^#{1,6}\s+"))
            {
                var label = Regex.Replace(text, @"^#{1,6}\s+", "");
                if (Regex.IsMatch(label, "^key takeaways$", RegexOptions.IgnoreCase)) continue;
                var topic = Text(label, 15, "Red", 20.25, serif: true, bold: true);
                topic.Margin = new Thickness(0, first ? 0 : 12, 0, 5);
                content.Children.Add(topic);
            }
            else if (Regex.IsMatch(text, @"^[-*]\s+"))
            {
                content.Children.Add(Bullet(Regex.Replace(text, @"^[-*]\s+", ""), 6, "BodyCopy"));
            }
            else
            {
                var paragraph = Text(text, 14, "BodyCopy", 21.7);
                paragraph.Margin = new Thickness(0, 0, 0, 7);
                content.Children.Add(paragraph);
            }
            first = false;
        }
        return new Border
        {
            Margin = new Thickness(0, 10, 0, 14), Padding = new Thickness(18, 15, 18, 15), CornerRadius = new CornerRadius(8),
            BorderBrush = Palette.Brush(this, "Cyan"), BorderThickness = new Thickness(4, 1, 1, 1),
            Background = Palette.Brush(this, "Card"), Child = content,
        };
    }

    private static string CertaintyTier(long score) => score switch
    {
        >= 90 => "Almost certainly AI-generated",
        >= 70 => "Probably AI-generated",
        >= 50 => "Leaning AI-generated",
        >= 30 => "Leaning not AI-generated",
        >= 10 => "Probably not AI-generated",
        _ => "Almost certainly not AI-generated",
    };

    private void ShowImage(TranscriptView view)
    {
        var picture = new Image { Stretch = Stretch.Uniform, Width = 0, Height = 0 };
        var top = new FlexWrapPanel { Gap = 22, RowAlignment = VerticalAlignment.Top };
        // max-width: min(520px, 55%), max-height: 42vh, never larger than the picture itself.
        void Fit()
        {
            if (picture.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap || top.ActualWidth <= 0) return;
            var maxWidth = Math.Min(520, Math.Max(120, top.ActualWidth * 0.55));
            var maxHeight = Math.Max(160, window.ViewportHeight * 0.42);
            var scale = Math.Min(1, Math.Min(maxWidth / bitmap.PixelWidth, maxHeight / bitmap.PixelHeight));
            picture.Width = Math.Round(bitmap.PixelWidth * scale);
            picture.Height = Math.Round(bitmap.PixelHeight * scale);
        }
        _ = LoadPicture(picture, view.Video.Id, Fit);
        top.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = picture });
        top.SizeChanged += (_, _) => Fit();

        if (view.Score is { } score)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text($"Score: {score.Score}%", 32, "RedDeep", 35.2, serif: true));
            var certainty = Text(CertaintyTier(score.Score), 12, "Muted");
            certainty.Margin = new Thickness(0, 5, 0, 14);
            panel.Children.Add(certainty);
            var identification = Text($"{Regex.Replace(score.Identification, @"\.$", "")}, based on:", 14, "Ink", 21.7);
            identification.FontWeight = FontWeights.SemiBold;
            identification.Margin = new Thickness(0, 0, 0, 5);
            panel.Children.Add(identification);
            foreach (var reason in score.Reasons) panel.Children.Add(Bullet(reason, 5, "Ink"));
            FlexWrapPanel.SetGrow(panel, 1);
            FlexWrapPanel.SetBasis(panel, 240);
            top.Children.Add(panel);
        }

        PassageList.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(15), CornerRadius = new CornerRadius(8),
            BorderBrush = Palette.Brush(this, "Line"), BorderThickness = new Thickness(1),
            Background = Palette.Brush(this, "Card"), Child = top,
        });

        var prompt = Text(string.Join("\n\n", view.Passages.Select(p => p.Text)), 14, "Ink", 22.4);
        PassageList.Children.Add(new Border
        {
            MaxWidth = 866, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8),
            BorderBrush = Palette.Brush(this, "Cyan"), BorderThickness = new Thickness(3, 0, 0, 0),
            Child = new Border
            {
                Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(0, 8, 8, 0),
                BorderBrush = Palette.Brush(this, "Line"), BorderThickness = new Thickness(0, 1, 1, 1),
                Background = Palette.Brush(this, "PaperDeep"), Child = prompt,
            },
        });
    }

    private async Task LoadPicture(Image picture, string id, Action loaded)
    {
        try
        {
            // Read into memory so the stored picture is never held open (it must stay deletable).
            var bytes = await File.ReadAllBytesAsync(Backend.ImagePath(id));
            // Not disposed here: the picture decodes from this memory stream when it is drawn.
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            picture.Source = bitmap;
            loaded();
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    // ---- Jobs ----

    /// <summary>Runs one job in this tab, with its timer, and shows what comes back.</summary>
    public async Task<bool> Run(Func<Task<TranscriptView>> start, string doneMessage, string? submittedTitle = null,
        bool chime = true, string what = "Transcribing", bool showProgress = true)
    {
        SetRunning(true, false, what, showProgress);
        var completed = false;
        if (submittedTitle != null)
        {
            window.Pending[slot] = (submittedTitle, null);
            window.RefreshLibrary();
        }
        var began = DateTime.UtcNow;
        SetStatus("Starting…");
        try
        {
            var view = await start();
            if (submittedTitle != null) window.Pending[slot] = (view.Video.Title, view.Video.Id);
            Show(view);
            if (chime && view.Video.Method != "image") Native.PlayChime();
            window.RefreshLibrary();
            var seconds = (long)Math.Round((DateTime.UtcNow - began).TotalSeconds);
            var took = Duration(seconds);
            SetStatus(view.Notice ?? $"{doneMessage} in {(took.Length > 0 ? took : "0:00")}.", view.Notice != null);
            completed = true;
            return true;
        }
        catch (Exception error)
        {
            SetStatus(error.Message, error.Message != "Cancelled.");
            return false;
        }
        finally
        {
            if (submittedTitle != null) window.Pending.Remove(slot);
            SetRunning(false, completed);
            if (submittedTitle != null) window.RefreshLibrary();
        }
    }

    /// <summary>Opens a library entry, writing section titles first if it has none yet.</summary>
    public async Task Open(string id)
    {
        try
        {
            var view = Backend.GetTranscript(id);
            Show(view);
            SetStatus("");
            if (!Running) Stopwatch.Text = StopwatchText(0);
            var untitled = view.Video.Method != "image" && view.Passages.All(p => string.IsNullOrEmpty(p.Title));
            if (untitled && !Running) await Run(() => Backend.TitleVideoAsync(slot, id), "Titled");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    public static string PendingTitleFromPath(string path, string fallback)
    {
        var title = Regex.Replace(path.Split('\\', '/').Last(), @"\.[^.]+$", "").Trim();
        var suffixStart = Math.Max(title.LastIndexOf('('), title.LastIndexOf('['));
        if (suffixStart >= 0)
        {
            var suffix = title[suffixStart..];
            var closed = (suffix.StartsWith('(') && suffix.EndsWith(')')) || (suffix.StartsWith('[') && suffix.EndsWith(']'));
            if (closed && Regex.IsMatch(suffix, @"(?:\b\d{3,4}p\b|\bfps\b|\bav1\b|\baac\b|\bopus\b|\b(?:h\.?26[45]|hevc|vp9)\b|\bkbit\b|\bkbps\b|\bstereo\b|\bmono\b)", RegexOptions.IgnoreCase))
            {
                title = Regex.Replace(title[..suffixStart], @"[\s_.-]+$", "");
            }
        }
        return title.Length > 0 ? title : fallback;
    }

    public Task DescribeImage(string path) =>
        Run(() => Backend.DescribeImageAsync(slot, path), "Done", PendingTitleFromPath(path, "New image"), true, "Describing image", false);

    public Task TranscribeFile(string path) =>
        Run(() => Backend.TranscribeFileAsync(slot, path), "Done", PendingTitleFromPath(path, "New video"));

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        OnGo(sender, e);
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        var url = Url.Text.Trim();
        if (url.Length == 0 || Running) return;
        if (!await window.EnsureTools()) return;
        var save = saveVideoCopy;
        var done = await Run(() => Backend.TranscribeVideoAsync(slot, url, save), "Done", "New video");
        if (done) Url.Text = "";
    }

    private async void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await window.PickImage();
            if (path != null) await DescribeImage(path);
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private async void OnEmbedded(object sender, RoutedEventArgs e)
    {
        if (!await window.EnsureTools()) return;
        EmbeddedButton.IsEnabled = false;
        try
        {
            var target = await window.PickEmbedded();
            if (target != null)
            {
                var save = saveVideoCopy;
                await Run(() => Backend.TranscribeEmbeddedAsync(slot, target), "Done", "Screen capture");
            }
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
        finally
        {
            EmbeddedButton.IsEnabled = !Running;
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        Finish.IsEnabled = false;
        Backend.FinishCapture(slot);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Cancel.IsEnabled = false;
        SetStatus("Cancelling…");
        Backend.CancelJob(slot);
    }

    // ---- Transcript actions ----

    private void OpenPlayer(TranscriptView view, long startMs)
    {
        try { window.OpenVideoPlayer(view, startMs); }
        catch (Exception error) { SetStatus(error.Message, true); }
    }

    private void OnTakeaways(object sender, RoutedEventArgs e)
    {
        if (Current == null || Running) return;
        var id = Current.Video.Id;
        _ = Run(() => Backend.SummarizeVideoAsync(slot, id), "Key takeaways ready", null, false, "Summarizing");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var text = Current.Video.Method == "image"
            ? string.Join("\n\n", Current.Passages.Select(p => p.Text))
            : string.Join("\n\n", Current.Passages.Select(p =>
                string.IsNullOrEmpty(p.Title) ? $"[{p.Clock}] {p.Text}" : $"[{p.Clock}] {p.Title}\n{p.Text}"));
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetStatus("Copied.");
        }
        catch (Exception error)
        {
            SetStatus($"Could not copy: {error.Message}", true);
        }
    }

    private async void ExportAs(string format)
    {
        if (Current == null) return;
        var id = Current.Video.Id;
        try
        {
            string? saved;
            if (format == "md")
            {
                try
                {
                    saved = await Backend.ExportTranscriptAsync(id, "md", false, null);
                }
                catch (Exception error) when (error.Message.Contains("OBSIDIAN_CONFIRMATION_REQUIRED"))
                {
                    var approved = Native.Ask(window.Handle,
                        "Mimic found an existing .obsidian folder at the selected destination. This looks like an Obsidian vault. Save this note there anyway?",
                        "Confirm vault write");
                    if (!approved)
                    {
                        SetStatus("Markdown save canceled.");
                        return;
                    }
                    saved = await Backend.ExportTranscriptAsync(id, "md", true, null);
                }
            }
            else
            {
                var path = await window.PickSavePath(Backend.SuggestedFileName(id, format), format);
                if (path == null) return;
                await Backend.ExportTranscriptAsync(id, format, false, path);
                saved = path;
            }
            if (saved != null) SetStatus(format == "md" ? $"Saved to the Mimic Obsidian project folder: {saved}" : $"Saved the .{format} file.");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private async void OnPdf(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var id = Current.Video.Id;
        pdf.IsEnabled = false;
        SetStatus("Printing to PDF…");
        try
        {
            var path = await window.PickSavePath(Backend.SuggestedFileName(id, "pdf"), "pdf");
            if (path == null)
            {
                SetStatus("");
                return;
            }
            await Backend.ExportPdfAsync(id, path);
            SetStatus("Saved the PDF with Microsoft Print to PDF.");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
        finally
        {
            pdf.IsEnabled = true;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var (id, title) = (Current.Video.Id, Current.Video.Title);
        try
        {
            if (!Native.Ask(window.Handle, $"Delete \"{title}\" from the library?", "Mimic")) return;
            Backend.DeleteVideo(id);
            window.HideEverywhere(id);
            window.RefreshLibrary();
            SetStatus("Deleted.");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    // ---- Title editing ----

    private void OnTitleEdit(object sender, RoutedEventArgs e)
    {
        if (Current == null || Running) return;
        editingTitle = true;
        TitleEditor.Text = Current.Video.Title;
        Title.Visibility = TitleEdit.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = TitleSave.Visibility = TitleCancel.Visibility = Visibility.Visible;
        TitleSave.IsEnabled = TitleCancel.IsEnabled = true;
        FitTitle();
        TitleEditor.Focus(FocusState.Programmatic);
        TitleEditor.SelectAll();
    }

    private void OnTitleCancel(object sender, RoutedEventArgs e)
    {
        if (Current != null) Show(Current);
    }

    private void OnTitleSave(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var current = Current;
        var title = TitleEditor.Text.Trim();
        if (title.Length == 0)
        {
            SetStatus("A title cannot be empty.", true);
            TitleEditor.Focus(FocusState.Programmatic);
            return;
        }
        TitleSave.IsEnabled = TitleCancel.IsEnabled = false;
        try
        {
            Show(Backend.UpdateVideoTitle(current.Video.Id, title));
            window.RefreshLibrary();
            SetStatus("Title saved.");
        }
        catch (Exception error)
        {
            SetStatus($"Title not saved: {error.Message}", true);
            Show(current);
        }
    }

    private void OnTitleEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OnTitleSave(sender, e);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            OnTitleCancel(sender, e);
        }
    }
}
