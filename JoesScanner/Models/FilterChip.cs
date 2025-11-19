using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace JoesScanner.Models
{
    /// <summary>
    /// Bindable model for a single filter chip in the Settings filters card.
    /// Value = receiver / site / talkgroup label.
    /// IsActive = true means allowed (green), false means muted (grey).
    /// </summary>
    public class FilterChip : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        private bool _isActive = true;

        /// <summary>
        /// Display text and filter key for this chip.
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;

                _value = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When true, the chip is green and calls for this item are allowed.
        /// When false, the chip is grey and calls for this item are blocked
        /// once settings are saved.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                    return;

                _isActive = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
