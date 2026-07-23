using System;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class UpdateBackupInfoTests
{
    [Fact]
    public void DisplayName_PrefersManagedBackupName()
    {
        var backup = new UpdateBackupInfo
        {
            CustomDisplayName = "State before auto-sort",
            CurrentVersion = "1.5.3",
            TargetVersion = "1.6.0"
        };

        Assert.Equal("State before auto-sort", backup.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToVersionTransitionForLegacyBackup()
    {
        var backup = new UpdateBackupInfo
        {
            CurrentVersion = "1.5.3",
            TargetVersion = "1.6.0"
        };

        Assert.Equal("1.5.3 -> 1.6.0", backup.DisplayName);
    }

    [Fact]
    public void PresentationProperties_FormatDateAndSize()
    {
        var backup = new UpdateBackupInfo
        {
            CreatedUtc = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc),
            FileSizeBytes = 1536
        };

        Assert.NotEqual("-", backup.CreatedDisplay);
        Assert.EndsWith(" KB", backup.FileSizeDisplay);
        Assert.StartsWith("1", backup.FileSizeDisplay);
    }
}
