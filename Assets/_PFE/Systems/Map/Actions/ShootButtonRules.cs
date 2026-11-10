using System.Globalization;

namespace PFE.Systems.Map.Actions
{
    /// <summary>Which of AS3 <c>Interact.command("dam")</c>'s three arms applies (<c>Interact.as:1744-1758</c>).</summary>
    public enum DamageVerbOutcome
    {
        /// <summary><c>if(this.expl) this.explosion();</c> — an explosive prop detonates.</summary>
        Explode,

        /// <summary><c>else if(this.knop &amp;&amp; this.action == 0) setAct("open",1); loc.prob.check();</c> — a one-shot switch.</summary>
        OpenOnce,

        /// <summary><c>else this.actOsn();</c> — the ordinary button press, which runs the object's own script.</summary>
        Activate,
    }

    /// <summary>
    /// The oracle-literal rules behind AS3's <b>"Кнопка, нажимается пулями"</b> — the button that is
    /// pressed by shooting it (<c>AllData.as</c>, <c>id='knop2'</c>, <c>actdam='1' inter='4' knop='1'</c>).
    ///
    /// <para><b>The chain, in the order the oracle runs it.</b> This is a <i>unit</i> hit, not a tile or
    /// box hit, and that one fact is the whole reason the mechanic was missing from the port:</para>
    /// <list type="number">
    /// <item><c>Box.as:410-419</c> — a definition carrying <c>actdam</c> gets a
    /// <c>VirtualUnit</c> bound to it: <c>bindUnit(xml.@tipdam)</c> when the <i>placement</i> authors
    /// <c>tipdam</c>, else <c>bindUnit()</c> (whose default is <c>"-1"</c>).</item>
    /// <item><c>Location.as:2007-2009</c> — that virtual unit is pushed into <c>loc.units</c>, so the
    /// normal bullet/unit loop sees it.</item>
    /// <item><c>Bullet.as:503-562</c> — the bullet bounds-test hits it and calls
    /// <c>_loc2_.udarBullet(this)</c> at <c>:531</c>.</item>
    /// <item><c>VirtualUnit.as:40-48</c> — a <c>tipdam</c> mismatch returns <c>0</c> (the round still
    /// stops: <c>Bullet.as:535</c> tests <c>_loc4_ &gt;= 0</c>), a match calls
    /// <c>owner.command("dam")</c> and returns <c>1</c>. <c>VirtualUnit.damage</c> (<c>:30-38</c>) is
    /// the identical rule for melee / blast damage.</item>
    /// <item><c>Box.as:446-458</c> — <c>command("dam")</c> forwards to <c>inter.command("dam")</c>.</item>
    /// <item><c>Interact.as:1744-1758</c> — the three arms modelled by <see cref="ResolveDamageVerb"/>.</item>
    /// <item><c>Interact.as:1265-1327</c> — <c>actOsn()</c> toggles the button's own lamp, runs
    /// <c>allAct()</c>, pokes <c>loc.prob.check()</c> and starts <c>scrAct</c>.</item>
    /// </list>
    ///
    /// <para><b>Why these are strings and ints rather than an <c>ObjectInstance</c>.</b> Every decision
    /// here is a comparison the oracle makes on one authored attribute. Taking the raw values keeps the
    /// rules executable in the offline harness (an <c>ObjectInstance</c> drags in a
    /// <c>MapObjectDefinition</c>, which is a <c>ScriptableObject</c> and an ECall outside the editor),
    /// and makes the absent case — the one that decides whether a button shoots at all — an explicit
    /// test input rather than an accident of fixture construction.</para>
    /// </summary>
    public static class ShootButtonRules
    {
        /// <summary>The verb <c>VirtualUnit</c> sends to its owner (<c>VirtualUnit.as:36</c>, <c>:46</c>).</summary>
        public const string DamageVerb = "dam";

        /// <summary>
        /// AS3 <c>VirtualUnit.nTipDam</c>'s "accept every damage type" sentinel
        /// (<c>VirtualUnit.as:12</c> declares <c>-1</c>; <c>Box.bindUnit</c>'s default is <c>"-1"</c>,
        /// <c>Box.as:1227</c>).
        /// </summary>
        public const int NoTipDamageFilter = -1;

        /// <summary>
        /// <c>if(node.@actdam.length())</c> (<c>Box.as:410</c>) — the definition authors <c>actdam</c>.
        ///
        /// <para><b>Presence, not value.</b> The oracle tests the XML attribute's length, so
        /// <c>actdam="0"</c> still binds a virtual unit. There is exactly one such definition in the
        /// shipped data (<c>knop2</c>, <c>actdam='1'</c>), but a fixture that passed <c>"0"</c> and got
        /// <c>false</c> would be inventing a value test the oracle does not make.</para>
        ///
        /// <para><b>Definition only.</b> <c>node</c> is the <c>AllData</c> row; <c>xml</c> (the placement)
        /// authors only <c>tipdam</c>. A placement-level <c>actdam</c> does nothing in AS3.</para>
        /// </summary>
        public static bool HasBulletDamageProxy(string definitionActDam)
        {
            return !string.IsNullOrEmpty(definitionActDam);
        }

        /// <summary>
        /// <c>Box.as:412-418</c> into <c>VirtualUnit.as:24-27</c>. The placement's <c>tipdam</c> is the
        /// only filter source; absent means <c>bindUnit()</c> → <c>"-1"</c> → -1.
        ///
        /// <para><b>A present but unparseable value becomes 0, not -1.</b> AS3's <c>int()</c> of a
        /// non-numeric string is 0, which is <c>Unit.D_BUL</c> — such a button would accept only ordinary
        /// bullets. Returning -1 there would instead make it accept everything, a strictly more permissive
        /// invention. No shipped placement has that shape; the rule is spelled out so a future one cannot
        /// silently change.</para>
        /// </summary>
        public static int ResolveTipDamage(string placementTipDam)
        {
            if (string.IsNullOrEmpty(placementTipDam))
            {
                return NoTipDamageFilter;
            }

            return int.TryParse(
                placementTipDam,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// <c>VirtualUnit.as:32-35</c> and <c>:42-45</c> in one expression:
        /// <c>if(this.nTipDam &gt;= 0 &amp;&amp; this.nTipDam != param2) return 0;</c>.
        ///
        /// <para><b>The port's <c>DamageType</c> numbering is AS3's <c>Unit.D_*</c> numbering</b>
        /// (<c>D_BUL=0 … D_CRIO=11</c>, <c>Unit.as:64-77</c>; <c>DataEnums.cs:83</c>), so the authored
        /// integer can be compared directly to <c>(int)DamageType</c> without a translation table.</para>
        ///
        /// <para><b>A rejected hit is not a rejected round.</b> The oracle returns 0 and
        /// <c>Bullet.as:535</c> still calls <c>popadalo(0)</c>: the bullet stops on the button with a
        /// surface sound, but does not press it. The caller must not turn a <c>false</c> here into
        /// "keep sweeping".</para>
        /// </summary>
        public static bool AcceptsDamageType(int tipDamage, int damageType)
        {
            return tipDamage < 0 || tipDamage == damageType;
        }

        /// <summary>
        /// AS3 <c>Interact.knop</c> — set from the definition first (<c>Interact.as:304-306</c>), then
        /// overridden by the placement when it authors the attribute (<c>:403-405</c>).
        ///
        /// <para><b>The definition fallback is load-bearing, and its absence was a real bug.</b>
        /// <c>knop2</c> authors <c>knop='1'</c> on the <i>definition</i>; none of the 41 shipped
        /// placements repeat it. The port's <c>IsKnob</c> read only the placement list, so the
        /// <c>inter="0"</c> buttons in <c>rooms_prob/buttons4</c> did not look like knobs and would have
        /// taken the wrong <c>dam</c> arm.</para>
        ///
        /// <para><b>Non-empty, not positive.</b> AS3 assigns the XML attribute (an XMLList) to an untyped
        /// field and tests <c>if(this.knop)</c>; a one-node list whose text is <c>"0"</c> is truthy. No
        /// shipped placement authors <c>knop="0"</c>, and the port's existing <c>IsKnob</c> already used
        /// non-emptiness, so this keeps the two readings identical.</para>
        /// </summary>
        public static bool IsKnob(string placementKnop, string definitionKnop)
        {
            if (!string.IsNullOrEmpty(placementKnop))
            {
                return true;
            }

            return !string.IsNullOrEmpty(definitionKnop);
        }

        /// <summary>
        /// AS3 <c>Interact.action</c> — the definition's <c>inter</c> (<c>Interact.as:292</c>), then the
        /// placement's <c>inter</c> overriding it (<c>:395-398</c>).
        ///
        /// <para><b>The override is the whole of <c>buttons4</c>'s mechanic.</b> <c>knop2</c> is
        /// <c>inter='4'</c>, but six of its placements — all six in <c>rooms_prob/buttons4</c> — author
        /// <c>inter="0"</c>, one per damage type (<c>tipdam</c> 3, 5, 6, 9, 10, 11: fire, laser, plasma,
        /// spark, acid, cryo). In AS3 those become <c>knop &amp;&amp; action == 0</c> and take the
        /// one-shot <c>setAct("open",1)</c> arm instead of <c>actOsn()</c>'s toggle.</para>
        /// </summary>
        public static int ResolveInteractionMode(string placementInter, string definitionInter)
        {
            if (!string.IsNullOrEmpty(placementInter) &&
                int.TryParse(placementInter, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int placed))
            {
                return placed;
            }

            return int.TryParse(definitionInter, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int authored)
                ? authored
                : 0;
        }

        /// <summary>
        /// <c>Interact.as:1747</c> — <c>else if(Boolean(this.knop) &amp;&amp; this.action == 0)</c>.
        /// </summary>
        public static bool PressesOnce(bool knob, int interactionMode)
        {
            return knob && interactionMode == 0;
        }

        /// <summary>
        /// <c>Interact.as:1294</c> — <c>if((this.door &gt; 0 || this.knop &gt; 0) &amp;&amp; this.action &gt; 0)</c>,
        /// the branch that toggles the object's own <c>open</c> state (the button's lamp).
        /// </summary>
        public static bool TogglesOwnState(bool knob, bool hasDoor, int interactionMode)
        {
            return (hasDoor || knob) && interactionMode > 0;
        }

        /// <summary>
        /// <c>Interact.as:1315</c> — <c>if(Boolean(this.allact) || this.prob != null) this.allAct();</c>.
        ///
        /// <para><b>Two different sources, one broadcast.</b> The oracle's <c>allAct()</c> switch tests
        /// <c>this.prob</c> <i>before</i> the <c>allact</c> chain (<c>:1558</c>), so an object carrying
        /// either one runs. The port's <c>ObjectActionDispatcher.Dispatch</c> keeps that ordering; this
        /// predicate only decides whether to re-enter it at all.</para>
        /// </summary>
        public static bool ShouldRunAllAct(string allAct, string prob)
        {
            return !string.IsNullOrEmpty(allAct) || !string.IsNullOrEmpty(prob);
        }

        /// <summary>
        /// <c>Interact.command("dam")</c>, <c>Interact.as:1744-1758</c>. Priority matters: an explosive
        /// knob takes the explosion arm, and a knob whose placement authored <c>inter="0"</c> opens once
        /// rather than toggling.
        /// </summary>
        public static DamageVerbOutcome ResolveDamageVerb(bool explosive, bool knob, int interactionMode)
        {
            if (explosive)
            {
                return DamageVerbOutcome.Explode;
            }

            if (PressesOnce(knob, interactionMode))
            {
                return DamageVerbOutcome.OpenOnce;
            }

            return DamageVerbOutcome.Activate;
        }
    }
}
