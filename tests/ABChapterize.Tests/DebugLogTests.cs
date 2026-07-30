// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Ui;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="DebugLog"/> and the <c>--debug</c> option behind it: where the file lands,
/// what its header records, and that the option stays off the user-facing lists it is deliberately
/// missing from.
/// </summary>
public sealed class DebugLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one audio file to write a debug log beside.</summary>
    public DebugLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The probe result the header tests describe.</summary>
    private static MediaInfo Info
        => new(3723.5, 12345678, 2, "aac", "LC", InputDecoder: "libfdk_aac");

    /// <summary>Parses options for the temp file, which <see cref="CliOptions.Parse"/> requires to
    /// exist.</summary>
    /// <param name="options">Options preceding the target.</param>
    private CliOptions Parse(params string[] options)
        => CliOptions.Parse([.. options, _file])!;

    /// <summary>Opens a debug log for the temp file, closes it, and returns what it contains.</summary>
    /// <param name="options">The run's options.</param>
    private string OpenAndRead(CliOptions options)
    {
        using (var log = DebugLog.Open(_file, options, Info))
            log.Write("something happened");
        return File.ReadAllText(DebugLog.PathFor(_file));
    }

    [Fact]
    public void TheLog_LandsBesideItsAudioFile()
        => Assert.Equal(_file + ".debug.log", DebugLog.PathFor(_file));

    [Fact]
    public void TheHeader_NamesTheFileAndWhatTheProbeFound()
    {
        var text = OpenAndRead(Parse("--debug"));
        Assert.Contains(_file, text);
        Assert.Contains("1:02:03", text);          // the duration, via TimeFormat
        Assert.Contains("codec aac (LC)", text);
        Assert.Contains("2 existing chapter marking(s)", text);
        Assert.Contains("decoder libfdk_aac", text);
    }

    [Fact]
    public void TheHeader_RecordsTheSettingsDetectionActuallyRanWith()
    {
        // Resolved, not as typed: --lang de replaces the phrase and title defaults, and it is those
        // replacements a reader needs to see to make sense of the rest of the file.
        var text = OpenAndRead(Parse("--debug", "--lang", "de", "--quick-marks"));
        Assert.Contains("lang de", text);
        Assert.Contains("Kapitel", text);
        Assert.Contains("mark-refinement off (--quick-marks)", text);
    }

    [Fact]
    public void TheHeader_ListsEveryCustomMapping()
    {
        var text = OpenAndRead(Parse("--debug", "--custom", "/Zeittafel/:Zeittafel"));
        Assert.Contains("custom", text);
        Assert.Contains("Zeittafel", text);
    }

    [Fact]
    public void WrittenLines_AreTimestampedAndAppended()
    {
        OpenAndRead(Parse("--debug"));
        var text = OpenAndRead(Parse("--debug"));
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] something happened", text);
        // A second run must not erase the first one's record - the same bargain LogFile makes.
        Assert.Equal(2, text.Split("something happened").Length - 1);
    }

    [Fact]
    public void TheOption_TurnsLoggingOnByItself()
    {
        // Without this, every "only build the message if someone is listening" guard in the
        // detector would skip the very lines --debug exists to collect.
        var options = Parse("--debug");
        Assert.True(options.Debug);
        Assert.True(options.LoggingEnabled);
        Assert.False(options.Verbose);
    }

    [Fact]
    public void TheOption_IsOffByDefault()
    {
        var options = Parse();
        Assert.False(options.Debug);
        Assert.False(options.LoggingEnabled);
    }

    [Fact]
    public void TheOption_StaysOutOfTheHelpText()
    {
        // Deliberate: it is the option one is told to use, not one to pick off a list. If it ever
        // becomes advertised, this test is the place that decision gets made consciously.
        Assert.DoesNotContain("--debug", CliOptions.UsageText);
    }

    [Fact]
    public void TheOption_ClaimsNoShortForm()
    {
        // The single letters belong to options people choose for themselves, so -d stays --dry-run.
        var options = CliOptions.Parse(["-d", _file])!;
        Assert.True(options.DryRun);
        Assert.False(options.Debug);
    }
}
