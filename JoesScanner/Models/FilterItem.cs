using Microsoft.Maui.Controls;

namespace JoesScanner.Models
{
    /// <summary>
    /// Represents one line in the dynamic filter list: Receiver > Site > Talkgroup.
    /// Visual enabled/disabled state is driven by MainViewModel.
    /// </summary>
    public class FilterItem : BindableObject
    {
        private bool _isEnabled;

        public string Receiver { get; }
        public string Site { get; }
        public string Talkgroup { get; }

        /// <summary>
        /// True = green (allowed), False = red (blocked).
        /// This is computed by the view model based on disabled sets.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            internal set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public FilterItem(string receiver, string site, string talkgroup)
        {
            Receiver = receiver;
            Site = site;
            Talkgroup = talkgroup;
            _isEnabled = true;
        }

        public override bool Equals(object obj)
        {
            if (obj is not FilterItem other) return false;

            return string.Equals(Receiver, other.Receiver)
                   && string.Equals(Site, other.Site)
                   && string.Equals(Talkgroup, other.Talkgroup);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + (Receiver?.GetHashCode() ?? 0);
                hash = (hash * 23) + (Site?.GetHashCode() ?? 0);
                hash = (hash * 23) + (Talkgroup?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public override string ToString() => $"{Receiver} > {Site} > {Talkgroup}";
    }
}
