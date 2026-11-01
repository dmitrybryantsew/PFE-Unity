using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>What one call to <see cref="TileHitRules.Resolve"/> did to the tile.</summary>
    public enum TileHitOutcome
    {
        /// <summary>Nothing at all — no damage and no decal.</summary>
        Ignored,

        /// <summary>
        /// A <c>desoff</c> room above 500 hp: the hit was drawn and then thrown away. This is the
        /// branch that makes a wall scarred but unbreakable.
        /// </summary>
        VisualOnly,

        /// <summary><c>udar</c> refused the hit (indestructible, or below <c>thre</c>). A decal is still drawn.</summary>
        Refused,

        /// <summary>Hp dropped; the tile stands. A decal is drawn.</summary>
        Damaged,

        /// <summary>Hp reached 0. <b>No decal</b> — the caller runs the death decal instead.</summary>
        Destroyed,
    }

    /// <summary>
    /// The answer to "what does this hit do to this tile", including the visible part.
    /// </summary>
    public readonly struct TileHitDecision
    {
        public TileHitDecision(
            TileHitOutcome outcome, int decalType, bool drawDecal, bool decalBlocked,
            float decalRatio, int hitPointsAfter, TileDamageOutcome refusal = TileDamageOutcome.Damaged)
        {
            Outcome = outcome;
            DecalType = decalType;
            DrawDecal = drawDecal;
            DecalBlocked = decalBlocked;
            DecalRatio = decalRatio;
            HitPointsAfter = hitPointsAfter;
            Refusal = refusal;
        }

        public TileHitOutcome Outcome { get; }

        /// <summary>
        /// <see cref="TileDestruction"/>'s own verdict, carried through unchanged so a caller's
        /// diagnostic can name <i>which</i> gate refused — <c>thre</c> or <c>indestruct</c> — without
        /// re-deriving it. Only meaningful for <see cref="TileHitOutcome.Refused"/>.
        /// </summary>
        public TileDamageOutcome Refusal { get; }

        /// <summary>The <c>tipdec</c> the decal is drawn with, <b>after</b> the <c>100 → 4</c> rewrite.</summary>
        public int DecalType { get; }

        /// <summary>Whether the caller should stamp a decal at the impact point.</summary>
        public bool DrawDecal { get; }

        /// <summary>AS3 <c>param5</c> for <c>Grafon.dyrka</c> — the hit did not bite.</summary>
        public bool DecalBlocked { get; }

        /// <summary>AS3 <c>param2 / param1.hp</c> for <c>Grafon.dyrka</c>.</summary>
        public float DecalRatio { get; }

        /// <summary>The tile's hp after the call. Unchanged for <see cref="TileHitOutcome.VisualOnly"/>.</summary>
        public int HitPointsAfter { get; }

        /// <summary>True when the tile must now run its death sequence.</summary>
        public bool ShouldDie => Outcome == TileHitOutcome.Destroyed;
    }

    /// <summary>
    /// AS3 <c>Location.hitTile(tile, damage, x, y, tipDecal = 9)</c> — <b>the one entry point through
    /// which every weapon, every explosion and every ramming unit reaches a wall</b>
    /// (<c>Location.as:2503-2572</c>).
    ///
    /// <para><b>Why this is separate from <see cref="TileDestruction"/>.</b> <c>Tile.udar</c> is pure
    /// arithmetic — <c>if (indestruct || thre &gt; dmg) return false; hp -= dmg;</c> — and the port has
    /// had it in one place since it was written. But <c>hitTile</c> wraps it in three gates that have
    /// nothing to do with arithmetic, and the port had <b>none</b> of them:</para>
    /// <list type="number">
    /// <item><description><b>The <c>100</c> pseudo-type.</b> Two callers pass <c>100</c> instead of a
    /// real decal type — a unit ramming a wall (<c>Unit.as:3311,3319</c>) — and a weak hit of that kind
    /// against a tile with a threshold or <c>indestruct</c> is dropped <i>before</i> anything else
    /// happens. It is not a decal type at all; it is a "this is a body, not a bullet" marker that is
    /// rewritten to <c>4</c> (the blade decal) as soon as it is allowed through.</description></item>
    /// <item><description><b>The <c>desoff</c> gate.</b> A room authored <c>&lt;options desoff="1"&gt;</c>
    /// sets <c>Location.destroyOn = false</c> (<c>Location.as:490</c>), and then every hit against a tile
    /// still above 500 hp becomes <b>visual only</b> (<c>:2513-2520</c>). The wall scars; it does not
    /// yield. Without this the port lets the player shoot open rooms the designer sealed.</description></item>
    /// <item><description><b>The <c>phis</c> gates.</b> Already in the port, but expressed at the wrong
    /// granularity — see the note on <see cref="TilePhysicsType"/>.</description></item>
    /// </list>
    ///
    /// <para><b>The ratio is post-hit hp, and that is not a typo.</b> AS3 writes
    /// <c>this.grafon.dyrka(…, param2 / param1.hp)</c> <i>after</i> <c>udar</c> has already subtracted,
    /// so the denominator is the hp the tile has <b>left</b>. A hit that takes a 1000 hp wall to 900
    /// therefore scores <c>100/900 = 0.11</c>, not <c>0.10</c>. Three of <c>dyrka</c>'s arms gate a
    /// scorch on it, so the difference is visible: it makes the last hit before destruction the one most
    /// likely to throw a scorch.</para>
    /// </summary>
    public static class TileHitRules
    {
        /// <summary>
        /// The hp above which a <c>desoff</c> room refuses to lose a tile — <c>Location.as:2513</c>.
        /// </summary>
        public const int UndestroyableHpGate = 500;

        /// <summary>
        /// The damage at or below which a <c>100</c>-type hit against a thresholded or indestructible
        /// tile is dropped outright — <c>Location.as:2505</c>.
        /// </summary>
        public const int RamDamageGate = 50;

        /// <summary>The marker callers pass instead of a decal type when a <i>body</i> hits a wall.</summary>
        public const int RamDecalType = 100;

        /// <summary>What <c>100</c> is rewritten to: the blade decal.</summary>
        public const int RamDecalTypeRewritten = 4;

        /// <summary>
        /// One hit against one tile.
        /// </summary>
        /// <param name="tile">The tile. <c>null</c> answers <see cref="TileHitOutcome.Ignored"/>.</param>
        /// <param name="damage">The hit's tile-destroy value, in hp. Rounded, as <c>udar</c> takes an int.</param>
        /// <param name="decalType">
        /// The weapon's <c>tipdec</c>. Pass <see cref="RamDecalType"/> when a unit body is the source.
        /// </param>
        /// <param name="destroyOn">
        /// <c>Location.destroyOn</c> — false in a room authored <c>desoff="1"</c>. Defaults to true, which
        /// is what every room without the flag gets (<c>Location.as:209</c>).
        /// </param>
        /// <param name="roomActive">
        /// <c>Location.active</c>. AS3 only draws when the room being hit is the room on screen; a
        /// background room still takes the damage, it just has no bitmap to stamp into.
        /// </param>
        public static TileHitDecision Resolve(
            TileData tile, float damage, int decalType,
            bool destroyOn = true, bool roomActive = true)
        {
            if (tile == null)
            {
                return new TileHitDecision(TileHitOutcome.Ignored, decalType, false, false, 0f, 0);
            }

            int amount = Mathf.RoundToInt(damage);

            // Location.as:2505 — a ramming body against a wall that has a threshold, or cannot be
            // broken at all, is not even worth a mark. This runs before the 100→4 rewrite on purpose:
            // the test is on the marker, not on the blade decal.
            if (decalType == RamDecalType && amount <= RamDamageGate &&
                (tile.damageThreshold > 0 || tile.indestructible))
            {
                return new TileHitDecision(TileHitOutcome.Ignored, decalType, false, false, 0f, tile.hitPoints);
            }

            // Location.as:2509 — the marker becomes the blade decal.
            if (decalType == RamDecalType)
            {
                decalType = RamDecalTypeRewritten;
            }

            // Location.as:2513-2520 — `desoff` rooms only lose tiles that are already nearly gone.
            // Note the test is `phis == 1` exactly, not `>= 1`: a grate is not stamped.
            if (!destroyOn && tile.hitPoints > UndestroyableHpGate)
            {
                bool stamp = roomActive && IsExactlyPhisOne(tile);
                return new TileHitDecision(
                    TileHitOutcome.VisualOnly, decalType,
                    stamp, true, Ratio(amount, tile.hitPoints), tile.hitPoints);
            }

            // AS3 has no zero guard: `udar(0)` succeeds, subtracts nothing, and the tile then takes an
            // **unblocked** decal with a ratio of 0 (`Location.as:2555-2561`). That is not an edge case
            // — it is most weapons. `destroy` is the tile-*breaking* power, and a pistol that cannot
            // open concrete still pokes holes in it. `TileDestruction.Apply` refuses `damage <= 0` on
            // purpose (it is about reporting, and "damaged by zero" is a lie), so the visual half has to
            // be answered here or every non-breaching weapon would leave a clean wall.
            if (amount <= 0)
            {
                bool mark = roomActive && tile.IsDamageable();
                return new TileHitDecision(
                    mark ? TileHitOutcome.Damaged : TileHitOutcome.Ignored,
                    decalType, mark, false, 0f, tile.hitPoints);
            }

            // Location.as:2521 — `udar` plus the port's `phis >= 1` gate, which `TileDestruction` owns.
            TileDamageOutcome outcome = TileDestruction.Apply(tile, amount);

            switch (outcome)
            {
                case TileDamageOutcome.Destroyed:
                    // No decal: the tile is gone and the caller draws the death decal instead
                    // (`Location.as:2548` calls `tileDie`, never `dyrka`).
                    return new TileHitDecision(
                        TileHitOutcome.Destroyed, decalType, false, false, 0f, tile.hitPoints);

                case TileDamageOutcome.Damaged:
                    // Location.as:2559 — the tile bit, so the decal is unblocked and throws debris.
                    return new TileHitDecision(
                        TileHitOutcome.Damaged, decalType, roomActive, false,
                        Ratio(amount, tile.hitPoints), tile.hitPoints);

                case TileDamageOutcome.RefusedNotDamageable:
                    // A ladder, catwalk or ramp. AS3 reaches this branch too, but its `phis >= 1`
                    // guard means it draws nothing.
                    return new TileHitDecision(
                        TileHitOutcome.Ignored, decalType, false, false, 0f, tile.hitPoints);

                default:
                    // Location.as:2565 — refused by `indestruct` or by `thre`, but a wall: a blocked
                    // decal is still stamped, which is why an indestructible bulkhead shows bullet
                    // marks and a ladder does not.
                    return new TileHitDecision(
                        TileHitOutcome.Refused, decalType, roomActive, true,
                        Ratio(amount, tile.hitPoints), tile.hitPoints, outcome);
            }
        }

        /// <summary>
        /// AS3 <c>phis == 1</c> — a plain solid, <b>not</b> a level-2 solid. The port stores a grate as
        /// <see cref="TilePhysicsType.Wall"/> plus <c>levelTwoSolid</c>, so the flag has to come off to
        /// answer the oracle's question.
        /// </summary>
        private static bool IsExactlyPhisOne(TileData tile)
        {
            return tile.physicsType == TilePhysicsType.Wall && !tile.levelTwoSolid;
        }

        /// <summary>
        /// <c>damage / hp</c> with the oracle's own guard: AS3 divides by zero into
        /// <c>Infinity</c>/<c>NaN</c> and then compares it, which silently answers "no" to every gate.
        /// Returning 0 here gives the same answers without the NaN leaking into a scale.
        /// </summary>
        private static float Ratio(int amount, int hitPoints)
        {
            return hitPoints <= 0 ? 0f : (float)amount / hitPoints;
        }
    }
}
