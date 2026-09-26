using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map.Serialization;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Dual-run shadow logger for tile queries during Stage B / C transition.
    /// Runs queries against both primary (<see cref="GridTileQuery"/>) and shadow (<see cref="UnifiedTileQueryService"/>),
    /// logs field-level divergences when <see cref="PfeDebugSettings.TileQueryLogDivergence"/> is enabled.
    ///
    /// Implements <see cref="ITileQueryService"/> for transparent drop-in via VContainer DI.
    /// </summary>
    public sealed class TileQueryDivergenceLogger : ITileQueryService
    {
        private readonly ITileQueryService _primary;
        private readonly ITileQueryService _shadow;
        private readonly PfeDebugSettings _debugSettings;
        private int _divergenceCount;

        [LocalOnly]
        public TileQueryBackend Backend => _primary.Backend;

        [LocalOnly]
        public RoomInstance Room => _primary.Room;

        [LocalOnly]
        public int DivergenceCount => _divergenceCount;

        public TileQueryDivergenceLogger(
            ITileQueryService primary,
            ITileQueryService shadow,
            PfeDebugSettings debugSettings = null)
        {
            _primary = primary;
            _shadow = shadow;
            _debugSettings = debugSettings;
        }

        [LocalOnly]
        public bool IsSolidAt(Vector2Int tileCoord)
        {
            bool p = _primary.IsSolidAt(tileCoord);
            bool s = _shadow.IsSolidAt(tileCoord);
            if (p != s)
            {
                LogDivergence($"IsSolidAt({tileCoord}) primary={p} shadow={s}");
            }
            return p;
        }

        [LocalOnly]
        public bool CheckCollision(Rect boundsPx, TileQueryOptions options)
        {
            bool p = _primary.CheckCollision(boundsPx, options);
            bool s = _shadow.CheckCollision(boundsPx, options);
            if (p != s)
            {
                LogDivergence($"CheckCollision({boundsPx}) primary={p} shadow={s}");
            }
            return p;
        }

        [LocalOnly]
        public float GetGroundHeight(Vector2 positionPx)
        {
            float p = _primary.GetGroundHeight(positionPx);
            float s = _shadow.GetGroundHeight(positionPx);
            if (Mathf.Abs(p - s) > 0.01f)
            {
                LogDivergence($"GetGroundHeight({positionPx}) primary={p} shadow={s} diff={p - s}");
            }
            return p;
        }

        [LocalOnly]
        public bool IsOnGround(Rect boundsPx)
        {
            bool p = _primary.IsOnGround(boundsPx);
            bool s = _shadow.IsOnGround(boundsPx);
            if (p != s)
            {
                LogDivergence($"IsOnGround({boundsPx}) primary={p} shadow={s}");
            }
            return p;
        }

        [LocalOnly]
        public TileRaycastHit? Raycast(Vector2 originPx, Vector2 direction, float maxDistancePx)
        {
            TileRaycastHit? p = _primary.Raycast(originPx, direction, maxDistancePx);
            TileRaycastHit? s = _shadow.Raycast(originPx, direction, maxDistancePx);
            if (p.HasValue != s.HasValue)
            {
                LogDivergence($"Raycast hit presence primary={p.HasValue} shadow={s.HasValue}");
            }
            return p;
        }

        [LocalOnly]
        public TileQueryFlags Classify(Vector2Int tileCoord)
        {
            TileQueryFlags p = _primary.Classify(tileCoord);
            TileQueryFlags s = _shadow.Classify(tileCoord);
            if (p != s)
            {
                LogDivergence($"Classify({tileCoord}) primary={p} shadow={s}");
            }
            return p;
        }

        [LocalOnly]
        public TileMoveResult ResolveMove(in TileBox box, Vector2 delta, TileQueryFlags mask)
        {
            TileMoveResult p = _primary.ResolveMove(box, delta, mask);
            TileMoveResult s = _shadow.ResolveMove(box, delta, mask);
            if (Vector2.Distance(p.Position, s.Position) > 0.05f ||
                p.HitFloor != s.HitFloor ||
                p.HitCeiling != s.HitCeiling ||
                p.HitLeft != s.HitLeft ||
                p.HitRight != s.HitRight)
            {
                LogDivergence($"ResolveMove delta={delta} primaryPos={p.Position} shadowPos={s.Position}");
            }
            return p;
        }

        [Authoritative]
        public bool ApplyDamage(Vector2 positionPx, int damage, int radiusTiles = 1)
        {
            bool p = _primary.ApplyDamage(positionPx, damage, radiusTiles);
            _shadow.ApplyDamage(positionPx, damage, radiusTiles);
            return p;
        }

        [Authoritative]
        public void NotifyTilesMutated(RectInt tileRegion)
        {
            _primary.NotifyTilesMutated(tileRegion);
            _shadow.NotifyTilesMutated(tileRegion);
        }

        [LocalOnly]
        public TileStateSnapshot[] CaptureFullState() => _primary.CaptureFullState();

        [Authoritative]
        public void ApplyFullState(TileStateSnapshot[] snapshot)
        {
            _primary.ApplyFullState(snapshot);
            _shadow.ApplyFullState(snapshot);
        }

        [Authoritative]
        public TileMutation[] DrainMutations()
        {
            _shadow.DrainMutations();
            return _primary.DrainMutations();
        }

        [Authoritative]
        public void ApplyMutations(TileMutation[] mutations)
        {
            _primary.ApplyMutations(mutations);
            _shadow.ApplyMutations(mutations);
        }

        private void LogDivergence(string message)
        {
            _divergenceCount++;
            if (_debugSettings != null && _debugSettings.TileQueryLogDivergence)
            {
                Debug.LogWarning($"[TileQuery Divergence #{_divergenceCount}] {message}");
            }
        }
    }
}
