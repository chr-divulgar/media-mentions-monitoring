using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class FtpCertificateValidationTests
{
    private static X509Certificate2 CertificateFor(params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void A_fully_valid_certificate_is_accepted_without_any_configuration()
    {
        Assert.True(FtpCertificateValidation.ShouldAccept(SslPolicyErrors.None, certificate: null, expectedHost: null));
    }

    [Fact]
    public void A_name_mismatch_is_accepted_when_the_certificate_is_issued_for_the_configured_host()
    {
        // Connecting by IP: the chain is trusted, only the name differs from what was dialled.
        using var certificate = CertificateFor("server2.example.org", "mail.server2.example.org");

        Assert.True(FtpCertificateValidation.ShouldAccept(
            SslPolicyErrors.RemoteCertificateNameMismatch, certificate, "server2.example.org"));
    }

    [Fact]
    public void A_name_mismatch_is_rejected_when_the_certificate_is_not_issued_for_the_configured_host()
    {
        using var certificate = CertificateFor("someone-else.example.org");

        Assert.False(FtpCertificateValidation.ShouldAccept(
            SslPolicyErrors.RemoteCertificateNameMismatch, certificate, "server2.example.org"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_name_mismatch_is_rejected_when_no_host_is_configured(string? expectedHost)
    {
        using var certificate = CertificateFor("server2.example.org");

        Assert.False(FtpCertificateValidation.ShouldAccept(
            SslPolicyErrors.RemoteCertificateNameMismatch, certificate, expectedHost));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void Anything_beyond_a_name_mismatch_is_rejected_even_if_the_name_matches(SslPolicyErrors errors)
    {
        // An untrusted, expired or revoked certificate must never pass just because its name is right.
        using var certificate = CertificateFor("server2.example.org");

        Assert.False(FtpCertificateValidation.ShouldAccept(errors, certificate, "server2.example.org"));
    }
}
