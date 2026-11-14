using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace G_write;

public partial class MiniWindow : Window
{
    private readonly MainWindow? _main;
    private bool _isRestoring = false;

    public MiniWindow()
    {
        InitializeComponent();
    }

    public MiniWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        SyncWithMain();
    }

    public void SyncWithMain()
    {
        if (_main == null) return;
        UpdateProgress(_main.GetProgressValue(), _main.GetProgressMax());
        UpdatePauseState(!_main.isNotPaused);
        UpdateStopState(!_main.isNotStop);
    }

    public void UpdateProgress(double current, double max)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (miniProgressBar != null)
            {
                miniProgressBar.Maximum = max > 0 ? max : 100;
                miniProgressBar.Value = Math.Min(current, miniProgressBar.Maximum);
            }

            if (lblProgressText != null)
            {
                int pct = max > 0 ? (int)Math.Round((current / max) * 100.0) : 0;
                lblProgressText.Text = $"{pct}% ({(int)current} / {(int)max})";
            }
        });
    }

    public void UpdatePauseState(bool isPaused)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (txtBtnPause == null || txtIconPause == null || btnPause == null) return;
            if (isPaused)
            {
                txtBtnPause.Text = "Продолжить";
                txtIconPause.Text = "▶";
                btnPause.Background = new SolidColorBrush(Color.Parse("#E8F5E9"));
                btnPause.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
            }
            else
            {
                txtBtnPause.Text = "Пауза";
                txtIconPause.Text = "⏸";
                btnPause.Background = new SolidColorBrush(Color.Parse("#FFF2CC"));
                btnPause.Foreground = new SolidColorBrush(Color.Parse("#7F6000"));
            }
        });
    }

    public void UpdateStopState(bool isStopped)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (txtBtnStop == null || txtIconStop == null || btnStop == null) return;
            if (isStopped)
            {
                txtBtnStop.Text = "РАЗБЛОК.";
                txtIconStop.Text = "🔓";
                btnStop.Background = new SolidColorBrush(Color.Parse("#F57C00"));
            }
            else
            {
                txtBtnStop.Text = "СТОП";
                txtIconStop.Text = "🛑";
                btnStop.Background = new SolidColorBrush(Color.Parse("#D9534F"));
            }
        });
    }

    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        RestoreMainWindow();
    }

    public void RestoreMainWindow()
    {
        _isRestoring = true;
        Hide();
        _main?.RestoreFromMiniMode();
        _isRestoring = false;
    }

    public void ForceClose()
    {
        _isRestoring = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isRestoring)
        {
            e.Cancel = true;
            RestoreMainWindow();
            return;
        }
        base.OnClosing(e);
    }

    private void Gen_Click(object? sender, RoutedEventArgs e) => _main?.TriggerGen();
    private void Home_Click(object? sender, RoutedEventArgs e) => _main?.TriggerHome();
    private void Send_Click(object? sender, RoutedEventArgs e) => _main?.TriggerSend();
    private void Pause_Click(object? sender, RoutedEventArgs e) => _main?.TriggerPause();
    private void Stop_Click(object? sender, RoutedEventArgs e) => _main?.TriggerStop();
}
