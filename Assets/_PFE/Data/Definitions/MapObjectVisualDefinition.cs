using UnityEngine;
using System.Collections.Generic;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Imported visual data for one shared map object presentation.
    /// Keeps art and presentation metadata separate from gameplay behavior.
    /// </summary>
    [CreateAssetMenu(fileName = "MapObjectVisual", menuName = "PFE/Map/Map Object Visual Definition")]
    public class MapObjectVisualDefinition : ScriptableObject
    {
        [Header("Identification")]
        [Tooltip("Stable visual id used by definitions and tooling.")]
        public string visualId;
        [Tooltip("Primary object id this visual was imported for.")]
        public string objectId;
        [Tooltip("All object ids currently linked to this shared visual asset.")]
        public List<string> linkedObjectIds = new List<string>();
        [Tooltip("Original export folder name used to import this visual.")]
        public string sourceFolderName;
        [Tooltip("Original SWF symbol id if it could be inferred from the export folder.")]
        public int sourceSymbolId;

        [Header("Sprites")]
        [Tooltip("Imported frames in export order.")]
        public Sprite[] frames;
        [Tooltip("Pixels per unit used during import.")]
        public int pixelsPerUnit = 100;
        [Tooltip("Pixel size of the first frame.")]
        public Vector2Int pixelSize;

        [Header("Placement")]
        [Tooltip("Normalized sprite pivot intended for gameplay presentation.")]
        public Vector2 pivot = new Vector2(0.5f, 0f);
        [Tooltip("Optional local offset for presenter alignment.")]
        public Vector2 localOffset = Vector2.zero;
        [Tooltip("Sorting order hint for presenter renderers.")]
        public int sortingOrder;
        [Tooltip("Helpful hint for wall cabinets, terminals, and similar objects.")]
        public bool wallMounted;

        public bool HasFrames => frames != null && frames.Length > 0;
        public bool HasAnimation => frames != null && frames.Length > 1;
        public Sprite FirstFrame => HasFrames ? frames[0] : null;

        // ── Flash frame labels ───────────────────────────────────────────────
        //
        // AS3 addresses a box's visual state by LABEL, not by index:
        // Box.setVisState does `vis.gotoAndStop("open" | "close" | "die")` and, for the
        // one animated case, `vis.gotoAndPlay("comein")` (Box.as:508-532). The imported
        // PNG sheets carry no label data, so the label -> index mapping has to be
        // reconstructed. It was measured from the original SWF rather than guessed:
        // every one of the 21 door symbols in texture1.swf authors exactly
        //
        //     "close"@1  "open"@2  "die"@3          (3 frames -> frames[] 0 / 1 / 2)
        //
        // and every Z-door symbol (indoor1..4 = 11 frames, instdoor / inbasedoor /
        // inencldoor = 20 frames) authors only `"comein"@2` (frames[] index 1) with no
        // close/open/die at all. So the frame COUNT is the sheet's structural signature,
        // and these three accessors encode the measured layout once for both consumers
        // (this class's callers: RoomObjectVisualManager and DoorPropPresenter).
        //
        // Verified against H:\Games\FOE Remains\Remains\texture1.swf (symbol ids 90, 268,
        // 271, 279, 284, 289, 290, 295, 300, 305, 310, 313, 366, 371, 376, 378, 387, 392,
        // 404, 407, 409 -> close/open/die; 320, 329, 336, 345, 350, 355, 362 -> comein).

        /// <summary>
        /// True when this sheet is the close/open/die trio of a door box. Measured: every
        /// 3-frame door symbol in the source SWF carries exactly those three labels, and
        /// no other door sheet carries them.
        /// </summary>
        public bool IsDoorStateSheet => HasFrames && frames.Length == 3;

        /// <summary>
        /// True when this sheet is a <b>button lamp</b> — the pressed / released pair a <c>knop</c>
        /// toggles. Measured: exactly four sheets in the imported set are 15 frames long, and they are
        /// precisely <c>visknop1..4</c>.
        ///
        /// <para><b>The labels, read out of the source SWF</b> (<c>texture1.swf</c>, symbols 113 / 110 /
        /// 105 / 102 — <c>visknop1/2/3/4</c>): <c>frameCount=15</c>, and two FrameLabel records,
        /// <c>"close"@1</c> and <c>"open"@2</c>. So frame 1 of the sheet is the released lamp (dark) and
        /// frame 2 opens a 14-frame lit pulse. That is the same <c>close</c>/<c>open</c> label pair a
        /// door uses, at the same frame numbers — which is why one <see cref="OpenStateFrame"/> serves
        /// both layouts.</para>
        ///
        /// <para><b>Why the frame count is the discriminator and not the object id.</b> The PNG sheets
        /// carry no label data, so the mapping has to be reconstructed from something the importer
        /// preserved. Frame count is that something, and it is unambiguous here: 15 occurs four times in
        /// the 140 imported visuals and never for a non-button. The terminals are the control — every
        /// <c>visterm*</c> sheet is <i>one</i> frame with <b>zero</b> labels, so AS3's
        /// <c>gotoAndStop("open")</c> on a terminal throws and is swallowed by
        /// <c>Box.setVisState</c>'s try/catch; a terminal must therefore never change frame.</para>
        /// </summary>
        public bool IsButtonLampSheet => HasFrames && frames.Length == 15;

        /// <summary>Flash <c>"close"</c> — the intact door. Always the first frame.</summary>
        public int ClosedStateFrame => 0;

        /// <summary>
        /// Flash <c>"open"</c> — the doorway. <b>-1 when the sheet has no open state</b>,
        /// which is every Z-door sheet: they are <c>comein</c> clips, never opened or
        /// closed (<c>door=</c> is absent from their definitions).
        ///
        /// <para>Also 1 for a button lamp sheet, whose <c>"open"</c> label is on frame 2 for the same
        /// reason — see <see cref="IsButtonLampSheet"/>. This is what makes pressing a <c>knop</c>
        /// visibly light it, which AS3 does through <c>Interact.actOsn</c>'s
        /// <c>if(this.knop) … setVisState("open")</c>.</para>
        /// </summary>
        public int OpenStateFrame => IsDoorStateSheet || IsButtonLampSheet ? 1 : -1;

        /// <summary>
        /// Flash <c>"die"</c> — the wrecked door. <b>-1 when the sheet has no die state</b>;
        /// AS3's <c>gotoAndStop("die")</c> on a sheet without the label throws and is
        /// swallowed by <c>Box.setVisState</c>'s try/catch, so nothing changes.
        /// </summary>
        public int DestroyedStateFrame => IsDoorStateSheet ? 2 : -1;

        /// <summary>
        /// Flash <c>"comein"</c> — the entry animation of a Z door, and the only label AS3
        /// ever <i>plays</i> rather than seeks to. -1 when the sheet is a single frame, and -1 for a
        /// button lamp sheet, which authors no <c>comein</c> label (only <c>close</c> and <c>open</c>).
        /// </summary>
        public int ComeInFrame => HasAnimation && !IsButtonLampSheet ? 1 : -1;

        /// <summary>The sprite for <paramref name="frameIndex"/>, or null when out of range.</summary>
        public Sprite GetFrame(int frameIndex)
        {
            if (!HasFrames || frameIndex < 0 || frameIndex >= frames.Length)
            {
                return null;
            }

            return frames[frameIndex];
        }

        /// <summary>
        /// The sprite for a door state, falling back to the closed frame when the sheet
        /// does not implement that state.
        /// </summary>
        public Sprite GetDoorStateSprite(int stateFrame)
        {
            return GetFrame(stateFrame) ?? FirstFrame;
        }
    }
}
