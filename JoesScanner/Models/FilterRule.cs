using Microsoft.Maui.Controls;
using System;

namespace JoesScanner.Models
{
    public enum FilterLevel
    {
        Receiver,
        Site,
        Talkgroup
    }

    /// <summary>
    /// Represents a single filter row (Receiver, Site, or Talkgroup).
    /// Level determines how the rule is applied.
    /// </summary>
    public class FilterRule : BindableObject
    {
        private FilterLevel _level;
        private string _receiver = string.Empty;
        private string _site = string.Empty;
        private string _talkgroup = string.Empty;
        private bool _isMuted;
        private bool _isDisabled;
        private DateTime _lastSeenUtc;

        public FilterLevel Level
        {
            get => _level;
            set
            {
                if (_level == value)
                    return;

                _level = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayKey));
            }
        }

        /// <summary>
        /// Receiver label (for example VoiceReceiver or Source).
        /// </summary>
        public string Receiver
        {
            get => _receiver;
            set
            {
                var v = value ?? string.Empty;
                if (_receiver == v)
                    return;

                _receiver = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayKey));
            }
        }

        /// <summary>
        /// Site or system name.
        /// </summary>
        public string Site
        {
            get => _site;
            set
            {
                var v = value ?? string.Empty;
                if (_site == v)
                    return;

                _site = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayKey));
            }
        }

        /// <summary>
        /// Talkgroup label or ID.
        /// </summary>
        public string Talkgroup
        {
            get => _talkgroup;
            set
            {
                var v = value ?? string.Empty;
                if (_talkgroup == v)
                    return;

                _talkgroup = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayKey));
            }
        }

        /// <summary>
        /// When true, audio for this rule is muted but calls may still be shown.
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value)
                    return;

                _isMuted = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When true, calls that match this rule are not shown and not heard.
        /// </summary>
        public bool IsDisabled
        {
            get => _isDisabled;
            set
            {
                if (_isDisabled == value)
                    return;

                _isDisabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Last time this rule was seen in traffic (UTC).
        /// Used mostly for info and possible future cleanup.
        /// </summary>
        public DateTime LastSeenUtc
        {
            get => _lastSeenUtc;
            set
            {
                if (_lastSeenUtc == value)
                    return;

                _lastSeenUtc = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Display text for the filters table:
        ///   Receiver
        ///   Receiver > Site
        ///   Receiver > Site > Talkgroup
        /// </summary>
        public string DisplayKey
        {
            get
            {
                return Level switch
                {
                    FilterLevel.Receiver => Receiver,
                    FilterLevel.Site => string.IsNullOrWhiteSpace(Receiver)
                        ? Site
                        : $"{Receiver} > {Site}",
                    FilterLevel.Talkgroup => $"{Receiver} > {Site} > {Talkgroup}",
                    _ => Receiver
                };
            }
        }
    }
}
