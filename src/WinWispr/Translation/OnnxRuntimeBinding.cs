using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace WinWispr.Translation;

/// <summary>
/// Binds the managed ONNX Runtime package to the <c>onnxruntime.dll</c> that WinWispr already ships next to the
/// speech runtime (<c>Runtime\win-x64</c>), instead of letting the package look for a native library of its own.
/// The package carries no native binary here. Whichever of speech recognition and translation loads first loads the
/// shipped library; the other reuses the loaded module.
/// </summary>
internal static class OnnxRuntimeBinding
{
    private static readonly object Gate = new();
    private static bool _bound;

    internal static void EnsureBound() =>
        EnsureBound(Path.Combine(AppContext.BaseDirectory, "Runtime", "win-x64"));

    internal static void EnsureBound(string runtimeDirectory)
    {
        lock (Gate)
        {
            if (_bound)
                return;

            // DirectML.dll sits next to onnxruntime.dll; loading it first lets the loader resolve the dependency.
            string directMl = Path.Combine(runtimeDirectory, "DirectML.dll");
            if (File.Exists(directMl))
                NativeLibrary.Load(directMl);

            nint runtime = NativeLibrary.Load(Path.Combine(runtimeDirectory, "onnxruntime.dll"));
            NativeLibrary.SetDllImportResolver(
                typeof(OrtEnv).Assembly,
                (libraryName, _, _) =>
                    string.Equals(libraryName, "onnxruntime", StringComparison.OrdinalIgnoreCase) ? runtime : IntPtr.Zero);
            _bound = true;
        }
    }
}
