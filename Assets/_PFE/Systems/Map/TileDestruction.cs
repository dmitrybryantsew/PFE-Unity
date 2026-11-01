using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Map
{
    /// <summary>
    /// What one weapon hit did to one tile — the outcome side of AS3 <c>Tile.udar()</c>.
    ///
    /// <para>The four refusal reasons are kept apart on purpose. Three of them are authored
    /// (<c>phis == 0</c>, <c>indestruct</c>, <c>thre</c>) and one is the caller's own mistake (a hit
    /// carrying no destroy value at all); collapsing them into a single <c>false</c> is what made
    /// "the minigun will not open this door" indistinguishable from "tile destruction is broken".</para>
    /// </summary>
    public enum TileDamageOutcome
    {
        /// <summary><c>phis == 0</c> — a ladder, catwalk or ramp. Scenery; the oracle never destroys it.</summary>
        RefusedNotDamageable,

        /// <summary>The hit carries no tile damage (a weapon whose <c>destroy</c> is 0).</summary>
        RefusedNoDamage,

        /// <summary>The form is authored <c>indestruct</c>.</summary>
        RefusedIndestructible,

        /// <summary>The hit is below this form's <c>thre</c>. Dropped entirely — no chip damage.</summary>
        RefusedThreshold,

        /// <summary>Damage landed; the tile is chipped but standing.</summary>
        Damaged,

        /// <summary>Damage landed and <c>hp</c> reached 0. The caller must run the die sequence.</summary>
        Destroyed,
    }

    /// <summary>
    /// The single implementation of tile destruction — AS3 <c>Tile.udar(param1:int)</c>.
    ///
    /// <para><b>Why this exists as its own type.</b> The rule was implemented twice:
    /// <c>TileCollider.DamageThisTile</c> and <c>TileCollisionSystem.ApplyDamage</c> each carried their
    /// own copy of the gate order, and they had already drifted — one refused <c>damage &lt;= 0</c> and the
    /// other reported "damaged" for a zero-damage hit, one rounded the float and the other could not.
    /// Two copies of an oracle rule is how "bullets stopped breaking ladders" gets in, and the drift was
    /// invisible because only one of the two is reachable in play. Both callers now delegate here, so
    /// the rule has exactly one body and the divergence fixture has exactly one thing to pin.</para>
    ///
    /// <para><b>What it does NOT do.</b> It does not touch the room, the renderer or any component. AS3's
    /// <c>udar</c> is pure arithmetic on the tile's own fields; the die sequence (<c>Tile.die()</c>:
    /// drop the collider, tell the visual manager, notify the grid) belongs to whoever owns those
    /// objects, and the two callers own different ones. Keeping the side effects out is what lets the
    /// same rule serve a tile GameObject and a plain <see cref="TileData"/> in a test.</para>
    ///
    /// <para><b>The gate order is the oracle's, and it is load-bearing.</b>
    /// <c>Tile.as:340-348</c> reads <c>if (indestruct || thre &gt; param1) return false; hp -= param1;</c> —
    /// so an indestructible tile is refused before the threshold is even consulted, and damage below
    /// <c>thre</c> is dropped <i>entirely</i>: there is no chip damage and nothing accumulates. A weapon
    /// carrying <c>destroy = 10</c> can therefore never open a tile whose <c>thre</c> is 50, however long
    /// the trigger is held. That reads exactly like a broken weapon, which is why
    /// <see cref="TileDamageOutcome.RefusedThreshold"/> is a distinct answer rather than a <c>false</c>.
    /// </para>
    ///
    /// <para><b>The <c>phis &gt;= 1</c> gate in front.</b> <c>Location.hitTile()</c> guards every
    /// destruction branch on it (<c>Location.as:2525, 2538, 2557, 2565</c>), so a ladder (<c>stair</c>),
    /// a catwalk (<c>shelf</c>) and a ramp (<c>diagon</c>) are never destroyed no matter what <c>hp</c>
    /// their form declares. The port encodes that as <see cref="TileData.IsDamageable"/>. Without it an
    /// explosion could shoot out the catwalk it was standing on.</para>
    /// </summary>
    public static class TileDestruction
    {
        /// <summary>
        /// Applies one hit to one tile, exactly as <c>Tile.udar</c> does, and says what happened.
        ///
        /// <para>Damage is applied in full when it is applied at all: AS3 subtracts <c>param1</c>, not a
        /// capped or resisted fraction, and <c>Tile.mat</c> is not consulted on this path. There is
        /// deliberately no <see cref="DamageType"/> parameter — the oracle's <c>udar</c> takes an int and
        /// nothing else, and threading a type through would invite a modifier that does not exist.</para>
        /// </summary>
        public static TileDamageOutcome Apply(TileData tile, float destroyAmount)
        {
            if (tile == null || !tile.IsDamageable())
            {
                return TileDamageOutcome.RefusedNotDamageable;
            }

            // AS3 udar() takes an int. Rounded here rather than at each call site so a float destroy
            // value cannot mean two different numbers on two paths.
            int damage = Mathf.RoundToInt(destroyAmount);

            if (damage <= 0)
            {
                return TileDamageOutcome.RefusedNoDamage;
            }

            // TakeDamage owns the oracle's head (`indestruct || thre > param1`); the outcome only
            // names which of the two refused, so the rule is still written once.
            if (!tile.TakeDamage(damage))
            {
                return tile.indestructible
                    ? TileDamageOutcome.RefusedIndestructible
                    : TileDamageOutcome.RefusedThreshold;
            }

            return tile.IsDestroyed()
                ? TileDamageOutcome.Destroyed
                : TileDamageOutcome.Damaged;
        }

        /// <summary>True when the hit changed nothing — the four refusal reasons, and nothing else.</summary>
        public static bool WasRefused(TileDamageOutcome outcome)
        {
            switch (outcome)
            {
                case TileDamageOutcome.RefusedNotDamageable:
                case TileDamageOutcome.RefusedNoDamage:
                case TileDamageOutcome.RefusedIndestructible:
                case TileDamageOutcome.RefusedThreshold:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A one-line reason for a refusal, for the owner-facing diagnostic. Never null for a refusal
        /// and never meaningful for a hit — <see cref="WasRefused"/> is the gate, not this.
        /// </summary>
        public static string Describe(TileDamageOutcome outcome, TileData tile, float destroyAmount)
        {
            switch (outcome)
            {
                case TileDamageOutcome.RefusedNotDamageable:
                    return tile == null
                        ? "no tile at that coordinate"
                        : $"phis is 0 (physicsType {tile.physicsType}) — a ladder, catwalk or ramp is scenery";

                case TileDamageOutcome.RefusedNoDamage:
                    return "the hit carries no tile damage at all (weapon `destroy` is 0)";

                case TileDamageOutcome.RefusedIndestructible:
                    return "the tile is authored indestructible";

                case TileDamageOutcome.RefusedThreshold:
                    return $"thre {tile.damageThreshold} > {Mathf.RoundToInt(destroyAmount)} — " +
                           "the hit is below this form's threshold";

                default:
                    return outcome.ToString();
            }
        }
    }
}
