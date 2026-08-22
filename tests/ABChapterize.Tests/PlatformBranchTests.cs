// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Runtime.InteropServices;
using Xunit;
using ABChapterize.Audio;
using ABChapterize.Gpu;
using ABChapterize.Onnx;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the three places that branch on which operating system the tool is running on:
/// <see cref="FfmpegLocator"/>'s "not found" advice, <see cref="OnnxRuntimeNative"/>'s native
/// library lookup, and <see cref="VulkanDeviceEnumerator"/>'s explanation of an empty device list.
/// </summary>
/// <remarks>
/// These exist because two of the three platforms cannot be run here at all. ABChapterize ships
/// binaries for Windows and Linux only; Apple Silicon and Linux ARM64 build from source and have
/// never been executed by anyone on this project. A branch nobody can reach is a branch nobody can
/// check, so what is asserted is everything about those branches that does not require the platform
/// itself: that each one names its own paths and its own package manager, and that none of them
/// quietly hands a user advice meant for a different machine.
/// </remarks>
public class PlatformBranchTests
{
    // ---- FfmpegLocator: the right advice for the right machine -----------------------------

    [Fact]
    public void MacNotFoundMessage_NamesHomebrewAndMacPortsRatherThanApt()
    {
        var message = FfmpegLocator.MacNotFoundMessage;

        Assert.Contains("brew install ffmpeg", message);
        Assert.Contains("port install ffmpeg", message);
        // The bug this pins: macOS used to share the Linux text, so a Mac user was told to run a
        // package manager their machine does not have. Matched on the whole command, because a bare
        // "apt" also occurs inside "abchapterize".
        Assert.DoesNotContain("apt install", message);
    }

    [Fact]
    public void MacNotFoundMessage_ListsBothHomebrewPrefixesAndMacPorts()
    {
        var message = FfmpegLocator.MacNotFoundMessage;

        // Apple Silicon, Intel and MacPorts respectively. All three are searched, so all three are
        // claimed to have been searched.
        Assert.Contains("/opt/homebrew/bin", message);
        Assert.Contains("/usr/local/bin", message);
        Assert.Contains("/opt/local/bin", message);
    }

    [Fact]
    public void MacNotFoundMessage_DoesNotClaimLinuxOnlyLocationsWereSearched()
    {
        var message = FfmpegLocator.MacNotFoundMessage;

        // /opt/ffmpeg and /snap/bin are in the Linux search order and not in the macOS one.
        Assert.DoesNotContain("/opt/ffmpeg", message);
        Assert.DoesNotContain("/snap/bin", message);
    }

    [Fact]
    public void LinuxNotFoundMessage_KeepsItsOwnAdvice()
    {
        var message = FfmpegLocator.LinuxNotFoundMessage;

        Assert.Contains("apt install ffmpeg", message);
        Assert.Contains("/snap/bin", message);
        Assert.DoesNotContain("/opt/homebrew/bin", message);
        Assert.DoesNotContain("brew install", message);
    }

    [Fact]
    public void WindowsNotFoundMessage_KeepsItsOwnAdvice()
    {
        var message = FfmpegLocator.WindowsNotFoundMessage;

        Assert.Contains("FFMPEG_DIR", message);
        Assert.Contains("Program Files", message);
        Assert.DoesNotContain("brew", message);
        Assert.DoesNotContain("apt install", message);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("mac")]
    [InlineData("linux")]
    public void EveryNotFoundMessage_SaysWhatWasSearchedAndHowToOverrideIt(string platform)
    {
        var message = platform switch
        {
            "windows" => FfmpegLocator.WindowsNotFoundMessage,
            "mac" => FfmpegLocator.MacNotFoundMessage,
            _ => FfmpegLocator.LinuxNotFoundMessage,
        };

        // The shared shape: what was looked for, where, and the one escape hatch that works
        // regardless of how the machine is set up.
        Assert.Contains("ffmpeg/ffprobe could not be found", message);
        Assert.Contains("PATH", message);
        Assert.Contains("FFMPEG_DIR", message);
        Assert.Contains("Hint:", message);
    }

    // ---- OnnxRuntimeNative: finding a native nobody here can load ---------------------------

    [Fact]
    public void NativeFileName_MatchesTheHostPlatformsConvention()
    {
        var expected =
            OperatingSystem.IsWindows() ? "onnxruntime.dll"
            : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib"
            : "libonnxruntime.so";

        Assert.Equal(expected, OnnxRuntimeNative.NativeFileName);
    }

    [Fact]
    public void CandidateRuntimeIdentifiers_LeadsWithTheIdentifierTheRuntimeReports()
    {
        // The reported identifier is right on every platform this project has ever run on; the
        // second candidate exists only for the case where it is not. Trying it first keeps the
        // normal path a single File.Exists.
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, OnnxRuntimeNative.CandidateRuntimeIdentifiers().First());
    }

    [Fact]
    public void CandidateRuntimeIdentifiers_AreDistinctAndNonEmpty()
    {
        var candidates = OnnxRuntimeNative.CandidateRuntimeIdentifiers().ToList();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        // A duplicate would mean the same File.Exists twice, which is harmless but says the
        // fallback stopped being a fallback and became a copy of the first candidate.
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CandidateRuntimeIdentifiers_IncludeAPlainOsArchitectureSpelling()
    {
        var expectedOs =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        var expected = $"{expectedOs}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

        // Either the reported identifier already is the plain spelling - which is the case on both
        // released platforms - or the fallback supplies it. What must never happen is neither.
        Assert.Contains(expected, OnnxRuntimeNative.CandidateRuntimeIdentifiers());
    }

    // ---- VulkanDeviceEnumerator: why the device list is empty -------------------------------

    [Fact]
    public void AbsenceNote_PicksTheTextForTheHostPlatform()
    {
        var expected = OperatingSystem.IsMacOS()
            ? VulkanDeviceEnumerator.MacAbsenceNote
            : VulkanDeviceEnumerator.DefaultAbsenceNote;

        Assert.Equal(expected, VulkanDeviceEnumerator.AbsenceNote);
    }

    [Fact]
    public void MacAbsenceNote_DoesNotOfferCudaOnAPlatformThatHasNone()
    {
        // macOS has neither a Vulkan loader nor a CUDA native, so the usual "it will fall back to
        // CUDA or the CPU" reassurance would be half wrong there - and naming Metal is the half
        // that stops a user reading "no GPU" as "the GPU is going to waste".
        Assert.DoesNotContain("CUDA", VulkanDeviceEnumerator.MacAbsenceNote);
        Assert.Contains("Metal", VulkanDeviceEnumerator.MacAbsenceNote);
    }

    [Fact]
    public void DefaultAbsenceNote_PointsAtTheBackendsThatActuallyExistThere()
    {
        Assert.Contains("CUDA", VulkanDeviceEnumerator.DefaultAbsenceNote);
        Assert.DoesNotContain("Metal", VulkanDeviceEnumerator.DefaultAbsenceNote);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryAbsenceNote_IsAFinishedSentence(bool mac)
    {
        // Printed as a standalone line by --list-gpus and appended to a sentence by the --use-gpu
        // failure, so it has to stand on its own in both places.
        var note = mac ? VulkanDeviceEnumerator.MacAbsenceNote : VulkanDeviceEnumerator.DefaultAbsenceNote;

        Assert.False(string.IsNullOrWhiteSpace(note));
        Assert.EndsWith(".", note);
        // No leading-capital assertion: the macOS note opens with "macOS", which is spelled that
        // way at the start of a sentence too.
    }
}
