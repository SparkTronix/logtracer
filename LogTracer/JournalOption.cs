using System.ComponentModel;

namespace LogTracer;

public sealed class JournalOption(string name, bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public string Name { get; } = name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
