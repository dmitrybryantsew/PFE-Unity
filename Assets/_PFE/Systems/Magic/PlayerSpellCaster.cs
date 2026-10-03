using System;
using UnityEngine;
using MessagePipe;
using R3;
using PFE.Core;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.ModAPI;
using PFE.Systems.Audio;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// The live player spell caster — the port of AS3 <c>UnitPlayer</c>'s spell half: the
    /// <c>invent.spells</c> collection, the per-tick <c>step()</c> sweep, and the two cast triggers in
    /// <c>control()</c> (<c>UnitPlayer.as:1613-1616</c>, <c>:2226-2247</c>, <c>:2252-2284</c>).
    ///
    /// <para><b>This is the component that makes the spell subsystem reachable.</b> Everything it calls
    /// was written and proved offline in earlier passes — <see cref="Spell"/> (the cast prologue),
    /// <see cref="SpellCastRules"/> (the gates), <see cref="SpellBook"/> (the triggers),
    /// <see cref="LiveSpellHost"/> (the adapter). None of it ran in the game, because nothing owned a
    /// <see cref="SpellBook"/> or fed it a key. That is what this class is.</para>
    ///
    /// <para><b>Why a separate <see cref="ISimTickable"/> at <see cref="SimTickOrder.PlayerMotor"/>.</b>
    /// The same reason as <see cref="PFE.Entities.Player.PlayerManaTicker"/>, and the same hook: the
    /// player is motor-driven, so <c>UnitController.SimTick</c> returns early for it and there is no
    /// other per-tick call site. AS3 puts both blocks in the player's own frame; the port gives the
    /// player its own tick and this registers on it. Registering is <b>not</b> optional — an
    /// unregistered tickable silently never runs, which presents as "C does nothing" rather than as an
    /// error.</para>
    ///
    /// <para><b>It sits at the same <see cref="SimTickOrder"/> as the mana ticker and deliberately
    /// behind it.</b> <see cref="PlayerController"/> attaches the mana ticker first, so at equal order
    /// the mana block runs before this one and a cast spends from a pool that already regenerated this
    /// tick — which is the AS3 order (<c>step()</c>'s mana block precedes <c>control()</c>).</para>
    ///
    /// <para><b>No Unity type appears in <see cref="ISpellCaster"/>/<see cref="ISpellWorld"/></b>, so
    /// the mapping below is the only part of this class that cannot be proved offline — which is why it
    /// is a straight read of public fields, one per oracle site, with no branching of its own.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerSpellCaster : MonoBehaviour, ISpellCaster, ISimTickable
    {
        /// <summary>
        /// AS3 <c>World.kolHK</c> (<c>World.as:60</c>) — the number of <b>weapon</b> hotkey slots. The
        /// favourite table is indexed <c>kolHK * 2 + slot</c> (<c>UnitPlayer.as:2263</c>), so this
        /// constant is what converts a spell hotkey number into a favourite index.
        /// </summary>
        public const int FavouriteWeaponSlots = 12;

        /// <summary>
        /// AS3 <c>World.kolQS</c> (<c>World.as:62</c>) — the number of favourite-<b>spell</b> slots.
        /// Taken from <see cref="InputReader.SpellHotkeySlots"/> so the key bindings and the favourite
        /// indexing cannot drift apart.
        /// </summary>
        public const int HotkeySlots = InputReader.SpellHotkeySlots;

        // ── Collaborators ────────────────────────────────────────────────────────────────────────────

        private CharacterStats _stats;
        private UnitStats _unitStats;
        private WeaponMounts _mounts;
        private LandMap _landMap;
        private ISoundService _sound;
        private ContentRegistry _registry;
        private GameInventory _inventory;

        /// <summary>
        /// The cast target, in the same space as <see cref="ISpellCaster.MagicX"/>/<c>MagicY</c> —
        /// AS3's <c>World.w.celX</c>/<c>celY</c>. A delegate rather than a captured vector because the
        /// aim moves every frame and the caster must read the <i>current</i> one at cast time.
        /// </summary>
        private Func<Vector2> _aimProvider;

        // ── The seam ─────────────────────────────────────────────────────────────────────────────────

        private LiveSpellWorld _world;
        private LiveSpellHost _host;
        private SpellBook _book;

        // ── Held key state — AS3 ctr.keyDef / ctr.keySpell1..4 ───────────────────────────────────────
        //
        // Held booleans, and the cast WRITES THEM BACK: a failed cast or a non-prod spell clears the key
        // (UnitPlayer.as:2237-2244, :2268-2276). That is the whole prod rule, and it is why the input
        // messages carry both edges instead of a press event.
        private bool _defHeld;
        private readonly bool[] _hotkeyHeld = new bool[HotkeySlots];

        // ── The three AS3 `gg` fields the port has no home for ───────────────────────────────────────

        /// <summary>
        /// AS3 <c>gg.rat</c> (<c>UnitPlayer.as:311</c>, default 0) — the rat-potion transformation.
        /// <b>No producer in the port yet</b>: AS3 sets it from the <c>potion_rat</c> effect
        /// (<c>:4018</c>). Left as a field with a public setter so the effect path can write it when it
        /// lands, rather than making the whole gate unreachable.
        /// </summary>
        private int _rat;

        /// <summary>
        /// AS3 <c>gg.atkPoss</c> (<c>UnitPlayer.as:205</c>) — <b>defaults to 1 (true) and must.</b> Only
        /// the two <c>&lt;sk id='atkPoss' v1='0'/&gt;</c> rows zero it, and 7 of the 9 spells require it;
        /// a default of <c>false</c> would refuse them outright. Slice 5 is the producer that writes it.
        /// </summary>
        private bool _atkPoss = true;

        /// <summary>
        /// AS3 <c>gg.t_cryst</c> (<c>UnitPlayer.as:147</c>) — the <c>sp_cryst</c> window. Per-player, not
        /// per-spell, which is exactly why it cannot live on <see cref="Spell"/>: a second crystal cast
        /// inside the window is the one that downgrades to <c>est = 2</c>.
        /// </summary>
        private int _crystCooldown;

        // ── Room-derived line-of-sight query ─────────────────────────────────────────────────────────

        private RoomInstance _derivedRoom;
        private UnifiedTileQueryService _derivedTileQuery;

        // ── Sim registration ─────────────────────────────────────────────────────────────────────────

        private SimLoop _loop;
        private bool _registered;
        private bool _warnedLegacy;

        private CompositeDisposable _disposables;

        // ── Public surface ───────────────────────────────────────────────────────────────────────────

        /// <summary>The player's spell collection. Never null once <see cref="Construct"/> has run.</summary>
        public SpellBook Book => _book;

        /// <summary>How many spells the player currently owns — AS3's <c>invent.spells</c> count.</summary>
        public int SpellCount => _book?.Count ?? 0;

        /// <summary>The spell the Def key will cast — AS3's <c>currentSpell</c>.</summary>
        public Spell Selected => _book?.Current;

        /// <summary>The player's own step. See the class remarks for why it shares the mana ticker's order.</summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        // ── Wiring ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Binds every collaborator. Called once by <see cref="PlayerController"/> from <c>Construct</c>,
        /// because this component is added at runtime and VContainer only injects registered components —
        /// the same constraint <see cref="PFE.Entities.Player.PlayerManaTicker.Construct"/> documents.
        /// </summary>
        public void Construct(
            CharacterStats stats,
            UnitStats unitStats,
            WeaponMounts mounts,
            LandMap landMap,
            ISoundService sound,
            ContentRegistry registry,
            Func<Vector2> aimProvider)
        {
            _stats = stats;
            _unitStats = unitStats;
            _mounts = mounts;
            _landMap = landMap;
            _sound = sound;
            _registry = registry;
            _aimProvider = aimProvider;

            // The world is built once: it holds no state, and its own room tracking is the Func below.
            _world = new LiveSpellWorld(ResolveTileQuery, _sound);
            _host = new LiveSpellHost(this, _world);

            // AS3 `Invent.spells` is a string-keyed collection built lazily as items are used
            // (Invent.addSpell, :1018-1037). The factory is the port's AllData lookup: an id with no
            // item row, or a row whose spellData was never populated, is simply not a spell — and
            // SpellBook.GetOrAdd returns null for it rather than conjuring a phantom.
            _book = new SpellBook(CreateSpell);
        }

        /// <summary>
        /// Hands over the inventory, which is the producer for two things: the favourite-spell hotkeys
        /// (<c>invent.fav</c>) and the <c>respect == 1</c> refusal. <b>Optional, and null today in
        /// production</b> — the port has no production <see cref="GameInventory"/> yet, so the hotkeys
        /// resolve to nothing and the respect gate never fires. The Def key does not depend on it.
        /// </summary>
        public void SetInventory(GameInventory inventory) => _inventory = inventory;

        /// <summary>
        /// Subscribes to the spell input messages. Called by <see cref="PlayerController"/>.
        ///
        /// <para><b>Only the two keys this component owns.</b> <c>T</c> (<c>keyMagic</c>) is deliberately
        /// absent: it fires the equipped magic <i>weapon</i>, which has its own controller
        /// (<c>MagicWeaponController</c>) and its own gate — a different slice. Its message and broker
        /// exist (the input plumbing is complete), but subscribing here would mean storing a held state
        /// nothing reads, which is the "inert fix" shape this project keeps paying for.</para>
        /// </summary>
        public void BindInput(
            ISubscriber<SpellCastMessage> spellCastSubscriber,
            ISubscriber<SpellHotkeyMessage> spellHotkeySubscriber)
        {
            // Construct is called once per injection, and the player GameObject is injected twice in the
            // shipped scene (RegisterComponent AND autoInjectGameObjects). Dispose before re-subscribing
            // or every key press arrives twice — see PlayerController.Construct for the full story.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();

            // C — AS3 ctr.keyDef. Both edges: the cast clears the key, so the release edge must reach us
            // to avoid latching a stale "held" across a release.
            spellCastSubscriber.Subscribe(message => _defHeld = message.IsHeld).AddTo(_disposables);

            // Z/X — AS3 ctr.keySpell1/2 (slots 3-4 are unbound).
            spellHotkeySubscriber.Subscribe(message =>
            {
                if (message.Slot < 1 || message.Slot > HotkeySlots) return;
                _hotkeyHeld[message.Slot - 1] = message.IsHeld;
            }).AddTo(_disposables);
        }

        /// <summary>Puts the spell tick on the sim clock. Safe to call twice.</summary>
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

        // ── Public API (for the inventory UI and the developer console) ──────────────────────────────

        /// <summary>
        /// AS3 <c>Invent.addSpell</c> — put a spell in the player's collection. Returns the existing
        /// instance for an id already known, or null when the id is not a spell.
        /// </summary>
        public Spell LearnSpell(string spellId) => _book?.GetOrAdd(spellId);

        /// <summary>
        /// AS3 <c>UnitPlayer.changeSpell</c> (<c>:3850-3863</c>) — <b>a toggle</b>: selecting the spell
        /// already selected clears it. This is what the inventory's <c>useItem</c> dispatch calls.
        /// </summary>
        public Spell SelectSpell(string spellId) => _book?.Select(spellId);

        // ── ISimTickable ─────────────────────────────────────────────────────────────────────────────

        public void SimTick(int tickIndex) => Step();

        private void FixedUpdate()
        {
            if (_registered) return;

            if (!_warnedLegacy)
            {
                _warnedLegacy = true;
                Debug.LogWarning(
                    "[PlayerSpellCaster] No SimLoop attached, so the spell sweep and the cast triggers " +
                    "run on Unity's FixedUpdate instead of the sim clock. Cooldowns and the cast cadence " +
                    "will differ from AS3's 30 Hz by the ratio of the fixed step to 1/30 s.");
            }

            Step();
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
            _disposables = null;

            // Without this SimLoop keeps a reference to a dead MonoBehaviour and ticks it forever, which
            // presents as "the sim gets slower the longer the session runs".
            if (_loop != null && _registered)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }

        /// <summary>
        /// One tick of AS3's spell half: the sweep, then the two triggers — <b>in that order</b>, because
        /// AS3's <c>step()</c> (cooldowns, <c>:1613-1616</c>) runs before <c>control()</c> (the keys,
        /// <c>:2226-2284</c>). Reversing them would let a spell be cast on the same tick its cooldown
        /// expired by one frame less than the oracle allows.
        /// </summary>
        private void Step()
        {
            // AS3's shield bleed (UnitPlayer.as:1298-1300: `if(shithp > 0) { shithp -= 0.05; }`).
            // It belongs to UnitPlayer.step(), which is this tick, and it runs BEFORE the sweep and the
            // triggers because AS3 puts it at :1298 — above both the sweep (:1613) and control()
            // (:2226). That ordering is observable: a shield cast this tick is therefore not decayed on
            // its own cast tick, only from the next one.
            //
            // Player-only, and the caster is only ever on the player, so the rate is the player's. The
            // four bosses that also set shithp keep theirs until destroyed — see SpellShield.Decay.
            //
            // Above the book guard on purpose: the bleed is a player rule, not a spell rule. Gating it
            // on `_book != null` would make an unwired caster leave a live shield decaying nowhere —
            // exactly the "permanent shield" shape this call was added to fix.
            if (_unitStats != null)
            {
                _unitStats.ShitHp = SpellShield.Decay(_unitStats.ShitHp);
            }

            if (_book == null) return;

            _book.StepAll();

            float aimX = 0f, aimY = 0f;
            if (_aimProvider != null)
            {
                Vector2 aim = _aimProvider();
                aimX = aim.x;
                aimY = aim.y;
            }

            StepDefKey(aimX, aimY);
            StepHotkeys(aimX, aimY);
        }

        /// <summary>
        /// The Def key — AS3 <c>UnitPlayer.as:2226-2247</c>. The guard and the alicorn substitution live
        /// in <see cref="SpellBook.DefKey"/>; what is here is only the port of the two things the oracle
        /// does <i>around</i> it: reading <c>World.w.alicorn</c>, and writing the key back.
        /// </summary>
        private void StepDefKey(float aimX, float aimY)
        {
            SpellTriggerOutcome outcome = _book.DefKey(_defHeld, _rat, AlicornFlag, aimX, aimY);

            // `this.ctr.keyDef = false;` — the cast failed, the spell has no prod, or there was no spell.
            // Written back unconditionally, because the oracle assigns it in every one of those branches.
            _defHeld = outcome.KeyStaysHeld;
        }

        /// <summary>
        /// The favourite-spell hotkeys — AS3 <c>UnitPlayer.as:2252-2284</c>. Note the two asymmetries the
        /// oracle has and this must not "unify": <b>no <c>rat</c> guard</b> (the loop sits outside the
        /// <c>if(this.rat == 0)</c> block that opens at <c>:2391</c>) and <b>no alicorn substitution</b>.
        /// </summary>
        private void StepHotkeys(float aimX, float aimY)
        {
            for (int i = 0; i < _hotkeyHeld.Length; i++)
            {
                if (!_hotkeyHeld[i]) continue;

                // `invent.fav[World.kolHK * 2 + _loc3_]` — a lookup, and a null favourite clears the key
                // (SpellBook.Hotkey does that half, given a null id).
                SpellTriggerOutcome outcome = _book.Hotkey(true, ResolveFavouriteSpellId(i + 1), aimX, aimY);
                _hotkeyHeld[i] = outcome.KeyStaysHeld;
            }
        }

        /// <summary>
        /// The id the slot's favourite points at, or null when the slot is unbound.
        ///
        /// <para>AS3 indexes <c>invent.fav</c> directly; the port keeps the same table as
        /// <see cref="GameInventory.FavoriteSlots"/>, which is keyed <i>id → slot</i>, so this is a
        /// reverse lookup. With no inventory (today's production state) every slot is unbound and the
        /// hotkeys are inert — the Def key is the working path.</para>
        /// </summary>
        private string ResolveFavouriteSpellId(int slot)
        {
            if (_inventory == null) return null;

            int favouriteIndex = FavouriteWeaponSlots * 2 + slot;
            foreach (var pair in _inventory.FavoriteSlots)
            {
                if (pair.Value == favouriteIndex) return pair.Key;
            }
            return null;
        }

        /// <summary>
        /// The world's alicorn flag, read once per tick rather than per member so the caster and the
        /// host cannot disagree within a tick. See <see cref="LiveSpellWorld.Alicorn"/> — it is false
        /// because the port has no such global.
        /// </summary>
        private bool AlicornFlag => _world != null && ((ISpellWorld)_world).Alicorn;

        /// <summary>
        /// The <c>AllData</c> lookup behind <see cref="SpellBook"/>'s factory — AS3's
        /// <c>invent.spells[id] = new Spell(...)</c> reads the row's attributes at construction.
        /// </summary>
        private Spell CreateSpell(string id)
        {
            if (_registry == null) return null;

            ItemDefinition def = _registry.Get<ItemDefinition>(ContentType.Item, id);
            if (def == null || !def.spellData.IsPopulated) return null;

            return new Spell(id, def.spellData, _host);
        }

        /// <summary>
        /// The tile query for the room the player is actually in — the same derivation
        /// <c>PlayerTelekinesisController.ResolveTileQuery</c> uses, and for the same reason: AS3's
        /// <c>loc.isLine</c> reads the <i>current</i> <c>loc</c>, which is swapped on every room
        /// transition, so a query captured once would freeze on the room the caster was built in.
        /// </summary>
        private ITileQueryService ResolveTileQuery()
        {
            RoomInstance room = _landMap != null ? _landMap.currentRoom : null;
            if (room == null) return null;

            if (_derivedTileQuery == null || !ReferenceEquals(_derivedRoom, room))
            {
                _derivedRoom = room;
                _derivedTileQuery = new UnifiedTileQueryService(room);
            }

            return _derivedTileQuery;
        }

        // ── ISpellCaster — the oracle→port mapping, one member per line ──────────────────────────────
        //
        // Every member below is a read or a write of a public port field. There is no arithmetic and no
        // branching here on purpose: the rules live in SpellCastRules, which is proved offline, and this
        // is the only layer that knows which port member answers which AS3 read.

        /// <summary>
        /// AS3 <c>Spell.player</c> (<c>Spell.as:76-81</c>) — true when the owner is the player. This
        /// component only ever exists on the player, so it is a constant <c>true</c>. An NPC caster would
        /// need its own <see cref="ISpellCaster"/>, which is the point of the interface.
        /// </summary>
        bool ISpellCaster.IsPlayer => true;

        /// <inheritdoc/>
        int ISpellCaster.Rat => _rat;

        /// <summary>AS3 <c>World.w.pers.spellsPoss</c> (<c>Pers.as:423</c>).</summary>
        int ISpellCaster.SpellsPossible => _stats != null ? _stats.spellsPoss : 1;

        /// <inheritdoc/>
        bool ISpellCaster.AtkPossible => _atkPoss;

        /// <summary>
        /// AS3 <c>gg.invent.weapons[id].respect == 1</c> (<c>Spell.as:198</c>) — the spell's matching
        /// weapon is hidden. <c>1</c> is <see cref="WeaponRespect.Hidden"/>, which is the value the
        /// oracle's literal means (see the enum's own numbering).
        ///
        /// <para><b>False when there is no inventory or no such weapon</b>, which is the correct
        /// fallback: AS3 reaches this only for a weapon the player owns, and a missing row would throw
        /// there. The port's production state has no inventory at all, so the gate is currently inert —
        /// recorded, not silently different.</para>
        /// </summary>
        bool ISpellCaster.WeaponRespect(string spellId)
        {
            if (_inventory == null || string.IsNullOrEmpty(spellId)) return false;
            if (!_inventory.Weapons.TryGetValue(spellId, out GameWeaponInstance weapon) || weapon == null)
                return false;
            return weapon.Respect == WeaponRespect.Hidden;
        }

        /// <summary>AS3 <c>owner.mana</c> (<c>Unit.as:138</c>) — the regenerating budget.</summary>
        float ISpellCaster.Mana => _stats != null ? _stats.MagicMana : 0f;

        /// <summary>AS3 <c>World.w.pers.manaHP</c> (<c>Pers.as:141</c>) — the mana organ, not the budget.</summary>
        float ISpellCaster.ManaHp => _stats != null ? _stats.manaHp : 0f;

        /// <summary>AS3 <c>Pers.allDManaMult</c> (<c>Pers.as:227</c>, default 1).</summary>
        float ISpellCaster.AllDManaMult => _stats != null ? _stats.allDManaMult : 1f;

        /// <summary>
        /// AS3 <c>gg.pers.warlockDManaMult</c> (<c>Pers.as:323</c>, default 1). Read <b>only</b> at the
        /// spend, never in the check — see <see cref="SpellCastRules.ManaSpend"/>.
        /// </summary>
        float ISpellCaster.WarlockDManaMult => _stats != null ? _stats.warlockDManaMult : 1f;

        /// <summary>AS3 <c>Pers.spellDown</c> (<c>Pers.as:427</c>, default 1) — scales the cooldown.</summary>
        float ISpellCaster.SpellDown => _stats != null ? _stats.spellDown : 1f;

        /// <summary>AS3 <c>Unit.spellPower</c> (<c>Unit.as:316</c>, default 1).</summary>
        float ISpellCaster.SpellPower => _stats != null ? _stats.spellPower : 1f;

        /// <summary>AS3 <c>Pers.telePower</c> (<c>Pers.as:247</c>, default 1) — replaces the above for a player's tele spell.</summary>
        float ISpellCaster.TelePower => _stats != null ? _stats.telePower : 1f;

        /// <summary>
        /// Both halves of AS3 <c>UnitPlayer.manaSpell</c> (<c>:1710-1719</c>), forwarded to the port's
        /// existing <c>IManaSource.SpendMana</c> — the same seam the magic-weapon path already uses, so
        /// there is one mana-spend implementation rather than two. <see cref="LiveSpellHost"/> sends each
        /// half with a zero in the other, and a zero half is a no-op there.
        /// </summary>
        void ISpellCaster.SpendMana(float poolCost, float organCost)
        {
            if (_stats == null) return;
            ((IManaSource)_stats).SpendMana(poolCost, organCost);
        }

        /// <summary>
        /// AS3 <c>owner.magicX</c> (<c>Unit.as:340</c>) — the horn/magic mount point, in <b>Unity
        /// units</b>.
        ///
        /// <para><b>Unity units, not pixels, and that is deliberate.</b> This is the same quantity
        /// <c>MagicWeaponController</c> assigns to <c>State.X</c> from the same mount
        /// (<c>WeaponMounts.MagicHoldPoint</c>), so the two magic paths agree on their space. The one
        /// consumer that needs pixels — the line-of-sight ray — converts at its own boundary; see
        /// <see cref="LiveSpellWorld.IsBlocked"/>.</para>
        /// </summary>
        float ISpellCaster.MagicX => _mounts != null ? _mounts.MagicHoldPoint.x : 0f;

        /// <inheritdoc cref="ISpellCaster.MagicX"/>
        float ISpellCaster.MagicY => _mounts != null ? _mounts.MagicHoldPoint.y : 0f;

        /// <summary>
        /// AS3 <c>owner.addEffect(id, value)</c> — forwarded to the port's existing
        /// <c>UnitStats.Effects.AddEffect</c>, which is where <c>sp_slow</c>'s <c>inhibitor</c> already
        /// lands. The duration/announce defaults are the port's own, which is the honest choice: AS3's
        /// <c>addEffect</c> overloads differ and the spell path passes only two arguments.
        /// </summary>
        void ISpellCaster.AddEffect(string effectId, float value)
        {
            _unitStats?.Effects.AddEffect(effectId, value);
        }

        /// <summary>AS3 <c>owner.shithp = value</c> (<c>Spell.as:314-318</c>).</summary>
        void ISpellCaster.SetShitHp(float value)
        {
            if (_unitStats != null) _unitStats.ShitHp = value;
        }

        /// <summary>AS3 <c>World.w.pers.alicornShitHP</c> (<c>Pers.as:467</c>, default 2000).</summary>
        float ISpellCaster.AlicornShitHp => _stats != null ? _stats.alicornShitHP : 2000f;

        /// <summary>
        /// AS3 <c>gg.t_cryst</c> (<c>UnitPlayer.as:147</c>). <b>Per-player state, not per-spell</b> — the
        /// second crystal cast inside the window is the one that sets <c>est = 2</c>, which is why it
        /// cannot live on <see cref="Spell"/>.
        /// </summary>
        int ISpellCaster.CrystalCooldown
        {
            get => _crystCooldown;
            set => _crystCooldown = value;
        }

        // ── Test / debug seams for the three fields the port has no producer for ─────────────────────
        //
        // Exposed as methods rather than public fields so the mapping above stays read-only where the
        // oracle reads. These are the hooks slices 5 (atkPoss) and the rat-potion effect will call.

        /// <summary>Sets AS3 <c>gg.rat</c>. Producer owed — see the field's remarks.</summary>
        public void SetRat(int rat) => _rat = rat;

        /// <summary>Sets AS3 <c>gg.atkPoss</c>. Producer owed — see the field's remarks.</summary>
        public void SetAtkPossible(bool possible) => _atkPoss = possible;
    }
}
