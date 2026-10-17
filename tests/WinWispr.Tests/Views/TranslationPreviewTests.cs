using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;
using WinWispr.Views;

namespace WinWispr.Tests.Views;

[TestClass]
public sealed class TranslationPreviewTests
{
    [TestMethod]
    public void The_direction_is_shown_as_source_arrow_target()
    {
        Assert.AreEqual("DE → EN", TranslationPreviewPresentation.DirectionLabel(TranslationDirection.GermanToEnglish));
        Assert.AreEqual("EN → DE", TranslationPreviewPresentation.DirectionLabel(TranslationDirection.EnglishToGerman));
        Assert.AreEqual(string.Empty, TranslationPreviewPresentation.DirectionLabel(null));
    }

    [TestMethod]
    public void The_window_title_names_the_direction_for_screen_readers()
    {
        Assert.AreEqual(
            "WinWispr: Translation DE → EN", TranslationPreviewPresentation.WindowTitle(TranslationDirection.GermanToEnglish));
        Assert.AreEqual("WinWispr: Translation", TranslationPreviewPresentation.WindowTitle(null));
    }

    [TestMethod]
    public void The_preview_offers_a_scrollable_read_only_text_and_replace_copy_cancel_and_swap()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "TranslationPreviewWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);

        Assert.AreEqual("Auto", (string?)Named("TextScroller").Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("Translation", (string?)Named("TextScroller").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Translation direction", (string?)Named("DirectionText").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Copy translation", (string?)Named("CopyButton").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Cancel translation", (string?)Named("CancelButton").Attribute("AutomationProperties.Name"));
        // The text is shown in a block that cannot be edited, so that Ctrl+C copies the translation, not a selection.
        Assert.AreEqual("TextBlock", Named("TranslationText").Name.LocalName);
        Assert.AreNotEqual("True", (string?)Named("TranslationText").Attribute("IsTextSelectionEnabled"));
        Assert.AreEqual("Replace selection with translation", (string?)Named("ReplaceButton").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Swap translation direction", (string?)Named("SwapButton").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Replace (Enter)", (string?)Named("ReplaceButton").Attribute("Content"));
        Assert.AreEqual("\u21C4", (string?)Named("SwapButton").Attribute("Content"));
        // Esc and Ctrl+C work as accelerators of the whole content.
        var accelerators = document.Descendants().Where(element => element.Name.LocalName == "KeyboardAccelerator").ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "Escape:", "C:Control", "Enter:" },
            accelerators.Select(element => $"{(string?)element.Attribute("Key")}:{(string?)element.Attribute("Modifiers")}").ToArray());
    }

    [TestMethod]
    public void The_window_hides_replace_and_swap_and_ignores_enter_when_the_preview_cannot_replace()
    {
        string code = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "TranslationPreviewWindow.xaml.cs"));

        StringAssert.Contains(code, "preview.CanReplace ? Visibility.Visible : Visibility.Collapsed");
        StringAssert.Contains(code, "if (_canReplace && ReplaceButton.IsEnabled)");
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "WinWispr.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
