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
/// wording and - most of all - the handling of the token in one place: every request is built and
/// sent by <see cref="SendAsync"/>, so the bearer header cannot be forgotten on one request or
/// logged by another, and the token can be replaced mid-run without any caller knowing.
/// </para>
/// <para>
/// Deliberately not disposed-and-recreated per request. A run against a whole library makes a
/// request per book plus a download each, and a fresh client per call is the textbook way to
/// exhaust the socket pool.
/// </para>
/// <para>
/// One session serves a whole run and is used from one book at a time. Nothing here is safe to
/// call concurrently - the token is swapped in place when it expires - so a future path that
/// processes books in parallel needs a session each, or a lock around the swap.
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
    /// The bearer token every request carries, or null before <see cref="OpenAsync"/> has run.
    /// </summary>
    /// <remarks>
    /// Held here and applied per request rather than pinned into
    /// <see cref="HttpClient.DefaultRequestHeaders"/>, because it is replaced mid-run when the
    /// server expires it - see <see cref="LogInAsync"/> - and a default header is the one place
    /// that cannot be swapped safely while a request reading it is in flight.
    /// </remarks>
    private string? _token;

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

    /// <summary>How long a failing request keeps being repeated, and which failures qualify.</summary>
    private readonly AbsRetryPolicy _retry;

    /// <summary>
    /// Where the few notes a user must see whatever the verbosity go - a wait before another
    /// attempt, a download started again - or null when nothing is listening.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="_log"/> because these are the one thing here that is not a note: a
    /// run that has gone quiet for three minutes looks exactly like one that has hung, and the
    /// user has no way to tell the difference from the outside. Everything else this class has to
    /// say is <c>--verbose</c> material.
    /// </remarks>
    private readonly Action<string>? _notify;

    /// <summary>Creates a session against the given server. Nothing is sent until
    /// <see cref="OpenAsync"/> runs.</summary>
    /// <param name="connection">The resolved server and credentials.</param>
    /// <param name="retry">How long to keep trying a request that fails.</param>
    /// <param name="log">Sink for the connection note, or null.</param>
    /// <param name="notify">Sink for a note the user must see whatever the verbosity, or null.</param>
    public AbsSession(
        AbsConnection connection, AbsRetryPolicy retry, Action<string>? log = null,
        Action<string>? notify = null)
        : this(connection, new HttpClient(), retry, log, notify)
    {
    }

    /// <summary>Creates a session sending over a transport of the caller's choosing.</summary>
    /// <param name="connection">The resolved server and credentials.</param>
    /// <param name="handler">What to send over; disposed with the session.</param>
    /// <param name="retry">How long to keep trying a request that fails; null for not at all,
    /// which is what a test wanting no waiting asks for.</param>
    /// <param name="log">Sink for the connection note, or null.</param>
    /// <param name="notify">Sink for a note the user must see whatever the verbosity, or null.</param>
    /// <remarks>
    /// Exists for the tests. The two failures this class has to survive - the server expiring a
    /// token mid-run and the server not answering - are both unreachable from a short test against
    /// a live server: one takes an hour of waiting to reproduce, the other means switching a real
    /// machine off mid-request. Without a scripted transport they would be the parts of ABS mode
    /// covered by nothing.
    /// </remarks>
    internal AbsSession(
        AbsConnection connection, HttpMessageHandler handler, AbsRetryPolicy? retry = null,
        Action<string>? log = null, Action<string>? notify = null)
        : this(connection, new HttpClient(handler, disposeHandler: true),
               retry ?? AbsRetryPolicy.None, log, notify)
    {
    }

    /// <summary>Shared by the two public constructors; see them for the parameters.</summary>
    /// <param name="connection">The resolved server and credentials.</param>
    /// <param name="client">The client to send over, still to be configured.</param>
    /// <param name="retry">How long to keep trying a request that fails.</param>
    /// <param name="log">Sink for the connection note, or null.</param>
    /// <param name="notify">Sink for a note the user must see whatever the verbosity, or null.</param>
    private AbsSession(
        AbsConnection connection, HttpClient client, AbsRetryPolicy retry, Action<string>? log,
        Action<string>? notify)
    {
        _connection = connection;
        _retry = retry;
        _log = log;
        _notify = notify;
        _client = client;
        _client.Timeout = RequestTimeout;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"ABChapterize/{Cli.CliOptions.Version}");
    }

    /// <summary>The server and account, redacted; see <see cref="AbsConnection.Describe"/>.</summary>
    public string Describe => _connection.Describe;

    /// <summary>
    /// Authenticates and checks that this account may do what ABS mode needs of it.
    /// </summary>
    /// <param name="needsDownload">Whether the run intends to fetch audio files, which only ABS
    /// mode does.</param>
    /// <param name="needsUpdate">Whether the run intends to write chapters back, which not every
    /// account may do.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when the server is unreachable, the credentials are
    /// refused, or the account lacks a permission the run depends on.</exception>
    /// <remarks>
    /// The permission check is here, before the first book is downloaded, on purpose: an account
    /// without update rights fails at the very last step of a run that has already spent an hour
    /// transcribing, and finding that out first costs one request. Both rights are asked for only
    /// where the run actually uses them, so an account that may do exactly what was asked of it is
    /// never turned away for something it was never going to be told to do.
    /// </remarks>
    public async Task OpenAsync(bool needsDownload, bool needsUpdate, CancellationToken ct)
    {
        _token = _connection.ApiKey ?? await LogInAsync(ct);

        var session = await PostAsync<AbsWire.Session>("/api/authorize", new { }, ct);
        _user = session.User
                ?? throw new AppError($"{_connection.Describe} accepted the credentials but named no account.");

        var permissions = _user.Permissions;
        if (needsDownload && permissions is { Download: false })
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
    /// <returns>The bearer token to use until the server stops accepting it.</returns>
    /// <remarks>
    /// <para>
    /// Servers from 2.26 on answer with a short-lived <c>accessToken</c> and keep the refresh token
    /// in an http-only cookie; older ones answer with the long-lived <c>token</c> and nothing else.
    /// Both are read, newest first.
    /// </para>
    /// <para>
    /// <b>The access token lasts one hour.</b> Measured against 2.36.0 on 2026-08-25 by decoding the
    /// JWT this endpoint returns: its <c>exp</c> claim sits exactly 3600 s after its <c>iat</c>.
    /// That is well inside a single run - a book costs a quarter to half an hour to transcribe - so
    /// a library run loses the token two or three books in, and before the renewal below existed
    /// every request after that came back <c>401 Unauthorized</c>, reported by the user against
    /// <c>--abs-push</c>. This comment previously asserted the opposite, that "a run is short enough
    /// that the access token cannot expire mid-way"; it was never measured and it was wrong.
    /// </para>
    /// <para>
    /// Renewal is a fresh login rather than the refresh-token flow, and there is still deliberately
    /// no cookie jar. The username and password are held for the length of the run anyway, so
    /// logging in a second time writes down nothing the first one did not, while a cookie jar would
    /// add a place a credential lives. It also keeps one path for both server generations, the
    /// pre-2.26 one having no refresh endpoint to call.
    /// </para>
    /// <para>
    /// <b>Rejected: preferring the legacy token.</b> The same 2.36.0 login also returns the
    /// pre-2.26 <c>token</c> beside the new one, and that JWT carries <em>no</em> <c>exp</c> claim
    /// at all - it never expires, and reading it first would make this whole problem disappear in
    /// one line. It is the wrong trade: the field is deprecated and due to be removed, so the fix
    /// would rot into the same 401 on a later server, and a bearer token that never expires is a
    /// worse thing to be holding for the sake of avoiding a re-login. Newest first stays.
    /// </para>
    /// </remarks>
    private async Task<string> LogInAsync(CancellationToken ct)
    {
        using var response = await SendPostAsync(
            "/login", new { username = _connection.Username, password = _connection.Password },
            SendMode.Login, ct);
        await EnsureSuccessAsync(response, "POST", "/login", ct);
        var session = await ReadAsync<AbsWire.Session>(response, "/login", ct);

        return session.User?.AccessToken ?? session.User?.Token
            ?? throw new AppError(
                $"{_connection.Root} accepted the login for \"{_connection.Username}\" but returned no token.");
    }

    /// <summary>Signs in again after the server refused the token this session was using.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Announced rather than silent: on a long run this is the only visible sign that an hour has
    /// passed, and a login that starts failing here is worth being able to see in a debug log
    /// next to the request that provoked it.
    /// </remarks>
    private async Task RenewTokenAsync(CancellationToken ct)
    {
        _log?.Invoke("Audiobookshelf: the access token has expired, signing in again");
        _token = await LogInAsync(ct);
    }

    /// <summary>Whether an expired token can be replaced without asking the user for anything.</summary>
    /// <param name="mode">How the refused request was sent.</param>
    /// <remarks>
    /// Only a login can be repeated. Where the run was given an API key that key is the whole of
    /// the credential, so a 401 on one is a refusal to report rather than an expiry to work around
    /// - Audiobookshelf lets a key be created with an expiry date, and there is nothing this tool
    /// could do about one but say so.
    /// </remarks>
    private bool CanRenewToken(SendMode mode)
        => mode != SendMode.Login && _connection.ApiKey == null && _connection.Username != null;

    /// <summary>
    /// Runs a GET and deserializes the response.
    /// </summary>
    /// <typeparam name="T">The wire shape expected back.</typeparam>
    /// <param name="path">Request path, beginning with a separator (e.g. "/api/libraries").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    public async Task<T> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, _connection.Root + path),
            HttpCompletionOption.ResponseContentRead, SendMode.Authenticated, path, ct);
        await EnsureSuccessAsync(response, "GET", path, ct);
        return await ReadAsync<T>(response, path, ct);
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
    /// Retried on the same terms as <see cref="GetAsync{T}"/>, which is only safe because every
    /// POST this tool sends is one that can be repeated without accumulating anything - see
    /// <see cref="AbsRetryPolicy"/>, which is where that argument is set out and where it would
    /// have to be revisited if a second kind of write were ever added.
    /// </remarks>
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        using var response = await SendPostAsync(path, body, SendMode.Authenticated, ct);
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
        using var response = await SendPostAsync(path, body, SendMode.Authenticated, ct);
        await EnsureSuccessAsync(response, "POST", path, ct);
    }

    /// <summary>Sends one POST with a JSON body.</summary>
    /// <param name="path">Request path, beginning with a separator.</param>
    /// <param name="body">The request body, serialized as JSON.</param>
    /// <param name="mode">Which credential the request carries and what it survives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response, for the caller to check and dispose.</returns>
    /// <remarks>
    /// The body is serialized afresh for every attempt because an <see cref="HttpRequestMessage"/>
    /// cannot be sent twice - its content has been consumed by the time the first one comes back.
    /// </remarks>
    private Task<HttpResponseMessage> SendPostAsync(
        string path, object body, SendMode mode, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, _connection.Root + path)
            {
                Content = JsonContent.Create(body, options: Json),
            },
            HttpCompletionOption.ResponseContentRead, mode, path, ct);

    /// <summary>How one request is sent: which credential it carries.</summary>
    /// <remarks>
    /// Reads and writes are not told apart, deliberately, because nothing here treats them
    /// differently any more: a 401 is replayed for both, and so is a transient failure - see
    /// <see cref="AbsRetryPolicy"/> for why repeating this tool's writes is safe. A distinction
    /// nothing branches on is one that goes stale without anybody noticing.
    /// </remarks>
    private enum SendMode
    {
        /// <summary>The login itself. Carries no bearer - there either is not one yet, or the one
        /// there is has just been refused - and a refusal to it is the answer, not something to
        /// work around.</summary>
        Login,

        /// <summary>Everything else: carries the token and renews it once when the server says it
        /// has expired.</summary>
        Authenticated,
    }

    /// <summary>
    /// Sends one request: applies the bearer token, renews it when the server says it has expired,
    /// and keeps trying for as long as <c>--abs-retry</c> allows when the failure is one that could
    /// clear up.
    /// </summary>
    /// <param name="build">Builds the request. Called again for each attempt, an
    /// <see cref="HttpRequestMessage"/> being single-use.</param>
    /// <param name="completion">Whether the task completes on the headers or the whole body.</param>
    /// <param name="mode">Which credential the request carries.</param>
    /// <param name="path">The request path, for messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response, for the caller to check and dispose.</returns>
    /// <exception cref="AppError">Thrown when the server could not be reached at all and the retry
    /// budget is spent.</exception>
    /// <remarks>
    /// <para>
    /// Every request goes through here, which is what keeps the token in one place: applied at the
    /// moment of sending rather than pinned to the client, it cannot be left off a request and
    /// cannot be swapped underneath one that is already in flight. It is also what makes
    /// <c>--abs-retry</c> cover every interaction with the server rather than the handful somebody
    /// remembered to wrap.
    /// </para>
    /// <para>
    /// <b>The renewal and the retry are two different mechanisms and are kept apart.</b> A 401 is
    /// replayed exactly once and never waits, because it is not a failure at all - the server
    /// decided it before doing anything, and a fresh token is all it wants; a second 401 is a real
    /// refusal and is reported rather than turned into a login loop against a server that is simply
    /// saying no. A transient failure is the opposite: nothing about the request needs changing,
    /// only the moment, so it is repeated unchanged after a pause until the budget runs out.
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build, HttpCompletionOption completion, SendMode mode, string path,
        CancellationToken ct)
    {
        var renewed = false;
        var window = _retry.Open();
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                using var request = build();
                if (mode != SendMode.Login && _token != null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                response = await _client.SendAsync(request, completion, ct);
            }
            catch (Exception ex) when (AbsRetryPolicy.IsTransient(ex) && !ct.IsCancellationRequested
                                       && !window.Exhausted)
            {
                await WaitBeforeRetryAsync(window, path, ex.Message, ct);
                continue;
            }
            // Before the transient catch below, and only because a timeout and a Ctrl+C arrive as
            // the same exception type: without this the run's own cancellation would be reported as
            // a server that could not be reached.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (AbsRetryPolicy.IsTransient(ex))
            {
                throw Unreachable(ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed && CanRenewToken(mode))
            {
                response.Dispose();
                renewed = true;
                await RenewTokenAsync(ct);
                continue;
            }

            if (!AbsRetryPolicy.IsTransientStatus(response.StatusCode) || window.Exhausted)
                return response;

            // Read before disposing: the response object is what carries the words this note is
            // about to use, and it must not outlive the message it describes.
            var refusal = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            response.Dispose();
            await WaitBeforeRetryAsync(window, path, refusal, ct);
        }
    }

    /// <summary>Says what went wrong and waits out the pause before the next attempt.</summary>
    /// <param name="window">This request's share of the retry budget.</param>
    /// <param name="path">The request path, for the message.</param>
    /// <param name="failure">What came back, or did not.</param>
    /// <param name="ct">Cancellation token, which is what cuts a wait short.</param>
    /// <remarks>
    /// Announced rather than logged: a run that has gone silent for minutes is indistinguishable
    /// from one that has hung, and a user who cannot tell the two apart kills the good one.
    /// </remarks>
    private async Task WaitBeforeRetryAsync(
        AbsRetryWindow window, string path, string failure, CancellationToken ct)
    {
        _notify?.Invoke($"Audiobookshelf: {path} failed ({failure}); trying again {window.Describe}");
        await Task.Delay(window.Pause, ct);
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
    /// <para>
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> is what takes the body out of
    /// <see cref="RequestTimeout"/>: an audiobook is a gigabyte and a fixed deadline over the whole
    /// transfer would be a guess about the user network. What bounds a stalled download instead is
    /// Ctrl+C, which every other long step of a run is bounded by too.
    /// </para>
    /// <para>
    /// <b>A transfer that breaks off is started again at most once, however much retry budget is
    /// left.</b> The request in front of it is bounded by <c>--abs-retry</c> like any other, but
    /// the transfer is not and cannot be: a gigabyte over a home network takes longer than the
    /// whole budget by itself, so a budget-driven rule would either be no rule at all or one that
    /// spent the run re-downloading the same book. Once is the useful case - a link that dropped
    /// for a moment - without the failure mode where a server that cuts every stream at 90% is
    /// answered by fetching 90% of a book over and over.
    /// </para>
    /// <para>
    /// There is no resume from a byte offset. Audiobookshelf's download endpoint would very likely
    /// serve a Range request, but a partial file that has to be verified against the server's idea
    /// of the same file is a correctness problem, and detection running over a book quietly
    /// stitched from two halves is exactly the failure this class deletes partial downloads to
    /// avoid.
    /// </para>
    /// </remarks>
    public async Task<long> DownloadAsync(
        string path, string destination, Action<long>? onProgress, CancellationToken ct)
    {
        var restarted = false;
        while (true)
        {
            try
            {
                return await StreamToFileAsync(path, destination, onProgress, ct);
            }
            catch (Exception ex) when (ex is not AppError)
            {
                // A half-written audiobook is worse than none: ffprobe would read it as a truncated
                // file and detection would run over a book missing its end. Removed on the way out
                // whatever went wrong, cancellation included - and before another attempt too,
                // since the next one opens the file afresh anyway.
                TryDelete(destination);
                if (ct.IsCancellationRequested)
                    throw;
                if (restarted || !_retry.Enabled || !AbsRetryPolicy.IsTransient(ex))
                    throw Unreachable(ex);
                restarted = true;
                _notify?.Invoke(
                    $"Audiobookshelf: the download of {path} broke off ({ex.Message}); "
                    + $"starting it again in {_retry.Pause.TotalSeconds:0} s");
                await Task.Delay(_retry.Pause, ct);
            }
        }
    }

    /// <summary>One attempt at the download: the request, and the body copied to disk.</summary>
    /// <param name="path">Request path of the download endpoint.</param>
    /// <param name="destination">Where to write the file; overwritten if it exists.</param>
    /// <param name="onProgress">Called with the running byte count, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// Failures are left raw for <see cref="DownloadAsync"/> to classify - wrapping them here would
    /// hide the very thing it has to look at to decide whether another attempt is worth making.
    /// </remarks>
    private async Task<long> StreamToFileAsync(
        string path, string destination, Action<long>? onProgress, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, _connection.Root + path),
            HttpCompletionOption.ResponseHeadersRead, SendMode.Authenticated, path, ct);
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
            // An expired token has already been renewed and the request replayed by the time this
            // runs, so a 401 arriving here is the credential itself being refused. Which one that
            // is worth naming: an API key is all the run was given, and Audiobookshelf lets a key
            // be created with an expiry date, so "refused" and "expired" look identical from here.
            HttpStatusCode.Unauthorized => _connection.ApiKey != null
                ? " - the API key was refused; an Audiobookshelf key can carry an expiry date"
                : " - the credentials were refused",
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

    /// <summary>Wraps a transport failure in the message that names the server, which the raw
    /// socket error does not.</summary>
    /// <param name="ex">The transport failure.</param>
    private AppError Unreachable(Exception ex)
        => new($"Audiobookshelf at {_connection.Root} could not be reached: {ex.Message}");

    /// <summary>Releases the HTTP client and any transport it was given.</summary>
    public void Dispose() => _client.Dispose();
}
