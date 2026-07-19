using System;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// DPAPI (CurrentUser) protection for companion secrets at rest — the bearer token and
    /// the TLS certificate's private key. Bound to the Windows user account, so copying the
    /// files to another machine/user yields nothing usable.
    /// </summary>
    internal static class CompanionDataProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopPlus.Companion.v1");

        public static string ProtectString(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plaintext);
                byte[] protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string UnprotectString(string? protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64))
            {
                return string.Empty;
            }

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                // Wrong user / corrupted / not actually protected: treat as no secret.
                return string.Empty;
            }
        }

        public static byte[] ProtectBytes(byte[] data) =>
            ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        public static byte[] UnprotectBytes(byte[] protectedData) =>
            ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}
