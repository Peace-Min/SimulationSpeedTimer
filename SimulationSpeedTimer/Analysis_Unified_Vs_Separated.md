# Analysis: Unified vs. Separated Buffer Architecture for 3D Support

## 1. Context
We need to extend `ChartAxisDataProvider` to support 3D trajectory data ($x, y, z$).
The current 2D logic uses:
- **Event**: `Action<double, double, Dictionary<string, double>>`
- **Semantics**: `(Time, Shared_X_Value, { SeriesName: Y_Value })`

The question is: **Should we unify 2D and 3D into a single DTO/Event model, or keep them separated?**

---

## 2. Structural Mismatch Analysis (Data Topology)

The fundamental issue is that 2D and 3D data in this domain have different **topologies**.

### A. 2D Topology: "One Domain, Many Dependent Variables"
In 2D charts (e.g., Speed/RPM vs Time), multiple series share a single "Domain Axis" (X).
- **Structure**: `1 X (Shared) <-> N Ys (Scalars)`
- **Memory Layout**: `[X] + [Y1, Y2, Y3, ... Yn]` (Efficient)
- **Current Signature**: Matches this perfectly (`double x`, `Dict<string, double> y`).

### B. 3D Topology: "N Independent Trajectories"
In 3D plots, each object moves independently in space. There is no "Shared X" coordinate.
- **Structure**: `N Independent Vectors (x, y, z)`
- **Memory Layout**: `[(x1,y1,z1), (x2,y2,z2), ...]`
- **Conflict**: If we force this into the 2D signature, what is the `Shared_X_Value`?
  - It becomes meaningless (or must be Time).
  - The actual spatial $x$ coordinate moves to the dependent value side.

---

## 3. Comparison of Approaches

### Option A: Fully Unified Model (User Suggestion)
Create a single DTO that handles everything.

```csharp
public class UnifiedDataPoint
{
    public double? X; // For 3D (Individual X)
    public double Y;  // For 2D (Value) / 3D (Y)
    public double? Z; // For 3D (Z)
}

// Event: Action<double, Dictionary<string, UnifiedDataPoint>>
// Note: We removed the 'double sharedX' argument to unify.
```

| Aspect | Pros | Cons |
| :--- | :--- | :--- |
| **Cleanliness** | Single Event API. | **Ambiguity**: Field meanings shift (X is domain in 2D? Or spatial?). |
| **Logic** | One loop in `ProcessFrame`. | **Complexity**: Logic needs `if (is3D)` checks inside the loop anyway. |
| **Performance** | | **High Cost**: Allocating class/struct instances for every point vs scalar values. `Nullable<T>` checks overhead. |
| **Redundancy** | | **High**: In 2D, if we move 'Shared X' into the DTO, we duplicate the X value N times (Memory Bloat). If we keep Shared X outside, the DTO's 'X' field is dead weight for 2D. |

### Option B: Separated Events (Current Plan)
Keep 2D optimized, add 3D as a parallel parallel pipe.

```csharp
// 2D (Existing)
Action<double, double, Dictionary<string, double>> OnDataUpdated;

// 3D (New)
Action<double, Dictionary<string, (double x, double y, double z)>> OnDataUpdated3D;
```

| Aspect | Pros | Cons |
| :--- | :--- | :--- |
| **Cleanliness** | **Type Safety**: No chance of accessing 'Z' in 2D or missing 'X' in 3D. | Two events to subscribe to (if you need both). |
| **Performance** | **Zero Regression**: 2D path remains untouched and scalar-optimized. | |
| **Compatibility** | **100% Backward Compatible**. No refactoring of existing UI needed. | |
| **Topology** | Matches the "Shape" of data perfectly. | |

---

## 4. Decision: Why Separation is Superior Here

1.  **The "Shared X" Problem**:
    - Unifying forces us to either **duplicate the shared X** into every data point (bloat) or **keep a "Shared X" argument** that is useless for 3D (parameter pollution).
    - Separation allows 2D to keep its "Shared X" efficiency and 3D to drop it.

2.  **Performance (Zero-Cost Abstraction)**:
    - 90% of simulation data is usually 2D.
    - Using a Unified DTO with nullable Z (`double?`) adds overhead to the critical 2D path.
    - Separation ensures 2D processing pays **zero penalty** for 3D features existence.

3.  **Semantic Clarity**:
    - `Dictionary<string, double>` cleanly says "I am a list of scalar values".
    - `Dictionary<string, Vector3>` cleanly says "I am a list of spatial positions".
    - `Dictionary<string, UnifiedObject>` is vague ("What's inside me?").

## 5. Recommended Refinement

We will stick to the **Separated Design**, but we can improve the 3D definition to be more robust (using a readonly struct `Vector3` or `Tuple`).

**Plan**: Proceed with separation to guarantee performance and stability for the existing system.
