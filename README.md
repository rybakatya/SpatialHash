# OpenWorldToolkit.SpatialHash

A lightweight, allocation-lean **spatial index for the Unity Editor/Runtime** that buckets items on the XZ plane using coarse cells subdivided into smaller subcells. Perfect for proximity queries, AI neighborhoods, trigger checks, and crowd sims.


## ✨ Requirements
- Unity 2020 or newer  
- Works in **Play Mode** (no editor-only dependencies)
- Uses the **XZ** plane for spatial bucketing (Y is ignored)

---

## 📦 Installation (Unity Package Manager)

Install via **Package Manager** (UPM). Choose one of the options below.

### Option A — Add package from Git URL
1. Open **Window → Package Manager**.
2. Click the **+** button → **Add package from git URL…**
3. Paste the repo URL:
   ```
   https://github.com/<org-or-user>/OpenWorldToolkit.SpatialHash.git
   ```

## 🚀 Quick Start

### 1️⃣ Implement `ISpatialItem`
Your item must report its world position (XZ used, Y ignored):
```csharp
using UnityEngine;
using OpenWorldToolkit.SpatialHash;

public class Boid : MonoBehaviour, ISpatialItem
{
    public Vector3 GetPosition() => transform.position;
}
```

### 2️⃣ Create a spatial hash and insert items
```csharp
using System.Collections.Generic;
using UnityEngine;
using OpenWorldToolkit.SpatialHash;

public class BoidManager : MonoBehaviour
{
    public int cellSize = 16;   // world units per coarse cell
    public int subdiv = 4;      // subcells per axis (2..4)

    SpatialHash<Boid> hash;
    List<Boid> boids = new List<Boid>();

    void Awake()
    {
        hash = new SpatialHash<Boid>(cellSize, subdiv, initialCellCapacity: 1024, queryCapacity: 1024);

        // Insert all agents (once at spawn or after pooling)
        foreach (var b in boids)
            hash.Insert(b);
    }

    void Update()
    {
        // If agents moved significantly since last frame, update the index.
        // Simple approach: remove + insert. For heavy movement, keep cached cell/subindex externally for O(1) updates.
        foreach (var b in boids)
        {
            hash.Remove(b);
            hash.Insert(b);
        }
    }
}
```

### 3️⃣ Query neighbors
```csharp
// Circle query: exact per-item distance check
Vector3 center = transform.position;
float radius = 12f;
List<Boid> near = hash.QueryCircle(center, radius);

// 3×3 neighborhood: fast union of items near you (no radius filter)
List<Boid> local = hash.QueryNeighborhood(center);

// Collector overload to reuse your own list
var collector = new List<Boid>(256);
hash.QueryCircle(center, radius, collector);
```

---

## 🧩 How It Works

- **Coarse cells**: world split into squares of size `CellSize` on XZ.  
  `SpatialCell.FromVector3(pos, CellSize)` maps a point to `(x,z)` cell indices.
- **Subcells per coarse cell**: `Subdiv × Subdiv` grid (2..4) reduces iterations in dense cells.  
  Occupancy tracked with a **`ushort` bitmask** (≤16 bits), and an array of **`List<T>`** per subcell.
- **Insertion/Removal**: items are bucketed into one subcell based on local coords inside the coarse cell. Removal uses swap-back to avoid shifting costs; empty lists are pooled.
- **Queries**:
  - `QueryCircle(pos, radius)` touches only overlapped coarse cells and their overlapped **subcells**, then does precise squared-distance checks.
  - `QueryNeighborhood(pos)` returns union of items from the **3×3** coarse-cell neighborhood (no radius filtering).

---

## 🛠 Recommended Patterns

- **Moving items**: if an item crosses subcell/cell boundaries often, track its last `(SpatialCell, subIndex)` externally and perform O(1) remove/insert updates.
- **Avoid GC**: prefer the collector overload of `QueryCircle` to reuse your own list. The index also uses an internal scratch list to minimize allocations.
- **Parameter tuning**:
  - `cellSize`: usually **8–32** world units.
  - `subdiv`: **4** for high density, **3** balanced, **2** for sparse scenes.

---

## 🧪 Full Example

```csharp
using System.Collections.Generic;
using UnityEngine;
using OpenWorldToolkit.SpatialHash;

public class ProximityDemo : MonoBehaviour
{
    public int cellSize = 16;
    public int subdiv = 4;
    public float queryRadius = 10f;

    SpatialHash<Boid> hash;
    List<Boid> boids = new List<Boid>();
    readonly List<Boid> neighbors = new List<Boid>(256);

    void Start()
    {
        hash = new SpatialHash<Boid>(cellSize, subdiv, initialCellCapacity: 2048, queryCapacity: 1024);

        // Suppose boids list is populated from scene or pool
        foreach (var b in boids)
            hash.Insert(b);
    }

    void Update()
    {
        // Reindex (naive). For performance-critical cases, cache cell/subIndex per boid.
        foreach (var b in boids)
        {
            hash.Remove(b);
            hash.Insert(b);
        }

        // Example: find neighbors around player (or each boid)
        neighbors.Clear();
        hash.QueryCircle(Camera.main.transform.position, queryRadius, neighbors);

        // ...use neighbors
    }
}
```

---

## ⚠️ Troubleshooting

- **Nothing returned from queries**
  - Ensure you **insert** items after they spawn.
  - Confirm `GetPosition()` returns the **current** world position.
  - Set sensible `cellSize` (too large or too small can hurt results/perf).

- **Performance dips with dense crowds**
  - Increase `subdiv` from 2 → 3 or 4 to reduce per-subcell list sizes.
  - Use the **collector overload** to avoid result-list allocations.
  - Cache and update `(cell, subIndex)` for moving items to make updates O(1).

- **Queries include far-away items**
  - `QueryNeighborhood` is intentionally radius-less; use `QueryCircle` for precise radius filtering.

---

## 🚧 Limitations

- Not thread-safe; use on main thread or synchronize externally.
- Only considers **XZ** plane (Y ignored).
- Uses `List<T>` internally; for massive scales consider custom allocators/Native containers.

---

## 📜 License
See the repository’s `LICENSE` for details.
