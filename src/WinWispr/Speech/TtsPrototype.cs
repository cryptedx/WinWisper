using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinWispr.Speech;

// Explicit developer-only console; no application commands or settings are added.
internal static class TtsPrototype
{
    internal static readonly string German = "Am 22. September kostet das Ticket 12,50 Euro. Dr. Müller kommt um 14:30 Uhr, z. B. mit dem ICE. Please check the next station.";
    internal static readonly string English = "On September 22, the ticket costs 12 dollars and 50 cents. Dr. Smith arrives at 2:30 p.m., e.g. by train. Nächster Halt ist München.";
    internal static string Text(string language) => language switch
    {
        "de" => German,
        "en" => English,
        "long" => string.Join(' ', Enumerable.Repeat("This is a deliberately long passage without punctuation for bounded local speech generation", 100)),
        _ => throw new ArgumentException("Choose de, en, or long.")
    };

    internal static void Start(TtsSession session, CancellationToken shutdown)
    {
        if (!AllocConsole()) throw new InvalidOperationException("Could not open the prototype console.");
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        _ = Task.Run(async () =>
        {
            Console.WriteLine("Local TTS prototype. Commands: load <voice directory>, unload, speak <de|en|long> <0.5..2>, pause, resume, stop, quit. Resume after dictation is manual.");
            try
            {
                while (!shutdown.IsCancellationRequested && Console.ReadLine() is string command)
                {
                    try
                    {
                        if (command.StartsWith("load ", StringComparison.Ordinal))
                            await MeasureAsync("load", () => session.LoadAsync(command[5..].Trim('"')));
                        else if (command == "unload") await MeasureAsync("unload", session.UnloadAsync);
                        else if (command.StartsWith("speak ", StringComparison.Ordinal))
                        {
                            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            string text = Text(parts[1]);
                            float speed = parts.Length == 3 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                            _ = ObserveSpeechAsync(session, text, speed, shutdown);
                        }
                        else if (command == "pause") session.Pause();
                        else if (command == "resume") session.Resume();
                        else if (command == "stop") session.Stop();
                        else if (command == "quit") break;
                        else Console.WriteLine("Unknown command.");
                    }
                    catch (Exception error) { Console.WriteLine($"Command failed: {error.GetType().Name}: {error.Message}"); }
                }
            }
            finally { await session.UnloadAsync(); FreeConsole(); }
        });
    }

    private static async Task ObserveSpeechAsync(TtsSession session, string text, float speed, CancellationToken token)
    {
        try { await MeasureAsync("speech-with-playback", () => session.SpeakAsync(text, speed, token)); }
        catch (OperationCanceledException) { Console.WriteLine("Speech stopped; pending native results discarded."); }
        catch (Exception error) { Console.WriteLine($"Speech failed: {error.GetType().Name}: {error.Message}"); }
    }

    internal static async Task MeasureAsync(string phase, Func<Task> action)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long baseline = process.PrivateMemorySize64, peak = baseline;
        TimeSpan cpu = process.TotalProcessorTime;
        Stopwatch timer = Stopwatch.StartNew();
        double peakCpuPercent = 0, previousMilliseconds = 0;
        TimeSpan previousCpu = cpu;
        void Sample()
        {
            process.Refresh();
            peak = Math.Max(peak, process.PrivateMemorySize64);
            double milliseconds = timer.Elapsed.TotalMilliseconds;
            TimeSpan currentCpu = process.TotalProcessorTime;
            if (milliseconds > previousMilliseconds)
                peakCpuPercent = Math.Max(peakCpuPercent,
                    (currentCpu - previousCpu).TotalMilliseconds / (milliseconds - previousMilliseconds)
                    / Environment.ProcessorCount * 100);
            previousCpu = currentCpu;
            previousMilliseconds = milliseconds;
        }
        Task work = action();
        try
        {
            while (!work.IsCompleted)
            {
                Sample();
                await Task.WhenAny(work, Task.Delay(25));
            }
            await work;
        }
        finally
        {
            Sample();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase, elapsedMs = timer.Elapsed.TotalMilliseconds,
                sampledPeakCpuPercent = peakCpuPercent,
                processCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                privateBytesBefore = baseline, peakIncrementalPrivateBytes = peak - baseline,
                privateBytesAfter = process.PrivateMemorySize64,
                samplingIntervalMs = 25, scope = "whole-process; includes concurrent STT and UI"
            }));
        }
    }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
}
