// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Ui;

/// <summary>
/// The names of the processing phases, and the only place that knows how many there are: every
/// <see cref="WorkTracker.BeginPhase"/> call passes one of these constants, and
/// <see cref="Display"/> turns it into the wording the progress bar shows.
/// <para>
/// The split between the two is what keeps the display wording free to change. A phase's constant
/// is its identity - <see cref="ABChapterize.Detection.ChapterDetector"/> compares against it, the
/// debug log names it, and a replay harness parsing a log matches on it - while
/// <see cref="Display"/> is cosmetic and shared by nothing. Adding a phase means adding a constant,
/// an entry in <see cref="All"/> and one in <see cref="Display"/>; <c>PhaseNamesTests</c> fails on a
/// constant that has no display wording.
/// </para>
/// </summary>
public static class PhaseNames
{
    /// <summary>The silence/VAD pre-pass over the whole file.</summary>
    public const string Analyze = "Analyze";

    /// <summary>The chronological candidate walk, reading pauses <em>and</em> music - which is what
    /// the bare name says. A walk with no music left to read is <see cref="ChronologicalProbe"/>.</summary>
    public const string Probe = "Probe";

    /// <summary>A <see cref="Probe"/> walk with no jingle candidates in it, so all it reads is
    /// pauses, in chronological order - the C is for chronological, telling it apart from
    /// <see cref="SilenceProbe"/>, which reads pauses too but only over selected stretches.</summary>
    public const string ChronologicalProbe = "SC-probe";

    /// <summary>The music half of a jingle-first Probe.</summary>
    public const string JingleProbe = "J-probe";

    /// <summary>The pause half of a jingle-first Probe, over the stretches the music left
    /// unsettled.</summary>
    public const string SilenceProbe = "S-probe";

    /// <summary>The descending scan's skim of the file's longest pauses, ahead of
    /// <see cref="Probe"/> itself.</summary>
    public const string DescendingProbe = "SD-probe";

    /// <summary>A sub-floor sweep: the pauses under the length probing was willing to consider,
    /// swept inside a gap that is still missing a chapter. Runs inside <see cref="Probe"/> or
    /// <see cref="Reprobe"/> and borrows their bar, so it is a label rather than a phase of its own
    /// (see <see cref="WorkTracker.Relabel"/>).</summary>
    public const string SubFloorProbe = "SF-probe";

    /// <summary>Probing run again over the gaps left in the numbering, on the upgrade model.</summary>
    public const string Reprobe = "Re-probe";

    /// <summary>Straight-through transcription of a gap or of the file's tail.</summary>
    public const string Scan = "Scan";

    /// <summary>The Scan pass again, every decode displaced by half a Whisper chunk.</summary>
    public const string Rescan = "Re-scan";

    /// <summary><c>--verify</c>'s per-chapter check of the marks a file already carries.</summary>
    public const string Verify = "Verify";

    /// <summary>
    /// The phase that writes the finished file. Singled out by <see cref="ProgressRenderer"/> - the
    /// chapters are all decided by the time it runs, so the bar drops the chapter count rather than
    /// showing one nothing can change - which is why the name is a constant both ends share: a
    /// renderer testing for a label the writer no longer sets would fail silently.
    /// </summary>
    public const string Finish = "Finish";

    /// <summary>Every phase name, for the tests that hold <see cref="Display"/> complete.</summary>
    public static IReadOnlyList<string> All =>
    [
        Analyze, Probe, ChronologicalProbe, JingleProbe, SilenceProbe, DescendingProbe,
        SubFloorProbe, Reprobe, Scan, Rescan, Verify, Finish,
    ];

    /// <summary>What the bar spells a phase name as: the present participle, so the label reads as
    /// something in progress rather than as a heading.</summary>
    /// <param name="phase">One of the constants of this class, or the empty string before the first
    /// phase begins.</param>
    /// <returns>The display wording, or <paramref name="phase"/> itself for a name this class does
    /// not know - a phase is better shown under its bare name than not at all.</returns>
    /// <remarks>
    /// Spelled out rather than derived from the name. English forms a participle four different ways
    /// across these twelve words alone (drop the "e", double the "n", "y" to "ying", plain suffix),
    /// so a rule general enough to cover them would be more machinery - and more ways to be subtly
    /// wrong - than the table it replaces.
    /// </remarks>
    public static string Display(string phase) => phase switch
    {
        Analyze => "Analyzing...",
        Probe => "Probing...",
        ChronologicalProbe => "SC-probing...",
        JingleProbe => "J-probing...",
        SilenceProbe => "S-probing...",
        DescendingProbe => "SD-probing...",
        SubFloorProbe => "SF-probing...",
        Reprobe => "Re-probing...",
        Scan => "Scanning...",
        Rescan => "Re-scanning...",
        Verify => "Verifying...",
        Finish => "Finishing...",
        _ => phase,
    };
}
