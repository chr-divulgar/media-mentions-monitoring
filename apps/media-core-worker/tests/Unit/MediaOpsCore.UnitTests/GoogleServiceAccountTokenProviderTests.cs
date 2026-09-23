using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class GoogleServiceAccountTokenProviderTests
{
    [Fact]
    public void NormalizePem_should_convert_literal_backslash_n_to_real_newlines()
    {
        // A .env file stores a multi-line PEM as one line with literal "\n" text — this is the
        // same unescaping any Node consumer of the exact same FIREBASE_PRIVATE_KEY value needs.
        var input = "-----BEGIN PRIVATE KEY-----\\nABC\\n-----END PRIVATE KEY-----\\n";

        var result = GoogleServiceAccountTokenProvider.NormalizePem(input);

        Assert.Equal("-----BEGIN PRIVATE KEY-----\nABC\n-----END PRIVATE KEY-----\n", result);
    }

    [Fact]
    public void BuildSignedJwt_should_produce_a_valid_RS256_jwt_with_the_expected_claims()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var issuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var jwt = GoogleServiceAccountTokenProvider.BuildSignedJwt(
            issuer: "test@example.iam.gserviceaccount.com",
            privateKeyPem: privateKeyPem,
            scope: "https://www.googleapis.com/auth/datastore",
            audience: "https://oauth2.googleapis.com/token",
            issuedAt: issuedAt);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecodeString(parts[0]));
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());

        var claims = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecodeString(parts[1]));
        Assert.Equal("test@example.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/datastore", claims.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
        Assert.Equal(issuedAt.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal(issuedAt.AddMinutes(1).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());

        // The signature must verify against the same key pair's public half.
        var signedPart = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecodeBytes(parts[2]);
        Assert.True(rsa.VerifyData(signedPart, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void BuildSignedJwt_should_fail_verification_against_a_different_key()
    {
        using var signingKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var privateKeyPem = signingKey.ExportPkcs8PrivateKeyPem();

        var jwt = GoogleServiceAccountTokenProvider.BuildSignedJwt(
            "issuer", privateKeyPem, "scope", "aud", DateTimeOffset.UtcNow);

        var parts = jwt.Split('.');
        var signedPart = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecodeBytes(parts[2]);

        Assert.False(otherKey.VerifyData(signedPart, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static byte[] Base64UrlDecodeBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlDecodeString(string value) => Encoding.UTF8.GetString(Base64UrlDecodeBytes(value));
}
