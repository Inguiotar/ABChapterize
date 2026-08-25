// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Net;
using System.Net.Http.Json;
using Xunit;
using ABChapterize.Abs;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsSession"/>'s handling of an expired token.
/// </summary>
/// <remarks>
/// The reason this file exists at all: Audiobookshelf 2.26 and later hand out an access token that
/// lasts an hour (measured against 2.36.0, 2026-08-25), which is shorter than a run over more than
/// two or three books, so the renewal is on the normal path of any real library job rather than in
/// a corner of it. It is also invisible - nothing about it shows up in a short test against a live
/// server, and the failure it prevents takes an hour of waiting to reproduce by hand. Everything
/// here therefore drives a scripted transport instead.
/// </remarks>
public sealed class AbsSessionTests
{
    /// <summary>Where the scripted server pretends to live. Nothing is ever sent to it.</summary>
    private const string Server = "host:9";

    /// <summary>
    /// A transport answering from a script, recording what it was asked and with which token.
    /// </summary>
    /// <param name="answer">Given the request count so far (from 1) and the request, the response
    /// to answer with.</param>
    private sealed class ScriptedTransport(Func<int, HttpRequestMessage, HttpResponseMessage> answer)
        : HttpMessageHandler
    {
        /// <summary>Every request seen, as method, path and the bearer token it carried.</summary>
        public List<(string Method, string Path, string? Token)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                      request.Headers.Authorization?.Parameter));
            return Task.FromResult(answer(Seen.Count, request));
        }
    }

    /// <summary>A login response handing out the named token, in the shape 2.26 and later use.</summary>
    /// <param name="token">The access token to answer with.</param>
    private static HttpResponseMessage LoginGiving(string token)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { user = new { username = "root", accessToken = token } }),
        };

    /// <summary>A response carrying a readable but uninteresting body.</summary>
    /// <param name="status">The status code to answer with.</param>
    private static HttpResponseMessage Answering(HttpStatusCode status)
        => new(status) { Content = JsonContent.Create(new { user = new { username = "root" } }) };

    /// <summary>A session logging in with a username and password, over the given transport.</summary>
    /// <param name="transport">The scripted transport to send over.</param>
    /// <param name="retry">How long to keep trying, or null for not at all.</param>
    private static AbsSession LoggingIn(ScriptedTransport transport, AbsRetryPolicy? retry = null)
        => new(AbsConnection.Resolve(Server, null, "root", "secret"), transport, retry);

    /// <summary>
    /// A retry policy with a real budget and no waiting at all, which is the only way these tests
    /// can look at the retry loop: the pause a run actually uses is a minute.
    /// </summary>
    private static AbsRetryPolicy Retrying => AbsRetryPolicy.Of(minutes: 1, pauseSeconds: 0);

    /// <summary>What an unreachable server looks like from inside a script.</summary>
    private static HttpResponseMessage Refusing()
        => throw new HttpRequestException("connection refused");

    /// <summary>
    /// A body that hands over some bytes and then breaks, which is what a transfer cut part way
    /// through a book looks like: the request succeeded, the headers arrived, and the failure is
    /// somewhere in the middle of a gigabyte.
    /// </summary>
    /// <param name="bytesBeforeBreak">How much to deliver before failing.</param>
    private sealed class BreakingStream(int bytesBeforeBreak) : Stream
    {
        private int _delivered;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delivered >= bytesBeforeBreak)
                throw new IOException("the connection was closed");
            var give = Math.Min(count, bytesBeforeBreak - _delivered);
            _delivered += give;
            return give;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _delivered;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The headline case: a token accepted at the start of a run and refused part way through gets
    /// replaced, and the request that was refused is sent again and succeeds.
    /// </summary>
    /// <remarks>
    /// This is the failure the user hit against <c>--abs-push</c> - a run marks two or three books
    /// happily and then every server call comes back 401 for the rest of it. Nothing above
    /// <see cref="AbsSession"/> is told any of this happened, which is the point: the renewal
    /// cannot be something each caller has to remember.
    /// </remarks>
    [Fact]
    public async Task ARefusedTokenIsRenewedAndTheRequestSentAgain()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),                       // OpenAsync logs in
            2 => Answering(HttpStatusCode.OK),               // ... and authorizes
            3 => Answering(HttpStatusCode.Unauthorized),     // an hour passes; the token is dead
            4 => LoginGiving("second"),                      // so the session signs in again
            _ => Answering(HttpStatusCode.OK),               // and asks again, with the new token
        });
        using var session = LoggingIn(transport);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        await session.GetAsync<object>("/api/libraries", CancellationToken.None);

        Assert.Equal(
            [("POST", "/login", null), ("POST", "/api/authorize", "first"),
             ("GET", "/api/libraries", "first"), ("POST", "/login", null),
             ("GET", "/api/libraries", "second")],
            transport.Seen);
    }

    /// <summary>
    /// A write is replayed too, which is the case that matters most: the push happens after the
    /// half hour of transcription that made the token expire in the first place.
    /// </summary>
    /// <remarks>
    /// It does not contradict the rule that a POST is never retried. That rule guards against
    /// applying a change twice after an ambiguous failure; a 401 is a refusal decided before the
    /// server did anything, so there is nothing to apply twice. The assertion to keep is the
    /// count - two attempts, not three.
    /// </remarks>
    [Fact]
    public async Task ARefusedWriteIsSentAgainExactlyOnce()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            3 => Answering(HttpStatusCode.Unauthorized),
            4 => LoginGiving("second"),
            _ => Answering(HttpStatusCode.OK),
        });
        using var session = LoggingIn(transport);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        await session.PostAsync("/api/items/1/chapters", new { }, CancellationToken.None);

        Assert.Equal(2, transport.Seen.Count(r => r.Path == "/api/items/1/chapters"));
        Assert.Equal(["first", "second"],
                     transport.Seen.Where(r => r.Path == "/api/items/1/chapters").Select(r => r.Token));
    }

    /// <summary>
    /// A server that keeps saying no is reported rather than logged into for ever.
    /// </summary>
    /// <remarks>
    /// The one bug a renewal loop invites. A revoked account answers 401 to the fresh token as
    /// readily as to the stale one, and a session that treats every 401 as an expiry would sit
    /// there logging in until the run is killed - having burned nothing but the user's evening.
    /// </remarks>
    [Fact]
    public async Task ASecondRefusalIsReportedRatherThanRetriedForEver()
    {
        var transport = new ScriptedTransport((n, request) =>
            request.RequestUri!.AbsolutePath == "/login"
                ? LoginGiving($"token{n}")
                : Answering(HttpStatusCode.Unauthorized));
        using var session = LoggingIn(transport);

        var error = await Assert.ThrowsAsync<AppError>(
            () => session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None));

        Assert.Contains("401", error.Message);
        Assert.Contains("credentials were refused", error.Message);
        Assert.Equal(2, transport.Seen.Count(r => r.Path == "/login"));
    }

    /// <summary>
    /// A run given an API key is not sent round the login path, there being nothing to log in with.
    /// </summary>
    /// <remarks>
    /// The key is the whole credential, so a 401 on one is the server refusing it - which
    /// Audiobookshelf also answers for a key created with an expiry date that has passed. The
    /// message says which of the two credentials was refused, since "expired" and "wrong" are
    /// indistinguishable from here and the user is the only one who can tell them apart.
    /// </remarks>
    [Fact]
    public async Task AnApiKeyIsNeverExchangedForALogin()
    {
        var transport = new ScriptedTransport((_, _) => Answering(HttpStatusCode.Unauthorized));
        using var session = new AbsSession(AbsConnection.Resolve(Server, "key", null, null), transport);

        var error = await Assert.ThrowsAsync<AppError>(
            () => session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None));

        Assert.Contains("API key was refused", error.Message);
        Assert.DoesNotContain(transport.Seen, r => r.Path == "/login");
        Assert.Equal(["key"], transport.Seen.Select(r => r.Token));
    }

    /// <summary>
    /// The headline case for <c>--abs-retry</c>: a server that is not there yet is waited for
    /// rather than reported, and every kind of request is covered - the login as readily as what
    /// comes after it.
    /// </summary>
    [Fact]
    public async Task AServerThatIsNotAnsweringIsTriedAgainUntilItDoes()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => Refusing(),                     // the server is still starting up
            2 => LoginGiving("first"),           // ... and by the next attempt it is up
            3 => Refusing(),                     // the same again for the next request
            _ => Answering(HttpStatusCode.OK),
        });
        using var session = LoggingIn(transport, Retrying);

        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        Assert.Equal(
            [("POST", "/login", null), ("POST", "/login", null),
             ("POST", "/api/authorize", "first"), ("POST", "/api/authorize", "first")],
            transport.Seen);
    }

    /// <summary>
    /// <c>--abs-retry 0</c> means what it says: the first failure is the run's answer.
    /// </summary>
    [Fact]
    public async Task WithoutABudget_TheFirstFailureIsTheAnswer()
    {
        var transport = new ScriptedTransport((_, _) => Refusing());
        using var session = LoggingIn(transport);

        var error = await Assert.ThrowsAsync<AppError>(
            () => session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None));

        Assert.Contains("could not be reached", error.Message);
        Assert.Single(transport.Seen);
    }

    /// <summary>
    /// A server that never comes back is reported once the budget is spent, rather than retried for
    /// the rest of the user's evening.
    /// </summary>
    /// <remarks>
    /// The attempt count is deliberately asserted as a range. What the budget bounds is time, so
    /// how many attempts fit into it depends on how fast the machine running this is - the fact
    /// worth pinning is that there was more than one and that it stopped by itself.
    /// </remarks>
    [Fact]
    public async Task AServerThatNeverComesBackIsGivenUpOnWhenTheBudgetRunsOut()
    {
        var transport = new ScriptedTransport((_, _) => Refusing());
        using var session = LoggingIn(transport, AbsRetryPolicy.Of(minutes: 0.002, pauseSeconds: 0.01));

        await Assert.ThrowsAsync<AppError>(
            () => session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None));

        Assert.InRange(transport.Seen.Count, 2, 200);
    }

    /// <summary>
    /// A 503 is the shape a server behind a reverse proxy takes while it restarts, so it is waited
    /// out like a dropped connection.
    /// </summary>
    [Fact]
    public async Task AServiceUnavailableIsWaitedOut()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            3 => Answering(HttpStatusCode.ServiceUnavailable),
            _ => Answering(HttpStatusCode.OK),
        });
        using var session = LoggingIn(transport, Retrying);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        await session.GetAsync<object>("/api/libraries", CancellationToken.None);

        Assert.Equal(2, transport.Seen.Count(r => r.Path == "/api/libraries"));
    }

    /// <summary>
    /// An answer the server meant is not waited out. Three minutes cannot turn an item id the
    /// server has never heard of into one it has, and an <c>--abs</c> run over a library would pay
    /// that wait once per book.
    /// </summary>
    /// <param name="status">A refusal the server decided.</param>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ARefusalTheServerMeantIsReportedAtOnce(HttpStatusCode status)
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            _ => Answering(status),
        });
        using var session = LoggingIn(transport, Retrying);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        await Assert.ThrowsAsync<AppError>(
            () => session.GetAsync<object>("/api/libraries", CancellationToken.None));

        Assert.Equal(1, transport.Seen.Count(r => r.Path == "/api/libraries"));
    }

    /// <summary>
    /// The chapter update is retried like everything else, which is only safe because it replaces a
    /// book's whole chapter list rather than adding to it - see <see cref="AbsRetryPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The case that matters: the push is the last thing a run does, after half an hour of
    /// transcription, and it is the one request whose failure loses work that cannot be repeated
    /// cheaply.
    /// </remarks>
    [Fact]
    public async Task TheChapterUpdateIsRetriedToo()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            3 => Refusing(),
            _ => Answering(HttpStatusCode.OK),
        });
        using var session = LoggingIn(transport, Retrying);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        await session.PostAsync("/api/items/1/chapters", new { }, CancellationToken.None);

        Assert.Equal(2, transport.Seen.Count(r => r.Path == "/api/items/1/chapters"));
    }

    /// <summary>
    /// A transfer that breaks off part way through a book is started again - once, however much
    /// budget is left, since a book is a large file and a server that cuts every stream would
    /// otherwise have the run fetching most of the same one over and over.
    /// </summary>
    [Fact]
    public async Task ABrokenDownloadIsStartedAgainExactlyOnce()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BreakingStream(1024)),
            },
        });
        using var session = LoggingIn(transport, Retrying);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);
        var destination = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}.bin");

        try
        {
            await Assert.ThrowsAsync<AppError>(
                () => session.DownloadAsync("/download", destination, null, CancellationToken.None));

            Assert.Equal(2, transport.Seen.Count(r => r.Path == "/download"));
            // The half-written file goes with the failure: ffprobe would read one as a truncated
            // book and detection would run over an audiobook missing its end.
            Assert.False(File.Exists(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    /// <summary>The other half of it: the second attempt is a real one, and its bytes are the
    /// ones that end up on disk.</summary>
    [Fact]
    public async Task ARestartedDownloadKeepsWhatTheSecondAttemptDelivered()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            3 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BreakingStream(1024)),
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) },
        });
        using var session = LoggingIn(transport, Retrying);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);
        var destination = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}.bin");

        try
        {
            var written = await session.DownloadAsync(
                "/download", destination, null, CancellationToken.None);

            Assert.Equal(4096, written);
            Assert.Equal(4096, new FileInfo(destination).Length);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    /// <summary>
    /// A refusal of anything else is passed straight through, an expired token being the only
    /// thing this machinery is for.
    /// </summary>
    [Fact]
    public async Task AForbiddenResponseIsNotTreatedAsAnExpiredToken()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => LoginGiving("first"),
            2 => Answering(HttpStatusCode.OK),
            _ => Answering(HttpStatusCode.Forbidden),
        });
        using var session = LoggingIn(transport);
        await session.OpenAsync(needsDownload: true, needsUpdate: false, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AppError>(
            () => session.GetAsync<object>("/api/libraries", CancellationToken.None));

        Assert.Contains("may not do that", error.Message);
        Assert.Equal(1, transport.Seen.Count(r => r.Path == "/login"));
    }
}
