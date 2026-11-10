// Mimic by soybean_alien
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Mimic.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Mimic;

public sealed partial class MainWindow : Window
{
    private const string ReleaseUrl = "https://github.com/soybean-alien/Mimic/releases";
    private static readonly HashSet<string> VideoExtensions = ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "mpg", "mpeg", "3gp", "ts"];
    private static readonly HashSet<string> ImageExtensions = ["png", "jpg", "jpeg", "webp", "gif"];

    private readonly List<PaneView> panes = [];
    private int active;

    internal MimicBackend Backend { get; }
    public IntPtr Handle { get; }
    /// <summary>Jobs that have been submitted but are not in the library yet, by tab.</summary>
    public Dictionary<int, (string Title, string? Id)> Pending { get; } = [];
    public double ViewportHeight => Root.ActualHeight;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public MainWindow()
    {
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(Handle) / 96.0;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(800 * scale)));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(500 * scale);
        }

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "";
        Backend = new MimicBackend(AppContext.BaseDirectory, version);
        Backend.Progress += (slot, message) => DispatcherQueue.TryEnqueue(() => panes[slot].SetStatus(message));
        Backend.CaptureStarted += slot => DispatcherQueue.TryEnqueue(() => panes[slot].ShowFinish());
        Closed += (_, _) => Backend.Dispose();

        for (var slot = 0; slot < 2; slot++)
        {
            var pane = new PaneView(this, slot);
            var index = slot;
            pane.Tab.Chosen += (_, _) => Activate(index);
            panes.Add(pane);
            Tabs.Children.Add(pane.Tab);
            PanesHost.Children.Add(pane);
        }
        Activate(0);

        DropVeil.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        Root.SizeChanged += (_, e) => ApplyViewport(e.NewSize.Width, e.NewSize.Height);
        Root.KeyDown += OnRootKeyDown;
        Root.Loaded += (_, _) =>
        {
            // The stylesheet's two spread shadows around the dot, drawn as one soft halo.
            var halo = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 37, Height = 37, IsHitTestVisible = false };
            Canvas.SetLeft(halo, -14);
            Canvas.SetTop(halo, -14);
            ModelDotGlowHost.Children.Add(halo);
            void PaintHalo()
            {
                var glow = Palette.Color(Root, "ModelDotGlow");
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop { Color = glow, Offset = 0 });
                brush.GradientStops.Add(new GradientStop { Color = glow, Offset = 0.24 });
                brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(110, glow.R, glow.G, glow.B), Offset = 0.42 });
                brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, glow.R, glow.G, glow.B), Offset = 0.85 });
                halo.Fill = brush;
            }
            PaintHalo();
            Root.ActualThemeChanged += (_, _) => PaintHalo();
            // The dot is still on purpose: an endless pulse made the window redraw every frame, which cost CPU
            // for as long as Mimic was open.
            ThinScroll.Attach(LibraryScroll);
            ThinScroll.Attach(VideoScroll);
            // The window can open on a monitor with a different scale than the one it was sized for.
            var shown = Root.XamlRoot.RasterizationScale;
            if (Math.Abs(shown - scale) > 0.01)
            {
                AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1180 * shown), (int)(800 * shown)));
                if (AppWindow.Presenter is OverlappedPresenter overlapped)
                {
                    overlapped.PreferredMinimumWidth = (int)(720 * shown);
                    overlapped.PreferredMinimumHeight = (int)(500 * shown);
                }
            }
            ApplyViewport(Root.ActualWidth, Root.ActualHeight);
            Start();
        };
    }

    private PaneView ActivePane => panes[active];

    private void Start()
    {
        VersionText.Text = Backend.Version.Length > 0 ? $"Version {Backend.Version}" : "Version unavailable";
        try
        {
            RefreshLibrary();
            var folder = Path.GetDirectoryName(Backend.GetLibraryPath()) ?? "";
            LibraryLocationText.Text = $"Library folder: {folder}";
            ToolTipService.SetToolTip(LibraryLocation, $"Open local library folder: {folder}");
            ModelButtonText.Text = ModelLabel(Backend.GetModel());
            var missing = Backend.MissingTools();
            if (missing.Count > 0)
            {
                panes[0].SetStatus($"Missing tools: {string.Join(", ", missing)}.", true);
                ShowSetup();
            }
        }
        catch (Exception error)
        {
            panes[0].SetStatus(error.Message, true);
        }
    }

    // ---- Window-size rules from the stylesheet's media queries ----

    private void ApplyViewport(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var narrow = width <= 980;
        var compact = height <= 720;
        SidebarColumn.Width = new GridLength(narrow ? 230 : 286);
        Sidebar.Padding = new Thickness(narrow ? 14 : 20, compact ? 18 : 28, narrow ? 14 : 20, 18);
        Brand.Margin = new Thickness(5, 2, 5, compact ? 17 : 27);
        Main.Margin = new Thickness(narrow ? 24 : 42, 0, narrow ? 24 : 42, 28);
        foreach (var pane in panes) pane.ApplyViewport(width, height);
        ModelDialog.Width = SetupDialog.Width = Math.Min(540, width * 0.9);
        ModelDialog.MaxHeight = height * 0.88;
        var videoWidth = Math.Min(1120, width * 0.94);
        VideoDialog.Width = videoWidth;
        VideoDialog.MaxHeight = height * 0.88;
        VideoStage.Height = Math.Max(240, (videoWidth - 52) * 9 / 16);
    }

    // ---- Tabs and library ----

    private void Activate(int slot)
    {
        active = slot;
        for (var index = 0; index < panes.Count; index++)
        {
            panes[index].Visibility = index == slot ? Visibility.Visible : Visibility.Collapsed;
            panes[index].Tab.SetState(active: index == slot);
        }
        HighlightCurrent();
    }

    public void HighlightCurrent()
    {
        var currentId = ActivePane.Current?.Video.Id;
        foreach (var row in LibraryList.Children.OfType<LibraryRow>())
        {
            row.Current = row.VideoId != null && row.VideoId == currentId;
        }
    }

    public void HideEverywhere(string id)
    {
        foreach (var pane in panes)
        {
            if (pane.Current?.Video.Id == id) pane.Hide();
        }
    }

    public void RefreshLibrary()
    {
        try
        {
            var videos = Backend.ListVideos();
            var pending = Pending.Values.ToList();
            LibraryList.Children.Clear();
            if (videos.Count == 0 && pending.Count == 0)
            {
                LibraryList.Children.Add(new Border
                {
                    Margin = new Thickness(5, 4, 5, 4), Padding = new Thickness(12, 13, 12, 13), CornerRadius = new CornerRadius(8),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InkLine20Brush"],
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "Transcripts you make appear here.", FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = Palette.Brush(Root, "Muted"),
                    },
                });
                return;
            }
            foreach (var project in pending.Where(project => project.Id == null))
            {
                LibraryList.Children.Add(new LibraryRow(null, project.Title, "In progress…", "Cyan", false));
            }
            foreach (var video in videos)
            {
                var added = pending.Any(project => project.Id == video.Id);
                var row = new LibraryRow(video.Id, video.Title, PaneView.LibraryDetail(video), added ? "Cyan" : "Green", true);
                row.Chosen += (_, _) => _ = ActivePane.Open(video.Id);
                LibraryList.Children.Add(row);
            }
            HighlightCurrent();
        }
        catch (Exception error)
        {
            ActivePane.SetStatus(error.Message, true);
        }
    }

    // ---- Sidebar and footer buttons ----

    private void OnLibraryLocation(object sender, RoutedEventArgs e) => Try(Backend.OpenLibraryFolder);

    /// <summary>Lets the user choose where the library is kept, then restarts Mimic on it.</summary>
    private async void OnChangeLibrary(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
            picker.FileTypeFilter.Add("*");
            var folder = (await picker.PickSingleFolderAsync())?.Path;
            if (folder is null || string.Equals(folder, Path.GetDirectoryName(Backend.GetLibraryPath()), StringComparison.OrdinalIgnoreCase)) return;
            Backend.SetLibraryFolder(folder);
            if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe)?.Dispose();
            Application.Current.Exit();
        }
        catch (Exception error)
        {
            ActivePane.SetStatus(error.Message, true);
        }
    }
    private void OnStoredFiles(object sender, RoutedEventArgs e) => Try(Backend.OpenStoredFiles);

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) { ActivePane.SetStatus(error.Message, true); }
    }

    private void OnReleases(object sender, RoutedEventArgs e)
    {
        try { Backend.OpenLink(ReleaseUrl); }
        catch (Exception error) { ActivePane.SetStatus($"Could not open GitHub releases: {error.Message}", true); }
    }

    private async void OnUpdateYtDlp(object sender, RoutedEventArgs e)
    {
        var pane = ActivePane;
        UpdateButton.IsEnabled = false;
        pane.SetStatus("Checking for a yt-dlp update…");
        try
        {
            pane.SetStatus(await Backend.UpdateYtDlpAsync());
        }
        catch (Exception error)
        {
            pane.SetStatus(error.Message, true);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    // ---- Pickers ----

    public async Task<string?> PickImage()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        foreach (var extension in ImageExtensions) picker.FileTypeFilter.Add("." + extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSavePath(string suggestedName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        picker.FileTypeChoices.Add($"{extension.ToUpperInvariant()} file", ["." + extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    /// <summary>Gets out of the way, lets the user point at a video with the crosshair, then comes back.</summary>
    public async Task<Target?> PickEmbedded()
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.Minimize();
        try
        {
            return await Backend.PickEmbeddedAsync();
        }
        finally
        {
            presenter?.Restore();
            Native.Focus(Handle);
        }
    }

    // ---- Drag and drop ----

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        DropVeil.Opacity = 1;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropVeil.Opacity = 0;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropVeil.Opacity = 0;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        string? path;
        try
        {
            path = (await e.DataView.GetStorageItemsAsync()).FirstOrDefault()?.Path;
        }
        finally
        {
            deferral.Complete();
        }
        if (string.IsNullOrEmpty(path)) return;
        var extension = path.Split('\\', '/', '.').Last().ToLowerInvariant();
        var pane = ActivePane;
        if (pane.Running)
        {
            pane.SetStatus("This tab is already running a job. Choose the other tab or wait for it to finish.", true);
        }
        else if (ImageExtensions.Contains(extension))
        {
            await pane.DescribeImage(path);
        }
        else if (VideoExtensions.Contains(extension))
        {
            if (await EnsureTools()) await pane.TranscribeFile(path);
        }
        else
        {
            pane.SetStatus("Drop a video or image file (MP4, MOV, MKV, WebM, PNG, JPG, or WebP).", true);
        }
    }

    // ---- Dialog plumbing ----

    private void ShowDialog(FrameworkElement dialog)
    {
        ModelDialog.Visibility = VideoDialog.Visibility = SetupDialog.Visibility = Visibility.Collapsed;
        dialog.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
    }

    private void CloseDialogs()
    {
        Overlay.Visibility = Visibility.Collapsed;
        ModelDialog.Visibility = VideoDialog.Visibility = SetupDialog.Visibility = Visibility.Collapsed;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || Overlay.Visibility != Visibility.Visible) return;
        if (VideoDialog.Visibility == Visibility.Visible) CloseVideoPlayer();
        else if (SetupDialog.Visibility == Visibility.Visible && downloading) return;
        CloseDialogs();
        e.Handled = true;
    }
}
