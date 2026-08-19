# App launch/privacy Task 1 working-tree review package

- Base/head commit: 17d9be955dfa6c07bc5da252a4496a08dd335201 (no commits; detached HEAD)
- Scope: Task 1 owned solution/App-project wiring and new unit-test harness only

## Status

```text
 M RazorReaper.sln
 M RazorReaper/RazorReaper.csproj
?? tests/RazorReaper.UnitTests/
```

## Tracked stat

```text
warning: in the working copy of 'RazorReaper.sln', CRLF will be replaced by LF the next time Git touches it
warning: in the working copy of 'RazorReaper/RazorReaper.csproj', CRLF will be replaced by LF the next time Git touches it
 RazorReaper.sln                | 11 +++++++++++
 RazorReaper/RazorReaper.csproj |  4 ++++
 2 files changed, 15 insertions(+)
```

## Tracked diff

```diff
warning: in the working copy of 'RazorReaper.sln', CRLF will be replaced by LF the next time Git touches it
warning: in the working copy of 'RazorReaper/RazorReaper.csproj', CRLF will be replaced by LF the next time Git touches it
diff --git a/RazorReaper.sln b/RazorReaper.sln
index de5d1b9..55a2f7b 100644
--- a/RazorReaper.sln
+++ b/RazorReaper.sln
@@ -1,26 +1,37 @@
 ﻿
 Microsoft Visual Studio Solution File, Format Version 12.00
 # Visual Studio Version 17
 VisualStudioVersion = 17.14.36127.28
 MinimumVisualStudioVersion = 10.0.40219.1
 Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RazorReaper", "RazorReaper\RazorReaper.csproj", "{98F8BDC3-0A97-489B-B480-405098882516}"
 EndProject
+Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tests", "tests", "{0AB3BF05-4346-4AA6-1389-037BE0695223}"
+EndProject
+Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RazorReaper.UnitTests", "tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj", "{7EA21417-6B58-482A-8893-BBB7435947A4}"
+EndProject
 Global
 	GlobalSection(SolutionConfigurationPlatforms) = preSolution
 		Debug|Any CPU = Debug|Any CPU
 		Release|Any CPU = Release|Any CPU
 	EndGlobalSection
 	GlobalSection(ProjectConfigurationPlatforms) = postSolution
 		{98F8BDC3-0A97-489B-B480-405098882516}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
 		{98F8BDC3-0A97-489B-B480-405098882516}.Debug|Any CPU.Build.0 = Debug|Any CPU
 		{98F8BDC3-0A97-489B-B480-405098882516}.Debug|Any CPU.Deploy.0 = Debug|Any CPU
 		{98F8BDC3-0A97-489B-B480-405098882516}.Release|Any CPU.ActiveCfg = Release|Any CPU
 		{98F8BDC3-0A97-489B-B480-405098882516}.Release|Any CPU.Build.0 = Release|Any CPU
+		{7EA21417-6B58-482A-8893-BBB7435947A4}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
+		{7EA21417-6B58-482A-8893-BBB7435947A4}.Debug|Any CPU.Build.0 = Debug|Any CPU
+		{7EA21417-6B58-482A-8893-BBB7435947A4}.Release|Any CPU.ActiveCfg = Release|Any CPU
+		{7EA21417-6B58-482A-8893-BBB7435947A4}.Release|Any CPU.Build.0 = Release|Any CPU
 	EndGlobalSection
 	GlobalSection(SolutionProperties) = preSolution
 		HideSolutionNode = FALSE
 	EndGlobalSection
+	GlobalSection(NestedProjects) = preSolution
+		{7EA21417-6B58-482A-8893-BBB7435947A4} = {0AB3BF05-4346-4AA6-1389-037BE0695223}
+	EndGlobalSection
 	GlobalSection(ExtensibilityGlobals) = postSolution
 		SolutionGuid = {71893371-CB08-4AAB-B1FB-AF815216091A}
 	EndGlobalSection
 EndGlobal
diff --git a/RazorReaper/RazorReaper.csproj b/RazorReaper/RazorReaper.csproj
index 14ee923..33f88fb 100644
--- a/RazorReaper/RazorReaper.csproj
+++ b/RazorReaper/RazorReaper.csproj
@@ -81,20 +81,24 @@
 		<PackageReference Include="SkiaSharp" Version="3.119.0" />
 		<PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="3.119.0" />
 		<!-- BC3/BC1/BC7 texture encoding for the Sky Changer (Sky Injector). MIT-licensed,
 		     replaces the hand-rolled numpy BC3 encoder in t1m's original Python script. -->
 		<PackageReference Include="BCnEncoder.Net" Version="2.3.0" />
 		<!-- Discord Rich Presence (Lachee, MIT). Pure C# over Discord IPC named pipes —
 		     no native deps. Powers the "Playing Razor Reaper" profile activity. -->
 		<PackageReference Include="DiscordRichPresence" Version="1.6.1.70" />
 	</ItemGroup>
 
+	<ItemGroup>
+		<InternalsVisibleTo Include="RazorReaper.UnitTests" />
+	</ItemGroup>
+
 	<ItemGroup>
 		<None Update="appsettings.json">
 			<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
 		</None>
 		<None Update="appsettings.local.json" Condition="Exists('appsettings.local.json')">
 			<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
 		</None>
 	</ItemGroup>
 
 	<!-- Built-in INI presets, embedded into the assembly so they ship as a
```

## New harness files

### tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj

```diff
warning: in the working copy of 'tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj', CRLF will be replaced by LF the next time Git touches it
diff --git "a/tests\\RazorReaper.UnitTests\\RazorReaper.UnitTests.csproj" "b/tests\\RazorReaper.UnitTests\\RazorReaper.UnitTests.csproj"
new file mode 100644
index 0000000..12b3b7d
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\RazorReaper.UnitTests.csproj"
@@ -0,0 +1,28 @@
+﻿<Project Sdk="Microsoft.NET.Sdk">
+
+  <PropertyGroup>
+    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
+    <ImplicitUsings>enable</ImplicitUsings>
+    <Nullable>enable</Nullable>
+    <IsPackable>false</IsPackable>
+    <IsTestProject>true</IsTestProject>
+  </PropertyGroup>
+
+  <ItemGroup>
+    <PackageReference Include="coverlet.collector" Version="6.0.2">
+      <PrivateAssets>all</PrivateAssets>
+      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
+    </PackageReference>
+    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
+    <PackageReference Include="xunit" Version="2.9.2" />
+    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
+      <PrivateAssets>all</PrivateAssets>
+      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
+    </PackageReference>
+  </ItemGroup>
+
+  <ItemGroup>
+    <ProjectReference Include="..\..\RazorReaper\RazorReaper.csproj" />
+  </ItemGroup>
+
+</Project>
```

### tests\RazorReaper.UnitTests\GlobalUsings.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\GlobalUsings.cs" "b/tests\\RazorReaper.UnitTests\\GlobalUsings.cs"
new file mode 100644
index 0000000..c802f44
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\GlobalUsings.cs"
@@ -0,0 +1 @@
+global using Xunit;
```

### tests\RazorReaper.UnitTests\SmokeTests.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\SmokeTests.cs" "b/tests\\RazorReaper.UnitTests\\SmokeTests.cs"
new file mode 100644
index 0000000..b1593e5
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\SmokeTests.cs"
@@ -0,0 +1,161 @@
+using System.Net;
+using RazorReaper.UnitTests.Infrastructure;
+
+namespace RazorReaper.UnitTests;
+
+public sealed class SmokeTests
+{
+    [Fact]
+    public void AppAssemblyLoads()
+    {
+        Assert.Equal("RazorReaper", typeof(MauiProgram).Assembly.GetName().Name);
+    }
+}
+
+public sealed class InfrastructureTests
+{
+    [Fact]
+    public void PreferencesGetReturnsStoredValue()
+    {
+        var store = new FakePreferencesStore();
+
+        store.Set("enabled", true);
+
+        Assert.True(Assert.IsType<bool>(store.Get("enabled")));
+    }
+
+    [Fact]
+    public void PreferencesGetReturnsProvidedDefaultForMissingKey()
+    {
+        var store = new FakePreferencesStore();
+
+        Assert.Equal("fallback", store.Get("missing", "fallback"));
+    }
+
+    [Fact]
+    public void PreferencesRemoveDeletesOnlyExistingValue()
+    {
+        var store = new FakePreferencesStore();
+        store.Set("enabled", true);
+
+        Assert.True(store.Remove("enabled"));
+        Assert.Null(store.Get("enabled"));
+        Assert.False(store.Remove("enabled"));
+    }
+
+    [Fact]
+    public void PreferencesClearDeletesAllValues()
+    {
+        var store = new FakePreferencesStore();
+        store.Set("first", 1);
+        store.Set("second", 2);
+
+        store.Clear();
+
+        Assert.Null(store.Get("first"));
+        Assert.Null(store.Get("second"));
+    }
+
+    [Fact]
+    public async Task OsLocationProviderReturnsProgrammedResult()
+    {
+        var expected = new object();
+        var provider = new FakeOsLocationProvider { Result = expected };
+
+        var actual = await provider.GetAsync();
+
+        Assert.Same(expected, actual);
+    }
+
+    [Fact]
+    public async Task OsLocationProviderRecordsEachCallToken()
+    {
+        using var source = new CancellationTokenSource();
+        var provider = new FakeOsLocationProvider();
+
+        await provider.GetAsync(source.Token);
+
+        Assert.Equal(source.Token, Assert.Single(provider.Calls));
+    }
+
+    [Fact]
+    public async Task HttpHandlerRecordsStableRequestSnapshot()
+    {
+        using var handler = new RecordingHttpMessageHandler();
+        using var invoker = new HttpMessageInvoker(handler);
+        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/privacy")
+        {
+            Content = new StringContent("payload"),
+        };
+
+        using var response = await invoker.SendAsync(request, CancellationToken.None);
+
+        var recorded = Assert.Single(handler.Requests);
+        Assert.Equal(HttpMethod.Post, recorded.Method);
+        Assert.Equal(new Uri("https://example.invalid/privacy"), recorded.Uri);
+        Assert.Equal("payload", recorded.Body);
+    }
+
+    [Fact]
+    public async Task HttpHandlerReturnsProgrammedResponse()
+    {
+        using var handler = new RecordingHttpMessageHandler
+        {
+            ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)),
+        };
+        using var invoker = new HttpMessageInvoker(handler);
+        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/privacy");
+
+        using var response = await invoker.SendAsync(request, CancellationToken.None);
+
+        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
+    }
+
+    [Fact]
+    public async Task HttpHandlerRejectsPreCanceledRequestWithoutRecordingIt()
+    {
+        using var source = new CancellationTokenSource();
+        source.Cancel();
+        using var handler = new RecordingHttpMessageHandler();
+        using var invoker = new HttpMessageInvoker(handler);
+        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/privacy");
+
+        await Assert.ThrowsAnyAsync<OperationCanceledException>(
+            () => invoker.SendAsync(request, source.Token));
+
+        Assert.Empty(handler.Requests);
+    }
+
+    [Fact]
+    public void ManualClockNormalizesInitialValueToUtc()
+    {
+        var localOffset = new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(2));
+
+        var timeProvider = new ManualTimeProvider(localOffset);
+
+        Assert.Equal(new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero), timeProvider.GetUtcNow());
+    }
+
+    [Fact]
+    public void ManualClockAdvanceMovesUtcClockForward()
+    {
+        var timeProvider = new ManualTimeProvider(
+            new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero));
+
+        timeProvider.Advance(TimeSpan.FromMinutes(5));
+
+        Assert.Equal(
+            new DateTimeOffset(2026, 8, 16, 18, 5, 0, TimeSpan.Zero),
+            timeProvider.GetUtcNow());
+    }
+
+    [Fact]
+    public void ManualClockRejectsBackwardAdvance()
+    {
+        var timeProvider = new ManualTimeProvider(
+            new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero));
+
+        Assert.Throws<ArgumentOutOfRangeException>(
+            () => timeProvider.Advance(TimeSpan.FromTicks(-1)));
+    }
+}
```

### tests\RazorReaper.UnitTests\Infrastructure\FakePreferencesStore.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\Infrastructure\\FakePreferencesStore.cs" "b/tests\\RazorReaper.UnitTests\\Infrastructure\\FakePreferencesStore.cs"
new file mode 100644
index 0000000..2901326
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\Infrastructure\\FakePreferencesStore.cs"
@@ -0,0 +1,26 @@
+namespace RazorReaper.UnitTests.Infrastructure;
+
+public sealed class FakePreferencesStore
+{
+    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
+
+    public object? Get(string key, object? defaultValue = null)
+    {
+        return _values.TryGetValue(key, out var value) ? value : defaultValue;
+    }
+
+    public void Set(string key, object? value)
+    {
+        _values[key] = value;
+    }
+
+    public bool Remove(string key)
+    {
+        return _values.Remove(key);
+    }
+
+    public void Clear()
+    {
+        _values.Clear();
+    }
+}
```

### tests\RazorReaper.UnitTests\Infrastructure\FakeOsLocationProvider.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\Infrastructure\\FakeOsLocationProvider.cs" "b/tests\\RazorReaper.UnitTests\\Infrastructure\\FakeOsLocationProvider.cs"
new file mode 100644
index 0000000..a9a825f
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\Infrastructure\\FakeOsLocationProvider.cs"
@@ -0,0 +1,17 @@
+namespace RazorReaper.UnitTests.Infrastructure;
+
+public sealed class FakeOsLocationProvider
+{
+    private readonly List<CancellationToken> _calls = [];
+
+    public object? Result { get; set; }
+
+    public IReadOnlyList<CancellationToken> Calls => _calls.ToArray();
+
+    public ValueTask<object?> GetAsync(CancellationToken cancellationToken = default)
+    {
+        cancellationToken.ThrowIfCancellationRequested();
+        _calls.Add(cancellationToken);
+        return ValueTask.FromResult(Result);
+    }
+}
```

### tests\RazorReaper.UnitTests\Infrastructure\RecordingHttpMessageHandler.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\Infrastructure\\RecordingHttpMessageHandler.cs" "b/tests\\RazorReaper.UnitTests\\Infrastructure\\RecordingHttpMessageHandler.cs"
new file mode 100644
index 0000000..772ea1f
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\Infrastructure\\RecordingHttpMessageHandler.cs"
@@ -0,0 +1,32 @@
+using System.Collections.Concurrent;
+using System.Net;
+
+namespace RazorReaper.UnitTests.Infrastructure;
+
+public sealed record RecordedHttpRequest(HttpMethod Method, Uri? Uri, string? Body);
+
+public sealed class RecordingHttpMessageHandler : HttpMessageHandler
+{
+    private readonly ConcurrentQueue<RecordedHttpRequest> _requests = new();
+
+    public IReadOnlyList<RecordedHttpRequest> Requests => _requests.ToArray();
+
+    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResponseFactory { get; set; }
+        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
+
+    protected override async Task<HttpResponseMessage> SendAsync(
+        HttpRequestMessage request,
+        CancellationToken cancellationToken)
+    {
+        cancellationToken.ThrowIfCancellationRequested();
+
+        var body = request.Content is null
+            ? null
+            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
+
+        cancellationToken.ThrowIfCancellationRequested();
+        _requests.Enqueue(new RecordedHttpRequest(request.Method, request.RequestUri, body));
+
+        return await ResponseFactory(request, cancellationToken).ConfigureAwait(false);
+    }
+}
```

### tests\RazorReaper.UnitTests\Infrastructure\ManualTimeProvider.cs

```diff
diff --git "a/tests\\RazorReaper.UnitTests\\Infrastructure\\ManualTimeProvider.cs" "b/tests\\RazorReaper.UnitTests\\Infrastructure\\ManualTimeProvider.cs"
new file mode 100644
index 0000000..d3175c7
--- /dev/null
+++ "b/tests\\RazorReaper.UnitTests\\Infrastructure\\ManualTimeProvider.cs"
@@ -0,0 +1,33 @@
+namespace RazorReaper.UnitTests.Infrastructure;
+
+public sealed class ManualTimeProvider : TimeProvider
+{
+    private readonly object _sync = new();
+    private DateTimeOffset _utcNow;
+
+    public ManualTimeProvider(DateTimeOffset initialTime)
+    {
+        _utcNow = initialTime.ToUniversalTime();
+    }
+
+    public override DateTimeOffset GetUtcNow()
+    {
+        lock (_sync)
+        {
+            return _utcNow;
+        }
+    }
+
+    public void Advance(TimeSpan amount)
+    {
+        if (amount < TimeSpan.Zero)
+        {
+            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Time cannot move backward.");
+        }
+
+        lock (_sync)
+        {
+            _utcNow = _utcNow.Add(amount);
+        }
+    }
+}
```

