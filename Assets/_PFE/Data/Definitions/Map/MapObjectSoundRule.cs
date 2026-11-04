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
        /// Whether a <b>destruction</b> should be audible — <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c>
        /// (<c>Box.as:822</c>) wrapping the <c>Snd.ps(this.sndDie, X, Y)</c> in <c>Box.die()</c>
        /// (<c>Box.as:850-853</c>). This predicate is the <i>conjunction</i> of the two: the band and
        /// the <c>sndDie != ""</c> check that sits inside it.
        ///
        /// <para><b>What <c>param1</c> is.</b> <c>Box.die(param1 = 0)</c> takes the <i>damage type</i> of
        /// the hit that killed it, and the whole debris + sound block sits inside that test — so a prop
        /// destroyed by an exotic damage type (plasma, fire, EMP: indices 10 and up) breaks in
        /// <b>silence</b> and throws no shards. The bound is the same <c>10</c> that
        /// <c>Unit.D_*</c> uses for the first non-physical type.</para>
        ///
        /// <para><b>Note what is <i>not</i> in the band.</b> The <c>noiseDie</c> shout is emitted
        /// <i>after</i> the block closes (<c>Box.as:855-858</c>), so a broken prop still alerts at
        /// 800 px whatever killed it. See <c>SecurityRuntime.NotifyBoxDestroyed</c>.</para>
        ///
        /// <para><b>Why this is a rule and not two lines inside the presenter.</b> The presenter is a
        /// <c>MonoBehaviour</c> with a room, a collider and a renderer; a gate written there cannot be
        /// exercised offline. Every other box-sound gate in this class already lives here for that
        /// reason.</para>
        /// </summary>
        /// <param name="soundId">The resolved <c>@die</c> id, or null/empty when the prop has none.</param>
        /// <param name="damageType">The killing hit's damage-type index — AS3's <c>param1</c>.</param>
        public static bool PlaysOnDie(string soundId, int damageType)
            => !string.IsNullOrEmpty(soundId) && damageType >= 0 && damageType < DieSoundMaxDamageType;

        /// <summary>
        /// The exclusive upper bound on the damage type that still makes a die sound — the literal
        /// <c>10</c> in <c>if(param1 &gt;= 0 &amp;&amp; param1 &lt; 10)</c> (<c>Box.as:822</c>).
        /// </summary>
        public const int DieSoundMaxDamageType = 10;

        // ── The landing sample (`Box.as:1074-1108`) ────────────────────────────────────────────────
        //
        // These three live here, next to the attribute reader, rather than inside
        // `RoomObjectPhysicsLayer` where the sound is actually played. The reason is the one this
        // project keeps paying for: the layer cannot be run offline, so arithmetic written in it is
        // arithmetic no fixture can pin — and the unit conversion below is exactly the kind of
        // one-character slip that survives a review (px/second vs px/frame is a factor of 30, and the
        // sign flip is a factor of −1).

        /// <summary>
        /// The landing gate's speed threshold — <c>if(dy &gt; 5 …)</c> (<c>Box.as:1074</c>), in AS3's
        /// px per 30 Hz frame, positive downward. A slow settle is silent.
        /// </summary>
        public const float LandingSoundMinDy = 5f;

        /// <summary>The landing volume divisor — <c>Snd.ps(…, 0, dy / 15)</c> (<c>Box.as:1076</c>).</summary>
        public const float LandingVolumeDivisor = 15f;

        /// <summary>AS3's frame rate, for the px/second → px/frame conversion in the two rules below.</summary>
        public const float As3FramesPerSecond = 30f;

        /// <summary>
        /// <c>if(dy &gt; 5 &amp;&amp; this.sndFall &amp;&amp; this.sndOn)</c> (<c>Box.as:1074</c>).
        /// </summary>
        /// <param name="velocityYPerSecond">
        /// The prop's vertical velocity in the port's units — <b>px per second, positive up</b>. The
        /// conversion to AS3's px-per-frame-positive-down <c>dy</c> happens here and nowhere else.
        /// </param>
        /// <param name="soundId">The resolved <c>@fall</c> id, or null/empty when the prop has none.</param>
        /// <param name="soundOn">
        /// AS3 <c>Box.sndOn</c> — has this prop already landed once. False silences the first contact,
        /// which is what keeps a freshly built room from thudding in unison.
        /// </param>
        public static bool PlaysOnLanding(float velocityYPerSecond, string soundId, bool soundOn)
            => soundOn && !string.IsNullOrEmpty(soundId) && DownwardSpeedPerFrame(velocityYPerSecond) > LandingSoundMinDy;

        /// <summary>
        /// The landing sample's volume scale — <c>dy / 15</c> (<c>Box.as:1076</c>), with <c>dy</c> derived
        /// the same way <see cref="PlaysOnLanding"/> derives it.
        /// </summary>
        public static float LandingVolume(float velocityYPerSecond)
            => DownwardSpeedPerFrame(velocityYPerSecond) / LandingVolumeDivisor;

        /// <summary>
        /// The port's velocity (px/second, y-up) as AS3's <c>dy</c> (px/frame, positive <b>down</b>).
        /// Two conversions, both load-bearing: the frame rate and the sign.
        /// </summary>
        private static float DownwardSpeedPerFrame(float velocityYPerSecond)
            => -velocityYPerSecond / As3FramesPerSecond;


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
