using System;

namespace PFE.Character
{
    /// <summary>
    /// Runtime-only character composition state.
    /// Mirrors the original AS3 fields that were not part of the saved appearance itself.
    /// </summary>
    [Serializable]
    public struct CharacterVisualContext
    {
        public string armorId;
        public bool hideMane;
        public bool transparent;
        /// <summary>
        /// Whether to show wing parts (lwing/rwing).
        /// False by default — wings are granted by an in-game potion, not part of base appearance.
        /// </summary>
        public bool showWings;

        /// <summary>
        /// Whether to show the spell shield (<c>vis.shit</c>) — AS3 <c>UnitPlayer.as:4916-4924</c>, the
        /// <c>sp_mshit</c> graphic.
        ///
        /// <para><b>Driven off <c>shithp &gt; 0</c>, not off the effect system.</b> That is the oracle's
        /// own wiring and it is unusual: the other spell graphic, <c>vis.inh</c> for <c>sp_slow</c>,
        /// <i>is</i> effect-driven (<c>Effect.as:265-268</c>), but the shield is not. The reason is that
        /// <c>shithp</c> is a plain field with no effect behind it — a boss can set it directly
        /// (<c>UnitAlicorn.as:328-333</c>), and a shield that came from a boss has no spell to key off.
        /// So the flag tracks the field. See <c>SpellShield.ShouldShow</c>/<c>ShouldHide</c> for the rise
        /// and fall edges and the play/stop halves.</para>
        ///
        /// <para><b>This is the gate for the <c>"shit"</c> overlay clip, not for a body part.</b>
        /// <c>vis.shit</c> is a sibling of the body sprite, not a part inside it — see
        /// <see cref="PFE.Data.Definitions.CharacterOverlayDefinition"/> for why that distinction decides
        /// the port shape. <see cref="IsOverlayVisible"/> is what the assembler asks.</para>
        /// </summary>
        public bool showShield;

        public static CharacterVisualContext Default => new()
        {
            armorId = string.Empty,
            hideMane = false,
            transparent = false,
            showWings = false,
            showShield = false
        };

        /// <summary>
        /// The gate for an overlay clip, by the clip's own id — the key on
        /// <see cref="PFE.Data.Definitions.CharacterOverlayDefinition.overlayName"/>.
        ///
        /// <para><b>A name switch, deliberately, and the fallback is <c>false</c>.</b> Each overlay is a
        /// named child of the AS3 visual container with its own visibility source, and they are not
        /// uniform: <c>shit</c> tracks the <c>shithp</c> field, <c>inh</c> tracks the <c>inhibitor</c>
        /// effect, and the rest (<c>cryst</c>, <c>fetter</c>, <c>rat</c>, <c>svet</c>) have their own
        /// drivers. So there is no single flag to read — the mapping is the data.</para>
        ///
        /// <para>An unrecognised id returns <c>false</c> rather than <c>true</c>, so adding an overlay
        /// definition cannot make art appear on every character before its gate is written. That means
        /// adding an overlay is <b>two</b> edits — the definition row <i>and</i> a case here — and
        /// forgetting the second one shows as "nothing draws", not as "everything draws".</para>
        /// </summary>
        public bool IsOverlayVisible(string overlayName)
        {
            switch (overlayName)
            {
                case "shit": return showShield;

                // W2 lands here: case "inh": return showInhibitor;
                default:     return false;
            }
        }
    }
}
