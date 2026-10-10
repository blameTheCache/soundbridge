using System.Windows;

namespace SoundBridge.UI;

/// <summary>The small Answer / Decline prompt shown while the phone rings and the main window is not in front.</summary>
internal partial class CallPromptWindow : Window
{
    public CallPromptWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += (_, _) => PlaceAboveNotificationArea();
    }

    /// <summary>Raised when the user closes the prompt without answering or declining.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Set by the app when it is exiting. Otherwise closing only dismisses the prompt.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The window is reused for every call, and a closed WPF window can never be shown again.
        if (!AllowClose)
        {
            e.Cancel = true;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceAboveNotificationArea();
    }

    private void PlaceAboveNotificationArea()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
}
