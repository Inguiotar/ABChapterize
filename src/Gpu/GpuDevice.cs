// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Gpu;

/// <summary>
/// What kind of device the Vulkan driver reports, in the order Vulkan itself defines
/// (VkPhysicalDeviceType). Only <see cref="Discrete"/> and <see cref="Integrated"/> matter to
/// automatic selection; the rest exist so an unexpected value prints as something better than a
/// bare number.
/// </summary>
public enum GpuDeviceKind
{
    /// <summary>Neither the driver nor we can say - treated as "not a candidate for automatic selection".</summary>
    Other = 0,
    /// <summary>An integrated GPU, i.e. one sharing the CPU's memory. Typically the slow option.</summary>
    Integrated = 1,
    /// <summary>A discrete GPU on its own card with its own memory. Typically the fast option.</summary>
    Discrete = 2,
    /// <summary>A virtualized GPU, e.g. inside a VM.</summary>
    Virtual = 3,
    /// <summary>A software rasterizer pretending to be a GPU - slower than the real CPU backend.</summary>
    Cpu = 4,
}

/// <summary>
/// One GPU as the Vulkan driver enumerated it, together with the <see cref="Index"/> the Whisper
/// backend needs to select it.
/// </summary>
/// <remarks>
/// <para>
/// The index is the position in this process's own enumeration and nothing more durable than
/// that. It is emphatically not a property of the machine: on the project's two-GPU test box an
/// interactive desktop session and an SSH session enumerated the two cards in *opposite* order,
/// verified from both ends by GPU load and memory monitoring. Hence
/// <see cref="ABChapterize.Cli.CliOptions.UseGpu"/> matching on <see cref="Name"/>: an index a user
/// looked up once is worthless the next time they log in differently, a name is not.
/// <include file='../../notes/Gpu/GpuDevice.xml' path='doc/member[@name="Index"]/*' />
/// </para>
/// <para>
/// Enumerate and use within the same process, never persist.
/// </para>
/// </remarks>
/// <param name="Index">Position in this process's Vulkan enumeration, as
/// <c>WhisperFactoryOptions.GpuDevice</c> expects it.</param>
/// <param name="Name">The driver's own name for the device, e.g. "NVIDIA GeForce GTX 1070".</param>
/// <param name="Kind">Discrete, integrated, …, as reported by the driver.</param>
public sealed record GpuDevice(int Index, string Name, GpuDeviceKind Kind)
{
    /// <summary>
    /// One line for <c>--list-gpus</c> and for the error listing a failed <c>--use-gpu</c> match,
    /// e.g. <c>1: NVIDIA GeForce GTX 1070 (discrete)</c>.
    /// </summary>
    public override string ToString() => $"{Index}: {Name} ({Kind.ToString().ToLowerInvariant()})";
}
