using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopPlus
{
    internal static class ShellFileNameMap
    {
        internal const string UnicodeFormat = "FileNameMapW";

        internal static byte[] BuildUnicodePayload(IReadOnlyList<string> fileNames)
        {
            ArgumentNullException.ThrowIfNull(fileNames);
            if (fileNames.Count == 0)
            {
                throw new ArgumentException("At least one file name is required.", nameof(fileNames));
            }

            var payload = new StringBuilder();
            foreach (string fileName in fileNames)
            {
                if (string.IsNullOrEmpty(fileName) || fileName.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException("File names must be non-empty and cannot contain null characters.", nameof(fileNames));
                }

                payload.Append(fileName);
                payload.Append('\0');
            }

            payload.Append('\0');
            return Encoding.Unicode.GetBytes(payload.ToString());
        }
    }
}
