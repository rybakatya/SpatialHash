using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace OpenWorldToolkit.SpatialHash
{
    public sealed class SpatialHash<T> where T : ISpatialItem
    {
        public readonly int CellSize;          // world units per coarse cell
        public readonly int Subdiv;            // subcells per axis inside a coarse cell (2..4)
        public readonly float SubcellSize;     // world units per subcell

        // Per-cell storage: small sparse array of subcell lists + occupancy bitmask (<=16 bits for Subdiv<=4)
        struct CellBucket
        {
            public ushort occupancyMask;   // each bit = one subcell occupied (max 16 for Subdiv<=4)
            public List<T>[] subs;         // length = Subdiv * Subdiv
        }

        // Backing dictionary
        private readonly Dictionary<SpatialCell, CellBucket> _cells;

        // Scratch buffer returned by queries (avoid per-call allocations)
        private readonly List<T> _scratch;

        // Pool for subcell lists to reduce GC churn
        private readonly Stack<List<T>> _listPool;

        private readonly int _subCount;
        private readonly float _invSubcellSize;

        public SpatialHash(int cellSize, int subdiv = 4, int initialCellCapacity = 1024, int queryCapacity = 1024)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (subdiv < 2 || subdiv > 4) throw new ArgumentOutOfRangeException(nameof(subdiv), "Use 2..4 with ushort mask.");

            CellSize = cellSize;
            Subdiv = subdiv;
            SubcellSize = (float)CellSize / Subdiv;
            _invSubcellSize = 1f / SubcellSize;
            _subCount = Subdiv * Subdiv;

            _cells = new Dictionary<SpatialCell, CellBucket>(Mathf.Max(16, initialCellCapacity));
            _scratch = new List<T>(Mathf.Max(16, queryCapacity));
            _listPool = new Stack<List<T>>(256);
        }

        // -------------------------- Public API --------------------------

        /// <summary> Insert item by its current position. Returns the coarse cell it landed in. </summary>
        public SpatialCell Insert(T item)
        {
            var p = item.GetPosition();
            var cell = SpatialCell.FromVector3(p, CellSize);

            var bucket = GetOrCreateBucket(cell);

            GetLocalCoords(p, out float lx, out float lz);
            int subIdx = SubIndexFromLocal(lx, lz);

            if (bucket.subs[subIdx] == null)
                bucket.subs[subIdx] = (_listPool.Count > 0) ? _listPool.Pop() : new List<T>(8);

            bucket.subs[subIdx].Add(item);
            bucket.occupancyMask |= (ushort)(1 << subIdx);

            SetBucket(cell, bucket); // write back for .NET < 7
            return cell;
        }

        /// <summary>
        /// Remove item by looking up its current position (O(subcell) + IndexOf).
        /// For frequent movers, cache last known (cell, subIdx) externally for O(1) removal.
        /// </summary>
        public void Remove(T item)
        {
            var p = item.GetPosition();
            var cell = SpatialCell.FromVector3(p, CellSize);
            if (!_cells.TryGetValue(cell, out var bucket)) return;

            GetLocalCoords(p, out float lx, out float lz);
            int subIdx = SubIndexFromLocal(lx, lz);
            var list = bucket.subs[subIdx];
            if (list == null || list.Count == 0) return;

            int i = list.IndexOf(item);
            if (i >= 0)
            {
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);

                if (list.Count == 0)
                {
                    bucket.subs[subIdx] = null;
                    bucket.occupancyMask = (ushort)(bucket.occupancyMask & ~(1 << subIdx));

                    // Pool the emptied list
                    list.Clear();
                    if (_listPool.Count < 4096) _listPool.Push(list);
                }

                SetBucket(cell, bucket); // write back
            }
        }

        /// <summary>
        /// Circle query: returns items within 'radius' of 'pos' (exact per-item check).
        /// Efficient even with high-density cells: touches only overlapped coarse cells and their overlapped subcells.
        /// </summary>
        public List<T> QueryCircle(Vector3 pos, float radius)
        {
            _scratch.Clear();
            float r = Mathf.Max(0f, radius);
            float r2 = r * r;

            // Coarse cell bounds overlapped by the circle
            int minCellX = Mathf.FloorToInt((pos.x - r) / CellSize);
            int maxCellX = Mathf.FloorToInt((pos.x + r) / CellSize);
            int minCellZ = Mathf.FloorToInt((pos.z - r) / CellSize);
            int maxCellZ = Mathf.FloorToInt((pos.z + r) / CellSize);

            for (int cz = minCellZ; cz <= maxCellZ; cz++)
            {
                for (int cx = minCellX; cx <= maxCellX; cx++)
                {
                    var cell = new SpatialCell(cx, cz);
                    if (!_cells.TryGetValue(cell, out var bucket)) continue;
                    if (bucket.occupancyMask == 0) continue;

                    // Local position relative to this cell
                    float cellOriginX = cx * (float)CellSize;
                    float cellOriginZ = cz * (float)CellSize;
                    float localX = pos.x - cellOriginX;
                    float localZ = pos.z - cellOriginZ;

                    // AABB of the circle clipped to this cell
                    float minX = Mathf.Max(0f, localX - r);
                    float maxX = Mathf.Min(CellSize, localX + r);
                    float minZ = Mathf.Max(0f, localZ - r);
                    float maxZ = Mathf.Min(CellSize, localZ + r);
                    if (minX >= maxX || minZ >= maxZ) continue;

                    int subMinX = Mathf.Clamp((int)(minX * _invSubcellSize), 0, Subdiv - 1);
                    int subMaxX = Mathf.Clamp((int)(maxX * _invSubcellSize), 0, Subdiv - 1);
                    int subMinZ = Mathf.Clamp((int)(minZ * _invSubcellSize), 0, Subdiv - 1);
                    int subMaxZ = Mathf.Clamp((int)(maxZ * _invSubcellSize), 0, Subdiv - 1);

                    for (int sz = subMinZ; sz <= subMaxZ; sz++)
                    {
                        for (int sx = subMinX; sx <= subMaxX; sx++)
                        {
                            int subIdx = sz * Subdiv + sx;
                            int bit = 1 << subIdx;
                            if ((bucket.occupancyMask & bit) == 0) continue;

                            var list = bucket.subs[subIdx];
                            if (list == null || list.Count == 0) continue;

                            // Per-item precise circle test
                            for (int i = 0; i < list.Count; i++)
                            {
                                var it = list[i];
                                Vector3 ip = it.GetPosition();
                                if ((ip - pos).sqrMagnitude <= r2)
                                    _scratch.Add(it);
                            }
                        }
                    }
                }
            }

            return _scratch;
        }

        /// <summary>
        /// Fixed 3x3 coarse-cell neighborhood around 'pos', using subcells for sparsity.
        /// No radius filtering; returns everything in overlapped subcells.
        /// </summary>
        public List<T> QueryNeighborhood(Vector3 pos)
        {
            _scratch.Clear();

            var baseCell = SpatialCell.FromVector3(pos, CellSize);

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var cell = new SpatialCell(baseCell.x + dx, baseCell.z + dz);
                    if (!_cells.TryGetValue(cell, out var bucket)) continue;
                    if (bucket.occupancyMask == 0) continue;

                    ushort mask = bucket.occupancyMask;
                    while (mask != 0)
                    {
                        int subIdx = PopLowestBit(ref mask);
                        var list = bucket.subs[subIdx];
                        if (list != null && list.Count > 0)
                            _scratch.AddRange(list);
                    }
                }
            }

            return _scratch;
        }

        /// <summary>
        /// Optional: caller-provided collector to avoid using the shared scratch list.
        /// </summary>
        public void QueryCircle(Vector3 pos, float radius, List<T> collector)
        {
            collector.Clear();
            var res = QueryCircle(pos, radius);
            collector.AddRange(res);
        }

        // -------------------------- Internals --------------------------

        // .NET < 7 compatible helper: returns a copy; use SetBucket() to write back
        private CellBucket GetOrCreateBucket(in SpatialCell cell)
        {
            if (_cells.TryGetValue(cell, out var b))
                return b;

            var nb = new CellBucket
            {
                occupancyMask = 0,
                subs = new List<T>[_subCount]
            };
            _cells[cell] = nb; // create
            return nb;
        }

        private void SetBucket(in SpatialCell cell, in CellBucket bucket)
        {
            _cells[cell] = bucket;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Mod(float x, float m) => x - m * Mathf.Floor(x / m);

        // Local coords inside [0, CellSize) for this cell
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetLocalCoords(in Vector3 p, out float lx, out float lz)
        {
            lx = Mod(p.x, CellSize);
            lz = Mod(p.z, CellSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SubIndexFromLocal(float localX, float localZ)
        {
            int sx = Mathf.Clamp((int)(localX * _invSubcellSize), 0, Subdiv - 1);
            int sz = Mathf.Clamp((int)(localZ * _invSubcellSize), 0, Subdiv - 1);
            return sz * Subdiv + sx;
        }

        // Bit helpers for ushort mask (Subdiv<=4 => up to 16 bits)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopLowestBit(ref ushort mask)
        {
            int idx = TrailingZeroCount(mask);
            mask = (ushort)(mask & (mask - 1)); // clear lowest set bit
            return idx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TrailingZeroCount(ushort v)
        {
            if (v == 0) return 16;
            int c = 0;
            while ((v & 1) == 0) { v >>= 1; c++; }
            return c;
        }
    }
}
