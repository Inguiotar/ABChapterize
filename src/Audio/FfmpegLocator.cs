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

        throw new AppError(NotFoundMessage());
    }

    /// <summary>
    /// The "not found" text for the platform this is running on.
    /// </summary>
    /// <remarks>
    /// Three separate texts rather than one generic sentence because this message is the only help
    /// a user gets at this point: it has to list what <see cref="CandidateDirectories"/> actually
    /// just searched, and name an installation route that exists on their machine. A hint naming
    /// apt on a Mac reads as the tool not knowing what it is running on.
    /// <para>
    /// They are separate <c>internal</c> members rather than branches inside one method so that all
    /// three can be asserted from any host. That is the point: the macOS text is unreachable on the
    /// two platforms this project can run, so without it the wrong-package-manager bug could only
    /// be found by a Mac user hitting it.
    /// </para>
    /// </remarks>
    private static string NotFoundMessage()
        => OperatingSystem.IsWindows() ? WindowsNotFoundMessage
         : OperatingSystem.IsMacOS() ? MacNotFoundMessage
         : LinuxNotFoundMessage;

    /// <summary>Windows "not found" text.</summary>
    internal static string WindowsNotFoundMessage =>
        "ffmpeg/ffprobe could not be found. Searched %FFMPEG_DIR%\\bin and %FFMPEG_DIR% " +
        "itself, PATH, an \"ffmpeg\" folder in the current directory, next to " +
        "abchapterize.exe and in %USERPROFILE%, and Program Files.\n" +
        "Hint: set the environment variable FFMPEG_DIR to ffmpeg's base directory or " +
        "straight to the directory holding the binaries - both work, e.g. " +
        "set FFMPEG_DIR=C:\\Tools\\ffmpeg";

    /// <summary>macOS "not found" text.</summary>
    internal static string MacNotFoundMessage =>
        "ffmpeg/ffprobe could not be found. Searched $FFMPEG_DIR/bin and $FFMPEG_DIR " +
        "itself, PATH, ./ffmpeg, ~/ffmpeg, /opt/homebrew/bin, /usr/local/bin, " +
        "/opt/local/bin, /usr/bin, ~/bin and ~/.local/bin.\n" +
        "Hint: install ffmpeg with Homebrew (brew install ffmpeg) or MacPorts " +
        "(sudo port install ffmpeg), or set the environment variable FFMPEG_DIR to the " +
        "directory containing the binaries.";

    /// <summary>Linux (and any other Unix) "not found" text.</summary>
    internal static string LinuxNotFoundMessage =>
        "ffmpeg/ffprobe could not be found. Searched $FFMPEG_DIR/bin and $FFMPEG_DIR " +
        "itself, PATH, ./ffmpeg, ~/ffmpeg, /usr/bin, /usr/local/bin, /opt/ffmpeg/bin, " +
        "/opt/ffmpeg, /snap/bin, ~/bin and ~/.local/bin.\n" +
        "Hint: install ffmpeg with your package manager (e.g. sudo apt install ffmpeg) " +
        "or set the environment variable FFMPEG_DIR to the directory containing the binaries.";

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

        // 1. PATH (';'-separated on Windows, ':'-separated on Linux). A Windows entry may legally
        //    be wrapped in double quotes - cmd strips them, and some installers write them - so
        //    they come off here too. Left in place they simply make File.Exists answer false for
        //    every candidate in that directory, and the run then reports PATH as searched having
        //    silently skipped the one entry that held ffmpeg.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // An entry that was nothing but quotes must not fall through as "", which would search
            // the current directory - a place PATH never asked for.
            var unquoted = p.Trim('"');
            if (unquoted.Length > 0)
                yield return unquoted;
        }

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
        else if (OperatingSystem.IsMacOS())
        {
            // 3b. macOS has no system ffmpeg, so every install is somebody's package manager:
            //     Homebrew's prefix is /opt/homebrew on Apple Silicon and /usr/local on Intel,
            //     MacPorts uses /opt/local. All of them put their prefix on PATH for an
            //     interactive shell, so this list only earns its keep where PATH is not the
            //     user's - a launchd agent, or a run started from an app that inherited a bare
            //     environment.
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";
            yield return "/usr/bin";
            if (!string.IsNullOrEmpty(profile))
            {
                yield return Path.Combine(profile, "bin");
                yield return Path.Combine(profile, ".local", "bin");
            }
        }
        else
        {
            // 3c. Common Linux locations: package managers install flat into /usr/bin,
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
