// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Gpu;

/// <summary>
/// Outcome of resolving a GPU choice: exactly one of <see cref="Device"/> and <see cref="Error"/>
/// is meaningful, and both being null means "no opinion, let the backend do what it always did".
/// </summary>
/// <param name="Device">The device to use, or null for the backend's own default.</param>
/// <param name="Error">Why an explicit request could not be honoured, ready to print, or null on success.</param>
/// <param name="WasRequested">True when the user named a device, false when this came from
/// automatic selection - the two deserve different reporting, since only the first is worth
/// confirming out loud.</param>
public sealed record GpuSelection(GpuDevice? Device, string? Error, bool WasRequested);

/// <summary>
/// Turns "--use-gpu gtx" (or no request at all) into a concrete device index for the Whisper
/// backend.
/// </summary>
/// <remarks>
/// Deliberately free of any Vulkan or Whisper dependency: it takes a device list and a request
/// string and returns a decision, which is what makes the matching and the automatic preference
/// testable without a GPU present.
/// </remarks>
public static class GpuSelector
{
    /// <summary>
    /// Resolves a device from the enumerated list and the user's <c>--use-gpu</c> argument, if any.
    /// </summary>
    /// <param name="devices">Devices as enumerated, in backend index order; may be empty.</param>
    /// <param name="request">The user's <c>--use-gpu</c> text, or null when they did not ask for one.</param>
    /// <remarks>
    /// With no request this prefers the first discrete GPU, which is the whole point of enumerating
    /// at all: the backend's own default is "device 0", and on a desktop with an integrated GPU
    /// that is a coin flip the user never knew they were taking part in - measured at 8.6x on the
    /// project's test box. A machine with one GPU, or with none that call themselves discrete, gets
    /// no opinion imposed on it.
    /// </remarks>
    public static GpuSelection Select(IReadOnlyList<GpuDevice> devices, string? request)
    {
        if (string.IsNullOrWhiteSpace(request))
            return new GpuSelection(AutomaticChoice(devices), Error: null, WasRequested: false);

        if (devices.Count == 0)
            return new GpuSelection(null, "no Vulkan GPUs found, so --use-gpu cannot be honoured", WasRequested: true);

        return MatchRequest(devices, request.Trim());
    }

    /// <summary>
    /// Works out which device <c>GGML_VK_VISIBLE_DEVICES</c> leaves the backend running on, so that
    /// a run deferring to it can still name its GPU instead of falling silent.
    /// </summary>
    /// <param name="devices">Devices as enumerated, in backend index order; may be empty.</param>
    /// <param name="visibleDevices">The variable's raw value.</param>
    /// <returns>The device the backend will default to, or null when the value does not resolve to
    /// one - in which case the caller should name the variable rather than a device.</returns>
    /// <remarks>
    /// The variable is a comma-separated list of indices into the very enumeration this class is
    /// handed, and the backend keeps only those devices, in that order - so its device 0, the one it
    /// uses unless told otherwise, is the first entry. Reading it here is safe despite the
    /// enumeration order differing between session types (see <see cref="GpuDevice"/>), because both
    /// the variable and this lookup are resolved inside the session the backend runs in.
    /// <para>
    /// Confirmed on a two-GPU box against per-GPU load rather than the wall clock, and ggml says as
    /// much itself: an out-of-range index aborts the run with
    /// <c>ggml_vulkan: Invalid device index N in GGML_VK_VISIBLE_DEVICES</c>, so the value is
    /// bounds-checked against the same physical-device list, not a free-form filter.
    /// <include file='../../notes/Gpu/GpuSelector.xml' path='doc/member[@name="ResolveVisibleDevices"]/*' />
    /// </para>
    /// <para>
    /// Anything unparseable or out of range yields null rather than a guess. This is a variable
    /// people set by hand, and a wrong name in the banner would be worse than no name at all - the
    /// silent fallback it replaces (2026-07-29) already cost one confused test run on the remote
    /// machine, where a bare "Vulkan backend" was indistinguishable from GPU naming being broken.
    /// </para>
    /// </remarks>
    public static GpuDevice? ResolveVisibleDevices(IReadOnlyList<GpuDevice> devices, string visibleDevices)
    {
        var first = visibleDevices.Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, out var index)
            ? devices.FirstOrDefault(d => d.Index == index)
            : null;
    }

    /// <summary>
    /// Picks a device without being asked: the first discrete one when there is a choice to make.
    /// </summary>
    /// <param name="devices">Devices as enumerated.</param>
    private static GpuDevice? AutomaticChoice(IReadOnlyList<GpuDevice> devices)
    {
        // One device cannot be picked wrongly, and saying nothing keeps the backend on the exact
        // path it took before this option existed.
        if (devices.Count < 2)
            return null;

        var discrete = devices.Where(d => d.Kind == GpuDeviceKind.Discrete).ToList();

        // Several discrete cards is a genuine choice between comparable things, which is the
        // user's to make with --use-gpu rather than ours to guess by enumeration order.
        return discrete.Count == 1 ? discrete[0] : null;
    }

    /// <summary>
    /// Matches an explicit request, either a bare index or a case-insensitive substring of a
    /// device name.
    /// </summary>
    /// <param name="devices">Devices as enumerated.</param>
    /// <param name="request">Trimmed, non-empty request text.</param>
    private static GpuSelection MatchRequest(IReadOnlyList<GpuDevice> devices, string request)
    {
        // A bare number is an index, but only if the machine actually has one - which is what keeps
        // "--use-gpu 1070" meaning the GeForce rather than failing as an out-of-range index.
        // Model numbers are the obvious thing to type and are never small enough to collide with a
        // real index; the index reading exists for the one case names cannot serve, two identical
        // cards in one machine.
        if (int.TryParse(request, out var index) && devices.FirstOrDefault(d => d.Index == index) is { } byIndex)
            return new GpuSelection(byIndex, null, WasRequested: true);

        var matches = devices
            .Where(d => d.Name.Contains(request, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => new GpuSelection(matches[0], null, WasRequested: true),
            0 => Failure(devices, $"no GPU matches \"{request}\""),
            _ => Failure(devices, $"\"{request}\" matches {matches.Count} GPUs"),
        };
    }

    /// <summary>
    /// Builds the failure message, always listing what is actually available - a user who typed the
    /// wrong thing needs the real names more than they need the complaint.
    /// </summary>
    /// <param name="devices">Devices as enumerated.</param>
    /// <param name="reason">What went wrong, without trailing punctuation.</param>
    private static GpuSelection Failure(IReadOnlyList<GpuDevice> devices, string reason)
        => new(null, $"{reason}. Available: {string.Join("; ", devices)}", WasRequested: true);
}
