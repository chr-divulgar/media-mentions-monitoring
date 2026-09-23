using MediaOpsCore.Workers.Operations;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class DotEnvLoaderTests
{
    [Fact]
    public void ParseEnvFile_should_extract_simple_key_value_pairs()
    {
        var lines = new[]
        {
            "FIREBASE_BASE_URL=https://example-default-rtdb.firebaseio.com",
            "FIREBASE_PLATFORMS_PATH=platforms",
        };

        var result = DotEnvLoader.ParseEnvFile(lines);

        Assert.Equal("https://example-default-rtdb.firebaseio.com", result["FIREBASE_BASE_URL"]);
        Assert.Equal("platforms", result["FIREBASE_PLATFORMS_PATH"]);
    }

    [Fact]
    public void ParseEnvFile_should_skip_blank_lines_and_comments()
    {
        var lines = new[]
        {
            "# this is a comment",
            "",
            "   ",
            "KEY=value",
        };

        var result = DotEnvLoader.ParseEnvFile(lines);

        var pair = Assert.Single(result);
        Assert.Equal("KEY", pair.Key);
        Assert.Equal("value", pair.Value);
    }

    [Fact]
    public void ParseEnvFile_should_strip_surrounding_quotes_and_whitespace()
    {
        var lines = new[] { "  KEY  =   \"quoted value\"  " };

        var result = DotEnvLoader.ParseEnvFile(lines);

        Assert.Equal("quoted value", result["KEY"]);
    }

    [Fact]
    public void ParseEnvFile_should_ignore_lines_without_an_equals_sign_or_empty_key()
    {
        var lines = new[] { "not a valid line", "=missingkey" };

        var result = DotEnvLoader.ParseEnvFile(lines);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseEnvFile_should_keep_the_last_value_when_a_key_repeats()
    {
        var lines = new[] { "KEY=first", "KEY=second" };

        var result = DotEnvLoader.ParseEnvFile(lines);

        Assert.Equal("second", result["KEY"]);
    }
}
