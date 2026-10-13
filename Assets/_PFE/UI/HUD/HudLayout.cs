using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Which HUD element an anchor belongs to. The names are the port's own; the Oracle identifies
    /// them by MovieClip path (<c>vis.textWeapon</c>, <c>vis.odBar</c>, …), recorded on each table
    /// entry in <see cref="HudLayout"/>.
    /// </summary>
    public enum HudSlot
    {
        /// <summary>Oracle <c>vis.textWeapon</c> — weapon name, magazine readout and ammo type, as one block.</summary>
        WeaponBlock,

        /// <summary>Oracle <c>vis.hpBar</c> — the three-layer health bar (<c>hp</c> / <c>healhp</c> / <c>rad</c>).</summary>
        HealthBar,

        /// <summary>Oracle <c>vis.hp</c> — the <c>ceil(hp)/ceil(maxhp)</c> readout.</summary>
        HealthText,

        /// <summary>Oracle <c>vis.manaBar</c> — the mana-<i>organ</i> wound track.</summary>
        ManaBar,

        /// <summary>Oracle <c>vis.textMana</c> — the magic-<i>budget</i> readout. A different pool from <see cref="ManaBar"/> on purpose.</summary>
        ManaText,

        /// <summary>Oracle <c>vis.xpBar</c> — experience progress.</summary>
        ExperienceBar,

        /// <summary>
        /// Equipped-armour integrity. <b>No Oracle HUD counterpart.</b> AS3 shows armour condition only
        /// on the PipBuck's right-hand monitor row 8 (<c>PipBuck.as:501-545</c>); there is no
        /// <c>armourBar</c> in <c>GUI.as</c>. Kept as a port element, placed with the stat cluster.
        /// </summary>
        ArmourBar,

        /// <summary>The armour readout. See <see cref="ArmourBar"/>.</summary>
        ArmourText,

        /// <summary>
        /// The weapon reload progress bar. <b>Not a HUD element in the Oracle</b>: it is
        /// <c>reloadBar</c> attached to the player's world sprite and positioned above the head
        /// (<c>UnitPlayer.as:338-340</c>, <c>:367</c> <c>reloadbar.y = -scY - 10</c>). The port has it on
        /// the canvas; moving it to the player is an open item — see
        /// <c>docs/GameInterface/08_IMPLEMENTATION_LOG.md</c>.
        /// </summary>
        ReloadIndicator,

        /// <summary>Oracle <c>vis.textItem</c> — the active quick-item readout.</summary>
        ItemReadout,

        /// <summary>Oracle <c>vis.vItem</c> — the active quick-item icon.</summary>
        ItemIcon,

        /// <summary>Oracle <c>vis.odBar</c> — the S.A.T.S. action-point bar.</summary>
        ActionPointsBar,

        /// <summary>Oracle <c>vis.visibility</c> — the 41-frame sneak/detection eye.</summary>
        SneakEye,

        /// <summary>Oracle <c>vis.eff0..eff11</c> — the 12-slot status-effect strip.</summary>
        StatusEffectsStrip,

        /// <summary>Oracle <c>vis.hpbarboss</c> — the top-centre boss health bar.</summary>
        BossHealthBar,

        /// <summary>Oracle <c>vis.hpPet</c> — the companion health bar.</summary>
        PetHealthBar,

        /// <summary>Oracle <c>celObj</c> — the world-anchored interaction prompt.</summary>
        InteractionPrompt,

        /// <summary>Oracle <c>vis.pr_bar</c> — the hold-action progress bar, world-anchored to its target.</summary>
        HoldBar,

        /// <summary>Oracle <c>mess</c> — the notification toast.</summary>
        Toast,

        /// <summary>Oracle <c>vis.info</c> — the right-aligned info log.</summary>
        InfoLog,

        /// <summary>Oracle <c>vis.dial</c> — the dialogue window.</summary>
        Dialogue,

        /// <summary>Oracle <c>vis.inform</c> — the terminal reader.</summary>
        TerminalReader,

        /// <summary>Oracle <c>vis.imp</c> — the centred key-hint line.</summary>
        KeyHint,
    }

    /// <summary>
    /// Where a table entry's value comes from. This is the field that keeps the port honest: a
    /// <see cref="PortChoice"/> is <b>not</b> a faithful position, it is a gap that has not been
    /// closed yet, and it must not be mistaken for one when someone later compares the HUD against
    /// the original.
    /// </summary>
    public enum HudAnchorSource
    {
        /// <summary>
        /// Read straight out of <c>GUI.resizeScreen</c> (<c>GUI.as:310-349</c>), which re-runs on every
        /// screen resize. These are exact and are the contract this whole file exists to carry.
        /// </summary>
        OracleResizeScreen,

        /// <summary>
        /// Set by some other Oracle method, in absolute screen pixels. Ported, but note the Oracle
        /// sets it on a <i>state change</i> rather than on resize, so the port has to re-apply it.
        /// </summary>
        OracleRelative,

        /// <summary>
        /// The Oracle <b>never sets this</b>: the position lives in the SWF symbol <c>visualGUI</c>
        /// (Symbol 4651), which this pipeline imports as a flat PNG and cannot read coordinates from.
        /// The value below is the port's own and is a known gap — see
        /// <c>docs/GameInterface/06_IMPLEMENTATION_PLAN.md</c> Item I4.
        /// </summary>
        PortChoice,
    }

    /// <summary>
    /// One HUD element's placement, expressed the way uGUI wants it: a point on the parent rect that
    /// the element is pinned to, plus a pixel offset from that point.
    ///
    /// <para><b>Why a point and not just an anchor enum.</b> The Oracle mixes edges and centres in one
    /// table — <c>screenX - 20</c> is an edge, <c>screenX / 2</c> is a centre, <c>screenY - 50</c> is an
    /// edge on the other axis — so the natural representation is the fraction itself
    /// (<c>0</c>, <c>0.5</c>, <c>1</c>) rather than a fixed set of named corners. A table that can
    /// express <c>(0.5, 1)</c> needs no special case for the boss bar or the toast.</para>
    ///
    /// <para><b>Pivot equals the point.</b> Every entry pins the element by the same corner it is
    /// anchored to, which is what makes an offset read as "this far in from that edge". The one place
    /// this is deliberately different is the Oracle's own behaviour, not the port's: <c>odBar</c> is
    /// anchored bottom-right and grows leftward, so it must be pinned by its right edge.</para>
    /// </summary>
    public readonly struct HudAnchor
    {
        /// <summary>Fraction of the parent rect the element is pinned to, used for both anchorMin and anchorMax.</summary>
        public readonly Vector2 Point;

        /// <summary>The element's own pivot — the corner/centre that sits on <see cref="Point"/>.</summary>
        public readonly Vector2 Pivot;

        /// <summary>Offset from <see cref="Point"/>, in reference-resolution pixels (Y up).</summary>
        public readonly Vector2 Offset;

        public HudAnchor(Vector2 point, Vector2 offset)
            : this(point, point, offset)
        {
        }

        public HudAnchor(Vector2 point, Vector2 pivot, Vector2 offset)
        {
            Point = point;
            Pivot = pivot;
            Offset = offset;
        }

        /// <summary>
        /// Writes the anchor onto <paramref name="rect"/>. Deliberately does <b>not</b> touch
        /// <c>sizeDelta</c>: every element keeps the size it was built with, and a layout pass that
        /// resized things would fight whatever set them.
        /// </summary>
        public void Apply(RectTransform rect)
        {
            if (rect == null) return;

            rect.anchorMin = Point;
            rect.anchorMax = Point;
            rect.pivot = Pivot;
            rect.anchoredPosition = Offset;
        }

        public override string ToString()
        {
            return $"point=({Point.x:0.##},{Point.y:0.##}) pivot=({Pivot.x:0.##},{Pivot.y:0.##}) offset=({Offset.x:0.##},{Offset.y:0.##})";
        }
    }

    /// <summary>
    /// The HUD's placement contract — a direct port of <c>GUI.resizeScreen</c>
    /// (<c>…\pfe\scripts\fe\inter\GUI.as:310-349</c>) onto a <b>1200 × 800</b> virtual canvas.
    ///
    /// <para><b>The virtual resolution is the Oracle's, not a taste call.</b> <c>GUI.as:158-160</c>
    /// declares <c>screenX = 1200</c> / <c>screenY = 800</c>, and every offset in <c>resizeScreen</c> is
    /// measured against those. The camera's own reference is a different pair
    /// (<c>fe.inter.Camera.as:16-18</c>, 1280 × 800) — the UI canvas is a 1200-wide sheet centred over
    /// it, which is why the dialogue window computes <c>x = (screenX - 1200) / 2</c>.</para>
    ///
    /// <para><b>What this file is and is not.</b> It is the <i>contract</i>: one entry per HUD element,
    /// each tagged with where its number came from. It is not a builder — <see cref="HudLayoutScaler"/>
    /// applies it to live <see cref="RectTransform"/>s, and <c>HudBootstrapper</c> creates those. A
    /// layout table with no single owner is how a HUD ends up with three different ideas of where the
    /// bottom-left is.</para>
    ///
    /// <para><b>Y is flipped here, and that is the whole trap.</b> Flash puts <c>(0, 0)</c> at the
    /// top-left and grows Y downward, so <c>screenY - 20</c> is "20 up from the bottom". uGUI puts the
    /// origin at the pivot and grows Y upward, so the same edge is expressed as an anchor of
    /// <c>(x, 0)</c> with a positive offset. Every entry below is written in the uGUI convention; the
    /// Oracle expression it came from is quoted beside it so the flip can be checked rather than
    /// trusted.</para>
    /// </summary>
    public static class HudLayout
    {
        // ── The virtual canvas ────────────────────────────────────────────────

        /// <summary>Oracle <c>screenX</c> default (<c>GUI.as:158</c>).</summary>
        public const float ReferenceWidth = 1200f;

        /// <summary>Oracle <c>screenY</c> default (<c>GUI.as:160</c>).</summary>
        public const float ReferenceHeight = 800f;

        /// <summary>
        /// <c>CanvasScaler.matchWidthOrHeight</c>. 0.5 is the only value that does not privilege one
        /// axis: the Oracle's offsets are in absolute pixels on <i>both</i> axes, so a scaler matched
        /// entirely to width would stretch every vertical offset on a 16:10 or 21:9 display.
        /// </summary>
        public const float MatchWidthOrHeight = 0.5f;

        public static Vector2 ReferenceSize => new Vector2(ReferenceWidth, ReferenceHeight);

        // ── Dialogue window (GUI.as:336-345) ──────────────────────────────────

        /// <summary>The dialogue window's authored width; it is never scaled above 1 (<c>GUI.as:341-345</c>).</summary>
        public const float DialogueAuthoredWidth = 1200f;

        /// <summary>
        /// Oracle: <c>dial.scaleX = dial.scaleY = screenX &lt; 1200 ? screenX / 1200 : 1</c>
        /// (<c>GUI.as:336-345</c>). The window <b>shrinks</b> below 1200 and never grows — it is
        /// centred at 1:1 on anything wider.
        /// </summary>
        public static float DialogueScale(float screenX)
        {
            return screenX < DialogueAuthoredWidth ? screenX / DialogueAuthoredWidth : 1f;
        }

        /// <summary>
        /// Oracle: <c>dial.x = screenX &lt; 1200 ? 0 : (screenX - 1200) / 2</c> (<c>GUI.as:339,344</c>).
        /// Expressed as an offset from a centred anchor, which is the same position.
        /// </summary>
        public static float DialogueCentreOffset(float screenX)
        {
            return screenX < DialogueAuthoredWidth ? 0f : (screenX - DialogueAuthoredWidth) / 2f;
        }

        // ── Notification toast (GUI.as:325-335) ───────────────────────────────

        /// <summary>Oracle margin: <c>275</c> on each side (<c>GUI.as:325,328</c>).</summary>
        public const float ToastMargin = 275f;

        /// <summary>The toast's minimum authored width, <c>500</c> (<c>GUI.as:333</c>).</summary>
        public const float ToastMinWidth = 500f;

        /// <summary>
        /// Oracle: the toast is <c>screenX - 550</c> wide when that exceeds 500, and 500 wide
        /// otherwise; it is centred either way (<c>GUI.as:325-335</c>).
        /// </summary>
        public static float ToastWidth(float screenX)
        {
            float wide = screenX - ToastMargin * 2f;
            return wide > ToastMinWidth ? wide : ToastMinWidth;
        }

        // ── S.A.T.S. layer (GUI.as:322-323) ───────────────────────────────────

        /// <summary>
        /// Oracle: <c>vis.sats.scaleX = screenX / 100; vis.sats.scaleY = screenY / 100</c>
        /// (<c>GUI.as:322-323</c>). The S.A.T.S. overlay is authored at 100 × 100 and stretched to the
        /// whole screen — it is a scale, not an anchor, which is why it is not in the table.
        /// </summary>
        public static Vector2 SatsScale(float screenX, float screenY)
        {
            return new Vector2(screenX / 100f, screenY / 100f);
        }

        // ── Key-hint line (GUI.as:347-348) ────────────────────────────────────

        /// <summary>Oracle: <c>imp.y = round(screenY / 2 - 50)</c> (<c>GUI.as:348</c>) — 50 px below centre.</summary>
        public const float KeyHintCentreOffsetY = -50f;

        // ── The anchor table ──────────────────────────────────────────────────

        // Edge/corner shorthands. Written out rather than as magic Vector2s so the intent of each row
        // is readable without decoding two numbers.
        private static readonly Vector2 BottomLeft = new Vector2(0f, 0f);
        private static readonly Vector2 BottomRight = new Vector2(1f, 0f);
        private static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        private static readonly Vector2 TopRight = new Vector2(1f, 1f);
        private static readonly Vector2 TopCentre = new Vector2(0.5f, 1f);
        private static readonly Vector2 BottomCentre = new Vector2(0.5f, 0f);
        private static readonly Vector2 Centre = new Vector2(0.5f, 0.5f);

        // The weapon block's x is dynamic in the Oracle: `vis.textWeapon.x = 20 + weapon.textWidth`
        // (GUI.as:863) slides the block right as the weapon's display name grows. Only the baseline is
        // a constant, and the sliding belongs to whatever view renders the name — not here, because a
        // layout pass that re-ran on resize would undo it.
        private const float WeaponBlockInsetX = 20f;

        /// <summary>
        /// The Oracle's own HUD offsets, in the port's Y-up convention. Read the two comments on each
        /// row together: the first is the Oracle expression, the second is why it lands where it does.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<HudSlot, HudAnchor> Table =
            new System.Collections.Generic.Dictionary<HudSlot, HudAnchor>
            {
                // ── Set by GUI.resizeScreen (exact) ───────────────────────────

                // GUI.as:314 `vis.textWeapon.y = screenY - 20` — 20 px up from the bottom edge.
                // GUI.as:863 is the dynamic x; the constant part is 20.
                { HudSlot.WeaponBlock, new HudAnchor(BottomLeft, new Vector2(WeaponBlockInsetX, 20f)) },

                // GUI.as:316-317 `odBar.x = screenX - 10; odBar.y = screenY - 10` — pinned by its
                // right edge so the bar drains leftward, which is how the Oracle masks it.
                { HudSlot.ActionPointsBar, new HudAnchor(BottomRight, new Vector2(-10f, 10f)) },

                // GUI.as:318-319 `visibility.x = screenX - 10; visibility.y = screenY - 50`.
                // NOTE: this is only the pre-first-frame default. setVisibility re-anchors the eye to
                // the player's head every frame via cam.setKoord (GUI.as:981) — see HudSlot.SneakEye
                // and docs/GameInterface/07_VERIFICATION_AND_QUIRKS.md §2.4.
                { HudSlot.SneakEye, new HudAnchor(BottomRight, new Vector2(-10f, 50f)) },

                // GUI.as:324 `hpbarboss.x = screenX / 2` — horizontally centred. Its y is NOT set by
                // the Oracle, so the 20 px top inset is this port's; see SourceFor(BossHealthBar).
                { HudSlot.BossHealthBar, new HudAnchor(TopCentre, new Vector2(0f, -20f)) },

                // GUI.as:326-335 — the toast is centred with a 275 px side margin, clamped to a
                // 500 px minimum width. The x/y below is the centred anchor; the width is ToastWidth().
                { HudSlot.Toast, new HudAnchor(TopCentre, new Vector2(0f, -20f)) },

                // GUI.as:339,344 `dial.x = (screenX - 1200) / 2` — centred on a 1200-wide sheet.
                { HudSlot.Dialogue, new HudAnchor(Centre, Vector2.zero) },

                // GUI.as:346 `inform.x = round(screenX / 2)` — centred.
                { HudSlot.TerminalReader, new HudAnchor(Centre, Vector2.zero) },

                // GUI.as:347-348 `imp.x = round(screenX / 2); imp.y = round(screenY / 2 - 50)`.
                { HudSlot.KeyHint, new HudAnchor(Centre, new Vector2(0f, KeyHintCentreOffsetY)) },

                // GUI.as:315 `vis.info.x = screenX - 20` — right edge. Its y is not set; the inset is
                // this port's. See SourceFor(InfoLog).
                { HudSlot.InfoLog, new HudAnchor(TopRight, new Vector2(-20f, -20f)) },

                // ── Set elsewhere in the Oracle, in absolute pixels ───────────

                // GUI.as:1068,1073 `vis.textItem.y = 58` (or 88 while the pet bar is visible).
                // GUI.as:1067,1072 `vitem.y = 70` (or 100). Both are absolute top-left Y, i.e. measured
                // DOWN from the top edge. The x of neither is set anywhere in the Oracle.
                { HudSlot.ItemReadout, new HudAnchor(TopLeft, new Vector2(20f, -58f)) },
                { HudSlot.ItemIcon, new HudAnchor(TopLeft, new Vector2(20f, -70f)) },

                // GUI.as:1077,1081 `textMana.y = textItem.y + 35` when the item icon is visible, else
                // `= textItem.y`. The +35 form is the common case; see HudLayout.ApplyRelative.
                { HudSlot.ManaText, new HudAnchor(TopLeft, new Vector2(20f, -93f)) },

                // ── Not set by the Oracle at all: port choices ────────────────
                //
                // These elements' positions live in the SWF symbol `visualGUI` (Symbol 4651), which the
                // importer writes out as a single flat PNG (Assets/_PFE/Art/UI/Interface/HUD_MasterLayout.png)
                // with no placement data. The numbers below are the port's and are a KNOWN GAP: the
                // faithful fix is to read the child placements out of the SWF, not to nudge these until
                // they look right.
                //
                // They sit in the top-left because the Oracle's own top-left content does — textItem is
                // pinned at absolute y=58 (GUI.as:1068) and hpBar/manaBar/xpBar are shown and hidden as
                // one group with it (GUI.as:758,764,773). The rows below are laid out to stay inside the
                // 0..58 band that the item/mana readouts do not occupy.

                { HudSlot.HealthBar, new HudAnchor(TopLeft, new Vector2(20f, -10f)) },
                { HudSlot.HealthText, new HudAnchor(TopLeft, new Vector2(348f, -12f)) },
                { HudSlot.ArmourBar, new HudAnchor(TopLeft, new Vector2(20f, -38f)) },
                { HudSlot.ArmourText, new HudAnchor(TopLeft, new Vector2(348f, -40f)) },
                { HudSlot.ManaBar, new HudAnchor(TopLeft, new Vector2(20f, -50f)) },
                { HudSlot.ExperienceBar, new HudAnchor(TopLeft, new Vector2(20f, -58f)) },
                { HudSlot.StatusEffectsStrip, new HudAnchor(TopRight, new Vector2(-20f, -20f)) },
                { HudSlot.PetHealthBar, new HudAnchor(TopLeft, new Vector2(20f, -80f)) },

                // Above the weapon block, at the bottom-left. In the Oracle this bar is on the player's
                // sprite, not the HUD — see HudSlot.ReloadIndicator.
                { HudSlot.ReloadIndicator, new HudAnchor(BottomLeft, new Vector2(20f, 56f)) },

                // World-anchored in the Oracle, not screen-anchored: `celObj` is re-projected every
                // frame by GUI.step (GUI.as:1949), and pr_bar rides with it (GUI.as:1463
                // `celobj.y = pr_bar.y = cam.screenY - 40 - celobj.height`). The resting anchors below
                // are the port's — deliberately kept where the port already had them so Item I4 changes
                // no more than it must, and both slots are consumed by Item I5.
                { HudSlot.InteractionPrompt, new HudAnchor(Centre, Vector2.zero) },
                { HudSlot.HoldBar, new HudAnchor(BottomCentre, new Vector2(0f, 96f)) },
            };

        /// <summary>
        /// Where the anchor for <paramref name="slot"/> came from. Every <see cref="HudAnchorSource.PortChoice"/>
        /// is an open gap, not a ported value — see the table's trailing block.
        /// </summary>
        public static HudAnchorSource SourceFor(HudSlot slot)
        {
            switch (slot)
            {
                case HudSlot.WeaponBlock:
                case HudSlot.ActionPointsBar:
                case HudSlot.SneakEye:
                case HudSlot.Dialogue:
                case HudSlot.TerminalReader:
                case HudSlot.KeyHint:
                    return HudAnchorSource.OracleResizeScreen;

                // The Oracle sets these, but in another method and on a state change rather than on
                // resize — so a port must re-apply them, and their x is still its own.
                case HudSlot.ItemReadout:
                case HudSlot.ItemIcon:
                case HudSlot.ManaText:
                    return HudAnchorSource.OracleRelative;

                // Horizontally centred by the Oracle, vertically not set. Classified as a port choice
                // rather than as Oracle-set because half the anchor is invented and half is not, and a
                // "mostly ported" tag is how an invented value gets quoted as a citation.
                case HudSlot.BossHealthBar:
                case HudSlot.Toast:
                case HudSlot.InfoLog:
                default:
                    return HudAnchorSource.PortChoice;
            }
        }

        /// <summary>
        /// The anchor for <paramref name="slot"/>. Falls back to the centred anchor for a slot that has
        /// no row — every slot does today, and the fallback exists so a new enum member cannot throw
        /// out of a layout pass during a resize.
        /// </summary>
        public static HudAnchor AnchorFor(HudSlot slot)
        {
            return Table.TryGetValue(slot, out HudAnchor anchor)
                ? anchor
                : new HudAnchor(Centre, Vector2.zero);
        }

        /// <summary>Applies <paramref name="slot"/>'s anchor to a live rect. Null-tolerant.</summary>
        public static void Apply(HudSlot slot, RectTransform rect)
        {
            AnchorFor(slot).Apply(rect);
        }

        /// <summary>True when the slot has a row in the table.</summary>
        public static bool HasAnchor(HudSlot slot)
        {
            return Table.ContainsKey(slot);
        }

        /// <summary>
        /// The Oracle's <c>setOtstup</c> (<c>GUI.as:1063-1083</c>) as a pure function: the quick-item
        /// readout, its icon and the mana line all shift down by 30 px while the pet bar is on screen,
        /// and the mana line sits 35 px below the item readout only when the item icon is visible.
        ///
        /// <para>Separate from <see cref="AnchorFor(HudSlot)"/> because it is <i>state-dependent</i>
        /// placement: the table's rows are the resting positions and this is the adjustment. Folding it
        /// into the table would mean two rows for one element and no way to express "which is active".</para>
        ///
        /// <para><b>Signs.</b> Flash grows Y downward, so <c>y = 58 + 30</c> is 30 px <i>lower</i> on
        /// screen — which in this file's Y-up, top-anchored convention is a <i>more negative</i> offset.
        /// Hence the <c>-30</c> below, and the <c>+35</c> that brings the mana line back up to the item
        /// readout's own row when the icon is hidden.</para>
        /// </summary>
        public static HudAnchor AnchorFor(HudSlot slot, bool petBarVisible, bool itemIconVisible)
        {
            HudAnchor anchor = AnchorFor(slot);

            // Only the three elements setOtstup touches move. Guarded rather than applied to whatever
            // slot the caller passes, because a blanket shift would silently drag every other element
            // 30 px down the moment a pet exists.
            bool shiftedByOtstup = slot == HudSlot.ItemReadout
                                   || slot == HudSlot.ItemIcon
                                   || slot == HudSlot.ManaText;

            if (!shiftedByOtstup)
            {
                return anchor;
            }

            // textMana rides below textItem only while the item icon is showing (GUI.as:1077,1081).
            if (slot == HudSlot.ManaText && !itemIconVisible)
            {
                anchor = Shift(anchor, 0f, 35f);
            }

            if (petBarVisible)
            {
                anchor = Shift(anchor, 0f, -30f);
            }

            return anchor;
        }

        /// <summary>Applies the state-dependent anchor from <see cref="AnchorFor(HudSlot,bool,bool)"/>.</summary>
        public static void ApplyRelative(HudSlot slot, RectTransform rect, bool petBarVisible, bool itemIconVisible)
        {
            AnchorFor(slot, petBarVisible, itemIconVisible).Apply(rect);
        }

        private static HudAnchor Shift(HudAnchor anchor, float dx, float dy)
        {
            return new HudAnchor(anchor.Point, anchor.Pivot,
                new Vector2(anchor.Offset.x + dx, anchor.Offset.y + dy));
        }
    }
}
