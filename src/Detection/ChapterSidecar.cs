// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using ABChapterize.Audio;

namespace ABChapterize.Detection;

/// <summary>
/// Reads and writes the chapter sidecar files used by --export/--import: a per-file
/// companion that lets a rare misdetection be hand-corrected and re-applied without
/// re-running Whisper. Two formats, chosen by --simple-metadata:
/// <list type="bullet">
/// <item>default: ffmpeg's own FFMETADATA1 (see <see cref="FfmpegClient.BuildFfMetadata"/>),
/// readable/writable by ffmpeg itself with no glue code on the write side.</item>
/// <item>--simple-metadata: a plain "H:MM:SS.fff  Title" line per chapter, the de facto
/// format for manual audiobook chapter editing (m4b-tool, mp4chaps -e) and easier to
/// hand-edit, at the cost of a small parser here.</item>
/// </list>
/// </summary>
public static partial class ChapterSidecar
{
    /// <summary>Sidecar file path for an audio file, in the format selected by <paramref name="simple"/>.</summary>
    /// <param name="audioFile">Path of the audio file the sidecar belongs to.</param>
    /// <param name="simple">True for the plain-text format, false for FFMETADATA.</param>
    public static string PathFor(string audioFile, bool simple)
        => audioFile + (simple ? ".chapters.txt" : ".chapters.ffmeta");

    /// <summary>Builds the plain-text sidecar: one "H:MM:SS.fff  Title" line per chapter.</summary>
    /// <param name="chapters">Chapter markings sorted by start time.</param>
    public static string BuildSimple(IReadOnlyList<Chapter> chapters)
    {
        var sb = new StringBuilder();
        foreach (var c in chapters)
            sb.Append(FormatTimestamp(c.StartSeconds)).Append("  ").Append(c.Title.Replace('\n', ' ')).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Parses the plain-text sidecar format. Blank lines and lines starting with ';' or
    /// '#' are ignored, matching the mp4chaps/m4b-tool convention.
    /// </summary>
    /// <param name="text">Raw sidecar file content.</param>
    /// <param name="sidecarPath">Path of the sidecar file, for error messages.</param>
    /// <exception cref="AppError">Thrown on a malformed line, or when no chapters resulted.</exception>
    public static List<Chapter> ParseSimple(string text, string sidecarPath)
    {
        var chapters = new List<Chapter>();
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;
            var m = SimpleLineRegex().Match(line);
            if (!m.Success)
                throw new AppError(
                    $"Invalid line {lineNo} in \"{sidecarPath}\": expected \"H:MM:SS[.fff]  Title\".");
            var hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            chapters.Add(new Chapter(hours * 3600 + minutes * 60 + seconds, m.Groups[4].Value));
        }
        if (chapters.Count == 0)
            throw new AppError($"Sidecar file \"{sidecarPath}\" contains no chapters.");
        return chapters;
    }

    /// <summary>Matches one "H:MM:SS[.fff]  Title" sidecar line.</summary>
    [GeneratedRegex(@"^(\d+):([0-5]\d):([0-5]\d(?:\.\d+)?)\s+(.+)$")]
    private static partial Regex SimpleLineRegex();

    /// <summary>
    /// Parses an FFMETADATA1 sidecar into its chapter list. Only [CHAPTER] sections and
    /// their TIMEBASE/START/title keys are read; everything else (global tags, [STREAM]
    /// sections) is ignored. A title containing a literal embedded newline - written by
    /// our own <see cref="FfmpegClient.BuildFfMetadata"/> as a backslash followed by a
    /// real line break - is not reconstructed across lines; chapter titles never contain
    /// newlines in this tool's own output, so this is not a practical limitation.
    /// </summary>
    /// <param name="text">Raw sidecar file content.</param>
    /// <param name="sidecarPath">Path of the sidecar file, for error messages.</param>
    /// <exception cref="AppError">Thrown when no chapters resulted.</exception>
    public static List<Chapter> ParseFfMetadata(string text, string sidecarPath)
    {
        var chapters = new List<Chapter>();
        var inChapter = false;
        double timebaseNum = 1, timebaseDen = 1000;
        double? start = null;
        string? title = null;

        void Flush()
        {
            if (inChapter && start is { } s && title != null)
                chapters.Add(new Chapter(s * timebaseNum / timebaseDen, title));
            start = null;
            title = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                inChapter = line == "[CHAPTER]";
                if (inChapter)
                {
                    timebaseNum = 1;
                    timebaseDen = 1000;
                }
                continue;
            }
            if (!inChapter)
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var value = line[(eq + 1)..];
            switch (line[..eq])
            {
                case "TIMEBASE":
                    var parts = value.Split('/');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d)
                        && d != 0)
                    {
                        timebaseNum = n;
                        timebaseDen = d;
                    }
                    break;
                case "START":
                    if (double.TryParse(value, CultureInfo.InvariantCulture, out var sVal))
                        start = sVal;
                    break;
                case "title":
                    title = FfmpegClient.UnescapeMeta(value);
                    break;
            }
        }
        Flush();

        if (chapters.Count == 0)
            throw new AppError($"Sidecar file \"{sidecarPath}\" contains no chapters.");
        return chapters;
    }

    /// <summary>Formats a chapter mark position as h:mm:ss.fff for the plain-text sidecar
    /// (millisecond precision, matching FFMETADATA's own timebase, for lossless round-trips).</summary>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.fff");
}
