using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// The player's bare-hoof strikes — AS3 <c>WKick</c> (<c>weapon/WKick.as</c>), the weapon
    /// <c>UnitPlayer</c> builds directly as <c>new WKick(this,"punch")</c> (<c>:431</c>) and keeps
    /// alongside whatever is in hand.
    ///
    /// <para><b>Two strikes, one flag.</b> <c>kick = ctr.keyRun</c> (<c>UnitPlayer.as:2379</c>) picks
    /// between the front hoof strike and the back kick. Everything that differs between them —
    /// reach (60 vs 70 px), height lines (five vs two), damage (×1 vs ×2), knock (×1 vs ×1.5), the
    /// tile budget (the weapon's 4 vs <c>Pers.kickDestroy</c> 30) and the sound — is in
    /// <see cref="HoofStrikeRules"/>, which is Unity-free and fixture-pinned. This class is wiring:
    /// when to fire, where the body is, and what to do with the answer.</para>
    ///
    /// <para><b>What this class used to be, and why that was wrong twice.</b> It modelled
    /// <c>WPunch</c> — the <i>scorpion</i> punch — instead: it aimed the strike at the cursor, fired
    /// at <c>rapid-5</c>, and detected the "back hit" from the aim being opposite the facing. Three
    /// separate divergences, and the third is the interesting one: <c>WPunch</c>'s back-hit branch is
    /// gated on <c>WPunch.zadok</c>, a field that is <b>declared and read but never assigned anywhere
    /// in the oracle</b>, so it is dead code. The live rule is <c>WKick</c>'s run key. The port had
    /// therefore implemented the branch that never runs and omitted the one that does — and because
    /// the two look alike from the outside ("a stronger strike when you are facing away"), a
    /// structural review could not tell them apart.</para>
    ///
    /// <para><b>The strike is a fixed horizontal sweep, not an aimed segment.</b> <c>WKick</c> never
    /// reads <c>celX</c>/<c>celY</c>: it binds <c>bindMove</c> lines at fixed heights in front of (or
    /// behind) the body, and the bullet walks them. The cursor only decides which way the body faces,
    /// via <c>storona</c>. Aiming the hit volume at the cursor — what this class did — made a
    /// down-right cursor punch the floor.</para>
    ///
    /// <para><b>Delivery.</b> One <see cref="ShotKind.MeleeSweep"/> plan per strike carries the damage
    /// context to <see cref="IMeleeHitVolume"/> (the same seam <c>MeleeWeaponController</c> uses), and
    /// the controller then walks the segment list through <c>BindMove</c>, one segment per flash frame,
    /// exactly as the oracle's bullet walks its waypoints. Nothing here becomes a projectile:
    /// <c>ProjectileSpawner</c> ignores <c>MeleeSweep</c>.</para>
    ///
    /// <para><b>Owner multipliers (WKick.as:47-54, :69).</b> WIRED: <c>punchDamMult</c> scales both
    /// <c>b.damage</c> and <c>b.otbros</c>; <c>kickDestroy</c> replaces the weapon's <c>destroy</c> on
    /// the kick. NOT WIRED, deliberately: the stun proc (<c>dopCh = punchDamMult - 1</c>,
    /// <c>dopDamage = 30/60</c>) — <c>DopChance</c>/<c>DopDamage</c> have no reader anywhere in the
    /// port, so writing them would change nothing observable. The constants are recorded in
    /// <see cref="HoofStrikeRules.KickStunDamage"/> for whoever lands the consumer.</para>
    /// </summary>
    public sealed class UnarmedWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>100 px = 1 unit, the project's own scale (see <c>TileQueryConstants</c>). Kept
        /// local because this assembly must not reach into the map's query layer for one constant.</summary>
        private const float PpuScale = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly IWeaponStats _def;

        private float _frameAccum;
        private bool  _attackHeld;
        private bool  _attackJustPressed;
        private Vector2 _lastAimTarget;
        private Vector2 _lastHoldPoint;

        /// <summary>
        /// <c>owner.storona</c> — +1 right, −1 left. Taken from <see cref="Owner"/> when the caller
        /// supplies a body (production always does); the aim-based fallback is the port's old
        /// approximation and only exists so a test with no unit still works.
        /// </summary>
        private float _storona = 1f;

        private readonly List<ShotPlan> _plans = new();

        // ── The strike in flight ──────────────────────────────────────────────

        /// <summary><c>WKick.kick</c>, latched at attack start — AS3 assigns it immediately before
        /// <c>attack()</c> (<c>UnitPlayer.as:2379-2380</c>), so a mid-swing key change cannot move the
        /// hit volume out from under the animation.</summary>
        private bool _kick;

        /// <summary>The path this strike is walking, in feet-relative AS3 px. Null outside a strike.</summary>
        private HoofSegment[] _segments;

        /// <summary>Index of the next segment to bind. AS3's bullet advances its waypoint list.</summary>
        private int _nextSegment;

        /// <summary>The AS3 sound id of the strike that just fired, or null. Read once by the caller.</summary>
        private string _pendingSound;

        // ── Owner inputs ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>WKick.kick</c> — the run key, set by the caller immediately before
        /// <see cref="BeginAttack"/>. Shift in AS3 (<c>inter/Ctr.as:23</c>).
        ///
        /// <para><b>This is the whole difference between the two attacks.</b> It is not the cursor, and
        /// it is not "am I facing away": holding run while striking throws the back hooves.</para>
        /// </summary>
        public bool Kick { get; set; }

        /// <summary>
        /// The body the strike is thrown from: <c>owner.X</c>, <c>owner.Y</c> (feet) and
        /// <c>owner.storona</c>. Set per tick by the caller, like <c>MeleeWeaponController.Owner</c>.
        ///
        /// <para>Null falls back to the hold point and the aim, which is what this controller did
        /// before the seam existed — and it is wrong by a body height, because the hold point sits at
        /// chest level while every line is measured from the feet.</para>
        /// </summary>
        public HoofOwnerFrame? Owner { get; set; }

        /// <summary>
        /// The sweep surface. <c>MeleeHitVolume</c> in production; a recording stub in tests.
        ///
        /// <para>Optional: with no volume the strike still consumes its cooldown, still plays its
        /// sound and still emits its plan — it simply cannot hit anything. That is the state the whole
        /// unarmed path was in before this seam: the plan was emitted and nothing read it, so a punch
        /// had never dealt damage or broken a tile.</para>
        /// </summary>
        public IMeleeHitVolume HitVolume { get; set; }

        /// <summary>
        /// The oracle's tile query, for the "is there a low obstacle in front of me?" probe
        /// (<c>WKick.as:60-65</c>). Takes a <b>world</b> point; returns whether a tile is there and its
        /// <c>thre</c>.
        ///
        /// <para>Null means "no tiles in this world" — the probe then reports false, which is the
        /// oracle's own answer when <c>getAbsTile</c> returns null. It is <i>not</i> the same as
        /// "no obstacle": a wall the strike should have cleared over would be missed, so production
        /// must supply this.</para>
        /// </summary>
        public Func<Vector2, HoofTileProbe> ProbeTile { get; set; }

        // ── Owner stats ───────────────────────────────────────────────────────

        /// <summary>
        /// The wielder's RPG multipliers, or null for a weapon with no owner (an enemy, or a test).
        ///
        /// <para>AS3 reads these straight off <c>World.w.pers</c> inside <c>WKick.actions()</c>
        /// (<c>WKick.as:47-54,69</c>) rather than copying them in <c>setPers</c> — the punch/kick
        /// family is the one weapon type that reaches into <c>Pers</c> at fire time. The port holds a
        /// live reference for the same reason the ranged controller does.</para>
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        /// <summary><c>Pers.punchDamMult</c> (<c>WKick.as:47-48</c>), 1 when there is no owner.</summary>
        private float PunchDamMult => _statSource != null ? _statSource.PunchDamMult : 1f;

        /// <summary>
        /// <c>Pers.kickDestroy</c> (<c>WKick.as:69</c>) — the tile damage a <b>kick</b> carries, in
        /// place of the weapon's own <c>destroy</c>. Only read on the kick branch, which is the whole
        /// reason a bare kick can break a wall the punch only dents: the punch uses the row's
        /// <c>destroy='4'</c> (<c>AllData.as:26</c>), the kick uses <c>Pers</c>' 30
        /// (<c>Pers.as:335</c>) — and a perk can raise it to 80 (<c>powerkick</c>, <c>AllData.as:5785</c>).
        /// </summary>
        private float KickDestroy => _statSource != null ? _statSource.KickDestroy : 0f;

        // ── Constructor ───────────────────────────────────────────────────────

        public UnarmedWeaponController(WeaponRuntimeState state, IWeaponStatSource statSource = null)
        {
            State       = state;
            _def        = state.Def;
            _statSource = statSource;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack()
        {
            _attackHeld        = true;
            _attackJustPressed = true;
        }

        public void EndAttack() => _attackHeld = false;
        public void StartReload() { }

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * SimClock.FramesPerSecond;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(holdPoint, aimTarget);

            _attackJustPressed = false;
            State.SyncReactive();
        }

        public IReadOnlyList<ShotPlan> FlushShotPlans()
        {
            if (_plans.Count == 0) return System.Array.Empty<ShotPlan>();
            var result = new List<ShotPlan>(_plans);
            _plans.Clear();
            return result;
        }

        public void Dispose() => State.Dispose();

        /// <summary>
        /// The AS3 sound id of the strike that fired this tick — <c>m_big</c> for the kick,
        /// <c>m_med</c> for the punch (<c>WKick.as:81</c>, <c>:105</c>) — or null. Cleared on read so a
        /// caller that ticks twice in one frame cannot play it twice.
        ///
        /// <para>Played here rather than through <c>ShotCues</c> because the cue carries a bool and the
        /// id comes from the weapon's row: <c>punch</c> authors no <c>&lt;snd shoot&gt;</c>, and the
        /// oracle's two strike sounds are hard-coded at the call site, not data.</para>
        /// </summary>
        public string ConsumeStrikeSound()
        {
            string s = _pendingSound;
            _pendingSound = null;
            return s;
        }

        // ── Per-flash-frame ────────────────────────────────────────────────────

        private void TickOneFlashFrame(Vector2 holdPoint, Vector2 aimTarget)
        {
            _lastAimTarget = aimTarget;
            _lastHoldPoint = holdPoint;

            // AS3 `WKick.actions():30-32` — X/Y/storona are refreshed from the owner every frame.
            _storona = Owner.HasValue
                ? Owner.Value.Storona
                : (aimTarget.x >= holdPoint.x ? 1f : -1f);

            // Unarmed: no world position tracking — position follows owner.
            State.X = holdPoint.x;
            State.Y = holdPoint.y;
            State.Rot   = Mathf.Atan2(aimTarget.y - holdPoint.y, aimTarget.x - holdPoint.x);
            State.Ready = true;

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TAttack <= 0)
                RunAttack();

            // ── actions() ────────────────────────────────────────────────────
            RunActions();

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        // ── attack() ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>WKick.attack()</c> (<c>:110-117</c>): sets <c>t_attack = rapid</c> unconditionally.
        /// </summary>
        private void RunAttack()
        {
            // Single-shot debounce (non-auto fists — tap timing). `AutomaticTrigger` is AS3's
            // `attack(true)`; see WeaponRuntimeState.AutomaticTrigger.
            bool isAuto = _def.IsAuto;
            if (!isAuto && State.TAuto > 0 && !State.AutomaticTrigger)
            {
                State.TAuto = 3;
                return;
            }

            // AS3 `UnitPlayer.as:2379` assigns `kick` immediately before `attack()`, so the mode is
            // fixed for the whole swing even if the run key is released mid-animation.
            _kick = Kick;

            State.TAttack  = Mathf.RoundToInt(_def.rapid);
            State.IsAttack = true;
            State.KolShoot++;
            State.TShoot = 3;
        }

        // ── actions() ────────────────────────────────────────────────────────

        private void RunActions()
        {
            // AS3 `WKick.actions():33-37` decrements FIRST and then tests the decremented value
            // (`if(t_attack > 0) --t_attack;` … `if(t_attack == rapid - 8)`). The port tested before
            // decrementing, which fired one frame late — the same off-by-one MeleeWeaponController
            // had (WClub.as:495 is the last statement of its block).
            if (State.TAttack > 0) State.TAttack--;

            if (State.TAttack == Mathf.RoundToInt(_def.rapid) - HoofStrikeRules.FireFrameOffset)
                Shoot();

            // The bullet walks its waypoint list one segment per frame (`Bullet.bindMove` →
            // `run()`), so the volume visits each line in the oracle's order.
            StepSegments();

            if (State.TRet   > 0) State.TRet--;
            if (State.TShoot > 0) State.TShoot--;
            if (State.TAuto  > 0) State.TAuto--;
            else                  State.Pow = 0;

            State.RotUp = 0f;  // No recoil lift on unarmed.
        }

        // ── shoot() ──────────────────────────────────────────────────────────

        /// <summary>
        /// <c>WKick.actions()</c>'s fire block (<c>:37-107</c>): probe for a low obstacle, pick the
        /// branch, arm the volume and publish the damage.
        /// </summary>
        private void Shoot()
        {
            float bodyX  = Owner.HasValue ? Owner.Value.X          : _lastHoldPoint.x;
            float feetY  = Owner.HasValue ? Owner.Value.FeetWorldY : _lastHoldPoint.y;

            // The side the strike lands on: the kick is mirrored (`WKick.as:59`).
            float strikeSign = _kick ? -_storona : _storona;

            // ── The probe (WKick.as:60-65 for the kick, :85-90 for the punch) ────────────────
            // Both branches probe at 60 px even though the kick sweeps to 70 — `:60-61` is inside the
            // kick branch and still says 60.
            bool overLowTile = false;
            if (ProbeTile != null)
            {
                float probeX = bodyX + strikeSign * HoofStrikeRules.PunchReachPx / PpuScale;

                HoofTileProbe lower = ProbeTile(new Vector2(
                    probeX, feetY + HoofStrikeRules.LowLinePx / PpuScale));
                HoofTileProbe upper = ProbeTile(new Vector2(
                    probeX, feetY + HoofStrikeRules.HighLinePx / PpuScale));

                overLowTile = HoofStrikeRules.OverLowTile(
                    lower.Exists, upper.Exists, lower.Threshold, upper.Threshold);
            }

            // ── The path ────────────────────────────────────────────────────────────────────
            // Handed a zero origin: the rules return feet-relative pixels, and BindSegment adds the
            // body back. Same arithmetic the oracle does with absolute X/Y, one conversion less.
            _segments    = HoofStrikeRules.Segments(_kick, overLowTile, x: 0f, feetY: 0f, storona: _storona);
            _nextSegment = 0;

            // ── Damage (WKick.as:47-49, :66-70) ─────────────────────────────────────────────
            // AS3 stamps the two hit procs on the bullet at fire time, like any weapon.
            DamageContext baseDmg = DamageContext.FromWeapon(
                _def, null, State.OwnerFaction,
                critInvisChance: _statSource != null ? _statSource.CritInvis : 0f,
                desintegrChance: _statSource != null ? _statSource.Desintegr : 0f);

            // `punchDamMult` scales BOTH damage and knockback (unlike melee, where the knock has no
            // stat term at all — WClub.as:594).
            float punchMult = PunchDamMult;

            float damageScale    = (_kick ? HoofStrikeRules.KickDamageScale      : 1f) * punchMult;
            float knockbackScale = (_kick ? HoofStrikeRules.KickKnockbackScale   : 1f) * punchMult;

            // `b.knockx = storona; b.knocky = -0.1` (:43, :66) — almost flat, and in AS3's Y-down
            // space −0.1 is UP, hence the negation for Unity.
            Vector2 knockDir = new Vector2(strikeSign, -HoofStrikeRules.KnockY);

            DamageContext dmgCtx = baseDmg.WithScaledDamage(damageScale, knockbackScale, knockDir);

            // `b.destroy = World.w.pers.kickDestroy` on the kick only (:69), gated on `owner.player` in
            // the oracle — which is "there is a Pers" here. The punch keeps the row's own destroy (4).
            if (_kick && _statSource != null)
                dmgCtx = dmgCtx.WithDestroyTiles(KickDestroy);

            // ── Arm the volume ──────────────────────────────────────────────────────────────
            // AS3 sets `b.tilehit = false` and `b.off = false` here; the port's equivalent latch is
            // cleared by SetActive(true) (see MeleeHitVolume).
            //
            // The PAYLOAD goes on before the window opens, and that order is the fix for "the kick
            // cannot break anything". This controller binds its first line in the same tick it
            // publishes its plan (StepSegments below), while PlayerWeaponLoadout only forwards a
            // plan's damage to the volume at the END of that tick — so before this line existed the
            // first line of every strike swept with the *previous* strike's payload, and the first
            // strike of a run swept with none at all (SweepTilesForImpact returns early on
            // `!_hasDamageContext`). For the back kick that first line is the low one, the only line
            // that can reach a wall, so a kick after a punch spent its one tile hit on the punch's
            // destroy=4 instead of Pers.kickDestroy=30, and the opening kick of a run did nothing at
            // all. The loadout's forward is now a second, identical write.
            HitVolume?.SetDamageContext(dmgCtx);
            HitVolume?.SetActive(true);

            // ── Publish ─────────────────────────────────────────────────────────────────────
            // The plan carries the damage context to the volume through PlayerWeaponLoadout, the same
            // way a club's does. WorldPosition is the far end of the first line — where the hooves are
            // at the moment of impact, used by impact effects and the debug overlay.
            HoofSegment first = _segments[0];
            Vector2 impactPos = new Vector2(
                bodyX + first.FromX / PpuScale,
                feetY - first.FromY / PpuScale);

            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: impactPos,
                angleRad:      strikeSign >= 0f ? 0f : Mathf.PI,
                kind:          ShotKind.MeleeSweep,
                damage:        dmgCtx,
                pelletIndex:   0,
                totalPellets:  1,
                cues:          new ShotCues(
                                   playShootSound:   false,   // the oracle's cue is hard-coded, see ConsumeStrikeSound
                                   spawnShellCasing: false,
                                   spawnMuzzleFlash: false,
                                   makeNoise:        _def.noiseRadius > 0f,
                                   noiseRadius:      _def.noiseRadius / PpuScale,
                                   shineRadius:      0),
                meleePrevTip:  impactPos,
                meleeCurrTip:  impactPos));

            _pendingSound = _kick ? HoofStrikeRules.KickSound : HoofStrikeRules.PunchSound;
            State.IsShoot = true;
        }

        // ── The sweep ─────────────────────────────────────────────────────────

        /// <summary>
        /// Bind the next line of the strike, or close the volume when the list is spent.
        ///
        /// <para><b>One segment per flash frame, not all at once.</b> A <c>BindMove</c> repositions one
        /// capsule; issuing five in a single frame would leave only the last one standing when physics
        /// ran, so four of the five lines could never hit a unit. Spreading them is also what the
        /// oracle does — the bullet walks its waypoints at <c>speed</c> px/frame.</para>
        /// </summary>
        private void StepSegments()
        {
            if (_segments == null) return;

            if (_nextSegment >= _segments.Length)
            {
                EndStrike();
                return;
            }

            BindSegment(_segments[_nextSegment++]);
        }

        private void BindSegment(in HoofSegment seg)
        {
            if (HitVolume == null) return;

            float ox = Owner.HasValue ? Owner.Value.X          : _lastHoldPoint.x;
            float oy = Owner.HasValue ? Owner.Value.FeetWorldY : _lastHoldPoint.y;

            // Feet-relative px → world units. `FromY` is `-height` (AS3 writes `Y - h` with Y down),
            // so subtracting it moves the line UP in Unity.
            Vector2 from = new Vector2(ox + seg.FromX / PpuScale, oy - seg.FromY / PpuScale);
            Vector2 to   = new Vector2(ox + seg.ToX   / PpuScale, oy - seg.ToY   / PpuScale);

            HitVolume.BindMove(from, to);
        }

        /// <summary>Close the sweep. AS3's bullet simply expires (<c>liv</c>) once its moves are spent.</summary>
        private void EndStrike()
        {
            _segments    = null;
            _nextSegment = 0;
            HitVolume?.SetActive(false);
        }
    }
}
