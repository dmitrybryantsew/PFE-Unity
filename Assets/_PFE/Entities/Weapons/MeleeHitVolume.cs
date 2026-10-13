using UnityEngine;
using VContainer;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using MessagePipe;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Trigger collider that moves between two world positions each flash frame
    /// during a melee weapon's strike window.
    ///
    /// MeleeWeaponController calls BindMove(prevTip, currTip) each flash frame
    /// while the strike is active. This moves the collider to the midpoint and
    /// scales it to cover the swept arc, so Unity trigger detection fires for
    /// anything the blade passes through.
    ///
    /// Outside the strike window SetActive(false) disables the collider entirely.
    ///
    /// On trigger enter: reads DamageContext from the controller's last ShotPlan
    /// and reports the hit to DamageSystem — same path as projectile hits.
    ///
    /// Setup:
    ///   1. Add to a child GameObject of the player's weapon object.
    ///   2. Add a CapsuleCollider2D (trigger, small size — BindMove resizes it).
    ///   3. MeleeWeaponController.HitVolume = this  (assigned by PlayerWeaponLoadout).
    ///
    /// AS3 equivalent: the vzz array sweep in WClub.actions() that updates hit
    /// points from mindlina to dlina along the weapon arc each frame.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider2D))]
    public sealed class MeleeHitVolume : MonoBehaviour, IMeleeHitVolume
    {
        // ── State ─────────────────────────────────────────────────────────────

        // Current damage context — set by PlayerWeaponLoadout from the latest ShotPlan.
        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        /// <summary>
        /// AS3's <c>Bullet.tilehit</c> (<c>Bullet.as:73</c>). Set on the first tile the blade enters,
        /// and read by the tile branch's own guard (<c>:476</c>) — so a swing damages <b>one</b> tile
        /// and then stops looking, however many tiles the blade passes through afterwards.
        ///
        /// <para>Cleared on the hit volume's enable edge (<see cref="SetActive"/>), which is the
        /// port's stand-in for AS3's <c>WClub.shoot()</c> reset (<c>WClub.as:596</c>) — one clear per
        /// strike window, i.e. one per swing.</para>
        /// </summary>
        private bool _tileHitThisSwing;

        private CapsuleCollider2D _collider;
        private bool _isActive;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private PFE.Systems.Combat.DamageSystem _damageSystem;
#pragma warning restore CS0649

        // ── Owner ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The unit swinging this volume, resolved from the hierarchy — the volume is a child of the
        /// wielder's weapon object on both paths (<c>PlayerWeaponLoadout</c>'s scene child, and the
        /// child <c>EnemyWeaponMount</c> builds).
        /// </summary>
        private UnitController _ownerUnit;

        /// <summary>Whether the hierarchy has been walked. Cached, because a miss would otherwise re-walk every trigger.</summary>
        private bool _ownerResolved;

        // ── Initialization ────────────────────────────────────────────────────

        private void Awake()
        {
            // Before SetActive(false): the owner walk is a hierarchy query, and it is clearer to do it
            // while this object is still active.
            ResolveOwner();

            _collider = GetComponent<CapsuleCollider2D>();
            _collider.isTrigger = true;
            SetActive(false);
        }

        private void ResolveOwner()
        {
            if (_ownerResolved) return;
            _ownerResolved = true;
            _ownerUnit = GetComponentInParent<UnitController>();
        }

        /// <summary>
        /// Update the damage payload for this hit. Call from PlayerWeaponLoadout / EnemyWeaponMount
        /// whenever a MeleeSweep ShotPlan is received.
        ///
        /// <para><b>This is a payload write, not a swing boundary.</b> It used to clear
        /// <see cref="_tileHitThisSwing"/> too, reasoning that a swing emits exactly one plan. That
        /// stopped being true when the power charge landed: AS3 rewrites the bullet's damage on every
        /// charging frame (<c>WClub.as:352</c>), so the controller re-arms the plan once on the
        /// release frame — and a clear arriving then would let a single swing break <i>two</i> tiles.
        /// The latch is cleared by <see cref="SetActive"/> now (the window-open edge, which really is
        /// one per swing), so this method can be called any number of times inside one swing.</para>
        /// </summary>
        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
        }

        // ── BindMove API ──────────────────────────────────────────────────────

        /// <summary>
        /// Move the hit volume to sweep between prevTip and currTip.
        /// AS3 equivalent: updating vzz[] positions from mindlina to dlina each frame.
        ///
        /// Positions the collider at the midpoint and sets its size to cover the arc.
        /// </summary>
        public void BindMove(Vector2 prevTip, Vector2 currTip)
        {
            Vector2 mid    = (prevTip + currTip) * 0.5f;
            float   length = Vector2.Distance(prevTip, currTip);
            float   angle  = Mathf.Atan2(currTip.y - prevTip.y, currTip.x - prevTip.x) * Mathf.Rad2Deg;

            transform.position = new Vector3(mid.x, mid.y, transform.position.z);
            transform.rotation = Quaternion.Euler(0f, 0f, angle);

            // Resize capsule to span the swept distance, minimum width 0.15 units.
            _collider.size      = new Vector2(Mathf.Max(length, 0.15f), 0.2f);
            _collider.direction = CapsuleDirection2D.Horizontal;

            // Tiles are resolved separately: a tile is not an IDamageable, so the trigger above can
            // never see one. See SweepTilesForImpact.
            SweepTilesForImpact(prevTip, currTip);
        }

        // ── Tile destruction ──────────────────────────────────────────────────

        /// <summary>
        /// AS3's <b>bullet-versus-tile</b> half of the swing — the part that makes a club, sword or
        /// sledge break the wall it is swung into.
        ///
        /// <para><b>The divergence this closes.</b> In AS3 a melee swing <i>is</i> a bullet
        /// (<c>WClub.as:148-156</c>), and <c>Bullet.run()</c> tests the bullet's point against the tile
        /// under it on every sub-step (<c>Bullet.as:432</c>, <c>:476-493</c>). On the first tile it
        /// overlaps it calls <c>loc.hitTile(tile, this.destroy, X, Y, this.tipDecal)</c> — which is
        /// <c>Tile.udar(destroy)</c>, the same damage the projectile path applies. The port mapped the
        /// swing onto a <b>trigger capsule</b> instead, and a trigger capsule only ever reports
        /// <see cref="IDamageable"/>: tiles implement <see cref="IDestructibleTile"/> and not
        /// <c>IDamageable</c>, so <b>no melee or unarmed hit in the port had ever destroyed a tile</b>.
        /// The damage amount was already being computed and carried here — <c>DamageContext.DestroyTiles</c>
        /// is imported from <c>&lt;char destroy&gt;</c> and rode the melee plan the whole time; only the
        /// consumer was missing.</para>
        ///
        /// <para><b>Why the sweep is the segment and not the capsule.</b> The capsule is a fat
        /// approximation of the arc, sized to cover the frame's motion. AS3 asks a <i>point</i>
        /// question about where the blade tip is. Sub-stepping the tip's own path
        /// (<see cref="MeleeTileSweepMath"/>) is the oracle's geometry, and it is what makes "the first
        /// tile entered" mean the same thing on both sides. A capsule overlap would report every tile
        /// the arc grazed, in an order Unity does not guarantee.</para>
        ///
        /// <para><b>One tile per swing</b>, via <see cref="_tileHitThisSwing"/> — AS3's
        /// <c>tilehit</c> latch.</para>
        /// </summary>
        private void SweepTilesForImpact(Vector2 prevTip, Vector2 currTip)
        {
            if (_tileHitThisSwing) return;
            if (!_hasDamageContext) return;

            // AS3's `run()` is in AS3 pixels; the tips arrive in world units.
            Vector2 prevPx  = prevTip * TileQueryConstants.UnitToPixel;
            Vector2 deltaPx = (currTip - prevTip) * TileQueryConstants.UnitToPixel;

            if (!MeleeTileSweepMath.TryFirstTileHit(
                    prevPx.x, prevPx.y, deltaPx.x, deltaPx.y,
                    IsTileAtPixel, out float hitPx, out float hitPy))
                return;

            // Latch BEFORE applying: AS3 sets `tilehit = true` (Bullet.as:491) in the same block that
            // calls hitTile, so a tile that refuses the damage (indestructible, or below its `thre`)
            // still consumes the swing's one tile — which is exactly the oracle's behaviour, and the
            // reason this is not inside the `destructible != null` guard.
            _tileHitThisSwing = true;

            Collider2D destructibleCollider = DestructibleColliderAtPixel(hitPx, hitPy);
            if (destructibleCollider == null) return;

            var destructible = destructibleCollider.GetComponent<IDestructibleTile>();
            if (destructible == null) return;

            Vector2 hitWorld = new Vector2(hitPx, hitPy) * TileQueryConstants.PixelToUnit;

            // TileCollider.ApplyDestruction is AS3 Tile.udar(): `if (indestruct || thre > param1)
            // return false; hp -= param1`. It already owns the `phis >= 1` gate and the destroy
            // sequence, so routing through it keeps melee and projectiles on ONE destruction path
            // rather than two that can drift.
            destructible.ApplyDestruction(hitWorld, _damageContext.DestroyTiles, _damageContext.DamageType);
        }

        /// <summary>
        /// "Is a solid surface here?" — the predicate <see cref="MeleeTileSweepMath"/> sub-steps over.
        /// AS3 asks <c>loc.getAbsTile(X, Y)</c> and then tests <c>phis == 1 || phis == 2</c> with the
        /// point inside the tile's physical bounds (<c>Bullet.as:476</c>).
        ///
        /// <para><b>A door box answers this too, and that is the oracle's own answer.</b> A closed
        /// door's cells <i>are</i> <c>phis == 1</c> in AS3 — <c>Box.initDoor</c> put them there
        /// (<c>Box.as:666</c>) — so the swing stops on the door's tile and damages it. The port has no
        /// collider on those cells (they are <c>_</c> in the shipped room maps; see
        /// <c>DoorPropPresenter._solidCollider</c>), so the door box's own surface stands in for them.
        /// Without that the swing passed straight through a closed door, exactly as a bullet did.</para>
        /// </summary>
        private static bool IsTileAtPixel(float px, float py) => DestructibleColliderAtPixel(px, py) != null;

        /// <summary>
        /// The destructible surface at a world-pixel position, identified by component rather than by
        /// layer — the same idiom (and the same reason) as <c>Projectile.FindTileCollider</c>: the tile
        /// layers are private constants inside <see cref="TileCollider"/>, and a probe that silently
        /// stopped matching if they were renumbered would present as "melee no longer breaks tiles".
        ///
        /// <para><b>Two components qualify, because the port has two things that are a tile's solid
        /// surface.</b> A real tile carries <see cref="TileCollider"/>; an authored door or barricade
        /// box carries a solid <c>BoxCollider2D</c> on a <c>DoorPropPresenter</c>, which is the
        /// <see cref="IDestructibleTile"/> for the cells AS3 stamped <c>phis = 1</c> and the port
        /// cannot (see <c>DoorPropPresenter._solidCollider</c>). Testing the interface rather than
        /// either concrete type is what keeps a door and a wall on one path — the same reason
        /// <c>Projectile.HandleImpact</c> resolves its target by interface.</para>
        ///
        /// <para>The probe is a 2 px box rather than a true point. <c>Physics2D</c> has no point-overlap
        /// query that is both non-allocating and not deprecated on this engine version, and 2 px is well
        /// inside a 40 px tile — AS3's own test is a point against bounds it has already resolved.</para>
        /// </summary>
        private static Collider2D DestructibleColliderAtPixel(float px, float py)
        {
            var centre = new Vector2(
                px * TileQueryConstants.PixelToUnit,
                py * TileQueryConstants.PixelToUnit);

            Collider2D[] hits = Physics2D.OverlapBoxAll(centre, new Vector2(0.02f, 0.02f), 0f);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;
                if (hit.GetComponent<TileCollider>() == null &&
                    hit.GetComponent<IDestructibleTile>() == null) continue;
                return hit;
            }

            return null;
        }

        /// <summary>
        /// Enable or disable the hit volume.
        ///
        /// <para><b>The enable edge is where AS3's <c>b.tilehit = false</c> lives in the port.</b> AS3
        /// clears the latch in <c>shoot()</c> (<c>WClub.as:596</c>), once per attack, when the bullet is
        /// armed. The port clears it here instead because the enable edge is exactly "the blade starts
        /// a new pass", and the two are observationally identical: the latch can only ever be set from
        /// <see cref="SweepTilesForImpact"/>, which only runs from <see cref="BindMove"/>, which the
        /// controller only calls inside an open strike window — so nothing can reach a tile between
        /// attack start and the window opening. Clearing here rather than on the plan is also what
        /// makes the power charge's mid-swing plan re-arm safe (see
        /// <see cref="SetDamageContext"/>).</para>
        /// </summary>
        public void SetActive(bool active)
        {
            if (active) _tileHitThisSwing = false;

            _isActive              = active;
            _collider.enabled      = active;
            gameObject.SetActive(active);
        }

        // ── Trigger detection ─────────────────────────────────────────────────

        /// <summary>
        /// Applies melee damage to anything the volume overlaps, <b>except units of the wielder's own
        /// faction</b>.
        ///
        /// <para><b>The faction filter is the oracle's, and this class used to say the opposite.</b>
        /// The old comment reasoned from <c>Unit.udarUnit</c> (<c>Unit.as:4125-4167</c>) and its caller
        /// <c>Unit.udar</c> (<c>:3277</c>) — both of which really do apply damage with no <c>fraction</c>
        /// test — and concluded that melee is faction-blind in AS3. But those are the <i>pair-level</i>
        /// functions; the thing that decides <i>which pairs are tested at all</i> is the loop in
        /// <c>Bullet.run()</c>, and melee goes through it: <c>WClub.as:161-162</c> sets
        /// <c>checkLine = true; b.checkLine = checkLine;</c> on its bullet, and the very next thing that
        /// loop does (<c>Bullet.as:515</c>) is</para>
        /// <code>
        /// if((this.targetObj || _loc2_.fraction != this.owner.fraction) &amp;&amp; X &gt;= _loc2_.X1 &amp;&amp; …)
        /// </code>
        /// <para>A swing carries no <c>targetObj</c>, so the fraction test is the whole rule — the same
        /// predicate <see cref="FactionRule.CanHitDirectly"/> already encodes for projectiles. Confirmed
        /// by <c>WClub.as:517</c>'s <c>this.weap.isLine(X,Y)</c>, which only a weapon (never a plain
        /// bullet) has.</para>
        ///
        /// <para><b>What the missing filter cost.</b> Two things, and the second is why this was fixed
        /// now: a player's swing damaged friendly NPCs, and — once enemy melee was enabled, since every
        /// ArmedShooter that rolls a <c>tip == 1</c> weapon now swings one — <b>an enemy damaged itself
        /// and its allies</b>. AS3's <c>weaponX = X; weaponY = Y - scY * 0.5</c> puts the muzzle inside
        /// the owner's own body, so the volume genuinely overlaps its wielder.</para>
        ///
        /// <para><b>Non-units stay unfiltered.</b> A crate or a barrel is not a <c>Unit</c> and has no
        /// <c>fraction</c> in AS3 either — those are handled by <c>udarBox</c> — so only a
        /// <see cref="UnitController"/> target is tested.</para>
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isActive || other.isTrigger) return;

            var damageable = other.GetComponent<IDamageable>();
            if (damageable == null || !damageable.IsAlive) return;

            ResolveOwner();

            if (_ownerUnit != null)
            {
                UnitController targetUnit = other.GetComponentInParent<UnitController>();
                if (targetUnit != null
                    && !FactionRule.CanHitDirectly(_ownerUnit.Faction, targetUnit.Faction))
                {
                    return;
                }
            }

            if (_hasDamageContext && _damageSystem != null)
            {
                // Report, do not resolve — DamageSystem owns the formula and the tick that runs it.
                _damageSystem.Report(PendingDamage.Direct(
                    _damageContext, damageable, other.transform.position));
            }
            else
            {
                damageable.TakeDamage(1f);
            }
        }

        // ── Debug ─────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_isActive) return;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
            Gizmos.DrawWireCube(transform.position, _collider.size);
        }
    }
}
