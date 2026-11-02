using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloseLoom.Infrastructure.Persistence;
using CloseLoom.Infrastructure.Logging;
using CloseLoom.Core.Models;
using CloseLoom.Infrastructure.Monitoring;
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
namespace CloseLoom.Desktop;

public partial class MainWindow : Window
{
    private const int MaximumVisibleEvents = 500;
    private readonly List<MonitoredApplication> _applications = [];
    private readonly IUserSettingsStore _settingsStore;
    private readonly IApplicationStore _applicationStore;
    private readonly JsonLinesActivityLog _activityLog;
    private readonly IApplicationMonitor _monitor;
    private IReadOnlyList<ActivityEvent> _activityEvents = [];
    private StackPanel? _activityFeedPanel;
    private StackPanel? _logResultsPanel;
    private System.Windows.Controls.TextBox? _logFilterBox;
    private UserSettings _settings = new();
    private bool _allowClose;
    private bool _exitInProgress;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _ownedTrayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private readonly System.Windows.Threading.DispatcherTimer _statusRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    public MainWindow(IUserSettingsStore settingsStore, IApplicationStore applicationStore, JsonLinesActivityLog activityLog, IApplicationMonitor monitor) { _settingsStore = settingsStore; _applicationStore = applicationStore; _activityLog = activityLog; _monitor = monitor; _monitor.StatusChanged += Monitor_StatusChanged; _statusRefreshTimer.Tick += StatusRefreshTimer_Tick; Closed += (_, _) => { _monitor.StatusChanged -= Monitor_StatusChanged; _statusRefreshTimer.Stop(); DisposeTray(); }; InitializeComponent(); ShowDashboard(); }
    private void Monitor_StatusChanged(object? sender, ApplicationMonitorStatus _status) => Dispatcher.BeginInvoke(new Action(() =>
    {
        var status = _monitor.Status;
        UpdateStatusBadge(status);
        var shouldRefreshActivity = PageTitle.Text is "Dashboard" or "Обзор" or "Activity Monitor" or "Монитор активности";
        if (shouldRefreshActivity && !_statusRefreshTimer.IsEnabled) _statusRefreshTimer.Start();
    }));
    private void UpdateStatusBadge(ApplicationMonitorStatus status)
    {
        MonitorStatusText.Text = status.IsPaused ? L("Monitoring paused", "Мониторинг приостановлен") : status.IsRunning ? L("Monitoring active", "Мониторинг работает") : L("Monitoring stopped", "Мониторинг остановлен");
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status.IsPaused ? "#E6B85C" : status.IsRunning ? "#55D6A5" : "#8993A0"));
    }
    private async void StatusRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _statusRefreshTimer.Stop();
        if (PageTitle.Text is "Dashboard" or "Обзор")
        {
            await RefreshActivityAsync(showError: false);
            ShowDashboard();
        }
        else if (PageTitle.Text is "Activity Monitor" or "Монитор активности")
        {
            await RefreshActivityAsync(showError: false);
            RenderActivityFeed();
        }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            if (!_settings.DryRun)
            {
                _settings = _settings with { DryRun = true };
                await _settingsStore.SaveAsync(_settings);
                MessageBox.Show(L("Dry Run was restored because Active termination is unavailable.", "Dry Run включён снова, так как реальное завершение недоступно."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception) { _settings = new UserSettings(); MessageBox.Show(L("CloseLoom could not load preferences. Safe default settings are being used.", "Не удалось загрузить настройки. Используются безопасные параметры по умолчанию."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); }
        ApplyPreferences();
        try { await ReloadApplications(); }
        catch (Exception) { MessageBox.Show(L("CloseLoom could not load the approved application list. No applications will be monitored.", "Не удалось загрузить список разрешённых приложений. Наблюдение за программами не начнётся."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); }
        try { _activityLog.MaxBytes = _settings.MaxLogBytes; _activityEvents = await _activityLog.ReadAsync(); }
        catch (Exception) { _activityEvents = []; MessageBox.Show(L("CloseLoom could not read its activity log.", "Не удалось прочитать журнал событий."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); }
        ShowDashboard();
    }
    private async Task ReloadApplications() { _applications.Clear(); _applications.AddRange(await _applicationStore.ListAsync()); }
    private async Task ChangeMonitoringAsync(Func<ValueTask> action)
    {
        try { await action(); await RefreshActivityAsync(); ShowDashboard(); }
        catch (Exception) { MessageBox.Show(L("Monitoring could not be started or changed. See the activity log for details.", "Не удалось запустить или изменить мониторинг. Подробности смотрите в журнале."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowStorageError() => MessageBox.Show(L("The change could not be saved. Check local storage permissions and try again.", "Не удалось сохранить изменения. Проверьте доступ к локальному хранилищу и повторите попытку."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Error);
    private async Task SaveSettingsMutationAsync(UserSettings updated, bool refreshSettingsPage)
    {
        try { await _settingsStore.SaveAsync(updated); _settings = updated; _activityLog.MaxBytes = updated.MaxLogBytes; ApplyPreferences(); if (refreshSettingsPage) ShowSettings(); }
        catch (Exception) { ShowStorageError(); }
    }
    private void ApplyPreferences()
    {
        DashboardNav.Content = L("◫   Dashboard", "◫   Обзор"); ApplicationsNav.Content = L("▣   Applications", "▣   Приложения"); ActivityNav.Content = L("◷   Activity Monitor", "◷   Активность"); LogsNav.Content = L("☷   Logs", "☷   Журнал"); SettingsNav.Content = L("⚙   Settings", "⚙   Настройки"); ConfigureTray();
        var light = _settings.Theme == "light";
        System.Windows.Application.Current.Resources["Panel"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#1B2028"));
        System.Windows.Application.Current.Resources["ButtonForeground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#17202A" : "#F4F7FA"));
        System.Windows.Application.Current.Resources["ButtonBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#E6EBF0" : "#252C36"));
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#F4F6F8" : "#11151B"));
        PageTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#17202A" : "#F4F7FA"));
        PageSubtitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#596574" : "#A3ACB9"));
    }
    private void ConfigureTray()
    {
        DisposeTray();
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Открыть CloseLoom" : "Open CloseLoom", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Начать мониторинг" : "Start Monitoring", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await ChangeMonitoringAsync(() => _monitor.StartAsync())));
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Остановить мониторинг" : "Stop Monitoring", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await ChangeMonitoringAsync(() => _monitor.StopAsync())));
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Приостановить / продолжить" : "Pause / Resume Monitoring", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await ChangeMonitoringAsync(() => _monitor.Status.IsPaused ? _monitor.ResumeAsync() : _monitor.PauseAsync())));
        var dryRunEnabled = _monitor.Status.DryRunEnabled;
        var dryRun = new System.Windows.Forms.ToolStripMenuItem(dryRunEnabled ? (_settings.Language == "ru" ? "Dry Run (включён)" : "Dry Run (enabled)") : (_settings.Language == "ru" ? "Active (завершение недоступно)" : "Active (termination unavailable)")) { Checked = dryRunEnabled, CheckOnClick = false, Enabled = false };
        _trayMenu.Items.Add(dryRun);
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Последние события" : "Recent Activity", null, (_, _) => Dispatcher.Invoke(ShowRecentActivityFromTray));
        _trayMenu.Items.Add(_settings.Language == "ru" ? "Выйти" : "Exit CloseLoom", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitAppAsync())));
        var iconUri = new Uri("pack://application:,,,/Assets/CloseLoom.ico");
        var resource = System.Windows.Application.GetResourceStream(iconUri);
        if (resource is not null)
        {
            using var stream = resource.Stream;
            using var loaded = new System.Drawing.Icon(stream);
            _ownedTrayIcon = (System.Drawing.Icon)loaded.Clone();
        }
        _trayIcon = new System.Windows.Forms.NotifyIcon { Icon = _ownedTrayIcon ?? System.Drawing.SystemIcons.Application, Text = "CloseLoom", ContextMenuStrip = _trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    }
    private void DisposeTray()
    {
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        if (_trayMenu is not null) { _trayMenu.Dispose(); _trayMenu = null; }
        _ownedTrayIcon?.Dispose(); _ownedTrayIcon = null;
    }
    private void ShowRecentActivityFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ShowActivity();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_exitInProgress) { e.Cancel = true; return; }
        if (_settings.CloseBehavior == "tray")
        {
            e.Cancel = true; Hide();
            if (_settings.NotificationsEnabled) _trayIcon?.ShowBalloonTip(1800, "CloseLoom", L("CloseLoom is still running in the system tray.", "CloseLoom продолжает работать в системном трее."), System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        if (_settings.CloseBehavior == "ask" && MessageBox.Show(L("Close CloseLoom completely? Choose No to keep it open.", "Полностью закрыть CloseLoom? Выберите «Нет», чтобы оставить его открытым."), "CloseLoom", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        e.Cancel = true;
        // WPF forbids calling Close() reentrantly while this Closing event is being processed.
        // Defer shutdown until the current event handler has fully returned.
        Dispatcher.BeginInvoke(new Action(async () => await ExitAppAsync()));
    }
    private async Task ExitAppAsync()
    {
        if (_exitInProgress || _allowClose) return;
        _exitInProgress = true;
        try
        {
            await _monitor.StopAsync();
            await ((App)System.Windows.Application.Current).ShutdownHostAsync();
            _allowClose = true; DisposeTray(); Close();
        }
        catch (Exception)
        {
            _exitInProgress = false;
            MessageBox.Show(L("CloseLoom could not stop cleanly. It will remain open so monitoring state is not abandoned.", "CloseLoom не удалось безопасно остановить. Приложение останется открытым."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private Brush ThemeForeground => new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.Theme == "light" ? "#17202A" : "#F4F7FA"));
    private TextBlock Text(string value, double size = 14, bool bold = false, string? color = null) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color ?? (_settings.Theme == "light" ? "#17202A" : "#F4F7FA"))), TextWrapping = TextWrapping.Wrap };
    private Border Card(UIElement child) => new() { Background = (Brush)FindResource("Panel"), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B333E")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 12), Child = child };
    private string L(string english, string russian) => _settings.Language == "ru" ? russian : english;
    private void Header(string title, string subtitle) { _activityFeedPanel = null; _logResultsPanel = null; _logFilterBox = null; PageTitle.Text = title; PageSubtitle.Text = subtitle; PageContent.Children.Clear(); }
    private void ShowDashboard()
    {
        Header(L("Dashboard", "Обзор"), L("A clear view of your approved applications.", "Обзор разрешённых приложений."));
        var row = new WrapPanel();
        var status = _monitor.Status;
        UpdateStatusBadge(status);
        row.Children.Add(Card(new StackPanel { Children = { Text(L("MONITORED APPS", "ПРИЛОЖЕНИЯ"), 10, true, "#A3ACB9"), Text(_applications.Count.ToString(), 30, true), Text(L("Explicitly approved", "Разрешены пользователем"), 12, false, "#A3ACB9") } }));
        row.Children.Add(Card(new StackPanel { Children = { Text(L("TRACKED PROCESSES", "ПРОЦЕССЫ"), 10, true, "#A3ACB9"), Text(status.TrackedProcesses.ToString(), 30, true), Text(status.IsRunning ? L("Monitoring active", "Мониторинг работает") : L("Monitoring stopped", "Мониторинг остановлен"), 12, false, "#A3ACB9") } }));
        row.Children.Add(Card(new StackPanel { Children = { Text(L("TERMINATED", "ЗАВЕРШЕНО"), 10, true, "#A3ACB9"), Text(status.TotalTerminatedProcesses.ToString(), 30, true), Text(L("Confirmed by monitor", "Подтверждено монитором"), 12, false, "#A3ACB9") } })); PageContent.Children.Add(row);
        var safety = new StackPanel(); safety.Children.Add(Text(L("Protection at a glance", "Состояние защиты"), 19, true)); safety.Children.Add(Text(status.DryRunEnabled ? L("Dry Run is active. No processes will be terminated.", "Активен Dry Run. Завершение процессов не выполняется.") : L("Active mode is reported by the monitor, but the Windows terminator remains fail-closed.", "Монитор сообщает Active, но Windows terminator остаётся в fail-closed режиме."), 13, false, "#A3ACB9")); safety.Children.Add(new Border { Height = 12 });
        PageContent.Children.Add(Card(safety));
        var events = new StackPanel(); events.Children.Add(Text(L("Recent activity", "Последние события"), 18, true));
        foreach (var entry in _activityEvents.OrderByDescending(x => x.TimestampUtc).Take(5)) events.Children.Add(FormatActivity(entry));
        if (_activityEvents.Count == 0) events.Children.Add(Text(L("No recorded activity yet.", "Событий пока нет."), 13, false, "#A3ACB9"));
        var refresh = new Button { Content = L("Refresh activity", "Обновить события"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => { await RefreshActivityAsync(); ShowDashboard(); };
        events.Children.Add(refresh);
        PageContent.Children.Add(Card(events));
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var hasEnabledApp = _applications.Any(app => app.Enabled);
        var start = new Button { Content = L("Start Monitoring", "Начать мониторинг"), IsEnabled = !status.IsRunning && hasEnabledApp, Margin = new Thickness(0, 0, 9, 0), ToolTip = hasEnabledApp ? null : L("Add and enable an application first.", "Сначала добавьте и включите приложение.") };
        start.Click += async (_, _) => await ChangeMonitoringAsync(() => _monitor.StartAsync());
        var stop = new Button { Content = L("Stop Monitoring", "Остановить мониторинг"), IsEnabled = status.IsRunning };
        stop.Click += async (_, _) => await ChangeMonitoringAsync(() => _monitor.StopAsync());
        var pause = new Button { Content = status.IsPaused ? L("Resume", "Продолжить") : L("Pause", "Приостановить"), IsEnabled = status.IsRunning, Margin = new Thickness(9, 0, 0, 0) };
        pause.Click += async (_, _) => await ChangeMonitoringAsync(() => status.IsPaused ? _monitor.ResumeAsync() : _monitor.PauseAsync());
        controls.Children.Add(start); controls.Children.Add(stop); controls.Children.Add(pause); PageContent.Children.Add(Card(controls));
        if (!string.IsNullOrWhiteSpace(status.LastError)) PageContent.Children.Add(Text(status.LastError, 11, false, "#E6B85C"));
        PageContent.Children.Add(Text(L("Dry Run is on. Active termination is unavailable because the Windows terminator remains fail-closed.", "Dry Run включён. Реальное завершение недоступно: Windows terminator работает в безопасном fail-closed режиме."), 11, false, "#E6B85C"));
    }
    private void ShowApplications()
    {
        Header(L("Applications", "Приложения"), L("Only programs you add here may be considered for monitoring.", "Для наблюдения доступны только добавленные здесь программы."));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) }; var add = new Button { Content = L("＋  Choose EXE", "＋  Выбрать EXE"), Margin = new Thickness(0, 0, 9, 0) }; add.Click += (_, _) => AddExecutable(); bar.Children.Add(add); PageContent.Children.Add(bar);
        if (_applications.Count == 0) PageContent.Children.Add(Card(new StackPanel { Children = { Text(L("No applications added", "Приложения не добавлены"), 17, true), Text(L("Choose an executable to begin building your allowlist. Nothing is monitored automatically.", "Добавьте EXE в список разрешённых приложений. Автоматическое наблюдение не включается."), 13, false, "#A3ACB9") } }));
        foreach (var app in _applications.ToArray())
        {
            var line = new StackPanel();
            var heading = new DockPanel();
            var remove = new Button { Content = L("Remove", "Удалить") };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += async (_, _) => { try { await _applicationStore.RemoveAsync(app.Id); await ReloadApplications(); ShowApplications(); } catch (Exception) { ShowStorageError(); } };
            heading.Children.Add(remove);
            heading.Children.Add(new StackPanel { Children = { Text(app.Name, 15, true), Text(app.ExecutablePath, 11, false, "#A3ACB9") } });
            line.Children.Add(heading);
            var enabled = new CheckBox { Content = L("Monitor this application", "Наблюдать за приложением"), IsChecked = app.Enabled, Margin = new Thickness(0, 10, 0, 5), Foreground = ThemeForeground };
            var background = new CheckBox { Content = L("May continue working in the background (requires consent)", "Может продолжить работу в фоне (требуется согласие)"), IsChecked = app.AllowBackgroundTerminationConsent, Margin = new Thickness(0, 4, 0, 5), Foreground = ThemeForeground };
            var delay = new System.Windows.Controls.TextBox { Text = ((int)app.EffectiveGracePeriod.TotalSeconds).ToString(), Width = 72, Margin = new Thickness(0, 4, 0, 6) };
            var saveApp = new Button { Content = L("Save application", "Сохранить"), HorizontalAlignment = HorizontalAlignment.Left };
            saveApp.Click += async (_, _) =>
            {
                if (!int.TryParse(delay.Text, out var seconds) || seconds < 3 || seconds > 300) { MessageBox.Show(L("Grace period must be between 3 and 300 seconds.", "Задержка должна быть от 3 до 300 секунд."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                var consent = background.IsChecked == true;
                if (consent && !app.AllowBackgroundTerminationConsent && MessageBox.Show(L("This app may continue important work after its window closes. Do you explicitly consent to future termination when safety checks pass?", "Приложение может выполнять важную работу после закрытия окна. Вы явно соглашаетесь на возможное завершение после успешных проверок безопасности?"), "CloseLoom", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try { await _applicationStore.AddOrUpdateAsync(app with { Enabled = enabled.IsChecked == true, GracePeriod = TimeSpan.FromSeconds(seconds), MayContinueInBackground = consent, AllowBackgroundTerminationConsent = consent }); await ReloadApplications(); ShowApplications(); }
                catch (Exception) { ShowStorageError(); }
            };
            line.Children.Add(enabled); line.Children.Add(Text(L("Grace period (seconds; minimum 3)", "Задержка (секунды; минимум 3)"), 11, false, "#A3ACB9")); line.Children.Add(delay); line.Children.Add(background); line.Children.Add(saveApp);
            PageContent.Children.Add(Card(line));
        }
        PageContent.Children.Add(Text(L("Default grace period: 3 seconds. Terminating an application may discard unsaved work.", "Задержка по умолчанию: 3 секунды. Завершение приложения может привести к потере несохранённых данных."), 12, false, "#E6B85C"));
    }
    private async void AddExecutable() { var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = L("Select an application executable", "Выберите исполняемый файл приложения"), CheckFileExists = true, Multiselect = false }; if (dialog.ShowDialog(this) == true && !_applications.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.ExecutablePath, dialog.FileName))) try { await _applicationStore.AddOrUpdateAsync(new MonitoredApplication(Guid.NewGuid(), System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, GracePeriod: TimeSpan.FromSeconds(_settings.GracePeriodSeconds))); await ReloadApplications(); ShowApplications(); } catch (Exception) { ShowStorageError(); } }
    private UIElement FormatActivity(ActivityEvent entry)
    {
        var eventName = entry.Type switch
        {
            ActivityEventType.ProcessDetected => L("Process detected", "Процесс обнаружен"),
            ActivityEventType.WindowOpened => L("Window opened", "Окно открыто"),
            ActivityEventType.WindowClosed => L("Window closed", "Окно закрыто"),
            ActivityEventType.GracePeriodStarted => L("Grace period started", "Началась задержка"),
            ActivityEventType.GracePeriodCancelled => L("Grace period cancelled", "Задержка отменена"),
            ActivityEventType.TerminationRequested => L("Termination requested", "Запрошено завершение"),
            ActivityEventType.ProcessSkipped => L("Process skipped", "Процесс пропущен"),
            ActivityEventType.ProcessTerminated => L("Process terminated", "Процесс завершён"),
            ActivityEventType.ProcessExited => L("Process exited", "Процесс завершился"),
            ActivityEventType.Error => L("Error", "Ошибка"),
            _ => entry.Type.ToString()
        };
        var detail = $"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {eventName}  ·  {LocalizeKnownEventText(entry.Result)}";
        var row = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Text(entry.ApplicationName ?? L("Unknown application", "Неизвестное приложение"), 13, true));
        row.Children.Add(Text(detail, 11, false, "#A3ACB9"));
        if (entry.ProcessId is not null) row.Children.Add(Text($"PID {entry.ProcessId}", 10, false, "#A3ACB9"));
        if (!string.IsNullOrWhiteSpace(entry.Reason)) row.Children.Add(Text(LocalizeKnownEventText(entry.Reason), 11, false, "#A3ACB9"));
        return row;
    }
    private string LocalizeKnownEventText(string value) => _settings.Language == "ru" ? value switch
    {
        "Detected" => "Обнаружен",
        "Observed" => "Зафиксировано",
        "Started" => "Начато",
        "Cancelled" => "Отменено",
        "Skipped" => "Пропущено",
        "Dry run" => "Тестовый режим",
        "Error" => "Ошибка",
        "Process exit confirmed" => "Выход процесса подтверждён",
        "Termination requested" => "Запрошено завершение",
        "Monitoring stopped" => "Мониторинг остановлен",
        "Would terminate this verified process; Dry Run is enabled." => "Процесс прошёл проверки; в режиме Dry Run завершение не выполнялось.",
        "Termination check was cancelled." => "Проверка завершения отменена.",
        "A suitable window reopened during the grace period." => "Подходящее окно открылось снова во время задержки.",
        "Window state changed during the grace period." => "Состояние окна изменилось во время задержки.",
        "Process identity changed; PID may have been reused." => "Идентификатор процесса изменился; возможно, PID был использован повторно.",
        "A visible top-level window is still open." => "Открыто видимое окно приложения.",
        "Application is not enabled in the allowlist." => "Приложение отключено в списке разрешённых.",
        "Application is protected." => "Приложение находится под защитой.",
        "Executable is not in the allowlist." => "Исполняемый файл отсутствует в списке разрешённых.",
        "Executable does not match the allowlist entry." => "Исполняемый файл не совпадает с записью списка разрешённых.",
        "Current executable path does not match the selected application." => "Путь процесса не совпадает с выбранным приложением.",
        "Process is not proven to belong to the current user session." => "Не подтверждена принадлежность процесса текущему сеансу пользователя.",
        "Window state could not be verified." => "Не удалось проверить состояние окна.",
        "No eligible user window was observed for this process." => "Для процесса не обнаружено подходящего пользовательского окна.",
        "Process identity changed; PID was reused." => "Идентификатор процесса изменился; PID был повторно использован.",
        _ => value
    } : value;
    private async Task RefreshActivityAsync(bool showError = true)
    {
        try { _activityEvents = await _activityLog.ReadAsync(); }
        catch (Exception) { if (showError) MessageBox.Show(L("Unable to read the activity log.", "Не удалось прочитать журнал событий."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning); _activityEvents = []; }
    }
    private async void ShowActivity()
    {
        await RefreshActivityAsync();
        Header(L("Activity Monitor", "Монитор активности"), L("Events recorded by the monitor.", "События, записанные монитором."));
        _activityFeedPanel = new StackPanel(); PageContent.Children.Add(_activityFeedPanel); RenderActivityFeed();
    }
    private void RenderActivityFeed()
    {
        if (_activityFeedPanel is null) return;
        _activityFeedPanel.Children.Clear();
        var latest = _activityEvents.OrderByDescending(x => x.TimestampUtc).Take(MaximumVisibleEvents).ToArray();
        _activityFeedPanel.Children.Add(Text(L("Showing up to the 500 latest events.", "Показаны последние 500 событий."), 11, false, "#A3ACB9"));
        foreach (var entry in latest) _activityFeedPanel.Children.Add(Card(FormatActivity(entry)));
        if (_activityEvents.Count == 0) _activityFeedPanel.Children.Add(Card(Text(L("No recorded activity yet.", "Событий пока нет."), 13, false, "#A3ACB9")));
    }
    private async void ShowLogs()
    {
        await RefreshActivityAsync();
        Header(L("Logs", "Журнал"), L("Search and export recorded monitor events.", "Поиск и экспорт записанных событий."));
        _logFilterBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10), Height = 34, ToolTip = L("Filter by app, event, result, reason, or PID", "Фильтр: приложение, событие, результат, причина или PID") };
        _logResultsPanel = new StackPanel();
        _logFilterBox.TextChanged += (_, _) => RenderLogResults();
        PageContent.Children.Add(_logFilterBox);
        PageContent.Children.Add(Text(L("Search is limited to the latest 500 events. Export includes the full journal.", "Поиск выполняется по последним 500 событиям. Экспорт содержит полный журнал."), 11, false, "#A3ACB9"));
        var refresh = new Button { Content = L("Refresh log", "Обновить журнал"), Margin = new Thickness(0, 8, 0, 10) };
        refresh.Click += async (_, _) => { await RefreshActivityAsync(); RenderLogResults(); };
        var export = new Button { Content = L("Export log…", "Экспорт журнала…"), Margin = new Thickness(0, 0, 0, 12) };
        export.Click += async (_, _) => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON Lines (*.jsonl)|*.jsonl|All files (*.*)|*.*", FileName = "closeloom-activity.jsonl", AddExtension = true }; if (dialog.ShowDialog(this) == true) try { await _activityLog.ExportAsync(dialog.FileName); } catch (Exception) { MessageBox.Show(L("Log export failed.", "Не удалось экспортировать журнал."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Error); } };
        PageContent.Children.Add(refresh); PageContent.Children.Add(export); PageContent.Children.Add(_logResultsPanel); RenderLogResults();
    }
    private void RenderLogResults()
    {
        if (_logResultsPanel is null) return;
        _logResultsPanel.Children.Clear();
        var query = _logFilterBox?.Text ?? string.Empty;
        var found = _activityEvents.OrderByDescending(x => x.TimestampUtc).Take(MaximumVisibleEvents).Where(x => string.Join(" ", x.ApplicationName, x.Type, x.Result, x.Reason, x.ProcessId).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var item in found) _logResultsPanel.Children.Add(Card(FormatActivity(item)));
        if (found.Length == 0) _logResultsPanel.Children.Add(Card(Text(L("No matching log events.", "События не найдены."), 13, false, "#A3ACB9")));
    }
    private void ShowSettings()
    {
        Header(L("Settings", "Настройки"), L("Preferences are stored in your local CloseLoom profile.", "Параметры хранятся локально в профиле CloseLoom."));
        var panel = new StackPanel();
        panel.Children.Add(Text(L("Safety", "Безопасность"), 18, true));
        var dryRunEnabled = _monitor.Status.DryRunEnabled;
        panel.Children.Add(new CheckBox { Content = dryRunEnabled ? L("Dry Run is enabled", "Режим Dry Run включён") : L("Active mode reported by monitor", "Монитор сообщает режим Active"), IsChecked = dryRunEnabled, IsEnabled = false, Foreground = ThemeForeground, Margin = new Thickness(0, 14, 0, 5) });
        panel.Children.Add(Text(dryRunEnabled ? L("No processes will be terminated in Dry Run.", "В режиме Dry Run процессы не завершаются.") : L("The Windows terminator still refuses active termination.", "Windows terminator по-прежнему отказывает в реальном завершении."), 12, false, "#E6B85C"));
        panel.Children.Add(new Border { Height = 16 });
        panel.Children.Add(Text(L("Appearance", "Оформление"), 18, true));
        var theme = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { "Dark", "Light" }, SelectedIndex = _settings.Theme == "light" ? 1 : 0 };
        panel.Children.Add(theme);
        panel.Children.Add(Text(L("Language", "Язык интерфейса"), 18, true));
        var language = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { "English", "Русский" }, SelectedIndex = _settings.Language == "ru" ? 1 : 0 };
        panel.Children.Add(language);
        panel.Children.Add(Text(L("Default grace period in seconds (3–300)", "Задержка завершения по умолчанию (3–300 секунд)"), 13, true));
        var grace = new System.Windows.Controls.TextBox { Text = _settings.GracePeriodSeconds.ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(grace);
        var notifications = new CheckBox { Content = L("Show notifications", "Показывать уведомления"), IsChecked = _settings.NotificationsEnabled, Foreground = ThemeForeground, Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(notifications);
        panel.Children.Add(Text(L("Protected applications", "Защищённые приложения"), 18, true));
        panel.Children.Add(Text(L("These executable paths are excluded from termination decisions.", "Эти исполняемые файлы исключены из решений о завершении."), 12, false, "#A3ACB9"));
        foreach (var protectedPath in _settings.ProtectedExecutablePaths)
        {
            var protectedRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var removeProtected = new Button { Content = L("Remove", "Удалить") };
            DockPanel.SetDock(removeProtected, Dock.Right);
            removeProtected.Click += async (_, _) => await SaveSettingsMutationAsync(_settings with { ProtectedExecutablePaths = _settings.ProtectedExecutablePaths.Where(x => !StringComparer.OrdinalIgnoreCase.Equals(x, protectedPath)).ToArray() }, refreshSettingsPage: true);
            protectedRow.Children.Add(removeProtected); protectedRow.Children.Add(Text(protectedPath, 11, false, "#A3ACB9")); panel.Children.Add(protectedRow);
        }
        var addProtected = new Button { Content = L("＋ Add protected EXE", "＋ Добавить защищённый EXE"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8) };
        addProtected.Click += async (_, _) => { var picker = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = false, Title = L("Select a protected executable", "Выберите защищённый EXE") }; if (picker.ShowDialog(this) == true && !_settings.ProtectedExecutablePaths.Contains(picker.FileName, StringComparer.OrdinalIgnoreCase)) await SaveSettingsMutationAsync(_settings with { ProtectedExecutablePaths = _settings.ProtectedExecutablePaths.Append(picker.FileName).ToArray() }, refreshSettingsPage: true); };
        panel.Children.Add(addProtected);
        panel.Children.Add(Text(L("Maximum activity log size in MiB (1–100)", "Максимальный размер журнала в МиБ (1–100)"), 13, true));
        var maxLog = new System.Windows.Controls.TextBox { Text = Math.Clamp(_settings.MaxLogBytes / (1024 * 1024), 1, 100).ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(maxLog);
        panel.Children.Add(Text(L("When closing", "При закрытии окна"), 18, true));
        var closeBehavior = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 6), Items = { L("Ask every time", "Спрашивать"), L("Keep running in tray", "Оставлять в трее"), L("Exit CloseLoom", "Завершать CloseLoom") }, SelectedIndex = _settings.CloseBehavior == "tray" ? 1 : _settings.CloseBehavior == "exit" ? 2 : 0 };
        panel.Children.Add(closeBehavior);
        var save = new Button { Content = L("Save settings", "Сохранить настройки"), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(grace.Text, out var seconds) || seconds is < 3 or > 300 || !int.TryParse(maxLog.Text, out var logMiB) || logMiB is < 1 or > 100)
            {
                MessageBox.Show(L("Grace period must be 3–300 seconds and log size 1–100 MiB.", "Задержка должна быть 3–300 секунд, размер журнала — 1–100 МиБ."), "CloseLoom", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var updated = _settings with { DryRun = true, Theme = theme.SelectedIndex == 1 ? "light" : "dark", Language = language.SelectedIndex == 1 ? "ru" : "en", GracePeriodSeconds = seconds, NotificationsEnabled = notifications.IsChecked == true, MaxLogBytes = (long)logMiB * 1024 * 1024, CloseBehavior = closeBehavior.SelectedIndex == 1 ? "tray" : closeBehavior.SelectedIndex == 2 ? "exit" : "ask" };
            await SaveSettingsMutationAsync(updated, refreshSettingsPage: true);
        };
        panel.Children.Add(save);
        PageContent.Children.Add(Card(panel));
    }
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowDashboard(); private void Applications_Click(object sender, RoutedEventArgs e) => ShowApplications(); private void Activity_Click(object sender, RoutedEventArgs e) => ShowActivity(); private void Logs_Click(object sender, RoutedEventArgs e) => ShowLogs(); private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
}
