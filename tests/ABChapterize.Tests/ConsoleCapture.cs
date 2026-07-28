// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The xUnit collection every test class redirecting <see cref="Console.Out"/> belongs to.
/// <c>Console.Out</c> is process-wide, so two such classes running in parallel - which is xUnit's
/// default across classes - would capture each other's output and fail at random.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCapture
{
    /// <summary>The collection's name, for the <see cref="CollectionAttribute"/> on its members.</summary>
    public const string Name = "console capture";
}
