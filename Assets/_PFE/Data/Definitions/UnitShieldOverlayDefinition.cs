using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Which of AS3's three shield domes a unit draws — the classes <c>visShit</c> (symbol 3625),
    /// <c>visShit2</c> (1903) and <c>visShit3</c> (1900).
    /// </summary>
    /// <remarks>
    /// <para><b>The oracle picks by class, not by data.</b> <c>UnitAlicorn.as:191-198</c> —
    /// <c>if(this.tr == 3) this.visshit = new visShit2(); else this.visshit = new visShit();</c> — and
    /// <c>UnitBossAlicorn.as:130260</c> uses <c>visShit3</c>. So the choice is a property of the unit
    /// subclass and its tier, which is why it is an enum here and a virtual read on
    /// <c>UnitController</c> rather than a field on the unit's data row.</para>
    /// </remarks>
    public enum UnitShieldOverlayKind
    {
        /// <summary>No dome is defined for this unit.</summary>
        None = 0,

        /// <summary><c>visShit</c> — the ordinary alicorn dome (tr1, tr2), and every other shielded unit.</summary>
        Normal = 1,

        /// <summary><c>visShit2</c> — the tr3 alicorn's, drawn at the same 1.5 scale.</summary>
        Large = 2,

        /// <summary><c>visShit3</c> — <c>UnitBossAlicorn</c>'s, 12 frames rather than 20.</summary>
        Boss = 3,
    }

    /// <summary>
    /// The three shield domes, as art. One asset, loaded from
    /// <c>Resources/Characters/UnitShieldOverlayDefinition</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not part of <c>PlayerAnimationDefinition</c>.</b> The player's asset already
    /// carries a <c>shit</c> overlay row — the <i>same</i> symbol 3625 — but it is the player's
    /// composition: body parts, state clips, armour sets. A unit's dome has no body to compose with, so
    /// it needs a clip and nothing else. Sharing the player's asset would also mean an enemy visual
    /// depending on the player's import, which is the coupling that makes "the shield disappeared"
    /// unfalsifiable.</para>
    ///
    /// <para><b>An absent clip is a real state, not an error.</b> <c>visShit2</c> and <c>visShit3</c> are
    /// exported from the SWF but were never imported into this project, so those two fields are
    /// legitimately empty until <c>PFE/Art/Import Unit Shield Overlays</c> is run.
    /// <see cref="UnitShieldOverlayRules"/> treats "no frames" as "not available" and says so at
    /// runtime rather than drawing sprite 0 of an empty array.</para>
    /// </remarks>
    [CreateAssetMenu(
        menuName = "PFE/Unit Shield Overlay",
        fileName = "UnitShieldOverlayDefinition")]
    public class UnitShieldOverlayDefinition : ScriptableObject
    {
        /// <summary>
        /// Where the runtime loader looks. Relative to a <c>Resources</c> folder, with no extension —
        /// the file is <c>Assets/_PFE/Data/Resources/Characters/UnitShieldOverlayDefinition.asset</c>.
        /// </summary>
        public const string ResourcePath = "Characters/UnitShieldOverlayDefinition";

        /// <summary><c>visShit</c> (symbol 3625) — 20 frames, <c>ClampForever</c>.</summary>
        public CharacterOverlayDefinition normal;

        /// <summary><c>visShit2</c> (symbol 1903) — 20 frames. Empty until the art is imported.</summary>
        public CharacterOverlayDefinition large;

        /// <summary><c>visShit3</c> (symbol 1900) — 12 frames. Empty until the art is imported.</summary>
        public CharacterOverlayDefinition boss;
    }

    /// <summary>
    /// Which dome a unit draws, and whether it has frames to draw at all.
    ///
    /// <para><b>Unity-free on purpose.</b> The renderer that consumes this cannot be exercised from an
    /// offline host — assigning a <c>SpriteRenderer</c> reaches a Unity <c>ECall</c> and the JIT refuses
    /// the method that mentions it. So the <i>decision</i> lives here, where a plain NUnit fixture can
    /// call it, and <c>UnitShieldOverlay</c> is reduced to "ask this, then assign the sprite". That is
    /// the same split <c>OverlayClip</c> and <c>CharacterSpriteAssembler</c> already use.</para>
    /// </summary>
    public static class UnitShieldOverlayRules
    {
        /// <summary>
        /// The dome class a unit draws, from AS3's own two branches.
        /// </summary>
        /// <param name="tier">
        /// The alicorn's <c>tr</c>. <c>3</c> is the only value that changes the dome
        /// (<c>UnitAlicorn.as:191</c>); every other tier, and every non-alicorn, takes <c>visShit</c>.
        /// </param>
        /// <param name="boss">
        /// Whether the unit is <c>UnitBossAlicorn</c> — the only class in the oracle that instantiates
        /// <c>visShit3</c>.
        /// </param>
        /// <remarks>
        /// <b>The <c>boss</c> arm is currently unclaimed.</b> No controller returns
        /// <c>UsesBossShieldOverlay == true</c>: the boss alicorn's dome is a one-shot
        /// (<c>UnitBossAlicorn.die():709-712</c> <c>gotoAndPlay(2)</c> on its first lethal hit), not a
        /// pool, so the pool-driven <c>UnitShieldOverlay</c> cannot draw it and
        /// <c>BossAlicornController</c> returns false. The mapping and the <c>boss</c> clip slot are kept
        /// because the art association is real and the one-shot driver is a known, small follow-up — see
        /// <c>TOPIC_open_work.md</c>.
        /// </remarks>
        public static UnitShieldOverlayKind KindFor(int tier, bool boss)
        {
            if (boss) return UnitShieldOverlayKind.Boss;
            return tier == 3 ? UnitShieldOverlayKind.Large : UnitShieldOverlayKind.Normal;
        }

        /// <summary>Whether a clip exists and carries at least one sprite.</summary>
        /// <remarks>
        /// The <c>frames</c> half is the one that matters: a definition field can be non-null with an
        /// empty array (an asset created before the import, or an inspector edit that cleared it), and
        /// treating that as "available" is what draws sprite index 0 of a null array.
        /// </remarks>
        public static bool HasFrames(CharacterOverlayDefinition clip)
        {
            return clip != null && clip.frames != null && clip.frames.Length > 0;
        }

        /// <summary>
        /// The clip for a kind, or <c>null</c> when the asset does not carry it.
        /// </summary>
        public static CharacterOverlayDefinition ClipFor(
            UnitShieldOverlayDefinition definition, UnitShieldOverlayKind kind)
        {
            if (definition == null) return null;

            CharacterOverlayDefinition clip = kind switch
            {
                UnitShieldOverlayKind.Normal => definition.normal,
                UnitShieldOverlayKind.Large => definition.large,
                UnitShieldOverlayKind.Boss => definition.boss,
                _ => null,
            };

            return HasFrames(clip) ? clip : null;
        }

        /// <summary>
        /// The clip a unit should actually draw, with an explicit stand-in when its own dome is missing.
        /// </summary>
        /// <remarks>
        /// <para><b>The fallback is deliberate and is reported, not silent.</b> <c>visShit2</c> and
        /// <c>visShit3</c> are not in the project yet, so without this a tr3 alicorn — the tier that
        /// carries the <i>biggest</i> shield — would be the only one with no dome at all, which reads as
        /// "the shield is still broken". Drawing the ordinary dome instead is closer to the oracle than
        /// drawing nothing, and the caller logs which substitution it made so the difference is
        /// visible rather than inferred. Once the import has run the exact clip wins and the fallback
        /// stops being reachable.</para>
        ///
        /// <para><b>Falling back to <see cref="UnitShieldOverlayKind.Normal"/> and not to "whatever is
        /// first"</b> matters: a "first non-empty clip" rule would hand a tr3 alicorn the <i>boss</i>
        /// dome as soon as <c>visShit3</c> alone was imported, which is a different silhouette from
        /// either of the oracle's two choices for that unit.</para>
        /// </remarks>
        /// <returns>The exact clip when present, else <c>normal</c> when it has frames, else <c>null</c>.</returns>
        public static CharacterOverlayDefinition Select(
            UnitShieldOverlayDefinition definition, int tier, bool boss)
        {
            UnitShieldOverlayKind kind = KindFor(tier, boss);

            CharacterOverlayDefinition exact = ClipFor(definition, kind);
            if (exact != null) return exact;

            if (kind == UnitShieldOverlayKind.Normal) return null;

            return ClipFor(definition, UnitShieldOverlayKind.Normal);
        }

        /// <summary>
        /// Whether <see cref="Select"/> answered with the unit's own dome rather than the stand-in.
        /// The caller uses this to decide whether to warn.
        /// </summary>
        public static bool IsExactMatch(UnitShieldOverlayDefinition definition, int tier, bool boss)
        {
            return ClipFor(definition, KindFor(tier, boss)) != null;
        }
    }
}
