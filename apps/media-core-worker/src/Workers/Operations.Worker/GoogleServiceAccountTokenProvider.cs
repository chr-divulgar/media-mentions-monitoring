using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Mints short-lived OAuth2 access tokens for a Google service account via the standard
/// "JWT Bearer Token" flow (https://developers.google.com/identity/protocols/oauth2/service-account),
/// using only the .NET base class library (RSA + System.Text.Json) — no Google SDK dependency,
/// matching this project's existing house style of plain HttpClient REST calls to Firebase
/// (see FirebaseAdapter.cs) rather than pulling in gRPC-based client libraries.
/// </summary>
public sealed class GoogleServiceAccountTokenProvider
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string FirestoreScope = "https://www.googleapis.com/auth/datastore";
    // Refresh this far ahead of real expiry so an in-flight request never races an expiring token.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly HttpClient httpClient;
    private readonly string clientEmail;
    private readonly string privateKeyPem;
    private readonly object syncRoot = new();
    private string? cachedToken;
    private DateTimeOffset cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public GoogleServiceAccountTokenProvider(HttpClient httpClient, string clientEmail, string privateKeyPem)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.clientEmail = clientEmail ?? throw new ArgumentNullException(nameof(clientEmail));
        this.privateKeyPem = NormalizePem(privateKeyPem ?? throw new ArgumentNullException(nameof(privateKeyPem)));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            if (cachedToken is not null && DateTimeOffset.UtcNow < cachedTokenExpiresAt - RefreshSkew)
            {
                return cachedToken;
            }
        }

        var jwt = BuildSignedJwt(clientEmail, privateKeyPem, FirestoreScope, TokenEndpoint, DateTimeOffset.UtcNow);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = jwt,
        });

        using var response = await httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body?.AccessToken))
        {
            throw new InvalidOperationException("Google token endpoint response did not include an access_token.");
        }

        lock (syncRoot)
        {
            cachedToken = body.AccessToken;
            cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 3600);
        }

        return body.AccessToken;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    // A .env file stores the PEM's line breaks as literal "\n" text rather than real newlines —
    // the same normalization any Node consumer of this same key already has to do.
    internal static string NormalizePem(string pem) => pem.Replace("\\n", "\n");

    internal static string BuildSignedJwt(string issuer, string privateKeyPem, string scope, string audience, DateTimeOffset issuedAt)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            scope,
            aud = audience,
            iat = issuedAt.ToUnixTimeSeconds(),
            exp = issuedAt.AddMinutes(1).ToUnixTimeSeconds(),
        }));
        var unsigned = $"{header}.{claims}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{unsigned}.{Base64UrlEncode(signature)}";
    }

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
