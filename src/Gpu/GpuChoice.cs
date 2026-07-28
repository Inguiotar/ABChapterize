// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Gpu;

/// <summary>
/// What a run decided about GPUs: which device to impose on the backend, and which one to name in
/// the startup line.
/// </summary>
/// <remarks>
/// The two differ whenever the backend's own default is left in place - a single Vulkan device, or
/// several discrete ones that only the user can choose between. Nothing is imposed there, but the
/// device that will be used is known all the same, and saying so is what turns a wrong GPU into
/// something visible in the first line of output instead of only in the wall clock.
/// </remarks>
/// <param name="Selected">Device to hand the Whisper backend, or null to leave its default alone.</param>
/// <param name="Reported">Device the run is expected to use, for the banner; null when no Vulkan
/// device exists, or when the choice was skipped entirely.</param>
/// <param name="DeferredTo">The <c>GGML_VK_VISIBLE_DEVICES</c> setting this run stepped aside for,
/// as <c>NAME=value</c>, or null when nothing did. Worth printing even alongside a resolved
/// <paramref name="Reported"/>: the device then came from an environment variable rather than from
/// this tool's own preference, which is what someone wondering why <c>--use-gpu</c> is refused
/// needs to see.</param>
public sealed record GpuChoice(GpuDevice? Selected, GpuDevice? Reported, string? DeferredTo = null)
{
    /// <summary>No GPU opinion at all, and nothing to say about one - CPU-only runs.</summary>
    public static GpuChoice None { get; } = new(null, null);
}
