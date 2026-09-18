using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// The Language picker on Settings opened a list that stopped at the bottom of its own card:
/// English, Deutsch and Русский were there and 中文 (简体) was not. The cause was never
/// overflow. server-styles.css sets backdrop-filter on the bare <c>.content-card</c> selector,
/// so every card in the app is a stacking context; a list painted inside one is covered by the
/// next card from that card's top edge down. Lifting the card's overflow:hidden — which is what
/// the old <c>.content-card:has(.rr-dd)</c> rule did — could not help, and quietly cost every
/// card holding a dropdown its rounded clipping.
///
/// The list is in the browser's top layer now, which sits above every stacking context on the
/// page, so no card, scroller or transform can reach it. These pin that, the placement rules
/// around it, and that the escape hatch is gone rather than kept "just in case".
/// </summary>
public sealed class DropdownLayerTests
{
    private static string Component() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "Components", "Shared", "Dropdown.razor"));

    private static string Layer() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "js", "dropdown-layer.js"));

    private static string Primitives() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "primitives.css"));

    [Fact]
    public void TheListIsRenderedIntoTheTopLayer()
    {
        var component = Component();

        // popover promotes it out of the card's stacking context without moving it in the DOM,
        // so Blazor keeps owning the element's lifetime.
        var pop = Regex.Match(component, @"<div class=""rr-dd-pop""[^>]*>", RegexOptions.Singleline);
        Assert.True(pop.Success, "The option list must still be the .rr-dd-pop element.");
        Assert.Contains("popover=\"manual\"", pop.Value, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", pop.Value, StringComparison.Ordinal);

        Assert.Contains("rrDropdownLayer.open", component, StringComparison.Ordinal);
        Assert.Contains("rrDropdownLayer.close", component, StringComparison.Ordinal);

        // The old transparent backdrop is gone: it was position:fixed inside the card, so the
        // card's backdrop-filter made it cover the card only and outside clicks never landed.
        Assert.DoesNotContain("rr-dd-backdrop", component, StringComparison.Ordinal);
        Assert.DoesNotContain("rr-dd-backdrop", Primitives(), StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));
        Assert.Contains("js/dropdown-layer.js", index, StringComparison.Ordinal);
    }

    /// <summary>
    /// A top-layer element is positioned against the window, so the placement has to be computed
    /// rather than inherited: the trigger's rectangle, a flip when the room below runs out, and a
    /// cap that keeps the list inside the window with its own scrollbar.
    /// </summary>
    [Fact]
    public void ThePlacementFlipsAndCapsToTheWindow()
    {
        var layer = Layer();

        Assert.Contains("state.trigger.getBoundingClientRect()", layer, StringComparison.Ordinal);
        Assert.Contains("var up = wanted > below && above > below;", layer, StringComparison.Ordinal);
        Assert.Contains("pop.classList.toggle('flip-up', up);", layer, StringComparison.Ordinal);
        Assert.Contains("pop.style.maxHeight = height + 'px';", layer, StringComparison.Ordinal);

        // Follows the trigger while the page moves under it, in capture so a scroll inside
        // .main-content — the real scroller, whose scroll event does not bubble — is seen too.
        Assert.Contains("window.addEventListener('scroll', state.reposition, true);", layer, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('resize', state.reposition);", layer, StringComparison.Ordinal);
        Assert.Contains("window.removeEventListener('scroll', state.reposition, true);", layer, StringComparison.Ordinal);
        Assert.Contains("window.removeEventListener('resize', state.reposition);", layer, StringComparison.Ordinal);

        // The cap itself stays a design token in the stylesheet; JS only narrows it.
        var rule = PopRule();
        Assert.Contains("max-height: 280px;", rule, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", rule, StringComparison.Ordinal);
        Assert.Contains("parseFloat(window.getComputedStyle(pop).maxHeight)", layer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trigger is sized to the label it happens to show, so a list that copied the trigger's
    /// width was as narrow as whatever was selected: on Settings with English picked the list was
    /// 82px and "中文 (简体)" read "中文 (…", and picking Chinese grew the same list to 103px. The
    /// width comes from the list's own content now — the stylesheet grows it to its longest option
    /// — and the trigger is only a floor, the window only a ceiling.
    /// </summary>
    [Fact]
    public void TheListGrowsToItsLongestOptionInsteadOfCopyingTheTrigger()
    {
        var layer = Layer();

        // The content decides the width. Without this the min-width below would be the width.
        Assert.Contains("width: max-content;", PopRule(), StringComparison.Ordinal);

        Assert.Contains("pop.style.minWidth = Math.round(rect.width) + 'px';", layer, StringComparison.Ordinal);
        Assert.Contains("pop.style.maxWidth = Math.max(0, Math.round(vw - 2 * MARGIN)) + 'px';", layer, StringComparison.Ordinal);

        // And nothing pins it to a fixed width again — that assignment was the defect. The only
        // width the layer may write is the empty one that hands the element back to the stylesheet.
        foreach (Match assignment in Regex.Matches(layer, @"pop\.style\.width\s*=\s*([^;]+);"))
        {
            Assert.Equal("''", assignment.Groups[1].Value.Trim());
        }

        // The horizontal clamp works from the width the list really took, measured after the
        // min-width lands — the list is wider than the trigger now, and a capped one also grows
        // its own scrollbar, so the trigger's rectangle is the wrong ruler for it.
        var measured = layer.IndexOf("var width = pop.getBoundingClientRect().width;", StringComparison.Ordinal);
        var floor = layer.IndexOf("pop.style.minWidth = Math.round(rect.width) + 'px';", StringComparison.Ordinal);
        Assert.True(measured > 0 && floor > 0 && floor < measured, "The list's width must be measured after its min-width is applied.");
        Assert.DoesNotContain("vw - rect.width - MARGIN", layer, StringComparison.Ordinal);

        // Left edges aligned, right edges aligned when growing right would leave the window, and
        // the clamp has the last word on both edges.
        Assert.Contains("if (left + width > vw - MARGIN) left = rect.right - width;", layer, StringComparison.Ordinal);
        Assert.Contains("left = Math.min(Math.max(MARGIN, left), Math.max(MARGIN, vw - width - MARGIN));", layer, StringComparison.Ordinal);

        // A reopen starts from the stylesheet: a floor left over from another trigger would widen
        // the next list, for the same reason the height reset is there.
        var openFn = layer[layer.IndexOf("open: function", StringComparison.Ordinal)..layer.IndexOf("reveal: function", StringComparison.Ordinal)];
        Assert.Contains("pop.style.minWidth = '';", openFn, StringComparison.Ordinal);
        Assert.Contains("pop.style.maxWidth = '';", openFn, StringComparison.Ordinal);

        // The label keeps its ellipsis: in a window too narrow for the longest option the list is
        // capped, and a clipped label is the honest outcome there.
        Assert.Contains("text-overflow: ellipsis;", Rule(".rr-dd-opt-label"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Dismissal moved into the layer, because the element it listens around is no longer painted
    /// inside the component's own card. A click on the trigger is not a dismissal: the trigger's
    /// own click closes the list, and closing here first would let that click reopen it.
    /// </summary>
    [Fact]
    public void OutsideClicksAndEscapeCloseItThroughTheComponent()
    {
        var layer = Layer();

        Assert.Contains("document.addEventListener('pointerdown', state.onPointerDown, true);", layer, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('keydown', state.onKeyDown, true);", layer, StringComparison.Ordinal);
        Assert.Contains("document.removeEventListener('pointerdown', state.onPointerDown, true);", layer, StringComparison.Ordinal);
        Assert.Contains("document.removeEventListener('keydown', state.onKeyDown, true);", layer, StringComparison.Ordinal);
        Assert.Contains("if (pop.contains(target) || trigger.contains(target)) return;", layer, StringComparison.Ordinal);
        Assert.Contains("if (event.key === 'Escape') dismiss(state);", layer, StringComparison.Ordinal);
        Assert.Contains("invokeMethodAsync('CloseFromLayer')", layer, StringComparison.Ordinal);

        var component = Component();
        Assert.Contains("[JSInvokable]", component, StringComparison.Ordinal);
        Assert.Contains("public Task CloseFromLayer()", component, StringComparison.Ordinal);

        // The keyboard model is unchanged — the trigger keeps focus, so it still drives the list.
        foreach (var key in new[] { "ArrowDown", "ArrowUp", "Home", "End", "Enter", "Escape" })
        {
            Assert.Contains("case \"" + key + "\":", component, StringComparison.Ordinal);
        }

        // A capped list has to scroll the keyboard cursor back into view itself.
        Assert.Contains("rrDropdownLayer.reveal", component, StringComparison.Ordinal);
        Assert.Contains("active.scrollIntoView({ block: 'nearest' });", layer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layer holds a DotNetObjectReference and four document-level listeners per open list.
    /// Both have to come off, including when the page is navigated away while a list is open.
    /// </summary>
    [Fact]
    public void TheComponentTakesTheLayerDownWithIt()
    {
        var component = Component();

        Assert.Contains("@implements IAsyncDisposable", component, StringComparison.Ordinal);
        Assert.Contains("public async ValueTask DisposeAsync()", component, StringComparison.Ordinal);
        Assert.Contains("_self?.Dispose();", component, StringComparison.Ordinal);
        // The window can go first; that is not a failure.
        Assert.Contains("catch (JSDisconnectedException)", component, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real proof that the cause was understood: nothing in the app opts a card out of its
    /// own clipping for the sake of a dropdown any more. If that rule ever comes back, the fix
    /// has been misread again.
    /// </summary>
    [Fact]
    public void NoCardGivesUpItsClippingForTheMenu()
    {
        var css = Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css");
        var offenders = Directory.GetFiles(css, "*.css", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\.content-card:has\(\s*\.rr-dd[^)]*\)"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);

        // And the list is placed against the window, not against an ancestor that could clip it.
        var rule = PopRule();
        Assert.Contains("position: fixed;", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("position: absolute;", rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// One primitive, so one fix. Every picker in the app — the monitor pickers on Crosshair and
    /// Stretched resolution, the option lists on Scripts, the destination on Convert and the rest
    /// — goes through &lt;Dropdown&gt;, and no page builds its own list markup beside it.
    /// </summary>
    [Fact]
    public void EveryPickerGoesThroughTheOnePrimitive()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");
        var ownList = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "Dropdown.razor")
            .Where(path => File.ReadAllText(path).Contains("rr-dd-pop", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(ownList);

        var callers = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("<Dropdown ", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        // The call sites the owner asked to be checked for regressions.
        foreach (var page in new[] { "Crosshair.razor", "StretchedRes.razor", "Scripts.razor", "FileConverter.razor", "Settings.razor" })
        {
            Assert.Contains(page, callers);
        }
    }

    /// <summary>
    /// And nothing is left of the control it replaced. A native &lt;select&gt; draws its open list
    /// through the OS, outside the page, so it stays a white system menu in a dark app whatever
    /// the CSS says — that is why the migration happened. Two stylesheets went on dressing one up
    /// long after the last element was gone (.settings-select on a page whose picker is a
    /// &lt;Dropdown&gt;, .convert-select likewise), which is exactly the sort of leftover the next
    /// page needing a picker copies.
    /// </summary>
    [Fact]
    public void NothingIsLeftOfTheNativeSelect()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");
        var markup = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            // Comments are read first: two files explain in prose why the element is gone, and
            // that explanation is the reason nobody puts one back.
            .Where(path => Regex.IsMatch(WithoutComments(File.ReadAllText(path)), @"<select\b"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(markup);

        // theme.css's `input, textarea, select` rule may keep naming the element — it is there for
        // the inputs. What must not survive is a class styled as a picker of its own.
        var css = Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css");
        var orphans = Directory.GetFiles(css, "*.css", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\.[\w-]*-select\b"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(orphans);
    }

    /// <summary>Razor and HTML comments stripped, so prose about markup is not read as markup.</summary>
    private static string WithoutComments(string markup)
        => Regex.Replace(Regex.Replace(markup, @"@\*[\s\S]*?\*@", string.Empty), @"<!--[\s\S]*?-->", string.Empty);

    /// <summary>The .rr-dd-pop block in primitives.css, up to its closing brace.</summary>
    private static string PopRule() => Rule(".rr-dd-pop");

    /// <summary>One rule block in primitives.css, from its selector to its closing brace.</summary>
    private static string Rule(string selector)
    {
        var css = Primitives();
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"primitives.css must still style {selector}.");
        return css[start..css.IndexOf('}', start)];
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
