using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Creates and persists the self-signed TLS certificate used by the companion server.
    /// The PFX (incl. private key) is encrypted at rest with DPAPI. Persisting it means the
    /// phone only has to accept the certificate warning once instead of on every PC restart.
    /// </summary>
    internal static class CompanionCertificate
    {
        private static readonly string CertPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus_companion.pfx");

        public static X509Certificate2 LoadOrCreate()
        {
            try
            {
                if (File.Exists(CertPath))
                {
                    var existing = LoadExisting(CertPath);
                    if (existing != null && existing.NotAfter > DateTime.Now.AddDays(7))
                    {
                        return existing;
                    }
                }
            }
            catch
            {
                // Missing/corrupt: fall through and regenerate.
            }

            var created = Create();
            TrySave(created);
            return created;
        }

        private static X509Certificate2? LoadExisting(string path)
        {
            byte[] fileBytes = File.ReadAllBytes(path);

            // Current format: DPAPI-protected PFX.
            try
            {
                byte[] pfx = CompanionDataProtector.UnprotectBytes(fileBytes);
                return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
            }
            catch
            {
            }

            // Legacy format: raw PFX written before encryption — load, then upgrade in place.
            try
            {
                var legacy = new X509Certificate2(fileBytes, (string?)null, X509KeyStorageFlags.Exportable);
                TrySave(legacy);
                return legacy;
            }
            catch
            {
                return null;
            }
        }

        private static void TrySave(X509Certificate2 certificate)
        {
            try
            {
                byte[] pfx = certificate.Export(X509ContentType.Pfx);
                File.WriteAllBytes(CertPath, CompanionDataProtector.ProtectBytes(pfx));
            }
            catch
            {
                // Non-fatal: we can still run this session with the in-memory certificate.
            }
        }

        private static X509Certificate2 Create()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=DesktopPlus Companion",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
                    false));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            foreach (var ip in CompanionNetwork.GetLocalIPv4Addresses())
            {
                try
                {
                    sanBuilder.AddIpAddress(ip);
                }
                catch
                {
                }
            }
            request.CertificateExtensions.Add(sanBuilder.Build());

            using var selfSigned = request.CreateSelfSigned(
                DateTimeOffset.Now.AddDays(-1),
                DateTimeOffset.Now.AddYears(5));

            // Round-trip through PFX so the private key is associated in a form Schannel/Kestrel
            // can use on Windows (the ephemeral key from CreateSelfSigned otherwise fails TLS).
            return new X509Certificate2(
                selfSigned.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.Exportable);
        }
    }
}
