namespace WinWispr.Views;

/// <summary>
/// What the overlay's status panel shows. Build it through <see cref="Progress"/> or
/// <see cref="Result"/> so a finished outcome never shows the progress ring.
/// </summary>
internal sealed record OverlayStatus(string Text, string? Detail, bool ShowsProgress, TimeSpan? Duration)
{
    private static readonly TimeSpan DefaultResultDuration = TimeSpan.FromSeconds(1.5);

    /// <summary>A translation is running. An overlay status the app shows itself, not a notice.</summary>
    internal static OverlayStatus Translating { get; } = Progress("Translating\u2026");

    /// <summary>The translation model is still loading when its translation is due.</summary>
    internal static OverlayStatus LoadingTranslation { get; } = Progress("Loading translation\u2026");

    /// <summary>Ongoing work: shows the progress ring and stays until replaced.</summary>
    internal static OverlayStatus Progress(string text) => new(text, null, ShowsProgress: true, Duration: null);

    /// <summary>A finished outcome without the ring; hides after 1.5 s unless a duration (or null to keep it) is given.</summary>
    internal static OverlayStatus Result(string text, string? detail = null) =>
        new(text, detail, ShowsProgress: false, DefaultResultDuration);

    internal static OverlayStatus Result(string text, string? detail, TimeSpan? duration) =>
        new(text, detail, ShowsProgress: false, duration);
}
