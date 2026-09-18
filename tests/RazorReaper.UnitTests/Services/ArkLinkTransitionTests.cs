using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests.Services;

/// <summary>
/// Close-with-ARK closes the user's app. There is no undo, no dialog and nothing on screen
/// afterwards to say what happened — so the two questions this logic answers are the two that
/// cost the most to get wrong: "is ARK really gone?" and "did ARK really just start?".
///
/// Neither was reachable before. Both lived inside a 3-second polling loop wrapped around a
/// process lookup, which is untestable twice over: a test would have to wait out real seconds and
/// own a real process. The loop still owns the timer and the lookup; the decision is now
/// <see cref="ArkPresenceDebounce"/>, and this is it under every ordering that matters.
/// </summary>
public sealed class ArkPresenceDebounceTests
{
    // ─── Exit debounce: the expensive one ──────────────────────────────────────

    /// <summary>
    /// Process enumeration transiently misses a live process. One missed poll closing the app is
    /// the failure this debounce exists to prevent, and it would look to the user like a crash.
    /// </summary>
    [Fact]
    public void OneMissedPollDoesNotCountAsArkExiting()
    {
        var presence = Running();

        Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
        Assert.Equal(ArkPresenceChange.None, presence.Observe(true));
    }

    [Fact]
    public void TwoMissedPollsInARowAreAnExit()
    {
        var presence = Running();

        Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
        Assert.Equal(ArkPresenceChange.WentDown, presence.Observe(false));
    }

    /// <summary>
    /// A blip resets the count. Otherwise a machine that drops one poll every few minutes would
    /// accumulate its way to a close while ARK ran the whole time.
    /// </summary>
    [Fact]
    public void MissesSeparatedByASightingNeverAddUp()
    {
        var presence = Running();

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
            Assert.Equal(ArkPresenceChange.None, presence.Observe(true));
        }

        Assert.True(presence.IsArkRunning);
    }

    [Fact]
    public void AnExitIsReportedOnceAndNotAgainWhileArkStaysClosed()
    {
        var presence = Running();

        presence.Observe(false);
        Assert.Equal(ArkPresenceChange.WentDown, presence.Observe(false));

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
        }
    }

    // ─── Start transition ──────────────────────────────────────────────────────

    /// <summary>
    /// The common case: the app is opened while ARK is already running. Treating the very first
    /// poll as a start would raise the window over the game at every launch.
    /// </summary>
    [Fact]
    public void TheFirstPollOnlyRecordsWhereArkIs()
    {
        Assert.Equal(ArkPresenceChange.None, new ArkPresenceDebounce().Observe(true));
        Assert.Equal(ArkPresenceChange.None, new ArkPresenceDebounce().Observe(false));
    }

    [Fact]
    public void ArkStartingAfterAConfirmedAbsenceIsAStart()
    {
        var presence = new ArkPresenceDebounce();
        presence.Observe(false);

        Assert.Equal(ArkPresenceChange.CameUp, presence.Observe(true));
    }

    [Fact]
    public void StayingUpIsNotRepeatedlyReportedAsAStart()
    {
        var presence = new ArkPresenceDebounce();
        presence.Observe(false);
        Assert.Equal(ArkPresenceChange.CameUp, presence.Observe(true));

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ArkPresenceChange.None, presence.Observe(true));
        }
    }

    /// <summary>
    /// Close-with-ARK off leaves the watcher running after an exit, and the next launch has to
    /// still bring the window up — that is the whole reason the loop keeps going.
    /// </summary>
    [Fact]
    public void AfterAnExitTheNextLaunchIsStillAStart()
    {
        var presence = Running();
        presence.Observe(false);
        Assert.Equal(ArkPresenceChange.WentDown, presence.Observe(false));

        Assert.Equal(ArkPresenceChange.CameUp, presence.Observe(true));
    }

    /// <summary>
    /// A start that is still inside the exit debounce is not an exit and not a start: nothing
    /// was ever confirmed down, so there is nothing to come back from.
    /// </summary>
    [Fact]
    public void AStartInsideTheDebounceWindowRaisesNothing()
    {
        var presence = Running();

        Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
        Assert.Equal(ArkPresenceChange.None, presence.Observe(true));
        Assert.Equal(ArkPresenceChange.None, presence.Observe(true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void TheConfirmCountIsHonouredExactly(int confirmPolls)
    {
        var presence = new ArkPresenceDebounce(confirmPolls);
        presence.Observe(true);

        for (var i = 1; i < confirmPolls; i++)
        {
            Assert.Equal(ArkPresenceChange.None, presence.Observe(false));
        }

        Assert.Equal(ArkPresenceChange.WentDown, presence.Observe(false));
    }

    /// <summary>ARK's default 3s poll cadence times two — the number the loop ships with.</summary>
    [Fact]
    public void TheShippedDebounceGivesABlipSixSecondsToTakeItselfBack()
        => Assert.Equal(2, ArkPresenceDebounce.ExitConfirmPolls);

    private static ArkPresenceDebounce Running()
    {
        var presence = new ArkPresenceDebounce();
        presence.Observe(true);
        return presence;
    }
}

/// <summary>
/// The one combined toggle the pre-split builds stored has to reach the two that replaced it,
/// exactly once. Getting it wrong is quiet in both directions: too little and the user's
/// close-with-ARK silently stops working after an update, too much and it comes back on every
/// launch after they turn it off.
/// </summary>
public sealed class ArkLinkLegacyMigrationTests
{
    [Fact]
    public void NoLegacyKeyMeansNothingToDo()
    {
        var plan = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: false, legacyValue: true, hasStartWithArk: false, hasCloseWithArk: false);

        Assert.Equal(ArkLinkLegacyMigration.None, plan);
        Assert.False(plan.RemoveLegacyKey);
    }

    [Fact]
    public void AnOldToggleThatWasOnTurnsBothNewOnesOn()
    {
        var plan = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: true, legacyValue: true, hasStartWithArk: false, hasCloseWithArk: false);

        Assert.True(plan.SetStartWithArk);
        Assert.True(plan.SetCloseWithArk);
        Assert.True(plan.RemoveLegacyKey);
    }

    /// <summary>
    /// Off carries nothing: both new options already default to off, and writing them would only
    /// freeze that default into the store.
    /// </summary>
    [Fact]
    public void AnOldToggleThatWasOffWritesNothingButStillGoesAway()
    {
        var plan = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: true, legacyValue: false, hasStartWithArk: false, hasCloseWithArk: false);

        Assert.False(plan.SetStartWithArk);
        Assert.False(plan.SetCloseWithArk);
        Assert.True(plan.RemoveLegacyKey);
    }

    /// <summary>
    /// The user has already been in the new settings and made a choice. The dead toggle does not
    /// get to overrule it — including when what they chose was to turn the option off, which is
    /// stored as a value like any other.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AValueTheUserAlreadySetIsNeverOverwritten(bool hasStart, bool hasClose)
    {
        var plan = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: true, legacyValue: true, hasStartWithArk: hasStart, hasCloseWithArk: hasClose);

        Assert.Equal(!hasStart, plan.SetStartWithArk);
        Assert.Equal(!hasClose, plan.SetCloseWithArk);
        Assert.True(plan.RemoveLegacyKey);
    }

    /// <summary>
    /// Removing the key is what makes this run once. Left behind, the next launch would migrate
    /// again — and by then the user may have turned the option off.
    /// </summary>
    [Fact]
    public void TheSecondLaunchMigratesNothing()
    {
        var first = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: true, legacyValue: true, hasStartWithArk: false, hasCloseWithArk: false);
        Assert.True(first.RemoveLegacyKey);

        // The key is gone and the user has since turned close-with-ARK back off.
        var second = ArkLinkLegacyMigration.Plan(
            hasLegacyKey: false, legacyValue: false, hasStartWithArk: true, hasCloseWithArk: true);

        Assert.Equal(ArkLinkLegacyMigration.None, second);
    }
}
