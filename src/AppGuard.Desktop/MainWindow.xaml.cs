using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppGuard.Infrastructure.Persistence;
using System.ComponentModel;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using DockPanel = System.Windows.Controls.DockPanel;
using CheckBox = System.Windows.Controls.CheckBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
namespace AppGuard.Desktop;

public partial class MainWindow : Window
{
    private readonly List<string> _applications = [];
    private readonly IUserSettingsStore _settingsStore;
    private readonly IApplicationStore _applicationStore;
    private UserSettings _settings = new();
    private bool _allowClose;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    public MainWindow(IUserSettingsStore settingsStore, IApplicationStore applicationStore) { _settingsStore = settingsStore; _applicationStore = applicationStore; InitializeComponent(); ShowDashboard(); }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { _settings = await _settingsStore.LoadAsync(); }
        catch (Exception) { _settings = new UserSettings(); MessageBox.Show("AppGuard could not load preferences. Safe default settings are being used.", "AppGuard", MessageBoxButton.OK, MessageBoxImage.Warning); }
        ApplyPreferences();
        try { await ReloadApplications(); }
        catch (Exception) { MessageBox.Show("AppGuard could not load the approved application list. No applications will be monitored.", "AppGuard", MessageBoxButton.OK, MessageBoxImage.Warning); }
        ShowDashboard();
    }
    private async Task ReloadApplications() { _applications.Clear(); _applications.AddRange((await _applicationStore.ListAsync()).Select(x => x.ExecutablePath)); }
    private void ApplyPreferences() { DashboardNav.Content = L("◫   Dashboard", "◫   Обзор"); ApplicationsNav.Content = L("▣   Applications", "▣   Приложения"); ActivityNav.Content = L("◷   Activity Monitor", "◷   Активность"); LogsNav.Content = L("☷   Logs", "☷   Журнал"); SettingsNav.Content = L("⚙   Settings", "⚙   Настройки"); ConfigureTray(); if (_settings.Theme == "light") { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F6F8")); PageTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17202A")); PageSubtitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#596574")); } else { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11151B")); PageTitle.Foreground = Brushes.White; PageSubtitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A3ACB9")); } }
    private void ConfigureTray()
    {
        DisposeTray();
        if (_settings.CloseBehavior != "tray") return;
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Открыть AppGuard" : "Open AppGuard", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Выйти" : "Exit AppGuard", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _trayIcon = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "AppGuard", ContextMenuStrip = _trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    }
    private void DisposeTray()
    {
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        if (_trayMenu is not null) { _trayMenu.Dispose(); _trayMenu = null; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_allowClose) return; if (_settings.CloseBehavior == "ask") { var answer = MessageBox.Show("Close AppGuard completely? Choose No to keep this window open.", "AppGuard", MessageBoxButton.YesNo, MessageBoxImage.Question); if (answer == MessageBoxResult.No) e.Cancel = true; return; } if (_settings.CloseBehavior == "tray") { e.Cancel = true; Hide(); if (_settings.NotificationsEnabled) _trayIcon?.ShowBalloonTip(1800, "AppGuard", "AppGuard is still running in the system tray.", System.Windows.Forms.ToolTipIcon.Info); } }
    public void ExitApp() { _allowClose = true; DisposeTray(); Close(); }
    private TextBlock Text(string value, double size = 14, bool bold = false, string? color = null) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color ?? "#F4F7FA")), TextWrapping = TextWrapping.Wrap };
    private Border Card(UIElement child) => new() { Background = (Brush)FindResource("Panel"), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B333E")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 12), Child = child };
    private string L(string english, string russian) => _settings.Language == "ru" ? russian : english;
    private void Header(string title, string subtitle) { PageTitle.Text = title; PageSubtitle.Text = subtitle; PageContent.Children.Clear(); }
    private void ShowDashboard()
    {
        Header(L("Dashboard", "Обзор"), L("A clear view of your approved applications.", "Обзор разрешённых приложений."));
        var row = new WrapPanel();
        row.Children.Add(Card(new StackPanel { Children = { Text("MONITORED APPS", 10, true, "#A3ACB9"), Text(_applications.Count.ToString(), 30, true), Text("Explicitly approved", 12, false, "#A3ACB9") } }));
        row.Children.Add(Card(new StackPanel { Children = { Text("TRACKED PROCESSES", 10, true, "#A3ACB9"), Text("0", 30, true), Text("Monitoring service unavailable", 12, false, "#A3ACB9") } }));
        row.Children.Add(Card(new StackPanel { Children = { Text("TERMINATED", 10, true, "#A3ACB9"), Text("0", 30, true), Text("No actions performed", 12, false, "#A3ACB9") } })); PageContent.Children.Add(row);
        var safety = new StackPanel(); safety.Children.Add(Text("Protection at a glance", 19, true)); safety.Children.Add(Text("Dry Run is enabled by default. AppGuard will not terminate processes until a complete safety review and explicit user activation are available.", 13, false, "#A3ACB9")); safety.Children.Add(new Border { Height = 12 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; var start = new Button { Content = "Start Monitoring", IsEnabled = false, Margin = new Thickness(0, 0, 9, 0), ToolTip = "Monitoring engine is not yet wired to this interface." }; var stop = new Button { Content = "Stop Monitoring", IsEnabled = false }; buttons.Children.Add(start); buttons.Children.Add(stop); safety.Children.Add(buttons); safety.Children.Add(Text("Monitoring controls are disabled until the safe coordinator is connected.", 11, false, "#E6B85C")); PageContent.Children.Add(Card(safety));
        var events = new StackPanel(); events.Children.Add(Text("Recent activity", 18, true)); events.Children.Add(Text("No events yet. Events will appear here when the monitor is available.", 13, false, "#A3ACB9")); PageContent.Children.Add(Card(events));
    }
    private void ShowApplications()
    {
        Header(L("Applications", "Приложения"), L("Only programs you add here may be considered for monitoring.", "Для наблюдения доступны только добавленные здесь программы."));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) }; var add = new Button { Content = "＋  Choose EXE", Margin = new Thickness(0, 0, 9, 0) }; add.Click += (_, _) => AddExecutable(); bar.Children.Add(add); PageContent.Children.Add(bar);
        if (_applications.Count == 0) PageContent.Children.Add(Card(new StackPanel { Children = { Text("No applications added", 17, true), Text("Choose an executable to begin building your allowlist. Nothing is monitored automatically.", 13, false, "#A3ACB9") } }));
        foreach (var path in _applications.ToArray()) { var line = new DockPanel(); var remove = new Button { Content = "Remove" }; DockPanel.SetDock(remove, Dock.Right); remove.Click += async (_, _) => { var app = (await _applicationStore.ListAsync()).FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.ExecutablePath, path)); if (app is not null) await _applicationStore.RemoveAsync(app.Id); await ReloadApplications(); ShowApplications(); }; line.Children.Add(remove); line.Children.Add(new StackPanel { Children = { Text(System.IO.Path.GetFileName(path), 15, true), Text(path, 11, false, "#A3ACB9") } }); PageContent.Children.Add(Card(line)); }
        PageContent.Children.Add(Text("Default grace period: 3 seconds. Use caution: terminating an application may discard unsaved work.", 12, false, "#E6B85C"));
    }
    private async void AddExecutable() { var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Select an application executable", CheckFileExists = true, Multiselect = false }; if (dialog.ShowDialog(this) == true && !_applications.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase)) { await _applicationStore.AddOrUpdateAsync(new AppGuard.Core.Models.MonitoredApplication(Guid.NewGuid(), System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName)); await ReloadApplications(); ShowApplications(); } }
    private void ShowEmpty(string title, string description) { Header(L(title, title == "Logs" ? "Журнал" : "Монитор активности"), L(description, "Здесь появятся события после подключения службы мониторинга.")); PageContent.Children.Add(Card(new StackPanel { Children = { Text("No recorded activity", 17, true), Text("This view displays events only after the monitoring service reports them.", 13, false, "#A3ACB9") } })); }
    private void ShowSettings() { Header(_settings.Language == "ru" ? "Настройки" : "Settings", _settings.Language == "ru" ? "Выберите оформление и поведение AppGuard." : "Choose how AppGuard presents and stores your preferences."); var panel = new StackPanel(); panel.Children.Add(Text("Safety", 18, true)); var dry = new CheckBox { Content = "Dry Run (recommended)", IsChecked = true, IsEnabled = false, Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 5) }; panel.Children.Add(dry); panel.Children.Add(Text("Dry Run stays enabled until active termination controls have completed a safety review.", 12, false, "#A3ACB9")); panel.Children.Add(new Border { Height = 18 }); panel.Children.Add(Text("Appearance", 18, true)); var theme = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { "Dark", "Light" }, SelectedIndex = _settings.Theme == "light" ? 1 : 0 }; panel.Children.Add(theme); panel.Children.Add(Text("Language", 18, true)); var language = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { "English", "Русский" }, SelectedIndex = _settings.Language == "ru" ? 1 : 0 }; panel.Children.Add(language); panel.Children.Add(Text("When closing", 18, true)); var closeBehavior = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { "Ask every time", "Keep running in tray", "Exit AppGuard" }, SelectedIndex = _settings.CloseBehavior == "tray" ? 1 : _settings.CloseBehavior == "exit" ? 2 : 0 }; panel.Children.Add(closeBehavior); var save = new Button { Content = "Save settings", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; save.Click += async (_, _) => { _settings = _settings with { Theme = theme.SelectedIndex == 1 ? "light" : "dark", Language = language.SelectedIndex == 1 ? "ru" : "en", CloseBehavior = closeBehavior.SelectedIndex == 1 ? "tray" : closeBehavior.SelectedIndex == 2 ? "exit" : "ask" }; await _settingsStore.SaveAsync(_settings); ApplyPreferences(); ShowSettings(); }; panel.Children.Add(save); PageContent.Children.Add(Card(panel)); }
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowDashboard(); private void Applications_Click(object sender, RoutedEventArgs e) => ShowApplications(); private void Activity_Click(object sender, RoutedEventArgs e) => ShowEmpty("Activity Monitor", "Events from application monitoring."); private void Logs_Click(object sender, RoutedEventArgs e) => ShowEmpty("Logs", "Search and export the AppGuard activity journal."); private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
}
