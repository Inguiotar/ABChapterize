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
    private static AbsSession LoggingIn(ScriptedTransport transport)
        => new(AbsConnection.Resolve(Server, null, "root", "secret"), transport);

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
        await session.OpenAsync(needsUpdate: false, CancellationToken.None);

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
        await session.OpenAsync(needsUpdate: false, CancellationToken.None);

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
            () => session.OpenAsync(needsUpdate: false, CancellationToken.None));

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
            () => session.OpenAsync(needsUpdate: false, CancellationToken.None));

        Assert.Contains("API key was refused", error.Message);
        Assert.DoesNotContain(transport.Seen, r => r.Path == "/login");
        Assert.Equal(["key"], transport.Seen.Select(r => r.Token));
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
        await session.OpenAsync(needsUpdate: false, CancellationToken.None);

        var error = await Assert.ThrowsAsync<AppError>(
            () => session.GetAsync<object>("/api/libraries", CancellationToken.None));

        Assert.Contains("may not do that", error.Message);
        Assert.Equal(1, transport.Seen.Count(r => r.Path == "/login"));
    }
}
