using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using PFE.Systems.Telekinesis;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The alicorn's telekinesis — AS3 <c>UnitAlicorn.findBox / upTeleObj / throwTele / dropTeleObj</c>
    /// (<c>fe/unit/UnitAlicorn.as:1140-1246</c>) driven by the cycle in <c>control():1026-1084</c>.
    ///
    /// <para><b>This is the enemy half of telekinesis, and it is the half the port was missing.</b> The
    /// player's grab (<c>PlayerTelekinesisController</c>) is cursor-driven and gated on mass; this one is
    /// AI-driven, has no mana cost, ignores <c>maxTeleMassa</c> entirely, and — the part that changes the
    /// fight — <b>picks the player up and throws them</b>. AS3's <c>findBox</c> prefers
    /// <c>celUnit</c> on a 25 % roll before it ever looks at a crate, so an engaged alicorn's telekinesis
    /// is a threat rather than a party trick.</para>
    ///
    /// <para><b>Two target kinds, one cycle.</b> A <i>unit</i> target is moved through
    /// <c>UnitController.AddVelocityPixelsPerFrame</c> plus <c>SetLevitated(true)</c> — the same seam the
    /// zombie's pull uses (<c>UnitZombie.as:1163-1192</c>), because AS3's <c>upTeleObj</c> only sets
    /// <c>teleObj.levit</c> and lets the unit's own <c>forces()</c> integrate it. A <i>prop</i> target is
    /// handed to <see cref="RoomObjectPhysicsLayer"/>'s hold, which already owns the accel + damping +
    /// collide step; this class only writes the hold point each tick.</para>
    ///
    /// <para><b>All the arithmetic is in <see cref="AlicornTelekinesisMath"/> and
    /// <see cref="AlicornVariantRules"/></b>, which are Unity-free and pinned by offline fixtures. This
    /// class is the plumbing: who is grabbed, when the throw happens, and what gets written where.</para>
    /// </summary>
    internal sealed class AlicornTelekinesis
    {
        /// <summary>Pixels per Unity unit — <c>TileQueryConstants.UnitToPixel</c>'s value.</summary>
        const float PixelsPerUnit = 100f;

        readonly AlicornBrain _brain;

        /// <summary>AS3 <c>t_tele</c> — the cycle phase. Counts up; negative is a cooldown.</summary>
        int _phase = AlicornVariantRules.TeleCycleStart;

        UnitController _heldUnit;
        ObjectInstance _heldProp;

        public AlicornTelekinesis(AlicornBrain brain)
        {
            _brain = brain;
        }

        /// <summary>AS3 <c>t_tele</c>, exposed for the debug overlay and for fixtures.</summary>
        public int Phase => _phase;

        public bool IsHolding => _heldUnit != null || _heldProp != null;

        public UnitController HeldUnit => _heldUnit;

        public ObjectInstance HeldProp => _heldProp;

        /// <summary>
        /// One tick of <c>control():1026-1084</c>, in the oracle's own order. Called from the brain's
        /// tick after the state machine has run, because the grab branch reads <c>aiState</c>.
        /// </summary>
        public void Tick()
        {
            UnitController self = _brain != null ? _brain.SelfController : null;
            if (self == null || !self.IsAlive)
            {
                Drop();
                return;
            }

            // `++this.t_tele;` (:1026) — unconditional, before any gate.
            _phase++;

            // `if(t_tele == 0) { if(aiState <= 1 || findBox() == null) t_tele = -(...); }` (:1027-1033).
            // Note the disjunction: a CALM unit also re-phases, it does not merely skip. Leaving the
            // phase at zero would freeze the cycle, because t_tele only passes through 0 once.
            if (AlicornVariantRules.IsTeleGrabTick(_phase))
            {
                bool grabbed = _brain.FamilyState > 1 && TryGrab(self);
                if (!grabbed)
                {
                    _phase = AlicornVariantRules.TeleAbortPhase(Roll());
                }
            }

            // `if(t_tele >= tTeleThrow + 10) t_tele = -(...)` (:1034) — the safety valve for a cycle
            // that reached the throw window without holding anything.
            if (AlicornVariantRules.ShouldAbortTeleCycle(_phase))
            {
                _phase = AlicornVariantRules.TeleAbortPhase(Roll());
            }

            if (!IsHolding)
            {
                return;
            }

            TickHold(self);

            // `if(t_tele >= tTeleThrow)` (:1066) — throw if there is still a target, otherwise let go.
            if (AlicornVariantRules.ShouldThrowHeld(_phase))
            {
                // `if(celUnit)` (:1068) — and `celUnit` is the oracle's REMEMBERED sighting, not "can I
                // see it right now". Gating on `TargetUnit` alone was a real bug: the port clears
                // `TargetUnit` on the first obscured tick, while a grab→throw cycle lasts `tTeleThrow`
                // = 60 ticks, so an alicorn that grabbed a crate and then lost line of sight for one
                // tick took the `else` branch and dropped the prop with zero velocity — the prop fell
                // under gravity instead of being thrown at the player. `HasLastKnownTargetPosition` is
                // this port's `celX`/`celY` surviving the sighting, which is what `celUnit` means here.
                if (HasRememberedTarget)
                {
                    Throw(self);
                    _phase = AlicornVariantRules.TelePhaseAfterThrow(Tier);
                }
                else
                {
                    Drop();
                    _phase = AlicornVariantRules.TelePhaseAfterDrop(Tier);
                }
            }

            // `if(teleObj.levit == 1) { dropTeleObj(); t_tele = -tTeleRes; }` (:1079-1083) — the caster
            // lets go the moment the held thing starts holding ITSELF up. Checked last, exactly as the
            // oracle does, so a throw and a self-lift on the same tick both resolve.
            if (IsHolding && TargetHoldsItselfUp())
            {
                Drop();
                _phase = AlicornVariantRules.TelePhaseAfterThrow(Tier);
            }
        }

        /// <summary>
        /// <c>dropTeleObj()</c> (<c>:1192-1203</c>) — release without an impulse. Safe to call when
        /// nothing is held.
        /// </summary>
        public void Drop()
        {
            if (_heldUnit != null)
            {
                if (_heldUnit.IsLevitated)
                {
                    _heldUnit.SetLevitated(false);
                }

                _heldUnit = null;
            }

            if (_heldProp != null)
            {
                RoomInstance room = _brain != null ? _brain.Room : null;
                RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
                layer?.TryReleaseTelekineticHold(_heldProp, Vector2.zero, treatAsThrow: false);
                _heldProp = null;
            }
        }

        int Tier => _brain != null ? _brain.Tier : 0;

        /// <summary>
        /// The oracle's <c>celUnit</c> as this port can express it — a target it can see <b>or</b> one
        /// it remembers having seen.
        /// </summary>
        /// <remarks>
        /// <c>celUnit</c> is cleared only when the sighting budget <c>aiSpok</c> runs out, so it survives
        /// the player stepping behind a wall. The port splits the two facts: <c>TargetUnit</c> is "visible
        /// this tick" and <c>HasLastKnownTargetPosition</c> is the remembered position. Reading only the
        /// first made the throw window (60 ticks) longer than a typical LOS break, so the throw became a
        /// drop — see the gate's remarks in <see cref="Tick"/>.
        /// </remarks>
        bool HasRememberedTarget =>
            _brain != null &&
            (_brain.Blackboard.TargetUnit != null || _brain.Blackboard.HasLastKnownTargetPosition);

        static float Roll() => UnityEngine.Random.value;

        // ── findBox ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>findBox()</c> (<c>:1140-1168</c>): the player first on a 25 % roll, then the props.
        /// </summary>
        bool TryGrab(UnitController self)
        {
            // `if(celUnit && isrnd(0.25)) { upTeleObj(celUnit); … return teleObj; }` (:1143-1152).
            // No range and no line-of-sight test here — `celUnit` is the unit this alicorn can SEE, and
            // being visible already implies both. Adding a second distance gate would refuse grabs the
            // oracle makes at the edge of its vision.
            UnitController unitTarget = _brain.Blackboard.TargetUnit;
            if (unitTarget != null && unitTarget.IsAlive && AlicornTelekinesisMath.PrefersUnitTarget(Roll()))
            {
                if (GrabUnit(unitTarget))
                {
                    return true;
                }
            }

            ObjectInstance prop = FindProp(self);
            if (prop == null)
            {
                return false;
            }

            GrabProp(self, prop);
            return true;
        }

        /// <summary>
        /// The prop half of <c>findBox</c> — <c>:1153-1166</c>: walk the props in order, accept each on
        /// its own 30 % roll, then test range and line of sight.
        /// </summary>
        ObjectInstance FindProp(UnitController self)
        {
            RoomInstance room = _brain.Room;
            RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
            if (layer == null || _brain.TileQueryService == null)
            {
                return null;
            }

            Vector2 origin = _brain.TileQueryService.OriginPixel;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;

            System.Collections.Generic.IReadOnlyList<ObjectInstance> props = layer.DynamicObjects;
            for (int i = 0; i < props.Count; i++)
            {
                ObjectInstance candidate = props[i];
                if (candidate == null ||
                    candidate.IsDestroyed() ||
                    !candidate.isActive ||
                    !candidate.IsLiftable() ||
                    candidate.IsHeldByTelekinesis())
                {
                    continue;
                }

                if (!AlicornVariantRules.PropIsGrabbable(
                        liftable: candidate.IsLiftable(),
                        alreadyLevitating: candidate.IsHeldByTelekinesis(),
                        massa: candidate.GetAs3Massa()))
                {
                    continue;
                }

                if (!AlicornTelekinesisMath.AcceptsProp(Roll()))
                {
                    continue;
                }

                Vector2 candidateCentre = candidate.GetApproximateBounds().center;
                if (!AlicornTelekinesisMath.InTeleRange((candidateCentre - selfLocal).sqrMagnitude))
                {
                    continue;
                }

                if (!HasLineOfSight(self, origin, candidateCentre))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// <c>loc.isLine(X, Y - scY/2, obj.X, obj.Y - obj.scY/2)</c> (<c>:1159</c>) — from the caster's
        /// vertical centre to the prop's.
        /// </summary>
        bool HasLineOfSight(UnitController self, Vector2 origin, Vector2 propCentreLocal)
        {
            var query = _brain.TileQueryService;
            if (query == null)
            {
                return false;
            }

            Vector2 selfFeetWorld = (Vector2)self.transform.position * PixelsPerUnit;
            float heightPx = self.SpriteSizePixels.y;
            Vector2 from = selfFeetWorld + new Vector2(0f, heightPx * 0.5f);
            Vector2 to = propCentreLocal + origin;

            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-3f)
            {
                return true;
            }

            return query.Raycast(from, delta / length, length) == null;
        }

        // ── upTeleObj ────────────────────────────────────────────────────────────────────────────

        bool GrabUnit(UnitController unit)
        {
            // AS3 refuses to grab something already levitating itself (`levit != 1` on the caster's
            // side is a different check — see `upTeleObj`'s `levit = 2`). A flyer or a player mid-perk
            // is not liftable, and grabbing one would leave a unit floating with no owner.
            if (unit.IsFlyingUnit || unit.IsLevitated)
            {
                return false;
            }

            _heldUnit = unit;
            _heldUnit.SetLevitated(true);
            return true;
        }

        void GrabProp(UnitController self, ObjectInstance prop)
        {
            RoomInstance room = _brain.Room;
            RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
            if (layer == null)
            {
                return;
            }

            AlicornTuning tuning = AlicornVariantRules.For(Tier);
            Vector2 target = HoldTargetFor(self, prop);

            // `this.teleObj.fracLevit = fraction;` (UnitAlicorn.as:1181) — the holder's faction rides
            // on the prop, so Box.attDrop (:924) will not let it hurt this alicorn's own side.
            if (!layer.TrySetTelekineticHold(prop, target, tuning.TeleSpeed, tuning.TeleAccel,
                    (int)self.Faction))
            {
                return;
            }

            _heldProp = prop;
        }

        // ── the hold ─────────────────────────────────────────────────────────────────────────────

        void TickHold(UnitController self)
        {
            if (_heldProp != null)
            {
                TickHeldProp(self, _heldProp);
                return;
            }

            if (_heldUnit != null)
            {
                TickHeldUnit(self, _heldUnit);
            }
        }

        void TickHeldUnit(UnitController self, UnitController unit)
        {
            if (!unit.IsAlive)
            {
                Drop();
                return;
            }

            // `upTeleObj`'s `levit` stamp is re-asserted every tick, exactly as the zombie's pull does
            // (`UnitZombie.as:1176-1180`): a victim that was cleared by something else mid-pull would
            // otherwise sink out of the grip.
            if (!unit.IsLevitated)
            {
                unit.SetLevitated(true);
            }

            Vector2 origin = _brain.TileQueryService != null ? _brain.TileQueryService.OriginPixel : Vector2.zero;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;
            Vector2 unitLocal = ((Vector2)unit.transform.position * PixelsPerUnit) - origin;

            AlicornTuning tuning = AlicornVariantRules.For(Tier);
            Vector2 target = AlicornTelekinesisMath.HoldTarget(
                selfLocal.x, selfLocal.y, self.FacingDirection, targetIsUnit: true);

            Vector2 velocity = unit.VelocityPixelsPerFrame;
            Vector2 next = AlicornTelekinesisMath.HoldAccelStep(
                unitLocal.x,
                unitLocal.y,
                target,
                velocity,
                tuning.TeleSpeed,
                tuning.TeleAccel,
                AlicornVariantRules.HoldDeadzone);

            // **`HoldAccelStep` answers the RESULTING velocity, not a delta.** The oracle's block is
            // `if(teleObj.X < teleX - derp && teleObj.dx < teleSpeed) teleObj.dx += teleAccel` — an
            // assignment to `dx` — so the function returns `velocity + accel`. Handing that straight to
            // `AddVelocityPixelsPerFrame` would add the whole velocity on every tick (`v += v`) and fling
            // the held unit out of the grip within a few frames. The difference is the only correct
            // thing to add.
            Vector2 delta = next - velocity;
            if (delta.sqrMagnitude > 0f)
            {
                unit.AddVelocityPixelsPerFrame(delta);
            }
        }

        void TickHeldProp(UnitController self, ObjectInstance prop)
        {
            if (prop.IsDestroyed() || !prop.IsHeldByTelekinesis())
            {
                // The layer released it (an impulse, or the room went away). Forget it rather than
                // keep writing a target to a prop nobody is holding.
                _heldProp = null;
                return;
            }

            RoomInstance room = _brain.Room;
            RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
            if (layer == null)
            {
                Drop();
                return;
            }

            // Re-asserted every tick, like the player's `UpdateHold` does: `TryApplyImpulse` and
            // `TryReleaseTelekineticHold` both clear `hasTelekineticTarget`, and a stale false makes the
            // prop chase its own position — motionless, with nothing in the log to explain it.
            MapObjectDynamicStateData state = prop.runtimeState.dynamicState;
            state.isHeldByTelekinesis = true;
            state.hasTelekineticTarget = true;
            state.telekineticTarget = HoldTargetFor(self, prop);
        }

        /// <summary>
        /// The hold point for a prop, converted to the reference point
        /// <see cref="RoomObjectPhysicsLayer.StepHeldObject"/> actually compares.
        /// </summary>
        /// <remarks>
        /// <b>The two telekinesis controllers compare different points, and both are the oracle's.</b>
        /// <c>UnitAlicorn.as:1058</c> tests <c>teleObj.Y</c> — the prop's <b>bottom</b> — against
        /// <c>teleY</c>. <c>StepHeldObject</c> compares the prop's AABB <b>centre</b>, because that is
        /// what the player's controller does (<c>UnitPlayer.as:1247</c>). Rather than teach the shared
        /// step a second reference point, the offset is applied here: a centre target of
        /// <c>teleY + height/2</c> is exactly "put the bottom at <c>teleY</c>".
        /// </remarks>
        Vector2 HoldTargetFor(UnitController self, ObjectInstance prop)
        {
            Vector2 origin = _brain.TileQueryService != null ? _brain.TileQueryService.OriginPixel : Vector2.zero;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;

            Vector2 target = AlicornTelekinesisMath.HoldTarget(
                selfLocal.x, selfLocal.y, self.FacingDirection, targetIsUnit: false);

            target.y += prop.GetApproximateBounds().height * 0.5f;
            return target;
        }

        // ── throwTele ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>throwTele()</c> (<c>:1205-1246</c>) — the impulse, then the release.
        /// </summary>
        void Throw(UnitController self)
        {
            UnitController target = _brain.Blackboard.TargetUnit;
            Vector2 origin = _brain.TileQueryService != null ? _brain.TileQueryService.OriginPixel : Vector2.zero;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;

            // `celX`/`celY` — the look-at point. For a visible unit that is its own position; for a
            // remembered one it is `LastKnownTargetPosition`. The remembered arm is not a fallback for a
            // broken case: the throw gate accepts a remembered target on purpose (`if(celUnit)`), so
            // without this the aim point would collapse to the caster's own position and the prop would
            // be flung at the alicorn's feet instead of at the player.
            Vector2 celLocal;
            if (target != null)
            {
                celLocal = ((Vector2)target.transform.position * PixelsPerUnit) - origin;
            }
            else if (_brain != null && _brain.Blackboard.HasLastKnownTargetPosition)
            {
                celLocal = _brain.Blackboard.LastKnownTargetPosition - origin;
            }
            else
            {
                celLocal = selfLocal;
            }

            AlicornTuning tuning = AlicornVariantRules.For(Tier);

            if (_heldUnit != null)
            {
                AlicornTelekinesisMath.ThrowImpulse(
                    targetIsUnit: true,
                    facing: self.FacingDirection,
                    objectX: 0f,
                    objectCentreY: 0f,
                    celX: celLocal.x,
                    celY: celLocal.y,
                    throwForce: tuning.ThrowForce,
                    massa: 0f,
                    out float impulseX,
                    out float impulseY);

                _heldUnit.AddVelocityPixelsPerFrame(new Vector2(impulseX, impulseY));

                // `(teleObj as UnitPlayer).t_throw = 30` (:1234) — the thrown player keeps a 30-tick
                // throw window in which their own collision with the world is handled as a projectile.
                // The port's `UnitController` has no `t_throw`, so the impulse is all that lands; see
                // the gap note in the alicorn family doc.
                Drop();
                return;
            }

            if (_heldProp != null)
            {
                Rect bounds = _heldProp.GetApproximateBounds();
                AlicornTelekinesisMath.ThrowImpulse(
                    targetIsUnit: false,
                    facing: self.FacingDirection,
                    objectX: bounds.center.x,
                    objectCentreY: bounds.center.y,
                    celX: celLocal.x,
                    celY: celLocal.y,
                    throwForce: tuning.ThrowForce,
                    massa: _heldProp.GetAs3Massa(),
                    out float impulseX,
                    out float impulseY);

                RoomInstance room = _brain.Room;
                RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;

                // `UnitAlicorn.as:1238` — `(this.teleObj as Box).isThrow = true;` and NOTHING else.
                // There is no `t_throw` write anywhere in `throwTele`, so an alicorn's throw carries a
                // ZERO-tick damage grace: the crate hurts the first unit it overlaps, on the tick it is
                // released. Passing the player's two-tick window here (which is what the shared seam
                // used to do implicitly) made every alicorn-thrown crate harmless for its whole flight.
                layer?.TryReleaseTelekineticHold(
                    _heldProp,
                    new Vector2(
                        TelekinesisMath.PerFrameVelocityToPerSecond(impulseX),
                        TelekinesisMath.PerFrameVelocityToPerSecond(impulseY)),
                    treatAsThrow: true,
                    throwGraceTicks: PropThrowGrace.EnemyThrowTicks);

                _heldProp = null;
            }
        }

        /// <summary>
        /// <c>teleObj.levit == 1</c> on the caster's side — the target has started holding itself up.
        /// </summary>
        bool TargetHoldsItselfUp()
        {
            if (_heldUnit != null)
            {
                // Our own stamp is `levit = 2` (IsLevitated), so it must NOT be part of this test: a
                // held player is levitated by definition, and reading that back would drop them on the
                // grab tick.
                return _heldUnit.IsFlyingUnit;
            }

            return false;
        }
    }
}
