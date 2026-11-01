using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Entities.Player;
using PFE.Systems.Map;
using PFE.Systems.Pets;
using UnityEngine;

namespace PFE.Entities.Pets
{
    /// <summary>
    /// The player's companion manager — the port of <c>UnitPlayer.callPet</c>/<c>uncallPet</c>
    /// (<c>UnitPlayer.as:3899-3970</c>), the pet half of <c>UnitPlayer.step</c> (<c>:1495-1497</c>,
    /// <c>:3480-3510</c>) and the owl's saved HP (<c>Pers.as:2369-2381</c>).
    ///
    /// <para><b>Why a component on the player and not state on the pet.</b> Every rule here is a rule
    /// about <i>the player's</i> relationship with a pet: the 5-second summon lock, the per-pet death
    /// cooldown, which pet is currently out, and what happens when the player changes room. The pet
    /// object is destroyed with the room, so anything that has to survive that has to live here. AS3
    /// keeps it in exactly the same place — <c>gg.currentPet</c>, <c>gg.noPet</c>, <c>gg.noPet2</c>,
    /// <c>gg.retPet</c> are all on the player's global object, not on the pet.</para>
    ///
    /// <para><b>An <see cref="ISimTickable"/> at <see cref="SimTickOrder.PlayerMotor"/>.</b> The player
    /// is motor-driven, so <c>UnitController.SimTick</c> returns early for it and a separate tickable is
    /// the only per-tick hook — the same reason <c>PlayerManaTicker</c> and
    /// <c>PlayerRadiationTicker</c> exist. Registering it is <c>PlayerController.AttachSimulation</c>'s
    /// job; an unregistered tickable silently never runs, which presents as "the phoenix never revives".</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PetCompanion : MonoBehaviour, ISimTickable
    {
        /// <summary>
        /// One live controller per kind. AS3 constructs all three pets once
        /// (<c>UnitPlayer.as:446-465</c>) and keeps them for the session, so switching from the owl to
        /// the phoenix and back does <b>not</b> heal the owl — the same object comes back with the HP it
        /// had. Keeping the controllers here reproduces that; spawning on demand would not.
        /// </summary>
        private readonly Dictionary<PetKind, PetController> _pets = new Dictionary<PetKind, PetController>();

        /// <summary>AS3 <c>gg.currentPet</c> — the pet that is out, or null.</summary>
        private PetKind? _current;

        /// <summary>AS3 <c>gg.noPet</c> — the dead pet's own cooldown, in ticks.</summary>
        private int _noPet;

        /// <summary>AS3 <c>gg.noPet2</c> — the global 5-second summon lock, in ticks.</summary>
        private int _noPetLock;

        /// <summary>AS3 <c>gg.retPet</c> — the pet a potion should restore (<c>:298</c>, <c>:365</c>).</summary>
        private string _retPet = string.Empty;

        /// <summary>
        /// Each pet's HP as a fraction of its own maximum, so a pet rebuilt after a room change comes
        /// back with the health it had. AS3 keeps the object alive instead
        /// (<c>Location.resetUnits</c> keeps pets, <c>Location.as:2252-2254</c>); the port's room owns
        /// its unit GameObjects, so the fraction is the honest equivalent — and it is also exactly what
        /// <c>Pers.owlhpProc</c> saves.
        /// </summary>
        private readonly Dictionary<PetKind, float> _hpFraction = new Dictionary<PetKind, float>();

        private PlayerController _player;
        private SimLoop _loop;
        private bool _registered;
        private bool _reportedNoRoom;

        /// <summary>
        /// AS3 <c>gg.atkPoss</c>. Defaults to <c>true</c> because the oracle's default is 1 and only two
        /// <c>&lt;sk id='atkPoss' v1='0'/&gt;</c> rows ever zero it; the port has the flag on
        /// <c>PlayerSpellCaster</c> with no producer yet, so reading it here would be a second source for
        /// a value nothing writes. Wired when that producer lands.
        /// </summary>
        public Func<bool> CanAttackProvider { get; set; }

        /// <summary>AS3 <c>World.w.alicorn</c> — the "you are the alicorn" mode forbids pets.</summary>
        public Func<bool> AlicornProvider { get; set; }

        /// <summary>
        /// AS3 <c>loc.petOn</c> — false in a room whose cell authors <c>&lt;options petoff/&gt;</c>.
        ///
        /// <para><b>Named gap.</b> The port generates that flag (<c>LandLayoutPlan.PetOn</c>) but never
        /// carries it onto <see cref="RoomInstance"/>, so every room answers <c>true</c> today. The seam
        /// is here, and <see cref="PetRules.DecideCall"/> already refuses and freezes on it, so
        /// importing the flag is the only remaining work.</para>
        /// </summary>
        public Func<RoomInstance, bool> PetsAllowedProvider { get; set; }

        /// <summary>AS3 <c>gg.currentPet</c> — the pet that is out, or null.</summary>
        public PetKind? Current => _current;

        /// <summary>The live controller for the pet that is out, or null.</summary>
        public PetController ActivePet =>
            _current.HasValue && _pets.TryGetValue(_current.Value, out PetController pet) ? pet : null;

        /// <summary>AS3 <c>gg.noPet</c> in ticks — the dead pet's remaining cooldown.</summary>
        public int CooldownTicks => _noPet;

        /// <summary>AS3 <c>gg.retPet</c> — the pet a potion restores.</summary>
        public string ReturnPetId => _retPet;

        /// <summary>AS3 <c>gg.currentPet.id</c>, or null.</summary>
        public string CurrentPetId => _current.HasValue ? PetIds.ToId(_current.Value) : null;

        /// <summary>
        /// AS3 <c>gg.currentPet.id == id</c> — the <c>&lt;dial pet='phoenix'&gt;</c> gate
        /// (<c>NPC.as:300-310</c>).
        /// </summary>
        public bool HasPet(string petId)
        {
            if (string.IsNullOrEmpty(petId) || !_current.HasValue) return false;

            return string.Equals(PetIds.ToId(_current.Value), petId, StringComparison.Ordinal);
        }

        /// <summary>The player's own step — see the class remarks.</summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        public void Construct(PlayerController player)
        {
            _player = player;
        }

        public void Attach(SimClock clock, SimLoop loop)
        {
            if (loop == null) return;

            _loop = loop;
            if (!_registered)
            {
                _loop.Register(this);
                _registered = true;
            }
        }

        private void OnDestroy()
        {
            if (_registered && _loop != null)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }

        // ── The tick ───────────────────────────────────────────────────────────────────────────────

        public void SimTick(int tickIndex)
        {
            if (_player == null) return;

            if (_noPet > 0) _noPet--;
            if (_noPetLock > 0) _noPetLock--;

            if (!_current.HasValue)
            {
                PetHealthBarFeed.Clear();
                return;
            }

            // A pet is destroyed with its room, so a room change has to rebuild it. AS3 keeps the pet
            // object and re-parents it (`UnitPlayer.as:618-634`); the port rebuilds it at the player and
            // restores the HP fraction, which is the same picture.
            PetController pet = EnsureSpawned(_current.Value);
            if (pet == null) return;

            // `if(pet && pet.sost == 4 && noPet <= 0 && pet.optAutores) pet.resurrect();` — the
            // phoenix's automatic revive (`UnitPlayer.as:1495-1497`).
            if (pet.Sost >= PetController.SostGone && _noPet <= 0 && PetRules.AutoResurrects(_current.Value))
            {
                pet.Resurrect();
            }

            // A living pet reports its fraction, and the fraction is remembered so a rebuild keeps it.
            if (pet.MaxHp > 0f)
            {
                _hpFraction[pet.Kind] = Mathf.Clamp01(pet.Hp / pet.MaxHp);
            }

            PetHealthBarFeed.Report(pet.Hp / Mathf.Max(1f, pet.MaxHp), _noPet / (float)SimClock.FramesPerSecond);
        }

        // ── callPet (UnitPlayer.as:3899-3952) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>gg.callPet(id, force)</c>. <paramref name="forced"/> is what the moon spell and the two
        /// potion restores pass; it bypasses every refusal but the alicorn one.
        /// </summary>
        /// <returns>True when a pet is out afterwards (a toggle-off returns true too).</returns>
        public bool CallPet(PetKind kind, bool forced = false)
        {
            if (_player == null) return false;

            PetController owl = LivePet(PetKind.Owl);
            float owlHp = _hpFraction.TryGetValue(PetKind.Owl, out float savedOwlFraction) ? savedOwlFraction : 1f;
            if (owl != null) owlHp = owl.Hp;

            var request = new PetCallRequest(
                kind,
                forced,
                noPetTicks: _noPet,
                lockTicks: _noPetLock,
                canAttack: CanAttackProvider == null || CanAttackProvider(),
                alicorn: AlicornProvider != null && AlicornProvider(),
                current: _current,
                owlHp: owlHp,
                petsAllowed: PetsAllowed(_player.CurrentRoom));

            PetCallDecision decision = PetRules.DecideCall(request);

            if (decision.Refused)
            {
                // AS3 prints `petNot` / `petNot2` / `petNot3`; the port has no info strip on the HUD for
                // it yet, so the refusal is logged with the key intact.
                Debug.Log($"[PetCompanion] call '{PetIds.ToId(kind)}' refused ({decision.Refusal}).");
                return false;
            }

            // `noPet2 = 5 * 30;` — set AFTER the first two refusals and BEFORE the owl test, so a
            // refused owl call still spends the lock (`:3915`).
            _noPetLock = PetRules.SummonLockTicks;

            // `if(pet) { info("petRecall"); pet.recall(); pet = null; }` — whatever is out goes away
            // first, even when the same pet is being asked for again.
            if (_current.HasValue)
            {
                LivePet(_current.Value)?.Recall();
            }

            _retPet = string.Empty;

            if (_current.HasValue && _current.Value == kind)
            {
                // `if(currentPet == param1) currentPet = "";` — the same pet toggles OFF.
                _current = null;
                PetHealthBarFeed.Clear();
                return true;
            }

            if (!PetsAllowed(_player.CurrentRoom))
            {
                _current = null;
                Debug.Log("[PetCompanion] this room forbids pets (`noPetCall`).");
                return false;
            }

            _current = kind;

            PetController pet = EnsureSpawned(kind);
            if (pet == null)
            {
                _current = null;
                return false;
            }

            pet.Call();
            return true;
        }

        /// <summary>
        /// <c>gg.uncallPet(keepRet)</c> (<c>:3954-3970</c>) — dismiss and forget. With
        /// <paramref name="keepRet"/> the id is remembered so <c>potion_shadow</c>/<c>potion_rat</c> can
        /// restore it when they end.
        /// </summary>
        public void UncallPet(bool keepRet = false)
        {
            string dismissedId = _current.HasValue ? PetIds.ToId(_current.Value) : null;

            if (_current.HasValue)
            {
                LivePet(_current.Value)?.Recall();
            }

            _current = null;

            // `if(keepRet) this.retPet = id;` — remembered so a potion can put the pet back when its
            // effect ends (`Effect.as:298`, `:365`).
            _retPet = keepRet && !string.IsNullOrEmpty(dismissedId) ? dismissedId : string.Empty;

            PetHealthBarFeed.Clear();
        }

        /// <summary>Re-summon the pet a potion dismissed, if there is one.</summary>
        public bool RestoreReturnedPet()
        {
            if (string.IsNullOrEmpty(_retPet) || !PetIds.TryParse(_retPet, out PetKind kind)) return false;

            _retPet = string.Empty;
            return CallPet(kind, forced: true);
        }

        /// <summary>
        /// <c>cast_moon</c> (<c>Spell.as:428-440</c>) — the <c>sp_moon</c> spell, the <b>only</b> way the
        /// moon is ever summoned.
        ///
        /// <para><b>Two branches, and the second is the one that is easy to lose.</b> The oracle reads
        /// <c>if(currentPet != "moon")</c> → full-heal the stored moon and call it <b>forced</b>;
        /// <b>else</b> → <c>pet.heal(pet.maxhp)</c>, i.e. casting it again while the moon is already out
        /// is a <i>full heal</i>, not a no-op and not a re-summon. A port that treated the second cast as
        /// "already summoned" would silently turn the spell into a one-shot.</para>
        ///
        /// <para><b><c>hp = maxhp</c> before the call, deliberately.</b> The moon is summoned at full
        /// health every time, so a moon that died and is re-cast does not come back at the fraction it
        /// died with. That is why the fraction is written before <see cref="CallPet"/> rather than
        /// letting <c>EnsureSpawned</c> restore the old one.</para>
        /// </summary>
        public bool CastMoon()
        {
            if (_player == null) return false;

            if (!_current.HasValue || _current.Value != PetKind.Moon)
            {
                _hpFraction[PetKind.Moon] = 1f;

                PetController stored = LivePet(PetKind.Moon);
                if (stored != null && stored.MaxHp > 0f) stored.SetHpFraction(1f);

                return CallPet(PetKind.Moon, forced: true);
            }

            // `else if(this.gg.pet) this.gg.pet.heal(this.gg.pet.maxhp);`
            PetController moon = ActivePet;
            if (moon == null) return false;

            moon.Heal(moon.MaxHp);
            return true;
        }

        // ── Death ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="PetController"/>'s death report — <c>UnitPet.die()</c>'s tail (<c>:510-520</c>).
        /// </summary>
        public void OnPetDied(PetController pet)
        {
            if (pet == null) return;

            if (pet.MaxHp > 0f) _hpFraction[pet.Kind] = 0f;

            // `if(optAutores) noPet = pers.petRes * 30;` — the phoenix alone starts a cooldown; an owl
            // waits to be repaired, a moon has to be re-cast.
            if (PetRules.AutoResurrects(pet.Kind))
            {
                _noPet = PetRules.DeathCooldownTicks(PetRules.EffectivePetRes);
            }

            PetHealthBarFeed.Report(0f, _noPet / (float)SimClock.FramesPerSecond);
        }

        /// <summary>
        /// <c>if(optUncall) { recall(); gg.pet = null; currentPet = ""; }</c> — the moon's death is an
        /// un-summon, not a corpse (<c>UnitPet.as:498-505</c>).
        /// </summary>
        public void OnPetUncalled(PetController pet)
        {
            if (pet == null) return;

            if (_current.HasValue && _current.Value == pet.Kind)
            {
                _current = null;
            }

            _pets.Remove(pet.Kind);
            _hpFraction[pet.Kind] = 0f;
            PetHealthBarFeed.Clear();
        }

        /// <summary>
        /// A player level-up revives a dead summoned pet for free — <c>noPet = 0</c> then
        /// <c>resurrect()</c> when <c>optAutores</c> (<c>UnitPlayer.as:3495-3501</c>).
        /// </summary>
        public void OnPlayerLevelUp()
        {
            if (!_current.HasValue) return;
            if (!PetRules.AutoResurrects(_current.Value)) return;

            PetController pet = LivePet(_current.Value);
            if (pet == null) return;

            _noPet = 0;
            if (pet.Sost >= PetController.SostGone)
            {
                pet.Resurrect();
            }
        }

        // ── Healing ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>this.pet.heal(this.drad / 15, 1)</c> (<c>UnitPlayer.as:942-945</c>) — the player's
        /// radiation heals the phoenix and nothing else.
        /// </summary>
        public void HealFromRadiation(float drad)
        {
            PetController pet = ActivePet;
            if (pet == null) return;

            pet.Heal(drad / PetRules.RadiationHealDivisor, mode: 1);
        }

        /// <summary>
        /// The workbench/vendor repair (<c>PipPageWork.as:595-607</c>, <c>PipPageVend.as:823-836</c>).
        /// The owl is the only pet a shop can mend, and repairing it revives it <b>only while it is the
        /// summoned pet</b> — <c>if(sost == 4 &amp;&amp; gg.currentPet == id)</c>.
        /// </summary>
        /// <returns>The HP actually restored, for the caller's price.</returns>
        public float RepairOwl(float amount)
        {
            PetController owl = LivePet(PetKind.Owl);
            if (owl == null || amount <= 0f) return 0f;

            float before = owl.Hp;
            owl.Repair(amount, reviveIfDead: _current.HasValue && _current.Value == PetKind.Owl);

            if (owl.MaxHp > 0f)
            {
                _hpFraction[PetKind.Owl] = Mathf.Clamp01(owl.Hp / owl.MaxHp);
            }

            return Mathf.Max(0f, owl.Hp - before);
        }

        /// <summary>
        /// <c>owl.hp &lt; owl.maxhp</c> — the condition that puts the owl row on the workbench page
        /// (<c>PipPageWork.as:217-236</c>).
        /// </summary>
        public bool OwlNeedsRepair
        {
            get
            {
                PetController owl = LivePet(PetKind.Owl);
                return owl != null && owl.Hp < owl.MaxHp;
            }
        }

        // ── Persistence ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The saved half — AS3 <c>Pers.owlhpProc</c> (<c>:2369-2381</c>) plus the three global fields.
        /// </summary>
        public PetSaveData CreateSaveData()
        {
            var data = new PetSaveData
            {
                currentPet = CurrentPetId ?? string.Empty,
                noPetTicks = _noPet,
                returnPet = _retPet,
            };

            for (int i = 0; i < PetRules.AllKinds.Length; i++)
            {
                PetKind kind = PetRules.AllKinds[i];
                float fraction = _hpFraction.TryGetValue(kind, out float saved) ? saved : 1f;

                PetController pet = LivePet(kind);
                if (pet != null && pet.MaxHp > 0f)
                {
                    fraction = Mathf.Clamp01(pet.Hp / pet.MaxHp);
                }

                data.SetHpFraction(kind, fraction);
            }

            return data;
        }

        /// <summary>
        /// Put a saved state back. The pet itself is summoned on the next tick, at the player, with the
        /// saved HP — which is what <c>Location.createUnit</c> does for a pet whose
        /// <c>owlhpProc &gt; 0</c> (<c>Pers.as:753-755</c>).
        /// </summary>
        public void RestoreFromSaveData(PetSaveData data)
        {
            if (data == null) return;

            _noPet = Mathf.Max(0, data.noPetTicks);
            _retPet = data.returnPet ?? string.Empty;

            for (int i = 0; i < PetRules.AllKinds.Length; i++)
            {
                PetKind kind = PetRules.AllKinds[i];
                _hpFraction[kind] = Mathf.Clamp01(data.GetHpFraction(kind));
            }

            if (!string.IsNullOrEmpty(data.currentPet) && PetIds.TryParse(data.currentPet, out PetKind kind2))
            {
                // `gg.currentPet = pers.currentPet` is restored unconditionally, even for a pet whose
                // `owlhpProc <= 0` (`UnitPlayer.as:456-457, :475`): the owl is the pet that is out, it
                // is simply at zero health and has to be repaired. That is why the fraction and the id
                // are independent fields rather than one "is alive" bit.
                _current = kind2;
            }
            else
            {
                _current = null;
            }
        }

        // ── Plumbing ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The live controller for <paramref name="kind"/>, spawning it at the player when it does not
        /// exist or was destroyed with a room. Returns null when the room cannot host it.
        /// </summary>
        PetController EnsureSpawned(PetKind kind)
        {
            if (_pets.TryGetValue(kind, out PetController pet) && pet != null)
            {
                return pet;
            }

            RoomInstance room = _player.CurrentRoom;
            if (room == null)
            {
                if (!_reportedNoRoom)
                {
                    _reportedNoRoom = true;
                    Debug.LogWarning(
                        "[PetCompanion] the player has no current room, so the pet cannot be built. " +
                        "AS3 cannot reach this state — `loc` is the location.");
                }

                return null;
            }

            pet = PetSpawner.Spawn(room, kind, _player.transform.position);
            if (pet == null) return null;

            pet.SetOwner(_player, this);

            // The HP a rebuilt pet comes back with — the saved fraction of its own maximum. The
            // spawner builds every unit at full health (`currentHealth = definition.health`), so this
            // is not optional: without it a room change heals the pet, and a pet saved dead comes back
            // alive.
            float fraction = _hpFraction.TryGetValue(kind, out float saved) ? saved : 1f;
            pet.SetHpFraction(fraction);

            // A pet that was out is still out after the rebuild. `Call()` is the oracle's own
            // `call()`; with hp > 0 it only re-shows the body and re-runs `setLevel`, and
            // `PetRules.RescaleCurrentHp` preserves the fraction just written. A pet at zero is left
            // dead on purpose: for the phoenix the cooldown arm in `SimTick` is what brings it back,
            // and for the owl only a repair can.
            if (_current.HasValue && _current.Value == kind && fraction > 0f)
            {
                pet.Call();
            }

            _pets[kind] = pet;
            return pet;
        }

        /// <summary>The live controller for a kind, or null. Never spawns.</summary>
        PetController LivePet(PetKind kind)
        {
            if (_pets.TryGetValue(kind, out PetController pet))
            {
                return pet != null ? pet : null;
            }

            return null;
        }

        bool PetsAllowed(RoomInstance room)
        {
            if (PetsAllowedProvider != null) return PetsAllowedProvider(room);
            return true;
        }
    }
}
