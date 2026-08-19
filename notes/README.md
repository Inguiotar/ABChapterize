# Source doc-comment notes

This folder holds the *forensic* half of the source documentation: the measurements, dated case
studies, rejected alternatives and removed-feature histories that used to live inside the doc
comments themselves and had grown to roughly half the tree.

Nothing here is a summary, a duplicate or a rewrite. Every paragraph was moved out of a `///`
comment verbatim, and the comment it came from carries a one-line `<include>` pointing back at it,
so the material is still attached to the exact member it explains - just not in the way of somebody
reading the code.

## Layout

One file per source file, mirroring `src\` exactly:

| source | notes |
| --- | --- |
| `src\Detection\PreciseMarkRefiner.cs` | `notes\Detection\PreciseMarkRefiner.xml` |
| `src\Language\Phrases\PhraseCompiler.cs` | `notes\Language\Phrases\PhraseCompiler.xml` |

A notes file is a `<doc>` root holding one `<member name="...">` per documented member, named after
the member itself (the type's own name for the type). Its children are ordinary XML doc elements -
`<para>`, `<see cref="..."/>`, `<c>`, `<em>`, `<b>` - because that is what they already were.

The source side is one line inside a `<remarks>` that also carries a one-sentence teaser, so a
reader can see *what kind* of material is waiting without opening anything:

```csharp
/// <remarks>
/// Notes: why this correction is not ground truth (Die Dritte Macht ch 7), the removed cheap
/// first round and why it could not be repaired.
/// <include file='../../notes/Detection/PreciseMarkRefiner.xml' path='doc/member[@name="RefinePreciseMarkAsync"]/*' />
/// </remarks>
```

The `file` path is **relative to the source file**, not to the project directory. That matters:
`tests\ABChapterize.Tests` and the three harnesses under `tools\` compile these same sources from
their own project directories, and a project-relative path would resolve in one and not the others.

## What lives where

The split is *why you must not change this* stays, *how we found out* moves.

| stays in the source | moves here |
| --- | --- |
| contract, behaviour, usage - however long they get | measurement provenance and datasets |
| invariants a caller must respect | corpus statistics |
| the reason an alternative is off the table | dated case studies naming real books |
| the one-sentence reason a constant has its exact value | the calibration derivation behind it |
| `<param>`, `<returns>`, `<exception>` | removed-feature histories |

## It is checked, not trusted

Two mechanisms, covering the two ways a link can rot:

- **Missing notes file** → the compiler emits **CS1589**. The build runs at zero warnings, so that
  is already fatal in practice.
- **Anchor that matches nothing** → the compiler says *nothing* and quietly leaves the literal
  `<include>` element in the generated documentation. The `VerifyDocNoteIncludes` target in
  `ABChapterize.csproj` fails the build on any such leftover, naming the file and the anchor.

`NotesTreeTests` in the test project covers what neither of those can see: a member nobody
references (its evidence orphaned), an include path that resolves only through the compiler's
working-directory fallback and so would break under a differently rooted project, a `<remarks>`
nested inside a `<summary>`, a doc line duplicated by a bad edit, a BOM, and a malformed notes file.
Every one of those fired at least once while this tree was being built, which is why they are tests
rather than a checklist.

Crefs inside a notes file are **bound and validated exactly as if they were still in the source** -
including private members and `using static` constants from other classes - and an unresolved one
reports **CS1574** against the `<include>` line. Nothing about the existing doc-comment doctrine is
weakened by moving a paragraph out here.

Notes files are never published: `PublishDocumentationFile` is false, so the merged `.xml` stays a
build-time artefact.
