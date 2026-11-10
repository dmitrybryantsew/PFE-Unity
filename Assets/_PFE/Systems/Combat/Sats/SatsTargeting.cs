using System.Collections.Generic;
using UnityEngine;
using PFE.Entities.Units;
using PFE.Sim.Combat.Sats;

namespace PFE.Systems.Combat.Sats
{
    /// <summary>
    /// One unit S.A.T.S. may target, with the percentage it would print — the port of one entry of
    /// <c>Sats.units</c> (<c>Sats.as:503-527</c>).
    /// </summary>
    public readonly struct SatsCandidate
    {
        public readonly UnitController Unit;

        /// <summary>The hit chance as a 0..1 fraction, or <c>-1</c> when the weapon is
        /// <c>noperc</c> and no percentage is shown (<c>Sats.as:492-500</c>).</summary>
        public readonly float HitChance;

        public SatsCandidate(UnitController unit, float hitChance)
        {
            Unit = unit;
            HitChance = hitChance;
        }

        public bool ShowsPercent => HitChance >= 0f;

        /// <summary><c>Math.round(prec * 100)</c> — the label's integer.</summary>
        public int Percent => ShowsPercent ? SatsRules.PercentForLabel(HitChance) : 0;
    }

    /// <summary>
    /// <c>Sats.getUnits()</c> — who can be targeted, and <c>Sats.setCel</c>'s hover hit test.
    ///
    /// <para><b>The candidate set is computed once, on entry, and never again.</b> The world is
    /// frozen while the mode is open, so nothing can walk into or out of the set; the Oracle relies
    /// on the same fact (<c>getUnits</c> is called only from <c>onoff</c>). A target that dies while
    /// the mode is open is handled by the queue's own aliveness test, not by refreshing.</para>
    /// </summary>
    public sealed class SatsTargeting
    {
        /// <summary><c>Unit.getTileVisi()</c>'s threshold — <c>visi &gt; 0.3</c>
        /// (<c>Unit.as:4527-4530</c>). A target standing in deep shadow is not targetable even with a
        /// clear line of sight.</summary>
        public const float FogVisibilityThreshold = 0.3f;

        private readonly List<SatsCandidate> _candidates = new List<SatsCandidate>(32);

        /// <summary>How many cells each unit already carries — <c>u.n</c> in <c>Sats.as:502</c>, read
        /// by <c>SatsCel</c> to size its bracket. Incremented on queue, decremented on removal, and
        /// reset with the candidate set.</summary>
        private readonly Dictionary<UnitController, int> _cellsPerUnit =
            new Dictionary<UnitController, int>();

        public IReadOnlyList<SatsCandidate> Candidates => _candidates;

        /// <summary>
        /// Rebuild the target set. <paramref name="meleeReachPx"/> is <c>pers.meleeR</c>; the melee
        /// branch multiplies it by 1.2 and adds 100 (<c>Sats.as:466-472</c>) — an explicit override,
        /// not the vision range.
        /// </summary>
        public void Refresh(UnitController player, bool isMelee, float meleeReachPx, ISatsWorldQuery world)
        {
            _candidates.Clear();
            _cellsPerUnit.Clear();

            if (player == null) return;

            Vector2 playerPx = ToPixels(player.WorldPosition);

            // `for each (u in World.w.loc.units)` — the port's flat list of live views. Units without
            // an entity id are still mid-spawn (see UnitViewRegistry) and are skipped by
            // IsSatsTargetable's aliveness test rather than by an id check here.
            IReadOnlyList<UnitController> live = UnitViewRegistry.Live;

            for (int i = 0; i < live.Count; i++)
            {
                UnitController u = live[i];
                if (u == null) continue;

                // `gg.isMeet(u) && u.isSats && u.sost < 3 && !u.invis`
                if (!u.IsSatsTargetable(player)) continue;

                Vector2 unitPx = ToPixels(u.WorldPosition);
                Vector2 body = u.SpriteSizePixels;
                Vector2 centrePx = new Vector2(unitPx.x, unitPx.y - body.y * 0.5f);

                if (isMelee)
                {
                    // `gg.look(u, false, 0, pers.meleeR * 1.2 + 100) > 0` — a range override with the
                    // same LOS ray behind it. Note the fog test is NOT applied on this branch: AS3
                    // omits `getTileVisi()` for melee, so a blade reaches into an unlit corner a gun
                    // cannot see into.
                    float reachPx = meleeReachPx * 1.2f + 100f;
                    if (Vector2.Distance(playerPx, centrePx) > reachPx) continue;
                    if (world != null && !world.HasLineOfSight(playerPx, centrePx)) continue;
                }
                else
                {
                    // `gg.look(u) > 0 && u.getTileVisi()`. The range half of `look()` resolves to
                    // 4 000 px at the default `visibility` of 1000 — wider than any room — so the LOS
                    // ray and the fog test are the real filters, and the range term is not modelled.
                    // Recorded rather than silently approximated: a unit further than 40 tiles away
                    // would be admissible here and refused by the Oracle.
                    if (world != null)
                    {
                        if (!world.HasLineOfSight(playerPx, centrePx)) continue;

                        // `getTileVisi()` is `visi > 0.3` on the fog mask. The read-back returns
                        // FALSE when there IS NO mask — fog disabled, mask not built, collision-only
                        // room — and in that state the unit is fully visible. Using the bool as a
                        // "skip" guard here emptied the whole candidate list in every fogless room,
                        // so the mode showed no names, percentages, brackets or hover panel. The
                        // distinction lives in SatsRules.FogAdmitsTarget so it is fixture-testable
                        // without a room; this line only supplies the two readings.
                        bool fogCovered = world.TryGetFogVisibility(u.WorldPosition, out float vis);
                        if (!SatsRules.FogAdmitsTarget(fogCovered, vis, FogVisibilityThreshold)) continue;
                    }
                }

                _candidates.Add(new SatsCandidate(u, 0f));
            }

            // The percentages need the weapon, which the caller has; they are filled by
            // FillHitChances so this method stays a pure "who" and the "how likely" is one loop.
        }

        /// <summary>
        /// Compute every candidate's hit percentage — <c>getPrec</c> per unit
        /// (<c>Sats.as:490-500</c>), skipped entirely for a <c>noperc</c> weapon, which leaves
        /// <see cref="SatsCandidate.HitChance"/> at <c>-1</c> so the label draws no number.
        /// </summary>
        public void FillHitChances(UnitController player, in SatsWeapon weapon, float skillConf, bool showPercent)
        {
            if (!showPercent)
            {
                for (int i = 0; i < _candidates.Count; i++)
                {
                    _candidates[i] = new SatsCandidate(_candidates[i].Unit, -1f);
                }
                return;
            }

            Vector2 playerPx = ToPixels(player.WorldPosition);

            for (int i = 0; i < _candidates.Count; i++)
            {
                UnitController u = _candidates[i].Unit;
                Vector2 unitPx = ToPixels(u.WorldPosition);
                Vector2 body = u.SpriteSizePixels;
                Vector2 centrePx = new Vector2(unitPx.x, unitPx.y - body.y * 0.5f);

                // `hypot(weapon.X - u.X, weapon.Y - u.Y)` — from the WEAPON, not the body centre.
                // The port has no exposed muzzle point on the loadout, so the player's origin stands
                // in. The offset is the hold point (a few tens of pixels), which shifts the printed
                // percentage slightly at close range; recorded as a known approximation rather than
                // presented as exact.
                float d = Vector2.Distance(playerPx, unitPx);

                var inputs = new SatsPrecInputs(
                    precisionPx:        weapon.PrecisionPx,
                    precMult:           weapon.PrecMult,
                    skillMultiplier:    weapon.SkillMultiplier,
                    precMultCont:       1f,   // precMultCont is not modelled — see the deferred table
                    antiPrecisionPx:    weapon.AntiPrecisionPx,
                    deviationDeg:       weapon.DeviationDeg,
                    mazil:              weapon.Mazil,
                    skillConf:          skillConf,
                    distancePx:         d,
                    targetDexterity:    u.UnitStats != null ? u.UnitStats.dexterity : 1f,
                    targetBodyHeightPx: body.y);

                _candidates[i] = new SatsCandidate(u, SatsRules.HitChance(inputs));
            }
        }

        /// <summary>
        /// Which unit is under the pointer — the port of <c>setCel</c>'s glow test
        /// (<c>Sats.as:246-256</c>), which walks the drawn copies looking for the red hover glow.
        ///
        /// <para><b>The Oracle's version is a hit test against a bitmap, in reverse order</b> (the
        /// last-drawn unit wins), so a unit standing in front of another is the one you lock. This
        /// uses the body box, which is the same rectangle the glow sits on to within the sprite's
        /// transparent margin. Reverse order is preserved because it is the only thing that makes two
        /// overlapping targets predictable.</para>
        /// </summary>
        public UnitController UnitUnderPointer(Vector2 pointerWorldPx)
        {
            for (int i = _candidates.Count - 1; i >= 0; i--)
            {
                UnitController u = _candidates[i].Unit;
                if (u == null) continue;

                Vector2 unitPx = ToPixels(u.WorldPosition);
                Vector2 body = u.SpriteSizePixels;
                float halfW = body.x * 0.5f;

                // AS3's box is `X1 = X - scX/2 … X2 = X + scX/2`, `Y1 = Y - scY … Y2 = Y`
                // (Unit.as:1875-1878): the origin is the body's BOTTOM centre.
                if (pointerWorldPx.x < unitPx.x - halfW) continue;
                if (pointerWorldPx.x > unitPx.x + halfW) continue;
                if (pointerWorldPx.y < unitPx.y - body.y) continue;
                if (pointerWorldPx.y > unitPx.y) continue;

                return u;
            }

            return null;
        }

        /// <summary><c>++u.n</c> (<c>SatsCel.as:35</c>) — one more bracket on this unit.</summary>
        public void NoteCellAdded(UnitController unit)
        {
            if (unit == null) return;
            _cellsPerUnit.TryGetValue(unit, out int n);
            _cellsPerUnit[unit] = n + 1;
        }

        /// <summary><c>--u.n</c> (<c>Sats.as:283</c>).</summary>
        public void NoteCellRemoved(UnitController unit)
        {
            if (unit == null) return;
            if (!_cellsPerUnit.TryGetValue(unit, out int n)) return;
            if (n <= 1) _cellsPerUnit.Remove(unit);
            else _cellsPerUnit[unit] = n - 1;
        }

        /// <summary>How many cells this unit carries — the <c>n</c> in <c>(scX + 10n) / 100</c>.</summary>
        public int CellsOn(UnitController unit)
        {
            return unit != null && _cellsPerUnit.TryGetValue(unit, out int n) ? n : 0;
        }

        public void Clear()
        {
            _candidates.Clear();
            _cellsPerUnit.Clear();
        }

        /// <summary>
        /// Unity world units → world pixels. <c>TileQueryConstants.UnitToPixel</c> is 100 and this is
        /// the only conversion S.A.T.S. makes; everything inside the mode is pixels, matching the
        /// Oracle's own units so its constants (<c>320</c> px antiprec, <c>220</c> px melee reach)
        /// read as written.
        /// </summary>
        public static Vector2 ToPixels(Vector3 world) => new Vector2(world.x * 100f, world.y * 100f);
    }
}
