using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// One entry of a loot pool — the port's stand-in for the anonymous objects
    /// <c>LootGen.init()</c> pushes into <c>arr[tip]</c> (<c>LootGen.as:45-112</c>).
    ///
    /// <para><b><c>Stage</c> and <c>Level</c> are nullable on purpose.</b> AS3's filter tests
    /// <c>_loc8_.st == null || _loc8_.st &lt;= gameStage</c> (<c>:138</c>) — "absent" is a third state
    /// that passes the gate, distinct from <c>0</c>, which would fail a stage-1 gate. Modelling them as
    /// non-nullable <c>0</c> silently drops every unstaged entry from every gated roll.</para>
    ///
    /// <para><b><c>Worth</c> is only populated for weapons</b>, from <c>&lt;com&gt;@worth</c>
    /// (<c>LootGen.as:49</c>). The worth gate is an <b>exact match</b> (<c>:138</c>,
    /// <c>param3 == _loc8_.worth</c>), not a threshold — see <c>05_VERIFICATION_AND_QUIRKS.md</c> §5 Q7.</para>
    /// </summary>
    public readonly struct LootPoolEntry
    {
        public readonly string Id;
        public readonly int? Stage;
        public readonly float Chance;
        public readonly int? Level;
        public readonly float? Worth;

        public LootPoolEntry(string id, float chance, int? stage = null, int? level = null, float? worth = null)
        {
            Id = id;
            Chance = chance;
            Stage = stage;
            Level = level;
            Worth = worth;
        }
    }

    /// <summary>
    /// The <c>arr</c> of <c>LootGen.init()</c> — every tip's candidate list, keyed by tip string.
    /// Injected rather than static so the roller stays pure and a test can supply a fixed pool.
    /// </summary>
    public interface ILootPoolProvider
    {
        /// <summary>Returns the pool for a tip, or <c>null</c> when the tip has none — AS3 <c>arr[tip] == null</c>.</summary>
        IReadOnlyList<LootPoolEntry> GetPool(string tip);
    }

    /// <summary>
    /// The <c>World.w.pers</c> perk flags the tables read. Five of the container branches and the money
    /// multipliers depend on them, so a roller that ignored them would silently under- or over-produce.
    /// </summary>
    public interface ILootPersFlags
    {
        /// <summary>AS3 <c>pers.freel</c> — the "free loader" perk: extra ammo/explosive rolls.</summary>
        bool Freel { get; }

        /// <summary>AS3 <c>pers.barahlo</c> — extra component rolls in <c>wbattle</c>.</summary>
        bool Barahlo { get; }

        /// <summary>AS3 <c>pers.capsMult</c> — scales <c>money</c> stacks (<c>LootGen.as:247</c>).</summary>
        float CapsMult { get; }

        /// <summary>AS3 <c>pers.bitsMult</c> — scales <c>bit</c> stacks (<c>LootGen.as:251</c>).</summary>
        float BitsMult { get; }

        /// <summary>AS3 <c>pers.difCapsMult</c> — the difficulty term on both money and bits.</summary>
        float DifCapsMult { get; }

        /// <summary>AS3 <c>pers.dropTre</c> — the treasure perk's gem chance on a unit drop (<c>Unit.as:4460-4466</c>).</summary>
        float DropTre { get; }
    }

    /// <summary>
    /// The per-run <c>@limit</c> counter store — AS3 <c>Game.getLimit</c>/<c>addLimit</c>
    /// (<c>Game.as:681-708</c>). Injected so the roller has no save-state dependency.
    /// </summary>
    public interface ILootLimitStore
    {
        int GetLimit(string key);
        void AddLimit(string key, int delta);
    }

    /// <summary>
    /// The room/level facts the tables branch on: the biome, the room's <c>itemsTip</c>, the location's
    /// script id, and whether the player already carries an upgraded weapon.
    /// </summary>
    public interface ILootEnvironment
    {
        /// <summary>AS3 <c>loc.land.act.biom</c> — <c>0</c> means the "home" biome (radcookie rolls).</summary>
        int Biom { get; }

        /// <summary>AS3 <c>loc.itemsTip</c> — <c>"bibl"</c> adds a book roll (<c>LootGen.as:275</c>).</summary>
        string ItemsTip { get; }

        /// <summary>AS3 <c>loc.land.act.id</c> — <c>"minst"</c> swaps the <c>term</c> table's data item.</summary>
        string ActId { get; }

        /// <summary>
        /// AS3 <c>World.w.land.rnd</c> — whether the land was procedurally generated. Half of the
        /// <c>safe</c> table's bloat-ambush guard (<c>LootGen.as:698</c>).
        /// </summary>
        bool LandRnd { get; }

        /// <summary>
        /// AS3 <c>param1.prob != null</c> on the location — i.e. the room came from the procedural
        /// generator rather than being hand-authored. The other half of the <c>safe</c> guard
        /// (<c>LootGen.as:698</c>): the ambush fires only in a generated room.
        /// </summary>
        bool HasProb { get; }

        /// <summary>
        /// Whether the player <b>lacks</b> a weapon (or lacks its upgraded variant) — AS3
        /// <c>World.w.invent.weapons[id] == null || weapons[id].variant == 0</c>. The <c>specweap</c>
        /// branch hands out a fixed unique weapon only when this is true.
        /// </summary>
        bool LacksWeapon(string weaponId);
    }

    /// <summary>
    /// The two side effects a container table can have that are <b>not</b> items: spawning a critter
    /// (<c>loc.createUnit("rat" …)</c>, <c>LootGen.as:531-547</c>) and the pip-boy open/close animation
    /// (<c>replic("full")</c>, <c>:689-693</c>).
    ///
    /// <para><b>Why this exists.</b> A pure <c>IReadOnlyList&lt;LootRoll&gt;</c> cannot express "and also
    /// a rat runs out". Leaving it out would silently make <c>fridge</c> and <c>food</c> drop fewer
    /// critters than the oracle — the exact "looks ported, isn't" failure this project keeps finding. So
    /// the effect is routed to an injected sink and the caller decides what to do with it.</para>
    /// </summary>
    public interface ILootSideEffects
    {
        /// <summary>AS3 <c>loc.createUnit(id, nx, ny, true)</c>.</summary>
        void CreateUnit(string unitId, float x, float y);

        /// <summary>AS3 <c>Interact.replic("full" | "empty")</c> — presentation only.</summary>
        void Replic(string state);
    }

    /// <summary>
    /// Everything a roll needs, passed explicitly — the deliberate replacement for
    /// <c>LootGen</c>'s <c>private static loc/nx/ny/lootBroken/is_loot</c> fields
    /// (<c>LootGen.as:14-22</c>), which are a re-entrancy bug rather than a design: a nested roll would
    /// overwrite the outer roll's state.
    ///
    /// <para>The class is mutable because the roller accumulates into <see cref="Results"/> — but every
    /// field it reads is supplied by the caller, so two rolls with the same context produce the same
    /// output.</para>
    /// </summary>
    public sealed class LootContext
    {
        /// <summary>The RNG stream. Required.</summary>
        public IRngService Rng;

        /// <summary>The pool provider (<c>arr</c>). Required.</summary>
        public ILootPoolProvider Pools;

        /// <summary>
        /// The item-row resolver. Required for any roll that can produce a stackable or a weapon — see
        /// <see cref="ILootItemCatalog"/>. A <c>null</c> catalog is tolerated (every item comes out at
        /// the caller's quantity with condition 1) but is a configuration bug, so
        /// <see cref="LootRoller"/> records it in <see cref="UnmatchedKeys"/> rather than hiding it.
        /// </summary>
        public ILootItemCatalog Catalog;

        /// <summary>The key being rolled — <c>"chest"</c>, <c>"raider"</c>. Used for provenance and reporting.</summary>
        public string TableKey = string.Empty;

        /// <summary>AS3 <c>lootBroken</c> — a broken container yields degraded contents (<c>LootGen.as:179-260</c>).</summary>
        public bool LootBroken;

        /// <summary>
        /// AS3 <c>allDif</c> — the <b>specific container's</b> lock difficulty, read by <c>safe</c> alone
        /// (<c>LootGen.as:316-329</c>). For an ordinary container the caller passes <b>50</b>, not 0
        /// (<c>Interact.as:2013</c>: <c>this.prize ? this.allDif : 50</c>).
        /// </summary>
        public float AllDif = 50f;

        /// <summary>AS3 <c>(owner as Unit).hero</c> — the boss tier; only the eight hero-gated rows read it.</summary>
        public int Hero;

        /// <summary>AS3 <c>loc.locDifLevel</c> — the room difficulty every table scales off.</summary>
        public int LocDifLevel;

        /// <summary>AS3 <c>loc.weaponLevel</c> — the weapon-tier gate (<c>LootGen.as:188</c>).</summary>
        public float WeaponLevel = 1f;

        /// <summary>AS3 <c>World.w.land.gameStage</c> — the story-stage gate (<c>LootGen.as:131</c>).</summary>
        public int GameStage;

        /// <summary>AS3 <c>World.w.land.lootLimit</c> = <c>landDifficulty + 3</c> (<c>Land.as:139</c>).</summary>
        public int LootLimit;

        /// <summary>Perk flags. <c>null</c> means "no perks" — every flag false, every multiplier 1.</summary>
        public ILootPersFlags Pers;

        /// <summary>Per-run <c>@limit</c> store. <c>null</c> disables the limit check.</summary>
        public ILootLimitStore Limits;

        /// <summary>Room/level facts. <c>null</c> means "plain room": biome 0, no itemsTip, no act id, no weapon knowledge.</summary>
        public ILootEnvironment Env;

        /// <summary>Non-item side effects. <c>null</c> drops them (the caller has been told).</summary>
        public ILootSideEffects SideEffects;

        /// <summary>The rolls produced so far. The roller appends; the caller reads.</summary>
        public readonly List<LootRoll> Results = new List<LootRoll>();

        /// <summary>
        /// Keys that were rolled but matched no table. AS3 is silent here (the <c>if/else if</c> chain
        /// simply falls through and <c>loot()</c> returns false), which is exactly why <c>cont="*"</c>
        /// produced a silently empty container for years (Q1). The port records them so the divergence is
        /// visible instead of invisible.
        /// </summary>
        public readonly List<string> UnmatchedKeys = new List<string>();

        /// <summary>The RNG stream, or a deterministic fallback is <b>not</b> provided — a missing RNG is a bug.</summary>
        public IRngService RequireRng() => Rng;

        /// <summary>AS3 <c>_loc7_</c> — the difficulty every table clamps at 20 (<c>LootGen.as:325</c>).</summary>
        public int EffectiveDif => LocDifLevel < 20 ? LocDifLevel : 20;

        public bool Freel => Pers != null && Pers.Freel;
        public bool Barahlo => Pers != null && Pers.Barahlo;
        public float CapsMult => Pers?.CapsMult ?? 1f;
        public float BitsMult => Pers?.BitsMult ?? 1f;
        public float DifCapsMult => Pers?.DifCapsMult ?? 1f;
        public float DropTre => Pers?.DropTre ?? 0f;

        public int Biom => Env?.Biom ?? 0;
        public string ItemsTip => Env?.ItemsTip ?? string.Empty;
        public string ActId => Env?.ActId ?? string.Empty;
        public bool LandRnd => Env != null && Env.LandRnd;
        public bool HasProb => Env != null && Env.HasProb;
        public bool LacksWeapon(string id) => Env == null || Env.LacksWeapon(id);
    }
}
