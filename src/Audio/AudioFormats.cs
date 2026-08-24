// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Audio;

/// <summary>
/// Which container formats can hold chapter marks - the one question that decides what a run
/// working on files is allowed to touch.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from what ffmpeg can <em>decode</em>, because those are two different limits and
/// only one of them applies everywhere. Detection needs a decode and nothing more; it is the
/// writing of the result that needs a container able to carry chapters. Wherever the marks end
/// up somewhere other than the file - Audiobookshelf mode, where they go into the server's
/// database and the downloaded copy is a throwaway - this list does not apply and must not be
/// consulted, or a perfectly workable book is passed over for a limitation that is not in play.
/// </para>
/// <para>
/// The list is what ffmpeg can both read and write chapter marks for, verified empirically
/// through the exact remux command <see cref="FfmpegClient.WriteChaptersAsync"/> issues:
/// mp4/ipod, ID3v2 mp3, Ogg Opus, Matroska. Notably absent are <c>.ogg</c> (Vorbis) and
/// <c>.flac</c>, whose muxers accept chapters and silently drop them - the worst failure shape
/// there is, and the reason this is a list rather than an attempt-and-see.
/// </para>
/// </remarks>
public static class AudioFormats
{
    /// <summary>The extensions of the containers chapter marks can be written into, lower-case.</summary>
    public static readonly string[] ChapterCapable = [".m4a", ".m4b", ".mp3", ".opus", ".mka"];

    /// <summary>The same list for a message, e.g. ".m4a/.m4b/.mp3/.opus/.mka".</summary>
    public static string ChapterCapableText => string.Join("/", ChapterCapable);

    /// <summary>Whether a file's container can carry the marks a run would write into it.</summary>
    /// <param name="path">Path or name of the file; only its extension is read.</param>
    public static bool CanHoldChapters(string path)
        => ChapterCapable.Contains(Path.GetExtension(path).ToLowerInvariant());
}
