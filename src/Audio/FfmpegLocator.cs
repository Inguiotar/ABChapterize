// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;

namespace ABChapterize.Audio;

/// <summary>
/// Locates the ffmpeg and ffprobe executables in the FFMPEG_DIR environment variable, then in
/// a list of well-known, OS-specific locations.
/// </summary>
public static class FfmpegLocator
{
    /// <summary>Platform-specific file name of the ffmpeg executable.</summary>
    private static string FfmpegName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>Platform-specific file name of the ffprobe executable.</summary>
    private static string FfprobeName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>
    /// Searches for ffmpeg and ffprobe in FFMPEG_DIR and its "bin" subfolder (which, as the
    /// explicit user choice, override everything else), PATH, an "ffmpeg" folder near the current directory,
    /// the executable and the user profile, and common OS-specific install locations.
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
            ? "ffmpeg/ffprobe could not be found. Searched %FFMPEG_DIR%\\bin and %FFMPEG_DIR% " +
              "itself, PATH, an \"ffmpeg\" folder in the current directory, next to " +
              "abchapterize.exe and in %USERPROFILE%, and Program Files.\n" +
              "Hint: set the environment variable FFMPEG_DIR to ffmpeg's base directory or " +
              "straight to the directory holding the binaries - both work, e.g. " +
              "set FFMPEG_DIR=C:\\Tools\\ffmpeg"
            : "ffmpeg/ffprobe could not be found. Searched $FFMPEG_DIR/bin and $FFMPEG_DIR " +
              "itself, PATH, ./ffmpeg, ~/ffmpeg, /usr/bin, /usr/local/bin, /opt/ffmpeg/bin, " +
              "/opt/ffmpeg, /snap/bin, ~/bin and ~/.local/bin.\n" +
              "Hint: install ffmpeg with your package manager (e.g. sudo apt install ffmpeg) " +
              "or set the environment variable FFMPEG_DIR to the directory containing the binaries.");
    }

    /// <summary>Enumerates all candidate directories in search order (duplicates possible, harmless).</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        // 0. FFMPEG_DIR may point either at ffmpeg's base directory or straight at the
        //    directory holding the binaries; both are supported, since Windows release zips
        //    nest them under "bin" while Linux static builds are flat, and nobody should have
        //    to know which layout they unpacked. As the explicit user choice it has the
        //    highest priority, e.g. to select a custom ffmpeg build over an installed one.
        var ffmpegDir = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            yield return Path.Combine(ffmpegDir, "bin");
            yield return ffmpegDir;
        }

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
    }
}
