using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The managed ONNX Runtime package on the onnxruntime.dll that WinWispr ships, without the translation model: the
/// binding finds the shipped library, the versions agree, and a graph runs with exactly the session options that the
/// translation graphs use. They need the shipped <c>Runtime\win-x64</c> folder, which the test build copies, but no
/// model, so they run whenever the WindowsAcceptance category is selected.
/// </summary>
[TestClass]
public sealed class TranslationRuntimeAcceptanceTests
{
    /// <summary>A graph of one Identity node on a float tensor of shape [1], serialized ONNX (IR 8, opset 13).</summary>
    private const string IdentityGraphBase64 =
        "CAhCAhANOjcKEAoBeBIBeSIISWRlbnRpdHkSAWdaDwoBeBIKCggIARIECgIIAWIPCgF5EgoKCAgBEgQKAggB";

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void The_managed_binding_runs_on_the_shipped_onnxruntime_with_the_translation_session_options()
    {
        OnnxRuntimeBinding.EnsureBound();
        OnnxRuntimeBinding.EnsureBound(); // binding twice is harmless

        string version = OrtEnv.Instance().GetVersionString();
        Assert.AreEqual("1.24.4", version, "the managed package and the shipped native library must be the same version");
        CollectionAssert.Contains(OrtEnv.Instance().GetAvailableProviders(), "CPUExecutionProvider");

        using SessionOptions options = MarianModel.CreateSessionOptions();
        using var session = new InferenceSession(Convert.FromBase64String(IdentityGraphBase64), options);
        using var input = OrtValue.CreateTensorValueFromMemory(new[] { 42.5f }, [1L]);
        using IDisposableReadOnlyCollection<OrtValue> output = session.Run(new RunOptions(), ["x"], [input], ["y"]);

        Assert.AreEqual(42.5f, output[0].GetTensorDataAsSpan<float>()[0]);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Session_options_can_be_created_repeatedly_for_several_models()
    {
        OnnxRuntimeBinding.EnsureBound();

        for (int i = 0; i < 3; i++)
        {
            using SessionOptions options = MarianModel.CreateSessionOptions();
            using var session = new InferenceSession(Convert.FromBase64String(IdentityGraphBase64), options);
            Assert.IsTrue(session.InputMetadata.ContainsKey("x"));
        }
    }
}
