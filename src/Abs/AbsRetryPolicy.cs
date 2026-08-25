// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Net;

namespace ABChapterize.Abs;

/// <summary>
/// How long a run keeps trying to reach an Audiobookshelf server that is not answering
/// (<c>--abs-retry</c>), and which failures are worth trying again at all.
/// </summary>
/// <remarks>
/// <para>
/// A budget rather than an attempt count, because what the user knows is how long an outage they
/// are willing to sit through - a server restarting after an update, a NAS spinning up, a Wi-Fi
/// link that drops for a minute. How many requests fit into that is arithmetic nobody should have
/// to do. The pause between attempts is a constant for the same reason: with a budget in minutes
/// and a fixed pause, "three minutes" means three minutes whatever the server does.
/// </para>
/// <para>
/// <b>Every request this tool sends is safe to repeat, which is what makes retrying a write
/// possible at all.</b> There are three: the login, <c>/api/authorize</c>, and the chapter update,
/// which replaces a book's whole chapter list rather than appending to it - so sending it twice
/// leaves exactly what sending it once would. A write endpoint that accumulated (a comment, an
/// upload, a progress event) would break that and must not be added without revisiting this; see
/// <see cref="AbsChapterPush"/>, which is the one write there is and says the same thing from the
/// other side.
/// </para>
/// <para>
/// <b>A refusal the server decided is not retried</b>, only a failure that could clear up on its
/// own. Waiting three minutes to be told again that an item id does not exist, or that this account
/// may not update items, delays the answer without improving it - and an <c>--abs</c> run over a
/// library would pay that per book. <see cref="IsTransientStatus"/> draws the line; a 401 never
/// reaches it, being handled a step earlier by signing in again (see
/// <see cref="AbsSession"/>).
/// </para>
/// </remarks>
public sealed class AbsRetryPolicy
{
    /// <summary>
    /// How long to wait between attempts, in seconds. Long on purpose: what this exists to survive
    /// is a server that is down rather than one that is busy, and something restarting is not back
    /// a second later. Retrying quickly would spend the whole budget while the server was still
    /// coming up.
    /// </summary>
    internal static double RetryPauseSeconds = 60.0;

    /// <summary>
    /// A policy that gives up at the first failure - what <c>--abs-retry 0</c> resolves to, and
    /// what the tests use, having no time to wait for anything.
    /// </summary>
    public static readonly AbsRetryPolicy None = new(TimeSpan.Zero, TimeSpan.Zero);

    private readonly TimeSpan _budget;

    /// <summary>Creates a policy directly; see <see cref="For"/> for the one a run uses.</summary>
    /// <param name="budget">How long to keep trying, or zero to give up at the first failure.</param>
    /// <param name="pause">How long to wait between attempts.</param>
    private AbsRetryPolicy(TimeSpan budget, TimeSpan pause)
    {
        _budget = budget;
        Pause = pause;
    }

    /// <summary>The policy a run with the given <c>--abs-retry</c> value works under.</summary>
    /// <param name="minutes">The budget in minutes; zero or less disables retrying.</param>
    /// <remarks>
    /// <see cref="RetryPauseSeconds"/> is read here rather than at each wait, so a whole run pauses
    /// by the same amount even though the field behind it is writable (<c>--set:</c>).
    /// </remarks>
    public static AbsRetryPolicy For(double minutes)
        => minutes <= 0
            ? None
            : new(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(Math.Max(0, RetryPauseSeconds)));

    /// <summary>A policy with an explicit pause, so a test can exercise the waiting without doing
    /// any.</summary>
    /// <param name="minutes">The budget in minutes.</param>
    /// <param name="pauseSeconds">How long to wait between attempts.</param>
    internal static AbsRetryPolicy Of(double minutes, double pauseSeconds)
        => new(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(pauseSeconds));

    /// <summary>Whether this run retries at all.</summary>
    public bool Enabled => _budget > TimeSpan.Zero;

    /// <summary>How long to wait between attempts.</summary>
    public TimeSpan Pause { get; }

    /// <summary>
    /// Starts one request's budget. Per request rather than per run: an outage is survived once
    /// per thing that runs into it, and a run of a hundred books should not be poorer for having
    /// already waited out an earlier one.
    /// </summary>
    public AbsRetryWindow Open() => new(_budget, Pause);

    /// <summary>
    /// Whether a transport failure is the kind a second attempt could survive - a dropped
    /// connection or a timeout, as opposed to a refusal the server meant.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <remarks>
    /// Cancellation is not screened out here, since this cannot see the run's token: every caller
    /// checks it before asking, because a <see cref="TaskCanceledException"/> is both what a
    /// timeout looks like and what Ctrl+C looks like.
    /// </remarks>
    public static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or IOException;

    /// <summary>
    /// Whether a response the server actually sent is worth asking for again.
    /// </summary>
    /// <param name="status">The status code that came back.</param>
    /// <remarks>
    /// Anything in the 5xx range, plus 408 and 429: a server that is starting up, reloading its
    /// database, behind a proxy that has not found it yet, or telling the caller to slow down. The
    /// remaining 4xx are the server's considered answer to this exact request - a wrong id, a
    /// chapter list it dislikes, an account without the right - and repeating the request cannot
    /// change any of them.
    /// </remarks>
    public static bool IsTransientStatus(HttpStatusCode status)
        => (int)status >= 500
           || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

/// <summary>
/// One request's share of the retry budget: how much of it is left, and how long to wait before
/// spending some of it.
/// </summary>
/// <remarks>
/// A struct carrying a timestamp rather than a countdown, so that the time a request itself spends
/// failing counts against the budget too. A 120 s request timeout would otherwise let a
/// three-minute budget run for a quarter of an hour.
/// </remarks>
public readonly struct AbsRetryWindow
{
    private readonly TimeSpan _budget;
    private readonly long _startedAt;

    /// <summary>Opens a window; see <see cref="AbsRetryPolicy.Open"/>.</summary>
    /// <param name="budget">How long attempts may keep being made.</param>
    /// <param name="pause">How long to wait between them.</param>
    internal AbsRetryWindow(TimeSpan budget, TimeSpan pause)
    {
        _budget = budget;
        Pause = pause;
        _startedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>How long to wait before the next attempt.</summary>
    public TimeSpan Pause { get; }

    /// <summary>
    /// Whether the budget has run out and the failure in hand is the run's answer.
    /// </summary>
    /// <remarks>
    /// Asked at the moment of failure, so the last attempt is the one that starts at the budget's
    /// own edge: a three-minute budget with a one-minute pause tries at 0, 60, 120 and 180
    /// seconds, and reports the failure at 180.
    /// </remarks>
    public bool Exhausted => Stopwatch.GetElapsedTime(_startedAt) >= _budget;

    /// <summary>How a log line words the wait, including what is left to spend after it.</summary>
    public string Describe
    {
        get
        {
            var left = _budget - Stopwatch.GetElapsedTime(_startedAt) - Pause;
            return $"in {Pause.TotalSeconds:0} s"
                   + (left > TimeSpan.Zero ? $" ({left.TotalSeconds:0} s of --abs-retry left after that)" : "");
        }
    }
}
