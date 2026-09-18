using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests;

public class ArkKeyBindingParserTests
{
    // Lines copied verbatim from a real, heavily customised ARK Input.ini.
    private static readonly string[] RealWorldIni =
    [
        "[/Script/Engine.InputSettings]",
        "ActionMappings=(ActionName=\"AccessInventory\",Key=F,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"ShowMyInventory\",Key=Y,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"TransferItem\",Key=T,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"Use\",Key=E,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"CraftAll\",Key=A,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"Crouch\",Key=LeftAlt,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"UseItem10\",Key=Zero,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"Fire\",Key=LeftMouseButton,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"AltFire\",Key=Gamepad_RightShoulder,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "ActionMappings=(ActionName=\"CallLandOne\",Key=None,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "AxisMappings=(AxisName=\"MoveForward\",Key=W,Scale=1.000000)",
        "AxisMappings=(AxisName=\"MoveForward\",Key=S,Scale=-1.000000)",
    ];

    [Theory]
    [InlineData(ArkActions.AccessInventory, "F")]
    [InlineData(ArkActions.ShowMyInventory, "Y")]
    [InlineData(ArkActions.TransferItem, "T")]
    [InlineData(ArkActions.Use, "E")]
    public void ReadsTheActionsTheScriptsDependOn(string action, string expected)
    {
        var bindings = ArkKeyBindingParser.Parse(RealWorldIni);

        Assert.Equal(expected, bindings[action]);
    }

    [Fact]
    public void ForwardAxisTakesThePositiveDirectionNotTheNegativeOne()
    {
        // MoveForward is bound twice: W at +1 and S at -1. Picking the wrong one would walk backwards.
        var bindings = ArkKeyBindingParser.Parse(RealWorldIni);

        Assert.Equal("W", bindings[ArkActions.MoveForward]);
    }

    [Fact]
    public void NumberKeyNamesBecomeDigits()
    {
        var bindings = ArkKeyBindingParser.Parse(RealWorldIni);

        Assert.Equal("0", bindings["UseItem10"]);
    }

    [Theory]
    [InlineData("Fire")]                 // mouse button
    [InlineData("AltFire")]              // gamepad
    [InlineData("CallLandOne")]          // explicitly unbound
    public void BindingsAScriptCannotPressAreNotOffered(string action)
    {
        // Keeping these would hand a script a key it can never synthesize.
        var bindings = ArkKeyBindingParser.Parse(RealWorldIni);

        Assert.False(bindings.ContainsKey(action));
    }

    [Theory]
    [InlineData("F", "F")]
    [InlineData("y", "Y")]
    [InlineData("Zero", "0")]
    [InlineData("Nine", "9")]
    [InlineData("SpaceBar", "Space")]
    [InlineData("Enter", "Enter")]
    [InlineData("F7", "F7")]
    [InlineData("F12", "F12")]
    public void TranslatesArkKeyTokensToScriptLabels(string arkKey, string expected)
    {
        Assert.True(ArkKeyBindingParser.TryTranslateKey(arkKey, out var label));
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Gamepad_FaceButton_Bottom")]
    [InlineData("LeftMouseButton")]
    [InlineData("MouseScrollUp")]
    [InlineData("Global_Menu")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("F25")]
    public void UnpressableTokensAreRejected(string? arkKey)
    {
        Assert.False(ArkKeyBindingParser.TryTranslateKey(arkKey, out _));
    }

    [Fact]
    public void LaterLinesWinLikeUnrealResolvesThem()
    {
        string[] ini =
        [
            "ActionMappings=(ActionName=\"AccessInventory\",Key=E,bShift=False)",
            "ActionMappings=(ActionName=\"AccessInventory\",Key=F,bShift=False)",
        ];

        Assert.Equal("F", ArkKeyBindingParser.Parse(ini)[ArkActions.AccessInventory]);
    }

    [Fact]
    public void AnEmptyOrMissingFileYieldsNoBindingsRatherThanThrowing()
    {
        Assert.Empty(ArkKeyBindingParser.Parse(null));
        Assert.Empty(ArkKeyBindingParser.Parse([]));
        Assert.Empty(ArkKeyBindingParser.Parse(["", "   ", "[/Script/Engine.InputSettings]", "junk"]));
    }

    /// <summary>
    /// The lines for the six actions the scripts press, copied verbatim out of
    /// <c>ARK/ShooterGame/Config/DefaultInput.ini</c> — "+" prefix, gamepad entries, reversed axis
    /// and all. Copied rather than read: the install is hundreds of gigabytes, is not on the build
    /// machine, and a test that skips itself when it is missing proves nothing.
    /// </summary>
    private static readonly string[] GameDefaultInputIni =
    [
        "[/Script/Engine.InputSettings]",
        ";+DebugExecBindings=(Key=L,Command=\"ToggleInfiniteAmmo\")",
        "+ActionMappings=(ActionName=\"Run\",Key=LeftShift,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "+ActionMappings=(ActionName=\"ShowMyInventory\",Key=I,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "+ActionMappings=(ActionName=\"Use\",Key=E,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "+ActionMappings=(ActionName=\"AccessInventory\",Key=F,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "+ActionMappings=(ActionName=\"TransferItem\",Key=T,bShift=False,bCtrl=False,bAlt=False,bCmd=False)",
        "+AxisMappings=(AxisName=\"MoveForward\",Key=Gamepad_LeftY,Scale=1.000000)",
        "+AxisMappings=(AxisName=\"MoveForward\",Key=S,Scale=-1.000000)",
        "+AxisMappings=(AxisName=\"MoveForward\",Key=W,Scale=1.000000)",
    ];

    /// <summary>
    /// The one that would have caught it. Access Inventory sat on "E" — ARK's <c>Use</c> — from
    /// 554176b through 1.5.2, so every install without an AccessInventory line in Input.ini pressed
    /// the wrong key and reported a healthy run doing it.
    /// </summary>
    [Fact]
    public void EveryStockBindingIsWhatTheGamesOwnDefaultInputIniSays()
    {
        var shipped = ArkKeyBindingParser.Parse(GameDefaultInputIni);

        foreach (var (action, stock) in ArkKeyBindingParser.StockBindings)
        {
            Assert.True(shipped.ContainsKey(action), $"{action} is not in ARK's DefaultInput.ini");
            Assert.Equal(shipped[action], stock);
        }
    }

    [Fact]
    public void UnrealsPlusPrefixedLinesAreReadLikeAnyOther()
    {
        // The player's Input.ini writes "ActionMappings="; the install's DefaultInput.ini writes
        // "+ActionMappings=". Rejecting the second would leave the base layer empty and silent.
        var bindings = ArkKeyBindingParser.Parse(GameDefaultInputIni);

        Assert.Equal("F", bindings[ArkActions.AccessInventory]);
        Assert.Equal("W", bindings[ArkActions.MoveForward]);
    }

    [Fact]
    public void CommentedOutMappingsAreNotBindings()
    {
        string[] ini =
        [
            "+ActionMappings=(ActionName=\"AccessInventory\",Key=F,bShift=False)",
            ";+ActionMappings=(ActionName=\"AccessInventory\",Key=E,bShift=False)",
        ];

        Assert.Equal("F", ArkKeyBindingParser.Parse(ini)[ArkActions.AccessInventory]);
    }

    [Fact]
    public void StockBindingsCoverEveryActionTheScriptsAskFor()
    {
        // Input.ini only lists what the player changed, so anything absent must have a factory value.
        foreach (var action in new[]
                 {
                     ArkActions.AccessInventory, ArkActions.ShowMyInventory, ArkActions.TransferItem,
                     ArkActions.Use, ArkActions.MoveForward, ArkActions.Run,
                 })
        {
            Assert.True(ArkKeyBindingParser.StockBindings.ContainsKey(action), action);
        }
    }
}
