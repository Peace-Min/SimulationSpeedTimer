using System;
using System.Threading.Tasks;

namespace SimulationSpeedTimer.Examples
{
    /// <summary>
    /// Current session-safe usage example for the SQLite polling service.
    /// This file is an example reference and is not compiled into the main project.
    /// </summary>
    public sealed class SimulationDbReadServiceUsageExample
    {
        private bool _canStart = true;

        public async Task StartSimulationAsync(string dbPath)
        {
            if (!_canStart)
            {
                return;
            }

            _canStart = false;

            var config = new GlobalDataService.GlobalDataServiceConfig
            {
                DbPath = dbPath,
                QueryInterval = 0.1,
                RequiredSchema = BuildRequiredSchema()
            };

            // 1. Always start through SimulationContext.
            //    This guarantees:
            //    - previous session is fully stopped first
            //    - repository is reset for the new session
            //    - OnSessionStarted is raised only after the new worker is ready
            //    - only the selected leaf columns are validated/read
            await SimulationContext.Instance.StartAsync(config);
        }

        public void OnSimulationTimeAdvanced(double simulationTime)
        {
            // 2. Feed simulation progress while the session is running.
            //    EnqueueTime is ignored automatically when the session is not Running.
            GlobalDataService.Instance.EnqueueTime(simulationTime);
        }

        public void OnSimulationCompletedNaturally()
        {
            // 3-A. Natural completion:
            //     request a graceful final sweep and flip the UI back to "start" when done.
            GlobalDataService.Instance.CompleteSession(OnGracefulCompletionUiReady);
        }

        public async Task OnUserPressedStopAsync()
        {
            // 3-B. Forced stop:
            //     await StopAsync so that old worker cleanup is fully complete before another start.
            await SimulationContext.Instance.StopAsync();

            // If your UI does not already listen to OnSessionStopped, you can re-enable Start here.
            _canStart = true;
        }

        private void OnGracefulCompletionUiReady()
        {
            // Important:
            // - this callback now runs after worker cleanup and completion signaling
            // - if this touches WPF controls directly, marshal to Dispatcher here
            _canStart = true;
        }

        private static SimulationSchema BuildRequiredSchema()
        {
            var schema = new SimulationSchema();

            var radarTable = new SchemaTableInfo("Object_Table_0", "ourDetectRadar");
            radarTable.AddColumn(new SchemaColumnInfo("COL1", "distance", "DOUBLE_TYPE"));
            radarTable.AddColumn(new SchemaColumnInfo("COL7", "azimuth", "DOUBLE_TYPE"));
            radarTable.AddColumn(new SchemaColumnInfo("COL9", "elevation", "DOUBLE_TYPE"));
            schema.AddTable(radarTable);

            var missileTable = new SchemaTableInfo("Object_Table_1", "ourMissile");
            missileTable.AddColumn(new SchemaColumnInfo("COL3", "speed", "DOUBLE_TYPE"));
            missileTable.AddColumn(new SchemaColumnInfo("COL4", "altitude", "DOUBLE_TYPE"));
            schema.AddTable(missileTable);

            return schema;
        }

        public async Task RestartAfterUserSelectionAsync(string nextDbPath)
        {
            // 4. Restart pattern:
            //    do not call GlobalDataService.Start directly.
            //    StartAsync already performs "stop previous -> start next" safely.
            await StartSimulationAsync(nextDbPath);
        }
    }
}
