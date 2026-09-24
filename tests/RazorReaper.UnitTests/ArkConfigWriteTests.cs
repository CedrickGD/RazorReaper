using RazorReaper.Utilities;

namespace RazorReaper.UnitTests;

/// <summary>
/// Players mark GameUserSettings.ini read-only so ARK stops resetting it. Every write the app
/// makes to an ARK config goes through <see cref="ArkUtilities.WriteArkConfig"/>, which must
/// write anyway and hand the lock back afterwards (report #14: "Access denied" as admin).
/// </summary>
public sealed class ArkConfigWriteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rr-arkcfg-{Guid.NewGuid():N}.ini");

    public ArkConfigWriteTests() => File.WriteAllText(_path, "old");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.SetAttributes(_path, FileAttributes.Normal);
            File.Delete(_path);
        }
    }

    [Fact]
    public void WritesAReadOnlyFileAndKeepsItReadOnly()
    {
        new FileInfo(_path).IsReadOnly = true;

        ArkUtilities.WriteArkConfig(_path, () => File.WriteAllText(_path, "new"));

        Assert.Equal("new", File.ReadAllText(_path));
        Assert.True(new FileInfo(_path).IsReadOnly);
    }

    [Fact]
    public void WritesANormalFileWithoutMakingItReadOnly()
    {
        ArkUtilities.WriteArkConfig(_path, () => File.WriteAllText(_path, "new"));

        Assert.Equal("new", File.ReadAllText(_path));
        Assert.False(new FileInfo(_path).IsReadOnly);
    }

    [Fact]
    public void RestoringAReadOnlyBackupDoesNotLockANormalFile()
    {
        // A backup taken while the live file was locked is itself read-only, and File.Copy carries that over.
        var backup = _path + ".bak";
        File.WriteAllText(backup, "backup");
        new FileInfo(backup).IsReadOnly = true;
        try
        {
            ArkUtilities.WriteArkConfig(_path, () => File.Copy(backup, _path, overwrite: true));

            Assert.Equal("backup", File.ReadAllText(_path));
            Assert.False(new FileInfo(_path).IsReadOnly);
        }
        finally
        {
            File.SetAttributes(backup, FileAttributes.Normal);
            File.Delete(backup);
        }
    }

    [Fact]
    public void PutsTheReadOnlyFlagBackWhenTheWriteThrows()
    {
        new FileInfo(_path).IsReadOnly = true;

        Assert.Throws<IOException>(() =>
            ArkUtilities.WriteArkConfig(_path, () => throw new IOException("disk full")));

        Assert.True(new FileInfo(_path).IsReadOnly);
        Assert.Equal("old", File.ReadAllText(_path));
    }

    // File.Copy carries the read-only flag over: a backup of a locked config would itself be
    // locked, so the pruning could never delete it and the next session backup could not
    // overwrite it.
    [Fact]
    public void ABackupOfAReadOnlyFileIsWritable()
    {
        var backup = _path + ".bak";
        try
        {
            new FileInfo(_path).IsReadOnly = true;

            ArkUtilities.CopyToBackup(_path, backup, overwrite: false);

            Assert.Equal("old", File.ReadAllText(backup));
            Assert.False(new FileInfo(backup).IsReadOnly);
            Assert.True(new FileInfo(_path).IsReadOnly);
        }
        finally
        {
            if (File.Exists(backup)) { File.SetAttributes(backup, FileAttributes.Normal); File.Delete(backup); }
        }
    }

    [Fact]
    public void AReadOnlyBackupLeftByAnOlderBuildIsOverwritten()
    {
        var backup = _path + ".bak";
        try
        {
            File.WriteAllText(backup, "stale");
            new FileInfo(backup).IsReadOnly = true;

            ArkUtilities.CopyToBackup(_path, backup, overwrite: true);

            Assert.Equal("old", File.ReadAllText(backup));
            Assert.False(new FileInfo(backup).IsReadOnly);
        }
        finally
        {
            if (File.Exists(backup)) { File.SetAttributes(backup, FileAttributes.Normal); File.Delete(backup); }
        }
    }
}
