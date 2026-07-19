using System;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Token generation and constant-time comparison for the companion bearer token.
    /// The token is the sole access boundary for the (whole-filesystem) API, so it is
    /// generated from a CSPRNG and compared without early-out timing leaks.
    /// </summary>
    internal static class CompanionAuth
    {
        public static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

        public static bool ConstantTimeEquals(string? provided, string? expected)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            {
                return false;
            }

            byte[] a = Encoding.UTF8.GetBytes(provided);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
