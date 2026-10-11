using System;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The one rule that decides whether a prop is a <b>container</b>, and which loot table it names —
    /// a direct port of AS3 <c>fe/serv/Interact.as</c>'s <c>cont</c> resolution.
    ///
    /// <para><b>Why a rule and not a family enum member.</b> AS3 has no container class. A prop is a
    /// container purely because its resolved <c>cont</c> string is non-null, and the only place that
    /// changes is <c>Interact.setAct("loot", n)</c>, which writes <c>cont = "empty"</c>
    /// (<c>Interact.as:834-844</c>). The port used to answer this question with a hand-maintained id
    /// set (<c>MapObjectDefinitionClassifier.ContainerIds</c>) that listed <c>mcrate1</c>…<c>mcrate5</c>
    /// and <c>box</c> — none of which carry <c>cont</c> in the oracle (<c>AllData.as:4879</c>). Family,
    /// <c>tip</c> and id are <i>indices</i>; the resolved <c>cont</c> is the answer.</para>
    ///
    /// <para><b>Precedence is placement-first, then definition</b> — the reverse of
    /// <c>ObjectInstance.GetAllAct</c> is NOT the model here, but the same shape: AS3 copies the
    /// <i>definition</i> row's <c>cont</c> first (<c>Interact.as:210-213</c>, <c>param2.@cont</c>) and
    /// then lets the <i>placed</i> node override it (<c>:332-335</c>, <c>this.xml.@cont</c>). The
    /// placement is applied second, so it wins — but only when it carries a value.</para>
    ///
    /// <para><b>Present-and-empty is absent.</b> Both reads are guarded by <c>.length()</c>
    /// (<c>:210</c>, <c>:332</c>). The port reads that guard as "the string is non-empty" — the same
    /// reading <c>ObjectInstance.GetAllAct</c> and <c>RoomPopulator.PlaceReturnDoor</c> already use —
    /// so an authored <c>cont=""</c> is a no-op and the definition's value survives. See
    /// <see cref="HasValue"/> for the honest note on the E4X ambiguity this reading resolves.</para>
    /// </summary>
    public static class ContainerRule
    {
        /// <summary>
        /// AS3's looted sentinel — <c>Interact.setAct("loot", n)</c> writes <c>cont = "empty"</c>
        /// (<c>Interact.as:840</c>) and <c>Interact.loot()</c> returns early on it (<c>:1965-1968</c>).
        /// A prop carrying this has already been opened.
        /// </summary>
        public const string EmptyCont = "empty";

        /// <summary>
        /// The wildcard key that appears on 7 room placements and matches <b>no</b> <c>lootCont</c>
        /// branch — see <c>docs/DeviceInteractionAndLoot/05_VERIFICATION_AND_QUIRKS.md</c> §5 Q1.
        /// AS3 yields a silently-empty container for it.
        /// </summary>
        public const string WildcardCont = "*";

        /// <summary>
        /// The port's reading of AS3's <c>attr.length()</c> guard: a present-but-empty attribute
        /// (<c>cont=""</c>) is <b>not</b> a value, so it does not override.
        ///
        /// <para><b>Honest note on the ambiguity.</b> In AS3's E4X, <c>xml.@cont.length()</c> is the
        /// <i>XMLList</i> count (0 or 1), which would make a present-but-empty <c>cont=""</c>
        /// <i>truthy</i> and let it override. This port (and <c>ObjectInstance.GetAllAct</c>,
        /// <c>GetHoldFrames</c>, <c>RoomPopulator.PlaceReturnDoor</c>) reads it as the string length
        /// instead. The two readings differ only for a present-but-empty value, and the string-length
        /// reading is the one that keeps a Z door placed with <c>inter=""</c> working. It is flagged
        /// as an owner-verifiable item; do not change it silently in either direction.</para>
        /// </summary>
        public static bool HasValue(string raw) => !string.IsNullOrEmpty(raw);

        /// <summary>
        /// Resolve the effective <c>cont</c> for one prop: the placement's value when it has one, else
        /// the definition's, else empty. AS3 <c>Interact.as:210-213</c> then <c>:332-335</c>.
        ///
        /// <para>Returns the value <b>verbatim</b> — including <c>"*"</c>, which is a real authored
        /// value that the caller may want to report (Q1). Never maps <c>"*"</c> or <c>""</c> to a
        /// sentinel here; the decision to treat a value as "no table" belongs to
        /// <see cref="IsContainer"/> / <see cref="HasLootTable"/>.</para>
        /// </summary>
        public static string ResolveCont(string placementCont, string definitionCont)
        {
            if (HasValue(placementCont)) return placementCont;
            if (HasValue(definitionCont)) return definitionCont;
            return string.Empty;
        }

        /// <summary>
        /// Whether a prop with this resolved <c>cont</c> behaves as a container: it can be opened and
        /// reports "empty" when its table yields nothing.
        ///
        /// <para>True for any non-empty value except the looted sentinel <see cref="EmptyCont"/>. Note
        /// this <b>includes</b> <see cref="WildcardCont"/>: AS3 lets the player open a <c>*</c> prop and
        /// answers "itsEmpty" (<c>Interact.as:2020-2023</c>). Use <see cref="HasLootTable"/> to ask the
        /// narrower question "will a table actually be rolled".</para>
        /// </summary>
        public static bool IsContainer(string resolvedCont)
            => HasValue(resolvedCont) && resolvedCont != EmptyCont;

        /// <summary>
        /// Whether this resolved <c>cont</c> names a table the oracle can actually roll — non-empty,
        /// not the looted sentinel, and not the wildcard. This is the predicate behind the
        /// classification rule: family, <c>tip</c> and id are indices, never the answer.
        /// </summary>
        public static bool HasLootTable(string resolvedCont)
            => IsContainer(resolvedCont) && resolvedCont != WildcardCont;

        /// <summary>Whether this is the looted sentinel written by <c>setAct("loot", n)</c>.</summary>
        public static bool IsLooted(string resolvedCont)
            => string.Equals(resolvedCont, EmptyCont, StringComparison.Ordinal);

        /// <summary>Whether this is the wildcard key that matches no table (Q1).</summary>
        public static bool IsWildcard(string resolvedCont)
            => string.Equals(resolvedCont, WildcardCont, StringComparison.Ordinal);
    }
}
