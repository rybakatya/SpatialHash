using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace OpenWorldToolkit.SpatialHash
{


 [StructLayout(LayoutKind.Sequential)]
    public struct SpatialCell : IEquatable<SpatialCell>
    {
        public int x;
        public int z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SpatialCell(int x, int z) { this.x = x; this.z = z; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SpatialCell FromVector3(in Vector3 v, int cellSize)
        {
            int cx = Mathf.FloorToInt(v.x / cellSize);
            int cz = Mathf.FloorToInt(v.z / cellSize);
            return new SpatialCell(cx, cz);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(SpatialCell other) => x == other.x && z == other.z;
        public override bool Equals(object obj) => obj is SpatialCell sc && Equals(sc);

        // Fast mix, low collisions
        public override int GetHashCode()
        {
            unchecked
            {
                uint ux = (uint)x;
                uint uz = (uint)z;
                ux *= 0x85ebca6b; ux ^= ux >> 13; ux *= 0xc2b2ae35;
                uz *= 0x27d4eb2f; uz ^= uz >> 15; uz *= 0x165667b1;
                return (int)(ux ^ uz);
            }
        }

        public override string ToString() => $"({x},{z})";
    }

}
