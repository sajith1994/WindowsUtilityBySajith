extern alias service;
using WindowsUtility.Models;
using Xunit;
using Details = service::WindowsUtility.ServiceHost.PcDetailsProvider;
using ServiceProblem = service::WindowsUtility.Models.RemoteProblem;

namespace WindowsUtility.Tests;

public class PcDetailsTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, "UnexpectedShutdown")]
    [InlineData("EventLog", 6008, "UnexpectedShutdown")]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001, "BlueScreen")]
    [InlineData("disk", 7, "DiskError")]
    [InlineData("Ntfs", 55, "DiskError")]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, "HardwareError")]
    [InlineData("Application Error", 1000, "AppCrash")]
    [InlineData("Application Hang", 1002, "AppFreeze")]
    public void Classify_RecognisesEachKindOfProblem(string provider, int id, string kind) =>
        Assert.Equal(kind, Details.Classify(provider, id, Now, new[] { "outlook.exe" })!.Kind);

    [Fact]
    public void Classify_NamesTheCrashedAppAndTheStopCode()
    {
        Assert.Equal("outlook.exe crashed", Details.Classify("Application Error", 1000, Now, new[] { "outlook.exe" })!.Summary);
        Assert.Contains("0x0000003b", Details.Classify("Microsoft-Windows-WER-SystemErrorReporting", 1001, Now, new[] { "0x0000003b" })!.Summary);
    }

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-General", 12)]
    [InlineData("Application Error", 1001)]
    [InlineData("SomeOtherSource", 41)]
    public void Classify_IgnoresOtherEvents(string provider, int id) =>
        Assert.Null(Details.Classify(provider, id, Now, Array.Empty<string>()));

    [Fact]
    public void OneBadShutdown_LoggedTwice_CountsOnce_ButTwoSeparateOnesCountTwice()
    {
        List<ServiceProblem> problems = new()
        {
            new() { Kind = "UnexpectedShutdown", TimeUtc = Now.AddMinutes(-60) },
            new() { Kind = "UnexpectedShutdown", TimeUtc = Now.AddMinutes(-59) },
            new() { Kind = "UnexpectedShutdown", TimeUtc = Now.AddDays(-3) },
            new() { Kind = "AppCrash", TimeUtc = Now.AddMinutes(-59) }
        };
        List<ServiceProblem> kept = Details.RemoveDuplicateShutdowns(problems);
        Assert.Equal(2, kept.Count(problem => problem.Kind == "UnexpectedShutdown"));
        Assert.Single(kept, problem => problem.Kind == "AppCrash");
    }

    [Fact]
    public void Summarise_CountsOnlyTheRequestedDays()
    {
        List<ServiceProblem> problems = new()
        {
            new() { Kind = "BlueScreen", TimeUtc = Now.AddDays(-2) },
            new() { Kind = "AppCrash", TimeUtc = Now.AddDays(-5) },
            new() { Kind = "AppCrash", TimeUtc = Now.AddDays(-20) }
        };
        var week = Details.Summarise(problems, 7, Now);
        var month = Details.Summarise(problems, 30, Now);
        Assert.Equal((1, 1), (week.BlueScreens, week.AppCrashes));
        Assert.Equal(2, month.AppCrashes);
        Assert.True(week.HasSerious);
        Assert.Equal("1 blue screen  •  1 app crash", week.Describe());
    }

    [Fact]
    public void GroupByProgram_AddsUpEveryProcessOfAProgram()
    {
        var programs = Details.GroupByProgram(new[]
        {
            ("chrome", 1, 10.0, 100L), ("Chrome", 2, 5.5, 50L), ("explorer", 3, 1.0, 80L)
        });
        var chrome = programs.Single(program => program.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((2, 15.5, 150L), (chrome.Count, chrome.CpuPercent, chrome.MemoryBytes));
    }

    [Fact]
    public void Describe_IsEmptyWhenNothingWasLogged() => Assert.Equal(string.Empty, new RemoteProblemSummary().Describe());
}
