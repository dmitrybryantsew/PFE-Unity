using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Map.TileCollision
{
    /// <summary>
    /// Records per-tick motor state, quantizes floating-point values to integer cents (x100),
    /// and computes a 64-bit FNV-1a hash over the entire trajectory.
    ///
    /// This makes regression testing a single-number comparison (ulong hash).
    /// </summary>
    public sealed class TraceRecorder
    {
        public readonly struct TickState
        {
            public readonly int Tick;
            public readonly int QuantizedX;
            public readonly int QuantizedY;
            public readonly int QuantizedDx;
            public readonly int QuantizedDy;
            public readonly bool IsGrounded;
            public readonly bool IsOnPlatform;
            public readonly bool IsOnLadder;
            public readonly bool IsInWater;
            public readonly bool IsFullySubmerged;
            public readonly bool HitCeiling;
            public readonly bool WallLeft;
            public readonly bool WallRight;

            public TickState(int tick, TilePhysicsController motor)
            {
                Tick = tick;
                QuantizedX = Mathf.RoundToInt(motor.PixelPosition.x * 100f);
                QuantizedY = Mathf.RoundToInt(motor.PixelPosition.y * 100f);
                QuantizedDx = Mathf.RoundToInt(motor.VelocityX * 100f);
                QuantizedDy = Mathf.RoundToInt(motor.VelocityY * 100f);
                IsGrounded = motor.IsGrounded;
                IsOnPlatform = motor.IsOnPlatform;
                IsOnLadder = motor.IsOnLadder;
                IsInWater = motor.IsInWater;
                IsFullySubmerged = motor.IsFullySubmerged;
                HitCeiling = motor.HitCeiling;
                WallLeft = motor.WallLeft;
                WallRight = motor.WallRight;
            }

            public override string ToString()
            {
                return $"t={Tick} pos=({QuantizedX / 100f:F2},{QuantizedY / 100f:F2}) vel=({QuantizedDx / 100f:F2},{QuantizedDy / 100f:F2}) g={IsGrounded} p={IsOnPlatform} lad={IsOnLadder} w={IsInWater} sub={IsFullySubmerged}";
            }
        }

        private readonly List<TickState> _frames = new List<TickState>();

        public IReadOnlyList<TickState> Frames => _frames;
        public int Count => _frames.Count;

        public void Record(int tick, TilePhysicsController motor)
        {
            _frames.Add(new TickState(tick, motor));
        }

        /// <summary>
        /// Compute 64-bit FNV-1a hash of the recorded frames.
        /// </summary>
        public ulong ComputeHash()
        {
            const ulong fnvOffsetBasis = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            ulong hash = fnvOffsetBasis;

            for (int i = 0; i < _frames.Count; i++)
            {
                TickState f = _frames[i];

                hash = HashInt(hash, fnvPrime, f.Tick);
                hash = HashInt(hash, fnvPrime, f.QuantizedX);
                hash = HashInt(hash, fnvPrime, f.QuantizedY);
                hash = HashInt(hash, fnvPrime, f.QuantizedDx);
                hash = HashInt(hash, fnvPrime, f.QuantizedDy);

                int flags = (f.IsGrounded ? 1 : 0)
                          | (f.IsOnPlatform ? 2 : 0)
                          | (f.IsOnLadder ? 4 : 0)
                          | (f.IsInWater ? 8 : 0)
                          | (f.IsFullySubmerged ? 16 : 0)
                          | (f.HitCeiling ? 32 : 0)
                          | (f.WallLeft ? 64 : 0)
                          | (f.WallRight ? 128 : 0);

                hash = HashInt(hash, fnvPrime, flags);
            }

            return hash;
        }

        private static ulong HashInt(ulong hash, ulong prime, int val)
        {
            unchecked
            {
                hash ^= (byte)(val & 0xFF);
                hash *= prime;
                hash ^= (byte)((val >> 8) & 0xFF);
                hash *= prime;
                hash ^= (byte)((val >> 16) & 0xFF);
                hash *= prime;
                hash ^= (byte)((val >> 24) & 0xFF);
                hash *= prime;
                return hash;
            }
        }

        public string DumpTrace()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Trace: {_frames.Count} frames, hash=0x{ComputeHash():X16}");
            for (int i = 0; i < _frames.Count; i++)
            {
                sb.AppendLine($"  [{i:D3}] {_frames[i]}");
            }
            return sb.ToString();
        }
    }
}
