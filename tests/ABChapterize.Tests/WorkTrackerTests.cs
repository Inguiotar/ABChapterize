// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the byte-based progress arithmetic of <see cref="WorkTracker"/>.
/// </summary>
public class WorkTrackerTests
{
    [Fact]
    public void NewTracker_ReportsZero()
    {
        var t = new WorkTracker();
        Assert.Equal(0, t.Fraction);
        Assert.Equal("", t.PhaseLabel);
    }

    [Fact]
    public void BeginPhase_ResetsProgressAndSetsLabel()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", 1000);
        t.Advance(500);
        t.BeginPhase("Pass 2", 200);
        Assert.Equal("Pass 2", t.PhaseLabel);
        Assert.Equal(0, t.Fraction);
    }

    [Fact]
    public void AdvanceAndTransientProgress_AddUp()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 2", 1000);
        t.Advance(250);
        t.SetPhaseProgress(250);
        Assert.Equal(0.5, t.Fraction);
    }

    [Fact]
    public void Advance_ClearsTransientProgress()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", 1000);
        t.SetPhaseProgress(900);
        t.Advance(100); // the finished work item replaces its own transient progress
        Assert.Equal(0.1, t.Fraction);
    }

    [Fact]
    public void Fraction_IsClampedToOne()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", 100);
        t.Advance(500);
        Assert.Equal(1, t.Fraction);
    }

    [Fact]
    public void ZeroTotal_YieldsZeroFraction()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", 0);
        t.Advance(10);
        Assert.Equal(0, t.Fraction);
    }

    [Fact]
    public void NegativeInputs_AreIgnored()
    {
        var t = new WorkTracker();
        t.BeginPhase("Pass 1", 100);
        t.Advance(-50);
        t.SetPhaseProgress(-10);
        Assert.Equal(0, t.Fraction);
    }
}
