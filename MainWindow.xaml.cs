using System;
using System.ComponentModel;
using System.Windows;
using Cut.Services;

namespace Cut;

public partial class MainWindow : Window
{
    private readonly App app;
    public MainWindow(App app, HotkeySettings settings)
    {
        InitializeComponent();
        this.app = app;
        for (char c = 'A'; c <= 'Z'; c++) KeyChoice.Items.Add(c.ToString());
        CtrlCheck.IsChecked = (settings.Modifiers & 2) != 0;
        AltCheck.IsChecked = (settings.Modifiers & 1) != 0;
        ShiftCheck.IsChecked = (settings.Modifiers & 4) != 0;
        KeyChoice.SelectedItem = ((char)settings.Key).ToString();
    }
    public void SetStatus(string text) => StatusText.Text = text;
    private void ApplyHotkey(object sender, RoutedEventArgs e)
    {
        uint modifiers = (CtrlCheck.IsChecked == true ? 2u : 0) | (AltCheck.IsChecked == true ? 1u : 0) | (ShiftCheck.IsChecked == true ? 4u : 0);
        if (modifiers == 0) { SetStatus("请至少选择一个修饰键。"); return; }
        if (KeyChoice.SelectedItem is string key) app.ChangeHotkey(new(modifiers, key[0]));
    }
    private async void StartCapture(object sender, RoutedEventArgs e) => await app.StartCaptureAsync();
    private void OpenTranslationSettings(object sender, RoutedEventArgs e) => new TranslationSettingsWindow { Owner = this }.ShowDialog();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!app.IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
