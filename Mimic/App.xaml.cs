// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using Microsoft.UI.Xaml;

namespace Mimic;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        // The in-app player is a WebView2. It keeps its profile in its own folder (two apps cannot
        // share one), and may start playing without a click inside the page.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "io.github.soybean-alien.mimic", "WinUI"));
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
    }

    /// <summary>Leaves the reason for a crash where it can be found.</summary>
    internal static void LogCrash(Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "io.github.soybean-alien.mimic");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{error}\r\n");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new MainWindow();
            Window.Activate();
        }
        catch (Exception error)
        {
            LogCrash(error);
            throw;
        }
    }
}
