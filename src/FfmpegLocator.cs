namespace Chapterize;

/// <summary>
/// Locates the ffmpeg and ffprobe executables using a list of well-known, OS-specific
/// locations with a fallback to the FFMPEG_DIR environment variable.
/// </summary>
public static class FfmpegLocator
{
    /// <summary>Platform-specific file name of the ffmpeg executable.</summary>
    private static string FfmpegName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>Platform-specific file name of the ffprobe executable.</summary>
    private static string FfprobeName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>
    /// Searches for ffmpeg and ffprobe in PATH, an "ffmpeg" folder near the current
    /// directory, the executable and the user profile, common OS-specific install
    /// locations, and finally FFMPEG_DIR.
    /// </summary>
    /// <returns>Tuple with the full paths of the ffmpeg and ffprobe executables.</returns>
    /// <exception cref="AppError">Thrown when the tools cannot be found; the message
    /// contains OS-specific installation hints.</exception>
    public static (string Ffmpeg, string Ffprobe) Locate()
    {
        foreach (var dir in CandidateDirectories())
        {
            var ffmpeg = Path.Combine(dir, FfmpegName);
            var ffprobe = Path.Combine(dir, FfprobeName);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return (ffmpeg, ffprobe);
        }

        throw new AppError(OperatingSystem.IsWindows()
            ? "ffmpeg/ffprobe could not be found. Searched PATH, .\\ffmpeg\\bin, " +
              "%USERPROFILE%\\ffmpeg\\bin, Program Files and %FFMPEG_DIR%\\bin.\n" +
              "Hint: set the environment variable FFMPEG_DIR to ffmpeg's base directory " +
              "(the directory that contains the \"bin\" folder), e.g. set FFMPEG_DIR=C:\\Tools\\ffmpeg"
            : "ffmpeg/ffprobe could not be found. Searched PATH, ./ffmpeg, ~/ffmpeg, /usr/bin, " +
              "/usr/local/bin, /opt/ffmpeg, /snap/bin and $FFMPEG_DIR.\n" +
              "Hint: install ffmpeg with your package manager (e.g. sudo apt install ffmpeg) " +
              "or set the environment variable FFMPEG_DIR to the directory containing the binaries.");
    }

    /// <summary>Enumerates all candidate directories in search order (duplicates possible, harmless).</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        // 1. PATH (';'-separated on Windows, ':'-separated on Linux).
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return p;

        // 2. An "ffmpeg" folder next to the current directory, the executable and the user
        //    profile. Windows release zips contain a "bin" subfolder, Linux static builds
        //    are flat; check both layouts everywhere.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory, profile })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            yield return Path.Combine(root, "ffmpeg", "bin");
            yield return Path.Combine(root, "ffmpeg");
        }

        if (OperatingSystem.IsWindows())
        {
            // 3a. Common Program Files locations.
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
        }
        else
        {
            // 3b. Common Linux locations: package managers install flat into /usr/bin,
            //     manual installs go to /usr/local/bin, static builds are often unpacked
            //     to /opt/ffmpeg, snap exposes /snap/bin.
            yield return "/usr/bin";
            yield return "/usr/local/bin";
            yield return "/opt/ffmpeg/bin";
            yield return "/opt/ffmpeg";
            yield return "/snap/bin";
            if (!string.IsNullOrEmpty(profile))
            {
                yield return Path.Combine(profile, "bin");
                yield return Path.Combine(profile, ".local", "bin");
            }
        }

        // 4. Fallback: FFMPEG_DIR points to ffmpeg's base directory (not the bin directory).
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
