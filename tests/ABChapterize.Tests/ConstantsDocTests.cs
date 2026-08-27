// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using ABChapterize.Cli;

namespace ABChapterize.Tests;

/// <summary>
/// Keeps <c>doc/constants.md</c> in step with the constants <c>--set:</c> can actually write.
/// </summary>
/// <remarks>
/// That file is generated from the source's own doc comments, and ninety-odd hand-maintained rows
/// would drift the first time a constant was retuned, renamed or added - silently, since nothing
/// else reads it. These tests are what makes the drift loud. They deliberately check names and
/// values rather than prose: the wording comes from the doc comment and is regenerated, but a row
/// that names a constant which no longer exists, or quotes a default that has since changed, is a
/// documentation bug that would send someone tuning against a number the tool does not use.
/// </remarks>
public sealed class ConstantsDocTests
{
    private static readonly Regex Row = new(@"^\| `(?<name>\w+)` \| `(?<value>[^`]+)` \|",
                                            RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^## (?<class>\w+)\s*$", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ABChapterize.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The document as "Class" -> ("Constant" -> the default it quotes).</summary>
    private static Dictionary<string, Dictionary<string, string>> Documented()
    {
        var byClass = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var current = "";
        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot(), "doc", "constants.md")))
        {
            if (Heading.Match(line) is { Success: true } h)
            {
                current = h.Groups["class"].Value;
                byClass[current] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else if (Row.Match(line) is { Success: true } r && current.Length > 0)
            {
                byClass[current][r.Groups["name"].Value] = r.Groups["value"].Value;
            }
        }
        return byClass;
    }

    [Fact]
    public void EveryOverridableClass_HasASectionOfItsOwn_AndNoOthersAreListed()
    {
        Assert.Equal(
            TuningOverrides.Classes.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
            Documented().Keys.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryOverridableConstant_IsDocumented_AndNothingElseIs()
    {
        var documented = Documented();
        foreach (var type in TuningOverrides.Classes)
        {
            var expected = TuningOverrides.ConstantsOf(type).Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(expected, documented[type.Name].Keys.OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TheDefaultEveryRowQuotes_IsTheValueTheBuildActuallyUses()
    {
        // Compared as numbers, not as text: the document carries the source literal ("30.0", "-35")
        // and reflection hands back a double, and it is the value that has to agree, not its
        // spelling. To six places rather than exactly, because a float constant widens to
        // something that is not what parsing its own literal as a double gives - 0.6f is
        // 0.60000002384 - and no constant here is tuned anywhere near that finely.
        var documented = Documented();
        foreach (var type in TuningOverrides.Classes)
            foreach (var (name, value, _) in TuningOverrides.ConstantsOf(type))
            {
                var text = documented[type.Name][name].TrimEnd('f', 'd', 'm');
                Assert.True(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                                            out var quoted),
                            $"{type.Name}.{name}: doc default \"{text}\" is not a number");
                Assert.Equal(Convert.ToDouble(value, CultureInfo.InvariantCulture), quoted, 6);
            }
    }

    /// <summary>
    /// Every row says something, and says a whole sentence of it. The names and defaults above are
    /// checked against the source, so they cannot rot quietly - the prose can, because nothing
    /// generates it any more and nothing else reads it.
    /// </summary>
    /// <remarks>
    /// Both shapes this catches were found in the file by a user rather than by the suite (build
    /// 418). A constant documented with <c>&lt;inheritdoc&gt;</c> left an empty cell, the compiler
    /// writing that tag through to the XML rather than resolving it; and one whose summary had lost
    /// its opening clause left the fragment "levels." behind. Hence both halves of the test: a
    /// description has to be long enough to be a sentence, and to begin like one.
    /// </remarks>
    [Fact]
    public void EveryRow_CarriesAWholeSentenceOfDescription()
    {
        var rows = new Regex(@"^\| `(?<name>\w+)` \| `[^`]+` \|(?<text>.*)\|\s*$");
        var bad = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot(), "doc", "constants.md")))
        {
            if (rows.Match(line) is not { Success: true } row)
                continue;
            var text = row.Groups["text"].Value.Trim();
            var name = row.Groups["name"].Value;
            if (text.Length < 20)
                bad.Add($"{name}: description is empty or a fragment (\"{text}\")");
            else if (!char.IsUpper(text[0]))
                bad.Add($"{name}: description does not begin a sentence (\"{Excerpt(text)}\")");
            else if (!text.EndsWith('.'))
                bad.Add($"{name}: description is cut off (\"{Excerpt(text)}\")");
        }
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    /// <summary>The head of a description, for a failure message that stays readable.</summary>
    /// <param name="text">The description as the document carries it.</param>
    private static string Excerpt(string text)
        => text.Length <= 60 ? text : text[..60] + "...";

    [Fact]
    public void TheDocumentPointsAtTheManual_WhichIsWhereTheWarningLives()
    {
        // The option is deliberately undocumented outside these two files, so the link between
        // them is the whole discovery path and must not rot.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "doc", "constants.md"));
        Assert.Contains("manual.md#tuning-constants", doc);
        var manual = File.ReadAllText(Path.Combine(RepoRoot(), "doc", "manual.md"));
        Assert.Contains("### Tuning constants", manual);
        Assert.Contains("constants.md", manual);
    }
}
