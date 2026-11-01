using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The sound a map object makes when its visual state changes — the port of AS3
    /// <c>fe.loc.Box</c>'s four sound attributes and the three places they are played
    /// (<c>Box.as:238-252</c> for the parse, <c>:508-532</c> for open/close, <c>:850-853</c> for die).
    ///
    /// <para><b>The port had none of this.</b> A crate opened in silence, and so did a door: nothing in
    /// the map layer read <c>@open</c>, <c>@close</c> or <c>@die</c>, and <c>MapObjectDefinition</c>
    /// carries no sound field at all. The ids are nonetheless already in the shipped data —
    /// <c>chest.asset</c> has <c>open: metal_open</c> in its <c>legacyAttributes</c> — and every id the
    /// oracle references already resolves in <c>SoundCatalog</c> (<c>metal_open</c>, <c>wood_open</c>,
    /// <c>safe_open</c>, <c>small_open</c>, <c>metal_door_open</c>, <c>wood_door_open</c>, <c>button</c>,
    /// <c>pole_off</c>, <c>big_door_close</c>). So this was a wiring gap, not a content gap — and no
    /// importer run is needed, because <see cref="MapObjectDefinition.legacyAttributes"/> already holds
    /// the raw row.</para>
    ///
    /// <para><b>The <c>testLoot</c> gate is asymmetric, and that is the oracle, not an oversight here.</b>
    /// <c>Box.setVisState</c> tests <c>!World.w.testLoot</c> on the <b>open</b> branch only
    /// (<c>:510</c>); the <b>close</b> branch (<c>:514</c>) has no such test. A loot-test sweep silences
    /// the crates it opens but not the doors it shuts. Modelled as written, with a fixture pinning the
    /// asymmetry so a later "cleanup" cannot make it symmetric.</para>
    /// </summary>
    public static class MapObjectSoundRule
    {
        /// <summary>AS3 <c>Box.sndOpen</c> — <c>node.@open</c> (<c>Box.as:242</c>).</summary>
        public const string OpenAttribute = "open";

        /// <summary>AS3 <c>Box.sndClose</c> — <c>node.@close</c> (<c>Box.as:246</c>).</summary>
        public const string CloseAttribute = "close";

        /// <summary>AS3 <c>Box.sndDie</c> — <c>node.@die</c> (<c>Box.as:250</c>).</summary>
        public const string DieAttribute = "die";

        /// <summary>AS3 <c>Box.sndFall</c> — <c>node.@fall</c> (<c>Box.as:238</c>).</summary>
        public const string FallAttribute = "fall";

        /// <summary>
        /// The id to play when this prop opens, or <c>null</c> when it has none.
        ///
        /// <para><c>null</c> and <c>""</c> are the same answer here because the oracle's guard is
        /// <c>this.sndOpen != ""</c> (<c>Box.as:510</c>) and an absent attribute leaves the field at its
        /// <c>""</c> initialiser (<c>:104</c>). Returning <c>null</c> lets a caller write
        /// <c>if (id != null)</c> without also having to think about empty.</para>
        /// </summary>
        public static string OpenSound(List<MapObjectAttributeData> attributes)
            => Resolve(attributes, OpenAttribute);

        /// <summary>AS3 <c>Box.sndClose</c> — played when a door shuts (<c>Box.as:514-517</c>).</summary>
        public static string CloseSound(List<MapObjectAttributeData> attributes)
            => Resolve(attributes, CloseAttribute);

        /// <summary>AS3 <c>Box.sndDie</c> — played once, when the prop is destroyed (<c>Box.as:850-853</c>).</summary>
        public static string DieSound(List<MapObjectAttributeData> attributes)
            => Resolve(attributes, DieAttribute);

        /// <summary>
        /// AS3 <c>Box.sndFall</c> — a <b>prop</b> landing (<c>Box.as:1074-1077</c>). Not to be confused
        /// with <c>Loot.sndFall</c>, which is a different ladder over the <i>item</i> row
        /// (<c>Systems/Loot/LootFallRules.ResolveFallSoundId</c>); a prop's is read off the map object row
        /// and gated on <c>sndOn</c> besides. The two are in different assemblies, which is why that one
        /// is named in prose rather than as a <c>cref</c>.
        /// </summary>
        public static string FallSound(List<MapObjectAttributeData> attributes)
            => Resolve(attributes, FallAttribute);

        /// <summary>
        /// Whether an open should be audible — <c>sndOpen != "" &amp;&amp; !World.w.testLoot</c>
        /// (<c>Box.as:510</c>). Split out so a loot-test sweep is a parameter and not an edit here.
        /// </summary>
        public static bool PlaysOnOpen(string soundId, bool testLoot)
            => !string.IsNullOrEmpty(soundId) && !testLoot;

        /// <summary>
        /// Whether a close should be audible — <c>sndClose != ""</c> (<c>Box.as:514</c>). Deliberately
        /// <b>not</b> gated on <c>testLoot</c>: see the class remarks.
        /// </summary>
        public static bool PlaysOnClose(string soundId)
            => !string.IsNullOrEmpty(soundId);

        /// <summary>
        /// Read one attribute through <see cref="MapObjectDataUtility.GetAttribute"/> rather than a
        /// second loop, so the case-insensitive first-wins rule here cannot drift from the rule every
        /// other map-object attribute reader uses.
        /// </summary>
        private static string Resolve(List<MapObjectAttributeData> attributes, string key)
        {
            if (attributes == null) return null;

            string value = MapObjectDataUtility.GetAttribute(attributes, key, null);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
