using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Pets;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// The companion's follower AI — a transcription of <c>UnitPet.control()</c>
    /// (<c>fe/unit/UnitPet.as:575-824</c>).
    ///
    /// <para><b>Two ladders, one field, and neither is <see cref="EnemyAIState"/>.</b> The oracle's
    /// <c>aiState</c> is 0 sit, 1 fly to the point, 2 attack, 3 go to a commanded point, 4 ram,
    /// 5 recoil. It is kept in <see cref="_aiState"/> exactly as written, because every threshold in
    /// the movement block branches on it and mapping it onto the port's five-state machine would put a
    /// translation between the oracle and the arithmetic. <c>_currentState</c> is still written, once
    /// per tick, so the F3 panel and the damage rules have something to read.</para>
    ///
    /// <para><b>Which half of the base class is used.</b> Perception is <b>not</b> inherited:
    /// <see cref="EnemySensors.Evaluate"/> is a sight/hearing model whose ranges and suspicion meter are
    /// a different rule from the pet's <c>findCel</c> (<c>:375-411</c>), which is a plain
    /// "nearest thing I can actually hurt within 800 px with a clear line". What <i>is</i> inherited is
    /// the candidate pool (<see cref="EnemyBrain.RefreshTargetCandidates"/>, which applies the faction
    /// and invulnerable-player filters) and the weapon half (<c>TickWeapons</c>), because those are the
    /// same questions for every unit in the game.</para>
    ///
    /// <para><b>Coordinates.</b> The fly point, the clamps and the grid probes are AS3 room-local pixels
    /// with <b>Y growing down</b> — <c>flyY = player.Y - 80</c> is 80 px <i>above</i> the player — so all
    /// of that arithmetic is done in AS3 space through
    /// <see cref="WorldCoordinates.UnityWorldToAs3RoomLocal"/> and only the finished velocity is
    /// mirrored into the port's up-positive frame. Mixing the two spaces inside the ladder is the exact
    /// mistake that puts a companion under the floor.</para>
    /// </summary>
    public class PetBrain : EnemyBrain
    {
        private PetKind _kind = PetKind.Phoenix;

        /// <summary>AS3 <c>aiState</c> — see the class remarks for what each value means.</summary>
        private int _aiState = 1;

        /// <summary>AS3 <c>aiTCh</c> — the retarget cadence. Decremented once per tick.</summary>
        private int _aiTCh;

        /// <summary>
        /// AS3 <c>oduplenie</c> — the 60-tick summon grace, during which the pet will not acquire a
        /// target (<c>:745</c>) and fades in (<c>:245-256</c>).
        /// </summary>
        private int _oduplenie;

        /// <summary>AS3 <c>flyX</c>/<c>flyY</c> — where it is trying to be, in AS3 room pixels.</summary>
        private float _flyX;
        private float _flyY;

        /// <summary>AS3 <c>celX</c>/<c>celY</c> — the aim point, in AS3 room pixels.</summary>
        private float _celX;
        private float _celY;

        /// <summary>AS3 <c>mater</c> — whether the pet collides with the world this tick.</summary>
        private bool _mater;

        /// <summary>AS3 <c>tempUnmat</c> — cleared by the player's "come here" command.</summary>
        private bool _tempUnmat;

        /// <summary>AS3 <c>flyBox</c> — the perch this pet is sitting on, if any.</summary>
        private bool _hasPerch;

        /// <summary>
        /// The room's tile boxes for <see cref="UnitCollisionAllMath"/>, rebuilt only when the pet
        /// changes room. Building it per call would allocate a grid on every tenth tick.
        /// </summary>
        private RoomCollisionTileGrid _collisionGrid;

        private UnitController _target;

        // The three speeds. `maxSpeed` is `walkSpeed` when idle and `runSpeed` otherwise (`:816-823`).
        private float _walkSpeed = 5f;
        private float _runSpeed = 10f;
        private float _accel = 1.5f;
        private float _brake = 0.3f;

        /// <summary>AS3 <c>storona</c> — the pet's facing sign.</summary>
        private int _storona = 1;

        /// <summary>AS3 <c>rasstWeap</c> — 200 for the phoenix, 400 for the other two.</summary>
        private float WeaponRange => PetRules.WeaponRange(_kind);

        private PetController Pet => _controller as PetController;

        /// <summary>The player the pet belongs to, or null before the companion hands it over.</summary>
        private PlayerController Owner => Pet != null ? Pet.Owner : null;

        /// <summary>AS3 <c>aiState</c>, exposed for the F3 panel and for fixtures.</summary>
        public int FamilyState => _aiState;

        /// <summary>AS3 <c>oduplenie</c>, exposed for the F3 panel.</summary>
        public int SummonGrace => _oduplenie;

        /// <summary>
        /// AS3 <c>isFly = true</c> (<c>UnitPet.as:75</c>). Answered by the controller too; the two agree
        /// because there is one flag, not two.
        /// </summary>
        public bool IsFlying => true;

        protected override bool StopsToAttack => false;

        // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ReadDefinitionTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ReadDefinitionTuning();
        }

        /// <summary>
        /// The three speeds and the brake, read once the definition exists.
        ///
        /// <para><b><c>runSpeed</c> is not the row's <c>run</c>.</b> <c>UnitPet.as:74</c> assigns
        /// <c>runSpeed = maxSpeed * 2</c> <i>after</i> the XML has been read, so the phoenix's authored
        /// <c>run='15'</c> is overwritten with 10. <see cref="PetRules.RunSpeed"/> is that line; using
        /// <c>definition.runSpeed</c> here would make the pet 50 % faster than the oracle's.</para>
        /// </summary>
        void ReadDefinitionTuning()
        {
            UnitDefinition definition = _controller != null ? _controller.Definition : null;
            if (definition == null) return;

            if (PetIds.TryParse(definition.id, out PetKind kind))
            {
                _kind = kind;
            }

            _walkSpeed = definition.moveSpeed > 0f ? definition.moveSpeed : 5f;
            _runSpeed = PetRules.RunSpeed(_walkSpeed);
            _accel = definition.Acceleration > 0f ? definition.Acceleration : 1.5f;
            _brake = UnitFlyPhysics.ResolveBrake(definition.braking);
        }

        /// <summary>Called by <see cref="PetController.Call"/> — the body has just been shown.</summary>
        public void OnSummoned()
        {
            _aiState = 1;
            _oduplenie = PetRules.SummonGraceTicks;
            _target = null;
            _aiTCh = 0;
            _mater = false;

            SnapFlyPointToSelf();
        }

        /// <summary>Called by <see cref="PetController.Recall"/>.</summary>
        public void OnRecalled()
        {
            _target = null;
            _aiState = 1;
            _aiTCh = 0;
            _hasPerch = false;
            StopMovement();
        }

        // ── The tick ───────────────────────────────────────────────────────────────────────────────

        public override void SimTick(int tickIndex)
        {
            if (_controller == null) return;

            // A pet that is dead or dismissed has no frame: `UnitPet.step()` still runs but `control()`
            // returns on `sost >= 3 || !active` (`:577-580`).
            if (!Pet.IsSummoned || !Pet.IsAlive)
            {
                return;
            }

            if (_oduplenie > 0)
            {
                _oduplenie--;
                Pet.ApplySummonAlpha(_oduplenie);
            }

            PetControl(tickIndex);

            UpdateAnimation();
            TickWeapons();
        }

        /// <summary>
        /// The ladder, arm for arm — <c>UnitPet.as:575-824</c>.
        /// </summary>
        void PetControl(int tickIndex)
        {
            PlayerController owner = Owner;
            if (owner == null)
            {
                StopMovement();
                return;
            }

            if (!TryRoomContext(out RoomInstance room, out float roomHeightPixels))
            {
                return;
            }

            Vector2 self = As3Position(transform.position, room);
            Vector2 player = As3Position(owner.transform.position, room);
            float rasst2 = (player - self).sqrMagnitude;

            // ── mater (`:586-599`) ────────────────────────────────────────────────────────────────
            // Inside `rasstUnmat` of the player, or holding a target, the pet collides again; the test
            // is re-run every tenth tick and only when it is not already colliding.
            if (rasst2 < PetRules.MaterialDistance * PetRules.MaterialDistance || _target != null)
            {
                if (!_mater && _aiTCh % 10 == 1 && !CollisionAll())
                {
                    _mater = true;
                }
            }
            else
            {
                _mater = false;
            }

            if (_tempUnmat) _mater = false;

            // ── the retarget timer (`:601-658`) ───────────────────────────────────────────────────
            if (_aiTCh > 0)
            {
                _aiTCh--;
            }
            else
            {
                _tempUnmat = false;

                if (_aiState == 0 || _aiState == 1)
                {
                    if (rasst2 > PetRules.LeashDistance * PetRules.LeashDistance || _controller.IsInWater)
                    {
                        _aiState = 1;
                        ComputeFlyPoint(self, player, room, roomHeightPixels);

                        if (PetRules.TurnsToTarget(_kind))
                        {
                            ApplyFacing(_flyX > self.x ? 1 : -1);
                        }
                    }
                    else if (PetRules.TurnsToTarget(_kind))
                    {
                        ApplyFacing(_flyX > player.x ? -1 : 1);
                    }
                }
                else if (_aiState == 2)
                {
                    // `if(rasst2 > rasstGG² * 9) getFlyPoint();` — three leashes away and it gives up
                    // the chase for a moment.
                    if (rasst2 > PetRules.LeashDistance * PetRules.LeashDistance * 9f)
                    {
                        ComputeFlyPoint(self, player, room, roomHeightPixels);
                    }

                    // `if(celUnit == null) setCel(null, celX + rand*300-150, celY + rand*120-60);`
                    if (_target == null)
                    {
                        _celX += UnityEngine.Random.value * 300f - 150f;
                        _celY += UnityEngine.Random.value * 120f - 60f;
                    }
                }
                else if (_aiState == 4 || _aiState == 5)
                {
                    _aiState = 2;
                }
                else if (_aiState == 3)
                {
                    // `if(World.w.t_battle <= 0) aiState = 1; else aiState = 2;` — the port has no
                    // global battle timer, so a commanded move always relaxes back to following. That is
                    // the oracle's answer in every room where no fight is running.
                    _aiState = 1;
                }

                float playerDx = owner.VelocityPixelsPerFrame.x;
                _aiTCh = PetRules.NextRetargetTicks(playerDx, UnityEngine.Random.value);
            }

            // ── the flight step (`:660-740`) ──────────────────────────────────────────────────────
            float flyDx = _flyX - self.x;
            float flyDy = _flyY - self.y;
            float flyDist = Mathf.Sqrt(flyDx * flyDx + flyDy * flyDy);

            Vector2 thrust = Vector2.zero;
            float damp = 0f;

            if (_aiState == 0)
            {
                // Sitting on a perch: no thrust and no momentum. `if(flyBox && !flyBox.stay) …` is the
                // prop-disappeared arm; a perch that stopped being a perch sends it back to flying.
                thrust = Vector2.zero;
                damp = 0f;
                StopMovement();

                if (_hasPerch && !PerchStillValid(room))
                {
                    _aiState = 1;
                    _hasPerch = false;
                }
            }
            else if (_aiState == 2)
            {
                if (PetRules.TurnsToTarget(_kind))
                {
                    ApplyFacing(_celX > self.x ? 1 : -1);
                }

                if (PetRules.ShouldCloseIn(flyDist, WeaponRange, _mater))
                {
                    thrust = Normalise(flyDx, flyDy,
                        PetRules.ApproachSpeed(_accel, flyDist));
                }
                else if (PetRules.ShouldBackOff(flyDist))
                {
                    // `spd.x = -flyDX; spd.y = -flyDY/2 - 3; norma(spd, accel);` — the -3 is a small
                    // upward bias in AS3's down-positive space, i.e. it rises as it backs off.
                    thrust = Normalise(-flyDx, -flyDy * 0.5f - 3f, _accel);
                }
                else
                {
                    damp = PetRules.ApproachDamp;
                }
            }
            else if (_aiState == 5)
            {
                // Recoil after a ram: thrust decays and the velocity decays faster.
                thrust *= PetRules.RecoilDamp;
                damp = PetRules.RecoilVelocityDamp;
            }
            else if (_aiState != 4)
            {
                // States 1 and 3 — fly to the point.
                if (PetRules.ShouldSettle(flyDist, _hasPerch, _mater))
                {
                    _aiState = 0;
                    StopMovement();
                }
                else if (PetRules.ShouldCoast(flyDist, _mater))
                {
                    damp = PetRules.CoastDamp;
                }
                else
                {
                    thrust = Normalise(flyDx, flyDy, PetRules.ApproachSpeed(_accel, flyDist));
                }
            }

            // ── target acquisition (`:745-771`) ───────────────────────────────────────────────────
            if (PetRules.ShouldAcquireTarget(_aiState, _aiTCh, _oduplenie))
            {
                if (FindTarget(self, room, roomHeightPixels))
                {
                    _aiState = 2;
                }
            }

            if (PetRules.ShouldRetarget(_aiState, _aiTCh))
            {
                if (!(_target != null && _target.IsAlive && VisibleTarget(_target)))
                {
                    if (!FindTarget(self, room, roomHeightPixels))
                    {
                        _aiState = 1;
                    }
                }
            }

            // ── the ram (`:779-804`) ──────────────────────────────────────────────────────────────
            if (_aiState == 2 && flyDist < WeaponRange && !Pet.HasWeapon && _target != null)
            {
                Vector2 targetAs3 = As3Position(_target.transform.position, room);
                _aiState = 4;
                _aiTCh = 15;
                thrust = Normalise(targetAs3.x - self.x, targetAs3.y - self.y, _runSpeed);
                damp = 0f;
            }

            if (_aiState == 4)
            {
                if (_target == null)
                {
                    _aiState = 2;
                }
                else if (RamContact(_target))
                {
                    thrust *= PetRules.RamRecoilDamp;
                    _aiState = 5;
                    _aiTCh = 15;

                    // `damage(dam * 0.02, D_INSIDE)` — the pet hurts itself to ram.
                    _controller.TakeDamage(PetRules.RamSelfDamage(Pet.PetDamage));
                }
            }

            float cap = PetRules.StateSpeedCap(_aiState, _walkSpeed, _runSpeed);
            ApplyThrust(thrust, damp, cap);

            // ── aiming and firing (`:825-833`) ────────────────────────────────────────────────────
            UpdateWeaponFire(room, self);

            Pet.UpdateWeaponHoldPoint();
            _currentState = MapToPortState(_aiState);
        }

        /// <summary>
        /// <c>UnitPet.as:825-833</c> — the aim point and the trigger. Split out of the ladder so
        /// <see cref="ExecuteAttackAction"/> can share it instead of growing a second, divergent rule.
        /// </summary>
        void UpdateWeaponFire(RoomInstance room, Vector2 self)
        {
            if (_aiState == 2 && _target != null && !_controller.IsInWater)
            {
                Vector2 targetAs3 = As3Position(_target.transform.position, room);
                _celX = targetAs3.x;
                _celY = targetAs3.y - BodyHalfHeightPixels(_target);
                _flyX = targetAs3.x;
                _flyY = targetAs3.y - 80f;

                float dist = Mathf.Sqrt(
                    (_celX - self.x) * (_celX - self.x) + (_celY - self.y) * (_celY - self.y));

                EnemyWeaponMount mount = Pet.PrimaryWeaponMount();
                if (dist <= WeaponRange && mount != null)
                {
                    // `currentWeapon.attack()` — aim at the target's centre and let the weapon's own
                    // reload gate the rate, exactly as the oracle does.
                    //
                    // CENTRE, not `GetUnitAimWorld`. `UnitPet.setCel` is the one override that drops
                    // the quarter-width lead (`UnitPet.as:815-816`: `celX = celUnit.X`,
                    // `celY = celUnit.Y - celUnit.scY / 2`), so a pet aims at the middle of the body
                    // and nothing else. `_celX`/`_celY` three lines up already compute that same point;
                    // the mount used to be handed `transform.position` — her FEET — which contradicted
                    // the line directly above it.
                    mount.SetAimTarget(
                        EnemySensors.GetUnitCenterPixels(_target) * TileQueryConstants.PixelToUnit);
                    mount.SetFiring(true);
                }
                else if (mount != null)
                {
                    mount.SetFiring(false);
                }
            }
            else
            {
                Pet.PrimaryWeaponMount()?.SetFiring(false);
            }
        }

        // ── The abstract attack contract ───────────────────────────────────────────────────────────
        //
        // `StopsToAttack` is false, so `TryEnterAttackState` never enters the port's Attack state and
        // never calls this. Kept as the abstract contract's implementation and pointed at the very
        // method the ladder uses, so a future change to `StopsToAttack` cannot silently produce a
        // second attack rule — the same shape as `FlyerBrain`.

        protected override void ExecuteAttackAction()
        {
            if (!TryRoomContext(out RoomInstance room, out _)) return;

            UpdateWeaponFire(room, As3Position(transform.position, room));
        }

        /// <summary>AS3 <c>aiState</c> mapped for reporting only — the movement never reads it back.</summary>
        EnemyAIState MapToPortState(int aiState)
        {
            switch (aiState)
            {
                case 0: return EnemyAIState.Idle;
                case 1:
                case 3: return EnemyAIState.Patrol;
                default: return EnemyAIState.CombatChase;
            }
        }

        // ── Movement ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the oracle's <c>dx += spd</c>, then <c>forces()</c>'s own cap.
        ///
        /// <para><b>Order is <c>forces()</c> → <c>control()</c>.</b> AS3 damps the velocity the
        /// previous frame left behind and <i>then</i> adds this frame's thrust, so a thrust smaller than
        /// the brake survives instead of being zeroed. Same shape as
        /// <c>FlyerBrain.Fly</c>, and the same reason it assigns as well as commands: the motor ramps
        /// toward a commanded speed, while the oracle's thrust is instantaneous.</para>
        /// </summary>
        /// <param name="thrust">AS3-space thrust, Y down.</param>
        /// <param name="damp">The arm's own multiplier on the live velocity, or 0 for "no damping".</param>
        /// <param name="cap">AS3 <c>maxSpeed</c> for this state.</param>
        void ApplyThrust(Vector2 thrust, float damp, float cap)
        {
            Vector2 velocity = _controller.VelocityPixelsPerFrame;

            if (damp > 0f)
            {
                velocity *= damp;
            }

            // `forces()`: `if(dx*dx + dy*dy > maxSpeed*maxSpeed) { dx *= 0.8; dy *= 0.8; }`.
            velocity += new Vector2(thrust.x, -thrust.y);
            if (velocity.sqrMagnitude > cap * cap)
            {
                velocity *= PetRules.OverSpeedDamp;
            }

            _controller.SetFlySpeedCapPixelsPerFrame(cap);
            _controller.SetVelocityPixelsPerFrame(velocity);
            MoveHorizontal(velocity.x);
            SetVerticalSpeed(velocity.y);
        }

        /// <summary><c>norma(spd, len)</c> — a vector of the given length, or zero when it has none.</summary>
        static Vector2 Normalise(float x, float y, float length)
        {
            float magnitude = Mathf.Sqrt(x * x + y * y);
            if (magnitude <= 0.0001f) return Vector2.zero;

            return new Vector2(x / magnitude * length, y / magnitude * length);
        }

        // ── The fly point (UnitPet.as:303-368) ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>getFlyPoint()</c>: the point beside and above the player, clamped, optionally snapped to
        /// a perch, and otherwise snapped to a free tile.
        /// </summary>
        void ComputeFlyPoint(Vector2 self, Vector2 player, RoomInstance room, float roomHeightPixels)
        {
            float limitX = room.width * WorldConstants.TILE_SIZE;
            float limitY = room.height * WorldConstants.TILE_SIZE;

            PetPoint point = PetRules.FollowPoint(player.x, player.y, _storona, limitX, limitY);
            float x = point.X;
            float y = point.Y;

            _hasPerch = false;
            float perchX = 0f;
            float perchY = 0f;
            if (PetRules.CanPerch(_kind))
            {
                _hasPerch = TryFindPerch(room, roomHeightPixels, x, y, out perchX, out perchY);
            }

            if (_hasPerch)
            {
                x = perchX;
                y = perchY;
            }
            else if (SolidAt(room, x, y))
            {
                // `x = Math.floor(x/40)*40+20; y = Math.floor(y/40)*40+39;` then four probes.
                PetPoint snapped = PetRules.SnapToTileGrid(x, y);
                PetPoint? free = PetRules.ProbeGridFallback(
                    snapped.X, snapped.Y, _storona,
                    (px, py) => SolidAt(room, px, py));

                if (free.HasValue)
                {
                    x = free.Value.X;
                    y = free.Value.Y;
                }
                else
                {
                    // `this.flyX = gg.X; this.flyY = gg.Y - 1;` — the player's own feet.
                    x = player.x;
                    y = player.y - 1f;
                }
            }

            _flyX = x;
            _flyY = y;
        }

        void SnapFlyPointToSelf()
        {
            if (!TryRoomContext(out RoomInstance room, out _)) return;

            Vector2 self = As3Position(transform.position, room);
            _flyX = self.x;
            _flyY = self.y;
        }

        /// <summary>
        /// <c>loc.isSolid(x, y)</c> at an AS3 room-local point: the column is <c>x / 40</c>, the row is
        /// the mirror of <c>y / 40</c> (<see cref="WorldCoordinates.As3YToUnityRow"/>).
        /// </summary>
        bool SolidAt(RoomInstance room, float as3X, float as3Y)
        {
            if (_tileQuery == null || room == null) return false;

            int column = Mathf.FloorToInt(as3X / WorldConstants.TILE_SIZE);
            int row = WorldCoordinates.As3YToUnityRow(as3Y, room.height);

            if (column < 0 || column >= room.width || row < 0 || row >= room.height) return true;

            return _tileQuery.IsSolidAt(new Vector2Int(column, row));
        }

        /// <summary>
        /// <c>collisionAll()</c> (<c>Unit.as:2541-2566</c>) — does the pet's own box overlap a blocking
        /// tile right now. Read by the <c>mater</c> arm (<c>UnitPet.as:588</c>): the pet only starts
        /// colliding once it is already overlapping something.
        ///
        /// <para><b>This used to be an approximation of the oracle, not the oracle.</b> It asked
        /// <see cref="PFE.Systems.Map.TileQuery.ITileQueryService.IsSolidAt"/> per <i>cell</i>, which is
        /// a different question from <c>collisionTile</c>'s AABB against <c>phX1..phY2</c> and got three
        /// things wrong: a raised (<c>heightLevel &gt; 0</c>) tile was treated as solid over its whole
        /// cell including the empty top; the body box was centred on the feet rather than standing on
        /// them; and <c>loc.sky</c> was ignored. <see cref="UnitCollisionAllMath"/> is the rule, and
        /// <see cref="RoomCollisionTileGrid"/> supplies the physics boxes.</para>
        ///
        /// <para><b>The body is <c>transT</c> and <c>isFly</c>, both from the constructor</b>
        /// (<c>UnitPet.as:75, :96-107</c>): a ghost wall is not solid to a pet, and a shelf never is —
        /// <c>isFly</c> alone satisfies the last disjunct of <c>:2578</c>.</para>
        /// </summary>
        bool CollisionAll()
        {
            if (!TryRoomContext(out RoomInstance room, out _)) return false;

            UnitDefinition definition = _controller != null ? _controller.Definition : null;

            // `scX`/`scY` are the unit's own collision size; `UnitDefinition.Width` is in units where
            // 1 = 100 px (see its tooltip), so the half-width is Width * 50 and the height Width * 100.
            float halfWidth = definition != null && definition.Width > 0f ? definition.Width * 50f : 25f;
            float height = definition != null && definition.Height > 0f ? definition.Height * 100f : 50f;

            Vector2 self = As3Position(transform.position, room);

            CollisionBody body = CollisionBody.FromFeet(
                feetX: self.x,
                feetY: self.y,
                halfWidth: halfWidth,
                height: height,
                transparentToGhostWalls: true,
                ignoresShelves: true,
                stays: false);

            if (_collisionGrid == null || !ReferenceEquals(_collisionGrid.Room, room))
            {
                _collisionGrid = new RoomCollisionTileGrid(room);
            }

            return UnitCollisionAllMath.Any(body, _collisionGrid);
        }

        /// <summary>
        /// The perch test — <c>UnitPet.as:327-339</c>. The oracle takes the <b>first</b> prop that
        /// passes, not the best one, so this stops at the first hit too.
        /// </summary>
        bool TryFindPerch(RoomInstance room, float roomHeightPixels, float flyX, float flyY,
            out float perchX, out float perchY)
        {
            perchX = 0f;
            perchY = 0f;

            if (room.objects == null) return false;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null || obj.definition == null || !obj.isActive) continue;

                MapObjectDefinition definition = obj.definition;

                // `stay` is a legacy attribute (`<obj stay='1'/>`), not an imported field — see
                // MapObjectDefinition.GetAttribute. A wall is `blocksMovement`: AS3's `obj.wall`.
                bool stays = !string.IsNullOrEmpty(definition.GetAttribute("stay"));
                if (!PetRules.IsPerchCandidate(
                        isWall: definition.blocksMovement,
                        stays: stays,
                        invisible: !obj.isActive,
                        x1: obj.position.x - definition.width * WorldConstants.TILE_SIZE * 0.5f,
                        x2: obj.position.x + definition.width * WorldConstants.TILE_SIZE * 0.5f,
                        topY: roomHeightPixels - obj.position.y,
                        flyX: flyX,
                        flyY: flyY))
                {
                    continue;
                }

                perchX = obj.position.x;
                perchY = roomHeightPixels - obj.position.y;
                return true;
            }

            return false;
        }

        /// <summary>Whether the perch this pet sat down on is still a valid one.</summary>
        bool PerchStillValid(RoomInstance room)
        {
            return TryFindPerch(room, room.height * WorldConstants.TILE_SIZE, _flyX, _flyY,
                out float x, out float y)
                   && Mathf.Abs(x - _flyX) < 1f
                   && Mathf.Abs(y - _flyY) < 1f;
        }

        // ── Targeting (UnitPet.as:375-411) ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>findCel()</c> — the nearest unit this pet can actually hurt, within
        /// <see cref="PetRules.VisionDistance"/>, with a clear line.
        ///
        /// <para><b>Reuses the base class's candidate pool.</b> <see cref="EnemyBrain.RefreshTargetCandidates"/>
        /// already applies the two filters the oracle also applies (different faction, not the
        /// invulnerable player), so this only adds the pet's own skip list. Re-implementing the pool
        /// would be a second copy of a rule that has already been wrong once.</para>
        /// </summary>
        bool FindTarget(Vector2 self, RoomInstance room, float roomHeightPixels)
        {
            RefreshTargetCandidates();

            UnitController best = null;
            float bestDistance = float.MaxValue;

            EnemyWeaponMount mount = Pet.PrimaryWeaponMount();
            float weaponDamage = mount != null && mount.Definition != null ? mount.Definition.baseDamage : 0f;
            float piercing = mount != null && mount.Definition != null ? mount.Definition.piercing : 0f;

            for (int i = 0; i < _targetCandidates.Count; i++)
            {
                UnitController candidate = _targetCandidates[i];
                if (candidate == null || !candidate.IsAlive) continue;
                if (candidate.IsInvulnerable) continue;          // `invulner`
                if (candidate.IsInWater) continue;               // `optEnW` — never a swimmer

                // `!(damage * vulner[tipDamage] < armour + skin + 1 - pier)` — a pet does not shoot
                // what it cannot hurt. A weaponless pet rams instead, and the +1 keeps a zero-damage
                // weapon against zero armour admissible.
                float vulnerability = 1f;
                float armour = 0f;
                float skin = 0f;
                if (candidate.UnitStats != null)
                {
                    skin = candidate.UnitStats.skinResistance;
                }

                if (mount != null && !PetRules.WeaponCanHurt(weaponDamage, vulnerability, armour, skin, piercing))
                {
                    continue;
                }

                Vector2 candidateAs3 = As3Position(candidate.transform.position, room);
                float dx = candidateAs3.x - self.x;
                float dy = candidateAs3.y - self.y;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                if (distance > PetRules.VisionDistance) continue;
                if (!VisibleTarget(candidate)) continue;
                if (distance >= bestDistance) continue;

                best = candidate;
                bestDistance = distance;
            }

            _target = best;
            if (_target != null)
            {
                Vector2 targetAs3 = As3Position(_target.transform.position, room);
                _celX = targetAs3.x;
                _celY = targetAs3.y - BodyHalfHeightPixels(_target);
                _flyX = targetAs3.x;
                _flyY = targetAs3.y - 80f;
                return true;
            }

            return false;
        }

        /// <summary>
        /// <c>loc.isLine(X, Y - 30, celUnit.X, celUnit.Y - celUnit.scY/2)</c> — the pet's own occlusion
        /// walk, through the shared tile query. Port world pixels, because that is the space
        /// <see cref="EnemySensors.CheckLineOfSight"/> resolves tiles in.
        /// </summary>
        bool VisibleTarget(UnitController candidate)
        {
            Vector2 eye = GetSightRayOriginPixels();
            Vector2 target = (Vector2)candidate.transform.position * 100f
                             + new Vector2(0f, BodyHalfHeightPixels(candidate));
            return _sensors.CheckLineOfSight(eye, target, _tileQuery, PetRules.VisionDistance);
        }

        /// <summary>AS3 <c>scY / 2</c> in pixels — the body's own half height.</summary>
        static float BodyHalfHeightPixels(UnitController unit)
        {
            UnitDefinition definition = unit != null ? unit.Definition : null;
            return definition != null && definition.Height > 0f ? definition.Height * 50f : 25f;
        }

        /// <summary>
        /// <c>attKorp(celUnit)</c> for the ram. The pet's own damage number is not the row's
        /// <c>&lt;comb damage&gt;</c>, so the contact is scaled to it rather than taken from
        /// <see cref="UnitController.MeleeDamage"/>.
        /// </summary>
        bool RamContact(UnitController target)
        {
            float melee = _controller.MeleeDamage;
            if (melee > 0.0001f)
            {
                return _controller.TryContactAttack(target, Pet.PetDamage / melee);
            }

            // A row with no contact damage at all (the moon's is 1, so this is a safety net): report the
            // hit directly rather than dividing by zero.
            if (_controller.DamageSystem == null) return false;

            var pending = PendingDamage.Contact(
                DamageContext.Contact(Pet.PetDamage, DamageType.PhysicalMelee),
                target,
                target.transform.position);

            _controller.DamageSystem.Report(pending);
            return true;
        }

        // ── Coordinates ────────────────────────────────────────────────────────────────────────────

        bool TryRoomContext(out RoomInstance room, out float roomHeightPixels)
        {
            room = _controller != null ? _controller.Room : null;
            roomHeightPixels = room != null ? room.height * WorldConstants.TILE_SIZE : 0f;
            return room != null && _tileQuery != null;
        }

        /// <summary>
        /// A Unity world position → <b>AS3 room-local pixels</b>. Two conversions in one call and both
        /// are load-bearing: subtract the room origin (a world pixel is not a room pixel in any room but
        /// the first) and mirror Y.
        /// </summary>
        Vector2 As3Position(Vector3 unityPosition, RoomInstance room)
        {
            Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;
            return WorldCoordinates.UnityWorldToAs3RoomLocal(unityPosition, origin, room.height);
        }

        // ── Animation ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>UnitPet.animate()</c> (<c>:265-300</c>): dead is <c>die</c>, a sitting pet is
        /// <c>stay</c>, everything else is <c>fly</c>, and a firing pet plays <c>attack</c>.
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return "die";
            }

            if (!Pet.IsSummoned)
            {
                return null;
            }

            if (_aiState == 0)
            {
                return "stay";
            }

            EnemyWeaponMount mount = Pet.PrimaryWeaponMount();
            if (mount != null && mount.IsFiring && _animator != null && _animator.HasState("attack"))
            {
                return "attack";
            }

            return _animator != null && _animator.HasState("fly") ? "fly" : "stay";
        }
    }
}
