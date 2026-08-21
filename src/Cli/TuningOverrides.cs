// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Reflection;
using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Errors;
using ABChapterize.Vad;

namespace ABChapterize.Cli;

/// <summary>
/// <c>--set:&lt;class&gt;.&lt;constant&gt;=&lt;value&gt;</c>: the tuning constants this build was
/// calibrated with, opened up for a run that wants to try other values. Deliberately in-depth -
/// undocumented outside the manual and the generated <c>doc/constants.md</c> it points at - because
/// every one of these numbers has evidence behind it and changing one without reading that evidence
/// is how a book quietly loses chapters.
/// </summary>
/// <remarks>
/// The class name is required rather than inferred, even though the constant names happen to be
/// unique across the exposed classes. It is a (weak) confirmation that whoever typed it went and
/// looked at the class the constant lives in.
/// <include file='../../notes/Cli/TuningOverrides.xml' path='doc/member[@name="TuningOverrides"]/*' />
/// </remarks>
internal static class TuningOverrides
{
    /// <summary>The option prefix, spelled once so the parser, the expansion and the error
    /// messages cannot drift apart.</summary>
    internal const string Option = "--set:";

    /// <summary>
    /// The classes whose constants may be overridden. Adding one is this line and nothing else -
    /// the option surface, the validation and <c>doc/constants.md</c> are all derived from it by
    /// reflection.
    /// </summary>
    /// <remarks>
    /// Detection and the audio analysis behind it, and nothing else. The rest of the tree's
    /// constants describe protocol and layout - ffmpeg arguments, ONNX tensor shapes, progress bar
    /// widths - where an override does not tune anything, it corrupts output.
    /// </remarks>
    private static readonly Type[] Exposed =
    [
        typeof(DetectionTuning),
        typeof(VadSegmenter),
        typeof(SileroVadDetector),
        typeof(AudioFidelity),
    ];

    /// <summary>Every field a <c>--set:</c> may write, by "Class.Name".</summary>
    /// <remarks>
    /// Built once and cached, from the <em>declared</em> surface rather than a hand-kept list:
    /// public and internal static fields of a numeric type. A private constant is implementation
    /// detail of its class and stays one; a computed property (a constant derived from others, such
    /// as <see cref="DetectionTuning.RescanShiftSeconds"/>) is not a field and so is not settable,
    /// which is the intended answer - override what it is derived from and it follows.
    /// </remarks>
    private static readonly Dictionary<string, FieldInfo> Fields = BuildFields();

    /// <summary>The value each settable field was compiled with, captured before anything can have
    /// written to it.</summary>
    /// <remarks>
    /// These are process-global statics, so without a snapshot the first run to override one would
    /// change every later run in the same process. That is only a curiosity for the CLI, which
    /// parses one command line and exits, but it is a real hazard for the test suite, which parses
    /// hundreds in one process. <see cref="Apply"/> restores the snapshot before applying anything,
    /// which makes a parse idempotent and each test independent of the ones before it.
    /// </remarks>
    private static readonly Dictionary<string, object> Defaults =
        Fields.ToDictionary(e => e.Key, e => e.Value.GetValue(null)!);

    private static Dictionary<string, FieldInfo> BuildFields()
    {
        var fields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Exposed)
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if ((f.IsPublic || f.IsAssembly) && !f.IsInitOnly && !f.IsLiteral &&
                    (f.FieldType == typeof(double) || f.FieldType == typeof(int) ||
                     f.FieldType == typeof(float)))
                    fields[$"{type.Name}.{f.Name}"] = f;
        return fields;
    }

    /// <summary>The exposed classes, for <c>doc/constants.md</c> and for error messages.</summary>
    internal static IReadOnlyList<Type> Classes => Exposed;

    /// <summary>Every settable constant of one class, with the value it was compiled with, in
    /// declaration order.</summary>
    /// <param name="type">One of <see cref="Classes"/>.</param>
    internal static IEnumerable<(string Name, object Default, FieldInfo Field)> ConstantsOf(Type type)
        => Fields.Where(e => e.Value.DeclaringType == type)
            .Select(e => (e.Value.Name, Defaults[e.Key], e.Value))
            .OrderBy(e => e.Name, StringComparer.Ordinal);

    /// <summary>
    /// Restores every exposed constant to the value it was compiled with, then applies the given
    /// <c>--set:</c> arguments in order.
    /// </summary>
    /// <param name="args">The whole command line; anything not starting with <see cref="Option"/>
    /// is ignored.</param>
    /// <returns>What was applied, "Class.Name=value" each, in the order given - for the --verbose
    /// banner, the debug log and <see cref="CliOptions.RunFingerprint"/>. Empty when nothing was
    /// overridden, which is the ordinary case.</returns>
    /// <exception cref="CliError">Thrown for an unknown class or constant, a malformed argument, or
    /// a value that is not a finite number of the constant's type.</exception>
    internal static IReadOnlyList<string> Apply(IEnumerable<string> args)
    {
        foreach (var (key, field) in Fields)
            field.SetValue(null, Defaults[key]);

        // A request for the usage text is answered whatever else is on the command line, exactly as
        // ConfigFile.Expand treats it: someone reaching for --help because their --set: is rejected
        // should get the help rather than the rejection again.
        if (args.Any(a => a is "--help" or "-?" or "/?"))
            return [];

        var applied = new List<string>();
        foreach (var arg in args)
        {
            if (!arg.StartsWith(Option, StringComparison.Ordinal))
                continue;
            applied.Add(ApplyOne(arg[Option.Length..], arg));
        }
        return applied;
    }

    /// <summary>Applies one "Class.Name=value" body.</summary>
    /// <param name="body">The argument with <see cref="Option"/> taken off.</param>
    /// <param name="whole">The argument as typed, for error messages.</param>
    private static string ApplyOne(string body, string whole)
    {
        var eq = body.IndexOf('=');
        if (eq <= 0 || eq == body.Length - 1)
            throw new CliError(
                $"Malformed {whole}: expected {Option}<class>.<constant>=<value>, " +
                "e.g. --set:DetectionTuning.WhisperChunkSeconds=25.");
        var name = body[..eq].Trim();
        var text = body[(eq + 1)..].Trim();

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            throw new CliError(
                $"Malformed {whole}: the constant has to be named with its class, " +
                $"e.g. --set:DetectionTuning.{name}=<value>. " +
                $"Overridable classes: {string.Join(", ", Exposed.Select(t => t.Name))}.");

        if (!Fields.TryGetValue(name, out var field))
            throw UnknownName(name, whole);

        object value;
        if (field.FieldType == typeof(int))
        {
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
                throw new CliError($"{whole}: \"{text}\" is not a whole number.");
            value = i;
        }
        else
        {
            // The same parser every other decimal option goes through, so "," and "." are both
            // accepted here too (see NumberCulture).
            if (!NumberCulture.TryParseDecimal(text, out var d) || !double.IsFinite(d))
                throw new CliError($"{whole}: \"{text}\" is not a finite number.");
            // Boxed as the field's own type. Without the cast to object the ternary unifies both
            // branches to double, and reflection then refuses to put a double in a float field.
            value = field.FieldType == typeof(float) ? (object)(float)d : d;
        }

        RejectNonPositiveDuration(field, value, whole);
        field.SetValue(null, value);
        return $"{field.DeclaringType!.Name}.{field.Name}={text}";
    }

    /// <summary>
    /// Refuses a length of time that is not one. Every exposed constant whose name ends in
    /// <c>Seconds</c> is a duration, a window or a tolerance, and none of the 93 of them is zero or
    /// negative by default - so a value that is says nothing this code can act on.
    /// </summary>
    /// <remarks>
    /// The failure it exists to prevent is not a wrong result but a run that never ends: several
    /// loops step by a quantity derived from one of these, and a zero leaves them standing still.
    /// <c>SilenceThresholdProbe.AddFrameLevels</c> was measured doing it - a frame length of zero
    /// took a 20-second file to an <c>OutOfMemoryException</c> naming nothing that led back to the
    /// cause. Refusing it here is what turns that into a sentence saying which constant was wrong.
    /// <para>
    /// It does not subsume the guards at those loops, and is not meant to: a step is often the
    /// <em>difference</em> of two of these, so two individually valid values can still produce one
    /// that does not advance (<c>WhisperChunkSeconds</c> minus three phrase margins, an overlap at
    /// or above its chunk length). A very small positive value can also truncate to a zero frame
    /// count. This rule catches what is nonsense on its own; the guards catch what is only nonsense
    /// in combination.
    /// </para>
    /// </remarks>
    /// <param name="field">The constant being set.</param>
    /// <param name="value">The parsed value, boxed as the field's own type.</param>
    /// <param name="whole">The argument as typed, for the message.</param>
    /// <exception cref="CliError">Thrown when a <c>...Seconds</c> constant is given a value that is
    /// not above zero.</exception>
    private static void RejectNonPositiveDuration(FieldInfo field, object value, string whole)
    {
        if (!field.Name.EndsWith("Seconds", StringComparison.Ordinal))
            return;
        var seconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (seconds > 0)
            return;
        throw new CliError(
            $"{whole}: {field.Name} is a length of time and has to be above zero. " +
            "A zero or negative one leaves the searches that step by it unable to move.");
    }

    /// <summary>The error for a name that resolves to no constant, naming the near misses.</summary>
    /// <param name="name">The "Class.Name" that was not found.</param>
    /// <param name="whole">The argument as typed.</param>
    private static CliError UnknownName(string name, string whole)
    {
        var dot = name.LastIndexOf('.');
        var className = name[..dot];
        var constant = name[(dot + 1)..];
        if (Exposed.All(t => !string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase)))
            return new CliError(
                $"{whole}: no overridable class named \"{className}\". " +
                $"Overridable classes: {string.Join(", ", Exposed.Select(t => t.Name))}. " +
                "See doc/constants.md for every constant and what it does.");
        // The class exists, so the constant is what is wrong - and the likeliest cause is that it
        // is one of the derived ones, which follow their inputs and cannot be set on their own.
        return new CliError(
            $"{whole}: \"{className}\" has no overridable constant named \"{constant}\". " +
            "It may be one that is derived from others, which follow whatever those are set to. " +
            "See doc/constants.md.");
    }
}
