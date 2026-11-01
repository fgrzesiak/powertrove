using System;
using System.Collections.Generic;
using PowerTrove.Core.Input;
using Xunit;

namespace PowerTrove.Core.Tests.Input;

public class HotkeyAdvisorTests
{
    private sealed class Processes(params string[] running) : IRunningProcessProbe
    {
        public bool IsRunning(string processName) => Array.IndexOf(running, processName) >= 0;
    }

    private sealed class FailingProcesses : IRunningProcessProbe
    {
        public bool IsRunning(string processName) => throw new InvalidOperationException("denied");
    }

    private static readonly HotkeyConfiguration CtrlAltSpace = new(KeyModifiers.Control | KeyModifiers.Alt, 0x20);
    private static readonly HotkeyConfiguration AltShiftSpace = new(KeyModifiers.Alt | KeyModifiers.Shift, 0x20);

    [Fact]
    public void Conflict_SuggestsFirstFreeAlternative()
    {
        var advice = HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Conflict, new Processes(), _ => true, false);

        Assert.Equal(HotkeyIssue.Conflict, advice.Issue);
        Assert.Equal(CtrlAltSpace, advice.Suggestion);
    }

    [Fact]
    public void Conflict_SkipsTakenAlternativesAndTheCurrentShortcut()
    {
        var probed = new List<HotkeyConfiguration>();
        var advice = HotkeyAdvisor.Evaluate(CtrlAltSpace, RegisterHotkeyResult.Conflict, new Processes(),
            candidate => { probed.Add(candidate); return candidate == AltShiftSpace; }, false);

        Assert.Equal(AltShiftSpace, advice.Suggestion);
        Assert.DoesNotContain(CtrlAltSpace, probed);
    }

    [Fact]
    public void Conflict_WithoutFreeAlternative_HasNoSuggestion()
    {
        var advice = HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Conflict, new Processes(), _ => false, false);

        Assert.Equal(HotkeyIssue.Conflict, advice.Issue);
        Assert.Null(advice.Suggestion);
    }

    [Theory]
    [InlineData(RegisterHotkeyResult.Error)]
    [InlineData(RegisterHotkeyResult.InitializationFailed)]
    public void OtherFailures_ReportWithoutSuggestion(RegisterHotkeyResult result)
    {
        var advice = HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, result, new Processes("PowerToys.PowerLauncher"), _ => true, false);

        Assert.Equal(HotkeyIssue.RegistrationFailed, advice.Issue);
        Assert.Null(advice.Suggestion);
    }

    [Theory]
    [InlineData("PowerToys.PowerLauncher", "PowerToys Run")]
    [InlineData("Flow.Launcher", "Flow Launcher")]
    [InlineData("Wox", "Wox")]
    public void RunningLauncher_OnDefaultShortcut_IsReportedEvenWhenRegistrationSucceeded(string process, string name)
    {
        var advice = HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Success, new Processes(process), _ => true, false);

        Assert.Equal(HotkeyIssue.LauncherMayConflict, advice.Issue);
        Assert.Equal(name, advice.LauncherName);
        Assert.Equal(CtrlAltSpace, advice.Suggestion);
    }

    [Fact]
    public void RunningLauncher_IsIgnoredForCustomShortcutOrAfterDismissal()
    {
        var launcher = new Processes("PowerToys.PowerLauncher");

        Assert.Equal(HotkeyAdvice.None, HotkeyAdvisor.Evaluate(CtrlAltSpace, RegisterHotkeyResult.Success, launcher, _ => true, false));
        Assert.Equal(HotkeyAdvice.None, HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Success, launcher, _ => true, true));
    }

    [Fact]
    public void NoLauncherOrFailingProbe_ReportsNothing()
    {
        Assert.Equal(HotkeyAdvice.None, HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Success, new Processes(), _ => true, false));
        Assert.Equal(HotkeyAdvice.None, HotkeyAdvisor.Evaluate(HotkeyAdvisor.Default, RegisterHotkeyResult.Success, new FailingProcesses(), _ => true, false));
    }

    [Fact]
    public void Alternatives_NeverIncludeTheDefault()
    {
        Assert.DoesNotContain(HotkeyAdvisor.Default, HotkeyAdvisor.Alternatives);
        Assert.Equal(HotkeyAdvisor.Alternatives.Count, new HashSet<HotkeyConfiguration>(HotkeyAdvisor.Alternatives).Count);
    }
}
