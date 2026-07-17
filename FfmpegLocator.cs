namespace Chapterize;

/// <summary>
/// Locates the ffmpeg and ffprobe executables using a list of well-known locations
/// with a fallback to the FFMPEG_DIR environment variable.
/// </summary>
public static class FfmpegLocator
{
    /// <summary>
    /// Searches for ffmpeg.exe and ffprobe.exe.
    /// Search order: PATH, .\ffmpeg\bin, %USERPROFILE%\ffmpeg\bin, common Program Files
    /// locations, and finally %FFMPEG_DIR%\bin.
    /// </summary>
    /// <returns>Tuple with the full paths of ffmpeg.exe and ffprobe.exe.</returns>
    /// <exception cref="AppError">Thrown when the tools cannot be found; the message
    /// contains a hint about the FFMPEG_DIR environment variable.</exception>
    public static (string Ffmpeg, string Ffprobe) Locate()
    {
        foreach (var dir in CandidateDirectories())
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return (ffmpeg, ffprobe);
        }

        throw new AppError(
            "ffmpeg/ffprobe could not be found. Searched PATH, .\\ffmpeg\\bin, " +
            "%USERPROFILE%\\ffmpeg\\bin, Program Files and %FFMPEG_DIR%\\bin.\n" +
            "Hint: set the environment variable FFMPEG_DIR to ffmpeg's base directory " +
            "(the directory that contains the \"bin\" folder), e.g. set FFMPEG_DIR=C:\\Tools\\ffmpeg");
    }

    /// <summary>Enumerates all candidate directories in search order (duplicates possible, harmless).</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        // 1. PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return p;

        // 2. Relative to the current directory.
        yield return Path.Combine(Environment.CurrentDirectory, "ffmpeg", "bin");

        // 3. Relative to the executable.
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin");

        // 4. User profile.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            yield return Path.Combine(profile, "ffmpeg", "bin");

        // 5. Common Program Files locations.
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(folder))
                continue;
            yield return Path.Combine(folder, "ffmpeg", "bin");
            yield return Path.Combine(folder, "ffmpeg");
        }

        // 6. Fallback: FFMPEG_DIR points to ffmpeg's base directory (not the bin directory).
        var ffmpegDir = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            yield return Path.Combine(ffmpegDir, "bin");
            yield return ffmpegDir; // tolerate FFMPEG_DIR pointing directly at bin
        }
    }
}

/// <summary>
/// Exception for fatal application errors that abort the whole run.
/// The message is printed to stderr without a stack trace.
/// </summary>
public sealed class AppError : Exception
{
    /// <summary>Creates a new fatal application error.</summary>
    /// <param name="message">Human readable description of the error.</param>
    public AppError(string message) : base(message) { }
}
