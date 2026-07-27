// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Language;

/// <summary>
/// The phrases and titles one language answers with when the corresponding command line option
/// was not given. A record rather than a tuple because the set has outgrown what positional
/// fields stay readable at: a caller reading <c>defaults.EpilogueTitle</c> cannot silently swap
/// two same-typed neighbours the way <c>defaults.Item6</c> could.
/// </summary>
/// <param name="Phrase">Default --chapter-phrase.</param>
/// <param name="Title">Default --title, the word chapter titles are built from.</param>
/// <param name="Intro">Default --intro-title.</param>
/// <param name="Prologue">Default --prologue-phrase.</param>
/// <param name="PrologueTitle">Default --prologue-title.</param>
/// <param name="Epilogue">Default --epilogue-phrase.</param>
/// <param name="EpilogueTitle">Default --epilogue-title.</param>
public sealed record LanguageDefaults(
    string Phrase, string Title, string Intro,
    string Prologue, string PrologueTitle, string Epilogue, string EpilogueTitle);
