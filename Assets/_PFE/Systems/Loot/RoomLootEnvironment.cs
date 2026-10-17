using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The one production implementation of <see cref="ILootEnvironment"/> — AS3's <c>loc</c>,
    /// <c>loc.land</c> and <c>World.w.invent</c>, read together by <c>LootGen</c>.
    ///
    /// <para><b>Why this exists.</b> <c>LootContext.Env</c> was never set by either sink, so every roll in
    /// the shipping game ran with <c>biom 0</c>, no <c>itemsTip</c>, no act id and
    /// <c>LacksWeapon</c> always true. Four of the roller's branches read those: the <c>bibl</c> book roll
    /// (<c>LootGen.as:649</c>), the <c>minst</c> data item (<c>:652</c>), the biome-gated arms, the
    /// <c>safe</c> bloat ambush (<c>:698</c>) and the four <c>specweap</c> unique-weapon candidates
    /// (<c>:745-755</c>). A branch that can never be taken looks exactly like a branch that is not
    /// ported, which is why this is one adapter rather than six inline literals.</para>
    ///
    /// <para><b>It takes primitives, not a <c>RoomInstance</c>.</b> The loot namespace must not depend on
    /// the map namespace — the dependency runs the other way (<c>ContainerPropPresenter</c> is a map type
    /// that uses the loot namespace). Each sink reads its own room's fields and passes them in, which also
    /// keeps this type constructible in an offline fixture with no room at all.</para>
    /// </summary>
    public sealed class RoomLootEnvironment : ILootEnvironment
    {
        /// <inheritdoc />
        public int Biom { get; }

        /// <inheritdoc />
        public string ItemsTip { get; }

        /// <inheritdoc />
        public string ActId { get; }

        /// <inheritdoc />
        public bool LandRnd { get; }

        /// <inheritdoc />
        public bool HasProb { get; }

        private readonly Func<string, bool> _lacksWeapon;

        /// <param name="biom">
        /// AS3 <c>loc.land.act.biom</c>. A <b>negative</b> value is the port's "unknown" sentinel
        /// (<c>LocationEnemyContext.NoBiome</c>) and is read as <c>0</c>, because AS3 has no negative
        /// biome: a land whose <c>&lt;land&gt;</c> row carries no <c>biom</c> attribute has
        /// <c>biom = 0</c> (<c>LandAct.as:30,115-117</c>). Treating the sentinel as a real biome would
        /// make every biome-gated arm fail in a room that simply never had the field.
        /// </param>
        /// <param name="itemsTip">
        /// AS3 <c>loc.itemsTip</c> = the room's <c>&lt;options items="…"&gt;</c>
        /// (<c>Location.as:512</c>). <b>The port does not import this attribute yet</b>, so callers pass
        /// the empty string and the <c>bibl</c> book roll is not taken. Eleven authored rooms use
        /// <c>items="bibl"</c>, so this is a real (if small) content gap rather than a dead field — see
        /// <see cref="RoomLootEnvironment"/>'s callers for the recorded follow-up.
        /// </param>
        /// <param name="actId">AS3 <c>loc.land.act.id</c> — the current land's id.</param>
        /// <param name="landRnd">AS3 <c>World.w.land.rnd</c> — a procedural land.</param>
        /// <param name="hasProb">
        /// AS3 <c>param1.prob != null</c> — the room was built as a prob room rather than coming from the
        /// land grid. The port's discriminator is a non-empty <c>RoomInstance.probId</c>, which is written
        /// only by <c>ProbRoomBuilder</c> (<c>ProbTransition.AdmitsGridStep</c> uses the same test).
        /// </param>
        /// <param name="lacksWeapon">
        /// AS3 <c>invent.weapons[id] == null || weapons[id].variant == 0</c>. <c>null</c> means "cannot be
        /// answered" and is read as <b>false</b> — i.e. "the player already has it", which is the value
        /// that stops the <c>specweap</c> table handing out a duplicate unique weapon. Assuming the
        /// opposite (<c>true</c>) would hand out <c>lsword^1</c>, <c>antidrak^1</c>, <c>quick^1</c> and
        /// <c>mlau^1</c> on every roll in a session with no inventory wired. This is an assumption, not a
        /// fact, and the callers say so where they cannot answer it.
        /// </param>
        public RoomLootEnvironment(
            int biom,
            string itemsTip,
            string actId,
            bool landRnd,
            bool hasProb,
            Func<string, bool> lacksWeapon = null)
        {
            Biom = biom < 0 ? 0 : biom;
            ItemsTip = itemsTip ?? string.Empty;
            ActId = actId ?? string.Empty;
            LandRnd = landRnd;
            HasProb = hasProb;
            _lacksWeapon = lacksWeapon;
        }

        /// <inheritdoc />
        public bool LacksWeapon(string weaponId) => _lacksWeapon != null && _lacksWeapon(weaponId);
    }
}
