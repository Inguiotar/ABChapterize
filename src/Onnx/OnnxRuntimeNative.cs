// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ABChapterize.Onnx;

/// <summary>
/// Where Microsoft.ML.OnnxRuntime finds its native library, for every type in this codebase that
/// runs an ONNX model.
/// <para>
/// This lives on its own rather than beside its first caller because the registration may happen
/// exactly once per process: the runtime installs a resolver of its own on first use and refuses a
/// second, and .NET likewise allows one <see cref="NativeLibrary.SetDllImportResolver"/> per
/// assembly. While the Silero VAD was the only model in the tree, its worker's static constructor
/// was a sound place to do it - the CLR guarantees a type's initializer runs before its first
/// instance. With a second model (<see cref="Audio.SpeechDenoiser"/>) that guarantee says nothing
/// about which of the two is constructed first, so both call <see cref="EnsureRegistered"/> and the
/// idempotence below decides.
/// </para>
/// </summary>
public static class OnnxRuntimeNative
{
    /// <summary>Guards the one-shot registration; <see cref="EnsureRegistered"/> may be called from
    /// several threads, since VAD workers are constructed in parallel.</summary>
    private static readonly Lock Gate = new();

    /// <summary>Whether the resolver has been installed.</summary>
    private static bool _registered;

    /// <summary>
    /// Registers the resolver below if it has not been registered yet. Safe to call repeatedly and
    /// from any thread; every type that constructs an <see cref="InferenceSession"/> must call it
    /// before doing so.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
                return;
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>Loads "onnxruntime" from <c>runtimes\&lt;rid&gt;\</c> when it's there (the published
    /// layout, see the csproj's PruneForeignRuntimes target); otherwise returns
    /// <see cref="IntPtr.Zero"/> to fall back to the default search (an unpublished build output,
    /// where the native still sits flat next to the assembly). Any other library name also falls
    /// through unchanged - "onnxruntime_providers_shared", the only other native OnnxRuntime ships,
    /// is never P/Invoked from managed code; onnxruntime itself loads it from its own directory, so
    /// moving both together is enough without a second entry here.</summary>
    /// <param name="libraryName">The native library the runtime is asking for.</param>
    /// <param name="assembly">The assembly making the request; unused, the name decides.</param>
    /// <param name="searchPath">The caller's search path hint; unused for the same reason.</param>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "onnxruntime")
            return IntPtr.Zero;
        var fileName = OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so";
        var path = Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, fileName);
        return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }
}
