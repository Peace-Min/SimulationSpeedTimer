using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SimulationSpeedTimer
{
    /// <summary>
    /// 전역 공유 메모리 저장소 (순수 데이터 저장소 역할)
    /// - GlobalDataService가 조회한 데이터를 시간 기반으로 저장
    /// - 외부 서비스(차트 등)의 조회 요청에 응답
    /// - 이벤트 발행 없음 (Pull 방식만 지원)
    /// </summary>
    public class SharedFrameRepository
    {
        private static SharedFrameRepository _instance;
        public static SharedFrameRepository Instance => _instance ?? (_instance = new SharedFrameRepository());

        // 핵심 저장소: Key = Time, Value = Frame
        private readonly ConcurrentDictionary<double, SimulationFrame> _frames;

        // 시간 순서 유지 (빠른 범위 조회용)
        private readonly SortedSet<double> _timeIndex;

        // 동기화 (SortedSet은 thread-safe하지 않음)
        private readonly ReaderWriterLockSlim _lock;

        // 슬라이딩 윈도우 설정 (메모리 관리)
        private double _maxWindowSize = 60.0; // 최근 60초만 유지

        // 세션 관리 (데이터 격리용)
        private Guid _currentSessionId;
        public Guid CurrentSessionId => _currentSessionId;

        // 데이터 추가 알림 이벤트 (프레임 리스트, 세션 ID)
        public event Action<List<SimulationFrame>, Guid> OnFramesAdded;

        // 스키마 정보 (논리-물리 매핑용)
        public SimulationSchema Schema { get; set; }

        private SharedFrameRepository()
        {
            _frames = new ConcurrentDictionary<double, SimulationFrame>();
            _timeIndex = new SortedSet<double>();
            _lock = new ReaderWriterLockSlim();
            _currentSessionId = Guid.NewGuid(); // 초기 세션 ID
        }

        /// <summary>
        /// 슬라이딩 윈도우 크기 설정 (초 단위)
        /// </summary>
        public void SetWindowSize(double seconds)
        {
            if (seconds > 0)
                _maxWindowSize = seconds;
        }

        /// <summary>
        /// 청크 단위로 데이터 저장 (GlobalDataService에서 호출)
        /// </summary>
        public void StoreChunk(Dictionary<double, SimulationFrame> chunk, Guid sessionId)
        {
            if (chunk == null || chunk.Count == 0) return;

            // 핵심: 세션 ID 검증 (이전 세션의 데이터가 늦게 도착하면 폐기)
            if (sessionId != _currentSessionId)
            {
                // Console.WriteLine($"[SharedFrameRepository] Data rejected. Session mismatch. (Start:{sessionId} != Current:{_currentSessionId})");
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                // 재검증 (Lock 획득 사이 변경 가능성)
                if (sessionId != _currentSessionId) return;

                foreach (var kvp in chunk)
                {
                    if (_frames.TryGetValue(kvp.Key, out var existingFrame))
                    {
                        // 기존 프레임이 있으면 병합 (테이블 데이터 추가/갱신)
                        existingFrame.Merge(kvp.Value);
                    }
                    else
                    {
                        // 없으면 신규 등록
                        _frames[kvp.Key] = kvp.Value;
                        _timeIndex.Add(kvp.Key);
                    }
                }

                // 슬라이딩 윈도우 적용 (오래된 데이터 제거)
                CleanupOldFrames();
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // 이벤트 발생: 저장된 프레임들을 시간순으로 정렬하여 통보
            // (Lock 밖에서 안전하게 호출)
            if (chunk.Count > 0)
            {
                var sortedFrames = chunk.OrderBy(x => x.Key).Select(x => x.Value).ToList();
                OnFramesAdded?.Invoke(sortedFrames, _currentSessionId);
            }
        }

        /// <summary>
        /// 특정 시간의 프레임 조회 (O(1))
        /// </summary>
        public SimulationFrame GetFrame(double time)
        {
            return _frames.TryGetValue(time, out var frame) ? frame : null;
        }

        /// <summary>
        /// 시간 범위 조회 (차트용)
        /// </summary>
        public List<SimulationFrame> GetFramesInRange(double start, double end)
        {
            _lock.EnterReadLock();
            try
            {
                var times = _timeIndex.Where(t => t >= start && t <= end);
                return times.Select(t => _frames[t]).ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 최근 N개의 프레임을 조회합니다 (테스트 및 디버깅용)
        /// </summary>
        public List<SimulationFrame> GetLatestFrames(int count)
        {
            _lock.EnterReadLock();
            try
            {
                // 최신순으로 가져와서 다시 시간순(오름차순)으로 정렬
                var times = _timeIndex.Reverse().Take(count).OrderBy(t => t);
                return times.Select(t => _frames[t]).ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 특정 테이블의 특정 컬럼 값 조회 (차트 데이터 추출)
        /// </summary>
        public List<(double Time, object Value)> GetColumnValues(
            string tableName,
            string columnName,
            double startTime,
            double endTime)
        {
            _lock.EnterReadLock();
            try
            {
                var result = new List<(double, object)>();
                var times = _timeIndex.Where(t => t >= startTime && t <= endTime);

                foreach (var time in times)
                {
                    if (_frames.TryGetValue(time, out var frame))
                    {
                        var table = frame.GetTable(tableName);
                        if (table != null)
                        {
                            var value = table[columnName];
                            if (value != null)
                            {
                                result.Add((time, value));
                            }
                        }
                    }
                }
                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 현재 저장된 시간 범위 조회
        /// </summary>
        public (double Min, double Max)? GetTimeRange()
        {
            _lock.EnterReadLock();
            try
            {
                if (_timeIndex.Count == 0) return null;
                return (_timeIndex.Min, _timeIndex.Max);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 저장된 프레임 개수 조회
        /// </summary>
        public int GetFrameCount()
        {
            return _frames.Count;
        }

        /// <summary>
        /// 새로운 세션을 시작합니다. 
        /// 외부(Context)에서 생성한 Session ID를 받아 상태를 초기화합니다.
        /// </summary>
        public void StartNewSession(Guid sessionId)
        {
            _lock.EnterWriteLock();
            try
            {
                _frames.Clear();
                _timeIndex.Clear();
                _currentSessionId = sessionId; // 주입받은 ID 사용

                // 스키마 정보도 초기화 (이전 세션의 스키마 잔재 제거)
                Schema = null;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 메모리 관리: 오래된 프레임 제거 (WriteLock 내부에서만 호출)
        /// </summary>
        private void CleanupOldFrames()
        {
            if (_timeIndex.Count == 0) return;

            double latestTime = _timeIndex.Max;
            double cutoffTime = latestTime - _maxWindowSize;

            var toRemove = _timeIndex.Where(t => t < cutoffTime).ToList();
            foreach (var time in toRemove)
            {
                _frames.TryRemove(time, out _);
                _timeIndex.Remove(time);
            }
        }
    }
}
