using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WinWispr.Core;
using WinWispr.Views;

namespace WinWispr.SettingsUiAcceptance;

/// <summary>
/// Captures the overlay in every state over light, dark and colourful backgrounds for visual review.
/// Writes BMP files to WINWISPR_OVERLAY_EVIDENCE.
/// </summary>
internal static class OverlayGallery
{
    internal static async Task RunAsync()
    {
        string evidence = Environment.GetEnvironmentVariable("WINWISPR_OVERLAY_EVIDENCE")
            ?? throw new InvalidOperationException("Expected WINWISPR_OVERLAY_EVIDENCE.");
        Directory.CreateDirectory(evidence);

        var backdropRoot = new Grid();
        var backdrop = new Window { Content = backdropRoot, Title = "WinWispr overlay gallery" };
        if (backdrop.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.Maximize();
        }
        backdrop.Activate();
        nint backdropHandle = WindowNative.GetWindowHandle(backdrop);

        var overlay = new OverlayWindow();
        try
        {
            foreach ((string name, Action<Grid> paint) in Backgrounds())
            {
                paint(backdropRoot);
                await Task.Delay(300);

                overlay.ShowRecording([]);
                overlay.ShowAt(new WindowTarget(backdropHandle));
                await CaptureAsync(overlay, evidence, $"{name}-recording-rest");

                float[] peaks = Enumerable.Range(0, 64).Select(index => (float)(0.05 + 0.6 * Math.Abs(Math.Sin(index / 5.0)))).ToArray();
                for (int frame = 0; frame < 40; frame++)
                {
                    overlay.SetWaveform(peaks);
                    await Task.Delay(20);
                }
                await CaptureAsync(overlay, evidence, $"{name}-recording-level");

                SetPointerOver(overlay, true);
                await CaptureAsync(overlay, evidence, $"{name}-hover");
                SetPointerOver(overlay, false);

                overlay.ShowCancel(TimeSpan.FromSeconds(30));
                await CaptureAsync(overlay, evidence, $"{name}-cancel");

                overlay.ShowStatus(OverlayStatus.Progress("Transcribing…"));
                await CaptureAsync(overlay, evidence, $"{name}-status");
                overlay.HideOverlay();

                // The tinted material Windows falls back to without transparency effects.
                overlay.AcrylicOverride = false;
                overlay.ShowRecording([]);
                overlay.ShowAt(new WindowTarget(backdropHandle));
                await CaptureAsync(overlay, evidence, $"{name}-fallback");
                overlay.AcrylicOverride = null;
                overlay.HideOverlay();
            }
        }
        finally
        {
            overlay.HideOverlay();
            overlay.Close();
            backdrop.Close();
        }
    }

    private static IEnumerable<(string Name, Action<Grid> Paint)> Backgrounds()
    {
        yield return ("light", root => Paint(root, ColorHelper.FromArgb(255, 0xF3, 0xF3, 0xF3), Colors.Black));
        yield return ("dark", root => Paint(root, ColorHelper.FromArgb(255, 0x1C, 0x1C, 0x1C), Colors.White));
        yield return ("colour", root => Paint(root, null, Colors.White));
    }

    private static void Paint(Grid root, Windows.UI.Color? background, Windows.UI.Color text)
    {
        root.Children.Clear();
        root.RowDefinitions.Clear();
        if (background is Windows.UI.Color solid)
        {
            root.Background = new SolidColorBrush(solid);
        }
        else
        {
            var stops = new GradientStopCollection();
            Windows.UI.Color[] colours = [Colors.OrangeRed, Colors.Gold, Colors.LimeGreen, Colors.DeepSkyBlue, Colors.MediumPurple];
            for (int index = 0; index < colours.Length; index++)
                stops.Add(new GradientStop { Color = colours[index], Offset = index / (double)(colours.Length - 1) });
            root.Background = new LinearGradientBrush { GradientStops = stops, StartPoint = new(0, 0), EndPoint = new(1, 0) };
        }

        // Fine text everywhere so the blur behind the capsule is visible.
        var lines = new StackPanel();
        for (int line = 0; line < 120; line++)
            lines.Children.Add(new TextBlock
            {
                Text = string.Concat(Enumerable.Repeat("WinWispr overlay gallery · The quick brown fox jumps over the lazy dog · ", 6)),
                Foreground = new SolidColorBrush(text),
                FontSize = 14
            });
        root.Children.Add(lines);
    }

    private static void SetPointerOver(OverlayWindow overlay, bool value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(OverlayWindow).GetField("_pointerOver", flags)!.SetValue(overlay, value);
        typeof(OverlayWindow).GetMethod("UpdatePanelVisibility", flags)!.Invoke(overlay, null);
    }

    private static async Task CaptureAsync(OverlayWindow overlay, string directory, string name)
    {
        await Task.Delay(500);
        Marshal.ThrowExceptionForHR(DwmFlush());
        nint handle = WindowNative.GetWindowHandle(overlay);
        if (!GetWindowRect(handle, out Rect rect))
            throw new InvalidOperationException("Cannot read overlay bounds.");
        const int pad = 16;
        int x = rect.Left - pad, y = rect.Top - pad;
        int width = rect.Right - rect.Left + pad * 2, height = rect.Bottom - rect.Top + pad * 2;

        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020))
                throw new InvalidOperationException("Cannot capture overlay pixels.");
            SelectObject(memory, previous);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[checked(width * height * 4)];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
                throw new InvalidOperationException("Cannot read overlay pixels.");
            using var writer = new BinaryWriter(File.Create(Path.Combine(directory, $"{name}.bmp")));
            writer.Write((ushort)0x4D42);
            writer.Write(54 + pixels.Length);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(-height);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixels.Length);
            writer.Write(new byte[16]);
            writer.Write(pixels);
            Console.WriteLine($"OVERLAY_GALLERY {name} rect={rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top} dpi={GetDpiForWindow(handle)}");
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
        internal uint Color;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
}
