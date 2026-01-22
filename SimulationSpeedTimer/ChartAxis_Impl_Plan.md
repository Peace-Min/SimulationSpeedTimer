# Implementation Plan - ChartAxisDataProvider Remodel

## Goal
Refactor `ChartAxisDataProvider` to support the simplified `DatabaseQueryConfig` (Single X, Y, Z columns) and implement logic that dynamically handles 2D vs 3D based on the presence of the Z column.

## User Review Required
> [!IMPORTANT]
> **Breaking Change**: The `Action<double, double, Dictionary<string, double>>` event currently used for 2D charts will be **REMOVED** or **MODIFIED** because the data structure has fundamentally changed from "One X, Many Ys" to "One X, One Y, (Opt) One Z". 
> The plan is to introduce a **Unified Event** that covers both, or keep two separate ones if we want to distinguish listeners. Given the "Simplify" instruction, I propose a single event with nullable Z.

## Proposed Changes

### `ChartAxisDataProvider.cs`

1.  **Remove Legacy Event**: Remove `Action<double, double, Dictionary<string, double>>` which implied multi-series.
2.  **Add New Event**:
    ```csharp
    // Time, X, Y, Z (Nullable)
    public Action<double, double, double, double?> OnDataUpdated;
    ```
    *   If Z is null -> 2D Consumer uses (Time, X, Y).
    *   If Z is valid -> 3D Consumer uses (Time, X, Y, Z).
3.  **Update `ProcessFrame` Logic**:
    *   Single logic path.
    *   Fetch X, Y.
    *   Fetch Z (if configured).
    *   Invoke `OnDataUpdated(time, x, y, z)`.

#### [MODIFY] [ChartAxisDataProvider.cs](file:///c:/Users/minph/OneDrive/%EB%B0%94%ED%83%95%20%ED%99%94%EB%A9%B4/%EC%83%88%20%ED%8F%B4%EB%8D%94/as/SimulationSpeedTimer/ChartAxisDataProvider.cs)
- Replace loop over `YAxisSeries` with simple single-fetch for `YColumn`.
- Add conditional fetch for `ZColumn`.
- Emit new event signature.

## Verification Plan

### Automated Tests
1.  **Compile Check**: Ensure no build errors after removing list-based properties.
2.  **Logic Logic**:
    *   Test Case A (2D): Configure X, Y. Run `ProcessFrame`. Verify event received with Z=null.
    *   Test Case B (3D): Configure X, Y, Z. Run `ProcessFrame`. Verify event received with Z=value.

### Manual Verification
- Since this is a library code change, valid compilation and unit test (if applicable) are the main verification steps.
- The user can hook this provider to their UI and verify the graph updates.
