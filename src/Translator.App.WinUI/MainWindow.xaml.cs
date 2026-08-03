using Microsoft.UI.Xaml;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Translator_App_WinUI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        Activated += OnActivated;
        AssignOwnerHwnd();
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) => AssignOwnerHwnd();

    private void AssignOwnerHwnd()
    {
        if (RootFrame.Content is MainPage page)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != 0)
            {
                page.SetOwnerHwnd(hwnd);
            }
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (RootFrame.Content is MainPage page)
        {
            await page.DisposeAsync();
        }
    }
}
