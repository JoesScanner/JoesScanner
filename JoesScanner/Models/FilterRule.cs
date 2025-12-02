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

    // Represents a single filter row (Receiver, Site, or Talkgroup).
    // Level determines how the rule is applied.
    public class FilterRule : BindableObject
    {
        private FilterLevel _level;
        private string _receiver = string.Empty;
        private string _site = string.Empty;
        private string _talkgroup = string.Empty;
        private bool _isMuted;
        private bool _isDisabled;
        private DateTime _lastSeenUtc;

        // Filter level that determines if the rule applies at receiver, site, or talkgroup level.
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

        // Receiver label used for matching, for example VoiceReceiver or Source.
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

        // Site or system name used for matching.
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

        // Talkgroup label or ID used for matching.
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

        // When true, audio for calls matching this rule is muted but calls may still be displayed.
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

        // When true, calls that match this rule are neither shown nor heard.
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

        // Last time this rule matched traffic, stored as UTC.
        // Used for informational purposes and potential future cleanup.
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

        // Display text for the filters table:
        //   Receiver
        //   Receiver > Site
        //   Receiver > Site > Talkgroup
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
