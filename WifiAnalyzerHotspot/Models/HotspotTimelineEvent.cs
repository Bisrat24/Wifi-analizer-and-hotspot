using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiAnalyzerHotspot.Models
{
    public class HotspotTimelineEvent : INotifyPropertyChanged
    {
        private DateTime _timestamp = DateTime.Now;
        private string _eventType = "Joined";
        private string _hostName = "Device";
        private string _macAddress = string.Empty;
        private string _ipAddress = string.Empty;
        private string _durationText = "-";
        private string _details = string.Empty;

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                _timestamp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedTime));
            }
        }

        public string FormattedTime => _timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        public string TimeOnly => _timestamp.ToString("HH:mm:ss");

        public string EventType
        {
            get => _eventType;
            set
            {
                _eventType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EventBadgeColor));
                OnPropertyChanged(nameof(EventIcon));
            }
        }

        public string HostName
        {
            get => _hostName;
            set { _hostName = value; OnPropertyChanged(); }
        }

        public string MacAddress
        {
            get => _macAddress;
            set { _macAddress = value; OnPropertyChanged(); }
        }

        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        public string DurationText
        {
            get => _durationText;
            set { _durationText = value; OnPropertyChanged(); }
        }

        public string Details
        {
            get => _details;
            set { _details = value; OnPropertyChanged(); }
        }

        public string EventIcon => _eventType switch
        {
            "Joined" => "🟢",
            "Left" => "🔴",
            "Paused" => "⏸️",
            "Resumed" => "▶️",
            "Blocked" => "🚫",
            "Unblocked" => "✅",
            _ => "ℹ️"
        };

        public string EventBadgeColor => _eventType switch
        {
            "Joined" => "#00E676",
            "Left" => "#94A3B8",
            "Paused" => "#FFD600",
            "Resumed" => "#00E5FF",
            "Blocked" => "#FF5252",
            "Unblocked" => "#00E676",
            _ => "#00B0FF"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
