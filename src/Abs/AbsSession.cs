// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ABChapterize.Errors;

namespace ABChapterize.Abs;

/// <summary>
/// One authenticated conversation with an Audiobookshelf server: the token, the request helpers
/// every other ABS class is built on, and the streaming download.
/// </summary>
/// <remarks>
/// <para>
/// The only class in <c>src\Abs\</c> that touches <see cref="HttpClient"/>. Everything above it
/// asks in terms of paths and wire objects, which is what keeps the retry policy, the error
/// wording and - most of all - the handling of the token in one place: a bearer header set on the
/// client rather than passed around cannot be forgotten on one request or logged by another.
/// </para>
/// <para>
/// Deliberately not disposed-and-recreated per request. A run against a whole library makes a
/// request per book plus a download each, and a fresh client per call is the textbook way to
/// exhaust the socket pool.
/// </para>
/// </remarks>
public sealed class AbsSession : IDisposable
{
    /// <summary>How the server was reached and as whom; kept for error messages.</summary>
    private readonly AbsConnection _connection;

    private readonly HttpClient _client;

    /// <summary>Where progress notes go, or null when nothing is listening.</summary>
    private readonly Action<string>? _log;

    /// <summary>The account this session authenticated as, once <see cref="OpenAsync"/> has run.</summary>
    private AbsWire.User? _user;

    /// <summary>
    /// How the wire is read: Audiobookshelf spells its fields in camelCase and this tool in Pascal,
    /// and the write direction is spelled back the same way so the two cannot drift.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// How long a plain API call may take. Generous rather than tight because the item listing of a
    /// large library is one request, and a server that is mid-scan answers it slowly. The download
    /// is not bound by this - see <see cref="DownloadAsync"/>.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How often a read-only request is attempted before giving up, and how long the pause between
    /// attempts is. One retry, not a policy: this talks to a home server over a LAN, where the
    /// failure worth surviving is a single dropped connection part way through a hundred-book run,
    /// and anything worse should stop the run rather than be papered over.
    /// </summary>
    private const int ReadAttempts = 2;

    /// <inheritdoc cref="ReadAttempts"/>
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(2);

    /// <summary>Creates a session against the given server. Nothing is sent until
    /// <see cref="OpenAsync"/> runs.</summary>
    /// <param name="connection">The resolved server and credentials.</param>
    /// <param name="log">Sink for the connection note, or null.</param>
    public AbsSession(AbsConnection connection, Action<string>? log = null)
    {
        _connection = connection;
        _log = log;
        _client = new HttpClient { Timeout = RequestTimeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"ABChapterize/{Cli.CliOptions.Version}");
    }

    /// <summary>The server and account, redacted; see <see cref="AbsConnection.Describe"/>.</summary>
    public string Describe => _connection.Describe;

    /// <summary>
    /// Authenticates and checks that this account may do what ABS mode needs of it.
    /// </summary>
    /// <param name="needsUpdate">Whether the run intends to write chapters back, which not every
    /// account may do.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when the server is unreachable, the credentials are
    /// refused, or the account lacks a permission the run depends on.</exception>
    /// <remarks>
    /// The permission check is here, before the first book is downloaded, on purpose: an account
    /// without update rights fails at the very last step of a run that has already spent an hour
    /// transcribing, and finding that out first costs one request.
    /// </remarks>
    public async Task OpenAsync(bool needsUpdate, CancellationToken ct)
    {
        var token = _connection.ApiKey ?? await LogInAsync(ct);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var session = await PostAsync<AbsWire.Session>("/api/authorize", new { }, ct);
        _user = session.User
                ?? throw new AppError($"{_connection.Describe} accepted the credentials but named no account.");

        var permissions = _user.Permissions;
        if (permissions is { Download: false })
            throw new AppError(
                $"The Audiobookshelf account \"{_user.Username}\" may not download audio files, "
                + "which ABS mode needs in order to look at a book at all.");
        if (needsUpdate && permissions is { Update: false })
            throw new AppError(
                $"The Audiobookshelf account \"{_user.Username}\" may not update items, so the "
                + "chapters this run detects could not be sent back. Use --dry-run to detect without writing.");

        _log?.Invoke($"Audiobookshelf {_connection.Root}, signed in as {_user.Username}"
                     + (_user.Type.Length > 0 ? $" ({_user.Type})" : "")
                     + (_connection.ApiKey != null ? " via API key" : ""));
    }

    /// <summary>
    /// Exchanges a username and password for a token.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bearer token to use for the rest of the run.</returns>
    /// <remarks>
    /// Servers from 2.26 on answer with a short-lived <c>accessToken</c> and keep the refresh token
    /// in an http-only cookie; older ones answer with the long-lived <c>token</c> and nothing else.
    /// Both are read, newest first. A run is short enough that the access token cannot expire
    /// mid-way, so there is no refresh handling here and deliberately no cookie jar - one fewer
    /// place a credential could be written down.
    /// </remarks>
    private async Task<string> LogInAsync(CancellationToken ct)
    {
        var session = await PostAsync<AbsWire.Session>(
            "/login", new { username = _connection.Username, password = _connection.Password }, ct);
        return session.User?.AccessToken ?? session.User?.Token
            ?? throw new AppError(
                $"{_connection.Root} accepted the login for \"{_connection.Username}\" but returned no token.");
    }

    /// <summary>
    /// Runs a GET and deserializes the response.
    /// </summary>
    /// <typeparam name="T">The wire shape expected back.</typeparam>
    /// <param name="path">Request path, beginning with a separator (e.g. "/api/libraries").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    public async Task<T> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _client.GetAsync(_connection.Root + path, ct);
                await EnsureSuccessAsync(response, "GET", path, ct);
                return await ReadAsync<T>(response, path, ct);
            }
            catch (Exception ex) when (attempt < ReadAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                _log?.Invoke($"Audiobookshelf: {path} failed ({ex.Message}); retrying");
                await Task.Delay(RetryPause, ct);
            }
        }
    }

    /// <summary>
    /// Runs a POST with a JSON body and deserializes the response.
    /// </summary>
    /// <typeparam name="T">The wire shape expected back.</typeparam>
    /// <param name="path">Request path, beginning with a separator.</param>
    /// <param name="body">The request body, serialized as JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    /// <remarks>
    /// Never retried, unlike <see cref="GetAsync{T}"/>: the two things this tool posts are a login
    /// and a chapter update, and repeating either after an ambiguous failure is worse than
    /// reporting it.
    /// </remarks>
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        using var response = await SendPostAsync(path, body, ct);
        await EnsureSuccessAsync(response, "POST", path, ct);
        return await ReadAsync<T>(response, path, ct);
    }

    /// <summary>
    /// Runs a POST whose response body carries nothing this tool needs.
    /// </summary>
    /// <param name="path">Request path, beginning with a separator.</param>
    /// <param name="body">The request body, serialized as JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PostAsync(string path, object body, CancellationToken ct)
    {
        using var response = await SendPostAsync(path, body, ct);
        await EnsureSuccessAsync(response, "POST", path, ct);
    }

    /// <summary>Sends one POST, translating the transport failures into <see cref="AppError"/>.</summary>
    /// <param name="path">Request path, beginning with a separator.</param>
    /// <param name="body">The request body, serialized as JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response, for the caller to check and dispose.</returns>
    private async Task<HttpResponseMessage> SendPostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            return await _client.PostAsJsonAsync(_connection.Root + path, body, Json, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>
    /// Downloads one file to disk, reporting progress as it goes.
    /// </summary>
    /// <param name="path">Request path of the download endpoint.</param>
    /// <param name="destination">Where to write the file; overwritten if it exists.</param>
    /// <param name="onProgress">Called with the running byte count, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="AppError">Thrown when the download fails, in which case the partial file
    /// has already been removed.</exception>
    /// <remarks>
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> is what takes the body out of
    /// <see cref="RequestTimeout"/>: an audiobook is a gigabyte and a fixed deadline over the whole
    /// transfer would be a guess about the user network. What bounds a stalled download instead is
    /// Ctrl+C, which every other long step of a run is bounded by too.
    /// </remarks>
    public async Task<long> DownloadAsync(
        string path, string destination, Action<long>? onProgress, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync(
                _connection.Root + path, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(response, "GET", path, ct);

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16,
                useAsync: true);

            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                onProgress?.Invoke(written);
            }
            return written;
        }
        catch (Exception ex) when (ex is not AppError)
        {
            // A half-written audiobook is worse than none: ffprobe would read it as a truncated
            // file and detection would run over a book missing its end. Removed on the way out
            // whatever went wrong, cancellation included.
            TryDelete(destination);
            throw ex is OperationCanceledException ? ex : Unreachable(ex);
        }
    }

    /// <summary>Deletes a partial download, noting rather than raising a failure to.</summary>
    /// <param name="path">The file to remove.</param>
    /// <remarks>
    /// Only ever called while another failure is being reported, and throwing here would replace
    /// the error that matters with one about tidying up. It is still worth a log line: a temporary
    /// folder that will not empty is usually a virus scanner holding the file, which is the sort of
    /// thing that only makes sense once somebody sees it written down.
    /// </remarks>
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not remove the partial download {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns a non-success response into an <see cref="AppError"/> naming what was asked and what
    /// came back.
    /// </summary>
    /// <param name="response">The response to check.</param>
    /// <param name="method">The HTTP method, for the message.</param>
    /// <param name="path">The request path, for the message.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, string method, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        // The body is Audiobookshelf own explanation ("Bad Request", "Invalid chapters") and is
        // usually the only thing that says which part of the request it disliked. Truncated because
        // an error page from a reverse proxy in front of the server is HTML and would fill the
        // console with markup.
        var detail = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (detail.Length > 200)
            detail = detail[..200] + "...";

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " - the credentials were refused",
            HttpStatusCode.Forbidden => " - this account may not do that",
            HttpStatusCode.NotFound => " - no such item on this server",
            _ => "",
        };
        throw new AppError(
            $"Audiobookshelf {method} {path} failed: {(int)response.StatusCode} {response.ReasonPhrase}{hint}"
            + (detail.Length > 0 ? $" ({detail})" : ""));
    }

    /// <summary>Deserializes a response body, reporting a shape this tool cannot read as an error
    /// rather than as a null.</summary>
    /// <typeparam name="T">The wire shape expected back.</typeparam>
    /// <param name="response">The successful response.</param>
    /// <param name="path">The request path, for the message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string path, CancellationToken ct)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                   ?? throw new AppError($"Audiobookshelf answered {path} with an empty body.");
        }
        catch (JsonException ex)
        {
            throw new AppError($"Audiobookshelf answered {path} with something unreadable: {ex.Message}");
        }
    }

    /// <summary>Whether a failure is the kind a second attempt could survive - a dropped
    /// connection or a timeout, as opposed to a refusal the server meant.</summary>
    /// <param name="ex">The exception to classify.</param>
    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or IOException;

    /// <summary>Wraps a transport failure in the message that names the server, which the raw
    /// socket error does not.</summary>
    /// <param name="ex">The transport failure.</param>
    private AppError Unreachable(Exception ex)
        => new($"Audiobookshelf at {_connection.Root} could not be reached: {ex.Message}");

    /// <summary>Releases the HTTP client and with it the token held in its default headers.</summary>
    public void Dispose() => _client.Dispose();
}
