using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

/// <summary>Only the E2E profile may replace the microphone, and only with a file in its own data root.</summary>
[TestClass]
[DoNotParallelize]
public sealed class E2eReplayAudioPathTests
{
    private const string Mode = "WINWISPR_E2E_MODE";
    private const string DataRoot = "WINWISPR_E2E_DATA_ROOT";
    private const string Instance = "WINWISPR_E2E_INSTANCE_ID";
    private const string Audio = "WINWISPR_E2E_AUDIO_PCM16";

    [TestMethod]
    public void A_normal_installation_never_names_a_replay_clip()
    {
        WithProfile((root, replay) =>
        {
            Environment.SetEnvironmentVariable(Mode, null);

            Assert.IsNull(AppDataPaths.E2eReplayAudioPath);
        });
    }

    [TestMethod]
    public void The_E2E_profile_names_no_clip_unless_asked()
    {
        WithProfile((root, replay) =>
        {
            Environment.SetEnvironmentVariable(Audio, null);

            Assert.IsNull(AppDataPaths.E2eReplayAudioPath);
        });
    }

    [TestMethod]
    public void A_file_directly_inside_the_data_root_is_accepted()
    {
        WithProfile((root, replay) => Assert.AreEqual(replay, AppDataPaths.E2eReplayAudioPath));
    }

    [TestMethod]
    public void Other_places_and_missing_files_are_rejected()
    {
        WithProfile((root, replay) =>
        {
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "clip.pcm"), [0, 0]);
            string elsewhere = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pcm");
            File.WriteAllBytes(elsewhere, [0, 0]);
            try
            {
                foreach (string rejected in new[]
                {
                    Path.Combine(nested, "clip.pcm"),
                    elsewhere,
                    Path.Combine(root, "missing.pcm"),
                    Path.Combine(root, "..", Path.GetFileName(elsewhere))
                })
                {
                    Environment.SetEnvironmentVariable(Audio, rejected);

                    Assert.ThrowsExactly<InvalidOperationException>(
                        () => { _ = AppDataPaths.E2eReplayAudioPath; }, rejected);
                }
            }
            finally
            {
                File.Delete(elsewhere);
            }
        });
    }

    private static void WithProfile(Action<string, string> scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinWispr-E2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string replay = Path.Combine(root, "replay.pcm");
        File.WriteAllBytes(replay, [0, 0]);
        string[] names = [Mode, DataRoot, Instance, Audio];
        string?[] saved = names.Select(name => Environment.GetEnvironmentVariable(name)).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(Mode, "taskboard");
            Environment.SetEnvironmentVariable(DataRoot, root);
            Environment.SetEnvironmentVariable(Instance, Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(Audio, replay);
            scenario(root, replay);
        }
        finally
        {
            for (int index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], saved[index]);
            Directory.Delete(root, recursive: true);
        }
    }
}
