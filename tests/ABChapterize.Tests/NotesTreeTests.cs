using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Guards the <c>notes\</c> tree, which holds the measurements, dated case studies and rejected
/// alternatives that used to live inside the doc comments (see <c>notes\README.md</c>).
/// </summary>
/// <remarks>
/// The compiler covers only half of it. A missing notes file is CS1589 and a stale
/// <c>&lt;see cref&gt;</c> inside one is CS1574, but an <c>&lt;include&gt;</c> whose XPath matches
/// nothing fails <em>silently</em> - Roslyn leaves the literal element in the generated
/// documentation and carries on. The csproj's VerifyDocNoteIncludes target catches that one from
/// the build side; everything here is what neither can see: a notes member nobody references (its
/// content orphaned), an include path that resolves only by the compiler's working-directory
/// fallback and so would break under a differently rooted project, a <c>&lt;remarks&gt;</c> nested
/// inside a <c>&lt;summary&gt;</c>, and a doc line duplicated by a bad edit.
/// <para>
/// Every one of these fired at least once while the tree was being built, which is why they are
/// tests rather than a checklist.
/// </para>
/// </remarks>
public class NotesTreeTests
{
    private static readonly Regex IncludeDirective = new(
        @"<include file='(?<file>[^']+)' path='doc/member\[@name=""(?<name>[^""]+)""\]",
        RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ABChapterize.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string[] Sources() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories);

    private static string[] NotesFiles() =>
        Directory.Exists(Path.Combine(RepoRoot(), "notes"))
            ? Directory.GetFiles(Path.Combine(RepoRoot(), "notes"), "*.xml", SearchOption.AllDirectories)
            : [];

    /// <summary>Every notes file parses, carries no BOM, and declares each member once.</summary>
    [Fact]
    public void EveryNotesFile_IsWellFormedAndDeclaresEachMemberOnce()
    {
        foreach (var file in NotesFiles())
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{file} has a UTF-8 BOM; the tree carries none.");

            var doc = XDocument.Load(file);
            var names = doc.Root!.Elements("member").Select(m => (string)m.Attribute("name")!).ToList();
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    /// <summary>
    /// Every include resolves to a member that exists, by a path relative to the source file
    /// itself rather than to any project directory.
    /// </summary>
    /// <remarks>
    /// The relative-path half is not pedantry: this test project and the three harnesses under
    /// <c>tools\</c> compile the same sources from their own directories, so a path that only
    /// works from the main project's root would resolve in one build and not the others.
    /// </remarks>
    [Fact]
    public void EveryInclude_ResolvesRelativeToItsOwnSourceFile()
    {
        foreach (var source in Sources())
            foreach (Match m in IncludeDirective.Matches(File.ReadAllText(source)))
            {
                var target = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(source)!, m.Groups["file"].Value));
                Assert.True(File.Exists(target),
                    $"{source}: include points at {m.Groups["file"].Value}, which does not exist " +
                    "relative to that source file.");

                var names = XDocument.Load(target).Root!.Elements("member")
                    .Select(e => (string)e.Attribute("name")!);
                Assert.Contains(m.Groups["name"].Value, names);
            }
    }

    /// <summary>No notes member is left unreferenced, which would orphan the evidence in it.</summary>
    [Fact]
    public void EveryNotesMember_IsIncludedBySomeSource()
    {
        var referenced = new HashSet<string>();
        foreach (var source in Sources())
            foreach (Match m in IncludeDirective.Matches(File.ReadAllText(source)))
            {
                var target = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(source)!, m.Groups["file"].Value));
                referenced.Add(target + "|" + m.Groups["name"].Value);
            }

        foreach (var file in NotesFiles())
            foreach (var member in XDocument.Load(file).Root!.Elements("member"))
                Assert.Contains(Path.GetFullPath(file) + "|" + (string)member.Attribute("name")!,
                    referenced);
    }

    /// <summary>
    /// A <c>&lt;remarks&gt;</c> never opens before its <c>&lt;/summary&gt;</c>, which would nest
    /// the two and quietly drop the remarks from the generated documentation.
    /// </summary>
    [Fact]
    public void NoRemarksBlock_SitsInsideASummary()
    {
        foreach (var source in Sources())
        {
            var block = new List<string>();
            foreach (var line in File.ReadAllLines(source).Append(""))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("///")) { block.Add(trimmed); continue; }
                if (block.Count > 0) { AssertNotNested(source, block); block.Clear(); }
            }
        }

        static void AssertNotNested(string source, List<string> block)
        {
            var text = string.Join("\n", block);
            var summaryEnd = text.IndexOf("</summary>", StringComparison.Ordinal);
            var remarks = text.IndexOf("<remarks>", StringComparison.Ordinal);
            if (summaryEnd >= 0 && remarks >= 0)
                Assert.True(remarks > summaryEnd,
                    $"{source}: a <remarks> opens before its </summary>.");
        }
    }

    /// <summary>No doc-comment line is immediately repeated - the signature of a bad bulk edit.</summary>
    [Fact]
    public void NoDocCommentLine_IsDuplicatedByItsNeighbour()
    {
        foreach (var source in Sources())
        {
            string? previous = null;
            var number = 0;
            foreach (var line in File.ReadAllLines(source))
            {
                number++;
                var trimmed = line.Trim();
                if (trimmed.StartsWith("///") && trimmed.Length > 12 && trimmed == previous)
                    Assert.Fail($"{source}:{number} repeats the doc line above it: {trimmed}");
                previous = trimmed;
            }
        }
    }
}
