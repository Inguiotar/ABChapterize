// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Ui;

namespace ABChapterize.Processing;

/// <summary>The kinds of file <c>--cleanup</c> recognizes as its business.</summary>
public enum CleanupArtifact
{
    /// <summary>Not one of ours - left alone.</summary>
    None,

    /// <summary>An original parked by an interrupted write (<see cref="FfmpegClient.ParkedSuffix"/>).
    /// The only kind whose treatment depends on what else is on disk: see
    /// <see cref="CleanupRunner"/>.</summary>
    Parked,

    /// <summary>A remux target left behind by an interrupted write
    /// (<see cref="FfmpegClient.TempInfix"/>) - always scratch, never anything's only copy.</summary>
    Temp,

    /// <summary>A <c>--backup</c> file (".bak").</summary>
    Backup,

    /// <summary>A <c>--debug</c> troubleshooting log (<see cref="DebugLog.Suffix"/>).</summary>
    DebugLog,

    /// <summary>A directory's batch checkpoint (<see cref="BatchProgress.FileName"/>).</summary>
    BatchProgress,

    /// <summary>An audio file still carrying a ".missing-marks" tag.</summary>
    TaggedAudio,
}

/// <summary>
/// What one file on disk is, as far as <c>--cleanup</c> is concerned.
/// </summary>
/// <param name="Kind">Which kind of artifact it is, or <see cref="CleanupArtifact.None"/>.</param>
/// <param name="AudioPath">The audio file it belongs to - the file itself for
/// <see cref="CleanupArtifact.TaggedAudio"/>, and empty for
/// <see cref="CleanupArtifact.BatchProgress"/>, which belongs to a directory rather than to any one
/// book. This is what <c>--filter</c> is matched against, so that filtering a cleanup selects
/// audiobooks and takes their leftovers along, rather than asking the user to write a regexp that
/// also matches ".debug.log".</param>
public readonly record struct CleanupMatch(CleanupArtifact Kind, string AudioPath)
{
    /// <summary>
    /// Recognizes what a file is from its name alone. Nothing here touches the disk: the decision
    /// of what to <em>do</em> with an artifact depends on what else exists next to it, and keeping
    /// that decision out of here is what makes this half testable without a folder full of
    /// audiobooks.
    /// </summary>
    /// <param name="path">Path of the file to classify.</param>
    public static CleanupMatch Of(string path)
    {
        if (EndsWith(path, FfmpegClient.ParkedSuffix, out var parked))
            return new(CleanupArtifact.Parked, parked);
        if (TryStripTempInfix(path, out var temp))
            return new(CleanupArtifact.Temp, temp);
        if (EndsWith(path, DebugLog.Suffix, out var log))
            return new(CleanupArtifact.DebugLog, log);
        if (EndsWith(path, ".bak", out var backup))
            return new(CleanupArtifact.Backup, backup);
        if (Path.GetFileName(path).Equals(BatchProgress.FileName, StringComparison.OrdinalIgnoreCase))
            return new(CleanupArtifact.BatchProgress, "");
        if (MissingMarksTag.IsTagged(path))
            return new(CleanupArtifact.TaggedAudio, path);
        return new(CleanupArtifact.None, "");
    }

    /// <summary>Tests for a suffix and hands back what precedes it.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="suffix">The suffix to strip.</param>
    /// <param name="stripped">The path without the suffix, or "" when it did not match.</param>
    private static bool EndsWith(string path, string suffix, out string stripped)
    {
        var matched = path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && path.Length > suffix.Length;
        stripped = matched ? path[..^suffix.Length] : "";
        return matched;
    }

    /// <summary>
    /// Recognizes a remux target, whose name carries the audio file's own extension <em>after</em>
    /// the marker (ffmpeg picks its muxer from that extension, so it cannot simply be dropped) -
    /// which is why this one needs more than a suffix test.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <param name="stripped">The audio file it belongs to, or "" when it did not match.</param>
    private static bool TryStripTempInfix(string path, out string stripped)
    {
        stripped = "";
        var at = path.LastIndexOf(FfmpegClient.TempInfix, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
            return false;
        // Only an extension may follow, so a directory that happens to be named this way cannot
        // put every file under it up for deletion.
        var rest = path[(at + FfmpegClient.TempInfix.Length)..];
        if (rest.Length > 0 && (rest[0] != '.' || rest.AsSpan().ContainsAny('/', '\\')))
            return false;
        stripped = path[..at];
        return true;
    }
}
