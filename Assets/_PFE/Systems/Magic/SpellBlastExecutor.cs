using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Security;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// The Unity half of AS3 <c>Spell.cast_blast</c> (<c>fe/unit/Spell.as:371-418</c>) — walk the room's
    /// units, decide who the pulse reaches, and write the velocity.
    ///
    /// <para><b>Why this is a separate file from <see cref="SpellBlastMath"/>.</b> The formula is pure
    /// and pinned offline; this is the loop, and the loop needs <c>UnitController</c>. Keeping them apart
    /// means the part that can be proved is proved, and the part that cannot is small enough to read in
    /// one screen.</para>
    ///
    /// <para><b>Two callers, one implementation.</b> The <c>tr1</c> alicorn's reactive blast
    /// (<c>UnitAlicorn.as:419</c>) and a player who bought <c>sp_blast</c> from the mage vendor both run
    /// <c>cast_blast</c> in the oracle, and they differ in exactly one line —
    /// <c>if(this.owner.player) this.loc.budilo(X, Y, 500)</c>. That difference is the
    /// <c>alertRoom</c> parameter.</para>
    /// </summary>
    public static class SpellBlastExecutor
    {
        /// <summary>
        /// Run one pulse, centred on <paramref name="caster"/>'s <b>feet</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>The centre is the feet, not the horn.</b> <c>cast_blast</c> opens with
        /// <c>this.X = this.owner.X; this.Y = this.owner.Y;</c> (<c>:382-383</c>), which <i>overwrites</i>
        /// the <c>magicX</c>/<c>magicY</c> that <see cref="Spell.Cast"/> resolved a few lines earlier.
        /// Centring on the horn instead would lift the whole pulse by roughly half a body height and
        /// change which units are in range — most visibly, a unit standing under the caster.</para>
        ///
        /// <para><b>Not modelled, deliberately and visibly:</b></para>
        /// <list type="bullet">
        /// <item><description><c>u.stun += floor(random * power * dam)</c> (<c>:401</c>) — the port's
        /// <c>UnitController</c> has no <c>stun</c> field. Its <c>shok</c> equivalent lives on the enemy
        /// blackboard and is a different counter (a contact-damage multiplier, not a control lock), so
        /// writing it from here would be a different mechanic under the same name.</description></item>
        /// <item><description><c>u.t_throw = 30</c> (<c>:402</c>) — no port field. The velocity is what
        /// throws the unit; the 30-tick airborne window is the animation and collision-mode half.</description></item>
        /// <item><description><c>Emitter.emit("blast", …)</c> and <c>World.w.quake(…)</c>
        /// (<c>:410-417</c>) — presentation, and the port has no emitter for a bare unit effect. The
        /// quake is camera shake, which the port also has no channel for.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="caster">The unit the pulse is centred on; its room is the unit list.</param>
        /// <param name="radius">The row's <c>rad</c> — 500 for <c>sp_blast</c>.</param>
        /// <param name="damage">The row's <c>dam</c> — the <b>impulse ceiling</b>, not HP damage.</param>
        /// <param name="power">The caster's <c>spellPower</c>.</param>
        /// <param name="alertRoom"><c>this.owner.player</c> — shout to the room as well.</param>
        /// <returns>How many units the pulse actually moved.</returns>
        public static int Execute(UnitController caster, float radius, float damage, float power, bool alertRoom)
        {
            if (caster == null || radius <= 0f) return 0;

            RoomInstance room = caster.Room;
            if (room == null)
            {
                // AS3 iterates `this.loc.units` and returns immediately when `loc == null` (:378-381).
                // The port's room is the same list, so a caster with no room has nothing to affect.
                return 0;
            }

            Vector2 centrePx = (Vector2)caster.transform.position * TileQueryConstants.UnitToPixel;
            FactionType casterFaction = caster.Faction;

            int moved = 0;
            UnitController[] units = Object.FindObjectsByType<UnitController>(FindObjectsSortMode.None);
            for (int i = 0; i < units.Length; i++)
            {
                UnitController target = units[i];
                if (target == null) continue;

                // `_loc1_.Y - _loc1_.scY / 2` — the target's CENTRE. The port's Y runs up and the
                // origin is the feet, so the centre is feet + half the body; AS3 subtracts because its
                // Y runs down. Same point, mirrored.
                Vector2 targetFeetPx = (Vector2)target.transform.position * TileQueryConstants.UnitToPixel;
                Vector2 targetCentrePx = targetFeetPx + new Vector2(0f, target.SpriteSizePixels.y * 0.5f);

                Vector2 delta = targetCentrePx - centrePx;
                float distanceSq = delta.sqrMagnitude;

                bool eligible = SpellBlastMath.IsEligibleTarget(
                    isFixed: target.IsFixed,
                    sameFaction: target.Faction == casterFaction,
                    sameRoom: target.Room == room,
                    // `sost != 4`. The port has no unravel state; a corpse is `sost == 3` and IS a legal
                    // target in the oracle, so this must not be "not alive".
                    isUnraveled: false,
                    isSelf: target == caster);

                if (!eligible || !SpellBlastMath.IsInRadius(distanceSq, radius)) continue;

                float distance = Mathf.Sqrt(distanceSq);
                float impulse = SpellBlastMath.Impulse(
                    damage, power, distance, radius,
                    UnityEngine.Random.value,
                    target.Knocked,
                    target.Mass);

                SpellBlastMath.Velocity(delta.x, delta.y, distance, impulse, out float vx, out float vy);
                if (vx == 0f && vy == 0f) continue;

                // `_loc1_.dx = …; _loc1_.dy = …` — an ASSIGNMENT. A unit moving toward the caster is
                // stopped and reversed, not accelerated.
                target.SetVelocityPixelsPerFrame(new Vector2(vx, vy));
                moved++;
            }

            if (alertRoom && moved > 0)
            {
                // `if(this.owner.player) this.loc.budilo(this.X, this.Y, 500)` (:406-409). Radius 500
                // here, not the alarm pulse's 1500 — a blast is heard across a room, not across the land.
                SecurityRuntime.AlertRoom(room, centrePx, BlastNoiseRadiusPixels);
            }

            return moved;
        }

        /// <summary>The <c>500</c> in <c>loc.budilo(X, Y, 500)</c> (<c>Spell.as:408</c>).</summary>
        public const float BlastNoiseRadiusPixels = 500f;
    }
}
