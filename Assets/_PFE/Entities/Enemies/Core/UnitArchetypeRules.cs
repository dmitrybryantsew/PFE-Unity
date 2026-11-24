using PFE.Entities.Enemies.Archetypes;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// The single source of truth for <b>which archetype behaviour a unit id carries</b> — the port's
    /// data-driven replacement for the deleted per-class <c>EnemyController</c> leaf subclasses.
    ///
    /// <para><b>Why this exists.</b> AS3 dispatches on the <i>class</i> — <c>UnitDron</c>,
    /// <c>UnitGutsy</c>, <c>UnitSlime</c> — and the port used to mirror that with one C# subclass per
    /// class (40+ controllers, most of which only set a <c>Kind</c>, an <c>AiFamily</c> or a single
    /// weapon id). But the port's spawner already composes the unit id deterministically from the
    /// same data AS3 read (<see cref="PFE.Systems.Map.UnitVariantResolver"/>), so the id alone
    /// answers every question the subclasses answered. The brains resolve their <c>Kind</c> from here
    /// inside <c>ApplyDefinitionTuning</c>, and the archetype controllers resolve their family
    /// facts (AI vocabulary, secondary weapon, griffin arm) the same way.</para>
    ///
    /// <para><b>Matching is by family prefix, not by table of every variant id</b> — <c>dron2</c>
    /// carries the same class facts as <c>dron</c>, exactly as AS3's inheritance does. A row whose
    /// id matches nothing keeps the family's default, which is the oracle's own declared default
    /// for that class.</para>
    ///
    /// <para><b>Everything here is a pure function of the unit id</b> so it is assertable offline
    /// and cannot drift with spawn order — no randomness, no scene state.</para>
    /// </summary>
    public static class UnitArchetypeRules
    {
        // ── Robot family (AS3 UnitAIRobot and the small robot classes) ────────────────────────────

        /// <summary>
        /// The robot kind a unit id carries — AS3 <c>UnitDron</c>, <c>UnitSpriteBot</c>,
        /// <c>UnitVortex</c>, <c>UnitRoller</c> and <c>UnitMsp</c> each set their own behaviour in
        /// their constructors; every other robot row is the plain <c>UnitAIRobot</c> shape
        /// (<see cref="RobotKind.Standard"/>).
        /// </summary>
        public static RobotKind RobotKindFor(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return RobotKind.Standard;
            if (unitId.StartsWith("dron", System.StringComparison.Ordinal)) return RobotKind.Drone;
            if (unitId.StartsWith("spritebot", System.StringComparison.Ordinal)) return RobotKind.SpriteBot;
            if (unitId.StartsWith("vortex", System.StringComparison.Ordinal)) return RobotKind.Vortex;
            if (unitId.StartsWith("roller", System.StringComparison.Ordinal)) return RobotKind.Roller;
            if (unitId.StartsWith("msp", System.StringComparison.Ordinal)) return RobotKind.Msp;
            return RobotKind.Standard;
        }

        /// <summary>
        /// The secondary <c>attack()</c> a robot carries — <c>UnitGutsy.as:28</c> builds
        /// <c>robofire</c> and <c>UnitSentinel.as:27</c> builds <c>robomlau</c> unconditionally in
        /// their constructors. Nothing else in the family has one.
        /// </summary>
        public static RobotSecondaryKind RobotSecondaryKindFor(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return RobotSecondaryKind.None;
            if (unitId.StartsWith("gutsy", System.StringComparison.Ordinal)) return RobotSecondaryKind.Flamer;
            if (unitId.StartsWith("sentinel", System.StringComparison.Ordinal)) return RobotSecondaryKind.Rocket;
            return RobotSecondaryKind.None;
        }

        /// <summary>
        /// Whether the id belongs to the <c>extends UnitAIRobot</c> group that reads the placement
        /// <c>ai</c> as <c>quiet</c> (<c>UnitAIRobot.as:49-52</c>) — <c>robobrain</c>, <c>protect</c>,
        /// <c>gutsy</c>, <c>sentinel</c>, <c>eqd</c> and the placed <c>robot</c> alias. The flying and
        /// rolling robots (<c>dron</c>, <c>spritebot</c>, <c>vortex</c>, <c>roller</c>, <c>msp</c>)
        /// extend <c>Unit</c> and read no <c>aiTip</c>, so they keep the generic vocabulary.
        /// </summary>
        public static bool UsesRobotAiFamily(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return false;
            return unitId.StartsWith("robobrain", System.StringComparison.Ordinal)
                || unitId.StartsWith("protect", System.StringComparison.Ordinal)
                || unitId.StartsWith("gutsy", System.StringComparison.Ordinal)
                || unitId.StartsWith("sentinel", System.StringComparison.Ordinal)
                || unitId == "robot"
                || unitId == "eqd";
        }

        // ── Flyer family (AS3 UnitBloat, UnitBat, UnitPhoenix, UnitBloatEmitter) ──────────────────

        /// <summary>
        /// The flyer kind a unit id carries — <c>ebloat</c> is the nest (<c>UnitBloatEmitter</c>),
        /// <c>bat</c>/<c>bloodwing</c> is the diving <c>UnitBat</c>, <c>phoenix</c> is the parked
        /// prop, and every <c>bloat</c> row is the drift-split <c>UnitBloat</c>.
        /// </summary>
        public static FlyerKind FlyerKindFor(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return FlyerKind.Bloat;
            if (unitId.StartsWith("ebloat", System.StringComparison.Ordinal)) return FlyerKind.Emitter;
            if (unitId.StartsWith("bat", System.StringComparison.Ordinal)) return FlyerKind.Bat;
            if (unitId.StartsWith("bloodwing", System.StringComparison.Ordinal)) return FlyerKind.Bat;
            if (unitId.StartsWith("phoenix", System.StringComparison.Ordinal)) return FlyerKind.Phoenix;
            return FlyerKind.Bloat;
        }

        // ── Beast family (AS3 UnitMonstrik, UnitHellhound, UnitSlime) ─────────────────────────────

        /// <summary>
        /// The beast kind a unit id carries — <c>hellhound</c> is the fast quadruped, the three
        /// slime ids are the momentum-free blob, and every critter row (rat, molerat, scorpions,
        /// tarakan) is the <c>UnitMonstrik</c> shape.
        /// </summary>
        public static BeastKind BeastKindFor(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return BeastKind.Critter;
            if (unitId.StartsWith("hellhound", System.StringComparison.Ordinal)) return BeastKind.Hound;
            if (unitId.StartsWith("slime", System.StringComparison.Ordinal)) return BeastKind.Slime;
            if (unitId.StartsWith("cryoslime", System.StringComparison.Ordinal)) return BeastKind.Slime;
            if (unitId.StartsWith("pinkslime", System.StringComparison.Ordinal)) return BeastKind.Slime;
            return BeastKind.Critter;
        }

        // ── Shared cross-family facts ──────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>true</c> only for <c>necros</c> — AS3 <c>UnitNecros extends Unit</c>, not
        /// <c>UnitRaider</c>, so it reads the placement <c>ai</c> as <c>stay</c> and nothing else
        /// (<c>UnitNecros.as:102</c>) even though the port routes it through the ArmedShooter
        /// controller for its weapon rolling.
        /// </summary>
        public static bool IsNecros(string unitId)
            => string.Equals(unitId, "necros", System.StringComparison.Ordinal);
    }
}
