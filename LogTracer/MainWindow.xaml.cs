using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LogTracer;

public partial class MainWindow : Window
{
    private const int EventsPerLog = 250;
    private static readonly string[] DefaultLogNames = ["Application", "System", "Security"];
    private readonly ObservableCollection<EventRecord> _events = [];
    private readonly ObservableCollection<JournalOption> _journalOptions = [];
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
        _eventsView.SortDescriptions.Add(new SortDescription(nameof(EventRecord.Timestamp), ListSortDirection.Descending));
        EventsDataGrid.ItemsSource = _eventsView;

        InitializeTimePickers();
        LoadJournalOptions();
        JournalOptionsListBox.ItemsSource = _journalOptions;
        UpdateLogFilterOptions();
        LevelComboBox.ItemsSource = new[] { "Все", "Ошибка", "Предупреждение", "Сведения", "Аудит успеха", "Аудит отказа" };
        LevelComboBox.SelectedIndex = 0;

        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += async (_, _) => await LoadEventsAsync();
        Closing += (_, _) => StopAutoRefresh();
    }

    private void InitializeTimePickers()
    {
        var hours = Enumerable.Range(0, 24)
            .Select(hour => hour.ToString("00", CultureInfo.InvariantCulture))
            .ToArray();
        var minutes = Enumerable.Range(0, 60)
            .Select(minute => minute.ToString("00", CultureInfo.InvariantCulture))
            .ToArray();

        StartHourComboBox.ItemsSource = hours;
        EndHourComboBox.ItemsSource = hours;
        StartMinuteComboBox.ItemsSource = minutes;
        EndMinuteComboBox.ItemsSource = minutes;
    }

    private void LoadJournalOptions()
    {
        var logNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var eventLog in EventLog.GetEventLogs())
            {
                logNames.Add(eventLog.Log);
                eventLog.Dispose();
            }
        }
        catch (Exception)
        {
        }

        logNames.UnionWith(DefaultLogNames);

        foreach (var logName in logNames)
        {
            _journalOptions.Add(new JournalOption(logName, DefaultLogNames.Contains(logName, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private string[] GetSelectedLogNames() => _journalOptions
        .Where(option => option.IsSelected)
        .Select(option => option.Name)
        .ToArray();

    private void UpdateLogFilterOptions()
    {
        var selectedLog = LogComboBox.SelectedItem as string;
        var logNames = GetSelectedLogNames();
        LogComboBox.ItemsSource = new[] { "Все" }.Concat(logNames).ToArray();
        LogComboBox.SelectedItem = logNames.Contains(selectedLog, StringComparer.OrdinalIgnoreCase) ? selectedLog : "Все";
    }

    private async void JournalSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateLogFilterOptions();
        RestartRealTimeUpdates();
        await LoadEventsAsync();
    }

    private void RestartRealTimeUpdates()
    {
        if (_eventWatchers.Count == 0)
        {
            return;
        }

        StopAutoRefresh();
        StartRealTimeUpdates();
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
            var selectedLogNames = GetSelectedLogNames();
            var result = await Task.Run(() => ReadEvents(selectedLogNames));

            _events.Clear();
            foreach (var eventRecord in result.Events)
            {
                _events.Add(eventRecord);
            }

            _eventsView.Refresh();
            UpdateEventCounts();
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

    private static LoadResult ReadEvents(IEnumerable<string> logNames)
    {
        var events = new List<EventRecord>();
        var errors = new List<string>();

        foreach (var logName in logNames)
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
        if (item is not EventRecord eventRecord
            || !TryGetBoundary(StartDatePicker, StartHourComboBox, StartMinuteComboBox, false, out var start)
            || !TryGetBoundary(EndDatePicker, EndHourComboBox, EndMinuteComboBox, true, out var end))
        {
            return false;
        }

        var searchText = SearchTextBox.Text.Trim();
        var eventId = EventIdTextBox.Text.Trim();
        var selectedLog = LogComboBox.SelectedItem as string ?? "Все";
        var selectedLevel = LevelComboBox.SelectedItem as string ?? "Все";

        return (start is null || eventRecord.Timestamp >= start)
            && (end is null || eventRecord.Timestamp <= end)
            && (selectedLog == "Все" || eventRecord.LogName == selectedLog)
            && (selectedLevel == "Все" || eventRecord.Level == selectedLevel)
            && (eventId.Length == 0 || eventRecord.EventId.ToString().Contains(eventId, StringComparison.Ordinal))
            && (searchText.Length == 0
                || eventRecord.LogName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || eventRecord.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || eventRecord.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetBoundary(DatePicker datePicker, ComboBox hourComboBox, ComboBox minuteComboBox, bool isEnd, out DateTime? boundary)
    {
        boundary = null;

        if (datePicker.SelectedDate is not DateTime date)
        {
            return true;
        }

        var hour = hourComboBox.SelectedItem as string;
        var minute = minuteComboBox.SelectedItem as string;
        if (hour is null && minute is null)
        {
            boundary = isEnd ? date.Date.AddDays(1).AddTicks(-1) : date.Date;
            return true;
        }

        if (hour is null || minute is null)
        {
            return false;
        }

        boundary = date.Date
            .AddHours(int.Parse(hour, CultureInfo.InvariantCulture))
            .AddMinutes(int.Parse(minute, CultureInfo.InvariantCulture));
        return true;
    }

    private void UpdateEventCounts()
    {
        var visibleEvents = _eventsView.Cast<EventRecord>().ToList();

        TotalEventsCountTextBlock.Text = visibleEvents.Count.ToString(CultureInfo.CurrentCulture);
        ErrorEventsCountTextBlock.Text = visibleEvents.Count(eventRecord => eventRecord.Level == "Ошибка").ToString(CultureInfo.CurrentCulture);
        WarningEventsCountTextBlock.Text = visibleEvents.Count(eventRecord => eventRecord.Level == "Предупреждение").ToString(CultureInfo.CurrentCulture);
        InformationEventsCountTextBlock.Text = visibleEvents.Count(eventRecord => eventRecord.Level == "Сведения").ToString(CultureInfo.CurrentCulture);
        SuccessAuditEventsCountTextBlock.Text = visibleEvents.Count(eventRecord => eventRecord.Level == "Аудит успеха").ToString(CultureInfo.CurrentCulture);
        FailureAuditEventsCountTextBlock.Text = visibleEvents.Count(eventRecord => eventRecord.Level == "Аудит отказа").ToString(CultureInfo.CurrentCulture);
    }

    private bool HasValidTimeFilters() => TryGetBoundary(StartDatePicker, StartHourComboBox, StartMinuteComboBox, false, out _)
        && TryGetBoundary(EndDatePicker, EndHourComboBox, EndMinuteComboBox, true, out _);

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_eventsView is null)
        {
            return;
        }

        _eventsView.Refresh();
        UpdateEventCounts();
        StatusTextBlock.Text = HasValidTimeFilters()
            ? $"Показано событий: {_eventsView.Cast<object>().Count()} из {_events.Count}."
            : "Выберите часы и минуты для обеих границ периода.";
    }

    private void ClearDateFilterButton_Click(object sender, RoutedEventArgs e)
    {
        StartDatePicker.SelectedDate = null;
        EndDatePicker.SelectedDate = null;
        StartHourComboBox.SelectedItem = null;
        StartMinuteComboBox.SelectedItem = null;
        EndHourComboBox.SelectedItem = null;
        EndMinuteComboBox.SelectedItem = null;
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventsAsync();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            DefaultExt = ".csv",
            FileName = $"LogTracer-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV-файл (*.csv)|*.csv"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var visibleEvents = _eventsView.Cast<EventRecord>().ToList();

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, BuildCsv(visibleEvents), new UTF8Encoding(true));
            StatusTextBlock.Text = $"Сохранено событий: {visibleEvents.Count}.";
        }
        catch (Exception)
        {
            StatusTextBlock.Text = "Не удалось сохранить файл.";
        }
    }

    private static string BuildCsv(IEnumerable<EventRecord> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Время;Журнал;Уровень;ID;Источник;Сообщение");

        foreach (var eventRecord in events)
        {
            builder.AppendLine(string.Join(';',
                EscapeCsv(eventRecord.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                EscapeCsv(eventRecord.LogName),
                EscapeCsv(eventRecord.Level),
                EscapeCsv(eventRecord.EventId.ToString(CultureInfo.InvariantCulture)),
                EscapeCsv(eventRecord.Source),
                EscapeCsv(eventRecord.Message)));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]))
        {
            value = $"'{value}";
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
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
        foreach (var logName in GetSelectedLogNames())
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
        StartDatePicker.IsEnabled = !isLoading;
        StartHourComboBox.IsEnabled = !isLoading;
        StartMinuteComboBox.IsEnabled = !isLoading;
        EndDatePicker.IsEnabled = !isLoading;
        EndHourComboBox.IsEnabled = !isLoading;
        EndMinuteComboBox.IsEnabled = !isLoading;
        JournalOptionsListBox.IsEnabled = !isLoading;
        ReloadButton.IsEnabled = !isLoading;
        ExportButton.IsEnabled = !isLoading;
        StatusTextBlock.Text = isLoading ? "Загрузка событий…" : StatusTextBlock.Text;
    }

    private sealed record LoadResult(IReadOnlyList<EventRecord> Events, IReadOnlyList<string> Errors);
}
