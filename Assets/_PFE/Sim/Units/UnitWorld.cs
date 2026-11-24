using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Sim.State;
using PFE.Systems.Combat;

namespace PFE.Sim.Units
{
    /// <summary>
    /// The pure C# store for per-unit simulation state (thread J, Phase 2) — the home the
    /// architecture report's <c>UnitWorld</c> sketch promises: one dense slot array per domain
    /// block, keyed by <see cref="UnitHandle"/>, owned by nobody but the sim. The migrated domains
    /// are the stealth/noise/suspicion block (<see cref="UnitStealthData"/>), the corpse lifecycle
    /// (<see cref="UnitCorpseData"/>), the movement motor record (<see cref="SimMotorState"/>) and
    /// the replicated unit record (<see cref="SimUnitState"/>) — the pieces
    /// <c>UnitController</c> and <c>TilePhysicsController</c> used to hold as loose fields; further
    /// domains (effects) land beside them as their migrations arrive, not before.
    ///
    /// <para><b>Why a store, and why now.</b> Phase 2 step 1 batched the <i>ticking</i> (one drive
    /// lane per order bucket); this is the matching batching of the <i>state</i>. As soon as a
    /// second system needs the same numbers — the future <c>UnitStealthSystem</c> iterating only
    /// the live slots, the snapshot projector reading the array directly — the data has to leave
    /// the <c>MonoBehaviour</c>. Both consumers are now real: the controller's facade forwards
    /// here, the handle is the identity a batch system keys on, and the projector reads the dense
    /// slot array instead of walking live views every tick (Phase 3).</para>
    ///
    /// <para><b>Authority: the store is sim truth; the unit record is the replicated part of it.</b>
    /// The stealth and corpse blocks were <c>[Authoritative("…host-owned Sim truth (F-02)")]</c> on
    /// the controller and stay host-only — they join the wire only when their owning system is
    /// extracted. The motor record is <c>[ClientPredicted]</c> in the same pre-wire position: the
    /// projector still emits <c>Motors = Array.Empty&lt;SimMotorState&gt;()</c>, so nothing on the
    /// motor array approaches the wire. The unit record is the exception: it IS the payload the
    /// snapshot projector emits each tick, and its view-derived fields (position, avatar extras,
    /// room attribution) are stamped at <see cref="PFE.Core.SimTickOrder.ViewAligned"/> by the
    /// view sync (<c>UnitRecordSyncSystem</c>) — the wire read itself never touches a view.</para>
    ///
    /// <para><b>Who acquires which slot.</b> The stealth/corpse/unit-record triple is acquired by
    /// <c>UnitController</c> and the motor record by <c>TilePhysicsController</c> — separate
    /// components with separate drive lifecycles, so a motored unit normally occupies two slots:
    /// its controller names one (all three controller-owned blocks in lockstep) and its motor
    /// names the other. Every <see cref="Acquire"/> initialises <i>all four</i> of a slot's
    /// records to their defaults, so a tenant never reads the previous occupant's numbers in a
    /// domain it does not own.</para>
    ///
    /// <para><b>Threading: none.</b> Like <c>ParticleWorld</c>, the store is touched from the
    /// <c>SimLoop</c> thread only — no locks, no atomics. A handle obtained on one loop is not
    /// valid input to another world.</para>
    /// </summary>
    public sealed class UnitWorld
    {
        /// <summary>The slot count a fresh world starts with; doubles on demand.</summary>
        private const int InitialCapacity = 64;

        private UnitStealthData[] _slots;

        // The corpse-lifecycle record, parallel to _slots and keyed by the same handle: one unit's
        // stealth and corpse records always live in the same slot of both arrays, so a domain can
        // never point at another unit's numbers.
        private UnitCorpseData[] _corpseSlots;

        // The movement-motor record (SimMotorState), parallel to _slots. Unlike stealth/corpse this
        // domain is acquired by the TilePhysicsController, a separate component with its own drive
        // lifecycle, so a motored unit occupies one controller slot plus one motor slot — the two
        // arrays' tenants are different populations, never the same slot under two names.
        private SimMotorState[] _motorSlots;

        // The replicated unit record (SimUnitState) and its room attribution, parallel to _slots
        // and keyed by the same handle as stealth/corpse: one controller's three blocks always
        // live in the same slot. The room key is the FNV-1a of RoomInstance.id
        // (SimSnapshotProjectionRules.RoomKey) — a ulong, not a room reference, so the store stays
        // Unity-free; 0 means "in no room".
        private SimUnitState[] _unitSlots;
        private ulong[] _roomKeys;

        // Dense list of the live slot indices, maintained by Acquire/Release: the iteration
        // surface a batch consumer (the snapshot projector, a future unit-step system) sweeps
        // instead of walking the capacity looking for live slots. Order is unspecified —
        // consumers that need a deterministic order (the wire) sort what they collect.
        private int[] _liveSlots;

        private int[] _generations;

        // Reused-slot free list, used as a stack: Release pushes, Acquire pops. Kept as a plain
        // array (not Stack<int>) so the hot path never allocates.
        private int[] _freeList;
        private int _freeCount;

        private int _nextSlot;
        private int _liveCount;

        public UnitWorld() : this(InitialCapacity)
        {
        }

        public UnitWorld(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                    "A UnitWorld needs at least one slot.");
            }

            _slots = new UnitStealthData[capacity];
            _corpseSlots = new UnitCorpseData[capacity];
            _motorSlots = new SimMotorState[capacity];
            _unitSlots = new SimUnitState[capacity];
            _roomKeys = new ulong[capacity];
            _liveSlots = new int[capacity];
            _generations = new int[capacity];
            _freeList = new int[capacity];
        }

        /// <summary>Slots currently held by a live handle — the count a batch system iterates.</summary>
        public int LiveCount => _liveCount;

        /// <summary>Slot-array length; grows geometrically, never shrinks. Exposed for tests and
        /// diagnostics, not a gameplay input.</summary>
        public int Capacity => _slots.Length;

        /// <summary>
        /// Takes a slot, initialised to <see cref="UnitStealthData.Default"/>, and returns the
        /// handle that names it. Reuses released slots (with a bumped generation) before touching
        /// fresh capacity.
        /// </summary>
        public UnitHandle Acquire()
        {
            int slot;
            if (_freeCount > 0)
            {
                slot = _freeList[--_freeCount];
            }
            else
            {
                if (_nextSlot == _slots.Length)
                {
                    Grow();
                }

                slot = _nextSlot++;

                // Generation 0 means "never issued" (it is what makes a default handle invalid),
                // so a slot's first issue must hand out generation 1. Release's bump from there
                // is what keeps a reissued slot distinguishable from its previous life.
                if (_generations[slot] == 0)
                {
                    _generations[slot] = 1;
                }
            }

            _slots[slot] = UnitStealthData.Default;
            _corpseSlots[slot] = UnitCorpseData.Default;
            _motorSlots[slot] = SimMotorState.Default;
            _unitSlots[slot] = SimUnitState.Default;
            _roomKeys[slot] = 0UL;
            _liveSlots[_liveCount] = slot;
            _liveCount++;
            return new UnitHandle(slot, _generations[slot]);
        }

        /// <summary>
        /// Returns the slot a handle names. <paramref name="handle"/> is cleared to
        /// <see cref="UnitHandle.Invalid"/> as part of the release, so a second call with the same
        /// variable is a documented no-op rather than a stale-access throw.
        ///
        /// <para><b>Strict validation, deliberately.</b> A handle whose generation no longer matches
        /// — released here and reissued since — and a handle that never came out of <i>this</i>
        /// world both throw. A silently-swallowed stale access would read another unit's stealth
        /// numbers, which is a desync that presents as "the AI forgot the player", not an
        /// exception.</para>
        /// </summary>
        public void Release(ref UnitHandle handle)
        {
            if (!handle.IsValid)
            {
                return;
            }

            Validate(handle, nameof(Release));

            _generations[handle.Slot]++;
            _slots[handle.Slot] = default;
            _corpseSlots[handle.Slot] = default;
            _motorSlots[handle.Slot] = default;
            _unitSlots[handle.Slot] = default;
            _roomKeys[handle.Slot] = 0UL;

            // Dense-list removal by swap-remove: despawn is the rare path, so the one linear scan
            // for the slot's position is cheaper than maintaining a reverse index on every write.
            // The swap changes the list's order — fine, the order was never contractual.
            for (int i = 0; i < _liveCount; i++)
            {
                if (_liveSlots[i] == handle.Slot)
                {
                    _liveSlots[i] = _liveSlots[_liveCount - 1];
                    break;
                }
            }

            _freeList[_freeCount++] = handle.Slot;
            _liveCount--;
            handle = UnitHandle.Invalid;
        }

        /// <summary>
        /// The read/write view of the slot a handle names. Throws on stale or foreign handles —
        /// see <see cref="Release(ref UnitHandle)"/> for why that is fail-loud rather than
        /// tolerant. Callers on the hot tick path hold the ref for one mutation block; nothing
        /// stores the returned ref past that.
        /// </summary>
        public ref UnitStealthData Access(in UnitHandle handle)
        {
            Validate(handle, nameof(Access));
            return ref _slots[handle.Slot];
        }

        /// <summary>
        /// The non-throwing read of a slot — a copy, unlike <see cref="Access(in UnitHandle)"/>'s
        /// ref — for overlays, tests and diagnostics that would rather draw "stale" than crash.
        /// </summary>
        public bool TryGet(in UnitHandle handle, out UnitStealthData data)
        {
            if (handle.IsValid && handle.Slot < _nextSlot && _generations[handle.Slot] == handle.Generation)
            {
                data = _slots[handle.Slot];
                return true;
            }

            data = default;
            return false;
        }

        /// <summary>
        /// The read/write view of the slot's corpse record — the corpse-domain twin of
        /// <see cref="Access(in UnitHandle)"/>: same borrowed-ref contract, same strict validation
        /// (a stale or foreign handle throws, because a silently-swallowed access would read
        /// another unit's corpse state, which presents as "the body never dissolves", not an
        /// exception).
        /// </summary>
        public ref UnitCorpseData AccessCorpse(in UnitHandle handle)
        {
            Validate(handle, nameof(AccessCorpse));
            return ref _corpseSlots[handle.Slot];
        }

        /// <summary>The non-throwing read of a slot's corpse record — the twin of
        /// <see cref="TryGet(in UnitHandle, out UnitStealthData)"/>.</summary>
        public bool TryGetCorpse(in UnitHandle handle, out UnitCorpseData data)
        {
            if (handle.IsValid && handle.Slot < _nextSlot && _generations[handle.Slot] == handle.Generation)
            {
                data = _corpseSlots[handle.Slot];
                return true;
            }

            data = default;
            return false;
        }

        /// <summary>
        /// The read/write view of the slot's motor record — the motor-domain twin of
        /// <see cref="Access(in UnitHandle)"/>: same borrowed-ref contract, same strict validation
        /// (a stale or foreign handle throws, because a silently-swallowed access would read
        /// another motor's position and velocity, which presents as "the unit teleports", not an
        /// exception). The slot is acquired by the <c>TilePhysicsController</c>, not the unit
        /// controller — see the class doc's "who acquires which slot".
        /// </summary>
        public ref SimMotorState AccessMotor(in UnitHandle handle)
        {
            Validate(handle, nameof(AccessMotor));
            return ref _motorSlots[handle.Slot];
        }

        /// <summary>The non-throwing read of a slot's motor record — the twin of
        /// <see cref="TryGetCorpse(in UnitHandle, out UnitCorpseData)"/>.</summary>
        public bool TryGetMotor(in UnitHandle handle, out SimMotorState data)
        {
            if (handle.IsValid && handle.Slot < _nextSlot && _generations[handle.Slot] == handle.Generation)
            {
                data = _motorSlots[handle.Slot];
                return true;
            }

            data = default;
            return false;
        }

        /// <summary>
        /// The read/write view of the slot's unit record — the unit-domain twin of
        /// <see cref="Access(in UnitHandle)"/>: same borrowed-ref contract, same strict validation
        /// (a stale or foreign handle throws, because a silently-swallowed access would read
        /// another unit's replicated record, which presents as "the client renders the wrong
        /// body", not an exception). Unlike the motor, this domain shares the controller's handle
        /// with stealth and corpse: one controller's three blocks always live in the same slot.
        /// </summary>
        public ref SimUnitState AccessUnit(in UnitHandle handle)
        {
            Validate(handle, nameof(AccessUnit));
            return ref _unitSlots[handle.Slot];
        }

        /// <summary>The non-throwing read of a slot's unit record — the twin of
        /// <see cref="TryGetMotor(in UnitHandle, out SimMotorState)"/>.</summary>
        public bool TryGetUnit(in UnitHandle handle, out SimUnitState data)
        {
            if (handle.IsValid && handle.Slot < _nextSlot && _generations[handle.Slot] == handle.Generation)
            {
                data = _unitSlots[handle.Slot];
                return true;
            }

            data = default;
            return false;
        }

        /// <summary>
        /// Stamps which room the slot's unit is attributed to — the room-attribution half of the
        /// per-tick view sync (<c>UnitRecordSyncSystem</c>). <paramref name="roomKey"/> is
        /// <c>SimSnapshotProjectionRules.RoomKey(room.id)</c>, or 0 when the unit is in no room;
        /// the snapshot projector matches it against the active-room set. Validated like every
        /// handle access: a stale handle writing another unit's attribution would present as
        /// "the unit flickers between rooms", not an exception.
        /// </summary>
        public void SetRoomKey(in UnitHandle handle, ulong roomKey)
        {
            Validate(handle, nameof(SetRoomKey));
            _roomKeys[handle.Slot] = roomKey;
        }

        // ── Batch surface (no handles) ──────────────────────────────────────────
        // The snapshot projector and future batch systems iterate the LIVE-count dense entries
        // rather than holding UnitHandles: LiveSlotAt maps a dense index to its slot and the two
        // raw reads fetch that slot's replicated record and room attribution. These reads skip
        // handle validation deliberately — a batch consumer does not (and must not) keep handles
        // alive across the iteration.

        /// <summary>The slot index at <paramref name="denseIndex"/> of the live list — iterate
        /// <see cref="LiveCount"/> entries through this, reading each slot with
        /// <see cref="UnitRecordAt(int)"/> and <see cref="RoomKeyAt(int)"/>.</summary>
        public int LiveSlotAt(int denseIndex)
        {
            if ((uint)denseIndex >= (uint)_liveCount)
            {
                throw new ArgumentOutOfRangeException(nameof(denseIndex), denseIndex,
                    $"The live list holds {_liveCount} entries.");
            }

            return _liveSlots[denseIndex];
        }

        /// <summary>The slot's replicated unit record — raw read for the batch surface; see the
        /// note above <see cref="LiveSlotAt(int)"/>.</summary>
        public SimUnitState UnitRecordAt(int slot) => _unitSlots[slot];

        /// <summary>The slot's room attribution (0 = in no room) — raw read for the batch
        /// surface; see the note above <see cref="LiveSlotAt(int)"/>.</summary>
        public ulong RoomKeyAt(int slot) => _roomKeys[slot];

        /// <summary>Drops every slot — the test/teardown reset. The next <see cref="Acquire"/>
        /// starts from slot 0 again; any handle handed out before this call is stale from here on,
        /// which is the ordinary release contract, just all at once. Generations are <i>bumped</i>,
        /// not zeroed: zeroing would hand generation 1 out a second time and let a pre-clear
        /// handle alias a reissued slot, which is exactly the confusion the generation exists to
        /// prevent.</summary>
        public void Clear()
        {
            for (int i = 0; i < _generations.Length; i++)
            {
                _generations[i]++;
            }

            Array.Clear(_slots, 0, _slots.Length);
            Array.Clear(_corpseSlots, 0, _corpseSlots.Length);
            Array.Clear(_motorSlots, 0, _motorSlots.Length);
            Array.Clear(_unitSlots, 0, _unitSlots.Length);
            Array.Clear(_roomKeys, 0, _roomKeys.Length);
            _freeCount = 0;
            _nextSlot = 0;
            _liveCount = 0;
        }

        private void Validate(in UnitHandle handle, string operation)
        {
            if (!handle.IsValid)
            {
                throw new InvalidOperationException(
                    $"UnitWorld.{operation}: the handle is Invalid (default) — the unit never " +
                    "acquired a slot, or its caller already cleared it.");
            }

            if (handle.Slot >= _nextSlot)
            {
                throw new InvalidOperationException(
                    $"UnitWorld.{operation}: slot {handle.Slot} was never issued by this world " +
                    $"(high-water mark {_nextSlot}). The handle belongs to a different UnitWorld.");
            }

            if (_generations[handle.Slot] != handle.Generation)
            {
                throw new InvalidOperationException(
                    $"UnitWorld.{operation}: handle (slot {handle.Slot}, generation " +
                    $"{handle.Generation}) is stale — the slot has been released and reissued at " +
                    $"generation {_generations[handle.Slot]}. Holding a handle past its unit's " +
                    "despawn is the bug this generation check exists to catch.");
            }
        }

        private void Grow()
        {
            int newCapacity = _slots.Length * 2;
            Array.Resize(ref _slots, newCapacity);
            Array.Resize(ref _corpseSlots, newCapacity);
            Array.Resize(ref _motorSlots, newCapacity);
            Array.Resize(ref _unitSlots, newCapacity);
            Array.Resize(ref _roomKeys, newCapacity);
            Array.Resize(ref _liveSlots, newCapacity);
            Array.Resize(ref _generations, newCapacity);

            // The free list never holds more than _nextSlot entries, and _nextSlot fits in the
            // old capacity — but grow it too so Release never has to check.
            Array.Resize(ref _freeList, newCapacity);
        }
    }

    /// <summary>
    /// The identity one unit's slot has inside a <see cref="UnitWorld"/> — a slot index paired
    /// with the generation the slot was issued at. Value type, two ints, nothing Unity-shaped:
    /// cheap to pass, safe to default-compare, and the generation half is what makes "the AI
    /// despawned and a new unit took the slot" detectable instead of silently wrong.
    /// </summary>
    public readonly struct UnitHandle : IEquatable<UnitHandle>
    {
        /// <summary>The slot inside the world's arrays.</summary>
        public readonly int Slot;

        /// <summary>The generation the slot was issued at. <c>0</c> means "never issued", which is
        /// what a default-constructed handle carries — so <c>default(UnitHandle)</c> is the one
        /// handle that is invalid by construction.</summary>
        public readonly int Generation;

        internal UnitHandle(int slot, int generation)
        {
            Slot = slot;
            Generation = generation;
        }

        /// <summary>Whether this handle can name a live slot. The default handle is invalid; a
        /// released one is made invalid by <see cref="UnitWorld.Release(ref UnitHandle)"/>.</summary>
        public bool IsValid => Generation > 0;

        /// <summary>The value to compare against and to hand back after a release.</summary>
        public static UnitHandle Invalid => default;

        public bool Equals(UnitHandle other) => Slot == other.Slot && Generation == other.Generation;

        public override bool Equals(object obj) => obj is UnitHandle other && Equals(other);

        public override int GetHashCode() => (Slot * 397) ^ Generation;

        public static bool operator ==(UnitHandle left, UnitHandle right) => left.Equals(right);

        public static bool operator !=(UnitHandle left, UnitHandle right) => !left.Equals(right);

        public override string ToString() => IsValid ? $"unit-slot {Slot}g{Generation}" : "unit-slot (invalid)";
    }

    /// <summary>
    /// The stealth/noise/suspicion runtime block of one unit — the six pieces AS3 scatters across
    /// <c>Unit</c> (<c>noise</c>, <c>noise_t</c>, <c>visibility</c>, <c>stealthMult</c>,
    /// <c>demask</c>, <c>detecting</c>) and <c>UnitPlayer</c> (<c>obs</c>/<c>isObs</c>,
    /// <c>lurked</c>, <c>isSit</c>), migrated off <c>UnitController</c> as one domain. One struct
    /// per slot — a single cache line's worth of reads for the sensor sweep, and one
    /// acquire/release unit for the world.
    ///
    /// <para><b>Authority, unchanged from the fields this replaces:</b> host-owned sim truth
    /// (F-02), never replicated, joined to <see cref="SimUnitState"/> only when the owning system
    /// is extracted. The per-field rationales moved here verbatim from the controller's
    /// declarations; the arithmetic that advances them stays in <see cref="NoiseMath"/> and
    /// <see cref="StealthMath"/>.</para>
    /// </summary>
    public struct UnitStealthData
    {
        /// <summary>
        /// AS3 <c>Unit.noise:int = 0</c> — how far this unit can currently be heard, in pixels
        /// before the listener's own <c>ear</c> is applied. Raised by <c>Unit.makeNoise</c>,
        /// decays 20 per tick, and is <b>0 whenever the unit is not doing anything loud</b>.
        /// </summary>
        public int Noise;

        /// <summary>
        /// AS3 <c>Unit.noise_t:int = 30</c> — the cooldown on the visible "something made a sound
        /// here" ripple. Kept because it is the only stateful half of <c>makeNoise</c>, and read
        /// by the F3 overlay so the mechanic is visible during a play-test.
        /// </summary>
        public int NoiseRippleTicks;

        /// <summary>
        /// AS3's <c>stay</c> as of the previous step, for detecting the landing edge. AS3 reads it
        /// directly because its <c>run()</c> sets it; the port resolves groundedness in two
        /// different places depending on the driver, so the edge is tracked here instead.
        /// </summary>
        public bool NoiseWasGrounded;

        /// <summary>
        /// The downward speed at the last airborne step, in px/frame — AS3's <c>dy</c> at the
        /// moment <c>checkShelf</c> fires. The port resolves the landing a step later, so the
        /// value has to be carried.
        /// </summary>
        public float NoiseFallSpeedPixelsPerFrame;

        /// <summary>
        /// AS3's <c>UnitPlayer.obs</c> / <c>isObs</c> — the suspicion the world has accumulated
        /// about this unit. Declared on <c>UnitPlayer</c> in the oracle and deliberately on every
        /// unit here: the sensor loop only ever sees a unit, so the meter has to be reachable from
        /// there. Non-player units simply never accumulate any — nothing calls the add for them.
        /// </summary>
        public NoiseMath.Observation Suspicion;

        /// <summary>
        /// The target-side stealth state — AS3's <c>visibility</c>, <c>stealthMult</c>,
        /// <c>demask</c>, <c>detecting</c>, <c>lurked</c>, <c>isSit</c>. Advanced by the unit's
        /// per-tick stealth pass; read by the enemy sensors.
        /// </summary>
        public StealthMath.StealthState Stealth;

        /// <summary>
        /// The state a freshly acquired slot starts at — what the controller's field
        /// initialisers used to produce. <see cref="NoiseWasGrounded"/> starts <c>true</c>
        /// (a unit is born on the ground; AS3's <c>stay</c> initialises likewise) and
        /// <see cref="Stealth"/> starts at <see cref="StealthMath.StealthState.Default"/>.
        /// </summary>
        public static UnitStealthData Default =>
            new UnitStealthData
            {
                NoiseWasGrounded = true,
                Stealth = StealthMath.StealthState.Default,
            };
    }

    /// <summary>
    /// Where a unit sits in AS3's <c>sost</c> state machine, as far as the port models it — the
    /// branch table of <c>Unit.die()</c> (<c>Unit.as:4302-4368</c>) as a value. Moved out of
    /// <c>UnitController</c> with the corpse-lifecycle domain (thread J, Phase 2); the member docs
    /// are the controller's own, kept verbatim.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the states that change behaviour.</b> AS3's <c>2</c> (dying, on a
    /// <c>timerDie</c> countdown) is not modelled — nothing in the port delays a death — so a unit
    /// goes straight from <c>1</c> to <c>3</c>. The three that remain are the three that a damage
    /// source, a bullet's stop decision or a despawn can observe.</para>
    /// </remarks>
    public enum CorpseState
    {
        /// <summary><c>sost 1</c> — alive. No body has been left.</summary>
        None = 0,

        /// <summary>
        /// <c>sost 3</c> — a body. Solid, still a legal target, still accumulating overkill until
        /// <c>UnitStats.CorpseDestroyed</c>.
        /// </summary>
        Corpse = 1,

        /// <summary>
        /// <c>sost 3</c> with a live <c>Desintegr</c> — the 40-frame dissolve a bloody body goes
        /// through on its way out (<c>Unit.as:4347-4353</c>). Still solid and still targetable, as
        /// in the oracle: <c>initBurn</c> changes nothing about <c>X1..X2</c> or <c>sost</c>.
        /// </summary>
        Dissolving = 2,

        /// <summary><c>sost 4</c> — <c>exterminate()</c> ran. Out of the world for good.</summary>
        Removed = 3,
    }

    /// <summary>
    /// The corpse-lifecycle runtime block of one unit — AS3's <c>sost</c> state, the dissolve
    /// countdown, the dissolve preset, and the killing blow's damage type. Migrated off
    /// <c>UnitController</c> as the second <see cref="UnitWorld"/> domain (thread J, Phase 2),
    /// beside <see cref="UnitStealthData"/>; the per-field rationales moved here from the
    /// controller's declarations, and the arithmetic that advances them stays in
    /// <c>PFE.Systems.Combat.DesintegrRules</c> and <c>CorpseDamageMath</c>.
    ///
    /// <para><b>Authority, unchanged from the fields this replaces:</b> host-owned sim truth
    /// (F-02), never replicated, joined to <see cref="PFE.Sim.State.SimUnitState"/> only when the
    /// owning system is extracted. The view-only halves of the old dissolve state (the per-body
    /// material, the captured tint) deliberately did <i>not</i> migrate — a sim store must not
    /// hold Unity objects.</para>
    /// </summary>
    public struct UnitCorpseData
    {
        /// <summary>
        /// Which branch of <c>Unit.die()</c> this unit is in — the port's <c>sost</c>. The state
        /// machine's transitions live on the controller; this is only the value those transitions
        /// write and every damage source reads.
        /// </summary>
        public CorpseState State;

        /// <summary>Canonical frames of dissolve elapsed, accumulated by the per-tick corpse pass.</summary>
        public float DissolveFrames;

        /// <summary>
        /// Which of the seven <c>Desintegr</c> presets this body is dissolving with — <c>initBurn</c>'s
        /// argument, kept because every frame of the effect reads it.
        /// </summary>
        public DesintegrTip Tip;

        /// <summary>
        /// The <c>D_*</c> code of the last hit that landed on a <i>living</i> unit — AS3's
        /// <c>param2</c>, remembered because the death effect is chosen from it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this has to be stored rather than passed.</b> <c>Unit.damage(param1, param2, …)</c>
        /// reads <c>param2</c> in the same breath as the HP subtraction (<c>Unit.as:3713-3741</c>), so the
        /// oracle never needs to keep it. The port splits that one function into a resolver
        /// (<c>DamageSystem</c> → <c>DamageCalculator</c>) and a receiver (<c>ApplyDamage</c>), and by the
        /// time <c>RaiseDeath()</c> reaches <c>EnterCorpse</c> the context is gone — the
        /// <c>DamageOutcome</c> is the only survivor, which is why it carries the type.</para>
        ///
        /// <para><b>Only written while alive.</b> A hit on a corpse returns early in the controller's
        /// <c>ApplyDamage</c>, so a body already dissolving cannot have its preset re-decided by
        /// whoever keeps shooting it — the oracle has the same property, because a dead unit's
        /// <c>damage()</c> never reaches the <c>hp &lt;= 0</c> block again.</para>
        ///
        /// <para><b><see cref="DamageType.PhysicalBullet"/> is the resting value and the correct one.</b>
        /// It maps to no burn tip, so a unit killed by something that never went through the damage
        /// pipeline — a scripted <c>KillNow</c>, a dev-console command, a fixture calling
        /// <c>OnDeath</c> directly — dies exactly as a rifle shot would, which is the least surprising
        /// answer available.</para>
        /// </remarks>
        public DamageType LastDamageType;

        /// <summary>The state a freshly acquired slot starts at — what the controller's field
        /// initialisers used to produce. <see cref="LastDamageType"/> rests at
        /// <see cref="DamageType.PhysicalBullet"/>; everything else is the enum's own zero.</summary>
        public static UnitCorpseData Default =>
            new UnitCorpseData { LastDamageType = DamageType.PhysicalBullet };
    }
}
