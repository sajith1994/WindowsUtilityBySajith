using System.IO;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public class LogRotationTests
{
    [Fact]
    public void ALogOverTheLimit_IsMovedAsideAndReplacesTheOldCopy()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wu-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string log = Path.Combine(folder, "application.log");
            string previous = Path.Combine(folder, "application.1.log");
            File.WriteAllText(previous, "old");
            File.WriteAllBytes(log, new byte[ApplicationLogService.MaximumLogBytes + 1]);

            ApplicationLogService.RotateIfLarge(log);

            Assert.False(File.Exists(log));
            Assert.Equal(ApplicationLogService.MaximumLogBytes + 1, new FileInfo(previous).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALogUnderTheLimit_IsLeftAlone()
    {
        string log = Path.Combine(Path.GetTempPath(), $"wu-log-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, "small");
        try
        {
            ApplicationLogService.RotateIfLarge(log);
            Assert.Equal("small", File.ReadAllText(log));
        }
        finally
        {
            File.Delete(log);
        }
    }
}
