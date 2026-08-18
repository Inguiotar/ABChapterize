// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Hooks;

/// <summary>
/// One "$..." placeholder out of a <c>--run-before</c> / <c>--run-after</c> command line, and the
/// piece of a file's path it stands for.
/// </summary>
/// <remarks>
/// The grammar counts <i>path elements</i>, and the root - "C:\", "\server\share\", "/" - counts as
/// one of them. That is what makes "$3" and "$99" both resolve to the whole path of
/// "c:\test\buch.mp3" while "$2" stops at "test\buch.mp3", and it is why a count larger than the
/// path has elements is clamped rather than rejected: a command line is written once and then run
/// against files at any depth, so "give me everything you have" has to be spellable without knowing
/// how deep the deepest one sits.
/// </remarks>
/// <param name="Directory">True for the "$-n" form, which names a folder rather than a file.</param>
/// <param name="Count">How many path elements the placeholder keeps ("$n") or drops ("$-n").</param>
internal readonly record struct PathPlaceholder(bool Directory, int Count)
{
    /// <summary>
    /// Whether this placeholder means anything. Only "$-0" does not: "the path without its last
    /// zero elements" would be the file's own path presented as a folder.
    /// </summary>
    public bool IsValid => !Directory || Count > 0;

    /// <summary>How this placeholder was written, for error messages.</summary>
    public override string ToString() => Directory ? $"$-{Count}" : $"${Count}";

    /// <summary>
    /// Reads a placeholder out of <paramref name="text"/>, starting at the character after the "$".
    /// </summary>
    /// <param name="text">The whole command line template.</param>
    /// <param name="start">Index of the first character after the "$".</param>
    /// <param name="placeholder">The placeholder read, valid only when this returns true. May still
    /// be <see cref="IsValid"/> false - that is a malformed placeholder rather than no placeholder,
    /// and the two get very different treatment.</param>
    /// <param name="length">How many characters were consumed after the "$".</param>
    /// <returns>False when what follows the "$" is not placeholder syntax at all - which is the
    /// ordinary case for "$HOME" and friends, and why those pass through untouched.</returns>
    public static bool TryParse(string text, int start, out PathPlaceholder placeholder, out int length)
    {
        placeholder = default;
        length = 0;
        var i = start;
        var directory = i < text.Length && text[i] == '-';
        if (directory)
            i++;
        var digits = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;
        if (i == digits)
            return false;
        // Clamped rather than refused: past the length of any real path every count means the same
        // thing, so there is nothing for an error to warn about.
        var count = int.TryParse(text.AsSpan(digits, i - digits), out var parsed) ? parsed : int.MaxValue;
        placeholder = new PathPlaceholder(directory, count);
        length = i - start;
        return true;
    }

    /// <summary>
    /// Resolves this placeholder against one file.
    /// </summary>
    /// <param name="fullPath">The file's absolute path. Absolute rather than as-given because the
    /// element counting has to have a root to stop at, and because a hook runs a command that may
    /// well change directory on its way.</param>
    public string Resolve(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var names = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        // The root is element 0; the file name is the last one.
        if (Directory)
            return root + Join(names.Take(Math.Max(0, names.Length - Count)));
        if (Count == 0)
            return Path.GetFileNameWithoutExtension(fullPath);
        return Count > names.Length
            ? root + Join(names)
            : Join(names.Skip(names.Length - Count));
    }

    /// <summary>
    /// Joins path elements, with a trailing separator for the "$-n" form so its value can be
    /// concatenated with a file name straight away, and so that "$-2" of "c:\test\buch.mp3" reads
    /// as the folder "c:\" rather than as the drive letter.
    /// </summary>
    /// <param name="names">The path elements to join, in order.</param>
    private string Join(IEnumerable<string> names)
    {
        var joined = string.Join(Path.DirectorySeparatorChar, names);
        if (!Directory || joined.Length == 0)
            return joined;
        return joined + Path.DirectorySeparatorChar;
    }
}
