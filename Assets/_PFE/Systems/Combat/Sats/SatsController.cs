using System.Collections.Generic;
using UnityEngine;
using PFE.Data;
using PFE.Entities.Units;
using PFE.Sim.Combat;
using PFE.Sim.Combat.Sats;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;

namespace PFE.Systems.Combat.Sats
{
    /// <summary>
    /// One queued shot — the port of <c>fe.inter.SatsCel</c>, minus the MovieClip.
    ///
    /// <para><b>What the Oracle stores here is a <i>plan</i>, not a shot.</b> <see cref="Kol"/> is how
    /// many rounds of the weapon this one cell covers (<c>satsQue</c>), and <see cref="Cons"/> is the
    /// per-round debit (<c>price / satsQue</c>) — so one cell on a three-shot weapon is three trigger
    /// pulls paid for up front, released one at a time by <c>Sats.act()</c>.</para>
    /// </summary>
    public sealed class SatsCell
    {
        /// <summary>The unit this cell is locked to, or <c>null</c> for a free-aim cell.</summary>
        public UnitController Target;

        /// <summary>The aim point in world pixels. For a unit cell this is re-derived every tick from
        /// the target's body centre (<c>SatsCel.run()</c>, <c>SatsCel.as:47-55</c>), so a target that
        /// moves between ticks is still hit where it now is.</summary>
        public Vector2 AimPoint;

        /// <summary>Rounds this cell still owes — <c>SatsCel.kol</c>.</summary>
        public int Kol;

        /// <summary>AP debited per round — <c>SatsCel.cons</c>.</summary>
        public float Cons;

        /// <summary>At least one round of this cell has fired — <c>SatsCel.begined</c>. Load-bearing:
        /// it is what keeps the rest of a burst going at a target that died mid-burst.</summary>
        public bool Begined;

        public bool IsUnitCell => Target != null;

        /// <summary>
        /// <c>SatsCel.run()</c> — re-anchor a unit cell to its target's body centre
        /// (<c>Y - scY/2</c>). A free-aim cell keeps the point it was queued at.
        /// </summary>
        public void Reanchor()
        {
            if (Target == null) return;
            Vector3 p = Target.WorldPosition;
            float scY = Target.SpriteSizePixels.y;
            AimPoint = new Vector2(p.x * 100f, p.y * 100f - scY * 0.5f);
        }
    }

    /// <summary>
    /// The message layer S.A.T.S. needs: <c>gui.infoText(key)</c> and <c>gui.setTopText(key)</c>.
    ///
    /// <para><b>Why an interface and not a direct call.</b> The port has no <c>infoText</c> — every
    /// other refusal in the codebase records that as a no-op (<c>LiveSpellWorld.ShowInfoText</c>,
    /// <c>HitAvoidance</c>'s notes). S.A.T.S. refuses entry in five different ways with five different
    /// strings, and "the mode did nothing when I pressed V" is indistinguishable from a broken key
    /// without them. So the seam is named, the bootstrapper supplies a real implementation, and a
    /// headless fixture can pass a recorder and assert <i>which</i> message was chosen.</para>
    /// </summary>
    public interface ISatsFeedback
    {
        /// <summary><c>gui.infoText(key)</c> — the transient centre-screen line.</summary>
        void InfoText(string key);

        /// <summary><c>gui.setTopText(key)</c> — the hint strip. <c>""</c> clears it.</summary>
        void SetTopText(string key);
    }

    /// <summary>
    /// The two world reads S.A.T.S. needs, kept behind a seam because <b>the coordinate frame is the
    /// implementation's problem, not the controller's</b>.
    ///
    /// <para>Inside <see cref="SatsController"/> everything is "world pixels" — Unity world space times
    /// <c>TileQueryConstants.UnitToPixel</c>. The tile system and the fog mask do not necessarily work
    /// in that frame (the room keeps its own origin, and <c>UnitNPCController.As3PixelsToRoomLocal</c>
    /// exists precisely because AS3 pixels are room-local). Converting in one place, at the edge, is
    /// how this port has broken itself before; the seam is the edge.</para>
    /// </summary>
    public interface ISatsWorldQuery
    {
        /// <summary><c>Unit.getTileVisi()</c> — is the tile under this point lit? The Oracle admits a
        /// target only when <c>visi &gt; 0.3</c> (<c>Unit.as:4527-4530</c>).</summary>
        bool TryGetFogVisibility(Vector3 worldPosition, out float visibility);

        /// <summary>The tile ray <c>Unit.look()</c> walks from the eye to the body
        /// (<c>Unit.as:4619-4636</c>). <c>true</c> when nothing solid is between the two points.</summary>
        bool HasLineOfSight(Vector2 fromWorldPx, Vector2 toWorldPx);
    }

    /// <summary>
    /// S.A.T.S. — the port of <c>fe.inter.Sats</c>: the entry gate, the action-point pools, the shot
    /// queue and the per-tick execution of that queue.
    ///
    /// <para><b>The one structural fact that decides everything else.</b> S.A.T.S. is a <i>hard
    /// pause</i>, not slow motion: <c>World.as:1385</c> sets <c>allStat = 2</c> while it is open and
    /// <c>World.as:1257</c> gates the whole room on <c>allStat == 1</c>. The port already has that
    /// mechanism (<see cref="PFE.Core.InterfacePauseGate"/>), so this class holds the gate while
    /// active and registers as a <see cref="PFE.Core.SimTickOrder.ViewAligned"/> tickable — the same
    /// bucket the PipBuck uses, because <c>World.as:1315-1320</c> steps the interfaces under the
    /// looser <c>allStat &gt;= 1</c>.</para>
    ///
    /// <para><b>The second fact, and the one a naive port gets wrong.</b> <c>sats.step2()</c> — the
    /// action-point regeneration, the "can I still afford it" check and the manual-fire cancel — sits
    /// <i>inside</i> the frozen block (<c>World.as:1280</c>), while <c>sats.step()</c> sits outside it
    /// (<c>:1320</c>). So while the mode is open: AP never regenerates, the affordability check never
    /// fires, and LMB does not clear the queue. All three happen only <i>after</i> you accept. This
    /// class reproduces the split with a single tickable that skips its second half when the gate is
    /// held.</para>
    /// </summary>
    public sealed class SatsController : PFE.Core.ISimTickable
    {
        private readonly PFE.Core.InterfacePauseGate _pause;
        private readonly List<SatsCell> _queue = new List<SatsCell>(16);

        // ── Latched input ──────────────────────────────────────────────────────────────────
        //
        // Latches, not polls. The bootstrapper's Update runs at display rate (60–144 Hz) and this
        // tick at 30 Hz, so a press sampled inside SimTick would be dropped on three frames out of
        // four. Each latch is set by the frame-level poll and consumed by the tick — the same shape
        // PipBuckBootstrapper uses for its hotkeys.
        private bool _latchToggle;
        private bool _latchQueueShot;
        private bool _latchRemoveLast;
        private bool _latchAccept;

        /// <summary>An attack press seen since the last tick. <c>step2</c> reads it to cancel a plan
        /// when the player fires manually (<c>Sats.as:205-208</c>).</summary>
        private bool _latchAttackEdge;

        // ── State ─────────────────────────────────────────────────────────────────────────

        private bool _active;
        private float _od = SatsRules.InitialOd;
        private float _odv = SatsRules.InitialOd;
        private float _skillConf = 1f;
        private int _apBarTicksLeft;
        private Vector2 _pointerWorldPx;

        public SatsController(PFE.Core.InterfacePauseGate pause)
        {
            _pause = pause;
        }

        // ── Scene bindings, pushed by SatsBootstrapper ────────────────────────────────────
        //
        // Pushed rather than injected: the player, its stats and the room all appear and disappear
        // across room streaming, and a container-scoped controller that captured them at build time
        // would hold a destroyed unit. The bootstrapper owns the lookup and re-pushes; null is a
        // legal state at every moment (before the player spawns, mid-transition).

        public UnitController Player { get; set; }
        public PlayerWeaponLoadout Loadout { get; set; }
        public CharacterStats Stats { get; set; }
        public ISatsWorldQuery World { get; set; }
        public ISatsFeedback Feedback { get; set; }
        public SatsTargeting Targeting { get; } = new SatsTargeting();

        /// <summary>
        /// <c>World.w.loc.base || World.w.alicorn</c> (<c>Sats.as:84-88</c>) — the two places S.A.T.S.
        /// silently refuses to open.
        ///
        /// <para><b>A seam with no implementation yet, and that is recorded rather than hidden.</b>
        /// The port has neither a <c>loc.base</c> flag on <see cref="PFE.Systems.Map.RoomInstance"/>
        /// nor a world-level alicorn-fight flag; <c>10_SATS_TACTICAL_MODE.md</c> §8 lists the lockout
        /// as absent. Until both land this returns <c>false</c>, which is the honest reading of
        /// "the exclusion does not exist in the port" — <i>not</i> "the exclusion is unnecessary".
        /// </summary>
        public System.Func<bool> IsRoomExcluded { get; set; }

        // ── Public state ──────────────────────────────────────────────────────────────────

        public bool IsActive => _active;
        public IReadOnlyList<SatsCell> Queue => _queue;
        public bool HasQueuedShots => _queue.Count > 0;

        /// <summary>The real pool — what the player can actually spend.</summary>
        public float Od => _od;

        /// <summary>The projected pool — <see cref="Od"/> minus what the queue has reserved.</summary>
        public float Odv => _odv;

        /// <summary>1 / 0.8 / 0.6, fixed at entry (<c>Sats.as:136-148</c>).</summary>
        public float SkillConfidence => _skillConf;

        /// <summary>
        /// The last §3.11 trigger to fire, or <c>null</c> before any has. Recorded whether or not a
        /// queue was standing — see <see cref="Invalidate"/>.
        /// </summary>
        public SatsInvalidationReason? LastInvalidation { get; private set; }

        /// <summary>
        /// How many times a trigger actually dropped a queue. Paired with
        /// <see cref="LastInvalidation"/> this distinguishes "the call site is wired" from "the call
        /// site is wired and reached a plan".
        /// </summary>
        public int InvalidationCount { get; private set; }

        /// <summary>
        /// The front cell's aim point in <b>Unity world units</b>, or <c>null</c> when the queue is
        /// empty. This is the override <c>PlayerController.HandleAiming</c> applies ahead of the
        /// cursor and the forced-aim scatter — the port of <c>celX = que[0].X</c>
        /// (<c>UnitPlayer.as:1162-1163</c>).
        /// </summary>
        public Vector2? QueueAimWorldUnits
        {
            get
            {
                if (_queue.Count == 0) return null;
                Vector2 px = _queue[0].AimPoint;
                return new Vector2(px.x / 100f, px.y / 100f);
            }
        }

        /// <summary>
        /// <c>GUI.step</c>'s <c>t_od</c> countdown (<c>GUI.as:2009-2015</c>): the AP bar shows for 200
        /// ticks after the last <c>setOd()</c>. Reported rather than owned so the view stays dumb.
        /// </summary>
        public bool ApBarVisible => _apBarTicksLeft > 0;

        /// <summary><c>GUI.setOd</c>'s first guard — no bar at all for a weapon S.A.T.S. cannot use.</summary>
        public bool ApBarApplicable
        {
            get
            {
                if (!TryReadWeapon(out SatsWeapon w)) return false;
                return !w.NoSats;
            }
        }

        /// <summary>
        /// Whether the labels carry a percentage — <c>!weapon.noPerc</c> (<c>Sats.as:492</c>). Also the
        /// flag that decides whether a unit can be locked at all, because the Oracle's hit test is the
        /// hover glow that a <c>noperc</c> weapon never applies.
        /// </summary>
        public bool ShowsPercentages
        {
            get
            {
                if (!TryReadWeapon(out SatsWeapon w)) return false;
                return !w.NoPerc;
            }
        }

        public int TickOrder => PFE.Core.SimTickOrder.ViewAligned;

        // ── Frame-level input, called by the bootstrapper from Update ─────────────────────

        public void LatchToggle() => _latchToggle = true;
        public void LatchQueueShot() => _latchQueueShot = true;
        public void LatchRemoveLast() => _latchRemoveLast = true;
        public void LatchAccept() => _latchAccept = true;

        /// <summary>An attack press this frame, for <c>step2</c>'s manual-fire cancel.</summary>
        public void LatchAttackEdge() => _latchAttackEdge = true;

        /// <summary>The pointer in world pixels — the free-aim target and the hover hit test.</summary>
        public void SetPointerWorldPixels(Vector2 pointerWorldPx) => _pointerWorldPx = pointerWorldPx;

        public Vector2 PointerWorldPixels => _pointerWorldPx;

        // ── The tick ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One <see cref="PFE.Core.SimTickOrder.ViewAligned"/> tick — <c>sats.step()</c> and, when the
        /// world is <b>not</b> frozen, <c>sats.step2()</c>.
        ///
        /// <para><b>Why both halves live in one tickable.</b> The Oracle splits them across two
        /// different gates (<c>World.as:1280</c> inside <c>allStat == 1</c>, <c>:1320</c> outside it),
        /// and the port cannot express that with the bucket split alone: <c>ViewAligned</c> always
        /// runs, so <c>step2</c> would run while the mode is open and AP would regenerate under a
        /// paused world. Reading the gate here is the literal transcription of <c>allStat</c>, and it
        /// also covers the case the Oracle covers by the same line — a PipBuck-open world, where
        /// S.A.T.S. is not active but its regen must still wait.</para>
        /// </summary>
        public void SimTick(int tickIndex)
        {
            // GUI.step runs under `allStat >= 1`, i.e. always — the bar's own countdown is not
            // paused by the interface that is showing it.
            if (_apBarTicksLeft > 0) _apBarTicksLeft--;

            if (_latchToggle)
            {
                _latchToggle = false;
                Toggle();
            }

            if (_active) StepInput();

            if (_pause == null || !_pause.IsWorldPaused)
            {
                Step2();
                TickRegen();
            }
            else
            {
                _latchAttackEdge = false;
            }
        }

        /// <summary>
        /// <c>Sats.step()</c> — the mode's own input, in the Oracle's order: attack, teleport,
        /// action (<c>Sats.as:181-201</c>). Each is consumed so no other system sees it that frame.
        /// </summary>
        private void StepInput()
        {
            if (_latchQueueShot)
            {
                _latchQueueShot = false;
                SetCel();
            }

            if (_latchRemoveLast)
            {
                _latchRemoveLast = false;
                UnsetCel(fromFront: false);
            }

            if (_latchAccept)
            {
                _latchAccept = false;
                Exit();
            }

            // A press latched while the mode is open is the mode's, not the weapon's. Drop it so it
            // cannot cancel the plan on the first tick after the mode closes.
            _latchAttackEdge = false;
        }

        /// <summary>
        /// <c>Sats.step2()</c> — the three post-accept checks (<c>Sats.as:203-220</c>), in order.
        /// </summary>
        private void Step2()
        {
            bool attackEdge = _latchAttackEdge;
            _latchAttackEdge = false;

            if (_queue.Count == 0) return;

            // 1. Manual fire cancels the plan. AS3 reads `ctr.keyAttack` as a level here, but the
            //    player's own attack handler has already consumed it by the time step2 runs (the
            //    room block runs the units first), so an edge seen since the last tick is the same
            //    observation.
            if (attackEdge)
            {
                ClearAll();
                return;
            }

            // 2. Can the pool still pay for the next shot? Note the doubled consMult — see
            //    SatsRules.CanAffordNextShot.
            if (!TryReadWeapon(out SatsWeapon w))
            {
                Invalidate(SatsInvalidationReason.WeaponInvalid);
                return;
            }

            float perShot = SatsRules.PerShotCost(
                w.SatsCons, w.ConsMult, _skillConf, w.SatsMult, w.SatsQue);

            if (!SatsRules.CanAffordNextShot(perShot, w.ConsMult, _od))
            {
                Feedback?.InfoText("noOd");
                Invalidate(SatsInvalidationReason.CannotAfford);
            }
        }

        /// <summary>
        /// The regeneration half of <c>step2</c>, split out because it runs on a <i>different</i>
        /// condition from the other two checks: it needs an <b>empty</b> queue, which is the opposite
        /// of the cancel checks. Called from the same tick, after them.
        /// </summary>
        private void TickRegen()
        {
            if (_queue.Count > 0) return;
            if (Stats == null) return;

            float before = _od;
            _od = SatsRules.RegenStep(_od, Stats.maxOd, SatsRules.OdRegenPerTick, queueEmpty: true);
            if (_od != before)
            {
                _odv = _od;
                NoteApBar();
            }
        }

        // ── Enter / exit (Sats.as:66-179) ─────────────────────────────────────────────────

        /// <summary><c>V</c> or middle mouse — the toggle (<c>Ctr.as:34</c>, <c>World.as:1377</c>).</summary>
        public bool Toggle()
        {
            if (_active) { Exit(); return false; }
            return Enter();
        }

        /// <summary>
        /// <c>Sats.onoff(1)</c> — try to open. Returns <c>true</c> when the mode opened.
        ///
        /// <para><b>The gate table, in the Oracle's order</b> (<c>Sats.as:84-155</c>): the room
        /// exclusion is <i>silent</i>; a missing or unsupported weapon, an empty reserve, a broken
        /// weapon and a mana shortfall each get their own message; and the skill gap is checked
        /// <b>last</b>, after the reticle has already been chosen — which is why a gap above 2 still
        /// leaves <c>skillConf</c> at 0 on the way out.</para>
        /// </summary>
        public bool Enter()
        {
            if (_active) return true;

            // (1) The silent refusals. `if (World.w.loc.base || World.w.alicorn) { active = false; return; }`
            if (IsRoomExcluded != null && IsRoomExcluded())
            {
                return false;
            }

            // (2) No weapon at all, or one that does not support the mode.
            if (!TryReadWeapon(out SatsWeapon w))
            {
                Feedback?.InfoText("noSats");
                return false;
            }

            if (w.NoSats)
            {
                Feedback?.InfoText("noSats");
                return false;
            }

            // (3) Weapon status: 4 no reserve ammo, 5 broken, 6 not enough mana. Each its own message,
            //     and each a separate `if` in the Oracle — they cannot both be true, so the order is
            //     immaterial and the set is kept explicit.
            if (SatsWeaponStatus.RefusesEntry(w.Status))
            {
                Feedback?.InfoText(
                    w.Status == SatsWeaponStatus.NoAmmo ? "noAmmo"
                    : w.Status == SatsWeaponStatus.Broken ? "brokenWeapon"
                    : "noMana");
                return false;
            }

            // (4) The weapon-skill gap. `weaponsLevelsOff` defaults to TRUE and has no writer anywhere
            //     in fe/, so this gate is always live — it is not a setting.
            if (!HitAvoidance.CanFire(w.WeaponLevel, w.SkillTier))
            {
                _skillConf = 0f;
                Feedback?.InfoText("weaponSkillLevel");
                return false;
            }

            _skillConf = HitAvoidance.SkillConfidence(w.WeaponLevel, w.SkillTier);

            // (5) Entry. A re-entry never inherits a stale queue (`Sats.as:156-159`).
            _active = true;
            _pause?.Acquire(this);

            if (_queue.Count > 0) Invalidate(SatsInvalidationReason.Reentry);

            Targeting.Refresh(Player, w.IsMelee, Stats != null ? Stats.meleeR : 100f, World);
            Targeting.FillHitChances(Player, w, _skillConf, showPercent: !w.NoPerc);
            _odv = _od;
            NoteApBar();
            Feedback?.SetTopText("infosats");

            return true;
        }

        /// <summary>
        /// <c>Sats.onoff(-1)</c> — close, and <b>accept</b>.
        ///
        /// <para><b>The queue survives.</b> That is the whole point: the Oracle's own hint text calls
        /// the exit key "accept" (<c>text_en.xml:4750-4751</c>), and the queued shots then fire in
        /// real time through the player's normal fire cycle. Cancelling a plan is <c>Q</c> (remove
        /// the last cell) or a manual shot — not the exit.</para>
        /// </summary>
        public void Exit()
        {
            if (!_active) return;

            _active = false;
            _pause?.Release(this);
            Targeting.Clear();
            Feedback?.SetTopText("");

            // AS3 also calls `ctr.clearAll()` here, which drops held keys so the key that closed the
            // mode does not also act on the world in the same frame. The port's input is edge-based
            // per action, and the mode consumes its own edges in StepInput, so there is nothing to
            // clear — the equivalent is that no latch survives the close.
            _latchQueueShot = false;
            _latchRemoveLast = false;
            _latchAccept = false;
        }

        // ── Queueing (Sats.as:237-295) ────────────────────────────────────────────────────

        /// <summary>
        /// <c>Sats.setCel()</c> — LMB: queue a shot on the hovered unit, or at the cursor.
        ///
        /// <para><b>Refuses rather than clamps</b> when the full cell price exceeds the projected
        /// pool (<c>Sats.as:239-243</c>).</para>
        ///
        /// <para><b>The unit lock goes through the hover glow, and a <c>noperc</c> weapon never
        /// glows.</b> That is not a bug in the Oracle: <c>setCel</c> decides which unit was clicked
        /// by looking for the red glow the hover handler applies (<c>Sats.as:246-256</c>), and
        /// <c>mOver</c> applies it only when <c>active &amp;&amp; !weapon.noPerc</c> (<c>:396-400</c>).
        /// So the seven <c>noperc</c> weapons (<c>bfg</c>, <c>glau</c>, <c>glau1</c>, <c>aglau</c>,
        /// <c>mlau</c>, <c>bel</c>, <c>eclipse</c>) can only place free-aim cells. Reproduced, not
        /// "fixed".</para>
        /// </summary>
        public void SetCel()
        {
            if (!_active) return;
            if (!TryReadWeapon(out SatsWeapon w)) return;

            float fullPrice = SatsRules.FullCellCost(w.SatsCons, w.ConsMult, _skillConf, w.SatsMult);

            if (!SatsRules.CanQueue(fullPrice, _odv))
            {
                Feedback?.InfoText("noOd");
                return;
            }

            UnitController target = w.NoPerc ? null : Targeting.UnitUnderPointer(_pointerWorldPx);

            var cell = new SatsCell
            {
                Target = target,
                Cons = fullPrice / (w.SatsQue > 0 ? w.SatsQue : 1),
                Kol = w.SatsQue > 0 ? w.SatsQue : 1,
                Begined = false,
            };

            if (target != null)
            {
                cell.Reanchor();
                Targeting.NoteCellAdded(target);
            }
            else
            {
                cell.AimPoint = _pointerWorldPx;
            }

            // `weapon.ready = false` (Sats.as:261). The weapon's own rotation step is frozen while
            // the mode is open, so the first queued shot waits for the slew — the mode's only
            // wind-up, and the reason a shot does not leave the barrel the instant you accept.
            if (Loadout != null && Loadout.Current != null) Loadout.Current.State.Ready = false;

            _odv -= fullPrice;
            NoteApBar();
            _queue.Add(cell);
        }

        /// <summary>
        /// <c>Sats.unsetCel(fromFront)</c> — refund one cell.
        ///
        /// <para><b>Two directions, two callers.</b> <c>Q</c> pops the <i>last</i> cell
        /// (<c>Sats.as:190-194</c>); the execution loop shifts the <i>front</i> when its target died
        /// before the burst began (<c>:290</c>). Emptying the queue exits the mode.</para>
        /// </summary>
        public void UnsetCel(bool fromFront)
        {
            if (_queue.Count == 0)
            {
                Exit();
                return;
            }

            SatsCell cell = fromFront ? _queue[0] : _queue[_queue.Count - 1];
            _queue.Remove(cell);

            if (cell.Target != null) Targeting.NoteCellRemoved(cell.Target);

            if (TryReadWeapon(out SatsWeapon w))
            {
                _odv += SatsRules.FullCellCost(w.SatsCons, w.ConsMult, _skillConf, w.SatsMult);
            }

            if (_queue.Count == 0)
            {
                Exit();
                _odv = _od;
            }

            NoteApBar();
        }

        /// <summary><c>Sats.clearAll()</c> — drop the whole plan and re-sync the projected pool.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Target != null) Targeting.NoteCellRemoved(_queue[i].Target);
            }

            _queue.Clear();
            _odv = _od;
            NoteApBar();
        }

        /// <summary>
        /// One of the seventeen ways the Oracle drops a plan (<c>10_SATS_TACTICAL_MODE.md</c> §3.11).
        ///
        /// <para><b>Why one entry point instead of seventeen call sites each calling
        /// <see cref="ClearAll"/>.</b> The list is the requirement: twelve of the seventeen are in
        /// <c>UnitPlayer.as</c> at sites that have nothing to do with S.A.T.S. (grenade, spell
        /// hotkey, weapon change, jump, EMP), and a call site that is forgotten produces a plan that
        /// fires after an action the player took to cancel it. Naming the reason at the call site
        /// keeps the ledger greppable — <c>grep SatsInvalidationReason</c> finds every trigger and
        /// shows which of the seventeen it is.</para>
        /// </summary>
        public void Invalidate(SatsInvalidationReason reason)
        {
            // The reason is recorded even when there is nothing to drop. "A trigger fired and found
            // an empty queue" is a different observation from "the trigger never fired", and only the
            // second one is a wiring bug — collapsing the two would make the ledger useless as
            // evidence.
            LastInvalidation = reason;

            if (_queue.Count == 0) return;
            InvalidationCount++;
            ClearAll();
        }

        // ── Execution (UnitPlayer.as:1133-1166) ──────────────────────────────────────────

        /// <summary>
        /// The queue's turn, called by <c>PlayerWeaponLoadout</c> at the <b>top</b> of its
        /// <c>FixedUpdate</c> — before the weapon's own tick.
        ///
        /// <para><b>Why before, and why the weapon ticks it.</b> In the Oracle this block is the
        /// head of <c>UnitPlayer.control()</c>, which runs before <c>weapon.actions()</c> in the same
        /// unit step: <c>control()</c> reads the <c>is_shoot</c> the <i>previous</i> tick's shot set,
        /// then arms the next one. The port's weapon tick lives in <c>PlayerWeaponLoadout.FixedUpdate</c>,
        /// so the same call site reproduces the same one-tick relationship — and it is naturally
        /// skipped while the world is frozen, because that method returns early on the pause gate.</para>
        ///
        /// <para>Returns <c>true</c> when the weapon should fire this tick, with
        /// <paramref name="aimWorldPx"/> set to the front cell's aim point.</para>
        /// </summary>
        /// <param name="shotsNow">The weapon's monotonic <c>ShotsFired</c> counter sampled at the
        ///   head of this tick.</param>
        /// <param name="shotsBaseline">The same counter sampled at the previous check. The difference
        ///   is how many rounds left the barrel in between — <c>currentWeapon.is_shoot</c> expressed
        ///   as a count rather than a one-tick bool.</param>
        public bool PreWeaponTick(int shotsNow, int shotsBaseline, out Vector2 aimWorldPx)
        {
            aimWorldPx = default;
            if (_queue.Count == 0) return false;

            // 1. `if (currentWeapon.is_shoot) { sats.act(); }` — ONE act() per round that left the
            //    barrel since the last check. A minigun cell (`<sats que='15'/>`) owes 15 charges;
            //    charging one per tick instead leaves kol at 15 forever, so the queue never empties
            //    and the held trigger keeps firing until the 500-round magazine is dry. That is the
            //    "accept one cell, empty the whole gun" defect.
            //
            //    A counter, not `State.IsShoot`: every controller clears that flag at the END of its
            //    own Tick, so it is always false at the head of the next FixedUpdate. See
            //    WeaponRuntimeState.ShotsFired.
            int roundsOwed = 0;
            for (int i = 0; i < _queue.Count; i++) roundsOwed += Mathf.Max(1, _queue[i].Kol);

            int charges = SatsRules.ChargesForShots(shotsNow, shotsBaseline, roundsOwed);
            for (int i = 0; i < charges && _queue.Count > 0; i++) Act();

            if (_queue.Count == 0) return false;

            // 2. A weapon that became unusable drops the plan. `status() > 3` is 4/5/6 — the three
            //    refusals plus "no mana". A jam (2) deliberately does NOT clear: the queue waits.
            if (!TryReadWeapon(out SatsWeapon w) || w.NoSats || w.Status > SatsWeaponStatus.Reloading)
            {
                Invalidate(SatsInvalidationReason.WeaponInvalid);
                return false;
            }

            // 3. Is the front cell still fireable?
            SatsCell front = _queue[0];
            front.Reanchor();

            bool hasTarget = front.Target != null;
            bool alive = hasTarget && front.Target.IsAlive;

            if (!SatsRules.ShouldFireFront(hasTarget, front.Begined, alive))
            {
                UnsetCel(fromFront: true);
                return false;
            }

            aimWorldPx = front.AimPoint;
            return true;
        }

        /// <summary>
        /// <c>Sats.act()</c> — charge the shot that just left, and advance or drop the front cell
        /// (<c>Sats.as:306-321</c>).
        ///
        /// <para><b><c>od</c> can go negative here</b> and the Oracle allows it: the affordability
        /// check in <c>step2</c> multiplies <c>consMult</c> twice and is therefore laxer than the
        /// charge. Clamping at zero would hide the quirk and change the pool a later session starts
        /// from.</para>
        /// </summary>
        private void Act()
        {
            if (_queue.Count == 0) return;

            SatsCell front = _queue[0];
            _od -= front.Cons;
            NoteApBar();

            int status = TryReadWeapon(out SatsWeapon w) ? w.Status : SatsWeaponStatus.Ready;

            if (SatsRules.ShotPopsFrontCell(front.Kol, status))
            {
                _queue.RemoveAt(0);
                if (front.Target != null) Targeting.NoteCellRemoved(front.Target);
            }
            else
            {
                front.Kol--;
                front.Begined = true;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        private void NoteApBar()
        {
            _apBarTicksLeft = SatsRules.OdBarVisibleTicks;
        }

        /// <summary>
        /// The live weapon, or <c>false</c> when there is none. A fresh snapshot every call: the
        /// Oracle caches <c>sats.weapon</c> at entry, but every value that matters (status, readiness,
        /// the stat multipliers) is read live there too, and a cache in the port would go stale the
        /// moment the player swaps weapons mid-plan — which is one of the seventeen cancel triggers
        /// precisely because it can happen.
        /// </summary>
        private bool TryReadWeapon(out SatsWeapon weapon)
        {
            weapon = default;
            if (Loadout == null) return false;

            IWeaponController current = Loadout.Current;
            if (current == null) return false;

            WeaponRuntimeState state = current.State;
            if (state == null || state.Def == null) return false;

            weapon = SatsWeapon.Read(state, Stats, Loadout);
            return true;
        }
    }

    /// <summary>
    /// The Oracle sites that drop a queued plan with <c>clearAll()</c>
    /// (<c>10_SATS_TACTICAL_MODE.md</c> §3.11). Naming them makes the ledger checkable: a missing
    /// member is a trigger somebody forgot.
    ///
    /// <para><b>Fifteen, not seventeen, and the difference is a correction to the research.</b> §3.11
    /// claims "all are <c>sats.clearAll()</c>", but its rows 12 (front target died) and 17 (<c>Q</c>
    /// down to zero) are <c>unsetCel()</c> — a <i>single</i>-cell removal that, when it empties the
    /// queue, calls <c>onoff(-1)</c>, i.e. it <b>closes the mode</b> rather than cancelling a plan
    /// (<c>Sats.as:267-292</c>). Modelling them as invalidation reasons would make the port clear the
    /// whole queue where the Oracle removes one cell and exits. They are handled by
    /// <see cref="SatsController.UnsetCel"/> and deliberately absent here. §3.11's row 7 ("a weapon
    /// work action in progress") is the <i>same</i> site as its row 15 — both are the
    /// <c>else if(currentWeapon &amp;&amp; t_work &lt;= 0)</c> arm at <c>:2334-2340</c>, whose
    /// <c>t_work &lt;= 0</c> is the gate rather than a second trigger — so it is
    /// <see cref="ManualFire"/> and has no member of its own.</para>
    /// </summary>
    public enum SatsInvalidationReason
    {
        /// <summary><c>E</c> — interact (<c>UnitPlayer.as:2119</c>).</summary>
        Interact,
        /// <summary><c>Q</c> — teleport (<c>:2145</c>).</summary>
        Teleport,
        /// <summary><c>G</c> — grenade (<c>:2188</c>).</summary>
        Grenade,
        /// <summary><c>T</c> — magic weapon (<c>:2208</c>).</summary>
        MagicWeapon,
        /// <summary><c>C</c> — defend spell (<c>:2228</c>).</summary>
        DefendSpell,
        /// <summary>Any favourite-spell hotkey (<c>:2259</c>).</summary>
        SpellHotkey,
        /// <summary>
        /// EMP hit — the PipBuck lockout (<c>:3276</c>). <b>No call site yet</b>: the trigger is
        /// <c>gg.pipOff</c>, which the port does not model (<c>PipBuckManager</c> records the same
        /// absence). The member stays so the gap is enumerable instead of invisible.
        /// </summary>
        EmpHit,
        /// <summary><c>changeWeapon()</c> (<c>:3590</c>).</summary>
        WeaponChange,
        /// <summary>
        /// <c>changePaintWeapon()</c> (<c>:3661</c>). <b>No call site, and none is possible</b>: the
        /// port has no <c>WPaint</c> and <c>WeaponControllerFactory</c> records that tip 12 never
        /// occurs in <c>AllData</c>, so there is no paint weapon to change.
        /// </summary>
        PaintWeaponChange,
        /// <summary>
        /// The player left the location — <c>UnitPlayer.outLoc</c> (<c>:602-605</c>). §3.11 calls this
        /// "jump (movement mode 4/5)" because the <c>param1 == 4</c> arm of <c>outLoc</c> launches the
        /// player (<c>dy = -jumpdy</c>); the trigger is the <i>location change</i>, not the jump key.
        /// </summary>
        LocationExit,
        /// <summary>The weapon became invalid (<c>UnitPlayer.as:1152-1155</c>).</summary>
        WeaponInvalid,
        /// <summary>The next shot is unaffordable (<c>Sats.as:209-213</c>).</summary>
        CannotAfford,
        /// <summary>A manual shot while a plan exists (<c>Sats.as:205-208</c>).</summary>
        ManualFire,
        /// <summary>Re-entering the mode (<c>Sats.as:156-159</c>).</summary>
        Reentry,
    }
}
