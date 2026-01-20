using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 히스토리 재생 기능을 총괄하는 메인 ViewModel입니다.
    /// 하위 ViewModel(HistoryPlaybackViewModel)의 생성과 소멸(Lifecycle)을 관리합니다.
    /// </summary>
    public class HistoryMainViewModel : INotifyPropertyChanged
    {
        private HistoryPlaybackViewModel _currentPlaybackVM;
        public HistoryPlaybackViewModel CurrentPlaybackVM
        {
            get => _currentPlaybackVM;
            private set
            {
                if (_currentPlaybackVM != value)
                {
                    _currentPlaybackVM = value;
                    OnPropertyChanged(nameof(CurrentPlaybackVM));
                }
            }
        }

        // [명령] 파일을 열고 로드하는 Command (RelayCommand 구현체 필요, 여기선 개념적 구현)
        // 실제로는 ICommand 구현체(RelayCommand 등)를 사용해야 합니다.
        // public ICommand OpenCommand { get; } 

        /// <summary>
        /// 새로운 DB 파일과 설정을 로드합니다.
        /// 기존 ViewModel은 폐기(Dispose)되고 새로운 인스턴스로 교체됩니다. (Immutable Pattern)
        /// </summary>
        public async Task OpenHistoryAsync(string dbPath, List<TableConfig> configs)
        {
            if (string.IsNullOrEmpty(dbPath)) return;

            // 1. 기존 인스턴스 정리 (Dispose)
            if (CurrentPlaybackVM != null)
            {
                CurrentPlaybackVM.Dispose();
                CurrentPlaybackVM = null;
                
                // GC 유도 (옵션: 대용량 메모리 해제 힌트)
                // GC.Collect(); 
            }

            // 2. 새로운 인스턴스 생성 (One-Shot)
            var newVM = new HistoryPlaybackViewModel(configs);
            
            // 3. UI 교체 (이제 View는 새로운 VM을 바라봄)
            CurrentPlaybackVM = newVM;

            // 4. 데이터 로딩 시작
            await newVM.LoadAsync(dbPath);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
