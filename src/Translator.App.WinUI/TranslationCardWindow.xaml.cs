using Microsoft.UI.Xaml;

namespace Translator_App_WinUI;

public sealed partial class TranslationCardWindow : Window
{
    public TranslationCardWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    public void SetText(string text) => TranslationText.Text = text;
}
