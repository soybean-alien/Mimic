// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using Mimic.Core;

namespace Mimic.Tests;

public class LibraryFolderTests
{
    [Fact]
    public void CopyToWritesALibraryThatOpens()
    {
        using var dir = new TempFolder();
        var copy = Path.Combine(dir.Path, "copy.db");
        using (var store = Store.Open(Path.Combine(dir.Path, "library.db")))
        {
            store.CopyTo(copy);
        }
        using var opened = Store.Open(copy);
        Assert.Empty(opened.List());
    }

    [Fact]
    public void ChosenFolderMovesOnlyTheLibrary()
    {
        using var home = new TempFolder();
        using var chosen = new TempFolder();
        var before = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", home.Path);
            var root = Path.Combine(home.Path, "Mimic");
            Directory.CreateDirectory(root);
            Assert.Equal(root, Path.GetDirectoryName(Paths.FromEnv().Database()));

            File.WriteAllText(Paths.FromEnv().LibraryPointer(), chosen.Path);
            var paths = Paths.FromEnv();
            Assert.Equal(chosen.Path, Path.GetDirectoryName(paths.Database()));
            Assert.Equal(chosen.Path, Path.GetDirectoryName(paths.ImagesDir()));
            // Tools and models stay in the default folder.
            Assert.StartsWith(root, Path.GetFullPath(paths.YtDlp()));
            Assert.StartsWith(root, Path.GetFullPath(paths.Model()));
            Assert.StartsWith(root, Path.GetFullPath(paths.FfmpegDir()));

            // A folder that has gone away falls back to the default.
            File.WriteAllText(paths.LibraryPointer(), Path.Combine(chosen.Path, "missing"));
            Assert.Equal(root, Path.GetDirectoryName(Paths.FromEnv().Database()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", before);
        }
    }
}
