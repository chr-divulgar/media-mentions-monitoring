namespace MediaOpsCore.BuildingBlocks.Infrastructure;

public sealed class FirebaseAdapterOptions
{
    public FirebaseAdapterOptions(Uri baseUrl, string rootPath = "monitoringArtifacts", string? authToken = null)
    {
        BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        if (!BaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The Firebase base URL must be absolute.", nameof(baseUrl));
        }

        RootPath = string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(rootPath)) : rootPath.Trim('/');
        AuthToken = authToken;
    }

    public Uri BaseUrl { get; }

    public string RootPath { get; }

    public string? AuthToken { get; }
}