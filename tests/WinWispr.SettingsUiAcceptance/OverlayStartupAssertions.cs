using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using WinWispr.Core;
using WinWispr.Views;

namespace WinWispr.SettingsUiAcceptance;

internal static class OverlayStartupAssertions
{
    internal static async Task RunAsync()
    {
        var overlay = new OverlayWindow();
        try
        {
            nint targetHandle = GetForegroundWindow();
            nint overlayHandle = WindowNative.GetWindowHandle(overlay);
            Require(targetHandle != 0 && targetHandle != overlayHandle, "An interactive foreground window is required.");

            for (int iteration = 0; iteration < 6; iteration++)
            {
                overlay.ShowStatus(OverlayStatus.Progress("Preparing…"));
                overlay.ShowAt(new WindowTarget(targetHandle));
                Require(IsCloaked(overlayHandle), "Overlay must prepare its first frame out of view.");
                Require(GetForegroundWindow() == targetHandle, "Showing the overlay took focus.");
                var baseline = CaptureScreenContent(overlayHandle, iteration, -1);

                // Also exercise a microphone that becomes ready before the first UI frame.
                if (iteration % 2 == 1)
                {
                    overlay.ShowRecording([]);
                    overlay.ShowAt(new WindowTarget(targetHandle));
                }

                try
                {
                    await WaitUntilAsync(() => !IsCloaked(overlayHandle), "Overlay did not reveal its rendered content.");
                }
                catch
                {
                    var surface = (Microsoft.UI.Xaml.FrameworkElement)overlay.Content;
                    var fields = typeof(OverlayWindow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Console.WriteLine($"OVERLAY_LAYOUT loaded={surface.IsLoaded} width={surface.ActualWidth} height={surface.ActualHeight} visible={overlay.AppWindow.IsVisible}");
                    foreach (var field in fields.Where(field => field.Name is "_preparedFrames" or "_awaitingFirstFrame"))
                        Console.WriteLine($"OVERLAY_STATE {field.Name}={field.GetValue(overlay)}");
                    throw;
                }
                bool contentVisible = false;
                bool emptyFrame = false;
                for (int frame = 0; frame < 8; frame++)
                {
                    Marshal.ThrowExceptionForHR(DwmFlush());
                    var sample = CaptureScreenContent(overlayHandle, iteration, frame);
                    // DWM may still present the unchanged desktop before the show reaches the screen.
                    bool unchangedDesktop = !contentVisible && sample.Hash == baseline.Hash;
                    emptyFrame |= !sample.HasContent && !unchangedDesktop;
                    contentVisible |= sample.HasContent;
                    Require(GetForegroundWindow() == targetHandle, "Rendered overlay took focus.");
                    await Task.Delay(16);
                }
                Require(contentVisible && !emptyFrame, $"Overlay content was missing during iteration {iteration}; inspect the captured frames.");
                overlay.HideOverlay();
                Require(!overlay.AppWindow.IsVisible, "Overlay did not hide.");
            }

            overlay.ShowStatus(OverlayStatus.Progress("Preparing…"));
            overlay.ShowAt(new WindowTarget(targetHandle));
            overlay.HideOverlay();
            await Task.Delay(100);
            Require(!overlay.AppWindow.IsVisible, "A pending frame reopened the hidden overlay.");
            Require(IsCloaked(overlayHandle), "A cancelled reveal was not detached.");

            overlay.ShowStatus(OverlayStatus.Progress("Preparing…"));
            overlay.ShowAt(new WindowTarget(targetHandle));
            await WaitUntilAsync(() => !IsCloaked(overlayHandle), "Overlay did not recover after a cancelled reveal.");
            Marshal.ThrowExceptionForHR(DwmFlush());
            await Task.Delay(16);
            Require(CaptureScreenContent(overlayHandle, 6, 0).HasContent, "Overlay content is missing after a cancelled reveal.");
            await AssertClickThroughAsync(overlayHandle);
        }
        finally
        {
            overlay.HideOverlay();
            overlay.Close();
        }
    }

    // The shadow margin passes clicks to the window underneath; the capsule keeps them.
    private static async Task AssertClickThroughAsync(nint overlayHandle)
    {
        Require(GetCursorPos(out Point original), "Cannot read the cursor position.");
        Require(GetWindowRect(overlayHandle, out Rect bounds), "Cannot read overlay bounds.");
        var margin = new Point { X = bounds.Left + 2, Y = bounds.Top + 2 };
        var centre = new Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
        try
        {
            Require(SetCursorPos(margin.X, margin.Y), "Cannot move the cursor.");
            await Task.Delay(150);
            Require(GetAncestor(WindowFromPoint(margin), 2) != overlayHandle, "The shadow margin must pass clicks through.");
            Require(SetCursorPos(centre.X, centre.Y), "Cannot move the cursor.");
            await Task.Delay(150);
            Require(GetAncestor(WindowFromPoint(centre), 2) == overlayHandle, "The capsule must receive clicks.");
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(5), failure);
            await Task.Delay(8);
        }
    }

    private static bool IsCloaked(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 14, out uint value, sizeof(uint)));
        return (value & 1) != 0;
    }

    private static (bool HasContent, string Hash) CaptureScreenContent(nint hwnd, int iteration, int frame)
    {
        Require(GetWindowRect(hwnd, out Rect rect), "Cannot read overlay bounds.");
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        nint coveringWindow = GetAncestor(WindowFromPoint(new Point
        {
            X = rect.Left + width / 2, Y = rect.Top + height / 2
        }), 2);
        GetWindowThreadProcessId(coveringWindow, out uint coveringProcess);
        DwmGetWindowAttribute(hwnd, 14, out uint cloaked, sizeof(uint));
        Console.WriteLine($"OVERLAY_WINDOW hwnd={hwnd} covering={coveringWindow} process={coveringProcess} cloaked={cloaked} rect={rect.Left},{rect.Top},{width},{height}");
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(memory, bitmap);
        try
        {
            Require(BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x40CC0020),
                "Cannot capture overlay pixels.");
            SelectObject(memory, previous);
            var info = new BitmapInfo
            {
                Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32
            };
            byte[] pixels = new byte[checked(width * height * 4)];
            Require(GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == height,
                "Cannot read overlay pixels.");
            int colored = 0;
            int text = 0;
            int background = 0;
            // Sample the middle of the capsule only: not its border, the shadow margin or the desktop around its ends.
            for (int y = height / 4; y < height * 3 / 4; y++)
                for (int x = width / 5; x < width * 4 / 5; x++)
                {
                    int offset = (y * width + x) * 4;
                    int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                    if (b > r + 25 && b > g + 25) colored++;
                    if (r > 170 && g > 170 && b > 170) text++;
                    // The tinted capsule lets a little of the blurred desktop through, so allow a slight cast.
                    if (Math.Max(r, Math.Max(g, b)) is >= 20 and <= 110 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 24) background++;
                }

            string? evidence = Environment.GetEnvironmentVariable("WINWISPR_OVERLAY_EVIDENCE");
            if (evidence is not null && (iteration == 0 || frame == 0 || colored + text < 5))
            {
                Directory.CreateDirectory(evidence);
                using var writer = new BinaryWriter(File.Create(Path.Combine(evidence, $"overlay-{iteration}-{frame}.bmp")));
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
            }
            Console.WriteLine($"OVERLAY_FRAME iteration={iteration} frame={frame} colored={colored} text={text} background={background}");
            return (colored + text >= 5 && background > width * height / 10,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

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

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, int size);
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
