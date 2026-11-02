using PFE.Data.Definitions;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The Super Alicorn's weapon anchor — the port of <c>UnitBossAlicorn.setWeaponPos</c>
    /// (<c>fe/unit/UnitBossAlicorn.as:174-189</c>), which hangs all four weapons off a per-animation-frame
    /// table instead of the body.
    ///
    /// <para><b>The gap this closes.</b> The boss carries four weapons
    /// (<c>alilight2</c>, <c>aliblade</c>, <c>alipsy2</c>, <c>alimray</c>) and every one of them was
    /// mounted at <c>WeaponHoldRule.Base</c> — <c>Y - scY/2</c>, i.e. a flat waist height on a unit whose
    /// sprite is 170 px tall. The oracle puts the anchor at the <b>horn</b> (129 px above the feet, 41 px
    /// forward) and moves it every frame with the breathing and the wing beat, so a bolt, a thrown blade,
    /// a psy wave and the ray all left from one fixed point on the belly instead of from the head.</para>
    ///
    /// <para><b>It is a hold-point source, not a rule.</b> <see cref="WeaponHoldRule"/> takes the body,
    /// the facing and the aim — it has no animation frame in its signature and never can, because the
    /// frame is not a property of the body. <see cref="IWeaponHoldPointSource"/> is the seam the griffin
    /// already uses for exactly this ("where is the hand this frame"), so the alicorn answers the same
    /// question through the same interface and <see cref="EnemyWeaponMount"/> needed no change at
    /// all.</para>
    ///
    /// <para><b>No arm node, and that is the difference from the griffin.</b>
    /// <see cref="GriffinArmRig"/> draws a limb, rotates it with the aim and reads an <c>emit</c> bone;
    /// the alicorn has none of that — its override assigns <c>weaponX/weaponY</c> directly, so the
    /// weapon is simply drawn at that point. This class therefore has no children, no rotation and no
    /// <c>LateUpdate</c>: it is a lookup, evaluated when the mount asks.</para>
    ///
    /// <para><b><c>weaponR</c> is not ported, deliberately.</b> The table's third column reaches the
    /// drawn weapon only through <c>WClub.as:757</c> — a melee club — and this boss holds no club. See
    /// <see cref="AlicornBossWPosRules"/> for the full argument.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AlicornBossHoldPoint : MonoBehaviour, IWeaponHoldPointSource
    {
        /// <summary>The unit this anchor belongs to. Resolved once, at <see cref="Attach"/>.</summary>
        private UnitController _owner;

        /// <summary>
        /// The body's animator, resolved lazily — the same reason <see cref="GriffinArmRig"/> gives:
        /// the anchor is built by the controller, and the body's <c>Visual</c> child may not exist yet.
        /// </summary>
        private UnitAnimator _animator;

        /// <summary>
        /// Attach the anchor to a boss, or return null when it cannot work.
        ///
        /// <para><b>Idempotent.</b> <c>RoomUnitSpawner</c> calls <c>Initialize</c> twice for a unit with
        /// a placement (once with the row definition, once with the placement-adjusted one), so a second
        /// call must return the existing anchor rather than stack a second component —
        /// <see cref="DisallowMultipleComponent"/> would throw on the <c>AddComponent</c>, which is a
        /// loud failure inside a spawn loop.</para>
        /// </summary>
        public static AlicornBossHoldPoint Attach(UnitController owner)
        {
            if (owner == null)
            {
                return null;
            }

            AlicornBossHoldPoint existing = owner.GetComponent<AlicornBossHoldPoint>();
            if (existing != null)
            {
                return existing;
            }

            return owner.gameObject.AddComponent<AlicornBossHoldPoint>().Bind(owner);
        }

        private AlicornBossHoldPoint Bind(UnitController owner)
        {
            _owner = owner;
            return this;
        }

        /// <summary>
        /// The anchor for the current animation cell — AS3's <c>try</c> branch
        /// (<c>UnitBossAlicorn.as:180-183</c>), or <c>false</c> for its <c>catch</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The arithmetic is the oracle's, term for term:</b></para>
        /// <code>
        /// weaponX = X + (obj.x + visBmp.x) * storona;
        /// weaponY = Y +  obj.y + visBmp.y;
        /// </code>
        /// <para><see cref="GriffinArmMath.ArmOffsetPixels"/> is that same pair of lines minus the
        /// mirror (<c>UnitMerc.as:229-230</c>), and it already applies the <c>visBmp</c> rule and the one
        /// y flip, so it is called rather than re-derived. What it returns is a <b>local</b> offset with
        /// no facing applied, because the griffin's arm inherits the mirror from the unit root's
        /// <c>localScale.x</c>. A world point does not, so <c>storona</c> is applied here — exactly as
        /// <see cref="WeaponHoldPointMath"/>'s <c>Zombie</c> arm applies its own <c>storona * 30</c>.</para>
        ///
        /// <para><b>Y needs no mirror.</b> A horizontal flip does not touch the vertical, and the sign
        /// was already flipped once inside <c>ArmOffsetPixels</c> (AS3 y runs down, room y runs up), so
        /// the offset is added straight to the feet.</para>
        ///
        /// <para><b>False, not a fallback position.</b> A cell outside the table, no animator, or no
        /// definition all answer <c>false</c>, and <c>EnemyWeaponMount.ResolveHoldPoint</c> then takes
        /// its own <c>HoldRule</c> path. Returning a plausible-looking point here would silently win
        /// over that path with a number nobody authored.</para>
        /// </remarks>
        public bool TryResolveHoldPoint(out Vector2 worldPoint)
        {
            worldPoint = default;

            if (_owner == null)
            {
                return false;
            }

            UnitDefinition definition = _owner.Stats;
            UnitAnimator animator = Animator;
            if (definition == null || animator == null)
            {
                return false;
            }

            if (!AlicornBossWPosRules.TryResolveCell(
                    animator.CurrentCellIndex, definition.spriteSheetColumns, out Vector2Int wPos))
            {
                return false;
            }

            Vector2 offsetPixels = GriffinArmMath.ArmOffsetPixels(
                wPos, definition.registrationPoint, definition.spriteDimensions);

            float facing = _owner.FacingDirection >= 0 ? 1f : -1f;
            Vector2 offset = WorldCoordinates.PixelToUnity(offsetPixels);

            worldPoint = new Vector2(
                _owner.transform.position.x + offset.x * facing,
                _owner.FeetWorldY + offset.y);
            return true;
        }

        /// <summary>
        /// Nothing to bind — the anchor does not depend on the weapon.
        ///
        /// <para>Implemented because <see cref="IWeaponHoldPointSource"/> requires it: the griffin needs
        /// the weapon's <c>rot</c> to pose its arm, and the interface carries that for the one
        /// implementation that does. A boss alicorn's anchor is a function of the animation alone, so
        /// the oracle reads nothing off <c>currentWeapon</c> here either — its override touches
        /// <c>weaponX/Y/R</c> only as assignment targets.</para>
        /// </summary>
        public void BindWeapon(WeaponRuntimeState weaponState)
        {
        }

        /// <summary>
        /// Nothing to pose — the aim moves the <b>rotation</b> of the weapon, never its origin.
        ///
        /// <para>AS3 agrees: <c>setWeaponPos</c> runs on the unit's own frame and writes the anchor from
        /// <c>animState</c>, while the aim is consumed by <c>Weapon.actions()</c> for <c>rot</c>. No
        /// alicorn anchor entry is a function of <c>celX</c>.</para>
        /// </summary>
        public void SetAimWorldPoint(Vector2 worldPoint)
        {
        }

        /// <summary>
        /// The body's animator, found on first use. Re-searched while null, because the anchor can be
        /// attached before the <c>Visual</c> child exists.
        /// </summary>
        private UnitAnimator Animator
        {
            get
            {
                if (_animator == null && _owner != null)
                {
                    _animator = _owner.GetComponentInChildren<UnitAnimator>(true);
                }

                return _animator;
            }
        }
    }
}
