// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.Web.WebView2.Core;
using Mimic.Core;

namespace Mimic;

public sealed partial class MainWindow
{
    // ---- Model dialog ----

    private string modelKind = ModelChoice.DefaultKind;
    private bool fillingModelForm;

    private IEnumerable<ChoiceRadio> KindRadios => [KindDefault, KindClaude, KindOpenAi, KindServer];

    private static string? TagOf(object? item) => (item as ComboBoxItem)?.Tag as string;

    private string ModelLabel(ModelChoice choice)
    {
        if (choice.Kind == ModelChoice.ClaudeKind)
        {
            var option = ClaudeModel.Items.OfType<ComboBoxItem>().FirstOrDefault(item => TagOf(item) == choice.Model);
            return $"Model: {option?.Content as string ?? choice.Model}";
        }
        return choice.Model.Length > 0 ? $"Model: {choice.Model}" : "Model: Default";
    }

    // The two model-name boxes suggest common model names.
    private static void SuggestModelNames(AutoSuggestBox box)
    {
        var typed = box.Text.Trim();
        box.ItemsSource = ((string)box.Tag).Split('|')
            .Where(name => name != typed && name.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnModelNameChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) SuggestModelNames(sender);
    }

    private void OnModelNameFocus(object sender, RoutedEventArgs e)
    {
        var box = (AutoSuggestBox)sender;
        SuggestModelNames(box);
        box.IsSuggestionListOpen = true;
    }

    private void SyncModelFields()
    {
        foreach (var radio in KindRadios) radio.IsChecked = radio.Kind == modelKind;
        void Enable(Control control, bool on)
        {
            // Dimmed and unreachable rather than disabled, so the field keeps the stylesheet's look (opacity .55).
            control.IsHitTestVisible = on;
            control.IsTabStop = on;
            control.Opacity = on ? 1 : 0.55;
        }
        Enable(ClaudeModel, modelKind == ModelChoice.ClaudeKind);
        Enable(OpenAiModel, modelKind == ModelChoice.OpenAIKind);
        foreach (var control in new Control[] { ServerPreset, ServerAddress, ServerModel, ServerKey })
        {
            Enable(control, modelKind == ModelChoice.ServerKind);
        }
    }

    private void OnKindChosen(object? sender, EventArgs e)
    {
        modelKind = ((ChoiceRadio)sender!).Kind;
        SyncModelFields();
    }

    private void ShowOpenAiSignIn(string status, bool signedIn)
    {
        OpenAiStatus.Text = status;
        OpenAiSignIn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        OpenAiSignIn.IsEnabled = true;
    }

    private async void RefreshOpenAiSignIn()
    {
        try
        {
            ShowOpenAiSignIn(await Backend.OpenAIStatusAsync(), true);
        }
        catch
        {
            ShowOpenAiSignIn("Not signed in", false);
        }
    }

    private async void OnOpenAiSignIn(object sender, RoutedEventArgs e)
    {
        OpenAiSignIn.IsEnabled = false;
        OpenAiStatus.Text = "Finish signing in in your browser.";
        try
        {
            ShowOpenAiSignIn(await Backend.OpenAISignInAsync(), true);
        }
        catch (Exception error)
        {
            ShowOpenAiSignIn(error.Message, false);
        }
    }

    private void OnModel(object sender, RoutedEventArgs e)
    {
        try
        {
            var choice = Backend.GetModel();
            fillingModelForm = true;
            modelKind = choice.Kind;
            if (ClaudeModel.SelectedIndex < 0) ClaudeModel.SelectedIndex = 0;
            if (ServerPreset.SelectedIndex < 0) ServerPreset.SelectedIndex = 0;
            if (choice.Kind == ModelChoice.ClaudeKind)
            {
                ClaudeModel.SelectedItem = ClaudeModel.Items.OfType<ComboBoxItem>().FirstOrDefault(item => TagOf(item) == choice.Model)
                    ?? ClaudeModel.SelectedItem;
            }
            if (choice.Kind == ModelChoice.OpenAIKind) OpenAiModel.Text = choice.Model;
            if (choice.Kind == ModelChoice.ServerKind)
            {
                ServerPreset.SelectedItem = ServerPreset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => TagOf(item) == choice.Address)
                    ?? ServerPreset.Items.OfType<ComboBoxItem>().Last();
                ServerAddress.Text = choice.Address;
                ServerModel.Text = choice.Model;
                ServerKey.Password = choice.Key;
            }
            fillingModelForm = false;
            ModelStatus.Text = "";
            ModelStatus.Visibility = Visibility.Collapsed;
            SyncModelFields();
            RefreshOpenAiSignIn();
            ShowDialog(ModelDialog);
        }
        catch (Exception error)
        {
            fillingModelForm = false;
            ActivePane.SetStatus(error.Message, true);
        }
    }

    private void OnServerPreset(object sender, SelectionChangedEventArgs e)
    {
        if (fillingModelForm || ServerPreset.SelectedItem is not ComboBoxItem preset) return;
        ServerAddress.Text = TagOf(preset) ?? "";
        if (preset.Content as string == "DeepSeek") ServerModel.Text = "deepseek-flash";
    }

    private void OnModelCancel(object sender, RoutedEventArgs e) => CloseDialogs();

    private void OnModelSave(object sender, RoutedEventArgs e)
    {
        var choice = modelKind switch
        {
            ModelChoice.ClaudeKind => ModelChoice.Claude(TagOf(ClaudeModel.SelectedItem) ?? ""),
            ModelChoice.OpenAIKind => ModelChoice.OpenAI((OpenAiModel.Text ?? "").Trim()),
            ModelChoice.ServerKind => ModelChoice.Server(ServerAddress.Text.Trim(), (ServerModel.Text ?? "").Trim(), ServerKey.Password.Trim()),
            _ => ModelChoice.Default,
        };
        try
        {
            Backend.SetModel(choice);
            ModelButtonText.Text = ModelLabel(choice);
            CloseDialogs();
        }
        catch (Exception error)
        {
            ModelStatus.Text = error.Message;
            ModelStatus.Visibility = Visibility.Visible;
        }
    }

    // ---- Video player ----

    private WebView2? player;

    private static string? YoutubeVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parsed.Host.EndsWith("youtu.be", StringComparison.Ordinal)) return segments.FirstOrDefault();
        if (parsed.Host.EndsWith("youtube.com", StringComparison.Ordinal) || parsed.Host.EndsWith("youtube-nocookie.com", StringComparison.Ordinal))
        {
            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts[0] == "v" && parts.Length == 2 && parts[1].Length > 0) return Uri.UnescapeDataString(parts[1]);
            }
            var marker = Array.FindIndex(segments, segment => segment is "embed" or "shorts" or "live");
            if (marker >= 0 && marker + 1 < segments.Length) return segments[marker + 1];
        }
        return null;
    }

    private void CloseVideoPlayer()
    {
        VideoStage.Child = null;
        player?.Close();
        player = null;
    }

    private void OnVideoClose(object sender, RoutedEventArgs e)
    {
        CloseVideoPlayer();
        CloseDialogs();
    }

    public async void OpenVideoPlayer(TranscriptView view, long startMs)
    {
        try
        {
            CloseVideoPlayer();
            var playback = Backend.GetVideoPlaybackSource(view.Video.Id);
            var url = view.Video.Url;
            var job = new Dictionary<string, object?>
            {
                ["title"] = view.Video.Title,
                ["start_ms"] = Math.Max(0, startMs),
                ["language"] = view.Video.Language,
                ["segments"] = view.Segments.Select(segment => new Dictionary<string, object>
                {
                    ["start_ms"] = segment.StartMs, ["end_ms"] = segment.EndMs, ["text"] = segment.Text,
                }).ToList(),
            };
            string? mediaFolder = null;
            var remote = !playback.Local && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal));
            if (remote && YoutubeVideoId(url) is { } youtubeId)
            {
                job["youtube_id"] = youtubeId;
            }
            else if (playback.Url.StartsWith("http", StringComparison.Ordinal))
            {
                job["src"] = playback.Url;
            }
            else
            {
                mediaFolder = Path.GetDirectoryName(playback.Url);
                job["src"] = "https://media.mimic.localhost/" + Uri.EscapeDataString(Path.GetFileName(playback.Url));
            }

            VideoDialogTitle.Text = view.Video.Title;
            var web = new WebView2 { DefaultBackgroundColor = Palette.Parse("#080910") };
            player = web;
            VideoStage.Child = web;
            ShowDialog(VideoDialog);
            await web.EnsureCoreWebView2Async();
            if (player != web) return;
            var core = web.CoreWebView2 ?? throw new InvalidOperationException("The video player could not start (Microsoft Edge WebView2 Runtime).");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("mimic.localhost", Path.Combine(AppContext.BaseDirectory, "Assets"),
                CoreWebView2HostResourceAccessKind.Allow);
            if (mediaFolder != null)
            {
                core.SetVirtualHostNameToFolderMapping("media.mimic.localhost", mediaFolder, CoreWebView2HostResourceAccessKind.Allow);
            }
            var json = JsonSerializer.Serialize(job);
            core.WebMessageReceived += (_, _) => core.PostWebMessageAsJson(json);
            core.Navigate("https://mimic.localhost/player.html");
        }
        catch (Exception error)
        {
            App.LogCrash(error);
            CloseVideoPlayer();
            CloseDialogs();
            ActivePane.SetStatus(error.Message, true);
        }
    }

    // ---- Downloads (new: the installer no longer bundles the tools or the Whisper model) ----

    private const string ModelTool = "Whisper model";
    private bool downloading;
    private CancellationTokenSource? downloadCancel;

    private static string Describe(string tool) => tool switch
    {
        "yt-dlp" => "yt-dlp: reads video pages and downloads their audio",
        "ffmpeg" => "FFmpeg: converts audio and video",
        "Whisper" => "Whisper engine: turns speech into text",
        _ => "Whisper model (optional download, 574 MB): needed before Mimic can transcribe",
    };

    /// <summary>True when everything a transcription needs is installed; otherwise offers the downloads.</summary>
    public Task<bool> EnsureTools()
    {
        if (Backend.MissingTools().Count == 0) return Task.FromResult(true);
        ShowSetup();
        return Task.FromResult(false);
    }

    private void ShowSetup()
    {
        RenderSetup();
        SetupStatus.Text = "";
        ShowDialog(SetupDialog);
    }

    private void RenderSetup()
    {
        var missing = Backend.MissingTools();
        var tools = missing.Where(tool => tool != ModelTool).ToList();
        SetupRows.Children.Clear();
        SetupIntro.Text = missing.Count == 0
            ? "Everything Mimic needs is installed."
            : "Two clicks set Mimic up: Download tools, then Download Whisper model. Mimic fetches the files, puts them in your Mimic folder, and never needs them again.";
        foreach (var tool in missing)
        {
            var row = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Root, "Ink") };
            row.Inlines.Add(new Run { Text = "•  " + Describe(tool) });
            // The exact file on the web, for anyone who would rather fetch it in the browser. The folder is shown with "Username" so the real Windows account name never appears on screen.
            var (url, then) = tool switch
            {
                "yt-dlp" => (ToolInstaller.YtDlpUrl, @" and save it in C:\Users\Username\Mimic\tools\yt-dlp"),
                "ffmpeg" => (ToolInstaller.FfmpegUrl, @", open the zip, and copy ffmpeg.exe and ffprobe.exe from its bin folder into C:\Users\Username\Mimic\tools\ffmpeg"),
                "Whisper" => (ToolInstaller.WhisperFallbackUrl, @", open the zip, and copy everything in its Release folder into C:\Users\Username\Mimic\tools\whisper\Release"),
                ModelTool => (ToolInstaller.ModelUrl, @" and save it in C:\Users\Username\Mimic\models"),
                _ => (null, null),
            };
            if (url is not null)
            {
                var link = new Hyperlink { NavigateUri = new Uri(url) };
                link.Inlines.Add(new Run { Text = "get the exact file in your browser" });
                row.Inlines.Add(new Run { Text = ".  Or ", Foreground = Palette.Brush(Root, "Muted") });
                row.Inlines.Add(link);
                row.Inlines.Add(new Run { Text = then, Foreground = Palette.Brush(Root, "Muted") });
            }
            SetupRows.Children.Add(row);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        if (tools.Count > 0)
        {
            buttons.Children.Add(DownloadButton("Download tools", tools, true));
        }
        if (missing.Contains(ModelTool))
        {
            buttons.Children.Add(DownloadButton("Download Whisper model", [ModelTool], tools.Count == 0));
        }
        if (buttons.Children.Count > 0) SetupRows.Children.Add(buttons);
    }

    private FlatButton DownloadButton(string text, List<string> tools, bool primary)
    {
        var button = new FlatButton
        {
            Content = text, Padding = new Thickness(17, 8, 17, 8), CornerRadius = new CornerRadius(5), FontSize = 14,
            BorderThickness = new Thickness(primary ? 0 : 1),
            BgKey = primary ? "Red" : null, FgKey = primary ? "#FFFFFF" : "Ink", BorderKey = primary ? null : "Line",
            HoverBgKey = primary ? "RedDeep" : null,
        };
        button.Click += (_, _) => Download(tools);
        return button;
    }

    private async void Download(List<string> tools)
    {
        if (downloading) return;
        downloading = true;
        downloadCancel = new CancellationTokenSource();
        foreach (var button in SetupRows.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<FlatButton>()))
        {
            button.IsEnabled = false;
        }
        SetupClose.Content = "Cancel";
        SetupProgress.Visibility = Visibility.Visible;
        SetupProgressFill.Width = 0;
        var progress = new Progress<(string Message, double? Fraction)>(update =>
        {
            SetupStatus.Foreground = Palette.Brush(Root, "Muted");
            SetupStatus.Text = update.Message;
            SetupProgressFill.Width = SetupProgress.ActualWidth * Math.Clamp(update.Fraction ?? 0, 0, 1);
        });
        try
        {
            using var installer = new ToolInstaller(Backend.Paths);
            foreach (var tool in tools) await installer.InstallAsync(tool, progress, downloadCancel.Token);
            SetupStatus.Foreground = Palette.Brush(Root, "Muted");
            SetupStatus.Text = "Downloaded.";
            var missing = Backend.MissingTools();
            ActivePane.SetStatus(missing.Count > 0 ? $"Missing tools: {string.Join(", ", missing)}." : "", missing.Count > 0);
        }
        catch (OperationCanceledException)
        {
            SetupStatus.Text = "Download cancelled.";
        }
        catch (Exception error)
        {
            SetupStatus.Foreground = Palette.Brush(Root, "Error");
            SetupStatus.Text = error.Message;
        }
        finally
        {
            downloading = false;
            downloadCancel = null;
            SetupClose.Content = "Close";
            SetupProgress.Visibility = Visibility.Collapsed;
            RenderSetup();
        }
    }

    private void OnSetupClose(object sender, RoutedEventArgs e)
    {
        if (downloading)
        {
            downloadCancel?.Cancel();
            return;
        }
        CloseDialogs();
    }
}
