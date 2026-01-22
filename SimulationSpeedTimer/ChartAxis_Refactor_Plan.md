# Refactoring Plan: ChartAxisDataProvider Cleanup

## Goal
Simplify the `ProcessFrame` logic in `ChartAxisDataProvider.cs` by reducing Code Duplication and utilizing the newly added helper properties in `DatabaseQueryConfig` (`IsXAxisTime`, `Is3DMode`).

## Proposed Changes

### 1. `ChartAxisDataProvider.cs`

*   **Helper Method Extraction**: Create a generic helper method `GetEffectiveValue` that encapsulates the "Fetch -> Check -> Fallback to NaN if Time" logic.
    ```csharp
    private double GetEffectiveValue(SimulationFrame frame, SeriesItem series, bool isTimeAxis);
    ```
*   **Loop Simplification**:
    *   Use `config.IsXAxisTime` and `config.Is3DMode`.
    *   Replace redundant `if-else` blocks with calls to `GetEffectiveValue`.
    *   Unified logic flow:
        1. Resolve X.
        2. Resolve Y using Helper.
        3. Resolve Z using Helper (only if `Is3DMode`).
        4. Invoke Event.

#### [MODIFY] [ChartAxisDataProvider.cs](file:///c:/Users/minph/OneDrive/%EB%B0%94%ED%83%95%20%ED%99%94%EB%A9%B4/%EC%83%88%20%ED%8F%B4%EB%8D%94/as/SimulationSpeedTimer/ChartAxisDataProvider.cs)
- Refactor `ProcessFrame` to be cleaner and more readable.
- Add `GetEffectiveValue` private method.

## Verification Plan

### Automated Verification
1.  **Compile Check**: Verify no syntax errors.
2.  **Logic Logic**: The behavior should remain identical to the previous step (passing `NaN` when X is Time).

### Manual Verification
- Review the code diff to ensure readability improvements (e.g., reduced nesting, clearer intent).
