using PFE.Entities.Enemies.Bosses;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using PFE.Systems.Telekinesis;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The boss alicorn's telekinesis — AS3 <c>UnitBossAlicorn.findBox / upTeleObj / throwTele /
    /// dropTeleObj</c> (<c>fe/unit/UnitBossAlicorn.as:591-701</c>) plus the hold block that is the tail
    /// of <c>control():533-565</c>.
    ///
    /// <para><b>Why this is not <see cref="AlicornTelekinesis"/>.</b> The two classes look alike and are
    /// not the same mechanic:</para>
    /// <list type="bullet">
    ///   <item><description><b>Driven differently.</b> The family's cycle is its own <c>t_tele</c>
    ///     countdown that grabs, holds 60 ticks and throws. The boss has <b>no cycle at all</b> — the
    ///     grab happens on the <c>aiState == 2</c> beat (<c>:447</c>) and the throw on the
    ///     <c>aiState == 3</c> beat (<c>:461</c>), both paced by the boss's <c>aiTCh</c> rotation. Its
    ///     hold, meanwhile, runs <i>every</i> tick, because the block is outside the ladder.</description></item>
    ///   <item><description><b>Different numbers.</b> Grab chance 50 % against the family's 25 %, range
    ///     1000 px against 600, throw force 60 against 30/45, hold speed 24/accel 6 against 16/4 or
    ///     20/5 — and the boss adds a near-wall damping term the family does not have
    ///     (<c>:668-671</c>).</description></item>
    ///   <item><description><b>Different failure mode.</b> A family alicorn that grabs nothing waits out
    ///     <c>t_tele</c>; the boss retries in <b>15 ticks</b> instead of 45 (<c>:448-450</c>), because a
    ///     boss with nothing to throw has no attack that beat.</description></item>
    /// </list>
    ///
    /// <para><b>The arithmetic is in <see cref="BossAlicornRules"/> and
    /// <see cref="AlicornTelekinesisMath"/>, both Unity-free and pinned offline.</b> The hold target
    /// (150 px for a unit, 80 for a prop, 40 above the feet) and the two throw directions are
    /// <i>identical</i> in the two classes, so they share the math and not the class — which is the
    /// rule this project already paid for once (<c>TelekinesisMath</c>'s header).</para>
    /// </summary>
    internal sealed class BossAlicornTelekinesis
    {
        const float PixelsPerUnit = 100f;

        readonly BossAlicornBrain _brain;

        UnitController _heldUnit;
        ObjectInstance _heldProp;

        /// <summary>One warning per boss, not one per failed grab attempt.</summary>
        bool _grabRefusalReported;

        public BossAlicornTelekinesis(BossAlicornBrain brain)
        {
            _brain = brain;
        }

        public bool IsHolding => _heldUnit != null || _heldProp != null;

        public UnitController HeldUnit => _heldUnit;

        public ObjectInstance HeldProp => _heldProp;

        UnitController Self => _brain != null ? _brain.SelfController : null;

        /// <summary>
        /// <c>findBox()</c> (<c>:591-619</c>): the player first on a 50 % roll, then the props.
        /// </summary>
        /// <returns><c>true</c> when something was grabbed — which is what the caller's
        /// <c>aiTCh = 45 vs 15</c> branch reads.</returns>
        public bool TryGrab()
        {
            UnitController self = Self;
            if (self == null || !self.IsAlive)
            {
                return false;
            }

            // `if(celUnit && isrnd(0.5)) { upTeleObj(celUnit); … return teleObj; }` (:594-603).
            UnitController target = _brain.Blackboard.TargetUnit;
            if (target != null && target.IsAlive && BossAlicornRules.PrefersUnitTarget(Roll()))
            {
                if (GrabUnit(target))
                {
                    return true;
                }
            }

            ObjectInstance prop = FindProp(self);
            if (prop == null)
            {
                return false;
            }

            // **The hold's answer is the grab's answer.** AS3's `findBox()` returns the object it
            // grabbed (`return this.teleObj;`, :602 / :613) and the caller's `aiTCh = 45 vs 15`
            // branch reads that return. This used to discard it and report success anyway, so a
            // refused hold gave the boss a 45-tick idle with nothing in her grip — which reads from
            // the outside as "she never uses telekinesis on objects".
            return GrabProp(self, prop);
        }

        /// <summary>
        /// The prop half — <c>:604-618</c>: walk the props in order, accept each on its own 30 % roll,
        /// then test range and line of sight.
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

                if (!BossAlicornRules.AcceptsProp(Roll()))
                {
                    continue;
                }

                Vector2 candidateCentre = candidate.GetApproximateBounds().center;
                if (!BossAlicornRules.InTeleRange((candidateCentre - selfLocal).sqrMagnitude))
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
        /// <c>loc.isLine(X, Y - scY/2, obj.X, obj.Y - obj.scY/2)</c> (<c>:610</c>) — caster's vertical
        /// centre to the prop's.
        /// </summary>
        bool HasLineOfSight(UnitController self, Vector2 origin, Vector2 propCentreLocal)
        {
            var query = _brain.TileQueryService;
            if (query == null)
            {
                return false;
            }

            Vector2 selfFeetWorld = (Vector2)self.transform.position * PixelsPerUnit;
            Vector2 from = selfFeetWorld + new Vector2(0f, self.SpriteSizePixels.y * 0.5f);
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
            // `upTeleObj` stamps `levit = 2` on anything that is not the player, and the player's
            // stamp is `pers.teleEnemy`. Either way the target must not already be holding itself up,
            // or the grip and the target's own levitation fight each other.
            if (unit.IsFlyingUnit || unit.IsLevitated)
            {
                return false;
            }

            _heldUnit = unit;
            _heldUnit.SetLevitated(true);
            return true;
        }

        /// <summary>
        /// <c>upTeleObj</c> for a prop, plus the hold's success — which is <c>findBox()</c>'s return
        /// value.
        /// </summary>
        /// <returns>
        /// <c>true</c> only when the prop layer accepted the hold. A <c>false</c> here leaves
        /// <see cref="_heldProp"/> null and lets the caller take the 15-tick retry.
        /// </returns>
        bool GrabProp(UnitController self, ObjectInstance prop)
        {
            RoomInstance room = _brain.Room;
            RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
            if (layer == null)
            {
                return false;
            }

            Vector2 target = HoldTargetFor(self, prop);

            // `this.teleObj.fracLevit = fraction;` (UnitBossAlicorn.as:632) — the boss's own faction
            // goes onto the prop, so Box.attDrop (:924) refuses to damage her with her own throw. This
            // is the fix for "a telekinetic crate hurt the alicorn instead of the player".
            if (!layer.TrySetTelekineticHold(prop, target, BossAlicornRules.TeleSpeed,
                    BossAlicornRules.TeleAccel, (int)self.Faction))
            {
                ReportGrabRefusedOnce(prop, self);
                return false;
            }

            _heldProp = prop;
            return true;
        }

        /// <summary>
        /// One line the first time the prop layer refuses a hold, naming the term that refused.
        /// </summary>
        /// <remarks>
        /// <c>TrySetTelekineticHold</c> refuses for exactly two reasons — the prop does not support
        /// telekinesis, or the layer does not track it — and neither leaves any trace otherwise. The
        /// gate above it (<c>IsLiftable</c>) already implies the first, so a refusal here means the
        /// layer lost the prop between the candidate scan and the hold, which is a real bug worth
        /// seeing once rather than a condition to retry quietly.
        /// </remarks>
        void ReportGrabRefusedOnce(ObjectInstance prop, UnitController self)
        {
            if (_grabRefusalReported)
            {
                return;
            }

            _grabRefusalReported = true;
            Debug.LogWarning(
                $"[BossAlicorn] findBox() selected '{prop?.objectId}' but the prop layer refused the " +
                $"hold (supportsTelekinesis={prop?.SupportsTelekinesis()}, " +
                $"capability={prop?.GetResolvedPhysicalCapability()}, " +
                $"bossFaction={(int)(self?.Faction ?? PFE.Data.Definitions.FactionType.Neutral)}). " +
                "The boss will retry in 15 ticks.");
        }

        // ── the hold (control():533-565) ─────────────────────────────────────────────────────────

        /// <summary>
        /// One tick of the hold block. Called <b>after</b> the rotation, every tick, alive or not
        /// holding — the oracle's block is the tail of <c>control()</c> and runs unconditionally.
        /// </summary>
        public void TickHold()
        {
            if (_heldProp != null)
            {
                TickHeldProp(_heldProp);
                return;
            }

            if (_heldUnit != null)
            {
                TickHeldUnit(_heldUnit);
            }
        }

        void TickHeldUnit(UnitController unit)
        {
            UnitController self = Self;
            if (self == null || !unit.IsAlive)
            {
                Drop();
                return;
            }

            if (!unit.IsLevitated)
            {
                unit.SetLevitated(true);
            }

            Vector2 origin = _brain.TileQueryService != null ? _brain.TileQueryService.OriginPixel : Vector2.zero;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;
            Vector2 unitLocal = ((Vector2)unit.transform.position * PixelsPerUnit) - origin;

            Vector2 target = AlicornTelekinesisMath.HoldTarget(
                selfLocal.x, selfLocal.y, self.FacingDirection, targetIsUnit: true);

            Vector2 velocity = unit.VelocityPixelsPerFrame;
            Vector2 next = AlicornTelekinesisMath.HoldAccelStep(
                unitLocal.x,
                unitLocal.y,
                target,
                velocity,
                BossAlicornRules.TeleSpeed,
                BossAlicornRules.TeleAccel,
                BossAlicornRules.HoldDeadzone);

            // `HoldAccelStep` answers the RESULTING velocity, not a delta — AS3 assigns to `dx`
            // (`teleObj.dx += teleAccel`). Feeding it back as a delta compounds the velocity every
            // tick and flings the victim out of the grip; the family paid for this already.
            Vector2 delta = next - velocity;
            if (delta.sqrMagnitude > 0f)
            {
                unit.AddVelocityPixelsPerFrame(delta);
            }
        }

        void TickHeldProp(ObjectInstance prop)
        {
            if (prop.IsDestroyed() || !prop.IsHeldByTelekinesis())
            {
                _heldProp = null;
                return;
            }

            UnitController self = Self;
            if (self == null)
            {
                Drop();
                return;
            }

            RoomInstance room = _brain.Room;
            RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;
            if (layer == null)
            {
                Drop();
                return;
            }

            // Re-asserted every tick: both `TryApplyImpulse` and `TryReleaseTelekineticHold` clear
            // `hasTelekineticTarget`, and a stale false makes the prop chase its own position.
            MapObjectDynamicStateData state = prop.runtimeState.dynamicState;
            state.isHeldByTelekinesis = true;
            state.hasTelekineticTarget = true;
            state.telekineticTarget = HoldTargetFor(self, prop);
        }

        /// <summary>
        /// The prop hold point, converted to the reference point
        /// <see cref="RoomObjectPhysicsLayer.StepHeldObject"/> compares.
        /// </summary>
        /// <remarks>
        /// <c>UnitBossAlicorn.as:544-552</c> tests <c>teleObj.Y</c> — the prop's <b>bottom</b> — while
        /// <c>StepHeldObject</c> compares its AABB <b>centre</b>. A centre target of
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

        // ── throwTele (UnitBossAlicorn.as:656-701) ───────────────────────────────────────────────

        /// <summary>
        /// <c>throwTele()</c> — the impulse, then the release. Called from the <c>aiState == 3</c> /
        /// <c>attState == 3</c> beat.
        /// </summary>
        public void Throw()
        {
            UnitController self = Self;
            if (self == null)
            {
                return;
            }

            RoomInstance room = _brain.Room;
            float roomLimitX = room != null ? room.width * 40f : float.MaxValue;

            Vector2 origin = _brain.TileQueryService != null ? _brain.TileQueryService.OriginPixel : Vector2.zero;
            Vector2 selfLocal = ((Vector2)self.transform.position * PixelsPerUnit) - origin;

            if (_heldUnit != null)
            {
                Vector2 unitLocal = ((Vector2)_heldUnit.transform.position * PixelsPerUnit) - origin;

                // `_loc2_ = throwForce; if(teleObj.massa > 1) _loc2_ /= sqrt(massa);` (:663-667) applies
                // to a **unit** as much as to a crate — `teleObj` is `celUnit` there. It happens to be a
                // no-op for the player (`Obj.massa` defaults to 1 and `pony` has no `<phis>`, so `:1058`
                // leaves it at 1), but not for a pet or a companion, whose `mass` is a real number.
                float unitMassa = _heldUnit.Definition != null ? _heldUnit.Definition.mass : 1f;
                float speed = BossAlicornRules.ThrowSpeed(
                    BossAlicornRules.ThrowForce, unitMassa, BossAlicornRules.IsNearWall(unitLocal.x, roomLimitX));

                // `{x: 100 * storona, y: -30}` (:672-678) — a unit is always thrown the way the boss
                // faces, never "at" anything. The magnitude is `norma`'s, i.e. the throw speed.
                float length = Mathf.Sqrt(100f * 100f + 30f * 30f);
                float k = speed / length;
                float dx = 100f * (self.FacingDirection >= 0 ? 1f : -1f) * k;

                // AS3 `y: -30` is up in a y-down space; the port's y is up, so it stays positive.
                float dy = 30f * k;

                _heldUnit.AddVelocityPixelsPerFrame(new Vector2(dx, dy));

                // `(teleObj as UnitPlayer).t_throw = 30` (:686-690) — the thrown player's own
                // collision is treated as a projectile for 30 ticks. The port has no `t_throw`, so the
                // impulse is all that lands; recorded in the boss doc rather than faked here.
                Drop();
                return;
            }

            if (_heldProp != null)
            {
                Rect bounds = _heldProp.GetApproximateBounds();
                float speed = BossAlicornRules.ThrowSpeed(
                    BossAlicornRules.ThrowForce,
                    _heldProp.GetAs3Massa(),
                    BossAlicornRules.IsNearWall(bounds.center.x, roomLimitX));

                // The prop is thrown AT the target: `{x: celX - teleObj.X, y: celY - (teleObj.Y -
                // scY/2) - |celX - teleObj.X| / 4}` (:681-685), with the |Δx|/4 term arcing it.
                Vector2 celLocal = TargetPoint(origin, selfLocal);
                float dx = celLocal.x - bounds.center.x;
                float dy = celLocal.y - bounds.center.y + Mathf.Abs(dx) / 4f;

                float length = Mathf.Sqrt(dx * dx + dy * dy);
                Vector2 impulse = length < 1e-4f
                    ? Vector2.zero
                    : new Vector2(dx, dy) * (speed / length);

                RoomObjectPhysicsLayer layer = room != null ? room.ExistingObjectPhysicsLayer : null;

                // `UnitBossAlicorn.as:693` — `isThrow = true`, no `t_throw`. The boss's throw has no
                // damage grace either; see the alicorn's identical call site.
                layer?.TryReleaseTelekineticHold(
                    _heldProp,
                    new Vector2(
                        TelekinesisMath.PerFrameVelocityToPerSecond(impulse.x),
                        TelekinesisMath.PerFrameVelocityToPerSecond(impulse.y)),
                    treatAsThrow: true,
                    throwGraceTicks: PropThrowGrace.EnemyThrowTicks);

                _heldProp = null;
            }
        }

        /// <summary>
        /// <c>celX</c>/<c>celY</c> as this port can express them — the visible target, else the
        /// remembered sighting, else the boss's own feet.
        /// </summary>
        Vector2 TargetPoint(Vector2 origin, Vector2 selfLocal)
        {
            UnitController target = _brain.Blackboard.TargetUnit;
            if (target != null)
            {
                return ((Vector2)target.transform.position * PixelsPerUnit) - origin;
            }

            if (_brain.Blackboard.HasLastKnownTargetPosition)
            {
                return _brain.Blackboard.LastKnownTargetPosition - origin;
            }

            return selfLocal;
        }

        /// <summary>
        /// <c>dropTeleObj()</c> (<c>:643-654</c>) — release without an impulse.
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

        static float Roll() => UnityEngine.Random.value;
    }
}
