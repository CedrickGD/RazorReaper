using System.Globalization;
using Microsoft.AspNetCore.Components;
using Dispatcher = Microsoft.AspNetCore.Components.Dispatcher;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// How a language switch reaches a component, app-wide.
///
/// The defect it replaces came back twice, in the sidebar and then on Settings, and both times
/// for the same reason: a parent's render does not reach a child whose parameters are unchanged.
/// Blazor's diff compares them, finds them equal — trivially so for a child that takes none —
/// and keeps the child as it stands, English text and all. Fixing that per component means one
/// subscription, one handler and one unsubscribe in every component that ever renders a string,
/// and being wrong about it is invisible until somebody switches language and looks at the right
/// corner of the window.
///
/// So the fix is the framework's own: MainLayout publishes the language tag as a cascading value,
/// and a component that renders translated text declares a matching <c>[CascadingParameter]</c>.
/// A changed cascading value forces SetParametersAsync and a render on exactly the components
/// holding it, at any depth, with no subscription to forget and nothing to dispose.
///
/// These tests pin the three things that has to be true: the mechanism really does repaint a
/// parameterless child (and really does not, without it), the layout really publishes the value,
/// and every component that renders translated text really declares the parameter.
/// </summary>
public sealed class LanguageCascadeTests
{
    private const string Declaration = "[CascadingParameter(Name = \"Language\")]";
    private const string Provider = "<CascadingValue Value=\"@Localizer.Language\" Name=\"Language\">";

    /// <summary>
    /// Files under Components that render <c>Localizer.T</c> and still do not declare the
    /// parameter, with the reason each is allowed to.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Layout/MainLayout.razor"] =
                "publishes the cascade, so it sits above it and cannot receive its own value. Its "
                + "one Localizer.T call builds the post-update toast — a string handed to the "
                + "notification service, not markup that a repaint would change.",
        };

    // ---- The mechanism ------------------------------------------------------

    /// <summary>
    /// The whole fix in one render pass: two children of the same cascading value, alike in every
    /// way except that one declares the parameter. The parent re-renders with a new tag; the one
    /// that declares it renders again and sees "de", and the one that does not is left exactly as
    /// it stood — which is the bug, reproduced here so the fix has something to be a fix of.
    /// </summary>
    [Fact]
    public async Task AChangedCascadeRepaintsTheComponentsThatDeclareItAndOnlyThose()
    {
        Reader.Renders.Clear();
        Deaf.Renders.Clear();

        using var harness = new Harness();
        var root = new Root();
        await harness.MountAsync(root);

        Assert.Equal([AppLanguages.English], Reader.Renders);
        Assert.Single(Deaf.Renders);

        await harness.Dispatcher.InvokeAsync(() =>
        {
            root.Tag = AppLanguages.German;
            root.Repaint();
        });

        Assert.Equal([AppLanguages.English, AppLanguages.German], Reader.Renders);
        Assert.Single(Deaf.Renders);
    }

    /// <summary>
    /// An unchanged value notifies nobody, so the layout re-rendering for one of its own reasons —
    /// the access gate flipping, an appearance change — does not drag every localized component in
    /// the window through a render with it.
    /// </summary>
    [Fact]
    public async Task ALayoutRenderThatDoesNotChangeTheLanguageRepaintsNothing()
    {
        Reader.Renders.Clear();
        Deaf.Renders.Clear();

        using var harness = new Harness();
        var root = new Root();
        await harness.MountAsync(root);

        await harness.Dispatcher.InvokeAsync(root.Repaint);

        Assert.Single(Reader.Renders);
        Assert.Single(Deaf.Renders);
    }

    // ---- The value ----------------------------------------------------------

    /// <summary>
    /// The cascaded value is the language tag read straight off the localizer, so what this pins
    /// is that the tag is a different string after every switch. A value that compared equal to
    /// the last one would notify nobody and the window would stay in the old language — which is
    /// the same defect again, arriving through the fix rather than around it.
    /// </summary>
    [Fact]
    public void TheCascadedValueChangesOnEverySwitch()
    {
        var localizer = New();
        var seen = new List<string> { localizer.Language };
        localizer.LanguageChanged += () => seen.Add(localizer.Language);

        foreach (var code in new[]
        {
            AppLanguages.German,
            AppLanguages.Russian,
            AppLanguages.SimplifiedChinese,
            AppLanguages.English,
        })
        {
            localizer.SetLanguage(code);
        }

        Assert.Equal(
            [
                AppLanguages.English,
                AppLanguages.German,
                AppLanguages.Russian,
                AppLanguages.SimplifiedChinese,
                AppLanguages.English,
            ],
            seen);

        for (var i = 1; i < seen.Count; i++)
        {
            Assert.NotEqual(seen[i - 1], seen[i]);
        }
    }

    /// <summary>Picking the language that is already on changes nothing, so the cascade is quiet.</summary>
    [Fact]
    public void ReselectingTheSameLanguageDoesNotMoveTheValue()
    {
        var localizer = New();
        var raised = 0;
        localizer.LanguageChanged += () => raised++;

        localizer.SetLanguage(AppLanguages.German);
        localizer.SetLanguage(AppLanguages.German);
        localizer.SetLanguage("DE");

        Assert.Equal(1, raised);
        Assert.Equal(AppLanguages.German, localizer.Language);
    }

    // ---- The layout publishes it --------------------------------------------

    [Fact]
    public void TheLayoutPublishesTheLanguageAsACascadingValue()
    {
        var layout = Source("Layout/MainLayout.razor");

        Assert.Contains(Provider, layout, StringComparison.Ordinal);
        Assert.Contains("</CascadingValue>", layout, StringComparison.Ordinal);

        // Everything the app renders has to be inside it: the router's pages arrive through
        // @Body, and the overlays are mounted around it.
        var open = layout.IndexOf(Provider, StringComparison.Ordinal);
        var close = layout.IndexOf("</CascadingValue>", StringComparison.Ordinal);
        var inside = layout[open..close];

        foreach (var child in new[]
        {
            "@Body",
            "<SharedNavbar />",
            "<GlobalSearch />",
            "<LicenseOverlay />",
            "<WhatsNewOverlay />",
            "<AccessBlocked />",
        })
        {
            Assert.Contains(child, inside, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The layout's own subscription is what moves the value: it re-renders the layout, which
    /// re-renders the CascadingValue with the new tag. Losing it would leave the cascade correct
    /// and never updated.
    /// </summary>
    [Fact]
    public void TheLayoutStillSubscribesAndUnsubscribes()
    {
        var layout = Source("Layout/MainLayout.razor");

        Assert.Contains("Localizer.LanguageChanged += HandleLanguageChanged", layout, StringComparison.Ordinal);
        Assert.Contains("Localizer.LanguageChanged -= HandleLanguageChanged", layout, StringComparison.Ordinal);
        Assert.Contains(
            "private void HandleLanguageChanged() => this.DispatchRender(() => InvokeAsync(StateHasChanged));",
            layout,
            StringComparison.Ordinal);
    }

    // ---- Every localized component declares it ------------------------------

    /// <summary>
    /// The scan. A component that renders <c>Localizer.T</c> without the parameter is the defect,
    /// exactly, and it cannot be seen by reading that component — only by switching language with
    /// it on screen. So it is read off the source instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(LocalizedComponents))]
    public void EveryComponentThatRendersTranslatedTextDeclaresTheCascadingParameter(string relativePath)
        => Assert.True(
            Source(relativePath).Contains(Declaration, StringComparison.Ordinal),
            $"{relativePath} renders Localizer.T but does not declare {Declaration}. A language "
            + "switch will leave it in the old language until something unrelated repaints it. Add "
            + "the parameter, or add the file to LanguageCascadeTests.Exempt with the reason.");

    public static TheoryData<string> LocalizedComponents()
    {
        var data = new TheoryData<string>();
        foreach (var path in LocalizedRazorFiles().Where(p => !Exempt.ContainsKey(p)))
        {
            data.Add(path);
        }

        return data;
    }

    /// <summary>
    /// An allow-list entry that no longer describes anything is worse than no allow-list: it is a
    /// hole nobody is looking at. Every entry has to name a file that exists and still renders
    /// translated text, and carry a reason worth reading.
    /// </summary>
    [Fact]
    public void EveryExemptionStillDescribesSomething()
    {
        var localized = LocalizedRazorFiles().ToHashSet(StringComparer.Ordinal);

        Assert.All(Exempt, entry =>
        {
            Assert.True(localized.Contains(entry.Key),
                $"{entry.Key} is exempt from the Language cascade but no longer renders Localizer.T.");
            Assert.True(entry.Value.Length > 40, $"{entry.Key} needs a reason, not a note.");
        });
    }

    /// <summary>
    /// The cascade replaces the per-component subscriptions, so it has to actually replace them.
    /// Two paths to the same repaint means the next reader has to work out which one is load
    /// bearing, which is how the sidebar ended up with a subscription whose comment described the
    /// layout's.
    /// </summary>
    [Fact]
    public void NoComponentKeepsASubscriptionOfItsOwn()
    {
        foreach (var path in RazorFiles())
        {
            if (Exempt.ContainsKey(path)) continue;

            Assert.DoesNotContain("LanguageChanged +=", Source(path), StringComparison.Ordinal);
        }
    }

    // ---- The render harness -------------------------------------------------

    /// <summary>
    /// A renderer with nowhere to render to. It exists so the diff runs for real: the question
    /// here is what Blazor does with a retained child, and no amount of reading the source
    /// answers that.
    /// </summary>
    /// <remarks>
    /// BL0006 warns that RenderTree is not a public contract and may change between releases.
    /// Taken deliberately and kept to these five lines: a batch is what a renderer is handed, and
    /// this one throws it away. If a future .NET changes the shape, this file stops compiling —
    /// which is the loudest way for that to arrive, and it takes nothing shipped with it.
    /// </remarks>
#pragma warning disable BL0006
    private sealed class Harness : Renderer
    {
        public Harness()
            : base(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
        {
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task MountAsync(IComponent root)
            => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(AssignRootComponentId(root)));

        protected override void HandleException(Exception exception) => throw exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    /// <summary>MainLayout, reduced to the one thing it does here.</summary>
    private sealed class Root : ComponentBase
    {
        public string Tag = AppLanguages.English;

        public void Repaint() => StateHasChanged();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<string>>(0);
            builder.AddComponentParameter(1, "Name", "Language");
            builder.AddComponentParameter(2, "Value", Tag);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<Reader>(0);
                child.CloseComponent();
                child.OpenComponent<Deaf>(1);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>AccentColorCard after the fix: no parameters, one cascading parameter.</summary>
    private sealed class Reader : ComponentBase
    {
        internal static readonly List<string?> Renders = [];

        [CascadingParameter(Name = "Language")]
        public string? Language { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => Renders.Add(Language);
    }

    /// <summary>The same card before it: nothing tells it the language moved.</summary>
    private sealed class Deaf : ComponentBase
    {
        internal static readonly List<string?> Renders = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder) => Renders.Add("render");
    }

    // ---- Paths --------------------------------------------------------------

    private static Localizer New()
        => new(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

    private static string ComponentsRoot()
        => Path.Combine(TranslationParityTests.RepositoryRoot(), "RazorReaper", "Components");

    private static string Source(string relativePath)
        => File.ReadAllText(Path.Combine(ComponentsRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> RazorFiles()
        => Directory.EnumerateFiles(ComponentsRoot(), "*.razor", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(ComponentsRoot(), p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static IEnumerable<string> LocalizedRazorFiles()
        => RazorFiles().Where(p => Source(p).Contains("Localizer.T(", StringComparison.Ordinal));
}
