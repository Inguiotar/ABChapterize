// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsConnection"/>: the three address spellings it accepts, the port rule
/// that separates them, where an omitted value is filled in from, and the refusals.
/// </summary>
/// <remarks>
/// The environment is process-global, so every test here clears all four variables first and puts
/// them back afterwards. Safe because the assembly runs its tests one at a time (see
/// <c>AssemblyInfo.cs</c>), and necessary because a developer machine with
/// <c>ABCHAPTERIZE_ABS_URL</c> exported would otherwise pass tests that should fail.
/// </remarks>
public sealed class AbsConnectionTests : IDisposable
{
    private static readonly string[] Variables =
    [
        AbsConnection.UrlVariable, AbsConnection.KeyVariable,
        AbsConnection.UserVariable, AbsConnection.PasswordVariable,
    ];

    private readonly Dictionary<string, string?> _saved = [];

    /// <summary>Takes the four variables out of the environment, remembering what was there.</summary>
    public AbsConnectionTests()
    {
        foreach (var variable in Variables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>Puts the environment back exactly as it was.</summary>
    public void Dispose()
    {
        foreach (var (variable, value) in _saved)
            Environment.SetEnvironmentVariable(variable, value);
    }

    [Theory]
    // A scheme spelled out is taken at its word, port and all.
    [InlineData("http://host:13378", "http", "host", 13378)]
    [InlineData("https://books.example.com", "https", "books.example.com", 443)]
    [InlineData("http://books.example.com", "http", "books.example.com", 80)]
    // Without one, http is assumed - and only then does Audiobookshelf's own port stand in.
    [InlineData("192.168.1.10:30067", "http", "192.168.1.10", 30067)]
    [InlineData("books.example.com", "http", "books.example.com", AbsConnection.DefaultPort)]
    [InlineData("  books.example.com  ", "http", "books.example.com", AbsConnection.DefaultPort)]
    public void ParseUrl_AcceptsTheThreeSpellings(string value, string scheme, string host, int port)
    {
        var uri = AbsConnection.ParseUrl(value, "--abs-url");
        Assert.Equal(scheme, uri.Scheme);
        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
    }

    [Fact]
    public void ParseUrl_KeepsAReverseProxySubPath()
    {
        var connection = new AbsConnection(
            AbsConnection.ParseUrl("https://example.com/audiobookshelf/", "--abs-url"), "k", null, null);
        Assert.Equal("https://example.com/audiobookshelf", connection.Root);
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("file:///books")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseUrl_RefusesWhatIsNotAnHttpServer(string value)
        => Assert.Throws<CliError>(() => AbsConnection.ParseUrl(value, "--abs-url"));

    [Fact]
    public void Resolve_TakesTheCommandLineOverTheEnvironment()
    {
        Environment.SetEnvironmentVariable(AbsConnection.UrlVariable, "from-env");
        Environment.SetEnvironmentVariable(AbsConnection.KeyVariable, "env-key");

        var connection = AbsConnection.Resolve("typed:9", "typed-key", null, null);

        Assert.Equal("typed", connection.BaseUri.Host);
        Assert.Equal("typed-key", connection.ApiKey);
    }

    [Fact]
    public void Resolve_FillsInEachValueSeparately()
    {
        // The ordinary shape: server and key exported once, the account named on the odd command.
        Environment.SetEnvironmentVariable(AbsConnection.UrlVariable, "server:9");
        Environment.SetEnvironmentVariable(AbsConnection.PasswordVariable, "secret");

        var connection = AbsConnection.Resolve(null, null, "reader", null);

        Assert.Equal("server", connection.BaseUri.Host);
        Assert.Equal("reader", connection.Username);
        Assert.Equal("secret", connection.Password);
        Assert.Null(connection.ApiKey);
    }

    [Fact]
    public void Resolve_WithoutAServer_Refuses()
        => Assert.Contains(
            AbsConnection.UrlVariable,
            Assert.Throws<CliError>(() => AbsConnection.Resolve(null, "key", null, null)).Message);

    [Fact]
    public void Resolve_WithoutACredential_Refuses()
        => Assert.Throws<CliError>(() => AbsConnection.Resolve("host", null, null, null));

    [Fact]
    public void Resolve_WithBothKindsOfCredential_Refuses()
        => Assert.Throws<CliError>(() => AbsConnection.Resolve("host", "key", "user", "pw"));

    [Fact]
    public void Resolve_WithAUserAndNoPassword_Refuses()
        => Assert.Throws<CliError>(() => AbsConnection.Resolve("host", null, "user", null));

    [Fact]
    public void Describe_NamesTheServerAndNeverTheSecret()
    {
        var withKey = AbsConnection.Resolve("host:9", "super-secret-key", null, null);
        Assert.Equal("http://host:9 (API key)", withKey.Describe);
        Assert.DoesNotContain("super-secret-key", withKey.Describe);

        var withLogin = AbsConnection.Resolve("host:9", null, "reader", "hunter2");
        Assert.Equal("http://host:9 (as reader)", withLogin.Describe);
        Assert.DoesNotContain("hunter2", withLogin.Describe);
    }

    /// <summary>
    /// The record's generated <c>ToString</c> would print every member, password included, and
    /// this type is public, reflected over by <c>FolderConfig</c> and printed by the connection
    /// note - so the override is a guard rather than a nicety.
    /// </summary>
    [Fact]
    public void ToString_IsTheRedactedForm()
    {
        var connection = AbsConnection.Resolve("host:9", null, "reader", "hunter2");
        Assert.DoesNotContain("hunter2", connection.ToString());
        Assert.Equal(connection.Describe, connection.ToString());
        Assert.DoesNotContain("hunter2", $"{connection}");
    }
}
