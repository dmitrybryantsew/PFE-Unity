namespace PFE.Entities.Units
{
    /// <summary>
    /// The item side of an equipped-armour projection: the thing that actually owns the condition.
    ///
    /// <para><b>Why this exists as an interface rather than a direct reference to
    /// <c>GameArmorInstance</c>.</b> Three reasons, in order of weight:</para>
    ///
    /// <list type="number">
    /// <item><description><b>It is the seam the oracle has.</b> AS3 splits the armour into the item
    /// (<c>Armor</c>, which owns <c>hp</c>/<c>maxhp</c> and the ratings) and the projection it writes
    /// onto its owner (<c>owner.armor</c>/<c>marmor</c>/<c>armor_qual</c>, via <c>setArmor()</c>,
    /// <c>Armor.as:297-311</c>). <see cref="ArmourState"/> is the projection; this is the item. Keeping
    /// them separate is the whole point — a projection that owned its own condition would let the
    /// item and the combat state disagree about how damaged the plate is.</description></item>
    /// <item><description><b>Layering.</b> <c>UnitStats</c> lives in the entity layer and must not
    /// depend on the inventory layer. The dependency runs one way:
    /// <c>GameArmorInstance</c> implements this, and <c>UnitStats</c> only ever sees this.</description></item>
    /// <item><description><b>Tests.</b> A fake item is two fields; a real <c>GameArmorInstance</c>
    /// needs a <c>ScriptableObject</c> definition, which drags the asset database into EditMode
    /// tests that have no business touching it.</description></item>
    /// </list>
    ///
    /// <para><b>Ownership rule.</b> The item is the single source of truth for integrity.
    /// <see cref="ArmourState.integrity"/> is a snapshot taken by <see cref="ToArmourState"/>, and
    /// <c>UnitStats</c> writes it back through <see cref="SetIntegrity"/> after every change — the
    /// design doc's step 8. Never mutate the snapshot and assume the item followed.</para>
    /// </summary>
    public interface IArmourItem
    {
        /// <summary>
        /// Stable content id — AS3 <c>Armor.id</c>. <b>The visual layer's key</b>, and the reason this
        /// member exists: <see cref="ArmourState"/> deliberately carries no identity (it is a combat
        /// projection, and a unit-pool state has no id to carry), so without this there is no channel
        /// for "which armour is on" to reach <c>CharacterAnimationDefinition.GetArmorSet</c>.
        ///
        /// <para>AS3 has the same channel: <c>UnitPlayer.changeArmor()</c> writes
        /// <c>Appear.ggArmorId = this.currentArmor.id</c> (<c>:3809</c>) in the same call that sets the
        /// stats. Empty means "no armour" — never <c>null</c>, so a subscriber can compare directly.</para>
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Whether wearing this hides the character's mane — AS3 <c>Armor.hideMane</c>, which
        /// <c>changeArmor()</c> copies to <c>Appear.hideMane</c> alongside the id (<c>:3810</c>).
        /// Part of this seam rather than read from the definition at the call site because the
        /// caller that knows the id does not necessarily have the definition.
        /// </summary>
        bool HideMane { get; }

        /// <summary>
        /// Whether this item may be repaired at all — AS3 <c>!norep</c>.
        ///
        /// <para><b>Where this comes from, exactly.</b> AS3's <c>Armor.repair()</c> (<c>Armor.as:340-348</c>)
        /// is <i>unconditional</i> — it adds to <c>hp</c>, clamps, and re-projects. The <c>norep</c> check
        /// lives in the <b>caller</b>: <c>PipPageWork.as:258</c> filters <c>!a.norep &amp;&amp; !a.und &amp;&amp;
        /// a.hp &lt; a.maxhp</c> while building the workbench's list of repair candidates, and the actual
        /// <c>arm.repair(...)</c> at <c>:568</c> only ever receives an armour that passed that filter. So in
        /// AS3 <c>norep</c> means <b>"never offered"</b>, not "refuses".</para>
        ///
        /// <para><b>Why it is on the seam instead.</b> The port closes the same door from the inside. Every
        /// repair path consults this — <c>GameArmorInstance.Repair</c> and <c>UnitStats.RepairArmour</c> —
        /// so a future caller that forgets the filter cannot diverge. Observable behaviour is identical to
        /// AS3 (nothing can repair <c>tre</c>, the one <c>norep</c> armour that also takes wear), but the
        /// route differs, and that is deliberate rather than accidental.</para>
        /// </summary>
        bool CanRepair { get; }

        /// <summary>
        /// Snapshot this item's current condition and its definition's ratings into a combat
        /// projection. AS3 <c>Armor.setArmor()</c>'s read half.
        /// </summary>
        ArmourState ToArmourState();

        /// <summary>
        /// Write condition back after the projection has been worn or repaired. AS3 keeps one
        /// <c>Armor.hp</c>; so does this — the projection is derived from it, never a second copy.
        /// </summary>
        /// <param name="integrity">The projection's integrity after the change.</param>
        void SetIntegrity(float integrity);
    }
}
