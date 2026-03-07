using System;

namespace DesktopPlus
{
    public sealed class UpdateBackupInfo
    {
        public string ArchivePath { get; set; } = string.Empty;
        public string ArchiveFileName { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string TargetVersion { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public long FileSizeBytes { get; set; }
        public string SourceInstallDirectory { get; set; } = string.Empty;
        public string SourceExecutablePath { get; set; } = string.Empty;
        public bool ContainsAppSnapshot { get; set; }
        public bool ContainsSettingsSnapshot { get; set; }
        public bool ContainsCustomLanguages { get; set; }
        public bool ContainsAutoSortStorage { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CurrentVersion) &&
                    !string.IsNullOrWhiteSpace(TargetVersion))
                {
                    return $"{CurrentVersion} -> {TargetVersion}";
                }

                if (!string.IsNullOrWhiteSpace(CurrentVersion))
                {
                    return CurrentVersion;
                }

                return string.IsNullOrWhiteSpace(ArchiveFileName) ? "-" : ArchiveFileName;
            }
        }

        public string CreatedDisplay =>
            CreatedUtc == default
                ? "-"
                : CreatedUtc.ToLocalTime().ToString("g");

        public string FileSizeDisplay => FormatFileSize(FileSizeBytes);

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{value:0} {units[unitIndex]}"
                : $"{value:0.0} {units[unitIndex]}";
        }
    }
}
