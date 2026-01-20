using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    public class ProgressViewModel
    {
        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(nameof(ProgressValue)); }
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
