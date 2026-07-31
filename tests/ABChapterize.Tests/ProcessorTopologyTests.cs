// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Concurrency;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="ProcessorTopology"/>, which supplies the default for both thread-count
/// options. The real core count of the machine running the tests is unknown, so what is asserted is
/// the contract every caller relies on rather than a number: a usable value, never above what the
/// process is actually allowed to use, and the same answer every time.
/// </summary>
public class ProcessorTopologyTests
{
    [Fact]
    public void PhysicalCoreCount_IsUsableAndNeverExceedsTheLogicalCount()
    {
        // The upper bound is the interesting half: Environment.ProcessorCount honours cgroup quotas
        // and affinity masks while a hardware topology query does not, so a core count above it
        // would mean the clamp that exists for containerized runs has stopped working.
        Assert.InRange(ProcessorTopology.PhysicalCoreCount, 1, Environment.ProcessorCount);
    }

    [Fact]
    public void PhysicalCoreCount_IsStable()
    {
        Assert.Equal(ProcessorTopology.PhysicalCoreCount, ProcessorTopology.PhysicalCoreCount);
    }
}
