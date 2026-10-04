using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// The port of WClub.as for tip==1 melee weapons.
    ///
    /// <para><b>What is faithful:</b> the dispatch (one controller for all three mtips), the
    /// per-sub-type <c>rapid_act</c>, the strike windows, the three-phase <c>anim</c>, the per-mtip
    /// swept geometry, and the damage/knock shape (<c>meleeDamMult</c> on damage only, knock =
    /// <c>storona, -0.2</c>).</para>
    ///
    /// <para><b>What is still an approximation</b> — each named at its site and tracked in
    /// <c>TOPIC_weapons_dispatch</c>: the spring position, the power-charge model (unreachable
    /// anyway: <c>meleePowerAttack</c> is 0 on every asset), the combo default
    /// (<c>!auto &amp;&amp; !powerfull → combinat</c>, <c>:177-180</c>), the durability <i>wear
    /// rate</i> (a flat 1 per swing here vs <c>crash()</c>-on-hit there), and the <c>rashod</c> ammo
    /// spend. The melee <c>resultDamage</c> <i>shape</i> is now complete: <c>param2</c> (the
    /// weapon-skill multiplier), <c>skillPlusDam</c> and <c>(1 - breaking*0.6)</c> all fold into the
    /// hit (WClub.as:649).</para>
    ///
    /// Key behaviors from AS3:
    ///
    /// Position (spring physics) — **an approximation, not a port.** AS3 (WClub.as:310-335) springs
    ///   toward `holdPoint + norma(cel - hold, meleeR)`, halves and clamps the step to
    ///   `max(levitRun, 1/massa)`, adds `blumR` to the drawn rotation, and puts `cel` at
    ///   `owner.cel - dlina*0.8*storona, +dlina*0.3` behind/below the cursor after a `lineCel()`
    ///   line-of-sight test. This controller snaps to the hold point whenever `t_attack <= 0` and
    ///   otherwise chases the cursor clamped to `dlina`, with no `meleeR`, no `levitRun`, no `blumR`
    ///   and no tile query. Tracked in TOPIC_weapons_dispatch.
    ///
    /// mtip sub-behaviors — all three drive the hit volume (see UpdateHitWindow):
    ///   0 = swing (club/sword) — rot sweeps an arc driven by anim
    ///   1 = thrust (spear)     — rot points at the cursor, tip lunges by anim * atDlina
    ///   2 = overhead (axe/saw) — ONE bindMove from mindlina to dlina at TAttack == 1
    ///
    /// Attack:
    ///   weaponAttack() → t_attack = rapid_act = resultRapid(rapid) (WClub.as:667,673) — NOT the
    ///   raw rapid. Every t_attack comparison below uses _rapidAct for that reason.
    ///
    ///   resultRapid is **per sub-type**: `mtip == 2` returns rapid / skillConf with no rapidMult
    ///   term at all (WClub.as:652-657), while mtip 0 and 1 divide by meleeSpdMult (which reaches
    ///   the family as `rapidMult = 1 / meleeSpdMult`, :204). So a speed perk shortens a club and a
    ///   spear, and must not touch an autoaxe / bsaw / ripper. See IgnoresSpeedMultiplier.
    ///
    ///   All three sub-types emit their damage plan here, from weaponAttack(): AS3 calls shoot()
    ///   once per attack at :674 for every mtip, and that is where the damage is fixed
    ///   (resultDamage + breaking, :592-593). The per-mtip code in actions() only *moves* the
    ///   already-armed bullet afterwards. See Shoot() for why emitting at the strike frame instead
    ///   was a defect, not a simplification.
    ///
    /// Hit window (on the rapid_act time base; the bounds are identical for mtip 0 and 1 — only the
    /// geometry differs):
    ///   mtip 0: rapid_act/2 &lt;= t_attack &lt; rapid_act*5/6  (WClub.as:378)
    ///   mtip 1: rapid_act/2 &lt;= t_attack &lt; rapid_act*5/6  (WClub.as:435)
    ///   mtip 2: t_attack == 1, once                       (WClub.as:469)
    ///   Each active frame calls MeleeHitVolume.BindMove().
    ///
    /// Combo system (meleeCombo=true):
    ///   combo counter increments each attack; at combo>=4: powerMult=2, reset
    ///   t_combo window = rapid + 20 frames between hits
    ///
    /// Power attack (meleePowerAttack=true):
    ///   pow accumulates while attack held; if 2 < pow < rapid*2.15: powerMult scales
    ///
    /// Animation:
    ///   anim drives rot via: rot = -PI/2 + (-PI/6 + anim*PI) * storona
    ///   anim is a three-phase value over the attack (-0.25 at the 5/6 seam → 1.0 at mid-swing →
    ///   0 at the end), continuous at both seams — see UpdateAnim.
    ///
    /// MeleeHitVolume:
    ///   Assigned by PlayerWeaponLoadout. Controller calls BindMove() each flash frame
    ///   during the active hit window. Outside the window, volume is disabled.
    /// </summary>
    public sealed class MeleeWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float PpuScale = 100f;

        // Default reach in Unity units when WeaponDefinition.meleeDlina not set.
        private const float DefaultReach = 1f;

        // AS3 WClub.atDlina (WClub.as:59) — the lunge distance the thrust tip travels along the
        // aim axis, in AS3 pixels. Only mtip==1 uses it (WClub.as:433-434).
        private const float AtDlinaPx = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        private float _frameAccum;
        private bool  _attackHeld;

        // Swing animation 0→1 value (anim in AS3).
        private float _anim;

        // Facing direction: +1 = right, -1 = left. Derived from aim target.
        private float _storona = 1f;

        // Combo state.
        private int _combo;
        private int _tCombo;

        // Power attack accumulator (mirrors pow in WeaponRuntimeState).
        // We track it locally since WeaponRuntimeState.Pow is already defined.

        // Power multiplier applied to this hit's damage.
        private float _powerMult = 1f;

        // Weapon tip positions for MeleeSweep (previous and current frame).
        private Vector2 _prevTip;
        private bool    _inStrikeWindow;
        private bool    _firstFrame = true;

        // Reach in Unity units.
        private float _dlina;
        private float _minDlina;

        /// <summary>WClub.atDlina in Unity units — the mtip==1 thrust lunge distance.</summary>
        private float AtDlina => AtDlinaPx / PpuScale;

        // External hit volume — assigned by PlayerWeaponLoadout after equip.
        // Typed as the interface so EditMode tests can substitute a recording stub; the real
        // MeleeHitVolume caches its collider in Awake, which never runs for AddComponent in EditMode.
        public IMeleeHitVolume HitVolume { get; set; }

        private readonly List<ShotPlan> _plans = new();

        // ── Owner stats ───────────────────────────────────────────────────────

        /// <summary>
        /// The wielder's RPG multipliers, or null for a weapon with no owner (an enemy, or a test).
        ///
        /// <para>AS3 <i>copies</i> these onto the weapon instance in the melee <c>setPers</c>
        /// (<c>WClub.as:204-205</c>: <c>rapidMult = 1 / meleeSpdMult</c> and
        /// <c>damMult *= meleeDamMult</c>) and the weapon then reads its own copy. The port holds a
        /// live reference instead, so a mid-fight perk change is picked up without rebuilding the
        /// weapon — the same mechanism-only divergence as <see cref="IWeaponStatSource"/> describes,
        /// and the same shape the ranged controller already uses.</para>
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        /// <summary>
        /// <c>Pers.meleeSpdMult</c> as AS3 applies it — a swing-duration <b>divisor</b>, because
        /// <c>rapidMult = 1 / meleeSpdMult</c> (<c>WClub.as:204</c>) and
        /// <c>resultRapid = rapid / skillConf * rapidMult / rapidMultCont</c> (<c>:658</c>).
        /// A missing owner falls back to 1, which leaves the swing duration untouched.
        /// </summary>
        private float MeleeSpdMult => _statSource != null ? _statSource.MeleeSpdMult : 1f;

        /// <summary>
        /// <c>Pers.meleeDamMult</c> — flat damage multiplier read by the melee
        /// <c>resultDamage</c> (<c>WClub.as:649</c>).
        /// </summary>
        private float MeleeDamMult => _statSource != null ? _statSource.MeleeDamMult : 1f;

        /// <summary>
        /// The owner's weapon-skill <b>tier</b> for this weapon's skill code — AS3
        /// <c>(owner as UnitPlayer).pers.getWeapLevel(this.skill)</c> (<c>Weapon.as:1368</c>), a 0..5
        /// tier, not a point total. <see cref="HitAvoidance.UnknownOwnerSkillLevel"/> when there is no
        /// stat source (an enemy, or a test), which fails every skill test open.
        ///
        /// <para><b>One accessor, three consumers.</b> The refuse-to-fire gate
        /// (<see cref="CheckAvail"/>), the under-skill miss chance and the over-qualification damage
        /// bonus all measure the same <c>gap = weaponLevel - tier</c>; deriving it more than once is
        /// how the port's two copies of a rule usually drift apart.</para>
        /// </summary>
        private int OwnerWeaponSkillLevel => _statSource != null
            ? _statSource.OwnerWeaponSkillLevel(_def.skillLevel)
            : HitAvoidance.UnknownOwnerSkillLevel;

        /// <summary>
        /// The owner's weapon-skill <b>multiplier</b> for this weapon's skill code — AS3 <c>_loc1_</c>
        /// in <c>Weapon.shoot</c> (<c>Weapon.as:1451-1459</c>), the <c>p2</c> slot of the melee
        /// <c>resultDamage</c> (<c>WClub.as:649</c>). 1 (the identity) when there is no stat source,
        /// which leaves the weapon's own damage untouched — AS3's state for a unit with no
        /// <c>Pers</c>.
        /// </summary>
        private float WeaponSkillMultiplier => _statSource != null
            ? _statSource.WeaponSkillMultiplier(_def.skillLevel)
            : 1f;

        /// <summary>
        /// <c>WClub.resultRapid</c>'s <c>mtip == 2</c> early-out (<c>WClub.as:654-657</c>): the
        /// overhead swing returns <c>rapid / skillConf</c> with <b>no</b> <c>rapidMult</c> term,
        /// while <c>mtip</c> 0 and 1 return <c>rapid / skillConf * rapidMult / owner.rapidMultCont</c>
        /// (<c>:658</c>). <c>rapidMult</c> is where <c>Pers.meleeSpdMult</c> enters the family
        /// (<c>rapidMult = 1 / meleeSpdMult</c>, <c>:204</c>), so a speed perk shortens a club and a
        /// spear and must leave an autoaxe / bsaw / ripper exactly as long as the data says.
        ///
        /// <para>Applying the divisor to all three sub-types (as this controller did) is a real
        /// behavioural difference, not a rounding one: at <c>meleeSpdMult 2</c> an overhead swung
        /// twice as fast as the oracle allows.</para>
        /// </summary>
        private bool IgnoresSpeedMultiplier => _def.meleeType == MeleeType.Overhead;

        /// <summary>
        /// The effective swing length for the attack <b>currently in flight</b>: AS3's
        /// <c>rapid_act</c> (<c>WClub.as:667</c>), i.e. <c>resultRapid(rapid)</c> with
        /// <c>skillConf</c> and <c>rapidMultCont</c> both 1 in the port.
        ///
        /// <para><b>Snapshotted, not recomputed.</b> AS3 assigns <c>rapid_act</c> once, inside
        /// <c>weaponAttack()</c> (<c>:667</c>), and every later comparison in the same swing reads
        /// that stored value. Reading a live <see cref="MeleeSpdMult"/> property instead would let a
        /// perk applied mid-swing move the hit window out from under the <c>t_attack</c> that was set
        /// at the start of it — a divergence AS3 cannot have, because it has no live reference.</para>
        ///
        /// <para>Every <c>t_attack</c> comparison uses this, <i>not</i> <c>_def.rapid</c>: a speed
        /// perk shortens the whole swing, hit window and combo window included — except on
        /// <see cref="MeleeType.Overhead"/>, which <see cref="IgnoresSpeedMultiplier"/> exempts.</para>
        ///
        /// <para><b>Why it is a float.</b> AS3's <c>rapid_act</c> is a <c>Number</c> and
        /// <c>t_attack</c> is an <c>int</c>, so <c>t_attack = rapid_act</c> (<c>:673</c>)
        /// <i>truncates</i> while every threshold keeps the fractional value. Collapsing the two into
        /// one rounded int (as this used to) shifted each bound by up to a frame whenever a speed
        /// multiplier produced a fraction — <c>rapid 20 / meleeSpdMult 3 = 6.67</c> became 7 instead
        /// of 6. Both are held here now: the float for the thresholds, the truncation at
        /// <c>State.TAttack</c>.</para>
        /// </summary>
        private float _rapidAct = 1f;

        // ── Constructor ───────────────────────────────────────────────────────

        public MeleeWeaponController(WeaponRuntimeState state, IWeaponStatSource statSource = null)
        {
            State       = state;
            _def        = state.Def;
            _statSource = statSource;
            _dlina   = _def.meleeDlina   > 0f ? _def.meleeDlina   / PpuScale : DefaultReach;
            _minDlina = _def.meleeMinDlina > 0f ? _def.meleeMinDlina / PpuScale : _dlina;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack() => _attackHeld = true;
        public void EndAttack()   => _attackHeld = false;
        public void StartReload() { }

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * SimClock.FramesPerSecond;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(holdPoint, aimTarget);

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

        // ── Per-flash-frame ────────────────────────────────────────────────────

        private void TickOneFlashFrame(Vector2 holdPoint, Vector2 aimTarget)
        {
            // ── Facing direction ──────────────────────────────────────────────
            _storona = aimTarget.x >= holdPoint.x ? 1f : -1f;

            // ── Spring position (AS3: del toward celTarget, clamped to meleeR) ──
            if (_firstFrame || State.TAttack <= 0)
            {
                State.X    = holdPoint.x;
                State.Y    = holdPoint.y;
                _firstFrame = false;
            }
            else
            {
                // Cursor target clamped to reach.
                Vector2 weaponPos = new Vector2(State.X, State.Y);
                Vector2 toTarget  = aimTarget - weaponPos;
                if (toTarget.magnitude > _dlina)
                    toTarget = toTarget.normalized * _dlina;

                Vector2 del = toTarget / 2f;   // AS3: (celX - X) / 2
                State.X += del.x;
                State.Y += del.y;
            }

            // ── Rotation ──────────────────────────────────────────────────────
            // AS3 sets `rot` inside each mtip branch (WClub.as:377 / :430 / :466):
            //   mtip 0 → the anim-driven swing arc
            //   mtip 1 / 2 → straight at the cursor
            // AS3 aims the latter from `owner.Y - owner.scY/2`; this controller has no owner
            // offset, so it aims from the weapon's own position like the rest of the class.
            UpdateAnim();
            State.Rot = _def.meleeType == MeleeType.Horizontal
                ? -Mathf.PI / 2f + (-Mathf.PI / 6f + _anim * Mathf.PI) * _storona
                : Mathf.Atan2(aimTarget.y - State.Y, aimTarget.x - State.X);
            State.Ready = true;

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TAttack <= 0)
                WeaponAttack();

            // ── Hit window (AS3 actions():337-497) ────────────────────────────
            // The order here is load-bearing. AS3 runs the whole anim + strike-window block while
            // t_attack still holds this frame's value, and only decrements at the END of that block
            // (`--t_attack`, :495) before the recharge/auto/combo timers. Decrementing first — as
            // RunActions used to, since it ran above this call — shifted every window bound and
            // every anim sample one frame late, which is the offset the hit-window fixture documents
            // as audit §4's remaining phase-math item.
            UpdateHitWindow(aimTarget);

            // ── actions() timers ──────────────────────────────────────────────
            RunActions();

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        // ── Attack initiation ─────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Weapon.checkAvail()</c>'s refuse-to-fire half (<c>Weapon.as:1377-1381</c>) — may this
        /// owner swing this weapon <i>at all</i>? <c>false</c> when the weapon's required skill sits
        /// more than two tiers above the owner's, which stops the attack before <c>t_attack</c> is
        /// armed (<c>:1304-1310</c>).
        ///
        /// <para><b>Melee reaches the base gate, not a second one.</b> <c>WClub</c> has no
        /// <c>attack()</c> override, so a swing runs the same <c>checkAvail()</c> a ranged shot does —
        /// the same 0.8 / 0.6 confidence and the same <c>&gt; 2</c> refusal. Only <c>WThrow</c>
        /// (0.75 / 0.5, its own copy) and <c>WKick</c>/<c>WPunch</c>/<c>WPaint</c> (no gate) differ, so
        /// this reuses <see cref="HitAvoidance.CanFire"/> rather than re-deriving the rule.</para>
        /// </summary>
        private bool CheckAvail() => HitAvoidance.CanFire(_def.weaponLevel, OwnerWeaponSkillLevel);

        /// <summary>
        /// Mirrors WClub.weaponAttack() (<c>WClub.as:661-692</c>).
        /// Sets t_attack, applies combo/power multipliers, and arms the damage — for <b>every</b>
        /// sub-type, because AS3 calls <c>shoot()</c> here once per attack (<c>:674</c>) whatever
        /// the mtip.
        /// </summary>
        private void WeaponAttack()
        {
            if (State.IsBroken) return;

            // AS3 runs checkAvail() in attack() before weaponAttack() (:1304 then :1341), so an
            // under-skilled owner never arms the swing. Unknown owner (no stat source) always passes.
            if (!CheckAvail()) return;

            _powerMult = 1f;

            // rapid_act is computed HERE and stored (WClub.as:667) — before the combo and power
            // tests that also read it, and before t_attack is set from it. Snapshotting once per
            // attack is what keeps the whole swing on one time base.
            //
            // The divisor is per sub-type: resultRapid's `mtip == 2` branch has no rapidMult term
            // (WClub.as:652-657). See IgnoresSpeedMultiplier.
            //
            // The 1f floor is a deliberate deviation — AS3 has none, so a large enough
            // meleeSpdMult collapses rapid_act toward 0 and t_attack truncates to 0, which leaves
            // the weapon unable to attack at all. Keeping one frame means such a weapon still
            // swings (with no strike window, since every bound is then above t_attack).
            _rapidAct = Mathf.Max(1f, _def.rapid / (IgnoresSpeedMultiplier ? 1f : MeleeSpdMult));

            // ── Combo (combinat) ──────────────────────────────────────────────
            // AS3 measures both windows off rapid_act, not rapid (WClub.as:676, :340): a speed
            // multiplier shortens the swing AND tightens the combo window with it.
            if (_def.meleeCombo)
            {
                _tCombo = (int)(_rapidAct + 20f);
                _combo++;
                if (_combo >= 4)
                {
                    _powerMult = 2f;
                    _combo     = 0;
                }
            }

            // ── Power attack (powerfull) ─────────────────────────────────────
            if (_def.meleePowerAttack && State.Pow > 2 && State.Pow < _rapidAct * 2.15f)
            {
                _powerMult = 1f + State.Pow / (_rapidAct * 2.15f);
            }

            // t_attack = rapid_act (WClub.as:673). rapid_act is a Number there and t_attack an int,
            // so the assignment truncates; the thresholds below keep the fractional value.
            State.TAttack  = Mathf.FloorToInt(_rapidAct);
            State.IsAttack = true;
            _anim          = 0f;

            // Arm the damage for every sub-type. AS3's shoot() is called once per attack, here, for
            // all three mtips (:674) — the per-mtip branches in actions() only move the bullet
            // afterwards. Emitting at the strike frame instead (as this did for mtip 1 and 2) put
            // the damage context one frame late for Thrust, whose window opens before its old
            // t_attack == rapid_act/2 emission point, so the first thrust swing swept with no
            // context and fell back to MeleeHitVolume's 1-damage branch. See Shoot().
            Shoot();

            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - 1);
            State.KolShoot++;
            State.TShoot = 3;
            State.IsShoot = true;
        }

        // ── Shoot (hit detection) ─────────────────────────────────────────────

        /// <summary>
        /// Arm the swing: build the damage context and publish the <see cref="ShotKind.MeleeSweep"/>
        /// plan that carries it to <see cref="MeleeHitVolume"/>. Called once per attack from
        /// <see cref="WeaponAttack"/>, for every sub-type — AS3's <c>shoot()</c> call site
        /// (<c>WClub.as:674</c>).
        ///
        /// <para><b>Why the plan must be emitted here and not at the strike frame.</b> The plan is
        /// the only carrier of the damage context to the hit volume
        /// (<c>PlayerWeaponLoadout.FixedUpdate</c> forwards its <c>Damage</c> through
        /// <c>SetDamageContext</c>), and <c>MeleeHitVolume.OnTriggerEnter2D</c> falls back to a flat
        /// <c>TakeDamage(1f)</c> while no context has ever been set. Emitting at
        /// <c>t_attack == rapid_act/2</c> — where Thrust used to emit — landed one frame <i>after</i>
        /// that window had closed, so the first thrust swing swept unarmed and every later one used
        /// the previous swing's context. Arming at attack start is what AS3 does and the only
        /// ordering that cannot do either.</para>
        ///
        /// <para><b>The tip is anchored on the weapon, not on the hold point.</b> AS3 lays the vzz
        /// seed points down at <c>X + cos2 * d</c> (<c>:399-400</c>, <c>:606-607</c>, <c>:615-616</c>)
        /// where <c>X</c>/<c>Y</c> are the weapon's own sprung position and <c>cos2</c>/<c>sin2</c>
        /// are the <i>blade</i> angle. Re-projecting from the hold point along the <i>aim</i> vector
        /// (as this did) puts the swept segment where the blade is not, as soon as the spring has let
        /// the weapon drift toward the cursor.</para>
        /// </summary>
        private void Shoot()
        {
            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 tipPos    = weaponPos + new Vector2(Mathf.Cos(State.Rot), Mathf.Sin(State.Rot)) * _dlina;

            DamageContext baseDmg  = DamageContext.FromWeapon(
                _def, null, State.OwnerFaction,
                // ── The weapon-skill channel (AS3 `_loc1_`, Weapon.as:1451-1459) ──────────────
                //
                // WClub has no attack() override, so a swing inherits the base attack()/checkAvail()
                // path and folds the same two skill inputs a ranged shot does: the owner's tier (the
                // over-qualification bonus `skillPlusDam`, and the under-skill miss chance, both
                // measured against `_def.weaponLevel`) and the per-point multiplier — the `p2` slot of
                // the melee resultDamage (WClub.as:649). Read LIVE off the owner, so a point spent
                // mid-fight moves the very next swing. A null `_statSource` is an enemy with no Pers:
                // the unknown tier leaves the miss chance at 0 and both skill factors at 1, which is
                // exactly AS3's state for a unit whose Pers was never set up.
                ownerWeaponSkillLevel: OwnerWeaponSkillLevel,
                weaponSkillMultiplier: WeaponSkillMultiplier,
                critInvisChance: _statSource != null ? _statSource.CritInvis : 0f,
                desintegrChance: _statSource != null ? _statSource.Desintegr : 0f);
            // Apply power / combo multiplier to base damage. Scaled through one method rather than a
            // positional re-construction: the 16-argument copy this replaced silently dropped
            // ownerFaction, and would drop every field added to DamageContext afterwards.
            //
            // Two further damage-only factors join here, and neither is folded into _powerMult:
            //
            //   meleeDamMult — AS3's `damMult` already carries it (setPers, WClub.as:205), so it is a
            //                  perk factor. Kept separate from the combo so a perk and a combo cannot
            //                  multiply each other by accident.
            //   wearMult     — `(1 - breaking*0.6)`, the last factor of the melee resultDamage
            //                  (WClub.as:649). It is a property of the WEAPON's condition, not of the
            //                  attack, so it must not ride on _powerMult either. AS3 recomputes
            //                  `breaking` at the top of WClub.shoot() (:581-588); State.Breaking()
            //                  is the same value on the port's state.
            //
            // The knock direction is stamped here because this is the only place that knows it, and
            // FromWeapon cannot: AS3's melee knock is the ATTACKER'S FACING with a small upward tilt,
            // not the swing angle — `WClub.as:152-153` sets `b.knockx = storona; b.knocky = -0.2`.
            // `storona` is already maintained here for the animation, and the `-0.2` is the oracle's
            // literal, which is why a clubbed enemy hops rather than sliding flat.
            //
            // Only damage scales with meleeDamMult and the wear penalty — AS3 leaves the knockback at
            // otbros*otbrosMult (WClub.as:594), with no melee or breaking term on it. The power/combo
            // multiplier is the one factor that scales BOTH (WClub.as:683 sets b.damage and b.otbros).
            float wearMult = WeaponWearMath.MeleeDamageMultiplier(State.Breaking());

            DamageContext finalDmg = baseDmg.WithScaledDamage(
                _powerMult * MeleeDamMult * wearMult, _powerMult, new Vector2(_storona, -0.2f));

            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: false,
                spawnMuzzleFlash: false,
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      0);

            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: new Vector2(State.X, State.Y),
                angleRad:      State.Rot,
                kind:          ShotKind.MeleeSweep,
                damage:        finalDmg,
                pelletIndex:   0,
                totalPellets:  1,
                cues:          cues,
                meleePrevTip:  _prevTip,
                meleeCurrTip:  tipPos
            ));

            _prevTip = tipPos;
        }

        // ── Actions (timers) ──────────────────────────────────────────────────

        private void RunActions()
        {
            // AS3 `--t_attack` (WClub.as:495) — the last statement of the anim/strike-window block
            // and therefore the first of the timer block. Thrust used to emit its damage plan here,
            // at t_attack == rapid_act/2; all three sub-types now arm at attack start (see Shoot()).
            if (State.TAttack > 0) State.TAttack--;
            if (State.TRet    > 0) State.TRet--;
            if (State.TShoot  > 0) State.TShoot--;

            // Combo window countdown.
            if (_tCombo > 0)
            {
                _tCombo--;
                if (_tCombo <= 0)
                    _combo = 0;
            }

            // Pow accumulator: increments while attack held but TAttack cooling down.
            if (_attackHeld && State.TAttack == 0)
                State.Pow++;
            else if (State.TAttack == 0)
                State.Pow = 0;

            State.RotUp = 0f;  // Melee weapons don't use recoil lift.
        }

        // ── Hit window ────────────────────────────────────────────────────────

        /// <summary>
        /// Per-subtype strike window and tip sweep, mirroring the three branches of
        /// WClub.actions(). Each branch drives MeleeHitVolume through BindMove(prev, curr).
        ///
        ///   mtip 0 Horizontal — WClub.as:378-408: a fan of kolvzz+1 lines along the swing arc,
        ///                       active while rapid_act/2 &lt;= t_attack &lt; rapid_act*5/6.
        ///   mtip 1 Thrust     — WClub.as:435-454: ONE line at X + cos2*dlina + plX, where
        ///                       plX/plY = cos2/sin2 * anim * atDlina (the lunge). Same window.
        ///   mtip 2 Overhead   — WClub.as:469-483: ONE bindMove from mindlina to dlina, taken when
        ///                       t_attack == 1 — i.e. once, at the END of the attack.
        ///
        /// This method used to early-return for every meleeType except Horizontal, and
        /// <c>HitVolume.SetActive(true)</c> exists only past that guard — so Thrust and Overhead
        /// had a permanently disabled hit volume and could never hit anything (spear / mspear /
        /// tlance, autoaxe / bsaw / ripper). Audit §3.
        ///
        /// NOTE: AS3's thresholds use <c>rapid_act</c> — <c>resultRapid()</c> plus the water
        /// doubling (<c>WClub.as:667-671</c>) — and <see cref="_rapidAct"/> is the port's
        /// <c>rapid_act</c> (minus the water doubling, which needs a tile query the controller does
        /// not have). Audit §4 had two halves: the <b>variable</b> half (the code built
        /// <c>rapid_act</c> nowhere and every branch read <c>_def.rapid</c>) is closed — all three
        /// branches read <see cref="_rapidAct"/>. The <b>bounds</b> half was not: mtip 0's lower
        /// bound was <c>rapid*1/6</c> instead of <c>rapid/2</c>, and the whole window ran one frame
        /// late because the timer decrement preceded this call. Both fixed 2026-10-03.
        /// </summary>
        private void UpdateHitWindow(Vector2 aimTarget)
        {
            if (State.TAttack <= 0)
            {
                EndStrikeWindow();
                return;
            }

            switch (_def.meleeType)
            {
                case MeleeType.Thrust:
                    UpdateThrustWindow(aimTarget);
                    break;

                case MeleeType.Overhead:
                    UpdateOverheadWindow(aimTarget);
                    break;

                default:   // MeleeType.Horizontal
                    UpdateHorizontalWindow();
                    break;
            }
        }

        /// <summary>
        /// mtip 0 — the swing-arc sweep (WClub.as:378-408).
        ///
        /// Bounds are <c>t_attack &gt;= rapid_act/2 &amp;&amp; t_attack &lt; rapid_act*5/6</c>
        /// (<c>:378</c>) — <b>identical to Thrust's</b>; only the swept geometry differs. This read
        /// <c>TAttack &gt; rapid*1/6</c> until 2026-10-03, which opened the window a third of the
        /// attack early and made it twice as long, so a club damaged during its own wind-up.
        /// </summary>
        private void UpdateHorizontalWindow()
        {
            float rapid = _rapidAct;
            if (State.TAttack < rapid / 2f || State.TAttack >= rapid * 5f / 6f)
            {
                EndStrikeWindow();
                return;
            }

            // AS3 sweeps `X + cos2 * (mindlina + i*stepdlina)` (:399-400): anchored on the weapon's
            // sprung position and the BLADE angle, not re-projected from the hold point along the
            // aim. `_minDlina` is where that fan starts — the data currently sets minlong == long on
            // every melee asset, so the fan collapses to the single tip below and the swept segment
            // is the tip's own motion since the previous frame.
            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 tip = weaponPos + new Vector2(Mathf.Cos(State.Rot), Mathf.Sin(State.Rot)) * _dlina;

            BeginOrExtendStrike(tip);
        }

        /// <summary>mtip 1 — the lunge: one line pushed out by anim * atDlina (WClub.as:435-454).</summary>
        private void UpdateThrustWindow(Vector2 aimTarget)
        {
            float rapid = _rapidAct;
            if (State.TAttack < rapid / 2f || State.TAttack >= rapid * 5f / 6f)
            {
                EndStrikeWindow();
                return;
            }

            // AS3: _loc6_ = X + cos2 * dlina + plX, with plX = cos2 * anim * atDlina.
            // Factoring cos2 out gives dir * (dlina + anim * atDlina) along the aim axis.
            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 tip = weaponPos + DirectionTo(aimTarget, weaponPos) * (_dlina + _anim * AtDlina);

            BeginOrExtendStrike(tip);
        }

        /// <summary>
        /// mtip 2 — one bindMove from mindlina to dlina, taken on the frame t_attack == 1
        /// (WClub.as:469-483), i.e. once, at the END of the attack.
        ///
        /// The damage plan is <b>not</b> emitted here: AS3 arms the bullet in <c>shoot()</c>, called
        /// once from <c>weaponAttack()</c> at attack start for every mtip (<c>:674</c>); this branch
        /// only performs the sweep. See <see cref="Shoot"/>.
        /// </summary>
        private void UpdateOverheadWindow(Vector2 aimTarget)
        {
            if (State.TAttack != 1)
            {
                EndStrikeWindow();
                return;
            }

            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 dir       = DirectionTo(aimTarget, weaponPos);

            HitVolume?.SetActive(true);
            HitVolume?.BindMove(weaponPos + dir * _minDlina, weaponPos + dir * _dlina);

            _prevTip        = weaponPos + dir * _dlina;
            _inStrikeWindow = true;
        }

        /// <summary>Open the strike window on the first active frame, then sweep prevTip → tip.</summary>
        private void BeginOrExtendStrike(Vector2 tip)
        {
            if (!_inStrikeWindow)
            {
                _prevTip        = tip;
                _inStrikeWindow = true;
                HitVolume?.SetActive(true);
            }

            HitVolume?.BindMove(_prevTip, tip);
            _prevTip = tip;
        }

        private void EndStrikeWindow()
        {
            if (!_inStrikeWindow) return;
            HitVolume?.SetActive(false);
            _inStrikeWindow = false;
        }

        /// <summary>Unit vector from origin toward target, falling back to +X when they coincide.</summary>
        private static Vector2 DirectionTo(Vector2 target, Vector2 origin)
        {
            Vector2 d = target - origin;
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Animate the swing arc: AS3's three-phase formula driven by <c>t_attack</c> against
        /// <c>rapid_act</c> (<c>WClub.as:362-374</c>).
        ///
        /// <para>All three phases are evaluated on the same <c>norm = t_attack / rapid_act</c>, which
        /// runs 1 → 0 across the attack, so the value is <b>continuous at both seams</b>: late meets
        /// mid at <c>norm = 5/6</c> (both <c>-0.25</c>), and mid meets early at <c>norm = 1/2</c>
        /// (both <c>1.0</c>). The early branch read <c>1 - norm*2</c> until 2026-10-03 — the oracle's
        /// <c>t_attack*2/rapid_act</c> (<c>:372</c>) inverted. That reversed the arc over the back
        /// half of every swing and broke the <c>1/2</c> seam with a jump from 1.0 straight down to
        /// 0.0.</para>
        /// </summary>
        private void UpdateAnim()
        {
            if (State.TAttack <= 0)
            {
                _anim = 0f;
                return;
            }

            float t    = State.TAttack;
            // AS3 normalises anim by rapid_act (WClub.as:362-374: every phase test is
            // `t_attack >= rapid_act * k`), so the arc keeps its shape when a speed perk
            // shortens the swing rather than playing a truncated version of it.
            float rap  = _rapidAct;
            float norm = t / rap;   // 1→0 as attack progresses

            // AS3 three-phase — one `norm` in all three, which is what makes the seams meet:
            //   late  (norm >= 5/6): anim = -(1 - norm) * 1.5
            //   mid   (norm >= 1/2): anim = -0.25 + ((1 - norm) - 1/6) * 3.75
            //   early (norm <  1/2): anim = norm * 2
            if (norm >= 5f / 6f)
                _anim = -(1f - norm) * 1.5f;
            else if (norm >= 0.5f)
                _anim = -0.25f + ((1f - norm) - 1f / 6f) * 3.75f;
            else
                _anim = norm * 2f;
        }
    }
}
