using System;
using System.Collections.Generic;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// What one pass of a trigger loop decided — the port of the two <c>ctr.key*</c> blocks in
    /// <c>UnitPlayer.control()</c> (<c>UnitPlayer.as:2226-2247</c> and <c>:2252-2284</c>).
    ///
    /// <para><b>Why a struct and not a <c>bool</c>.</b> The oracle does two separable things per pass:
    /// it may cast, and it may clear the key that asked for the cast. Returning only "did it cast"
    /// would lose the second half, and that half is the difference between "one press, one cast" and
    /// "the button is stuck down" — which is the entire reason <c>prod</c> exists.</para>
    /// </summary>
    public readonly struct SpellTriggerOutcome
    {
        /// <summary>
        /// True when a <see cref="Spell.Cast"/> call was made. False when the guard did not fire (key
        /// not held, or <c>rat != 0</c>) <i>and</i> when it fired but found no spell to cast — in both
        /// cases <see cref="Cast"/> is meaningless and <see cref="KeyStaysHeld"/> is false.
        /// </summary>
        public readonly bool Attempted;

        /// <summary>The cast's result. Only meaningful when <see cref="Attempted"/>.</summary>
        public readonly SpellCastResult Cast;

        /// <summary>
        /// What the calling key becomes after this pass. False means the oracle cleared it, so the
        /// caller must not fire again until the key is released and pressed anew.
        /// </summary>
        public readonly bool KeyStaysHeld;

        internal SpellTriggerOutcome(bool attempted, in SpellCastResult cast, bool keyStaysHeld)
        {
            Attempted = attempted;
            Cast = cast;
            KeyStaysHeld = keyStaysHeld;
        }
    }

    /// <summary>
    /// The player's spell collection and selection — the port of <c>Invent.spells</c>
    /// (<c>Invent.as:33</c>, <c>:73</c>, <c>addSpell</c> at <c>:1018-1037</c>), the per-tick
    /// <c>step()</c> sweep (<c>UnitPlayer.as:1613-1616</c>), the selection toggle <c>changeSpell</c>
    /// (<c>:3850-3863</c>) and the two cast triggers in <c>control()</c>.
    ///
    /// <para><b>This is the piece that answers "who calls <c>Spell.Step()</c>".</b> The plan left that
    /// open, and the answer is not a component but this loop: AS3 walks <b>every</b> spell the player
    /// owns each tick, not just the selected one, so an unselected spell's cooldown keeps running down
    /// while it sits in the inventory. Getting that wrong is invisible until a player selects a spell
    /// they cast a minute ago and finds it still on cooldown.</para>
    ///
    /// <para><b>No Unity type appears here</b>, so the whole trigger path runs offline. The spells are
    /// built by an injected factory rather than by naming <c>ItemDefinition</c>, which keeps the
    /// collection free of the data layer and lets a test hand in spies.</para>
    /// </summary>
    public sealed class SpellBook
    {
        /// <summary>
        /// The id AS3 substitutes when the player is in alicorn mode — a literal in the oracle
        /// (<c>UnitPlayer.as:2234</c>: <c>this.currentSpell = this.invent.spells["sp_mshit"]</c>).
        /// </summary>
        public const string AlicornSpellId = "sp_mshit";

        private readonly Func<string, Spell> _factory;

        // The dictionary is the oracle's `invent.spells` (a string-keyed collection). The list exists
        // only so the per-tick sweep has a stable order: AS3's `for each` over an Array used as a
        // dictionary has no specified order, and `step()` is a pure decrement, so the order is not
        // observable — but an unordered enumeration would make a test's failure output non-reproducible.
        private readonly Dictionary<string, Spell> _byId = new Dictionary<string, Spell>(StringComparer.Ordinal);
        private readonly List<Spell> _order = new List<Spell>();

        /// <param name="factory">
        /// Builds the <see cref="Spell"/> for an id, or returns <c>null</c> when the id is not a spell
        /// the player can have. Called at most once per id.
        /// </param>
        public SpellBook(Func<string, Spell> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// AS3 <c>currentSpell</c> (<c>UnitPlayer.as:137</c>) — the spell the Def key will cast. Null
        /// when nothing is selected, which is also the state after the selection is toggled off.
        /// </summary>
        public Spell Current { get; private set; }

        /// <summary>How many spells the book holds. AS3 has no equivalent; used by tests and the HUD.</summary>
        public int Count => _order.Count;

        /// <summary>The spells, in insertion order. Read-only — mutate through <see cref="GetOrAdd"/>.</summary>
        public IReadOnlyList<Spell> Spells => _order;

        /// <summary>
        /// AS3 <c>Invent.addSpell</c> (<c>:1018-1037</c>): return the existing spell for
        /// <paramref name="id"/> or build and keep a new one. Returns <c>null</c> for an empty id
        /// (<c>:1020-1023</c>) or when the factory declines it.
        ///
        /// <para>The oracle's second guard (<c>if(_loc2_ == null) return null;</c>, <c>:1030-1033</c>)
        /// is unreachable there — <c>new Spell(...)</c> cannot be null — but it is reachable here,
        /// because the factory is the port's stand-in for the <c>AllData</c> lookup and an id with no
        /// row is a real case.</para>
        /// </summary>
        public Spell GetOrAdd(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (_byId.TryGetValue(id, out Spell existing)) return existing;

            Spell created = _factory(id);
            if (created == null) return null;

            _byId[id] = created;
            _order.Add(created);
            return created;
        }

        /// <summary>Looks a spell up without creating one — AS3's <c>this.spells[param1]</c> read.</summary>
        public bool TryGet(string id, out Spell spell)
        {
            if (string.IsNullOrEmpty(id))
            {
                spell = null;
                return false;
            }
            return _byId.TryGetValue(id, out spell);
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.changeSpell</c> (<c>:3850-3863</c>) — <b>a toggle, not a set</b>:
        /// re-selecting the spell already selected clears the selection.
        ///
        /// <para>Two faithful details that a "clean" setter would lose. The comparison is on the
        /// <i>object</i> (<c>currentSpell == spells[id]</c>, reference equality), which is the same
        /// thing as comparing ids here because the book hands out one instance per id. And an
        /// <b>unknown id clears the selection</b> rather than being ignored: <c>spells[id]</c> is
        /// <c>undefined</c>, which is neither equal to the current spell nor truthy, so
        /// <c>currentSpell = undefined</c>. The oracle relies on that — <c>Invent.as:849-852</c> calls
        /// <c>changeSpell("")</c> to deselect a spell whose weapon just became hidden.</para>
        ///
        /// <para>The <c>usedSpell</c> message (<c>:3860-3863</c>) is <b>not</b> sent from here: it is
        /// player feedback, and the caller is the one that knows whether the selection was user-driven
        /// (<c>param2</c>). Show it when this returns non-null.</para>
        /// </summary>
        /// <returns>The new <see cref="Current"/>.</returns>
        public Spell Select(string id)
        {
            Spell target = TryGet(id, out Spell found) ? found : null;

            // ReferenceEquals, not a null check: `currentSpell == undefined` is false in AS3 when
            // currentSpell holds an object, so an unknown id assigns undefined (= null here) rather
            // than toggling. ReferenceEquals reproduces both branches exactly, including the
            // null-vs-null case (nothing selected, unknown id -> still nothing selected).
            Current = ReferenceEquals(Current, target) ? null : target;
            return Current;
        }

        /// <summary>
        /// AS3 <c>UnitPlayer.step()</c> (<c>:1613-1616</c>): <b>every</b> spell ticks down, selected or
        /// not. Called once per simulation tick.
        /// </summary>
        public void StepAll()
        {
            for (int i = 0; i < _order.Count; i++)
            {
                _order[i].Step();
            }
        }

        /// <summary>
        /// The Def-key trigger — AS3 <c>UnitPlayer.as:2226-2247</c>, inside
        /// <c>if(this.ctr.keyDef &amp;&amp; this.rat == 0)</c>.
        ///
        /// <para>Three things in the oracle's order, each of which a rewrite tends to reorder:</para>
        /// <list type="number">
        /// <item><description>the guard is <b>both</b> conditions — a held key while
        /// <paramref name="rat"/> is non-zero does nothing at all and leaves the key held;</description></item>
        /// <item><description>alicorn mode <b>overwrites</b> the selection with
        /// <c>sp_mshit</c> — a direct assignment, not <see cref="Select"/>, so it never toggles the
        /// player's own choice away. It is also a <i>lookup</i>: a player who does not own
        /// <c>sp_mshit</c> gets null and the key is cleared;</description></item>
        /// <item><description>the key is cleared when the cast failed <b>or</b> the spell has no
        /// <c>prod</c> — two separate <c>if</c>s, so a <c>prod</c> spell whose cast is refused still
        /// releases the key.</description></item>
        /// </list>
        ///
        /// <para><b>Not modelled:</b> <c>this.sats.clearAll()</c> (<c>:2228-2231</c>), which empties the
        /// VATS queue. The port has no VATS, so there is no queue to clear — see the same note on
        /// <c>CharacterStats.PrecisionMultiplier</c>.</para>
        /// </summary>
        public SpellTriggerOutcome DefKey(bool held, int rat, bool alicorn, float aimX, float aimY)
        {
            if (!held || rat != 0) return new SpellTriggerOutcome(false, default, held);

            if (alicorn)
            {
                // A lookup, deliberately: `invent.spells["sp_mshit"]` does not create the spell.
                Current = TryGet(AlicornSpellId, out Spell shield) ? shield : null;
            }

            return CastOrClear(Current, aimX, aimY);
        }

        /// <summary>
        /// The favourite-spell hotkey trigger — AS3 <c>UnitPlayer.as:2252-2284</c>.
        ///
        /// <para><b>Two asymmetries with <see cref="DefKey"/>, both in the oracle and both easy to
        /// "unify" away.</b> There is <b>no <c>rat</c> guard</b> — the loop sits outside the
        /// <c>if(this.rat == 0)</c> block that opens at <c>:2391</c>, so a transformed player can still
        /// fire a hotkeyed spell. And there is <b>no alicorn substitution</b>: the spell comes from
        /// <c>invent.fav</c>, so alicorn mode does not reroute it to <c>sp_mshit</c>.</para>
        ///
        /// <para>A missing favourite (<c>fav[...] == null</c>, <c>:2256</c>) and an unowned spell
        /// (<c>:2264-2277</c>) both clear the key, so an unbound hotkey cannot latch.</para>
        /// </summary>
        /// <param name="spellId">The id the slot's favourite points at, or null/empty for an unbound slot.</param>
        public SpellTriggerOutcome Hotkey(bool held, string spellId, float aimX, float aimY)
        {
            if (!held) return new SpellTriggerOutcome(false, default, held);

            // `_loc5_ = this.invent.spells[this.invent.fav[...]]` — a lookup, like the Def key's.
            Spell spell = TryGet(spellId, out Spell found) ? found : null;
            return CastOrClear(spell, aimX, aimY);
        }

        /// <summary>
        /// The shared tail of both triggers: cast, then decide the key.
        ///
        /// <para>A null spell clears the key — that is the oracle's <c>else { ctr.key = false; }</c> in
        /// both blocks, and it is what stops an unbound slot or an unowned spell from re-trying every
        /// tick forever.</para>
        /// </summary>
        private SpellTriggerOutcome CastOrClear(Spell spell, float aimX, float aimY)
        {
            if (spell == null) return new SpellTriggerOutcome(false, default, false);

            SpellCastResult result = spell.Cast(aimX, aimY);

            // `if(!cast(...)) key = false; if(!prod) key = false;` — the conjunction, not the cast's
            // own answer: a successful cast of a non-prod spell still releases the key.
            bool stays = result.Cast && spell.Produces;
            return new SpellTriggerOutcome(true, result, stays);
        }
    }
}
