using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Identity;

public sealed class ClientIdentityTests
{
    private const string InstallIdKey = "rr.telemetry.install_id";
    private const string CanonicalInstallId = "d85b1407-351d-4694-9392-03acc5870eb1";
    private const string CanonicalHardwareId = "5734B40BB3DF5517866D578B18438B61";

    [Fact]
    public void ClientIdentityReturnsCanonicalLegacyIdWithoutWriting()
    {
        var preferences = PreferencesWith(CanonicalInstallId);
        var hardware = new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD");
        var service = new ClientIdentityService(preferences, hardware);

        var identity = service.GetIdentity();

        Assert.Equal(CanonicalInstallId, identity.InstallId);
        Assert.Equal(CanonicalHardwareId, identity.HardwareId);
        Assert.Equal(0, preferences.SetCallCount);
        Assert.Equal(CanonicalInstallId, preferences.Peek(InstallIdKey));
    }

    [Fact]
    public void ClientIdentityCanonicalizesAlternateGuidInMemoryWithoutRewritingStoredBytes()
    {
        const string stored = "{D85B1407-351D-4694-9392-03ACC5870EB1}";
        var preferences = PreferencesWith(stored);
        var service = new ClientIdentityService(
            preferences,
            new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD"));

        var identity = service.GetIdentity();

        Assert.Equal(CanonicalInstallId, identity.InstallId);
        Assert.Equal(stored, preferences.Peek(InstallIdKey));
        Assert.Equal(0, preferences.SetCallCount);
    }

    [Fact]
    public void ClientIdentityAcceptsGuidEmptyWithoutWriting()
    {
        const string emptyGuid = "00000000-0000-0000-0000-000000000000";
        var preferences = PreferencesWith(emptyGuid);
        var service = new ClientIdentityService(
            preferences,
            new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD"));

        var identity = service.GetIdentity();

        Assert.Equal(emptyGuid, identity.InstallId);
        Assert.Equal(0, preferences.SetCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public void ClientIdentityReplacesInvalidLegacyIdWithOneCanonicalGuidWrite(string stored)
    {
        var preferences = PreferencesWith(stored);
        var service = new ClientIdentityService(
            preferences,
            new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD"));

        var identity = service.GetIdentity();

        Assert.True(Guid.TryParseExact(identity.InstallId, "D", out _));
        Assert.Equal(identity.InstallId.ToLowerInvariant(), identity.InstallId);
        Assert.Equal(1, preferences.SetCallCount);
        Assert.Equal(identity.InstallId, preferences.Peek(InstallIdKey));
    }

    [Fact]
    public void ClientIdentitySequentialCallsReturnSameRecordAndAcquireOnce()
    {
        var preferences = PreferencesWith(CanonicalInstallId);
        var hardware = new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD");
        var service = new ClientIdentityService(preferences, hardware);

        var first = service.GetIdentity();
        var second = service.GetIdentity();

        Assert.Same(first, second);
        Assert.Equal(1, preferences.GetCallCount);
        Assert.Equal(0, preferences.SetCallCount);
        Assert.Equal(1, hardware.CallCount);
    }

    [Fact]
    public async Task ClientIdentityConcurrentFirstCallsReturnOneRecordWriteOnceAndAcquireOnce()
    {
        const int callerCount = 8;
        var preferences = PreferencesWith("invalid");
        var hardware = new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD");
        var service = new ClientIdentityService(preferences, hardware);
        using var ready = new CountdownEvent(callerCount);
        using var release = new ManualResetEventSlim(false);

        var calls = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() =>
            {
                ready.Signal();
                release.Wait();
                return service.GetIdentity();
            }))
            .ToArray();

        var coordinated = ready.Wait(TimeSpan.FromSeconds(5));
        release.Set();
        Assert.True(coordinated);
        var identities = await Task.WhenAll(calls);

        var first = identities[0];
        Assert.All(identities, identity => Assert.Same(first, identity));
        Assert.Equal(1, preferences.GetCallCount);
        Assert.Equal(1, preferences.SetCallCount);
        Assert.Equal(1, hardware.CallCount);
    }

    [Fact]
    public void ClientIdentityConstructorPerformsNoPreferenceOrHardwareWork()
    {
        var preferences = PreferencesWith(CanonicalInstallId);
        var hardware = new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD");

        _ = new ClientIdentityService(preferences, hardware);

        Assert.Equal(0, preferences.GetCallCount);
        Assert.Equal(0, preferences.SetCallCount);
        Assert.Equal(0, hardware.CallCount);
    }

    [Fact]
    public void ClientIdentityCanBeObtainedWithoutConstructingTelemetry()
    {
        var preferences = PreferencesWith(CanonicalInstallId);
        var hardware = new RecordingRawHardwareIdentitySource("CPU-DISK-BOARD");

        var identity = new ClientIdentityService(preferences, hardware).GetIdentity();

        Assert.Equal(CanonicalInstallId, identity.InstallId);
        Assert.Equal(CanonicalHardwareId, identity.HardwareId);
    }

    [Theory]
    [InlineData("CPU-DISK-BOARD", "5734B40BB3DF5517866D578B18438B61")]
    [InlineData("MACHINE-GUID", "41CC1660BD817A5B8F2453926C38DFAB")]
    [InlineData("UNKNOWN_GUID", "2AF3AF4BC75E5D22CD66407BF9AB1C88")]
    public void ClientIdentityPreservesLegacyHardwareHashVectors(string rawHardwareId, string expected)
    {
        var service = new ClientIdentityService(
            PreferencesWith(CanonicalInstallId),
            new RecordingRawHardwareIdentitySource(rawHardwareId));

        var hardwareId = service.GetIdentity().HardwareId;

        Assert.Equal(expected, hardwareId);
        Assert.Equal(32, hardwareId.Length);
        Assert.Equal(hardwareId.ToUpperInvariant(), hardwareId);
    }

    [Fact]
    public void ClientIdentityWindowsSourceUsesFirstNonEmptyTrimmedValuesInLegacyOrder()
    {
        var queries = new List<string>();
        var machineGuidCalls = 0;
        var source = new WindowsRawHardwareIdentitySource(
            (wmiClass, property) =>
            {
                queries.Add($"{wmiClass}.{property}");
                return wmiClass switch
                {
                    "Win32_Processor" => [null, "  ", " CPU "],
                    "Win32_DiskDrive" => [" DISK ", "ignored"],
                    "Win32_BaseBoard" => ["BOARD"],
                    _ => [],
                };
            },
            () =>
            {
                machineGuidCalls++;
                return "unused";
            });
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        var identity = service.GetIdentity();

        Assert.Equal(CanonicalHardwareId, identity.HardwareId);
        Assert.Equal(
            [
                "Win32_Processor.ProcessorId",
                "Win32_DiskDrive.SerialNumber",
                "Win32_BaseBoard.SerialNumber",
            ],
            queries);
        Assert.Equal(0, machineGuidCalls);
    }

    [Fact]
    public void ClientIdentityWindowsSourceMapsIndividualQueryFailureToUnknownWithoutGuidFallback()
    {
        var machineGuidCalls = 0;
        var source = new WindowsRawHardwareIdentitySource(
            (wmiClass, _) => wmiClass switch
            {
                "Win32_Processor" => ["CPU"],
                "Win32_DiskDrive" => throw new InvalidOperationException("query failed"),
                "Win32_BaseBoard" => ["BOARD"],
                _ => [],
            },
            () =>
            {
                machineGuidCalls++;
                return "unused";
            });
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        var identity = service.GetIdentity();

        Assert.Equal("41DADCA9E163B0D52425C23492B162F8", identity.HardwareId);
        Assert.Equal(0, machineGuidCalls);
    }

    [Fact]
    public void ClientIdentityWindowsSourceUsesMachineGuidOnlyWhenEveryHardwareQueryIsUnknown()
    {
        var machineGuidCalls = 0;
        var source = new WindowsRawHardwareIdentitySource(
            (_, _) => [null, "  "],
            () =>
            {
                machineGuidCalls++;
                return "MACHINE-GUID";
            });
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        var identity = service.GetIdentity();

        Assert.Equal("41CC1660BD817A5B8F2453926C38DFAB", identity.HardwareId);
        Assert.Equal(1, machineGuidCalls);
    }

    [Theory]
    [InlineData(" MACHINE-GUID ", "8F0AD21DD5C9ECE3259C10357C4601EE")]
    [InlineData("", "E3B0C44298FC1C149AFBF4C8996FB924")]
    [InlineData("   ", "0AAD7DA77D2ED59C396C99A74E49F3A4")]
    [InlineData(null, "2AF3AF4BC75E5D22CD66407BF9AB1C88")]
    public void ClientIdentityWindowsSourcePreservesNonNullMachineGuidBytesVerbatim(
        string? machineGuid,
        string expected)
    {
        var source = new WindowsRawHardwareIdentitySource((_, _) => [], () => machineGuid);
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        var identity = service.GetIdentity();

        Assert.Equal(expected, identity.HardwareId);
    }

    [Fact]
    public void ClientIdentityWindowsSourceUsesUnknownGuidWhenMachineGuidReadThrows()
    {
        var source = new WindowsRawHardwareIdentitySource(
            (_, _) => [],
            () => throw new InvalidOperationException("registry unavailable"));
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        var identity = service.GetIdentity();

        Assert.Equal("2AF3AF4BC75E5D22CD66407BF9AB1C88", identity.HardwareId);
    }

    [Fact]
    public void ClientIdentityRetriesAfterTransientFirstAcquisitionFailure()
    {
        var attempts = 0;
        var source = new RecordingRawHardwareIdentitySource(() =>
            Interlocked.Increment(ref attempts) == 1
                ? throw new InvalidOperationException("transient")
                : "CPU-DISK-BOARD");
        var service = new ClientIdentityService(PreferencesWith(CanonicalInstallId), source);

        Assert.Throws<InvalidOperationException>(() => service.GetIdentity());
        var identity = service.GetIdentity();

        Assert.Equal(CanonicalHardwareId, identity.HardwareId);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public void ClientIdentityHwidAdapterIsLazyAndReturnsCentralizedHardwareId()
    {
        var identityService = new RecordingClientIdentityService(
            new ClientIdentity(CanonicalInstallId, CanonicalHardwareId));

        var adapter = new HwidService(identityService);

        Assert.Equal(0, identityService.CallCount);
        Assert.Equal(CanonicalHardwareId, adapter.GetHardwareId());
        Assert.Equal(1, identityService.CallCount);
    }

    private static FakePreferencesStore PreferencesWith(string installId)
    {
        var preferences = new FakePreferencesStore();
        preferences.Seed(InstallIdKey, installId);
        preferences.ResetCallCounts();
        return preferences;
    }

    private sealed class RecordingRawHardwareIdentitySource : IRawHardwareIdentitySource
    {
        private readonly Func<string> _getRawHardwareIdentity;
        private int _callCount;

        public RecordingRawHardwareIdentitySource(string rawHardwareIdentity)
            : this(() => rawHardwareIdentity)
        {
        }

        public RecordingRawHardwareIdentitySource(Func<string> getRawHardwareIdentity)
        {
            _getRawHardwareIdentity = getRawHardwareIdentity;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public string GetRawHardwareIdentity()
        {
            Interlocked.Increment(ref _callCount);
            return _getRawHardwareIdentity();
        }
    }

    private sealed class RecordingClientIdentityService(ClientIdentity identity) : IClientIdentityService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ClientIdentity GetIdentity()
        {
            Interlocked.Increment(ref _callCount);
            return identity;
        }
    }
}
