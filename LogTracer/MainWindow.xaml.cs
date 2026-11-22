using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace LogTracer;

public partial class MainWindow : Window
{
    private const int EventsPerLog = 250;
    private static readonly string[] LogNames = ["Application", "System", "Security"];
    private readonly ObservableCollection<EventRecord> _events = [];
    private readonly ICollectionView _eventsView;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly List<EventLog> _eventWatchers = [];
    private bool _isLoading;
    private DateTime _lastRealTimeRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _eventsView = CollectionViewSource.GetDefaultView(_events);
        _eventsView.Filter = MatchesFilter;
        EventsDataGrid.ItemsSource = _eventsView;

        LogComboBox.ItemsSource = new[] { "Все", "Application", "System", "Security" };
        LevelComboBox.ItemsSource = new[] { "Все", "Ошибка", "Предупреждение", "Сведения", "Аудит успеха", "Аудит отказа" };
        LogComboBox.SelectedIndex = 0;
        LevelComboBox.SelectedIndex = 0;

        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += async (_, _) => await LoadEventsAsync();
        Closing += (_, _) => StopAutoRefresh();
    }

    private async Task LoadEventsAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        SetLoadingState(true);

        try
        {
            var result = await Task.Run(ReadEvents);

            _events.Clear();
            foreach (var eventRecord in result.Events)
            {
                _events.Add(eventRecord);
            }

            _eventsView.Refresh();
            StatusTextBlock.Text = result.Errors.Count == 0
                ? $"Загружено событий: {_eventsView.Cast<object>().Count()}."
                : $"Загружено событий: {_eventsView.Cast<object>().Count()}. Недоступные журналы: {string.Join(", ", result.Errors)}.";
        }
        finally
        {
            _isLoading = false;
            SetLoadingState(false);
        }
    }

    private static LoadResult ReadEvents()
    {
        var events = new List<EventRecord>();
        var errors = new List<string>();

        foreach (var logName in LogNames)
        {
            try
            {
                using var eventLog = new EventLog(logName);
                var firstIndex = Math.Max(0, eventLog.Entries.Count - EventsPerLog);

                for (var index = eventLog.Entries.Count - 1; index >= firstIndex; index--)
                {
                    var entry = eventLog.Entries[index];
                    events.Add(new EventRecord(
                        entry.TimeGenerated,
                        logName,
                        GetLevel(entry.EntryType),
                        GetEventId(entry),
                        entry.Source,
                        GetMessage(entry)));
                }
            }
            catch (Exception)
            {
                errors.Add(logName);
            }
        }

        return new LoadResult(events.OrderByDescending(record => record.Timestamp).ToList(), errors);
    }

    private static int GetEventId(EventLogEntry entry) => (int)(entry.InstanceId & 0xFFFF);

    private static string GetLevel(EventLogEntryType entryType) => entryType switch
    {
        EventLogEntryType.Error => "Ошибка",
        EventLogEntryType.Warning => "Предупреждение",
        EventLogEntryType.Information => "Сведения",
        EventLogEntryType.SuccessAudit => "Аудит успеха",
        EventLogEntryType.FailureAudit => "Аудит отказа",
        _ => entryType.ToString()
    };

    private static string GetMessage(EventLogEntry entry)
    {
        try
        {
            return entry.Message;
        }
        catch (Exception)
        {
            return "Сообщение недоступно.";
        }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not EventRecord eventRecord)
        {
            return false;
        }

        var searchText = SearchTextBox.Text.Trim();
        var eventId = EventIdTextBox.Text.Trim();
        var selectedLog = LogComboBox.SelectedItem as string ?? "Все";
        var selectedLevel = LevelComboBox.SelectedItem as string ?? "Все";

        return (selectedLog == "Все" || eventRecord.LogName == selectedLog)
            && (selectedLevel == "Все" || eventRecord.Level == selectedLevel)
            && (eventId.Length == 0 || eventRecord.EventId.ToString().Contains(eventId, StringComparison.Ordinal))
            && (searchText.Length == 0
                || eventRecord.LogName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || eventRecord.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || eventRecord.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_eventsView is null)
        {
            return;
        }

        _eventsView.Refresh();
        StatusTextBlock.Text = $"Показано событий: {_eventsView.Cast<object>().Count()} из {_events.Count}.";
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync();
    }

    private void AutoRefreshModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { IsChecked: true, Tag: string mode })
        {
            return;
        }

        StopAutoRefresh();

        if (mode == "RealTime")
        {
            StartRealTimeUpdates();
            StatusTextBlock.Text = "Автообновление: в реальном времени.";
            return;
        }

        if (int.TryParse(mode, out var seconds))
        {
            _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
            _refreshTimer.Start();
            StatusTextBlock.Text = $"Автообновление: каждые {seconds} секунд.";
        }
    }

    private void StartRealTimeUpdates()
    {
        foreach (var logName in LogNames)
        {
            EventLog? eventLog = null;

            try
            {
                eventLog = new EventLog(logName) { EnableRaisingEvents = true };
                eventLog.EntryWritten += EventLog_EntryWritten;
                _eventWatchers.Add(eventLog);
            }
            catch (Exception)
            {
                eventLog?.Dispose();
            }
        }
    }

    private void EventLog_EntryWritten(object sender, EntryWrittenEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_isLoading || DateTime.UtcNow - _lastRealTimeRefresh < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _lastRealTimeRefresh = DateTime.UtcNow;
            await LoadEventsAsync();
        });
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await LoadEventsAsync();
    }

    private void StopAutoRefresh()
    {
        _refreshTimer.Stop();

        foreach (var eventLog in _eventWatchers)
        {
            eventLog.EntryWritten -= EventLog_EntryWritten;
            eventLog.EnableRaisingEvents = false;
            eventLog.Dispose();
        }

        _eventWatchers.Clear();
    }

    private void SetLoadingState(bool isLoading)
    {
        SearchTextBox.IsEnabled = !isLoading;
        EventIdTextBox.IsEnabled = !isLoading;
        LogComboBox.IsEnabled = !isLoading;
        LevelComboBox.IsEnabled = !isLoading;
        ReloadButton.IsEnabled = !isLoading;
        StatusTextBlock.Text = isLoading ? "Загрузка событий…" : StatusTextBlock.Text;
    }

    private sealed record LoadResult(IReadOnlyList<EventRecord> Events, IReadOnlyList<string> Errors);
}
