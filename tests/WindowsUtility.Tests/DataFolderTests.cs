using System.IO;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public class DataFolderTests
{
    [Fact]
    public void TheOldDataFolder_IsMovedWhole()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wu-move-{Guid.NewGuid():N}");
        string oldPath = Path.Combine(root, "old");
        string newPath = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(oldPath, "service"));
            File.WriteAllText(Path.Combine(oldPath, "settings.json"), "settings");
            File.WriteAllText(Path.Combine(oldPath, "service", "remote-viewers.json"), "pairings");

            SharedDataPaths.MoveFolder(oldPath, newPath);

            Assert.False(Directory.Exists(oldPath));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(newPath, "settings.json")));
            Assert.Equal("pairings", File.ReadAllText(Path.Combine(newPath, "service", "remote-viewers.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WhenTheNewFolderExists_OnlyMissingFilesAreCopiedAndTheOldFolderGoes()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wu-move-{Guid.NewGuid():N}");
        string oldPath = Path.Combine(root, "old");
        string newPath = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(oldPath, "service"));
            Directory.CreateDirectory(Path.Combine(newPath, "logs"));
            File.WriteAllText(Path.Combine(oldPath, "settings.json"), "old settings");
            File.WriteAllText(Path.Combine(oldPath, "service", "remote-viewers.json"), "pairings");
            File.WriteAllText(Path.Combine(newPath, "settings.json"), "new settings");

            SharedDataPaths.MoveFolder(oldPath, newPath);

            Assert.False(Directory.Exists(oldPath));
            Assert.Equal("new settings", File.ReadAllText(Path.Combine(newPath, "settings.json")));
            Assert.Equal("pairings", File.ReadAllText(Path.Combine(newPath, "service", "remote-viewers.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
