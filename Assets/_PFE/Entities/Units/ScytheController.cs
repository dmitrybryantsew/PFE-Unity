using PFE.Entities.Player;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The Manehattan final boss's blades — the port of <c>fe/unit/UnitScythe.as</c>.
    ///
    /// <para><b>What this unit is.</b> Not a projectile: a full <c>Unit</c> with health, a collider and
    /// a tick counter, spawned six at a time when the Super Alicorn's dome shatters and one every
    /// thirty ticks while she is invisible. Free, it flies in a straight line at a latched angle,
    /// accelerating five percent per tick, and <b>cannot hurt anything for its first sixty
    /// ticks</b> (<see cref="ScytheRules.ContactDelayTicks"/>) — it is a sweeping hazard, not a dart.
    /// Bound, it orbits her at 200 px on one of six evenly-spaced phases and is eight times as
    /// tough.</para>
    ///
    /// <para><b>Why the position is written by hand rather than by the shared mover.</b>
    /// <c>UnitScythe.run()</c> (<c>:95-115</c>) <i>overrides</i> the base <c>run()</c> outright, so
    /// none of AS3's integration, wall resolution or friction ever runs for a blade: it passes
    /// through walls and dies only on leaving the room. The port's equivalent of "the base
    /// integrator must not write my position" is <see cref="IsFixed"/> — see the remark on that
    /// override, which is the one place this substitution is argued.</para>
    ///
    /// <para><b>What is NOT ported.</b> <c>mater = false</c> (<c>:39</c>) has no consumer — the port
    /// has no matter flag. <c>doop = true</c> (<c>:43</c>) is the "passive target" flag AS3 reads for
    /// the battle timer, aggro and crit-invisibility (<c>Unit.as:3659/3760/3783/4377</c>), none of
    /// which the port has. <c>brake = accel / 2</c> (<c>:42</c>) is dead in the oracle too, because
    /// the only code that reads <c>brake</c> is the base <c>run()</c> the subclass replaced.
    /// <c>aiN</c> (<c>:29</c>) is assigned and never read.</para>
    /// </summary>
    public class ScytheController : UnitController
    {
        /// <summary>
        /// AS3 <c>UnitScythe</c>'s own id — <c>id = "scythe"</c> (<c>:34</c>).
        ///
        /// <para><b>Keyed on the unit id, not on a <c>cl</c> attribute.</b> <c>Unit.as:694</c>
        /// special-cases this id ahead of the XML lookup — <c>UnitScythe</c> is never named by a
        /// <c>cl=</c> row, and <c>scythe.asset</c> consequently has an empty <c>controllerId</c>. The
        /// spawner's fallback chain therefore lands on <c>unitId</c>, which is this string.</para>
        /// </summary>
        public const string ControllerId = "scythe";

        /// <summary><c>vis = new visualScythe()</c> (<c>:35</c>). One frame, one PNG — the oracle's
        /// spin is a <c>rotation</c> on that single frame, not a frame animation.</summary>
        public const string VisualClassName = "visualScythe";

        // ── state (UnitScythe.as:9-29) ────────────────────────────────────────────────────────────

        /// <summary><c>napr</c> (<c>:9</c>) — the latched flight angle, or <see cref="ScytheRules.NoAngle"/>.</summary>
        float _napr = ScytheRules.NoAngle;

        /// <summary><c>skor</c> (<c>:15</c>) — the current speed, in pixels per frame.</summary>
        float _skor = ScytheRules.InitialSpeed;

        /// <summary><c>t</c> (<c>:13</c>) — incremented by <c>control()</c> and read by both the orbit
        /// phase and the sixty-tick damage delay.</summary>
        int _t;

        /// <summary><c>bindN</c> (<c>:23</c>) — the ring slot, <c>-1</c> when free.</summary>
        int _bindN = -1;

        /// <summary><c>bind</c> (<c>Unit.as:206</c>) — the host this blade orbits, or <c>null</c>.</summary>
        UnitController _bind;

        /// <summary><c>cel</c> (<c>:17</c>) — the unit the blade was released at. Latched with
        /// <see cref="_napr"/> and never re-aimed.</summary>
        UnitController _cel;

        /// <summary><c>dr</c> (<c>:11</c>) — the visual spin rate, degrees per frame.</summary>
        float _spin;

        /// <summary><c>vis.osn.alpha</c> (<c>:37</c>) — starts at zero and fades in.</summary>
        float _bladeAlpha;

        /// <summary><c>vis.vzz.alpha</c> (<c>:38</c>) — the trail, faded in more slowly.</summary>
        float _trailAlpha;

        // ── reads ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The ring slot this blade occupies, or <c>-1</c> when it is free.</summary>
        public int BindIndex => _bindN;

        /// <summary>Whether this blade orbits a host. AS3 <c>bind != null</c>.</summary>
        public bool IsBound => _bind != null;

        /// <summary><c>t</c> — exposed so the boss overlay can show a blade counting toward its
        /// sixty-tick arming delay.</summary>
        public int Ticks => _t;

        /// <summary>
        /// <c>collisionTip = 0</c> (<c>:44</c>) — a blade that touches a unit damages it but exchanges
        /// no momentum, so it neither shoves nor recoils. See
        /// <see cref="UnitController.CollisionTip"/>.
        /// </summary>
        public override bool CollisionTip => false;

        /// <summary>
        /// AS3 <c>forces()</c> is an <b>empty override</b> (<c>:53-55</c>) — a blade takes no gravity
        /// and no flight brake at all.
        /// </summary>
        /// <remarks>
        /// <para>Two effects, and both are the oracle's. <see cref="UnitController.ApplyGravity"/>
        /// short-circuits to the flight arm, so no <c>dy += World.ddy</c> ever reaches a blade — AS3
        /// has no such term for one. And <see cref="UnitController.IsFlyingUnit"/> is what the
        /// telekinetics test before grabbing (<c>BossAlicornTelekinesis</c>:185), which is AS3's
        /// <c>levitPoss = false</c> (<c>:41</c>) arriving by the same route: <c>findBox()</c> skips a
        /// candidate whose <c>levitPoss</c> is false, and the boss therefore cannot pick up her own
        /// blade.</para>
        /// </remarks>
        protected override bool IsFlying => true;

        /// <summary>
        /// <b>"The shared mover does not write my position"</b> — the port's substitute for
        /// <c>UnitScythe.run()</c> overriding the base <c>run()</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a mechanism substitution, not a claim that the blade is immobile.</b> AS3
        /// has two integrators and the blade replaces one of them: <c>Unit.run()</c> owns
        /// <c>X += dx</c>, wall resolution and friction, and <c>UnitScythe.run()</c> replaces all
        /// three with a straight-line integration and a room-bounds death. The port has the same
        /// single integrator (<see cref="UnitController.Move"/>, plus its motor twin) and the only
        /// gate on it is <c>IsFixed</c> — so <c>IsFixed</c> is what "the base integrator is not
        /// mine" is spelled as here. <see cref="TickControl"/> writes the position instead.</para>
        ///
        /// <para><b>Why not simply write the position from <c>TickControl</c> and let <c>Move</c>
        /// run.</b> Because <c>Move</c> would then fight it on two counts, both of them real
        /// divergences rather than rounding: <c>ResolveHorizontalMotion</c> stops the blade at a wall
        /// (the oracle's blades pass through walls), and the <c>_groundProp</c> snap rewrites Y to a
        /// crate's top edge the moment a blade passes over one. Gating the one write the oracle gates
        /// is the same move <c>Move</c>'s own remark makes for a <c>fixed</c> turret.</para>
        ///
        /// <para><b>The two side effects of <c>IsFixed</c> are both correct for a blade.</b> A thrown
        /// crate's momentum exchange leaves the blade's velocity alone
        /// (<c>PropImpactMath.ResolveExchange</c>'s <c>unitIsFixed</c> arm) — the oracle's
        /// <c>run()</c> never reads <c>osndx</c>, so a crate cannot deflect a blade there either. And
        /// a spell blast takes no displacement from it for the same reason. Neither suppresses the
        /// <i>damage</i>: <c>PropImpactMath</c> is a velocity exchange, and the hit is reported
        /// separately, so a bound blade can still be shot or crushed to death.</para>
        /// </remarks>
        public override bool IsFixed => true;

        // ── setup, called by the boss after the spawner has built the body ────────────────────────

        /// <summary>
        /// <c>_loc2_.bind = this; _loc2_.maxhp *= 8; _loc2_.hp = _loc2_.maxhp; bindN = param1;</c>
        /// (<c>UnitBossAlicorn.as:300-306</c>).
        /// </summary>
        /// <param name="host">The boss. Stored as-is; a dead host kills the blade.</param>
        /// <param name="bindIndex">The ring slot, 0..5.</param>
        /// <remarks>
        /// Applied here rather than at spawn because the spawner seeds health from the
        /// <i>definition</i> (<c>RoomUnitSpawner.Spawn</c>) and knows nothing about binding — the same
        /// ordering <c>BossSummonSpawner</c> documents for its own post-spawn damage scale.
        /// </remarks>
        public void BindTo(UnitController host, int bindIndex)
        {
            _bind = host;
            _bindN = Mathf.Clamp(bindIndex, 0, ScytheRules.BindCount - 1);

            if (_unitStats != null)
            {
                float health = _unitStats.MaxHp.Value * BossAlicornRules.BoundScytheHealthMultiplier;
                _unitStats.MaxHp.Value = health;
                _unitStats.CurrentHp.Value = health;
            }
        }

        /// <summary>
        /// <c>_loc2_.dam *= 1 + level * 0.1</c> (<c>UnitBossAlicorn.as:298</c>).
        /// </summary>
        /// <remarks>
        /// The multiplier lands on <see cref="UnitController.MeleeDamage"/>'s private scale rather
        /// than on <c>UnitDefinition.damage</c>, because that asset is shared by every blade the boss
        /// will ever emit — writing it would compound across spawns.
        /// </remarks>
        public void ApplyEmitDamageScale(float scale)
        {
            if (scale <= 0f || Mathf.Approximately(scale, 1f))
            {
                return;
            }

            ScaleMeleeDamage(scale);
        }

        /// <summary>
        /// The unit the blade is aimed at — AS3 <c>cel = World.w.gg</c> (<c>:60</c>).
        /// </summary>
        /// <remarks>
        /// The boss passes its own blackboard target, which is the closer reading of <c>loc.gg</c>
        /// than a scene search. When it passes nothing (a bare test spawn) the blade falls back to
        /// <see cref="ResolvePlayer"/> on its first tick.
        /// </remarks>
        public void SetTarget(UnitController target)
        {
            _cel = target;
        }

        // ── the step (UnitScythe.as:117-138 control(), :95-115 run()) ─────────────────────────────

        /// <summary>
        /// <c>control()</c> then <c>run()</c>, in the oracle's order and in one method.
        /// </summary>
        /// <remarks>
        /// <para><b>Why both halves are here.</b> AS3 runs <c>control()</c> and then <c>run()</c> as
        /// two calls in the same frame, and <c>run()</c> is where the position write lives. The port's
        /// <see cref="UnitController.Move"/> is the position write and it is gated off for this
        /// subclass (see <see cref="IsFixed"/>), so the write has to happen in the one hook both
        /// drivers call — <see cref="UnitController.TickControl"/>. Splitting them across two methods
        /// would put the increment and the position in different frames.</para>
        ///
        /// <para><b><c>sost &gt;= 3</c> returns before the increment</b> (<c>:119-122</c>), so a corpse
        /// neither orbits nor arms. The port's <c>IsAlive</c> is the same test.</para>
        /// </remarks>
        public override void TickControl()
        {
            if (!IsAlive)
            {
                return;
            }

            // `++this.t` (:123) — BEFORE the run, so the first orbit frame uses t = 1.
            _t++;

            TickVisualSpin();

            if (_bind != null)
            {
                // `if(bind && (bind as Unit).sost >= 3) die();` (:130-133).
                if (ScytheRules.HostIsDead(hasHost: true, hostAlive: _bind.IsAlive))
                {
                    KillSelf();
                    return;
                }

                Vector2 centre = HostCentreRoomLocalPixels();
                BossAlicornRules.Vector2Pixels target =
                    ScytheRules.OrbitPixels(centre.x, centre.y, _bindN, _t);

                // `X = …; Y = …;` (:99-100) — an absolute write, not an integration.
                SetRoomLocalPixels(target.X, target.Y);
                SetVelocityPixelsPerFrame(Vector2.zero);
            }
            else
            {
                TickFreeFlight();
                if (!IsAlive)
                {
                    return;
                }
            }

            // `if(this.t > 60) attKorp(cel);` (:134-137).
            if (ScytheRules.CanDamage(_t) && _cel != null)
            {
                TryContactAttack(_cel);
            }
        }

        /// <summary>
        /// The unbound half of <c>control()</c> + <c>run()</c>: latch the angle once, accelerate,
        /// integrate, and die at the room edge.
        /// </summary>
        void TickFreeFlight()
        {
            EnsureAngleLatched();

            // No target yet: AS3 cannot reach this (loc.gg always exists), so the honest behaviour is
            // to hover in place rather than fly at an angle of -1 rad.
            if (_napr == ScytheRules.NoAngle)
            {
                SetVelocityPixelsPerFrame(Vector2.zero);
                return;
            }

            // `this.skor *= 1.05; dx = sin(napr) * skor; dy = cos(napr) * skor;` (:126-128).
            _skor = ScytheRules.AdvanceSpeed(_skor);
            BossAlicornRules.Vector2Pixels velocity = ScytheRules.VelocityFromAngle(_napr, _skor);

            // `X += dx / param1; Y += dy / param1;` (:104-105). `param1` is the frame divisor, which
            // is 1 at the canonical 30 Hz; a 60 Hz build takes two half-steps and must not travel
            // twice as far, hence the scale rather than the raw delta.
            float stepScale = StepSeconds * PFE.Core.SimClock.CanonicalTicksPerSecond;
            Vector2 position = RoomLocalPixels + new Vector2(velocity.X, velocity.Y) * stepScale;
            SetRoomLocalPixels(position.x, position.y);
            SetVelocityPixelsPerFrame(new Vector2(velocity.X, velocity.Y));

            // `if(X >= loc.limX || X <= 0 || Y >= loc.limY || Y <= 0) die();` (:106-109).
            if (ScytheRules.OutOfBounds(position.x, position.y, RoomLimitXPixels, RoomLimitYPixels))
            {
                KillSelf();
            }
        }

        /// <summary>
        /// <c>getNapr()</c> (<c>:82-93</c>) — latch the flight angle on the first tick that has a
        /// target, and never again.
        /// </summary>
        void EnsureAngleLatched()
        {
            if (_napr != ScytheRules.NoAngle)
            {
                return;
            }

            if (_cel == null)
            {
                _cel = ResolvePlayer();
            }

            if (_cel == null)
            {
                return;
            }

            Vector2 self = RoomLocalPixels;
            Vector2 target = RoomLocalPixelsOf(_cel.transform.position);

            // `Math.atan2(this.cel.X - X, this.cel.Y - Y)` — see ScytheRules.AngleToTarget for why the
            // arguments are the way round they are.
            _napr = ScytheRules.AngleToTarget(target.x - self.x, target.y - self.y);
        }

        // ── the visual (UnitScythe.as:64-80 animate()) ────────────────────────────────────────────

        /// <summary>
        /// <c>animate()</c>: the spin-up, the rotation and the two fades.
        /// </summary>
        /// <remarks>
        /// AS3 rotates <c>vis.osn</c> and mirrors it onto <c>vis.vzz</c> (the trail). The port's
        /// <c>scythe</c> art is a single 120×120 PNG, so there is one transform to rotate rather than
        /// two — the trail is part of the same frame. The two alpha ramps are still tracked
        /// separately, because the oracle's trail lags the blade by a visible amount and that is the
        /// effect being reproduced.
        /// </remarks>
        void TickVisualSpin()
        {
            _spin = ScytheRules.SpinStep(_spin);
            _bladeAlpha = ScytheRules.FadeStep(_bladeAlpha, ScytheRules.BladeFadeStep);
            _trailAlpha = ScytheRules.FadeStep(_trailAlpha, ScytheRules.TrailFadeStep);

            Transform visual = _visualRenderer != null ? _visualRenderer.transform : null;
            if (visual != null)
            {
                // AS3's `rotation` is clockwise-positive in a y-down space, so the port's y-up
                // counterpart is the negative angle. The sign only decides which way the blade
                // appears to spin; the oracle's own direction is clockwise on screen.
                visual.localRotation = Quaternion.Euler(0f, 0f, -_spin);
            }

            if (_visualRenderer != null)
            {
                Color colour = _visualRenderer.color;
                colour.a = _bladeAlpha;
                _visualRenderer.color = colour;
            }
        }

        // ── death ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>die()</c> (<c>:106-109</c>, <c>:130-133</c>) — AS3 has no <c>die()</c> override on
        /// <c>UnitScythe</c>, so both calls reach <c>Unit.die()</c> and take the <c>trup='0'</c> arm:
        /// <c>expl(); exterminate();</c>. The port reaches that through
        /// <see cref="UnitController.OnDeath"/> → <c>EnterCorpse</c>, which is the same branch table.
        /// </summary>
        /// <remarks>
        /// The kill goes through <see cref="UnitController.TakeDamage"/> rather than a bespoke
        /// <c>Destroy</c> so the corpse pipeline runs: a blade has <c>leavesCorpse = 0</c>, so it
        /// bursts and is removed on the same frame instead of lingering as a body.
        /// </remarks>
        void KillSelf()
        {
            if (!IsAlive)
            {
                return;
            }

            TakeDamage(CurrentHealth + 1f);
        }

        // ── geometry helpers ─────────────────────────────────────────────────────────────────────

        /// <summary>This unit's origin in <b>room-local, y-up</b> pixels.</summary>
        Vector2 RoomLocalPixels => RoomLocalPixelsOf(transform.position);

        /// <summary>
        /// A Unity world position → room-local, y-up pixels.
        /// </summary>
        /// <remarks>
        /// <b>Two conversions, and the origin is the one that is easy to forget.</b> The unit's
        /// <c>transform.position</c> is a world <i>unit</i> position, so it is scaled to world
        /// <i>pixels</i> and then the room's own origin is subtracted —
        /// <c>ITileQueryService.OriginPixel</c>, which is zero only in the room at land (0,0). This is
        /// the inverse of <c>RoomVisualController.RoomPixelToWorld</c>, written here rather than
        /// called because a controller holds a room, not a visual controller.
        /// </remarks>
        Vector2 RoomLocalPixelsOf(Vector3 worldPosition)
        {
            Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;
            return (Vector2)worldPosition * TileQueryConstants.UnitToPixel - origin;
        }

        /// <summary>The inverse of <see cref="RoomLocalPixelsOf"/> — write a room-local pixel position.</summary>
        void SetRoomLocalPixels(float x, float y)
        {
            Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;
            Vector3 worldPixels = new Vector3(x + origin.x, y + origin.y, 0f);
            transform.position = worldPixels * TileQueryConstants.PixelToUnit
                                 + new Vector3(0f, 0f, transform.position.z);
        }

        /// <summary>
        /// The host's <b>vertical centre</b> in room-local pixels — AS3's <c>bind.Y - bind.scY / 2</c>.
        /// </summary>
        /// <remarks>
        /// AS3 stores a unit's <c>Y</c> at its feet and measures <i>down</i>, so subtracting half a
        /// body moves up. The port stores the feet at the bottom and measures up, so the same point is
        /// <c>feetY + scY / 2</c>. This is the single sign in the orbit that the Y mirror changes, and
        /// <see cref="ScytheRules.OrbitPixels"/> documents its half.
        /// </remarks>
        Vector2 HostCentreRoomLocalPixels()
        {
            Vector2 feet = RoomLocalPixelsOf(_bind.transform.position);
            return new Vector2(feet.x, feet.y + _bind.SpriteSizePixels.y * 0.5f);
        }

        /// <summary>AS3 <c>loc.limX</c> — the room's width in pixels.</summary>
        float RoomLimitXPixels =>
            Room != null ? Room.width * WorldConstants.TILE_SIZE : ScytheRules.BindRadius * 10f;

        /// <summary>AS3 <c>loc.limY</c> — the room's height in pixels.</summary>
        float RoomLimitYPixels =>
            Room != null ? Room.height * WorldConstants.TILE_SIZE : ScytheRules.BindRadius * 5f;

        /// <summary>
        /// <c>World.w.gg</c> — the player. Resolved once, on the first tick that needs it.
        /// </summary>
        /// <remarks>
        /// AS3 reaches the player through a global; the port has no such handle, so this is the same
        /// substitution <c>BossAlicornBrain.Aim</c> documents. Unlike the boss's aim, a blade never
        /// re-resolves: it latches its angle once and flies, so a stale reference can only cost the
        /// angle, not a re-aim.
        /// </remarks>
        static UnitController ResolvePlayer()
        {
            return UnityEngine.Object.FindFirstObjectByType<PlayerController>();
        }
    }
}
