using WinWispr.Translation;

namespace WinWispr.Views;

/// <summary>The words the translation preview shows and names itself with, kept out of the window so tests can reach them.</summary>
internal static class TranslationPreviewPresentation
{
    /// <summary>The direction as the preview shows it, for example "DE → EN". Empty when no translation ran.</summary>
    internal static string DirectionLabel(TranslationDirection? direction) => direction switch
    {
        TranslationDirection.GermanToEnglish => "DE → EN",
        TranslationDirection.EnglishToGerman => "EN → DE",
        _ => string.Empty
    };

    /// <summary>
    /// The window title. The preview has no title bar; the title names its state for screen readers and UI
    /// Automation, the way the overlay's title names the overlay's state.
    /// </summary>
    internal static string WindowTitle(TranslationDirection? direction) =>
        DirectionLabel(direction) is { Length: > 0 } label
            ? $"WinWispr: Translation {label}"
            : "WinWispr: Translation";
}
