using AppGuard.Core.Models;
using AppGuard.Core.Policies;

namespace AppGuard.Core.Tests;

public sealed class TerminationPolicyTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly ProcessIdentity Identity = new(4001, Started, @"C:\Apps\Editor\editor.exe", 1);
    private readonly TerminationPolicy _policy = new();

    private static MonitoredApplication App(bool allow = true, bool background = false, bool protect = false) =>
        new(Guid.NewGuid(), "Editor", Identity.ExecutablePath, true, TimeSpan.FromSeconds(3), background, allow, protect);
    private static ProcessSnapshot Snapshot(ProcessIdentity? identity = null, bool exited = false) => new(identity ?? Identity, exited);
    private static TerminationOptions Options(bool dryRun = true, IReadOnlySet<string>? protectedPaths = null) => new(dryRun, true, 1, protectedPaths);
    private TerminationDecision Decide(MonitoredApplication? app = null, ProcessSnapshot? process = null,
        IReadOnlyList<WindowSnapshot>? windows = null, bool observed = true, bool changed = false,
        TerminationOptions? options = null) => _policy.Evaluate(app ?? App(), Identity, process ?? Snapshot(), windows ?? [], options ?? Options(), observed, changed);

    [Fact] public void LastWindowClosedAfterPreviouslyObservedWindow_AllowsDecision() => Assert.True(Decide().Allowed);
    [Fact] public void SecondOpenWindowBlocksTermination() => Assert.False(Decide(windows: [new(4001, 4, true, false, true)]).Allowed);
    [Fact] public void ReopenedWindowDuringGraceBlocksTermination() => Assert.Contains("changed during", Decide(changed: true).Reason);
    [Fact] public void MissingAllowlistEntryIsDenied() => Assert.False(_policy.Evaluate(null, Identity, Snapshot(), [], Options(), true, false).Allowed);
    [Fact] public void ProtectedApplicationIsDenied() => Assert.Contains("protected", Decide(app: App(protect: true)).Reason);

    [Fact]
    public void ReusedPidWithDifferentCreationTimeIsDenied()
    {
        var replacement = Identity with { StartedAtUtc = Started.AddMinutes(2) };
        Assert.Contains("reused", Decide(process: Snapshot(replacement)).Reason);
    }

    [Fact]
    public void DifferentExecutableIsDenied()
    {
        var replacement = Identity with { ExecutablePath = @"C:\Other\editor.exe" };
        Assert.Contains("does not match", Decide(process: Snapshot(replacement)).Reason);
    }

    [Fact] public void InaccessibleProcessIsDenied() => Assert.Contains("inaccessible", _policy.Evaluate(App(), Identity, null, [], Options(), true, false).Reason);
    [Fact] public void ExitedProcessIsDenied() => Assert.Contains("already exited", Decide(process: Snapshot(exited: true)).Reason);

    [Fact]
    public void MultipleInstancesAreEvaluatedIndependentlyByIdentity()
    {
        var second = Identity with { Pid = 4002, StartedAtUtc = Started.AddSeconds(1) };
        Assert.True(Decide().Allowed);
        Assert.False(_policy.Evaluate(App(), second, Snapshot(), [], Options(), true, false).Allowed);
    }

    [Fact] public void DryRunDoesNotAuthorizeRealTerminationAction() => Assert.True(Options().DryRun);
    [Fact] public void StoppedMonitoringIsDenied() => Assert.Contains("stopped", Decide(options: Options() with { MonitoringEnabled = false }).Reason);
    [Fact] public void CancellationDuringGraceIsRepresentedAsStateChange() => Assert.False(Decide(changed: true).Allowed);
    [Fact] public void NoPreviouslyObservedWindowCannotTriggerTermination() => Assert.False(Decide(observed: false).Allowed);
    [Fact] public void MinimizedWindowIsStillAnOpenWindow() => Assert.False(Decide(windows: [new(4001, 4, true, true, true)]).Allowed);
    [Fact] public void OwnedAuxiliaryDialogBlocksTermination() => Assert.False(Decide(windows: [new(4001, 4, true, false, false)]).Allowed);
    [Fact] public void ProtectedPathBlocksEvenAllowlistedApplication() => Assert.False(Decide(options: Options(protectedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Identity.ExecutablePath })).Allowed);
    [Fact] public void BackgroundCapableAppRequiresExplicitConsent() => Assert.False(Decide(app: App(allow: false, background: true)).Allowed);
}
