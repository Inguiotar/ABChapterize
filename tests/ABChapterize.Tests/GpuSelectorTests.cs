// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Gpu;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="GpuSelector"/>, which turns a <c>--use-gpu</c> string into a device.
/// The device lists here mirror the project's two-GPU test box, where picking the wrong one of the
/// two costs a factor of 8.6 in transcription speed.
/// </summary>
public class GpuSelectorTests
{
    private static readonly GpuDevice Intel = new(0, "Intel(R) UHD Graphics 630", GpuDeviceKind.Integrated);
    private static readonly GpuDevice Nvidia = new(1, "NVIDIA GeForce GTX 1070", GpuDeviceKind.Discrete);
    private static readonly GpuDevice Amd = new(0, "AMD Radeon(TM) 890M Graphics", GpuDeviceKind.Integrated);

    private static readonly IReadOnlyList<GpuDevice> TwoGpuBox = [Intel, Nvidia];

    [Theory]
    [InlineData("gtx")]
    [InlineData("GTX")]
    [InlineData("nvidia")]
    [InlineData("GeForce")]
    [InlineData("1070")]
    public void Select_MatchesDeviceName_CaseInsensitivelyAnywhereInIt(string request)
    {
        var selection = GpuSelector.Select(TwoGpuBox, request);

        Assert.Null(selection.Error);
        Assert.Equal(Nvidia, selection.Device);
        Assert.True(selection.WasRequested);
    }

    [Theory]
    [InlineData("uhd")]
    [InlineData("intel")]
    public void Select_CanAlsoPickTheIntegratedGpu_WhenAskedForItByName(string request)
    {
        var selection = GpuSelector.Select(TwoGpuBox, request);

        Assert.Equal(Intel, selection.Device);
    }

    [Fact]
    public void Select_PrefersTheDiscreteGpu_WhenNothingWasRequested()
    {
        var selection = GpuSelector.Select(TwoGpuBox, request: null);

        Assert.Equal(Nvidia, selection.Device);
        Assert.False(selection.WasRequested);
    }

    [Fact]
    public void Select_LeavesTheBackendAlone_OnASingleGpuMachine()
    {
        // Nothing to get wrong, so nothing is imposed - the backend keeps the exact path it took
        // before device selection existed.
        var selection = GpuSelector.Select([Amd], request: null);

        Assert.Null(selection.Device);
        Assert.Null(selection.Error);
    }

    [Fact]
    public void Select_LeavesTheBackendAlone_WhenSeveralDiscreteGpusCompete()
    {
        var second = new GpuDevice(1, "NVIDIA GeForce RTX 4090", GpuDeviceKind.Discrete);

        // Two comparable cards is a real choice, and enumeration order is no basis for making it
        // on the user's behalf.
        var selection = GpuSelector.Select([Nvidia with { Index = 0 }, second], request: null);

        Assert.Null(selection.Device);
    }

    [Fact]
    public void Select_LeavesTheBackendAlone_WhenNoGpuCallsItselfDiscrete()
    {
        var virtualGpu = new GpuDevice(1, "llvmpipe", GpuDeviceKind.Cpu);

        var selection = GpuSelector.Select([Amd, virtualGpu], request: null);

        Assert.Null(selection.Device);
    }

    [Fact]
    public void Select_ReportsAnUnmatchedRequest_AndListsWhatIsActuallyThere()
    {
        var selection = GpuSelector.Select(TwoGpuBox, "radeon");

        Assert.Null(selection.Device);
        Assert.Contains("no GPU matches \"radeon\"", selection.Error);
        Assert.Contains("Intel(R) UHD Graphics 630", selection.Error);
        Assert.Contains("NVIDIA GeForce GTX 1070", selection.Error);
    }

    [Fact]
    public void Select_RefusesAnAmbiguousRequest_RatherThanGuessing()
    {
        var second = new GpuDevice(2, "NVIDIA GeForce RTX 4090", GpuDeviceKind.Discrete);

        var selection = GpuSelector.Select([Intel, Nvidia, second], "nvidia");

        Assert.Null(selection.Device);
        Assert.Contains("matches 2 GPUs", selection.Error);
    }

    [Fact]
    public void Select_AcceptsABareIndex_ForMachinesHoldingTwoIdenticalCards()
    {
        var twins = new[] { Nvidia with { Index = 0 }, Nvidia with { Index = 1 } };

        var selection = GpuSelector.Select(twins, "1");

        Assert.Equal(1, selection.Device?.Index);
    }

    [Fact]
    public void Select_TreatsANumberAsAName_WhenNoSuchIndexExists()
    {
        // "1070" is a model number, not an index - reading it as one would fail a request that is
        // both reasonable and unambiguous.
        var selection = GpuSelector.Select(TwoGpuBox, "1070");

        Assert.Equal(Nvidia, selection.Device);
    }

    [Fact]
    public void Select_ReportsANumberThatIsNeitherAnIndexNorPartOfAName()
    {
        var selection = GpuSelector.Select(TwoGpuBox, "4090");

        Assert.Null(selection.Device);
        Assert.Contains("no GPU matches \"4090\"", selection.Error);
    }

    [Fact]
    public void Select_ReportsARequestOnAMachineWithoutVulkan()
    {
        var selection = GpuSelector.Select([], "gtx");

        Assert.Null(selection.Device);
        Assert.Contains("no Vulkan GPUs found", selection.Error);
    }

    [Fact]
    public void Select_StaysSilent_WithoutARequestOnAMachineWithoutVulkan()
    {
        var selection = GpuSelector.Select([], request: null);

        Assert.Null(selection.Device);
        Assert.Null(selection.Error);
    }
}
