namespace MediaOpsCore.Workers.Operations;

using MediaOpsCore.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Validates YouTube cookies file format and content.
/// </summary>
public interface IYouTubeCookiesValidator
{
    /// <summary>
    /// Validate cookies file format (Netscape format).
    /// </summary>
    Task<CookiesValidationResult> ValidateCookiesAsync(string? cookiesFilePath, string? cookiesContent = null);
    
    /// <summary>
    /// Test if yt-dlp can use the cookies successfully.
    /// </summary>
    Task<bool> TestCookiesWithYtdlpAsync(string cookiesFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validation result for YouTube cookies.
/// </summary>
public sealed record CookiesValidationResult(
    bool IsValid,
    bool FileExists,
    int? CookieCount,
    DateTime? EarliestExpiration,
    bool HasYouTubeDomain,
    string? Message);

/// <summary>
/// Validates YouTube cookies in Netscape format.
/// </summary>
public sealed class YouTubeCookiesValidator : IYouTubeCookiesValidator
{
    private readonly IYtdlpBinaryProvider _ytdlpProvider;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<YouTubeCookiesValidator> _logger;

    public YouTubeCookiesValidator(
        IYtdlpBinaryProvider ytdlpProvider,
        IProcessRunner processRunner,
        ILogger<YouTubeCookiesValidator> logger)
    {
        _ytdlpProvider = ytdlpProvider;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Validate Netscape format cookies file.
    /// </summary>
    public async Task<CookiesValidationResult> ValidateCookiesAsync(
        string? cookiesFilePath,
        string? cookiesContent = null)
    {
        // Read file if path provided, otherwise use content
        string? content = cookiesContent;
        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(cookiesFilePath))
        {
            if (!File.Exists(cookiesFilePath))
            {
                return new CookiesValidationResult(
                    IsValid: false,
                    FileExists: false,
                    CookieCount: null,
                    EarliestExpiration: null,
                    HasYouTubeDomain: false,
                    Message: $"Cookies file not found: {cookiesFilePath}");
            }

            try
            {
                content = await File.ReadAllTextAsync(cookiesFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[YouTubeCookiesValidator] Failed to read cookies file");
                return new CookiesValidationResult(
                    IsValid: false,
                    FileExists: true,
                    CookieCount: null,
                    EarliestExpiration: null,
                    HasYouTubeDomain: false,
                    Message: $"Error reading cookies file: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new CookiesValidationResult(
                IsValid: false,
                FileExists: File.Exists(cookiesFilePath),
                CookieCount: 0,
                EarliestExpiration: null,
                HasYouTubeDomain: false,
                Message: "Cookies content is empty");
        }

        // Validate Netscape format
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Check for Netscape header
        if (!lines.Any(l => l.StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("[YouTubeCookiesValidator] Missing Netscape HTTP Cookie File header");
            return new CookiesValidationResult(
                IsValid: false,
                FileExists: File.Exists(cookiesFilePath),
                CookieCount: 0,
                EarliestExpiration: null,
                HasYouTubeDomain: false,
                Message: "Missing Netscape HTTP Cookie File header");
        }

        // Parse cookies (skip comments and empty lines)
        var cookies = new List<NetscapeCookie>();
        var hasYouTubeDomain = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 7)
                continue;

            try
            {
                var domain = parts[0];
                var expiration = long.Parse(parts[4]);
                var name = parts[5];

                cookies.Add(new NetscapeCookie(domain, expiration, name));

                if (domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
                    hasYouTubeDomain = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[YouTubeCookiesValidator] Failed to parse cookie line: {Line}", line);
            }
        }

        if (cookies.Count == 0)
        {
            return new CookiesValidationResult(
                IsValid: false,
                FileExists: File.Exists(cookiesFilePath),
                CookieCount: 0,
                EarliestExpiration: null,
                HasYouTubeDomain: false,
                Message: "No valid cookies found in file");
        }

        if (!hasYouTubeDomain)
        {
            _logger.LogWarning("[YouTubeCookiesValidator] No cookies for youtube.com domain found");
            return new CookiesValidationResult(
                IsValid: false,
                FileExists: File.Exists(cookiesFilePath),
                CookieCount: cookies.Count,
                EarliestExpiration: UnixTimeStampToDateTime(cookies.Min(c => c.Expiration)),
                HasYouTubeDomain: false,
                Message: "No cookies for youtube.com domain");
        }

        var earliestExp = UnixTimeStampToDateTime(cookies.Min(c => c.Expiration));
        var now = DateTime.UtcNow;
        
        if (earliestExp < now)
        {
            _logger.LogWarning("[YouTubeCookiesValidator] Cookies expired at {ExpTime}", earliestExp);
            return new CookiesValidationResult(
                IsValid: false,
                FileExists: File.Exists(cookiesFilePath),
                CookieCount: cookies.Count,
                EarliestExpiration: earliestExp,
                HasYouTubeDomain: true,
                Message: $"Cookies expired at {earliestExp:O}");
        }

        _logger.LogInformation(
            "[YouTubeCookiesValidator] Cookies valid: {Count} cookies, YouTube domain present, expires {ExpTime}",
            cookies.Count, earliestExp);

        return new CookiesValidationResult(
            IsValid: true,
            FileExists: File.Exists(cookiesFilePath),
            CookieCount: cookies.Count,
            EarliestExpiration: earliestExp,
            HasYouTubeDomain: true,
            Message: $"Valid: {cookies.Count} cookies, YouTube domain, expires {earliestExp:O}");
    }

    /// <summary>
    /// Test if yt-dlp can successfully use the cookies.
    /// </summary>
    public async Task<bool> TestCookiesWithYtdlpAsync(
        string cookiesFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cookiesFilePath))
        {
            _logger.LogWarning("[YouTubeCookiesValidator] Cookies file not found for yt-dlp test: {Path}", cookiesFilePath);
            return false;
        }

        try
        {
            var (cmd, baseArgs) = await _ytdlpProvider.GetCommandAsync(cancellationToken);
            var args = new List<string>(baseArgs)
            {
                "--cookies", cookiesFilePath,
                "--get-url",
                "--format", "best",
                "--quiet",
                "--no-warnings",
                "https://www.youtube.com/watch?v=jNQXAC9IVRw"  // "Me at the zoo" — always accessible
            };

            var command = new ProcessCommand(
                cmd,
                args,
                timeout: TimeSpan.FromSeconds(30));

            var result = await _processRunner.RunAsync(command, cancellationToken);

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                _logger.LogInformation("[YouTubeCookiesValidator] yt-dlp test succeeded with provided cookies");
                return true;
            }

            _logger.LogWarning(
                "[YouTubeCookiesValidator] yt-dlp test failed. ExitCode={ExitCode}, Stderr={Stderr}",
                result.ExitCode, result.StandardError);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTubeCookiesValidator] Exception during yt-dlp test");
            return false;
        }
    }

    private static DateTime UnixTimeStampToDateTime(long timestamp)
    {
        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(timestamp);
        return dateTime;
    }

    private sealed record NetscapeCookie(string Domain, long Expiration, string Name);
}
