using System.Windows;
using System.Windows.Input;
using PS5Craft.Core;
using PS5Craft.Core.Models;
using PS5Craft.ViewModels;

namespace PS5Craft.App;

public partial class UpdateWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UpdateInfo _info;
    private bool _closingForUpdate;

    public UpdateWindow(MainViewModel vm, UpdateInfo info)
    {
        InitializeComponent();
        _vm = vm;
        _info = info;
        DataContext = vm;
        TitleText.Text = "Доступно обновление";
        CurrentVersionText.Text = AppVersion.Display;
        NewVersionText.Text = "v" + AppVersion.ToSemantic(info.Version);
        NotesText.Text = string.IsNullOrWhiteSpace(vm.UpdateChangelog)
            ? "Доступно новое обновление PS5Craft."
            : "Что нового:\n" + vm.UpdateChangelog;
        ShowOffer();
    }

    private void ShowOffer()
    {
        OfferPanel.Visibility = Visibility.Visible;
        DownloadPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OnDetails(object sender, RoutedEventArgs e) => _vm.OpenReleaseNotesCommand.Execute(null);

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        OfferPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DownloadPanel.Visibility = Visibility.Visible;
        TitleText.Text = "Загрузка обновления...";

        var cts = _vm.BeginUpdateDownloadCts();
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _vm.ReportDownloadProgress(p);
            DownloadBar.Value = p.Percent ?? 0;
            DownloadPercentText.Text = _vm.UpdateDownloadPercentText;
            DownloadSizeText.Text = _vm.UpdateDownloadSizeText;
            if (p.Percent is null)
            {
                DownloadBar.IsIndeterminate = true;
            }
            else
            {
                DownloadBar.IsIndeterminate = false;
            }
        });

        var ok = await _vm.DownloadAndApplyUpdateAsync(_info, progress, cts.Token);
        if (ok)
        {
            _closingForUpdate = true;
            Close();
            return;
        }

        if (cts.IsCancellationRequested)
        {
            ShowOffer();
            TitleText.Text = "Доступно обновление";
            return;
        }

        DownloadPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = string.IsNullOrWhiteSpace(_vm.UpdateStatusText)
            ? "Не удалось установить обновление."
            : _vm.UpdateStatusText;
        TitleText.Text = "Ошибка обновления";
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _vm.CancelUpdateDownloadCommand.Execute(null);

    private void OnErrorClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsDownloadingUpdate && !_closingForUpdate)
        {
            _vm.CancelUpdateDownloadCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
