// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;

namespace ABChapterize.Abs;

/// <summary>
/// Which Audiobookshelf server a run talks to, and as whom: the resolved base address plus either
/// an API key or a username and password.
/// </summary>
/// <param name="BaseUri">Absolute http/https address of the server, port included.</param>
/// <param name="ApiKey">The API key to authenticate with, or null when logging in instead.</param>
/// <param name="Username">The account to log in as, or null when an API key is used.</param>
/// <param name="Password">That password, or null when an API key is used.</param>
/// <remarks>
/// <see cref="ToString"/> is overridden rather than left to the record's generated one, which
/// prints every member: a connection reaching a log line, an exception message or a debug header
/// would otherwise take the password with it. Everything that names the server prints
/// <see cref="Describe"/>, and no member renders the secret at all.
/// </remarks>
public sealed record AbsConnection(Uri BaseUri, string? ApiKey, string? Username, string? Password)
{
    /// <summary>Environment variable holding the server address, read when <c>--abs-url</c> is absent.</summary>
    public const string UrlVariable = "ABCHAPTERIZE_ABS_URL";

    /// <summary>Environment variable holding the API key, read when <c>--abs-key</c> is absent.</summary>
    public const string KeyVariable = "ABCHAPTERIZE_ABS_KEY";

    /// <summary>Environment variable holding the account name, read when <c>--abs-user</c> is absent.</summary>
    public const string UserVariable = "ABCHAPTERIZE_ABS_USER";

    /// <summary>Environment variable holding the password, read when <c>--abs-password</c> is absent.</summary>
    public const string PasswordVariable = "ABCHAPTERIZE_ABS_PASSWORD";

    /// <summary>
    /// The port assumed for an address given without one and without a scheme - Audiobookshelf's
    /// own default. A scheme spelled out means that scheme's default port instead, since somebody
    /// who writes <c>https://books.example.com</c> is describing a reverse proxy on 443, not a
    /// direct server on 13378.
    /// </summary>
    public const int DefaultPort = 13378;

    /// <summary>How the server and the chosen authentication are named in logs and error
    /// messages. Never includes the key or the password.</summary>
    public string Describe
        => Root + (ApiKey != null ? " (API key)" : $" (as {Username})");

    /// <summary>
    /// The prefix every request path is appended to: the base address including any reverse-proxy
    /// sub-path, without a trailing separator.
    /// </summary>
    public string Root => BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>The redacted <see cref="Describe"/> form; see the type remarks for why this is
    /// not the record default.</summary>
    public override string ToString() => Describe;

    /// <summary>
    /// Turns what the user typed into an absolute address, accepting the three spellings people
    /// actually have to hand: a full URL, <c>host:port</c>, or a bare host name.
    /// </summary>
    /// <param name="value">The raw option or environment-variable value.</param>
    /// <param name="source">What to name in an error message - the option, or the variable.</param>
    /// <returns>An absolute http or https URI.</returns>
    /// <exception cref="CliError">Thrown for an unparseable address or a scheme other than http(s).</exception>
    public static Uri ParseUrl(string value, string source)
    {
        var text = value.Trim();
        if (text.Length == 0)
            throw new CliError($"{source} needs a server address, e.g. http://192.168.1.10:13378.");

        var schemeGiven = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(schemeGiven ? text : "http://" + text, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0)
            throw new CliError($"{source}: \"{value}\" is not a usable server address.");
        // Refused rather than ignored: an ftp:// or file:// address is a typo with a plausible
        // reading, and quietly talking http to whatever host it names is worse than saying so.
        if (uri.Scheme is not ("http" or "https"))
            throw new CliError(
                $"{source}: \"{value}\" uses the {uri.Scheme} scheme; Audiobookshelf is reached over http or https.");

        // Only the bare forms get Audiobookshelf's own default port. IsDefaultPort is what still
        // tells "host" from "host:80" here, the prepended scheme having erased the difference.
        return !schemeGiven && uri.IsDefaultPort
            ? new UriBuilder(uri) { Port = DefaultPort }.Uri
            : uri;
    }

    /// <summary>
    /// Settles the connection from the command line and, for anything it left out, the environment.
    /// </summary>
    /// <param name="url">The <c>--abs-url</c> value, or null.</param>
    /// <param name="key">The <c>--abs-key</c> value, or null.</param>
    /// <param name="user">The <c>--abs-user</c> value, or null.</param>
    /// <param name="password">The <c>--abs-password</c> value, or null.</param>
    /// <returns>The resolved connection.</returns>
    /// <exception cref="CliError">Thrown when no server is named, when no credential is, or when
    /// both kinds of credential are.</exception>
    /// <remarks>
    /// The environment stands in for each value separately rather than for the set as a whole, so
    /// the ordinary shape - server and key exported once, a <c>--abs-user</c> on the odd command
    /// that needs another account - works without re-stating the rest.
    /// </remarks>
    public static AbsConnection Resolve(string? url, string? key, string? user, string? password)
    {
        var (rawUrl, urlSource) = FromEnvironment(url, "--abs-url", UrlVariable);
        var (rawKey, keySource) = FromEnvironment(key, "--abs-key", KeyVariable);
        var (rawUser, userSource) = FromEnvironment(user, "--abs-user", UserVariable);
        var (rawPassword, _) = FromEnvironment(password, "--abs-password", PasswordVariable);

        if (rawUrl == null)
            throw new CliError($"No Audiobookshelf server given: use --abs-url, or set {UrlVariable}.");
        if (rawKey != null && rawUser != null)
            throw new CliError(
                $"{keySource} and {userSource} are two ways to authenticate; give one or the other.");
        if (rawKey == null && rawUser == null)
            throw new CliError(
                "No Audiobookshelf credentials given: use --abs-key, or --abs-user with --abs-password, "
                + $"or set {KeyVariable} or {UserVariable}/{PasswordVariable}.");
        if (rawUser != null && rawPassword == null)
            throw new CliError($"{userSource} needs a password: use --abs-password, or set {PasswordVariable}.");

        return new AbsConnection(ParseUrl(rawUrl, urlSource), rawKey, rawUser, rawPassword);
    }

    /// <summary>
    /// One value's resolution: what the command line gave, or else what the environment holds -
    /// and which of the two an error about it should name.
    /// </summary>
    /// <param name="given">The command line value, or null when the option was not given.</param>
    /// <param name="option">The option name.</param>
    /// <param name="variable">The environment variable standing in for it.</param>
    /// <returns>The value and the source to name; the value is null when neither supplied one.</returns>
    private static (string? Value, string Source) FromEnvironment(string? given, string option, string variable)
    {
        if (!string.IsNullOrEmpty(given))
            return (given, option);
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(fromEnvironment) ? (null, option) : (fromEnvironment, variable);
    }
}
