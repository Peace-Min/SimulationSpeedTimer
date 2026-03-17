using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SimulationSpeedTimer
{
    internal static class SessionIsolationValidationProgram
    {
        private const string KnownIssueRapidStorm = "latest-intent-wins not implemented";

        [STAThread]
        private static async Task<int> Main()
        {
            var results = new List<TestResult>();

            results.Add(await RunTestAsync(ValidationCategory.Gating, "Repository event uses producer session id", TestRepositoryEventUsesProducerSessionIdAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Serial transition blocks ghosting into next session", TestSerialTransitionPreventsGhostingAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "StopAsync returns after worker completion", TestStopAsyncHasNoLateEventsAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Schema switches cleanly per session", TestSchemaIsolationAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "RequiredSchema is mandatory", TestRequiredSchemaIsMandatoryAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Missing required column blocks schema ready", TestMissingRequiredColumnBlocksSchemaReadyAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Projection query exposes only required leaf columns", TestProjectionQuerySubsetAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "0.01 query interval preserves 10ms progression", TestTenMillisecondQueryIntervalPrecisionAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Delta event payload remains immutable after later merge", TestDeltaEventPayloadIsImmutableAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Natural completion enables UI start via callback exactly once", TestNaturalCompletionEnablesUiStartAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Forced stop suppresses callback and re-enables UI start via stop controller", TestForcedStopSuppressesCallbackAndReenablesUiStartAsync));
            results.Add(await RunTestAsync(ValidationCategory.Gating, "Restart after natural completion succeeds", TestRestartAfterNaturalCompletionAsync));

            results.Add(await RunTestAsync(ValidationCategory.Observational, "Natural completion callback observed while SimulationContext state is ...", ObserveNaturalCompletionStateAsync));
            results.Add(await RunTestAsync(ValidationCategory.Observational, "Rapid command storm keeps only final start", TestRapidCommandStormAsync));
            results.Add(await RunTestAsync(ValidationCategory.Observational, "TableDataViewModel drops old-session events after session switch", TestTableDataViewModelSessionSwitchAsync));
            results.Add(await RunTestAsync(ValidationCategory.Observational, "ChartAxisDataProvider drops mismatched-session events", TestChartAxisDataProviderSessionFilteringAsync));

            PrintSummary(results);

            return results
                .Where(result => result.Category == ValidationCategory.Gating)
                .All(result => result.Passed) ? 0 : 1;
        }

        private static async Task<TestResult> RunTestAsync(
            ValidationCategory category,
            string name,
            Func<Task<string>> test)
        {
            Console.WriteLine();
            Console.WriteLine($"[RUN][{category}] {name}");

            try
            {
                await ResetRuntimeAsync().ConfigureAwait(false);
                var message = await test().ConfigureAwait(false);
                Console.WriteLine($"[PASS][{category}] {name}");
                return new TestResult(category, name, true, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL][{category}] {name}: {ex.Message}");
                return new TestResult(category, name, false, ex.ToString());
            }
            finally
            {
                await ResetRuntimeAsync().ConfigureAwait(false);
            }
        }

        private static void PrintSummary(List<TestResult> results)
        {
            Console.WriteLine();
            Console.WriteLine("=== Gating Summary ===");
            foreach (var result in results.Where(r => r.Category == ValidationCategory.Gating))
            {
                Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} | {result.Name}");
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    Console.WriteLine($"  {result.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Observational Summary ===");
            foreach (var result in results.Where(r => r.Category == ValidationCategory.Observational))
            {
                Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} | {result.Name}");
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    Console.WriteLine($"  {result.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Known Issues ===");
            var rapidStorm = results.FirstOrDefault(result => result.Name == "Rapid command storm keeps only final start");
            if (rapidStorm.Name != null && !rapidStorm.Passed)
            {
                Console.WriteLine($"- {KnownIssueRapidStorm}");
            }
            else
            {
                Console.WriteLine("- none");
            }
        }

        private static async Task<string> TestRepositoryEventUsesProducerSessionIdAsync()
        {
            var repository = SharedFrameRepository.Instance;
            var producerSessionId = Guid.NewGuid();
            var alternateSessionId = Guid.NewGuid();
            var mismatches = 0;
            var acceptedEvents = 0;

            repository.StartNewSession(producerSessionId);
            repository.OnFramesAdded += HandleFramesAdded;

            using (var cts = new CancellationTokenSource())
            {
                var toggler = Task.Run(() =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        repository.StartNewSession(alternateSessionId);
                        Thread.Yield();
                        repository.StartNewSession(producerSessionId);
                    }
                }, cts.Token);

                for (var i = 0; i < 5000; i++)
                {
                    repository.StartNewSession(producerSessionId);
                    repository.StoreChunk(new Dictionary<double, SimulationFrame> { [i] = new SimulationFrame(i) }, producerSessionId);
                }

                cts.Cancel();
                try
                {
                    await toggler.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            repository.OnFramesAdded -= HandleFramesAdded;

            if (acceptedEvents == 0)
            {
                throw new InvalidOperationException("No repository events were observed.");
            }

            if (mismatches > 0)
            {
                throw new InvalidOperationException($"Observed {mismatches} mismatched event session IDs.");
            }

            return $"Observed {acceptedEvents} repository events without a session-id mismatch.";

            void HandleFramesAdded(List<SimulationFrame> frames, Guid sessionId)
            {
                acceptedEvents++;
                if (sessionId != producerSessionId)
                {
                    mismatches++;
                }
            }
        }

        private static async Task<string> TestSerialTransitionPreventsGhostingAsync()
        {
            var dbA = CreateDb(
                "serial_a",
                "TableA",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(50.0, 59.9, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));
            var dbB = CreateDb(
                "serial_b",
                "TableB",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(100.0, 109.9, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));
            var events = new List<FrameEvent>();

            try
            {
                SharedFrameRepository.Instance.OnFramesAdded += CaptureFrames;

                await SimulationContext.Instance.StartAsync(CreateConfig(dbA, "TableA", "TableA", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                var sessionA = SimulationContext.Instance.CurrentSessionId;

                for (var i = 0; i < 10; i++)
                {
                    GlobalDataService.Instance.EnqueueTime(Math.Round(50.0 + (i * 0.1), 1));
                }

                await WaitForAsync(() => SharedFrameRepository.Instance.GetFrameCount() >= 5, 3000, "session A initial frames")
                    .ConfigureAwait(false);

                await SimulationContext.Instance.StartAsync(CreateConfig(dbB, "TableB", "TableB", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                var sessionB = SimulationContext.Instance.CurrentSessionId;

                if (sessionA == sessionB)
                {
                    throw new InvalidOperationException("Session ID did not rotate.");
                }

                for (var i = 0; i < 10; i++)
                {
                    GlobalDataService.Instance.EnqueueTime(Math.Round(100.0 + (i * 0.1), 1));
                }

                await WaitForAsync(() =>
                {
                    var range = SharedFrameRepository.Instance.GetTimeRange();
                    return range.HasValue && range.Value.Min >= 100.0 && SharedFrameRepository.Instance.GetFrameCount() >= 5;
                }, 4000, "session B frames").ConfigureAwait(false);

                var allFrames = SharedFrameRepository.Instance.GetLatestFrames(50);
                if (allFrames.Any(frame => frame.Time < 100.0))
                {
                    throw new InvalidOperationException("Found old-session frames after session B started.");
                }

                if (events.Any(evt => evt.SessionId == sessionB && evt.MaxTime >= 50.0 && evt.MinTime < 100.0))
                {
                    throw new InvalidOperationException("Observed session B event payload containing session A frame times.");
                }

                return $"Observed {events.Count} frame events without cross-session contamination.";
            }
            finally
            {
                SharedFrameRepository.Instance.OnFramesAdded -= CaptureFrames;
                CleanupDb(dbA);
                CleanupDb(dbB);
            }

            void CaptureFrames(List<SimulationFrame> frames, Guid sessionId)
            {
                if (frames == null || frames.Count == 0) return;
                events.Add(new FrameEvent(sessionId, frames.Min(frame => frame.Time), frames.Max(frame => frame.Time)));
            }
        }

        private static async Task<string> TestStopAsyncHasNoLateEventsAsync()
        {
            var db = CreateDb(
                "stop_completion",
                "StopTable",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 20.0, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));
            var eventCount = 0;

            try
            {
                SharedFrameRepository.Instance.OnFramesAdded += CountFrames;

                await SimulationContext.Instance.StartAsync(CreateConfig(db, "StopTable", "StopTable", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);

                for (var i = 0; i < 15; i++)
                {
                    GlobalDataService.Instance.EnqueueTime(Math.Round(i * 0.1, 1));
                }

                await WaitForAsync(() => eventCount > 0, 3000, "pre-stop events").ConfigureAwait(false);

                await SimulationContext.Instance.StopAsync().ConfigureAwait(false);

                var observedAfterStop = eventCount;
                GlobalDataService.Instance.EnqueueTime(999.0);
                await Task.Delay(500).ConfigureAwait(false);

                if (eventCount != observedAfterStop)
                {
                    throw new InvalidOperationException("Events continued after StopAsync returned.");
                }

                if (SimulationContext.Instance.CurrentState != SimulationLifecycleState.Idle)
                {
                    throw new InvalidOperationException($"Expected Idle after stop, got {SimulationContext.Instance.CurrentState}.");
                }

                if (SimulationContext.Instance.CurrentSessionId != Guid.Empty)
                {
                    throw new InvalidOperationException("Session ID was not cleared on stop.");
                }

                if (GlobalDataService.Instance.HasActiveSession)
                {
                    throw new InvalidOperationException("GlobalDataService still reports an active session after stop.");
                }

                return $"Observed {eventCount} events before stop and none afterwards.";
            }
            finally
            {
                SharedFrameRepository.Instance.OnFramesAdded -= CountFrames;
                CleanupDb(db);
            }

            void CountFrames(List<SimulationFrame> frames, Guid sessionId)
            {
                if (frames != null && frames.Count > 0)
                {
                    eventCount++;
                }
            }
        }

        private static async Task<string> TestSchemaIsolationAsync()
        {
            var dbA = CreateDb(
                "schema_a",
                "SchemaA",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 2.0, 0.1, value => new Dictionary<string, object> { ["Value"] = value }));
            var dbB = CreateDb(
                "schema_b",
                "SchemaB",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("OtherValue", "DOUBLE") },
                BuildTimeSeriesRows(10.0, 12.0, 0.1, value => new Dictionary<string, object> { ["OtherValue"] = value }));

            try
            {
                await SimulationContext.Instance.StartAsync(CreateConfig(dbA, "SchemaA", "SchemaA", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);

                GlobalDataService.Instance.EnqueueTime(0.5);
                await WaitForAsync(() => SharedFrameRepository.Instance.Schema != null, 3000, "schema A load")
                    .ConfigureAwait(false);

                if (SharedFrameRepository.Instance.Schema.GetTable("SchemaA") == null)
                {
                    throw new InvalidOperationException("Schema A was not applied.");
                }

                await SimulationContext.Instance.StartAsync(CreateConfig(dbB, "SchemaB", "SchemaB", 0.1,
                    new SchemaColumnInfo("OtherValue", "OtherValue", "DOUBLE"))).ConfigureAwait(false);

                GlobalDataService.Instance.EnqueueTime(10.5);
                await WaitForAsync(() => SharedFrameRepository.Instance.Schema != null &&
                                         SharedFrameRepository.Instance.Schema.GetTable("SchemaB") != null,
                    3000,
                    "schema B load").ConfigureAwait(false);

                if (SharedFrameRepository.Instance.Schema.GetTable("SchemaA") != null)
                {
                    throw new InvalidOperationException("Old schema leaked into the new session.");
                }

                return "Schema switched cleanly from SchemaA to SchemaB.";
            }
            finally
            {
                CleanupDb(dbA);
                CleanupDb(dbB);
            }
        }

        private static async Task<string> TestRequiredSchemaIsMandatoryAsync()
        {
            var invalidConfigs = new[]
            {
                new GlobalDataService.GlobalDataServiceConfig { DbPath = "missing_required_schema.db", QueryInterval = 0.1, RequiredSchema = null },
                new GlobalDataService.GlobalDataServiceConfig { DbPath = "empty_required_schema.db", QueryInterval = 0.1, RequiredSchema = new SimulationSchema() }
            };
            var exceptions = 0;

            foreach (var config in invalidConfigs)
            {
                try
                {
                    await SimulationContext.Instance.StartAsync(config).ConfigureAwait(false);
                    throw new InvalidOperationException("StartAsync should have failed for an invalid RequiredSchema.");
                }
                catch (InvalidOperationException)
                {
                    exceptions++;
                }

                if (SimulationContext.Instance.CurrentState != SimulationLifecycleState.Idle)
                {
                    throw new InvalidOperationException("SimulationContext did not recover to Idle after a schema validation failure.");
                }

                if (SimulationContext.Instance.CurrentSessionId != Guid.Empty)
                {
                    throw new InvalidOperationException("CurrentSessionId was not cleared after a schema validation failure.");
                }

                if (GlobalDataService.Instance.HasActiveSession)
                {
                    throw new InvalidOperationException("GlobalDataService reported an active session after a schema validation failure.");
                }
            }

            return $"Rejected {exceptions} invalid RequiredSchema configurations.";
        }

        private static async Task<string> TestMissingRequiredColumnBlocksSchemaReadyAsync()
        {
            var missingLeafDb = CreateDb(
                "missing_leaf",
                "MissingLeaf",
                new[] { new DbColumnSpec("s_time", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 0.2, 0.1, value => new Dictionary<string, object>()));
            var missingTimeDb = CreateDb(
                "missing_time",
                "MissingTime",
                new[] { new DbColumnSpec("Value", "DOUBLE") },
                new[] { new Dictionary<string, object> { ["Value"] = 1.0 } });

            try
            {
                await SimulationContext.Instance.StartAsync(CreateConfig(missingLeafDb, "MissingLeaf", "MissingLeaf", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                GlobalDataService.Instance.EnqueueTime(0.1);
                await Task.Delay(700).ConfigureAwait(false);

                if (SharedFrameRepository.Instance.Schema != null)
                {
                    throw new InvalidOperationException("Schema became ready even though a required leaf column was missing.");
                }

                if (SharedFrameRepository.Instance.GetFrameCount() != 0)
                {
                    throw new InvalidOperationException("Frames were produced even though schema validation never completed.");
                }

                await SimulationContext.Instance.StopAsync().ConfigureAwait(false);

                await SimulationContext.Instance.StartAsync(CreateConfig(missingTimeDb, "MissingTime", "MissingTime", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                await Task.Delay(700).ConfigureAwait(false);

                if (SharedFrameRepository.Instance.Schema != null)
                {
                    throw new InvalidOperationException("Schema became ready even though s_time was missing.");
                }

                return "Worker stayed in schema-wait state for both missing-leaf and missing-s_time cases.";
            }
            finally
            {
                CleanupDb(missingLeafDb);
                CleanupDb(missingTimeDb);
            }
        }

        private static async Task<string> TestProjectionQuerySubsetAsync()
        {
            var db = CreateDb(
                "projection_subset",
                "ProjectionTable",
                new[]
                {
                    new DbColumnSpec("s_time", "DOUBLE"),
                    new DbColumnSpec("Leaf1", "DOUBLE"),
                    new DbColumnSpec("Leaf2", "DOUBLE"),
                    new DbColumnSpec("ExtraA", "DOUBLE"),
                    new DbColumnSpec("ExtraB", "DOUBLE")
                },
                new[]
                {
                    new Dictionary<string, object>
                    {
                        ["s_time"] = 0.1,
                        ["Leaf1"] = 10.0,
                        ["Leaf2"] = 20.0,
                        ["ExtraA"] = 30.0,
                        ["ExtraB"] = 40.0
                    }
                });

            try
            {
                await SimulationContext.Instance.StartAsync(CreateConfig(db, "ProjectionTable", "ProjectionObject", 0.1,
                    new SchemaColumnInfo("Leaf1", "Distance", "DOUBLE"))).ConfigureAwait(false);

                GlobalDataService.Instance.EnqueueTime(0.1);
                await WaitForAsync(() =>
                {
                    var frame = SharedFrameRepository.Instance.GetLatestFrames(1).FirstOrDefault();
                    return frame != null && frame.GetTable("ProjectionObject") != null;
                }, 3000, "projection frame").ConfigureAwait(false);

                var projectionFrame = SharedFrameRepository.Instance.GetLatestFrames(1).Single();
                var table = projectionFrame.GetTable("ProjectionObject");
                if (table == null)
                {
                    throw new InvalidOperationException("Projection table was not materialized.");
                }

                var columns = table.ColumnNames.OrderBy(name => name).ToList();
                if (columns.Count != 1 || columns[0] != "Distance")
                {
                    throw new InvalidOperationException($"Expected only Distance in the projected frame, but found: {string.Join(", ", columns)}");
                }

                return $"Projected columns: {string.Join(", ", columns)}";
            }
            finally
            {
                CleanupDb(db);
            }
        }

        private static async Task<string> TestTenMillisecondQueryIntervalPrecisionAsync()
        {
            var db = CreateDb(
                "ten_ms_precision",
                "PrecisionTable",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                new[]
                {
                    new Dictionary<string, object> { ["s_time"] = 0.01, ["Value"] = 1.0 },
                    new Dictionary<string, object> { ["s_time"] = 0.02, ["Value"] = 2.0 },
                    new Dictionary<string, object> { ["s_time"] = 0.03, ["Value"] = 3.0 },
                    new Dictionary<string, object> { ["s_time"] = 0.04, ["Value"] = 4.0 },
                    new Dictionary<string, object> { ["s_time"] = 0.05, ["Value"] = 5.0 }
                });

            try
            {
                await SimulationContext.Instance.StartAsync(CreateConfig(db, "PrecisionTable", "PrecisionTable", 0.01,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);

                foreach (var time in new[] { 0.01, 0.02, 0.03, 0.04, 0.05 })
                {
                    GlobalDataService.Instance.EnqueueTime(time);
                }

                await WaitForAsync(() => SharedFrameRepository.Instance.GetFrameCount() >= 5, 3000, "10ms frames")
                    .ConfigureAwait(false);

                var times = SharedFrameRepository.Instance
                    .GetLatestFrames(10)
                    .Select(frame => Math.Round(frame.Time, 2))
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();

                var expected = new[] { 0.01, 0.02, 0.03, 0.04, 0.05 };
                if (!expected.All(value => times.Contains(value)))
                {
                    throw new InvalidOperationException($"Expected 10ms frame times were not all observed. Actual: {string.Join(", ", times)}");
                }

                return $"Observed frame times: {string.Join(", ", times)}";
            }
            finally
            {
                CleanupDb(db);
            }
        }

        private static Task<string> TestDeltaEventPayloadIsImmutableAsync()
        {
            var repository = SharedFrameRepository.Instance;
            var sessionId = Guid.NewGuid();
            var payloads = new List<List<SimulationFrame>>();

            repository.StartNewSession(sessionId);
            repository.OnFramesAdded += CaptureFrames;

            try
            {
                var firstFrame = new SimulationFrame(1.0);
                var firstTable = new SimulationTable("ObjectA");
                firstTable.AddColumn("Distance", 10.0);
                firstFrame.AddOrUpdateTable(firstTable);
                repository.StoreChunk(new Dictionary<double, SimulationFrame> { [1.0] = firstFrame }, sessionId);

                var secondFrame = new SimulationFrame(1.0);
                var secondTable = new SimulationTable("ObjectB");
                secondTable.AddColumn("Speed", 20.0);
                secondFrame.AddOrUpdateTable(secondTable);
                repository.StoreChunk(new Dictionary<double, SimulationFrame> { [1.0] = secondFrame }, sessionId);

                if (payloads.Count != 2)
                {
                    throw new InvalidOperationException($"Expected 2 delta payloads, observed {payloads.Count}.");
                }

                var firstPayloadFrame = payloads[0].Single();
                var secondPayloadFrame = payloads[1].Single();
                var mergedFrame = repository.GetFrame(1.0);

                if (firstPayloadFrame.GetTable("ObjectA") == null || firstPayloadFrame.GetTable("ObjectB") != null)
                {
                    throw new InvalidOperationException("First payload was mutated after the later merge.");
                }

                if (secondPayloadFrame.GetTable("ObjectB") == null || secondPayloadFrame.GetTable("ObjectA") != null)
                {
                    throw new InvalidOperationException("Second payload does not represent a pure delta.");
                }

                if (mergedFrame.GetTable("ObjectA") == null || mergedFrame.GetTable("ObjectB") == null)
                {
                    throw new InvalidOperationException("Repository snapshot did not merge both tables.");
                }

                return Task.FromResult("First event stayed A-only, second event stayed B-only, repository snapshot merged A+B.");
            }
            finally
            {
                repository.OnFramesAdded -= CaptureFrames;
            }

            void CaptureFrames(List<SimulationFrame> frames, Guid observedSessionId)
            {
                if (observedSessionId == sessionId)
                {
                    payloads.Add(frames);
                }
            }
        }

        private static async Task<string> TestNaturalCompletionEnablesUiStartAsync()
        {
            var db = CreateDb(
                "natural_completion",
                "NaturalTable",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 0.4, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));

            try
            {
                var observation = await ExecuteNaturalCompletionAsync(db, "NaturalTable").ConfigureAwait(false);

                if (!observation.Probe.CanStart)
                {
                    throw new InvalidOperationException("UI start was not re-enabled by the completion callback.");
                }

                if (observation.Probe.EnableByCompletionCallbackCount != 1)
                {
                    throw new InvalidOperationException($"Expected exactly one completion callback, observed {observation.Probe.EnableByCompletionCallbackCount}.");
                }

                if (observation.Probe.EnableByForcedStopControllerCount != 0)
                {
                    throw new InvalidOperationException("Forced-stop controller should not have re-enabled start during natural completion.");
                }

                return $"Completion callback re-enabled start once. State at callback: {observation.StateAtCallback}.";
            }
            finally
            {
                CleanupDb(db);
            }
        }

        private static async Task<string> TestForcedStopSuppressesCallbackAndReenablesUiStartAsync()
        {
            var db = CreateDb(
                "forced_stop",
                "ForcedStopTable",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 1.0, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));

            try
            {
                var probe = new UiStartStateProbe();
                var naturalController = new NaturalCompletionControllerDouble(probe);
                var forcedStopController = new ForcedStopControllerDouble(probe);

                await SimulationContext.Instance.StartAsync(CreateConfig(db, "ForcedStopTable", "ForcedStopTable", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                naturalController.OnSimulationStarted();

                foreach (var time in new[] { 0.1, 0.2, 0.3 })
                {
                    GlobalDataService.Instance.EnqueueTime(time);
                }

                naturalController.RequestGracefulCompletion();
                await forcedStopController.StopAsync().ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);

                if (probe.EnableByCompletionCallbackCount != 0)
                {
                    throw new InvalidOperationException("Completion callback fired during forced stop.");
                }

                if (probe.EnableByForcedStopControllerCount != 1)
                {
                    throw new InvalidOperationException($"Expected forced-stop controller to enable start once, observed {probe.EnableByForcedStopControllerCount}.");
                }

                if (!probe.CanStart)
                {
                    throw new InvalidOperationException("UI start was not re-enabled after forced stop.");
                }

                if (SimulationContext.Instance.CurrentSessionId != Guid.Empty)
                {
                    throw new InvalidOperationException("CurrentSessionId was not cleared after forced stop.");
                }

                if (GlobalDataService.Instance.HasActiveSession)
                {
                    throw new InvalidOperationException("GlobalDataService still reported an active session after forced stop.");
                }

                return "Completion callback stayed suppressed and the forced-stop controller re-enabled UI start.";
            }
            finally
            {
                CleanupDb(db);
            }
        }

        private static async Task<string> TestRestartAfterNaturalCompletionAsync()
        {
            var dbA = CreateDb(
                "restart_after_natural_a",
                "RestartA",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 0.4, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));
            var dbB = CreateDb(
                "restart_after_natural_b",
                "RestartB",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(100.0, 100.4, 0.1, value => new Dictionary<string, object> { ["Value"] = value * 10 }));

            try
            {
                var observation = await ExecuteNaturalCompletionAsync(dbA, "RestartA").ConfigureAwait(false);

                var oldSessionId = observation.SessionId;
                await SimulationContext.Instance.StartAsync(CreateConfig(dbB, "RestartB", "RestartB", 0.1,
                    new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
                var newSessionId = SimulationContext.Instance.CurrentSessionId;

                if (oldSessionId == newSessionId)
                {
                    throw new InvalidOperationException("Restart after natural completion did not rotate the session id.");
                }

                foreach (var time in new[] { 100.1, 100.2, 100.3 })
                {
                    GlobalDataService.Instance.EnqueueTime(time);
                }

                await WaitForAsync(() =>
                {
                    var range = SharedFrameRepository.Instance.GetTimeRange();
                    return range.HasValue && range.Value.Min >= 100.0;
                }, 3000, "restart B frames").ConfigureAwait(false);

                return $"Restart succeeded. Session rotated from {oldSessionId} to {newSessionId}.";
            }
            finally
            {
                CleanupDb(dbA);
                CleanupDb(dbB);
            }
        }

        private static async Task<string> ObserveNaturalCompletionStateAsync()
        {
            var db = CreateDb(
                "natural_completion_observation",
                "ObservedNaturalTable",
                new[] { new DbColumnSpec("s_time", "DOUBLE"), new DbColumnSpec("Value", "DOUBLE") },
                BuildTimeSeriesRows(0.0, 0.4, 0.1, value => new Dictionary<string, object> { ["Value"] = value }));

            try
            {
                var observation = await ExecuteNaturalCompletionAsync(db, "ObservedNaturalTable").ConfigureAwait(false);
                return $"SimulationContext.CurrentState at callback: {observation.StateAtCallback}";
            }
            finally
            {
                CleanupDb(db);
            }
        }

        private static async Task<string> TestRapidCommandStormAsync()
        {
            var dbA = CreateDb("storm_a", "StormA", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(0.0, 1.0, 0.1, value => new Dictionary<string, object>()));
            var dbB = CreateDb("storm_b", "StormB", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(10.0, 11.0, 0.1, value => new Dictionary<string, object>()));
            var dbC = CreateDb("storm_c", "StormC", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(20.0, 21.0, 0.1, value => new Dictionary<string, object>()));
            var startedSessions = new List<Guid>();

            try
            {
                SimulationContext.Instance.OnSessionStarted += RememberSessionStart;

                await SimulationContext.Instance.StartAsync(CreateConfig(dbA, "StormA", "StormA", 0.1)).ConfigureAwait(false);

                startedSessions.Clear();

                var startB = Task.Run(() => SimulationContext.Instance.StartAsync(CreateConfig(dbB, "StormB", "StormB", 0.1)));
                await Task.Delay(10).ConfigureAwait(false);
                var stop = Task.Run(() => SimulationContext.Instance.StopAsync());
                await Task.Delay(10).ConfigureAwait(false);
                var startC = Task.Run(() => SimulationContext.Instance.StartAsync(CreateConfig(dbC, "StormC", "StormC", 0.1)));

                await Task.WhenAll(startB, stop, startC).ConfigureAwait(false);

                GlobalDataService.Instance.EnqueueTime(20.5);
                await WaitForAsync(() => SharedFrameRepository.Instance.Schema != null &&
                                         SharedFrameRepository.Instance.Schema.GetTable("StormC") != null,
                    3000,
                    "storm final schema").ConfigureAwait(false);

                if (startedSessions.Count != 1)
                {
                    throw new InvalidOperationException($"Expected only the final start to reach Running, but observed {startedSessions.Count} starts.");
                }

                return "Only the final start reached Running.";
            }
            finally
            {
                SimulationContext.Instance.OnSessionStarted -= RememberSessionStart;
                CleanupDb(dbA);
                CleanupDb(dbB);
                CleanupDb(dbC);
            }

            void RememberSessionStart(Guid sessionId)
            {
                startedSessions.Add(sessionId);
            }
        }

        private static async Task<string> TestTableDataViewModelSessionSwitchAsync()
        {
            var dbA = CreateDb("table_vm_a", "ObsTable", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(0.0, 0.0, 0.1, value => new Dictionary<string, object>()));
            var dbB = CreateDb("table_vm_b", "ObsTable", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(100.0, 100.0, 0.1, value => new Dictionary<string, object>()));

            try
            {
                var viewModel = new TableDataViewModel();
                viewModel.InitializeTableConfig(new List<TableConfig>
                {
                    new TableConfig
                    {
                        TableName = "ObsTable",
                        Columns = new List<ColumnConfig>
                        {
                            new ColumnConfig { FieldName = "Value", Header = "Value" }
                        }
                    }
                });

                await SimulationContext.Instance.StartAsync(CreateConfig(dbA, "ObsTable", "ObsTable", 0.1)).ConfigureAwait(false);
                var sessionA = SimulationContext.Instance.CurrentSessionId;

                SharedFrameRepository.Instance.StoreChunk(CreateManualChunk(1.0, "ObsTable", "Value", 10.0), sessionA);
                FlushTableViewModel(viewModel);

                var firstTimes = ExtractTimes(viewModel.Items);
                if (!firstTimes.Contains(1.0))
                {
                    throw new InvalidOperationException("Expected session A row was not flushed into the table view model.");
                }

                await SimulationContext.Instance.StartAsync(CreateConfig(dbB, "ObsTable", "ObsTable", 0.1)).ConfigureAwait(false);
                var sessionB = SimulationContext.Instance.CurrentSessionId;

                SharedFrameRepository.Instance.StoreChunk(CreateManualChunk(101.0, "ObsTable", "Value", 20.0), sessionB);
                FlushTableViewModel(viewModel);

                var times = ExtractTimes(viewModel.Items);
                if (times.Count != 1 || !times.Contains(101.0))
                {
                    throw new InvalidOperationException($"Expected only session B rows after switch, found: {string.Join(", ", times)}");
                }

                return $"Rows after session switch: {string.Join(", ", times)}";
            }
            finally
            {
                CleanupDb(dbA);
                CleanupDb(dbB);
            }
        }

        private static async Task<string> TestChartAxisDataProviderSessionFilteringAsync()
        {
            var db = CreateDb("chart_provider", "ObsChart", new[] { new DbColumnSpec("s_time", "DOUBLE") }, BuildTimeSeriesRows(0.0, 0.0, 0.1, value => new Dictionary<string, object>()));

            try
            {
                var provider = ChartAxisDataProvider.Instance;
                provider.ClearAllReceivers();

                var eventCount = 0;
                provider.OnDataUpdated = (receiverId, seriesIndex, time, x, y, z) => eventCount++;
                provider.RegisterReceiver("ReceiverA", new List<DatabaseQueryConfig>
                {
                    new DatabaseQueryConfig
                    {
                        XColumn = new SeriesItem { AttributeName = "Time" },
                        YColumn = new SeriesItem { ObjectName = "ObsChart", AttributeName = "Value" }
                    }
                });

                await SimulationContext.Instance.StartAsync(CreateConfig(db, "ObsChart", "ObsChart", 0.1)).ConfigureAwait(false);
                var currentSessionId = SimulationContext.Instance.CurrentSessionId;

                var frame = new SimulationFrame(1.0);
                var table = new SimulationTable("ObsChart");
                table.AddColumn("Value", 42.0);
                frame.AddOrUpdateTable(table);

                InvokePrivateMethod(provider, "HandleNewFrames", new List<SimulationFrame> { frame }, Guid.NewGuid());
                if (eventCount != 0)
                {
                    throw new InvalidOperationException("ChartAxisDataProvider dispatched a mismatched-session event.");
                }

                InvokePrivateMethod(provider, "HandleNewFrames", new List<SimulationFrame> { frame }, currentSessionId);
                if (eventCount != 1)
                {
                    throw new InvalidOperationException("ChartAxisDataProvider did not dispatch the matching-session event.");
                }

                return "Mismatched-session frame was dropped, matching-session frame was dispatched once.";
            }
            finally
            {
                ChartAxisDataProvider.Instance.ClearAllReceivers();
                ChartAxisDataProvider.Instance.OnDataUpdated = null;
                CleanupDb(db);
            }
        }

        private static async Task<NaturalCompletionObservation> ExecuteNaturalCompletionAsync(string dbPath, string tableName)
        {
            var probe = new UiStartStateProbe();
            var controller = new NaturalCompletionControllerDouble(probe);

            await SimulationContext.Instance.StartAsync(CreateConfig(dbPath, tableName, tableName, 0.1,
                new SchemaColumnInfo("Value", "Value", "DOUBLE"))).ConfigureAwait(false);
            var sessionId = SimulationContext.Instance.CurrentSessionId;
            controller.OnSimulationStarted();

            foreach (var time in new[] { 0.1, 0.2, 0.3, 0.4 })
            {
                GlobalDataService.Instance.EnqueueTime(time);
            }

            controller.RequestGracefulCompletion();
            await controller.WaitForCompletionAsync().ConfigureAwait(false);

            return new NaturalCompletionObservation(sessionId, probe, probe.StateObservedAtCompletion);
        }

        private static Dictionary<double, SimulationFrame> CreateManualChunk(double time, string tableName, string columnName, object value)
        {
            var frame = new SimulationFrame(time);
            var table = new SimulationTable(tableName);
            table.AddColumn(columnName, value);
            frame.AddOrUpdateTable(table);
            return new Dictionary<double, SimulationFrame> { [time] = frame };
        }

        private static List<double> ExtractTimes(ObservableCollection<ExpandoObject> items)
        {
            if (items == null)
            {
                return new List<double>();
            }

            return items
                .Select(item => (IDictionary<string, object>)item)
                .Where(dict => dict.ContainsKey("Time"))
                .Select(dict => Convert.ToDouble(dict["Time"]))
                .OrderBy(value => value)
                .ToList();
        }

        private static void FlushTableViewModel(TableDataViewModel viewModel)
        {
            InvokePrivateMethod(viewModel, "OnUIRefreshTimerTick", null, EventArgs.Empty);
        }

        private static object InvokePrivateMethod(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(target.GetType().FullName, methodName);
            }

            return method.Invoke(target, args);
        }

        private static string CreateDb(string prefix, string tableName, IEnumerable<DbColumnSpec> columns, IEnumerable<IDictionary<string, object>> rows)
        {
            var path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.db");
            SQLiteConnection.CreateFile(path);

            using (var conn = new SQLiteConnection($"Data Source={path};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    var columnSql = string.Join(", ", columns.Select(column => $"{QuoteIdentifier(column.Name)} {column.SqlType}"));
                    cmd.CommandText = $"CREATE TABLE {QuoteIdentifier(tableName)} ({columnSql});";
                    cmd.ExecuteNonQuery();
                }

                foreach (var row in rows)
                {
                    using (var insert = conn.CreateCommand())
                    {
                        var columnNames = row.Keys.Select(QuoteIdentifier).ToList();
                        var parameterNames = row.Keys.Select((key, parameterIndex) => "@p" + parameterIndex).ToList();
                        insert.CommandText =
                            $"INSERT INTO {QuoteIdentifier(tableName)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", parameterNames)})";

                        var parameterCursor = 0;
                        foreach (var kvp in row)
                        {
                            insert.Parameters.AddWithValue(parameterNames[parameterCursor], kvp.Value);
                            parameterCursor++;
                        }

                        insert.ExecuteNonQuery();
                    }
                }
            }

            return path;
        }

        private static IEnumerable<IDictionary<string, object>> BuildTimeSeriesRows(
            double start,
            double end,
            double step,
            Func<double, IDictionary<string, object>> extraFactory)
        {
            for (var value = start; value <= end + 0.000001; value += step)
            {
                var roundedValue = Math.Round(value, GetPrecision(step));
                var row = new Dictionary<string, object> { ["s_time"] = roundedValue };

                foreach (var extra in extraFactory(roundedValue))
                {
                    row[extra.Key] = extra.Value;
                }

                yield return row;
            }
        }

        private static int GetPrecision(double step)
        {
            step = Math.Abs(step);
            for (var precision = 0; precision <= 6; precision++)
            {
                if (Math.Abs(step - Math.Round(step, precision)) < 0.000000001)
                {
                    return precision;
                }
            }

            return 6;
        }

        private static GlobalDataService.GlobalDataServiceConfig CreateConfig(
            string dbPath,
            string tableName,
            string objectName,
            double queryInterval,
            params SchemaColumnInfo[] requiredColumns)
        {
            var schema = new SimulationSchema();
            var table = new SchemaTableInfo(tableName, objectName);
            foreach (var requiredColumn in requiredColumns)
            {
                table.AddColumn(requiredColumn);
            }
            schema.AddTable(table);

            return new GlobalDataService.GlobalDataServiceConfig
            {
                DbPath = dbPath,
                QueryInterval = queryInterval,
                RequiredSchema = schema
            };
        }

        private static string QuoteIdentifier(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        private static void CleanupDb(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
        }

        private static async Task ResetRuntimeAsync()
        {
            try
            {
                await SimulationContext.Instance.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            SharedFrameRepository.Instance.StartNewSession(Guid.NewGuid());
            SharedFrameRepository.Instance.ClearSessionSchema();
            SQLiteConnection.ClearAllPools();
        }

        private static async Task WaitForAsync(Func<bool> condition, int timeoutMs, string label)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out while waiting for {label}.");
        }

        private enum ValidationCategory
        {
            Gating,
            Observational
        }

        private struct TestResult
        {
            public TestResult(ValidationCategory category, string name, bool passed, string message)
            {
                Category = category;
                Name = name;
                Passed = passed;
                Message = message;
            }

            public ValidationCategory Category { get; }
            public string Name { get; }
            public bool Passed { get; }
            public string Message { get; }
        }

        private struct FrameEvent
        {
            public FrameEvent(Guid sessionId, double minTime, double maxTime)
            {
                SessionId = sessionId;
                MinTime = minTime;
                MaxTime = maxTime;
            }

            public Guid SessionId { get; }
            public double MinTime { get; }
            public double MaxTime { get; }
        }

        private struct DbColumnSpec
        {
            public DbColumnSpec(string name, string sqlType)
            {
                Name = name;
                SqlType = sqlType;
            }

            public string Name { get; }
            public string SqlType { get; }
        }

        private sealed class UiStartStateProbe
        {
            public bool CanStart { get; set; } = true;
            public int EnableByCompletionCallbackCount { get; set; }
            public int EnableByForcedStopControllerCount { get; set; }
            public SimulationLifecycleState StateObservedAtCompletion { get; set; }
        }

        private sealed class NaturalCompletionControllerDouble
        {
            private readonly UiStartStateProbe _probe;
            private readonly TaskCompletionSource<bool> _completionSource =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public NaturalCompletionControllerDouble(UiStartStateProbe probe)
            {
                _probe = probe;
            }

            public void OnSimulationStarted()
            {
                _probe.CanStart = false;
            }

            public void RequestGracefulCompletion()
            {
                GlobalDataService.Instance.CompleteSession(() =>
                {
                    _probe.EnableByCompletionCallbackCount++;
                    _probe.CanStart = true;
                    _probe.StateObservedAtCompletion = SimulationContext.Instance.CurrentState;
                    _completionSource.TrySetResult(true);
                });
            }

            public async Task WaitForCompletionAsync()
            {
                var completedTask = await Task.WhenAny(_completionSource.Task, Task.Delay(3000)).ConfigureAwait(false);
                if (completedTask != _completionSource.Task)
                {
                    throw new TimeoutException("Timed out while waiting for the natural completion callback.");
                }

                await _completionSource.Task.ConfigureAwait(false);
            }
        }

        private sealed class ForcedStopControllerDouble
        {
            private readonly UiStartStateProbe _probe;

            public ForcedStopControllerDouble(UiStartStateProbe probe)
            {
                _probe = probe;
            }

            public async Task StopAsync()
            {
                await SimulationContext.Instance.StopAsync().ConfigureAwait(false);
                _probe.EnableByForcedStopControllerCount++;
                _probe.CanStart = true;
            }
        }

        private sealed class NaturalCompletionObservation
        {
            public NaturalCompletionObservation(Guid sessionId, UiStartStateProbe probe, SimulationLifecycleState stateAtCallback)
            {
                SessionId = sessionId;
                Probe = probe;
                StateAtCallback = stateAtCallback;
            }

            public Guid SessionId { get; }
            public UiStartStateProbe Probe { get; }
            public SimulationLifecycleState StateAtCallback { get; }
        }
    }
}
