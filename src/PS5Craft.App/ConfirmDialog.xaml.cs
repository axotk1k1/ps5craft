using System.Windows;

namespace PS5Craft.App;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }
    public string? EditedPath { get; private set; }

    public ConfirmDialog(string title, string message, string confirmText = "Да", string cancelText = "Отмена", string? editablePath = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        if (!string.IsNullOrWhiteSpace(editablePath))
        {
            PathPanel.Visibility = Visibility.Visible;
            PathBox.Text = editablePath;
            EditedPath = editablePath;
            Loaded += (_, _) =>
            {
                PathBox.Focus();
                PathBox.CaretIndex = PathBox.Text.Length;
            };
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (PathPanel.Visibility == Visibility.Visible)
        {
            EditedPath = string.IsNullOrWhiteSpace(PathBox.Text) ? null : PathBox.Text.Trim();
        }

        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
