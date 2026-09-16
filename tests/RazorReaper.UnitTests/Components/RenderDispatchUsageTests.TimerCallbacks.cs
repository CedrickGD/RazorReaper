using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// The synchronous half of the timer sweep.
///
/// A <c>System.Threading.Timer</c> callback has no caller: on .NET 10 a throw escaping it is an
/// unhandled exception that terminates the process — measured on the machine this was written on,
/// exit code -1, no further tick. (<c>System.Timers.Timer</c> swallows a synchronous throw from
/// its Elapsed handler, so those are a diagnostics gap rather than a crash and are deliberately
/// not in scope here; an async lambda on either is caught by the scans in the main file.) The
/// same is true of a <c>ThreadPool.QueueUserWorkItem</c> callback, so those sites are held to
/// the same shape.
///
/// The async-lambda scans cannot see this family: a synchronous callback has no <c>async</c> to
/// match, and StretchedResService.OnRevertTick — RevertInternal, two notification calls, the
/// activity log and a subscriber invoke, none of it guarded — sailed through both of them. So
/// this test enumerates every construction site, follows the callback to the method that does
/// the work, and requires that method to own its faults: its body is nothing but
/// <c>try { … } catch (Exception) { … }</c>, or it does nothing but hand off to a method that is.
/// </summary>
public sealed partial class RenderDispatchUsageTests
{
    private readonly ITestOutputHelper output;

    public RenderDispatchUsageTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void EveryThreadingTimerCallbackOwnsItsFaults()
    {
        var sites = ProjectFiles()
            .SelectMany(path => PoolCallbackSites(path, File.ReadAllText(path)))
            .ToList();

        // The sweep, one line per site, so the proof is in the test output rather than in
        // someone's memory of having read the code.
        foreach (var site in sites)
        {
            output.WriteLine(site.ToString());
        }

        // A scan that finds nothing is scanning the wrong tree, not a clean one: the app has
        // eight System.Threading.Timer sites, plus the pool work items held to the same shape.
        Assert.NotEmpty(sites);
        Assert.Contains(sites, site => site.Kind == PoolCallbackKind.Timer);

        Assert.Empty(sites.Where(site => site.Verdict is not null).Select(site => site.ToString()));
    }

    /// <summary>
    /// The scan is only worth having if it fails on the shapes it exists to stop. Every case here
    /// is a callback that compiles, looks tidy, and takes the process down on its first throw.
    /// </summary>
    [Theory]
    // The OnRevertTick shape: a plain method with real work and no try at all.
    [InlineData("void Start() { _t = new Timer(_ => Tick(), null, 0, 1000); } void Tick() { Work(); }")]
    [InlineData("void Start() { _t = new System.Threading.Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { Work(); }")]
    // A try that does not start the body: the line before it is unguarded.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { Log(); try { Work(); } catch (Exception) { } }")]
    // A try that does not end the body.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (Exception) { } Cleanup(); }")]
    // Catching too little.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (IOException) { } }")]
    // A filter lets everything it excludes escape.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (Exception ex) when (ex is not OperationCanceledException) { } }")]
    // A catch-all that rethrows is not a guard.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (Exception) { Log(); throw; } }")]
    // The hand-off target is guarded, but the lambda does more than hand off.
    [InlineData("void Start() { _t = new Timer(_ => { Prepare(); Tick(); }, null, 0, 1000); } void Tick() { try { Work(); } catch { } }")]
    // A hand-off to something this file does not declare cannot be checked, so it does not pass.
    [InlineData("void Start() { _t = new Timer(_ => _service.Tick(), null, 0, 1000); }")]
    // The hand-off chain ends in an unguarded method.
    [InlineData("void Start() { _t = new Timer(_ => Flush(), null, 0, 1000); } void Flush() { _ = FlushAsync(); } async Task FlushAsync() { await Work(); }")]
    // An async lambda is the other test's business, but it must not pass here either.
    [InlineData("void Start() { _t = new Timer(async _ => await TickAsync(), null, 0, 1000); }")]
    // Pool work items are held to the same shape.
    [InlineData("void Raise() { ThreadPool.QueueUserWorkItem(_ => handler(), null); }")]
    [InlineData("void Raise() { ThreadPool.QueueUserWorkItem(static s => s.Run(), this, preferLocal: false); } void Run() { Work(); }")]
    public void TheTimerCallbackScanRejectsACallbackThatCanThrow(string source)
    {
        var sites = PoolCallbackSites("Sample.cs", source);

        Assert.NotEmpty(sites);
        Assert.Contains(sites, site => site.Verdict is not null);
    }

    [Theory]
    // Method group to a whole-body try/catch.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (Exception ex) { Log(ex); } }")]
    [InlineData("void Start() { _t = new System.Threading.Timer(OnTick, null, 0, 1000); } private void OnTick(object? _) { try { Work(); } catch (Exception) { } }")]
    // Lambda handing off to one.
    [InlineData("void Start() { _t = new Timer(_ => Tick(), null, 0, 1000); } void Tick() { try { Work(); } catch { } }")]
    // The AccessGate/License shape: a discarded Task from an async method that owns its faults.
    [InlineData("void Start() { _t = new Timer(state => { _ = PollAsync(); }, null, 0, 1000); } private async Task PollAsync() { try { await CheckAsync(); } catch (Exception ex) { Log(ex); } }")]
    // The App shape: a trampoline into a guarded async method.
    [InlineData("void Start() { _t = new Timer(_ => Flush(), null, 0, 1000); } private void Flush() { _ = FlushAsync(); } private async Task FlushAsync(CancellationToken token = default) { try { await Work(); } catch (Exception ex) { Log(ex); } }")]
    // A lambda whose own body is the guard.
    [InlineData("void Raise() { ThreadPool.QueueUserWorkItem(_ => { try { action(); } catch { } }); }")]
    // try/catch/finally, with the finally doing bookkeeping.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (Exception ex) { Log(ex); } finally { Interlocked.Exchange(ref _busy, 0); } }")]
    // Specific catches ahead of the catch-all.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { Work(); } catch (OperationCanceledException) { } catch (Exception ex) { Log(ex); } }")]
    // Nested blocks, locks and string braces inside the try do not confuse the matcher.
    [InlineData("void Start() { _t = new Timer(OnTick, null, 0, 1000); } void OnTick(object? s) { try { lock (_gate) { if (_stopped) { return; } } Work($\"{count} }}\"); } catch (Exception ex) { Log(ex, \"tick { failed\"); } }")]
    // The generic pool overload with a static lambda and a receiver chain.
    [InlineData("void Report(Sighting s) { ThreadPool.QueueUserWorkItem(static state => state.app.ReportOnPool(state.s), (app: this, s), preferLocal: false); } private void ReportOnPool(Sighting s) { try { Publish(s); } catch (Exception ex) { Log(ex); } }")]
    public void TheTimerCallbackScanAcceptsACallbackThatOwnsItsFaults(string source)
    {
        var sites = PoolCallbackSites("Sample.cs", source);

        Assert.NotEmpty(sites);
        Assert.All(sites, site => Assert.Null(site.Verdict));
    }

    [Fact]
    public void TheTimerCallbackScanLeavesSystemTimersTimerToTheFramework()
    {
        // Its Elapsed handler's synchronous throws are swallowed; there is nothing to crash.
        Assert.Empty(PoolCallbackSites("Sample.cs", "_t = new System.Timers.Timer(1000); _t.Elapsed += OnElapsed;"));
    }

    [Fact]
    public void TheTimerCallbackScanSeesThroughCommentsAndStrings()
    {
        const string Source = """""
            // new Timer(_ => Unguarded(), null, 0, 1) — prose, not a site
            /* new Timer(_ => Unguarded(), null, 0, 1) */
            var text = "new Timer(_ => Unguarded(), null, 0, 1)";
            var raw = """"new Timer(_ => Unguarded(), null, 0, 1)"""";
            var nested = $"{(open ? "}" : "{")}";
            var quote = '"'; var brace = '{'; var escaped = '\'';
            void Start() { _t = new Timer(OnTick, null, 0, 1); }
            void OnTick(object? s) { try { Work($"{count}"); } catch (Exception) { } }
            """"";

        var site = Assert.Single(PoolCallbackSites("Sample.cs", Source));

        Assert.Equal(7, site.Line);
        Assert.Null(site.Verdict);
    }

    [Fact]
    public void TheTimerCallbackScanReadsARazorFilesCodeBlockPastItsMarkup()
    {
        // Markup is full of apostrophes and quotes that are not C# literals; only @code is scanned.
        const string Source = """
            <p class="note">Don't panic — it's fine.</p>
            @code {
                private Timer? _t;
                void Start() { _t = new Timer(_ => Tick(), null, 0, 1); }
                void Tick() { try { Work(); } catch (Exception ex) { Logger.LogError(ex, "tick"); } }
            }
            """;

        var site = Assert.Single(PoolCallbackSites("Sample.razor", Source));

        Assert.Equal(4, site.Line);
        Assert.Null(site.Verdict);
    }

    // ─── The scan ──────────────────────────────────────────────────────────────────────────

    private enum PoolCallbackKind
    {
        Timer,
        ThreadPoolWorkItem
    }

    /// <summary>
    /// One construction site. <see cref="Verdict"/> is null when the callback owns its faults,
    /// and <see cref="Proof"/> then names the method whose whole-body try/catch is the guard,
    /// with the hand-off chain that led there.
    /// </summary>
    private sealed record PoolCallbackSite(
        string File,
        int Line,
        PoolCallbackKind Kind,
        string Callback,
        string? Verdict,
        string? Proof)
    {
        public override string ToString()
            => $"{File}:{Line}: {Kind} callback `{Callback}` — {Verdict ?? Proof}";
    }

    private static readonly Regex ThreadingTimerConstruction = new(
        @"\bnew\s+(?:System\.Threading\.)?Timer\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ThreadPoolWorkItem = new(
        @"\bThreadPool\s*\.\s*(?:Unsafe)?QueueUserWorkItem\s*(?:<[^<>()]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary><c>OnTick</c>, <c>this.OnTick</c>.</summary>
    private static readonly Regex MethodGroup = new(@"^(?:this\.)?([A-Za-z_]\w*)$", RegexOptions.Compiled);

    /// <summary><c>_ =&gt; …</c>, <c>state =&gt; …</c>, <c>(a, b) =&gt; …</c>, <c>static state =&gt; …</c>. Not <c>async</c>.</summary>
    private static readonly Regex Lambda = new(
        @"^(?:static\s+)?(?:[A-Za-z_]\w*|\([^()]*\))\s*=>\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// <c>[_ =] [receiver.]Name(args)</c> with arguments that make no call of their own — the
    /// only statement a hand-off may consist of.
    /// </summary>
    private static readonly Regex HandOff = new(
        @"^(?:_\s*=\s*)?(?:[A-Za-z_]\w*\s*(?:\.|\?\.)\s*)*([A-Za-z_]\w*)\s*\(([^()]*)\)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CatchClause = new(
        @"^catch\s*(?:\(\s*(?<type>[\w.]+)(?:\s+\w+)?\s*\))?\s*(?<when>when\b[^{]*)?\{",
        RegexOptions.Compiled);

    private static List<PoolCallbackSite> PoolCallbackSites(string path, string source)
    {
        var code = CodeOnly(source, razor: path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        var sites = new List<PoolCallbackSite>();

        foreach (var (regex, kind) in new[]
                 {
                     (ThreadingTimerConstruction, PoolCallbackKind.Timer),
                     (ThreadPoolWorkItem, PoolCallbackKind.ThreadPoolWorkItem)
                 })
        {
            foreach (Match match in regex.Matches(code))
            {
                var callback = FirstArgument(code, match.Index + match.Length - 1);
                var chain = new List<string>();
                var verdict = JudgeCallback(code, callback, chain);
                var proof = verdict is not null
                    ? null
                    : chain.Count == 0
                        ? "the lambda body is the guard"
                        : $"{string.Join(" -> ", chain)} owns its faults";

                sites.Add(new PoolCallbackSite(
                    Path.GetFileName(path),
                    LineOf(code, match.Index),
                    kind,
                    Squeeze(callback),
                    verdict,
                    proof));
            }
        }

        return sites;
    }

    private static string? JudgeCallback(string code, string callback, List<string> chain)
    {
        callback = callback.Trim();

        if (MethodGroup.Match(callback) is { Success: true } group)
        {
            return JudgeMethod(code, group.Groups[1].Value, chain);
        }

        if (Lambda.Match(callback) is { Success: true } lambda)
        {
            return JudgeBody(code, lambda.Groups[1].Value, chain);
        }

        return "not a method group or a synchronous lambda this scan understands";
    }

    /// <summary>A block or an expression: guarded whole, or a single hand-off to something that is.</summary>
    private static string? JudgeBody(string code, string body, List<string> chain)
    {
        body = body.Trim();

        if (!body.StartsWith('{'))
        {
            return JudgeHandOff(code, body.TrimEnd(';').Trim(), chain);
        }

        var close = MatchingBrace(body, 0);
        if (close != body.Length - 1)
        {
            return "the block does not end where the body ends";
        }

        var inner = body[1..close].Trim();
        if (IsGuardedBlock(inner, out var reason))
        {
            return null;
        }

        var statements = SplitStatements(inner);
        if (statements.Count == 1 && !statements[0].StartsWith("try", StringComparison.Ordinal))
        {
            return JudgeHandOff(code, statements[0], chain);
        }

        return reason;
    }

    private static string? JudgeHandOff(string code, string statement, List<string> chain)
    {
        var handOff = HandOff.Match(statement);
        return handOff.Success
            ? JudgeMethod(code, handOff.Groups[1].Value, chain)
            : $"does more than hand off to one method: `{Squeeze(statement)}`";
    }

    private static string? JudgeMethod(string code, string name, List<string> chain)
    {
        if (chain.Contains(name, StringComparer.Ordinal))
        {
            return $"the hand-off chain loops through {name}";
        }

        chain.Add(name);
        if (chain.Count > 4)
        {
            return $"the hand-off chain is deeper than four methods at {name}";
        }

        var declarations = Regex.Matches(code, $@"\b(?:void|Task|ValueTask)(?:<[^<>]*>)?\s+{Regex.Escape(name)}\s*\(");
        if (declarations.Count == 0)
        {
            return $"{name} is not declared as a void or Task method in this file, so its guard cannot be seen";
        }

        foreach (Match declaration in declarations)
        {
            var parameters = declaration.Index + declaration.Length - 1;
            var parametersEnd = MatchingParenthesis(code, parameters);
            if (parametersEnd < 0)
            {
                return $"{name}: could not read the parameter list";
            }

            var afterParameters = code[(parametersEnd + 1)..].TrimStart();
            string? verdict;
            if (afterParameters.StartsWith("=>", StringComparison.Ordinal))
            {
                var end = afterParameters.IndexOf(';');
                verdict = JudgeHandOff(code, afterParameters[2..(end < 0 ? afterParameters.Length : end)].Trim(), chain);
            }
            else if (afterParameters.StartsWith('{'))
            {
                var close = MatchingBrace(afterParameters, 0);
                verdict = close < 0
                    ? $"{name}: could not read the body"
                    : JudgeBody(code, afterParameters[..(close + 1)], chain);
            }
            else
            {
                verdict = $"{name}: neither a block nor an expression body";
            }

            if (verdict is not null)
            {
                return $"{name}: {verdict}";
            }
        }

        return null;
    }

    /// <summary>
    /// <c>try { … } catch … { … } [finally { … }]</c> and nothing else, where the last catch is
    /// a catch-all — bare, <c>(Exception)</c> or <c>(Exception ex)</c>, no <c>when</c> — that
    /// does not rethrow.
    /// </summary>
    private static bool IsGuardedBlock(string inner, out string reason)
    {
        inner = inner.Trim();
        if (!Regex.IsMatch(inner, @"^try\s*\{"))
        {
            reason = "the body does not begin with try";
            return false;
        }

        var tryClose = MatchingBrace(inner, inner.IndexOf('{'));
        if (tryClose < 0)
        {
            reason = "the try block is unbalanced";
            return false;
        }

        var rest = inner[(tryClose + 1)..].TrimStart();
        var catchAll = false;

        while (rest.StartsWith("catch", StringComparison.Ordinal))
        {
            var clause = CatchClause.Match(rest);
            if (!clause.Success)
            {
                reason = $"catch clause not understood: `{Squeeze(rest)}`";
                return false;
            }

            var open = clause.Index + clause.Length - 1;
            var close = MatchingBrace(rest, open);
            if (close < 0)
            {
                reason = "a catch block is unbalanced";
                return false;
            }

            var type = clause.Groups["type"].Value;
            if (clause.Groups["when"].Length == 0 && type is "" or "Exception" or "System.Exception")
            {
                if (Regex.IsMatch(rest[(open + 1)..close], @"\bthrow\b"))
                {
                    reason = "the catch-all rethrows";
                    return false;
                }

                catchAll = true;
            }

            rest = rest[(close + 1)..].TrimStart();
        }

        if (!catchAll)
        {
            reason = "no catch-all — a bare catch or catch (Exception) without a when filter";
            return false;
        }

        if (rest.StartsWith("finally", StringComparison.Ordinal))
        {
            var open = rest.IndexOf('{');
            var close = open < 0 ? -1 : MatchingBrace(rest, open);
            if (close < 0)
            {
                reason = "the finally block is unbalanced";
                return false;
            }

            if (Regex.IsMatch(rest[(open + 1)..close], @"\bthrow\b"))
            {
                reason = "the finally block throws";
                return false;
            }

            rest = rest[(close + 1)..].TrimStart();
        }

        if (rest.Length > 0)
        {
            reason = $"code after the try/catch is unguarded: `{Squeeze(rest)}`";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // ─── Text utilities ────────────────────────────────────────────────────────────────────

    /// <summary>The first argument of the call whose <c>(</c> sits at <paramref name="open"/>.</summary>
    private static string FirstArgument(string code, int open)
    {
        var depth = 0;
        for (var i = open + 1; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth == 0)
                    {
                        return code[(open + 1)..i].Trim();
                    }

                    depth--;
                    break;
                case ',' when depth == 0:
                    return code[(open + 1)..i].Trim();
            }
        }

        return code[(open + 1)..].Trim();
    }

    private static int MatchingBrace(string text, int open) => Matching(text, open, '{', '}');

    private static int MatchingParenthesis(string text, int open) => Matching(text, open, '(', ')');

    private static int Matching(string text, int open, char opener, char closer)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opener)
            {
                depth++;
            }
            else if (text[i] == closer && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> SplitStatements(string block)
    {
        var statements = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < block.Length; i++)
        {
            switch (block[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ';' when depth == 0:
                    Add(block[start..i]);
                    start = i + 1;
                    break;
            }
        }

        Add(block[start..]);
        return statements;

        void Add(string statement)
        {
            statement = statement.Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }
        }
    }

    private static string Squeeze(string text)
    {
        var squeezed = Regex.Replace(text, @"\s+", " ").Trim();
        return squeezed.Length <= 80 ? squeezed : squeezed[..77] + "…";
    }

    // ─── Masking ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The source with every comment and every string or character literal blanked, length and
    /// line breaks preserved, so braces, parentheses and the words the scan matches on can only
    /// be code. A Razor file is blanked up to its <c>@code</c> block: markup is full of quotes
    /// and apostrophes that are not literals, and timers live in code.
    /// </summary>
    private static string CodeOnly(string source, bool razor)
    {
        var chars = source.ToCharArray();
        var i = 0;

        if (razor)
        {
            var block = Regex.Match(source, @"@(?:code|functions)\s*\{");
            i = block.Success ? block.Index : chars.Length;
            Blank(chars, 0, i);
        }

        while (i < chars.Length)
        {
            var next = SkipNonCode(chars, i, razor);
            i = next > i ? next : i + 1;
        }

        return new string(chars);
    }

    /// <summary>Blanks the comment or literal starting at <paramref name="i"/> and returns the index after it, or <paramref name="i"/> when none starts there.</summary>
    private static int SkipNonCode(char[] chars, int i, bool razor)
    {
        var c = chars[i];
        var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

        if (c == '/' && next == '/')
        {
            return BlankThrough(chars, i, "\n", inclusive: false);
        }

        if (c == '/' && next == '*')
        {
            return BlankThrough(chars, i, "*/", inclusive: true);
        }

        if (razor && c == '@' && next == '*')
        {
            return BlankThrough(chars, i, "*@", inclusive: true);
        }

        if (c == '\'')
        {
            return SkipCharLiteral(chars, i);
        }

        if (c == '"' || ((c == '$' || c == '@') && StartsStringLiteral(chars, i)))
        {
            return SkipStringLiteral(chars, i);
        }

        return i;
    }

    private static bool StartsStringLiteral(char[] chars, int i)
    {
        while (i < chars.Length && chars[i] is '$' or '@')
        {
            i++;
        }

        return i < chars.Length && chars[i] == '"';
    }

    /// <summary>Regular, verbatim, interpolated and raw strings, with interpolation holes descended into.</summary>
    private static int SkipStringLiteral(char[] chars, int start)
    {
        var i = start;
        var verbatim = false;
        var interpolated = false;
        while (chars[i] is '$' or '@')
        {
            verbatim |= chars[i] == '@';
            interpolated |= chars[i] == '$';
            i++;
        }

        var quotes = 0;
        while (i + quotes < chars.Length && chars[i + quotes] == '"')
        {
            quotes++;
        }

        int end;
        if (quotes >= 3)
        {
            // Raw: closed by the next run of at least as many quotes.
            end = chars.Length;
            var run = 0;
            for (var k = i + quotes; k < chars.Length; k++)
            {
                run = chars[k] == '"' ? run + 1 : 0;
                if (run == quotes)
                {
                    end = k + 1;
                    break;
                }
            }
        }
        else if (quotes == 2)
        {
            end = i + 2;
        }
        else
        {
            end = chars.Length;
            for (var k = i + 1; k < chars.Length;)
            {
                var c = chars[k];
                if (!verbatim && c == '\\')
                {
                    k += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && k + 1 < chars.Length && chars[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }

                    end = k + 1;
                    break;
                }

                if (interpolated && c == '{')
                {
                    if (k + 1 < chars.Length && chars[k + 1] == '{')
                    {
                        k += 2;
                        continue;
                    }

                    k = SkipInterpolationHole(chars, k);
                    continue;
                }

                k++;
            }
        }

        Blank(chars, start, end);
        return end;
    }

    /// <summary>From the hole's <c>{</c> to just past its <c>}</c>, with nested literals handled.</summary>
    private static int SkipInterpolationHole(char[] chars, int open)
    {
        var depth = 0;
        for (var k = open; k < chars.Length;)
        {
            var c = chars[k];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                if (--depth == 0)
                {
                    return k + 1;
                }
            }
            else if (c == '\'')
            {
                k = SkipCharLiteral(chars, k);
                continue;
            }
            else if (c == '"' || ((c == '$' || c == '@') && StartsStringLiteral(chars, k)))
            {
                k = SkipStringLiteral(chars, k);
                continue;
            }

            k++;
        }

        return chars.Length;
    }

    /// <summary><c>'x'</c>, <c>'\''</c>, <c>'A'</c>; a lone apostrophe is left as it is.</summary>
    private static int SkipCharLiteral(char[] chars, int i)
    {
        if (i + 1 >= chars.Length)
        {
            return i + 1;
        }

        if (chars[i + 1] == '\\')
        {
            for (var k = i + 3; k < Math.Min(chars.Length, i + 12); k++)
            {
                if (chars[k] == '\'')
                {
                    Blank(chars, i, k + 1);
                    return k + 1;
                }
            }

            return i + 1;
        }

        if (i + 2 < chars.Length && chars[i + 2] == '\'')
        {
            Blank(chars, i, i + 3);
            return i + 3;
        }

        return i + 1;
    }

    private static int BlankThrough(char[] chars, int start, string terminator, bool inclusive)
    {
        var end = chars.Length;
        for (var k = start + 2; k + terminator.Length <= chars.Length; k++)
        {
            var matched = true;
            for (var t = 0; t < terminator.Length && matched; t++)
            {
                matched = chars[k + t] == terminator[t];
            }

            if (matched)
            {
                end = k + (inclusive ? terminator.Length : 0);
                break;
            }
        }

        Blank(chars, start, end);
        return end;
    }

    private static void Blank(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
        {
            if (chars[i] is not ('\n' or '\r'))
            {
                chars[i] = ' ';
            }
        }
    }
}
