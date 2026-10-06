using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Decides whether to trust the FTP server's TLS certificate. Pure, so the rule is tested without a server.
///
/// Servers are often reached by IP, or by a name the certificate was not issued for (a hosting
/// provider's own server name). Then .NET reports a name mismatch even though the certificate is
/// perfectly good. Switching validation off to get past that would also accept a forged certificate;
/// instead the operator names the host the certificate is issued for (archiveFtpCertificateHost) and
/// a mismatch is accepted only if the certificate really is valid for that name. Every other failure
/// — untrusted chain, expired, revoked, no certificate — is always rejected.
/// </summary>
internal static class FtpCertificateValidation
{
    public static bool ShouldAccept(SslPolicyErrors errors, X509Certificate2? certificate, string? expectedHost)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return errors == SslPolicyErrors.RemoteCertificateNameMismatch
            && certificate is not null
            && !string.IsNullOrWhiteSpace(expectedHost)
            && certificate.MatchesHostname(expectedHost.Trim());
    }
}
