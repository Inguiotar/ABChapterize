// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;

// The tuning constants --set: writes are process-global statics, and CliOptions.Parse restores
// them on every call (see TuningOverrides). With test classes running in parallel, any class that
// parses a command line therefore resets the tuning underneath any other class that is currently
// asserting on it - which is not a flaw in either test but in running them at the same time.
// Serializing the assembly is the honest fix; the suite is seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
