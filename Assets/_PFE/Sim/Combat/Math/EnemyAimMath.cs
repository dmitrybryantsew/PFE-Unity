using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The point an enemy points its weapon at — AS3's <c>celX</c>/<c>celY</c>.
    ///
    /// <para><b>One field, two readers.</b> In the oracle <c>celX</c>/<c>celY</c> is a field on the
    /// <i>unit</i> (<c>Unit.as:364-366</c>). The AI uses it as a movement goal and the weapon uses the
    /// same two numbers to compute its barrel angle
    /// (<c>Weapon.as:1074-1086</c>: <c>rot2 = Math.atan2(owner.celY - Y, owner.celX - X)</c>). The
    /// port split the field in two — the blackboard's <c>TargetDeltaX/Y</c> for the AI, and
    /// <see cref="EnemyAimMath"/> + <c>EnemyWeaponMount.SetAimTarget</c> for the weapon — so this class
    /// is the weapon half and must produce exactly what <c>setCel</c> wrote.</para>
    ///
    /// <para><b>Why a static of plain numbers.</b> The defect this exists to end is that each brain
    /// re-derived the aim point by hand and four of them aimed at
    /// <c>target.transform.position</c> — a ported unit's <i>feet</i> (<c>Unit.as:1875</c>:
    /// <c>Y1 = Y - scY; Y2 = Y</c>) — instead of the mid-body point the oracle uses. A shot fired at
    /// the feet travels into the floor tile the target stands on, which is the reported "the alicorn
    /// boss never hits me" and the earlier "turrets struggle to hit the player, something must be
    /// eating them". A brain cannot be driven from an offline fixture (<c>MonoBehaviour</c>), so the
    /// formula lives here where it can, and the brains keep only the wiring.</para>
    ///
    /// <para><b>Coordinates.</b> Pixels, room-local or world — the functions are pure offsets on a
    /// feet-anchored origin, so either space works as long as the caller is consistent. The port's Y
    /// grows <i>upward</i> while AS3's grows down, so every oracle <c>Y - k</c> becomes
    /// <c>feetY + k</c> here. That mirror is the single most common way to get this wrong.</para>
    /// </summary>
    public static class EnemyAimMath
    {
        /// <summary>
        /// AS3 <c>Unit.setCel(param1)</c> — the aim point when a unit has a live target
        /// (<c>Unit.as:4705-4710</c>):
        /// <code>
        /// celX = param1.X + param1.scX / 4 * param1.storona;
        /// celY = param1.Y - param1.scY / 2;
        /// </code>
        ///
        /// <para><b>Half a body up, a quarter body forward.</b> <c>celY</c> is the vertical centre of
        /// the target's body box, not its feet; <c>celX</c> is biased a quarter of the target's width
        /// toward the side it is <i>facing</i> (<c>storona</c>, not its velocity), so a target walking
        /// away is aimed slightly behind its centre.</para>
        ///
        /// <para><b>The lead is not cosmetic.</b> For the raider family <c>celX</c> is also the
        /// movement goal and <c>celDX</c> is compared against the 100/200 px thresholds
        /// (<c>UnitRaider.as:686-700</c>), so a quarter-width bias on a 120 px boss moves a real
        /// decision boundary.</para>
        /// </summary>
        /// <param name="targetFeetPx">The target's origin — AS3 <c>X</c>/<c>Y</c>, the bottom-centre of
        /// its body box.</param>
        /// <param name="targetSizePx">AS3 <c>scX</c>/<c>scY</c> — <c>UnitController.SpriteSizePixels</c>.</param>
        /// <param name="targetFacing">AS3 <c>storona</c>: <c>+1</c> right, <c>-1</c> left. The oracle's
        /// default is <c>+1</c> (<c>Obj.as:24</c>), so <c>0</c> must read as right, not as no bias.</param>
        public static Vector2 TargetCel(Vector2 targetFeetPx, Vector2 targetSizePx, int targetFacing)
        {
            int facing = targetFacing >= 0 ? 1 : -1;

            return new Vector2(
                targetFeetPx.x + targetSizePx.x * 0.25f * facing,
                targetFeetPx.y + targetSizePx.y * 0.5f);
        }

        /// <summary>
        /// The <b>same point without the lead</b> — AS3 <c>UnitPet.setCel</c>
        /// (<c>UnitPet.as:815-816</c>):
        /// <code>
        /// celX = celUnit.X;
        /// celY = celUnit.Y - celUnit.scY / 2;
        /// </code>
        ///
        /// <para><b>Pets are the one exception, and it is the oracle's, not a shortcut.</b> A pet
        /// overrides <c>setCel</c> outright and writes the target's horizontal centre with no
        /// <c>scX/4</c> term, so routing a pet through <see cref="TargetCel"/> would make it aim a
        /// quarter body ahead of its own master.</para>
        /// </summary>
        public static Vector2 TargetCentre(Vector2 targetFeetPx, Vector2 targetSizePx)
            => new Vector2(targetFeetPx.x, targetFeetPx.y + targetSizePx.y * 0.5f);

        /// <summary>
        /// The <c>cel</c> a unit with <b>no target</b> writes for itself — the "look ahead" point, e.g.
        /// <c>UnitRaider.as:682-683</c>, <c>UnitAlicorn.as:724-725</c>, <c>UnitZombie.as:570-571</c>,
        /// <c>UnitAIRobot.as:175-176</c>, and every <c>UnitBoss*</c>:
        /// <code>
        /// celY = Y - scY;            // the TOP of its own body, not its feet
        /// celX = X + scX * storona * 2;
        /// </code>
        ///
        /// <para>Two body widths forward at head height. This is what an idle armed unit points its
        /// barrel at, and it is why <c>EnemyWeaponMount</c>'s facing-only fallback tilted every idle
        /// gun down at the floor.</para>
        /// </summary>
        public static Vector2 AheadCel(Vector2 selfFeetPx, Vector2 selfSizePx, int selfFacing)
        {
            int facing = selfFacing >= 0 ? 1 : -1;

            return new Vector2(
                selfFeetPx.x + selfSizePx.x * 2f * facing,
                selfFeetPx.y + selfSizePx.y);
        }
    }
}
