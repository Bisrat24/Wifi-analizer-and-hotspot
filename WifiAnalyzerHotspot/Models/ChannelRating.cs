using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WifiAnalyzerHotspot.Models
{
    public class ChannelRating : INotifyPropertyChanged
    {
        private int _channel;
        private string _band = "2.4 GHz";
        private int _score;
        private string _stars = "★★★★★";
        private int _coChannelCount;
        private int _overlapCount;
        private string _statusText = "Recommended";
        private bool _isRecommended;
        private Brush _badgeBrush = Brushes.LimeGreen;

        public int Channel
        {
            get => _channel;
            set { _channel = value; OnPropertyChanged(); }
        }

        public string Band
        {
            get => _band;
            set { _band = value; OnPropertyChanged(); }
        }

        public int Score
        {
            get => _score;
            set { _score = value; OnPropertyChanged(); }
        }

        public string Stars
        {
            get => _stars;
            set { _stars = value; OnPropertyChanged(); }
        }

        public int CoChannelCount
        {
            get => _coChannelCount;
            set { _coChannelCount = value; OnPropertyChanged(); }
        }

        public int OverlapCount
        {
            get => _overlapCount;
            set { _overlapCount = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsRecommended
        {
            get => _isRecommended;
            set { _isRecommended = value; OnPropertyChanged(); }
        }

        // Always a frozen Brush from Brushes.*
        public Brush BadgeBrush
        {
            get => _badgeBrush;
            set
            {
                if (value.CanFreeze && !value.IsFrozen)
                {
                    value.Freeze();
                }
                _badgeBrush = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
