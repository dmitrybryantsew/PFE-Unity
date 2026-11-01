using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Magic;
using PFE.Systems.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Security;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Alicorn / Magic enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitAlicorn.as</c>.
    /// Elite flying caster with levitation, magic projectile, telekinesis, teleport, and shield.
    /// </summary>
    public class AlicornBrain : EnemyBrain
    {
        /// <summary>
        /// AS3 <c>Unit.isFly</c> (<c>Unit.as:284</c>) — declared <c>= false</c>, and the alicorn only
        /// leaves the ground through <c>jump()</c> (<c>UnitAlicorn.as:484-491</c>, which sets
        /// <c>isFly = true</c> after applying <c>dy = -jumpdy</c>).
        ///
        /// <para><b>This used to default to <c>true</c>, and that single literal was the whole
        /// "the alicorn is always airborne" defect.</b> With it true from <c>Awake</c>, and with
        /// <c>TickIdle</c>/<c>TickAlert</c>/<c>TickCombatChase</c> each re-asserting it, the only route
        /// back to the ground was the state-4 descent — which was itself unreachable because those same
        /// overrides reset <see cref="_familyState"/> to 2/3 on the very next tick. The unit drew the
        /// <c>fly</c> row forever. See <see cref="TakeOff"/> for the AS3 takeoff it now mirrors.</para>
        /// </summary>
        [SerializeField] protected bool _isFly;
        /// <summary>
        /// AS3 <c>Unit.isSit</c> (<c>Unit.as:282</c>), default <c>false</c>. It is a base-class flag set
        /// only by <c>setSit()</c> (<c>Unit.as:2512-2517</c>), which no enemy path calls, so for an
        /// alicorn enemy this is always <c>false</c> and the <c>polz</c>/<c>sit</c> arm of
        /// <see cref="ResolveAnimState"/> is unreachable today. It is modelled rather than omitted
        /// because <c>UnitAlicorn.animate()</c> branches on it (<c>UnitAlicorn.as:297-308</c>), and an
        /// omitted branch is the failure mode this guide set exists to prevent.
        /// </summary>
        [SerializeField] protected bool _isSit;
        [SerializeField] protected float _flightSpeed = 3.5f;
        [SerializeField] protected float _magicRangePixels = 480f;

        /// <summary>
        /// AS3 <c>jumpdy</c> (<c>Unit.as:236</c>, family row <c>jump='15'</c>) — the takeoff impulse
        /// <c>jump()</c> applies. Positive here because the port's <c>JumpVertical</c> is positive-up
        /// while AS3's <c>dy = -jumpdy</c> is positive-down (the mirror documented in the family guide
        /// §4).
        /// </summary>
        [SerializeField] protected float _jumpForce = 15f;

        /// <summary>Vertical speed of the state-4 descent, in px per frame.</summary>
        [SerializeField] protected float _descentSpeed = 2f;

        /// <summary>
        /// AS3 <c>t_fall</c> (<c>UnitAlicorn.as:103</c>, <c>int = 0</c>) — consecutive ticks spent
        /// <b>on the ground</b> while not flying.
        /// </summary>
        /// <remarks>
        /// <para>Reset to zero on every airborne tick and incremented on every grounded one
        /// (<c>:842</c> and <c>:846-855</c>). Past <see cref="FallTicksBeforeLift"/> it makes the unit
        /// fly again, with no impulse: <c>if(this.t_fall &gt; 6) { isFly = true; }</c>.</para>
        ///
        /// <para><b>Its absence was a real, observable defect.</b> Without it the port had exactly two
        /// ways into the air: the combat take-off (<see cref="ConsiderTakeOff"/>, which needs a target
        /// at least 40 px <i>above</i>) and nothing else. So once an alicorn landed it was a ground unit
        /// for the rest of its life. A pair that landed on a table stayed there — it could not descend
        /// off the prop and it could not fly off it either — and a calm alicorn in an empty room never
        /// hovered again. The oracle's alicorn is airborne by default and only touches down between
        /// <c>t_fall</c> cycles.</para>
        /// </remarks>
        protected int _tFall;

        /// <summary>
        /// <c>this.t_fall &gt; 6</c> (<c>UnitAlicorn.as:851</c>) — the grounded ticks before an alicorn
        /// lifts off again, i.e. seven.
        /// </summary>
        protected const int FallTicksBeforeLift = 6;

        /// <summary>
        /// The unit's X, in <b>Unity world units</b>, at the moment it entered the state-4 descent —
        /// the origin the ±100 px offset is measured from, as <c>X</c> is in <c>setCel(null, X + …)</c>
        /// (<c>UnitAlicorn.as:606</c>).
        /// </summary>
        protected float _descentStartX;

        /// <summary>
        /// <c>X + Math.random() * 200 - 100</c> minus <c>X</c> (<c>:606</c>) — the descent's horizontal
        /// target as an offset from where the unit entered the state, in pixels, ±100.
        /// </summary>
        protected float _descentOffsetPixels;

        // Timers
        protected int _aiTCh;
        /// <summary>
        /// <b>Removed:</b> the port used to keep a `_teleportCooldownTicks` here, gating an invented
        /// close-range `TryTeleportAway()`. AS3 has no such cooldown — the teleport is paced by
        /// <c>t_gotov</c>, the super's own wind-up, and by nothing else. <c>BossAlicornBrain</c> declares
        /// its own field of that name for its own teleport, which is unaffected.
        /// </summary>
        protected int _spellCooldownTicks;

        /// <summary>
        /// AS3 <c>aiAttackT = 16</c> (<c>UnitAlicorn.as:1016</c>) — how many frames the weapon's trigger
        /// is held once the attack decision is made. The weapon fires on each of them
        /// (<c>:1018-1023</c>: <c>--aiAttackT; if(aiAttackT &gt; 0) currentWeapon.attack();</c>), and
        /// the weapon's own <c>t_attack</c> paces the individual rounds.
        /// </summary>
        protected const int AttackBurstTicks = 16;

        /// <summary>Frames left in the current weapon burst; 0 when the alicorn is not firing.</summary>
        protected int _attackBurstTicks;

        /// <summary>
        /// Family internal state (UnitAlicorn.as:493-1085):
        /// 0: Idle, 1: Patrol, 2: Alert, 3: CombatChase, 4: Descend-and-land
        /// </summary>
        protected int _familyState;

        /// <summary>
        /// AS3 <c>aiState == 4</c> — "fly to a point far below and land"
        /// (<c>UnitAlicorn.as:603-608</c>, exit at <c>:833-837</c>).
        ///
        /// <para><b>Why this is a flag and not just <c>_familyState == 4</c>.</b> The port set
        /// <c>_familyState</c> at the top of every tick override, so the 4 written by the descent
        /// decision was overwritten by <c>1</c> on the very next tick: the descent lasted exactly one
        /// frame and the unit could never reach the ground. AS3's <c>aiState</c> is not recomputed
        /// while it holds 4 either — it is cleared by <c>isFly = false; aiState = 1</c> on landing.
        /// Keeping the mode in its own flag makes the descent run to completion regardless of which
        /// state tick is dispatching.</para>
        /// </summary>
        protected bool _descending;

        // ── the pack (UnitAlicorn.as:119, :386-413, :579-586) ────────────────────────────────────
        //
        // Four behaviours that only make sense for a unit that lives among siblings: the shout with no
        // hearing gate, the shared sighting, the reinforcement call, and the half-damage window that
        // follows being startled. The predicates are in AlicornPackRules (offline-testable); these are
        // the fields they read.

        /// <summary>
        /// AS3 <c>t_alarm</c> (<c>:119</c>) — <c>Math.round(Math.random() * 600 + 1500)</c>, 1500..2100.
        /// Counts down <b>only while searching</b> (<c>:579-586</c>) and fires once, spawning another
        /// alicorn at a random <c>enspawn</c> point.
        /// </summary>
        protected int _tAlarm;

        /// <summary>AS3 <c>t_alarm</c>, exposed for overlays and fixtures.</summary>
        public int ReinforcementTicks => _tAlarm;

        /// <summary>
        /// AS3 <c>this.mblast = new Spell(this, "sp_blast")</c> (<c>:228</c>) — a bare <see cref="Spell"/>
        /// owned by the unit, <b>not</b> an inventory entry.
        ///
        /// <para><b>This is why the alicorn needed no <c>SpellBook</c>.</b> AS3's enemy casters hold their
        /// spell directly (<c>mblast</c> is a field of <c>UnitAlicorn</c>), and the book exists only to be
        /// the <i>player's</i> collection. Built lazily on the first reflex so a tier that never fires it
        /// never allocates.</para>
        /// </summary>
        private Spell _mBlast;

        /// <summary>The <see cref="ISpellHost"/> the alicorn's spell casts through.</summary>
        private ISpellHost _spellHost;

        /// <summary>AS3 <c>overLook</c> — see <see cref="AlicornPackRules.LooksAround"/>.</summary>
        protected bool _overLook;

        /// <summary>Whether the vision cone is currently dropped. For overlays and fixtures.</summary>
        public bool LooksAround => _overLook;

        public bool IsFlying => _isFly;
        public bool IsSitting { get => _isSit; set => _isSit = value; }
        public bool IsDescending => _descending;
        public int FamilyState => _familyState;

        /// <summary>
        /// The unit this brain drives, as the base type. <c>_controller</c> is protected on
        /// <see cref="EnemyBrain"/>, and the telekinesis helper is a plain class rather than a nested
        /// one, so it needs a public read of it.
        /// </summary>
        public UnitController SelfController => _controller;

        /// <summary>Tile queries — raycasts for the telekinesis line-of-sight test, and the pixel origin.</summary>
        public ITileQueryService TileQueryService => _tileQuery;

        /// <summary>
        /// The room the unit is standing in, for its object physics layer. Null before
        /// <see cref="EnemyBrain.Initialize"/> has run or outside a generated room.
        /// </summary>
        public RoomInstance Room => _controller != null ? _controller.Room : null;

        /// <summary>
        /// AS3 <c>tr</c> — <c>1</c>/<c>2</c>/<c>3</c>, or <c>0</c> when the brain is attached to
        /// something that is not an <see cref="AlicornController"/> (a bare test spawn). Every consumer
        /// goes through <see cref="AlicornVariantRules.For"/>, which treats <c>0</c> as the <c>tr1</c>
        /// row — the same row the oracle's constructor falls through to for an unrecognised id.
        /// </summary>
        public int Tier => _controller is AlicornController alicorn ? alicorn.Tier : 0;

        /// <summary>
        /// AS3 <c>UnitAlicorn.teleObj</c> and its <c>t_tele</c> cycle — see
        /// <see cref="AlicornTelekinesis"/>. Built on first use rather than in <c>Awake</c>, because it
        /// needs nothing but <c>this</c> and building it eagerly would allocate for the tiers that never
        /// reach a grab.
        /// </summary>
        private AlicornTelekinesis _telekinesis;

        private AlicornTelekinesis Telekinesis => _telekinesis ??= new AlicornTelekinesis(this);

        /// <summary>AS3 <c>t_tele</c>, or <c>0</c> before the first tick. For overlays and fixtures.</summary>
        public int TelekinesisPhase => _telekinesis != null ? _telekinesis.Phase : 0;

        /// <summary>True while this alicorn is holding a unit or a prop.</summary>
        public bool IsHoldingWithTelekinesis => _telekinesis != null && _telekinesis.IsHolding;

        /// <summary>
        /// Release whatever is held, with no impulse — AS3 <c>dropTeleObj()</c>. Called on death
        /// (<c>UnitAlicorn.die():452</c>) and on teardown; safe when nothing is held.
        /// </summary>
        public void DropTelekinesis()
        {
            _telekinesis?.Drop();
        }

        // ── the super (UnitAlicorn.as:526-533, :715-735, :1102-1138) ─────────────────────────────

        /// <summary>
        /// AS3 <c>t_gotov</c> — the wind-up counter. <b>Counts up only while the unit is aware</b>
        /// (<c>:727-735</c> is the <c>else</c> of <c>aiSpok == 0</c>), and is reset only by
        /// <see cref="SuperSila"/> — so it accumulates across engagements rather than restarting when
        /// the unit loses its target.
        /// </summary>
        protected int _tGotov;

        /// <summary>AS3 <c>t_super</c> — ticks left in the current super; 0 when none is running.</summary>
        protected int _tSuper;

        /// <summary>AS3 <c>superInvis</c> — set by the <c>tr1</c> cloak, cleared by <c>superSilaVse</c>.</summary>
        protected bool _superInvis;

        /// <summary>Ticks the psionic weapon stays on the trigger — the <c>tr3</c> super.</summary>
        protected int _psyFireTicks;

        /// <summary>Whether the cloak is up. For overlays and fixtures.</summary>
        public bool IsSuperCloaked => _superInvis;

        /// <summary>AS3 <c>t_super</c>, exposed.</summary>
        public int SuperTicksRemaining => _tSuper;

        /// <summary>AS3 <c>t_gotov</c>, exposed.</summary>
        public int SuperWindUpTicks => _tGotov;

        /// <summary>
        /// <c>if(t_super &gt; 0) { --t_super; if(t_super &lt;= 0) superSilaVse(); }</c> (<c>:526-533</c>).
        /// </summary>
        protected virtual void TickSuperTimer()
        {
            if (_tSuper <= 0) return;

            _tSuper--;
            if (AlicornVariantRules.ShouldEndSuper(_tSuper))
            {
                SuperSilaVse();
            }
        }

        /// <summary>
        /// <c>if(aiSpok &gt; 0) { ++t_gotov; if(t_gotov &gt; tGotov) superSila(); }</c> (<c>:727-735</c>).
        /// </summary>
        /// <remarks>
        /// <b>The gate is the aware arm, not the calm one.</b> The oracle's <c>if(aiSpok == 0)</c> branch
        /// looks ahead and does <i>not</i> advance the counter; only the <c>else</c> does. Reading it the
        /// other way round would make every alicorn super itself on its first idle frame and then never
        /// again. There is deliberately no <c>else</c> here: <c>t_gotov</c> is not reset when awareness
        /// drops, which is what lets a long fight build up to the super across several sightings.
        /// </remarks>
        protected virtual void TickSuperWindUp()
        {
            if (_blackboard.AlertTimerTicks <= 0) return;
            if (_controller == null || !_controller.IsAlive) return;

            _tGotov++;
            if (AlicornVariantRules.IsSuperReady(_tGotov))
            {
                SuperSila();
            }
        }

        /// <summary>
        /// AS3 <c>superSila()</c> (<c>:1102-1138</c>) — the per-tier special, then the cooldown and the
        /// duration.
        /// </summary>
        /// <remarks>
        /// <para><b>Three kinds, and each one is the tier's identity:</b></para>
        /// <list type="bullet">
        ///   <item><description><b>1 — cloak</b> (<c>tr1</c>): <c>superInvis = true; isVis = false;</c>.
        ///     The unit keeps fighting while invisible; <c>superSilaVse</c> puts it back after
        ///     <c>t_super</c>. The port's <see cref="UnitController.SetVisible"/> is the same write —
        ///     it hides the sprite and nothing else, so the cloaked alicorn is still shootable, which is
        ///     the oracle's behaviour.</description></item>
        ///   <item><description><b>2 — teleport</b> (<c>tr2</c>): <c>actPort()</c>, but only while alert
        ///     (<c>aiState == 2</c>) or while chasing <i>and</i> either inside 200 px or on a coin flip
        ///     (<c>aiState == 3 &amp;&amp; (rasst2 &lt; 200*200 || isrnd())</c>). The coin is why the
        ///     super sometimes looks like it did nothing.</description></item>
        ///   <item><description><b>3 — psionic bolt</b> (<c>tr3</c>): <c>psyWeapon.attack()</c>, one
        ///     trigger pull on the second weapon the tier equips. Not a burst — <c>attack()</c> is
        ///     called once, and the weapon's own <c>t_attack</c> paces the round.</description></item>
        /// </list>
        ///
        /// <para><b>The cooldown and the duration are written for every kind, including a kind that
        /// could not fire.</b> <c>t_gotov = floor(tUsed * (0.8..1.2))</c> and <c>t_super = tSuper</c> are
        /// after the branch, not inside it — so a <c>tr3</c> alicorn whose psy mount failed to build
        /// still pays the cooldown, which is what the oracle does.</para>
        /// </remarks>
        protected virtual void SuperSila()
        {
            AlicornTuning tuning = AlicornVariantRules.For(Tier);

            switch (tuning.SuperSilaTip)
            {
                case 1:
                    _superInvis = true;
                    _controller?.SetVisible(false);
                    break;

                case 2:
                    if (_familyState == 2)
                    {
                        TryTeleportAway();
                    }
                    else if (_familyState == 3)
                    {
                        float distanceSq = _blackboard.TargetDistanceSq;
                        if (distanceSq < 200f * 200f || UnityEngine.Random.value < 0.5f)
                        {
                            TryTeleportAway();
                        }
                    }

                    break;

                case 3:
                    // `if(this.psyWeapon) this.psyWeapon.attack();` — a mount that does not exist is
                    // not an error here; the tier-3 alicorn simply spends its super on nothing.
                    if (EnemyController != null && EnemyController.GetWeaponMount("alipsy") != null)
                    {
                        _psyFireTicks = 1;
                    }

                    break;
            }

            _tGotov = AlicornVariantRules.SuperCooldownAfterUse(Tier, UnityEngine.Random.value);
            _tSuper = AlicornVariantRules.SuperDurationTicks;
        }

        /// <summary>
        /// AS3 <c>superSilaVse()</c> (<c>:1134-1138</c>) — only the cloak has an end state:
        /// <c>superInvis = false; isVis = true;</c>.
        /// </summary>
        protected virtual void SuperSilaVse()
        {
            if (!_superInvis) return;

            _superInvis = false;
            _controller?.SetVisible(true);
        }

        // ── the reactive reflexes (UnitAlicorn.damage():415-429) ──────────────────────────────────

        /// <summary>
        /// AS3 <c>UnitAlicorn.blasted</c> — the once-per-life latch shared by both reactive reflexes.
        /// Set by whichever fires first, cleared only in <c>setNull()</c> (<c>:481</c>), i.e. never for a
        /// spawned unit.
        /// </summary>
        protected bool _blasted;

        /// <summary>Whether the once-per-life reactive reflex has already been spent.</summary>
        public bool HasBlasted => _blasted;

        /// <summary>
        /// AS3 <c>UnitAlicorn.damage(param1, param2, param3, param4)</c> (<c>:415-429</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The two variants behave differently and the difference is the point.</b></para>
        /// <list type="bullet">
        ///   <item><description><c>tr1</c>: <c>mblast.cast(X, Y); blasted = true;</c> — and then the
        ///     oracle <b>falls through</b> to <c>super.damage()</c>, so the hit still lands. Returns
        ///     <c>false</c>.</description></item>
        ///   <item><description><c>tr2</c>: <c>actPort(true); blasted = true; return 0;</c> — the hit
        ///     never lands. Returns <c>true</c>, which the resolver turns into
        ///     <see cref="DamageVerdict.Refused"/>.</description></item>
        /// </list>
        ///
        /// <para><b>Both are gated on the unit being <i>calm</i></b> (<c>aiState &lt;= 1</c>), so neither
        /// is a combat action — they are startle reflexes that fire once, when something walks up on an
        /// unaware alicorn and hits it. And both require <c>osob</c>, the per-unit roll from the
        /// constructor, so the same tier is only <i>sometimes</i> reflexive.</para>
        ///
        /// <para><b><c>sp_blast</c> is now real</b> — <see cref="CastReactiveBlast"/> constructs the
        /// unit's own <c>mblast</c> and casts it through <see cref="NpcSpellHost"/>.
        /// <b>It deals no damage</b>: the row's <c>dam='20'</c> is the <i>velocity ceiling</i> of a radial
        /// throw, so what the reflex actually does is peel the attacker off. See
        /// <see cref="SpellBlastMath"/>.</para>
        /// </remarks>
        public override bool OnDamageContext(in PFE.Systems.Weapons.DamageContext context)
        {
            if (_controller == null || !_controller.IsAlive) return false;
            if (_blasted) return false;
            if (!(_controller is AlicornController alicorn)) return false;

            // `param3.weap.tip == 1` — a melee weapon. `DamageContext.IsMelee` is the same fact: AS3
            // sets `tipBullet = 1` only from WClub, and Weapon.create routes tip 1 to WClub.
            if (AlicornVariantRules.ShouldBlastOnMeleeHit(
                    alicorn.Tier, alicorn.Osob, _blasted, context.IsMelee, _familyState))
            {
                _blasted = true;
                CastReactiveBlast();
                return false;   // `blasted = true;` then falls through to super.damage()
            }

            // `param3.weap.owner.player` — the shot must be the PLAYER's. `OwnerFaction` is the port's
            // stand-in for that identity test; a turret or a fellow monster cannot trigger this.
            bool fromPlayer = context.OwnerFaction == FactionType.Player;
            if (AlicornVariantRules.ShouldPortAwayFromPlayerHit(
                    alicorn.Tier, alicorn.Osob, _blasted, fromPlayer, _familyState))
            {
                _blasted = true;
                TryTeleportAway(reactive: true);
                return true;    // `return 0;` — the hit is refused
            }

            return false;
        }

        /// <summary>
        /// AS3 <c>this.mblast.cast(X, Y)</c> — the <c>sp_blast</c> the <c>tr1</c> reflex fires at its own
        /// feet.
        /// </summary>
        /// <remarks>
        /// <para><b>What it does, because the row's <c>dam='20'</c> is not damage.</b> <c>cast_blast</c>
        /// assigns a radial <i>velocity</i> to every eligible unit within <c>rad='500'</c>, strongest at
        /// the centre and clamped at <c>dam × spellPower</c> = 40 px/frame. Nothing takes HP damage. The
        /// reflex's whole job is to peel a melee attacker off an unaware alicorn, and it does that by
        /// throwing them.</para>
        ///
        /// <para><b>The spell is owned by the unit, not by a book.</b> AS3's constructor does
        /// <c>this.mblast = new Spell(this, "sp_blast")</c> (<c>:228</c>) — the same shape as the
        /// telekinesis helper. <c>SpellBook</c> is the <i>player's</i> collection and is not involved.</para>
        ///
        /// <para><b>Built lazily and rebuilt if the controller changes.</b> The host reads the unit's
        /// transform and its sound service, so it cannot be constructed in <c>Awake</c> before the
        /// definition and controller are attached — and a brain re-attached to another controller would
        /// otherwise cast through the old one's position.</para>
        ///
        /// <para><b>The cast can still refuse, and a refusal is reported once.</b> An NPC skips every
        /// player gate (<c>Spell.as:191-220</c> is inside <c>if(this.player)</c>), so the only refusals
        /// left are "no data row" and "the effect is not ported" — neither of which can happen now that
        /// <c>sp_blast</c> is in <c>Spell.IsPortedEffect</c>. If one does, the reflex is still spent
        /// (<c>blasted</c> is set by the caller) and the log says so rather than pretending a blast
        /// happened.</para>
        /// </remarks>
        protected virtual void CastReactiveBlast()
        {
            EnemyController enemy = EnemyController;
            if (enemy == null) return;

            if (_spellHost == null || !ReferenceEquals(_spellHostUnit, enemy))
            {
                _spellHost = new NpcSpellHost(enemy, AlicornVariantRules.SpellPower);
                _spellHostUnit = enemy;
                _mBlast = null;
            }

            if (_mBlast == null)
            {
                ItemDefinition row = SpellCatalog.Find("sp_blast");
                if (row == null)
                {
                    if (_blastGapReported) return;
                    _blastGapReported = true;
                    Debug.LogWarning(
                        "[Alicorn] tr1 reactive blast: there is no `sp_blast` item row, so the spell " +
                        "cannot be constructed and the reflex is spent with no effect.");
                    return;
                }

                _mBlast = new Spell("sp_blast", row.spellData, _spellHost);
            }

            // `this.mblast.cast(X, Y)` — the coordinates are ignored by the effect: `cast_blast`
            // overwrites them with the owner's own position (:382-383), which is why the host supplies
            // the centre and `Spell`'s arguments are the caster's own.
            SpellCastResult result = _mBlast.Cast(_spellHost.MagicX, _spellHost.MagicY);
            if (!result.Cast && !_blastGapReported)
            {
                _blastGapReported = true;
                Debug.LogWarning(
                    "[Alicorn] tr1 reactive blast was refused (" + result.Refusal + "). The reflex is " +
                    "spent; no blast occurs.");
            }
        }

        /// <summary>The controller the current <see cref="_spellHost"/> wraps, so a re-attach rebuilds it.</summary>
        private EnemyController _spellHostUnit;

        private static bool _blastGapReported;

        // ── alarma (UnitAlicorn.as:431-450) ──────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>loc.land.aliAlarm</c> — set the first time any alicorn in the land dies
        /// (<c>die():466</c>), and read by every later <c>alarma()</c> to choose 75 over 30.
        /// </summary>
        /// <remarks>
        /// <b>A static because the port has no <c>Land</c>.</b> AS3's <c>aliAlarm</c> lives on the land
        /// object, which is the unit's enclosing region — a level, not a room. The port has rooms and no
        /// land, so the nearest honest thing is one flag for the session. It is deliberately not reset
        /// on room change: the oracle's is not reset on anything either, and its effect (a dead alicorn
        /// makes the survivors hunt longer) is exactly what it should be here.
        /// </remarks>
        public static bool LandIsAlarmed { get; set; }

        /// <summary>
        /// AS3 <c>UnitAlicorn.alarma(param1, param2)</c> (<c>:431-450</c>) — the startle, and the
        /// promotion that follows it.
        /// </summary>
        /// <remarks>
        /// <para>The oracle, verbatim:</para>
        /// <code>
        /// if(sost == 1 &amp;&amp; aiState &lt;= 1) {
        ///    super.alarma(param1, param2);
        ///    aiState = 3;
        ///    maxSpok = loc.land.aliAlarm ? 75 : 30;
        ///    aiSpok = maxSpok + 10;
        ///    aiState = 3;
        ///    overLook = true;
        ///    shok = Math.floor(Math.random() * 15 + 5);
        /// }
        /// </code>
        ///
        /// <para><b>An alicorn that is startled goes straight to a full-speed chase</b>, not to a
        /// search — and it gets the land's <i>larger</i> awareness budget, so it holds on far longer than
        /// a zombie would. That is the whole point of the override: the family's alarm is louder than
        /// the base class's.</para>
        ///
        /// <para><b>Two fields are not modelled.</b> <c>overLook</c> widens the look-ahead scan and
        /// <c>shok</c> is a hesitation counter; the port has neither, and inventing a behaviour for them
        /// would be worse than leaving them named. Everything else is exact.</para>
        /// </remarks>
        public override void Alarma(float sourceX = -1f, float sourceY = -1f)
        {
            if (_controller == null || !_controller.IsAlive) return;

            // `sost == 1 && aiState <= 1` — a living unit that is not already searching or chasing.
            // Without this the promotion would re-arm itself every time a neighbour shouted, and the
            // awareness budget would never run down.
            if (_familyState > 1) return;

            base.Alarma(sourceX, sourceY);

            int maxSpok = AlicornVariantRules.AlarmMaxSpok(LandIsAlarmed);
            _blackboard.AlertTimerTicks =
                AlicornVariantRules.AlarmAwareness(maxSpok) * EnemyAwarenessMath.AiCheckIntervalTicks;

            _familyState = 3;
            SetState(EnemyAIState.CombatChase);

            // `shok = Math.floor(Math.random() * 15 + 5)` (:448) — 5..19, assigned (not raised).
            //
            // **This must be written here, and that is not obvious.** The base class arms `shok` inside
            // `SetState(CombatChase)`'s Idle/Patrol arm — but `base.Alarma` above has already moved the
            // state to `Alert`, so that arm never runs and `shok` would stay at 0 for every alarmed
            // alicorn. `AlicornPackRules.ContactDamageScale` would then always answer 1 and the
            // half-damage window would be dead code that looks wired.
            _blackboard.ShockTimerTicks =
                StaggerMath.AlarmTicks(UnityEngine.Random.Range(0, StaggerMath.AlarmRollRange));
        }

        // ── budilo: the shout with no hearing gate (UnitAlicorn.as:386-401) ───────────────────────

        /// <summary>
        /// AS3 <c>UnitAlicorn.budilo(param1 = 500)</c> (<c>:386-401</c>) — a <b>full override</b> of
        /// <c>EnemyBrain.Budilo</c>, and it drops three of the base class's four gates.
        /// </summary>
        /// <remarks>
        /// <para><b>What is dropped:</b> the <c>makeNoise(noiseRun * 1.2)</c> that makes a shout audible
        /// as sound, the <c>radius</c> distance test, and the receiver's <c>ear</c> multiplier. An
        /// alicorn's alarm reaches <b>every living ally in the room</b>, however far away and however
        /// deaf — and it carries the <b>exact</b> aim point instead of a ±125 px smear.</para>
        ///
        /// <para><b>Why an override and not a wider radius.</b> Passing <c>budilo(99999)</c> to the base
        /// would still multiply by the receiver's <c>ear</c>, so a <c>ear='0'</c> ally would still never
        /// hear it — and the base would still make the shout audible, which the oracle's override does
        /// not. The three differences cannot be expressed as a parameter.</para>
        ///
        /// <para><b>Dispatch is virtual, as in the oracle.</b> <c>_loc2_.alarma(celX, celY)</c> calls the
        /// <i>receiver's</i> <c>alarma</c>, so an alicorn neighbour gets this class's promotion
        /// (<see cref="Alarma"/>) while a zombie gets its own. Calling
        /// <c>EnemySensors.RaiseAlarm</c> directly, as the base does, would flatten that.</para>
        /// </remarks>
        protected override void Budilo(float radius = NoiseMath.DefaultAlarmRadius)
        {
            if (_controller == null) return;

            Vector2 selfPx = (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel;

            // `if(celUnit == null) { celX = X; celY = Y; }` (:389-393) then `alarma(celX, celY)`.
            bool hasTarget = _blackboard.HasLastKnownTargetPosition;
            Vector2 aim = hasTarget
                ? _blackboard.LastKnownTargetPosition
                : (_blackboard.TargetUnit != null
                    ? (Vector2)_blackboard.TargetUnit.transform.position * TileQueryConstants.UnitToPixel
                    : selfPx);

            AlicornPackRules.ShoutPosition(hasTarget, aim.x, aim.y, selfPx.x, selfPx.y,
                                           out float shoutX, out float shoutY);

            FactionType faction = _controller.Faction;
            EnemyController[] allies = FindObjectsByType<EnemyController>(FindObjectsSortMode.None);
            for (int i = 0; i < allies.Length; i++)
            {
                EnemyController ally = allies[i];
                if (ally == null || ally.Brain == null) continue;

                if (!AlicornPackRules.AlarmEligible(
                        isSelf: ally.Brain == this,
                        sameFaction: ally.Faction == faction,
                        isAlive: ally.IsAlive))
                {
                    continue;
                }

                ally.Brain.Alarma(shoutX, shoutY);
            }
        }

        // ── telepat: the shared sighting (UnitAlicorn.as:403-413) ─────────────────────────────────

        /// <summary>
        /// <c>if(aiTCh % 30 == 1) this.telepat();</c> (<c>:675-678</c>) — the call site, hoisted out of
        /// the sensor block because the port's perception runs inside <c>base.SimTick</c>.
        /// </summary>
        /// <remarks>
        /// <b>Gated on the sharer holding a target</b>, which is the oracle's <c>if(celUnit)</c> around
        /// the call. Without it a calm alicorn would spend a scene scan every 30 ticks broadcasting
        /// nothing.
        /// </remarks>
        protected virtual void TickPackSighting()
        {
            if (_controller == null || !_controller.IsAlive) return;
            if (!AlicornPackRules.ShouldShareTarget(_aiTCh, sharerHasTarget: _blackboard.TargetUnit != null))
            {
                return;
            }

            Telepat();
        }

        /// <summary>
        /// AS3 <c>telepat()</c> (<c>:403-413</c>) — hand the current target to every living ally that has
        /// none.
        /// </summary>
        /// <remarks>
        /// <para><b>What the port's ally actually gets, and the honest limit of it.</b> AS3's
        /// <c>setCel(celUnit)</c> sets <c>celUnit</c> plus <c>celX</c>/<c>celY</c> and nothing else — no
        /// awareness budget, no state change. The port's equivalent is
        /// <see cref="EnemyBrain.ShareTarget"/>: <c>TargetUnit</c> plus the last-known position.</para>
        ///
        /// <para><b>The divergence that matters.</b> <c>EnemySensors.Evaluate</c> clears
        /// <c>TargetUnit</c> on the first tick the target is not visible, whereas AS3's <c>celUnit</c>
        /// survives a failed <c>findCel</c>. So what reliably survives the hand-off here is the
        /// <b>position</b>, and its observable effect is that an ally already in Alert or Chase walks to
        /// where the sharer is aiming (<c>EnemyBrain.TickAlert</c>'s
        /// <c>HasLastKnownTargetPosition</c> branch). An ally that is Idle or Patrol gains nothing it can
        /// act on — which is also true in the oracle, whose calm branches re-aim with
        /// <c>setCel(null, …)</c>.</para>
        ///
        /// <para><b>The position is the target's feet, not the oracle's offset point.</b> AS3 aims at
        /// <c>param1.X + param1.scX / 4 * param1.storona</c>, i.e. a quarter of a body ahead of the
        /// target in its facing direction — an aiming lead. The port's <c>LastKnownTargetPosition</c> is
        /// the target's origin, so a shared objective is up to ~14 px behind the oracle's. Recorded, not
        /// corrected: inventing a lead here would put the port's aim point out of step with the one the
        /// sensors themselves write.</para>
        /// </remarks>
        protected virtual void Telepat()
        {
            UnitController target = _blackboard.TargetUnit;
            if (target == null || _controller == null) return;

            Vector2 aim = _blackboard.HasLastKnownTargetPosition
                ? _blackboard.LastKnownTargetPosition
                : (Vector2)target.transform.position * TileQueryConstants.UnitToPixel;

            FactionType faction = _controller.Faction;
            EnemyController[] allies = FindObjectsByType<EnemyController>(FindObjectsSortMode.None);
            for (int i = 0; i < allies.Length; i++)
            {
                EnemyController ally = allies[i];
                if (ally == null || ally.Brain == null) continue;

                if (!AlicornPackRules.CanReceiveTelepathy(
                        isSelf: ally.Brain == this,
                        sameFaction: ally.Faction == faction,
                        isAlive: ally.IsAlive,
                        alreadyHasTarget: ally.Brain.Blackboard.TargetUnit != null))
                {
                    continue;
                }

                ally.Brain.ShareTarget(target, aim);
            }
        }

        // ── shok: the contact attack (UnitAlicorn.as:1008-1013) ──────────────────────────────────

        /// <summary>
        /// <c>if(celUnit &amp;&amp; celDX &lt; optDistAtt &amp;&amp; …) attKorp(celUnit, shok &lt;= 0 ? 1 : 0.5)</c>
        /// (<c>:1008-1013</c>) — the alicorn's melee, <b>halved while it is startled</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>In every state, not just the chase.</b> The oracle's block sits after the whole
        /// <c>aiState</c> chain and outside it, so a calm alicorn that has been <i>told</i> where the
        /// player is (<see cref="Telepat"/>) still swings when he walks into it. Restricting this to
        /// <c>TickCombatChase</c> would make the shared sighting inert.</para>
        ///
        /// <para><b>The empty body in the oracle is not a bug to port.</b>
        /// <c>if(attKorp(…) || isrnd(0.2)) { }</c> has no body at all — the only effect is
        /// <c>attKorp</c>'s own, and the coin flip is dead. Nothing is missing here.</para>
        ///
        /// <para><b><c>shok</c> is the port's <c>EnemyBlackboard.ShockTimerTicks</c></b> — see
        /// <see cref="AlicornPackRules.ContactDamageScale"/> for why the halving is contact-only.</para>
        /// </remarks>
        protected virtual void TickContactAttack()
        {
            UnitController target = _blackboard.TargetUnit;
            if (target == null || _controller == null || !_controller.IsAlive) return;

            // `celDX = celX - X` (:4733) — computed live from the two positions, NOT read off
            // `EnemyBlackboard.TargetDeltaX`.
            //
            // That field is written only by a sensor *sighting*, and it is sampled at the raycast
            // cadence. A target acquired through `Telepat` has never been sighted by this unit at all,
            // so its delta is whatever the last sighting left behind — possibly nothing. Reading it here
            // would make the contact attack fire at a stale range for exactly the units the shared
            // sighting was meant to help.
            float deltaX = (target.transform.position.x - _controller.transform.position.x)
                           * TileQueryConstants.UnitToPixel;

            if (!AlicornPackRules.InContactReachX(deltaX)) return;

            _controller.TryContactAttack(
                target, AlicornPackRules.ContactDamageScale(_blackboard.ShockTimerTicks));
        }

        // ── overLook ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Push <c>overLook</c> onto the sensors — AS3 <c>Unit.overLook</c> reaching
        /// <c>findCel</c>'s <c>look(cel, this.overLook || param1)</c> (<c>Unit.as:4671</c>).
        /// </summary>
        /// <remarks>
        /// <para>The port's equivalent flag is <c>EnemySensors.Omnidirectional</c>, which
        /// <c>EnemySensors.CheckLineOfSight</c> reads to drop the facing cone and the vertical-angle
        /// penalty — the same two things <c>look()</c> reads <c>param2</c> for.</para>
        ///
        /// <para><b>One tick of lag, by construction.</b> The oracle writes <c>overLook</c> in the
        /// <c>aiState</c> branches (<c>:870</c>, <c>:882</c>, <c>:933</c>) which run <i>before</i>
        /// <c>findCel()</c>; the port's <c>base.SimTick</c> runs perception before the state dispatch, so
        /// this reads the value the previous tick wrote. The alternative — writing the flag from inside
        /// each of the four state ticks — is four writers for one fact, which is how the two halves drift
        /// apart.</para>
        /// </remarks>
        protected virtual void ApplyOverLook()
        {
            _overLook = AlicornPackRules.LooksAround(_familyState);
            _sensors.Omnidirectional = _overLook;
        }

        // ── the pole objective (UnitAlicorn.as:534-551, :458-463) ─────────────────────────────────

        /// <summary>AS3 <c>UnitAlicorn.pole</c> — this unit guards a named lock and opens it when wounded.</summary>
        protected bool _pole;

        /// <summary>AS3 <c>UnitAlicorn.poleId</c> — the <c>allid</c> tag the broadcast carries.</summary>
        protected string _poleId = string.Empty;

        /// <summary>AS3 <c>t_pole</c> — the cosmetic <c>unlock</c> sparkle's 5-tick pace (<c>:536-542</c>).</summary>
        protected int _poleTicks;

        /// <summary>Whether this unit still carries an unopened pole objective.</summary>
        public bool CarriesPole => _pole;

        /// <summary>The tag its <c>unlock</c>/<c>open</c> broadcast carries; may be empty.</summary>
        public string PoleId => _poleId;

        /// <summary>
        /// AS3's constructor, <c>:213-222</c>: <c>if(param3 &amp;&amp; param3.@pole.length()) { poleId =
        /// param3.@pole; pole = true; }</c> and then <c>if(aiTip == "pole") pole = true;</c>.
        /// </summary>
        /// <remarks>
        /// <b>The two spellings do different things.</b> The <c>pole</c> <i>attribute</i> supplies the
        /// tag; <c>ai="pole"</c> only raises the flag and leaves the tag empty — and an empty tag is not
        /// "no broadcast": <c>allAct</c> treats a null <c>allid</c> as "every object", so an
        /// <c>ai="pole"</c> placement with no <c>pole</c> attribute opens <b>everything</b> in the room
        /// that answers <c>open</c>. That is the oracle's behaviour, not a bug to fix here.
        /// </remarks>
        public void AssignPole(string poleId)
        {
            if (!string.IsNullOrEmpty(poleId))
            {
                _poleId = poleId;
                _pole = true;
                return;
            }

            // `ai="pole"` with no attribute: the flag only.
            _pole = true;
        }

        /// <summary>
        /// AS3 <c>loc.allAct(this, "unlock", poleId); loc.allAct(this, "open", poleId); pole = false;</c>
        /// — <c>:549-551</c> and again in <c>die():458-463</c>.
        /// </summary>
        /// <remarks>
        /// The sender is passed as <c>null</c> rather than as this unit: AS3's sender is a <c>Unit</c>,
        /// the broadcast's exclusion test compares it against the room's <i>objects</i>, and the two lists
        /// are disjoint — so "exclude the sender" never excluded anything. <c>RunFromScript</c> is the
        /// same call with the same null, and it is the existing seam for it.
        /// </remarks>
        public int OpenPole()
        {
            if (!_pole) return 0;

            _pole = false;
            _poleTicks = 0;

            RoomInstance room = Room;
            ObjectInteractionBroadcast broadcast = ObjectInteractionBroadcast.Current;
            if (room == null || broadcast == null)
            {
                if (!_poleGapReported)
                {
                    _poleGapReported = true;
                    Debug.LogWarning(
                        "[Alicorn] a pole alicorn reached its open threshold but " +
                        (room == null ? "has no room" : "no ObjectInteractionBroadcast is published") +
                        "; its `unlock`/`open` broadcast was dropped. tag='" + _poleId + "'.");
                }

                return 0;
            }

            int sent = broadcast.RunFromScript(room, "unlock", _poleId);
            sent += broadcast.RunFromScript(room, "open", _poleId);
            return sent;
        }

        private static bool _poleGapReported;

        /// <summary>
        /// AS3's pole block at the top of <c>control()</c> (<c>:534-551</c>).
        /// </summary>
        /// <remarks>
        /// <para>The oracle's shape is a <b>guard post</b>: while <c>hp &gt;= maxhp * 0.7</c> the block
        /// <c>return</c>s out of <c>control()</c> entirely, so a pole alicorn above 70 % health does
        /// <i>nothing</i> — no patrol, no chase, no attack. The moment it drops below, it broadcasts and
        /// stops being a pole alicorn for the rest of its life.</para>
        ///
        /// <para><b>Port deviation, and it is deliberate.</b> The oracle's <c>return</c> exits the AI
        /// function, not <c>step()</c>, so perception keeps running. The port's <c>base.SimTick</c> does
        /// both sensors and state dispatch in one call, so an early return here would blind the guard.
        /// The guard is therefore applied inside the four state ticks via <see cref="PoleGuardActive"/>,
        /// which reproduces "does nothing but stand there" without losing the sighting that makes it
        /// open the door in the first place.</para>
        /// </remarks>
        protected virtual void TickPoleObjective()
        {
            if (!_pole) return;

            // `++t_pole; if(t_pole >= 5) { t_pole = 0; Emitter.emit("unlock", …) }` — the sparkle above
            // the lock. Cosmetic and unported: the port has no emitter for a bare unit effect.
            if (++_poleTicks >= 5) _poleTicks = 0;

            if (_controller == null)
            {
                return;
            }

            float hp = _controller.CurrentHealth;
            float maxHp = _controller.MaxHealth;
            if (!AlicornVariantRules.ShouldOpenPole(hp, maxHp))
            {
                return;
            }

            OpenPole();
        }

        /// <summary>
        /// Whether the pole guard is still holding this unit at its post — <c>hp &gt;= maxhp * 0.7</c>.
        /// Read by the four state ticks; see <see cref="TickPoleObjective"/> for why the oracle's early
        /// <c>return</c> is expressed here instead.
        /// </summary>
        protected bool PoleGuardActive
        {
            get
            {
                if (!_pole) return false;
                if (_controller == null) return false;
                return !AlicornVariantRules.ShouldOpenPole(
                    _controller.CurrentHealth, _controller.MaxHealth);
            }
        }


        /// <summary>
        /// The unit as an <see cref="EnemyController"/>, which is where the weapon mounts live.
        /// <c>_controller</c> is declared on <see cref="EnemyBrain"/> as the base <c>UnitController</c>,
        /// so the weapon half needs the cast; it is null only for a brain attached to something that is
        /// not an enemy controller, which is not a state an alicorn can be in.
        /// </summary>
        protected EnemyController EnemyController => _controller as EnemyController;

        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            ApplyDefinitionTuning();

            // `t_alarm = Math.round(Math.random() * 600 + 1500)` — rolled once, in the constructor in
            // AS3 (`:119`). Awake is the port's constructor for a spawned unit, and it is deliberately
            // not in ApplyDefinitionTuning: that method runs again on every definition assignment, and a
            // re-roll there would restart the countdown whenever the definition was re-applied.
            _tAlarm = AlicornPackRules.RollAlarmSpawnTicks(UnityEngine.Random.value);
        }

        /// <summary>
        /// Release on teardown. A caster removed mid-hold would otherwise leave its victim stamped
        /// <c>levit = 2</c> with no owner — and <see cref="UnitController.ApplyGravity"/>'s levitated arm
        /// only damps, it never pulls, so that victim hangs in the air forever.
        /// </summary>
        protected override void OnDestroy()
        {
            _telekinesis?.Drop();
            base.OnDestroy();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ApplyDefinitionTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyDefinitionTuning();
        }

        protected virtual void ApplyDefinitionTuning()
        {
            if (_controller == null || _controller.Definition == null) return;
            UnitDefinition def = _controller.Definition;

            _flightSpeed = def.moveSpeed > 0 ? def.moveSpeed : 3.5f;
            _patrolSpeed = _flightSpeed * 0.7f;
            _chaseSpeed = _flightSpeed;
            _alertSpeed = _flightSpeed * 0.8f;
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;
            if (_spellCooldownTicks > 0) _spellCooldownTicks--;

            // `if(this.t_super > 0) { … }` (:526-533) — the first block of control(), before the pole.
            TickSuperTimer();

            // `if(this.pole) { … }` — the first thing control() does that is not a state branch
            // (:534-551). Before the shield ladder, because the oracle's early `return` inside it skips
            // everything below, shield included.
            TickPoleObjective();

            if (_controller is AlicornController alicorn)
            {
                // AS3 `UnitAlicorn.control():571` reads `aiSpok > 0` — the awareness budget, which
                // survives losing sight of the target. This used to pass `TargetUnit != null`, i.e. "is
                // the target visible right now": a different and much stricter condition, which stalled
                // the shield timer at 150 the moment the player broke line of sight.
                alicorn.UpdateShieldTick(_blackboard.AlertTimerTicks > 0);
            }

            // Before the state tick, so the trigger is released on the same tick the unit dies or the
            // burst expires rather than one tick later.
            UpdateWeaponFire();

            // `if(aiSpok > 0) { ++t_gotov; if(t_gotov > tGotov) superSila(); }` (:727-735) — before the
            // state dispatch, as the oracle has it, so a super fired this tick is visible to the same
            // tick's aiState branches.
            TickSuperWindUp();

            // `overLook` before the sensors, because the oracle writes it in the aiState branches that
            // precede `findCel()` (:868-1002, then :648). The port's dispatch runs the state tick
            // *after* perception, so this reads the value the previous tick's branch wrote — one tick of
            // lag, and the only ordering that keeps a single writer.
            ApplyOverLook();

            base.SimTick(tickIndex);

            if (_controller == null || !_controller.IsAlive)
            {
                // AS3 `die()` drops the held object. The tick guard is the cheap half of that — it
                // catches a brain whose loop has not been unregistered yet.
                _telekinesis?.Drop();
                return;
            }

            // The descent owns the vertical axis until it lands, and it is checked before the flight
            // model because AS3's `aiState == 4` branch (`:824-840`) replaces the hover steering rather
            // than running alongside it.
            //
            // `t_fall` is reset from the state the tick STARTED in, not the one it ended in: `Land()`
            // clears `isFly` and then `:842` (`turnX = turnY = this.t_fall = 0`) still runs, because in
            // AS3 the landing happens *inside* the `if(isFly)` block. Reading `_isFly` after the
            // dispatch would start the countdown on the landing tick and lift the unit off one tick
            // early, every cycle.
            bool wasAirborne = _isFly || _descending;

            if (_descending)
            {
                // Reported after the state tick, because the tick override writes its own aiState
                // (2/3) at the top and the oracle's aiState is 4 for the whole descent.
                _familyState = 4;
                UpdateDescent();
            }
            else if (_isFly)
            {
                UpdateFlightPhysics(tickIndex);
            }

            // `turnX = turnY = this.t_fall = 0;` (:842) / `else { ++this.t_fall; if(this.t_fall > 6)
            // isFly = true; }` (:846-855). AS3's third arm is `else if(levit)`, which neither resets nor
            // increments — a levitated alicorn is held by someone else and is not "falling back to
            // earth", so it must not accumulate toward a take-off it cannot perform.
            if (wasAirborne)
            {
                _tFall = 0;
            }
            else if (_controller == null || !_controller.IsLevitated)
            {
                _tFall++;
                if (_tFall > FallTicksBeforeLift)
                {
                    // Deliberately NOT `TakeOff()`: the oracle writes `isFly = true` alone, with no
                    // `dy` impulse. Going through `TakeOff` would give every cycle a free jump.
                    _isFly = true;
                }
            }

            // `if(celUnit) { if(aiTCh % 30 == 1) this.telepat(); aiSpok = maxSpok + 10; }` (:673-680) —
            // the sighting is shared with the pack, on a 30-tick phase. Before the telekinesis tail
            // because the oracle's own order is :673 → :1026.
            TickPackSighting();

            // `if(celUnit && celDX < optDistAtt && …) attKorp(celUnit, shok <= 0 ? 1 : 0.5)` (:1008-1013)
            // — the contact attack, in EVERY state, which is what makes a shared sighting worth having:
            // a calm alicorn that has been told where the player is still swings at him.
            TickContactAttack();

            // `++this.t_tele;` … the tail of `control():1026-1084`. **Last, and unconditional while
            // alive**, because that is where the oracle puts it: the block sits after every aiState
            // branch and `control()` has no early return, so a descending or a calm alicorn still runs
            // its telekinesis cycle. What the state gates is the *grab* (`aiState <= 1`), not the tick.
            // Running it before the state machine would read a one-frame-stale aiState.
            Telekinesis.Tick();
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.jump()</c> (<c>:484-491</c>) — the only way off the ground:
        /// <c>if(!isFly) dy = -jumpdy * param1; isFly = true;</c>.
        ///
        /// <para>The <c>!isFly</c> guard is load-bearing: <c>jump()</c> is called every tick while the
        /// unit is navigating upward (<c>:997-1001</c>), and without the guard each of those ticks would
        /// re-apply the full impulse. Only the first call moves the unit; the rest merely keep it in the
        /// air.</para>
        ///
        /// <para><paramref name="param"/> is AS3's <c>param1</c> — <c>0.5</c> for the ledge hop in
        /// state 1 (<c>:890</c>/<c>:909</c>), <c>1</c> (or <c>1.5</c> when <c>isPlav</c>) for the combat
        /// takeoff in states 2/3 (<c>:999</c>).</para>
        /// </summary>
        protected virtual void TakeOff(float param = 1f)
        {
            if (!_isFly)
            {
                // Positive-up: the port's JumpVertical takes an "up" value, AS3's `dy = -jumpdy` is
                // positive-down. Carrying the minus sign across drives the unit into the floor.
                JumpVertical(_jumpForce * param);
            }
            _isFly = true;
        }

        /// <summary>
        /// `setCel(null, X + Math.random() * 200 - 100, Y + 1000); aiState = 4;` —
        /// <c>UnitAlicorn.as:606-608</c>, the entry to the descend-and-land state.
        /// </summary>
        /// <remarks>
        /// The <b>±100 px horizontal offset is the load-bearing half</b> and it used to be dropped:
        /// <see cref="UpdateDescent"/> moved straight down. The oracle's state-4 branch does not: it
        /// steers <c>spd.x</c> toward that <c>celX</c> as well as sinking (<c>:790-792</c>), so a
        /// descending alicorn drifts sideways by up to a hundred pixels and can therefore come off the
        /// crate, table or ledge it happens to be over. Moving vertically only, it descends onto the
        /// same prop it left and lands on it again, for ever — the reported "they cant go down from
        /// prop". Capturing the offset here, at the one place the state is entered, keeps the drift
        /// fixed for the whole descent exactly as <c>setCel</c> does.
        /// </remarks>
        protected virtual void BeginDescent()
        {
            _descending = true;
            _descentStartX = _controller != null ? _controller.transform.position.x : 0f;
            _descentOffsetPixels = UnityEngine.Random.value * 200f - 100f;
        }

        /// <summary>
        /// AS3's state-4 descent (<c>:824-840</c>): steer toward the descent point, sink, and land.
        /// <c>isFly = false; aiState = 1</c> on touchdown.
        /// </summary>
        protected virtual void UpdateDescent()
        {
            MoveHorizontal(DescentDriftSpeed());

            if (_controller.IsGrounded)
            {
                Land();
                return;
            }

            JumpVertical(-_descentSpeed);
        }

        /// <summary>
        /// The horizontal half of the descent, in px per frame — <c>spd.x = celX - X</c> before
        /// <c>norma</c> rescales the pair (<c>:790-798</c>).
        /// </summary>
        /// <remarks>
        /// The remaining distance is recomputed from the unit's live position every tick, so the drift
        /// decays as it closes in — the oracle's proportional term, not a constant slide that would
        /// overshoot and oscillate around the target. The arithmetic itself lives in
        /// <see cref="AlicornVariantRules.DescentDriftSpeed"/> so it can be pinned offline; this method
        /// only supplies the two inputs and the world→pixel conversion.
        /// </remarks>
        protected float DescentDriftSpeed()
        {
            if (_controller == null)
            {
                return 0f;
            }

            float targetX = _descentStartX + _descentOffsetPixels / TileQueryConstants.UnitToPixel;
            float remainingPixels = (targetX - _controller.transform.position.x) * TileQueryConstants.UnitToPixel;

            return AlicornVariantRules.DescentDriftSpeed(remainingPixels, _descentSpeed);
        }

        protected virtual void Land()
        {
            _isFly = false;
            _descending = false;
            _familyState = 1;
            SetState(EnemyAIState.Patrol);
        }

        /// <summary>
        /// The <c>isFly</c> branch of <c>UnitAlicorn.control()</c> (<c>:780-842</c>): steer toward the
        /// current goal while holding station, otherwise hover with the <c>floatY</c> bob.
        ///
        /// <para>There is still no <c>norma</c>-steered 2-D velocity in <see cref="EnemyBrain"/> — the
        /// base offers <c>MoveHorizontal</c> plus this positive-up <c>JumpVertical</c> — so the altitude
        /// half is a proportional approach to the goal rather than the oracle's normalised steering.
        /// That is the honest reduced form, not a claim of parity.</para>
        /// </summary>
        protected virtual void UpdateFlightPhysics(int tickIndex)
        {
            if (!_blackboard.HasLastKnownTargetPosition)
            {
                // Ambient hover bob (`floatY`, :783-788).
                JumpVertical(Mathf.Sin(tickIndex * 0.1f) * 1.5f);
                return;
            }

            // Hold roughly 60 px above the target — the stand-off the magic bolt wants.
            float desiredYOffset = _blackboard.TargetDeltaY + 60f;
            if (desiredYOffset > 20f)
            {
                JumpVertical(2f);
            }
            else if (desiredYOffset < -20f)
            {
                JumpVertical(-1.5f);
            }
            else
            {
                JumpVertical(0f);   // hold station
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            // The pole guard: above 70 % health a `pole` alicorn stands at its lock and does nothing.
            // See TickPoleObjective for why the oracle's early `return` from control() is expressed
            // here instead of as a return from SimTick.
            if (PoleGuardActive)
            {
                StopMovement();
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            StopMovement();
        }

        protected override void TickPatrol(int tickIndex)
        {
            _familyState = 1;
            // The pole guard: above 70 % health a `pole` alicorn stands at its lock and does nothing.
            // See TickPoleObjective for why the oracle's early `return` from control() is expressed
            // here instead of as a return from SimTick.
            if (PoleGuardActive)
            {
                StopMovement();
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);

                // AS3 `UnitAlicorn.as:603-608` — the decision ladder's first arm, verbatim: while
                // airborne, and not already landing, the unit is told to fly to a point far below
                // (`setCel(null, X ± 100, Y + 1000)`) and enters state 4. The port used to gate this
                // behind a 25% roll that the oracle does not have, which is why an airborne alicorn
                // almost never came down even when the state-4 flag had not been clobbered.
                if (_isFly)
                {
                    _familyState = 4;
                    BeginDescent();
                    return;
                }

                SetState(EnemyAIState.Idle);
                return;
            }

            // `aiState == 1`'s shoulder turn (`UnitAlicorn.as:881-928`) — the SAME block the raider
            // runs at `UnitRaider.as:1118-1164`: `stay && shX1/shX2 > 0.25` → probe 80 px ahead →
            // `jump(0.5)` onto a crate or `turnX = ±1`, and the turn is applied by the patrol arm.
            //
            // <b>The port's override dropped it, and that is the reported "they really like stare into
            // walls".</b> This method walked the unit in its facing direction with no reaction to
            // geometry at all, so a landed alicorn — and, after the descent landed it, that was every
            // alicorn — pushed at the first wall it met for the rest of its life. The base class already
            // implements the block; the override simply has to call it, in the base's own order
            // (decide the turn, then move, so this tick's move follows the new facing).
            HandleLedgeAhead(tickIndex);

            float dir = (_controller != null && _controller.FacingDirection < 0) ? -1f : 1f;
            MoveHorizontal(dir * _patrolSpeed);
        }

        protected override void TickAlert(int tickIndex)
        {
            _familyState = 2;
            // The pole guard: above 70 % health a `pole` alicorn stands at its lock and does nothing.
            // See TickPoleObjective for why the oracle's early `return` from control() is expressed
            // here instead of as a return from SimTick.
            if (PoleGuardActive)
            {
                StopMovement();
                return;
            }

            // `if(aiState == 2 && t_alarm >= 0) { --t_alarm; if(t_alarm == 0) enemySpawn(…) }` (:579-586)
            // — before the movement, as the oracle has it, and gated on state 2 alone.
            TickReinforcementTimer();

            ConsiderTakeOff();
            base.TickAlert(tickIndex);
        }

        /// <summary>
        /// AS3 <c>t_alarm</c>'s countdown (<c>:579-586</c>) — <b>only while searching</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>State 2 and not "aware", and the difference is a design decision in the oracle.</b>
        /// <c>alarma()</c> promotes an alicorn straight to <c>aiState = 3</c> (the chase), so a pack that
        /// was startled into combat <b>never spends the countdown</b> — the reinforcement is what happens
        /// when an alicorn <i>lost</i> the player and is casting about for him. An implementation that
        /// counted down while chasing would call for help in the middle of a fight, which is a much
        /// noisier and easier encounter.</para>
        ///
        /// <para><c>t_alarm == 0</c> fires exactly once: the counter is allowed to run to -1 (the gate is
        /// <c>&gt;= 0</c>) and never comes back, so an alicorn calls for help once per life.</para>
        /// </remarks>
        protected virtual void TickReinforcementTimer()
        {
            if (!AlicornPackRules.ShouldCallReinforcements(_familyState, _tAlarm)) return;
            if (_tAlarm < 0) return;

            _tAlarm--;
            if (AlicornPackRules.ReinforcementDue(_tAlarm))
            {
                CallReinforcements();
            }
        }

        /// <summary>
        /// <c>loc.enemySpawn(false, false, "alicorn")</c> (<c>:584</c>) — another alicorn, at a random
        /// <c>enspawn</c> point, already alarmed.
        /// </summary>
        /// <remarks>
        /// <para><b>The family is hard-coded, so the reinforcement is a random tier</b> — <c>createUnit</c>
        /// runs <c>randomCid("alicorn")</c>, which is a fresh <c>floor(random()*3+1)</c>. A <c>tr3</c>
        /// alicorn can therefore summon a <c>tr1</c>.</para>
        ///
        /// <para><b>The budget is the room's <c>kolEnSpawn</c>, shared with the alarm's own
        /// reinforcements.</b> <c>enemySpawn</c> opens with <c>if(kolEnSpawn &lt;= 0) return;</c>
        /// (<c>:2979-2982</c>), so a room that has already called in its garrison produces nothing here
        /// either. <c>SecurityRuntime.SpawnFamilyReinforcement</c> owns that counter.</para>
        ///
        /// <para><b>Silent when it cannot happen.</b> A room with no <c>enspawn</c> marker, or a spent
        /// budget, is a legal outcome in the oracle — <c>enemySpawn</c> returns without a message — so
        /// this does not log per attempt. It logs once when the <i>port</i> is missing something (no
        /// room), which the oracle cannot be.</para>
        /// </remarks>
        protected virtual void CallReinforcements()
        {
            RoomInstance room = Room;
            if (room == null)
            {
                if (_reinforcementGapReported) return;
                _reinforcementGapReported = true;
                Debug.LogWarning(
                    "[Alicorn] t_alarm expired but the unit has no room, so no `enspawn` point could be " +
                    "chosen and the reinforcement was not called. AS3 cannot reach this state.");
                return;
            }

            SecurityRuntime.SpawnFamilyReinforcement(
                room,
                AlicornPackRules.ReinforcementFamilyId,
                (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel);
        }

        private bool _reinforcementGapReported;

        /// <summary>
        /// AS3 <c>UnitAlicorn.as:997-1001</c> — in states 2/3 the unit jumps when it is navigating
        /// <b>upward</b> (<c>aiVNapr &lt; 0</c> sets <c>_loc2_ = 1</c>, which the next block turns into
        /// <c>jump(_loc2_)</c>), and <c>jump()</c> is what sets <c>isFly</c>.
        ///
        /// <para>This is the replacement for the port's <c>_isFly = true</c> at the top of the two
        /// ticks: same observable — an engaged alicorn takes to the air — but it is now an event the
        /// oracle actually has, so the unit can also be found on the ground.</para>
        ///
        /// <para>Port Y is up, so a target <i>above</i> is <c>TargetDeltaY &gt; 0</c>; AS3's
        /// <c>celDY &gt; 80</c> test (<c>:976</c>) is the opposite sign because AS3 Y grows down.</para>
        /// </summary>
        protected virtual void ConsiderTakeOff()
        {
            if (_isFly || _descending) return;
            if (_controller == null || !_controller.IsAlive) return;
            if (!_blackboard.HasLastKnownTargetPosition) return;

            if (_blackboard.TargetDeltaY > 40f)
            {
                TakeOff();
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            _familyState = 3;
            // The pole guard: above 70 % health a `pole` alicorn stands at its lock and does nothing.
            // See TickPoleObjective for why the oracle's early `return` from control() is expressed
            // here instead of as a return from SimTick.
            if (PoleGuardActive)
            {
                StopMovement();
                return;
            }

            ConsiderTakeOff();

            // The hunt ends on the awareness budget, not on the sighting — `EnemySensors` nulls
            // `TargetUnit` on the first obscured tick, and exiting here made the alicorn forget the
            // player the instant line of sight broke. See `EnemyBrain.ChaseBudgetExhausted`.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                // Keep closing on the last place the target was seen; the altitude half is the
                // `_isFly` branch in `SimTick`, which already steers on `LastKnownTargetPosition`.
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            // There used to be an invented `if (dist < 80) TryTeleportAway()` here. AS3 has no
            // close-range teleport: `actPort` has exactly two callers, `superSila`'s `superSilaTip == 2`
            // arm (:1113/:1117) and the `tr2` damage reflex (:424). A `tr2` alicorn that hopped away
            // whenever the player closed in was a different enemy from the oracle's, and it made the
            // real teleport (a super, on a cooldown) look like a permanent kiting trick.

            // Stand-off distance around 200..350 px
            if (dist > 350f)
            {
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }
            else if (dist < 150f)
            {
                // Back away slightly
                float dir = dx >= 0 ? -1f : 1f;
                MoveHorizontal(dir * _patrolSpeed);
            }
            else
            {
                StopMovement();
            }

            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            return _blackboard.TargetUnit != null && _blackboard.TargetDistance <= _magicRangePixels && _blackboard.HasLineOfSight;
        }

        /// <summary>
        /// Hold the <c>alilight</c> trigger for the burst window and keep its aim on the target.
        /// </summary>
        /// <remarks>
        /// <para><b>Held, not pulsed.</b> AS3 calls <c>currentWeapon.attack()</c> once per frame for the
        /// whole burst (<c>UnitAlicorn.as:1018-1023</c>) and lets the weapon's own <c>t_attack</c>
        /// decide when a round leaves. Holding <c>BeginAttack</c> for the same window is the port's
        /// equivalent, and the weapon controller's cooldown does the pacing. Pulsing it for a single
        /// tick would make a <c>rapid='35'</c> weapon fire once per decision instead of once per
        /// cooldown, which is a slower and quieter alicorn than the oracle's.</para>
        ///
        /// <para><b>The aim is re-read every firing tick</b>, not latched at the decision, so the bolt
        /// follows a moving player — which is what the oracle does, since <c>attack()</c> resolves its
        /// direction from the unit's current target each frame.</para>
        ///
        /// <para><b>The aim is in Unity world units, and it has to be.</b> This is the whole of the
        /// reported "the alicorn fires at ~45° to the right instead of at the player". The value is
        /// consumed twice, both times in world units:</para>
        /// <list type="bullet">
        ///   <item><description><c>WeaponHoldPointMath.Inputs.AimX</c> — documented as "Cursor / aim
        ///     world X. AS3 <c>celX</c>", compared against <c>OwnerX ± scX/2</c>, which is also world
        ///     units. The player's own <c>PlayerWeaponLoadout.ResolveHoldPoint</c> passes
        ///     <c>_aimTarget.x</c> and documents it as "in Unity units".</description></item>
        ///   <item><description><c>RangedWeaponController.TickOneFlashFrame</c>:
        ///     <c>rot2 = Atan2(aimTarget.y - State.Y, aimTarget.x - State.X)</c>, where <c>State.X/Y</c>
        ///     are the weapon's world position (they lerp toward the hold point, itself world units).
        ///     Feeding pixels here makes the numerator and denominator both ≈ the target's <i>absolute</i>
        ///     pixel coordinates, so the angle collapses to the direction from the <b>world origin</b> to
        ///     the player — a fixed heading that barely moves as the alicorn or the player does, which is
        ///     exactly the observed symptom.</description></item>
        /// </list>
        /// <para>An earlier revision multiplied by <c>100f</c> ("world pixels — the same space the
        /// projectile is resolved in"). That reasoning is wrong: the projectile is <i>spawned</i> at a
        /// world-unit position, but the <i>angle</i> is computed from the aim point here, and the port's
        /// entire weapon stack — the hold point, the rotation, <c>PlayerWeaponLoadout</c> — speaks Unity
        /// units. The <c>TargetDeltaX/Y</c> family on <c>EnemyBlackboard</c> really is in pixels, which is
        /// what makes this easy to get wrong; those are for the <i>AI</i> range tests and never for the
        /// weapon.</para>
        /// </remarks>
        protected virtual void UpdateWeaponFire()
        {
            if (EnemyController == null) return;

            EnemyWeaponMount mount = EnemyController.GetWeaponMount("alilight");
            if (mount == null) return;

            bool firing = _attackBurstTicks > 0 && _controller.IsAlive;
            if (firing) _attackBurstTicks--;

            if (firing && _blackboard.TargetUnit != null)
            {
                // Unity world units — the space WeaponHoldPointMath and RangedWeaponController both
                // resolve in. Do NOT scale by 100 here; see the remarks above.
                //
                // The POINT is `setCel`'s, not the target's origin: `celY = Y - scY/2`
                // (`Unit.as:4708`) is the middle of her body, while `transform.position` is her feet.
                // Aiming at the feet sent every bolt into the floor tile she stands on, which is the
                // reported "the alicorns never hit me, the boss especially".
                mount.SetAimTarget(EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit));
            }

            mount.SetFiring(firing);

            // `psyWeapon.attack()` from the tr3 super. Driven through the same mount seam as the bolt,
            // but for a single tick: AS3 calls `attack()` once, and the weapon's own `t_attack` paces
            // the round — holding the trigger would turn one pull into a stream.
            EnemyWeaponMount psy = EnemyController.GetWeaponMount("alipsy");
            if (psy == null) return;

            bool psyFiring = _psyFireTicks > 0 && _controller.IsAlive;
            if (psyFiring) _psyFireTicks--;

            if (psyFiring && _blackboard.TargetUnit != null)
            {
                psy.SetAimTarget(EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit));
            }

            psy.SetFiring(psyFiring);
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;
            if (_spellCooldownTicks > 0) return;

            _spellCooldownTicks = UnityEngine.Random.Range(30, 60);

            // Magic bolt attack (alilight). AS3 `:1016` arms the burst; the weapon fires on the
            // following frames from `UpdateWeaponFire`, which is what makes the bolt leave the horn
            // rather than the decision point.
            // No noise here: the bolt is a weapon, and its noise comes from the weapon's own
            // `<snd noise>` via ProjectileSpawner.AnnounceShot. AS3 `Weapon.as:1581`. The old
            // `MakeNoise(150, true)` was invented — no brain in the oracle calls makeNoise at all.
            // See docs/EnemyImplementationGuides/AUDIT_noise_sources_2026-10-19.md.

            // The bolt carries the damage now. Applying it here as well would make every shot hit
            // twice — once on the projectile's impact and once instantly — so the direct report is
            // only the fallback for a unit whose mount could not be built (no WeaponDefinition for the
            // id, or a bare test spawn with no container), which keeps an unarmed alicorn from being
            // completely harmless.
            if (EnemyController != null && EnemyController.GetWeaponMount("alilight") != null)
            {
                _attackBurstTicks = AttackBurstTicks;
                return;
            }
            // `MeleeDamage`, not `Definition.damage`: a starred enemy's `dam` is multiplied per instance
            // (Unit.as:1650-1669) and the definition is shared by every unit of this id.
            float baseDamage = _controller.MeleeDamage;

            // World units, like every other `PendingDamage.Direct` caller in the tree — the impact point
            // drives the floating-number and blood-spray overlays, which are Unity-space. This used to
            // carry the same `* 100f` as the aim target; see UpdateWeaponFire for why pixels are wrong
            // anywhere on the weapon path.
            Vector2 targetPos = _blackboard.TargetUnit.transform.position;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, DamageType.Laser),
                _blackboard.TargetUnit,
                targetPos);

            if (_controller.DamageSystem != null)
            {
                _controller.DamageSystem.Report(pending);
            }
            else
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.actPort(param1)</c> (<c>:1248-1317</c>) — pick a point, test it for
        /// collision, teleport there, and re-acquire.
        /// </summary>
        /// <remarks>
        /// <para><b>The previous implementation was a stub</b>: <c>transform.position += offset</c> with
        /// a 1.5-2.5 unit offset "away from the player". That is not the oracle in any respect — no
        /// collision test (so it teleported into walls), no room clamp, no tile snap, and a distance
        /// two orders of magnitude short of the real one (the near rung alone is 200-500 <i>pixels</i>,
        /// i.e. 2-5 units).</para>
        ///
        /// <para><b>Two callers, two ladders.</b> <c>superSila</c> calls <c>actPort()</c> — the
        /// near/far/random ladder. The <c>tr2</c> reactive escape calls <c>actPort(true)</c>, which
        /// skips the near rungs and lands anywhere in the room. See
        /// <see cref="AlicornTeleportMath"/> for the ladder itself.</para>
        ///
        /// <para><b>Deviation, recorded.</b> AS3 measures both near rungs from <c>World.w.gg</c> — the
        /// <b>player</b> — which the port has no global handle on. This uses the current target, then
        /// the last known target position, and finally the caster's own position. For the two callers
        /// that actually exist that is indistinguishable in practice (the super fires only while
        /// engaged, and the reactive escape only fires when the player's own shot just landed), but it
        /// is a substitution, not a port.</para>
        /// </remarks>
        /// <param name="reactive">AS3 <c>param1</c> — the <c>tr2</c> damage reflex's <c>true</c>.</param>
        /// <returns><c>true</c> when a clear point was found and the unit moved.</returns>
        protected virtual bool TryTeleportAway(bool reactive = false)
        {
            RoomInstance room = Room;
            ITileQueryService query = TileQueryService;
            if (room == null || query == null || _controller == null) return false;

            Vector2 origin = query.OriginPixel;
            Vector2 selfWorldPx = (Vector2)transform.position * TileQueryConstants.UnitToPixel;
            Vector2 selfLocal = selfWorldPx - origin;

            // `var _loc2_:Unit = World.w.gg;` — see the remarks for the substitution.
            Vector2 playerWorldPx;
            int playerFacing;
            if (_blackboard.TargetUnit != null)
            {
                playerWorldPx = (Vector2)_blackboard.TargetUnit.transform.position * TileQueryConstants.UnitToPixel;
                playerFacing = _blackboard.TargetUnit.FacingDirection;
            }
            else if (_blackboard.HasLastKnownTargetPosition)
            {
                playerWorldPx = _blackboard.LastKnownTargetPosition;
                playerFacing = _blackboard.TargetDeltaX >= 0f ? 1 : -1;
            }
            else
            {
                playerWorldPx = selfWorldPx;
                playerFacing = _controller.FacingDirection;
            }

            Vector2 playerLocal = playerWorldPx - origin;

            float roomLimitX = room.width * WorldConstants.TILE_SIZE;
            float roomLimitY = room.height * WorldConstants.TILE_SIZE;
            Vector2 spriteSize = _controller.SpriteSizePixels;

            for (int attempt = 1; attempt <= AlicornTeleportMath.MaxAttempts; attempt++)
            {
                Vector2 candidate = AlicornTeleportMath.PickPoint(
                    reactive: reactive,
                    attempt: attempt,
                    playerLocal: playerLocal,
                    playerFacing: playerFacing,
                    spriteSize: spriteSize,
                    roomLimitX: roomLimitX,
                    roomLimitY: roomLimitY,
                    tileSize: WorldConstants.TILE_SIZE,
                    rollBranch: UnityEngine.Random.value,
                    rollMagnitude: UnityEngine.Random.value,
                    rollJitter: UnityEngine.Random.value);

                // `if(!collisionAll(_loc3_ - X, _loc4_ - Y))` (:1312) — the oracle tests the body box
                // at the destination. `CheckCollision` takes room-local pixels, the same space the
                // candidate is in.
                if (room.CheckCollision(candidate, spriteSize))
                {
                    continue;
                }

                // `teleport(_loc3_, _loc4_, 1)` (:1314) — the motor is the only thing that can move the
                // body without the next tick colliding against the old position, and its `TeleportTo`
                // already does the oracle's `dx = dy = 0` (:1315).
                Vector2 worldPx = candidate + origin;
                if (_motor != null)
                {
                    _motor.TeleportTo(worldPx.x, worldPx.y);
                }
                else
                {
                    transform.position = worldPx / TileQueryConstants.UnitToPixel;
                }

                StopMovement();

                // `if(findCel(true) && celUnit) { aiSpok = 0; storona = celX > X ? 1 : -1; }`
                // (:1316-1319). `aiSpok = 0` drops the awareness budget to nothing — the unit
                // re-acquires from scratch on the next sensor pass rather than keeping the sighting it
                // had at the old position.
                _blackboard.AlertTimerTicks = 0;

                if (_blackboard.TargetUnit != null)
                {
                    float targetX = _blackboard.TargetUnit.transform.position.x;
                    ApplyFacing(targetX > transform.position.x ? 1 : -1);
                }

                return true;
            }

            // Twenty failures means the room has no clear body-sized cell left. The oracle just falls
            // out of the loop and does not move — same here, and deliberately silent: a log line per
            // attempt on a crowded map would be noise, and "did not teleport" is a legal outcome.
            return false;
        }

        /// <summary>
        /// AS3 <c>UnitAlicorn.animate()</c> (<c>UnitAlicorn.as:269-327</c>), branch for branch.
        ///
        /// <para><b>The state set is exactly <c>{die, fall, death, fly, polz, sit, walk, stay}</c>.</b>
        /// An earlier version of this method returned <c>pre</c> while the spell cooldown was above 20,
        /// and <c>trot</c>/<c>jump</c> on the ground/airborne arms. The oracle sets none of those for an
        /// alicorn, and <c>alicorn1..3</c>'s imported sheet authors all three at <c>length: 0</c> — so
        /// <c>SetState</c> accepted the id and found no frames, and the unit drew nothing for up to 40
        /// ticks after every spell (<c>_spellCooldownTicks</c> is set to <c>Random.Range(30,60)</c> on a
        /// cast). The membership lint could not see it: <c>pre</c>/<c>trot</c>/<c>jump</c> are all valid
        /// <see cref="AnimationSet.As3Ids"/>. Do not add a state here that the oracle does not set.</para>
        ///
        /// <para><c>stay</c> in the oracle is groundedness (see <c>Unit.as:1962</c>), and <c>dx</c> is the
        /// per-frame horizontal velocity in pixels — hence the <c>±1</c> test, not a units-per-second
        /// one.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // `sost == 2 || sost == 3` — the two death states.
            if (!_controller.IsAlive)
            {
                if (_controller.IsGrounded)
                {
                    return _animator != null && string.Equals(_animator.StateName, "death", StringComparison.Ordinal)
                        ? "fall"
                        : "die";
                }
                return "death";
            }

            if (_isFly) return "fly";

            bool grounded = _controller.IsGrounded;
            float dx = _motor != null ? _motor.State.Velocity.x : 0f;
            bool moving = dx > 1f || dx < -1f;

            if (_isSit)
            {
                return grounded && moving ? "polz" : "sit";
            }

            if (grounded && moving) return "walk";

            return "stay";
        }
    }
}
