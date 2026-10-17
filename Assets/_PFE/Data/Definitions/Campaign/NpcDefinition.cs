using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Represents an individual dialogue option or branch offered by an NPC.
    /// Corresponds to &lt;dial&gt; tags under &lt;npc&gt; in GameData.as.
    /// </summary>
    [Serializable]
    public class NpcDialogueEntry
    {
        [Tooltip("Dialogue entry ID (e.g. dialCalam2, vendor_weap_hi, dialStorm1)")]
        public string dialogueId = "";

        [Tooltip("Priority or critical conversation flag (imp='1' in AS3)")]
        public bool isImportant = false;

        [Tooltip("Required story trigger condition to show this dialogue (trigger attribute)")]
        public string requiredTrigger = "";

        [Tooltip("Required VALUE of the trigger attribute (n attribute). Empty means the trigger must equal 1.")]
        public string requiredTriggerValue = "";

        [Tooltip("Minimum barter SKILL level (barter attribute). The skill, not barterLvl — NPC.as:278.")]
        public int requiredBarter = 0;

        [Tooltip("Required current pet id (pet attribute). Read by NPC.as:307-310.")]
        public string requiredPet = "";

        [Tooltip("Required current land ID (land attribute)")]
        public string requiredLand = "";

        [Tooltip("Required quest ID in player journal (quest attribute)")]
        public string requiredQuest = "";

        [Tooltip("Required quest sub-stage index (sub attribute)")]
        public int requiredQuestStage = 0;

        [Tooltip("Prerequisite dialogue ID that must have been heard previously (prev attribute)")]
        public string previousDialogueId = "";

        [Tooltip("Required player armor equipped (armor attribute)")]
        public string requiredArmor = "";

        [Tooltip("Required player weapon equipped (weap attribute)")]
        public string requiredWeapon = "";

        [Tooltip("Required player level (lvl attribute)")]
        public int requiredLevel = 0;

        [Tooltip("Music track to trigger upon selecting dialogue (music attribute)")]
        public string music = "";

        [Tooltip("Follow-up script actions triggered upon dialogue activation")]
        public List<CampaignScriptAction> actions = new List<CampaignScriptAction>();
    }

    /// <summary>
    /// One frame of a persona's own animation clip — the <c>osn</c> MovieClip that
    /// <c>visual&lt;Vis&gt;</c> holds as a child.
    ///
    /// <para><b>Why a list and not a sheet.</b> A persona's art is a code-driven clip, not a blit
    /// sheet: <c>UnitNPC.as</c> drives it with <c>vis.osn.gotoAndStop("stay")</c> and
    /// <c>gotoAndPlay("move" + n)</c>, i.e. by <i>label</i> and by <i>timeline frame</i>. The port
    /// therefore keeps the frames in timeline order and the labels beside them, so a
    /// <c>gotoAndPlay</c> can start at the right frame and run on to the next one — which a
    /// label→single-sprite map could not express.</para>
    /// </summary>
    [Serializable]
    public class NpcVisualFrame
    {
        [Tooltip("The SWF timeline frame number, 1-based. This is what AS3's numeric gotoAndStop(n) addresses.")]
        public int frameNumber = 1;

        [Tooltip("The frame's own label ('stay', 'move1', 'fly', …), or empty when it carries none. " +
                 "A label is a marker on a frame, not a state: 'move1' plays on through every frame " +
                 "after it until the timeline wraps.")]
        public string label = "";

        [Tooltip("The imported frame.")]
        public Sprite sprite;
    }

    /// <summary>
    /// ScriptableObject definition for an NPC persona and dialogue tree.
    /// Corresponds to &lt;npc&gt; elements in ActionScript 3 GameData.as
    /// and fe.serv.NPC.
    /// </summary>
    [CreateAssetMenu(fileName = "NewNpcDef", menuName = "PFE/Campaign/NPC Definition")]
    public class NpcDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique NPC ID (e.g. calam, calam2, velvet, steel, autodoc, vendor_weapon)")]
        public string npcId = "";

        [Tooltip("Visual asset archetype identifier (vis attribute in AS3)")]
        public string visualId = "";

        [Tooltip("The imported body sprite for this NPC — the DisplayObject `visual<vis>` " +
                 "(`UnitNPC.as:101-108`). Written by PFE/Art/Import Unit Sprites; the NPC's own art is " +
                 "per PERSONA, not per unit id, because every NPC in the game shares the one `npc` unit " +
                 "row while carrying a different `vis`.")]
        public Sprite visualSprite;

        [Tooltip("The persona's own `osn` clip in timeline order — the frames " +
                 "`UnitNPC.animate()` selects with `vis.osn.gotoAndStop(\"stay\")`, `gotoAndPlay(\"move1\")` " +
                 "and `gotoAndStop(\"fly\")` (UnitNPC.as:164-174, 243-281). Empty for the 114 of 175 " +
                 "`visual*` symbols that have no `osn` child at all: those are genuinely static props " +
                 "and effects, and a persona using one is genuinely static.\n\n" +
                 "Written by PFE/Art/Import Unit Sprites from `pfe/sprites/DefineSprite_<osnId>*`, which " +
                 "is a DIFFERENT folder from the one `visualSprite` comes from — the `osn` clip is the " +
                 "outer symbol's child, so the outer symbol's own export is a single frame.")]
        public List<NpcVisualFrame> visualFrames = new List<NpcVisualFrame>();

        [Tooltip("The `osn` placement's scaleX inside `visual<Vis>`, applied when the persona's clip is " +
                 "drawn. 1 when the frames are the outer symbol's own.\n\n" +
                 "This is NOT cosmetic: the clip is a child MovieClip and the persona scales it. " +
                 "visualCalam's is 1.769 (the exported clip frames are 86x85 against the outer " +
                 "symbol's 153x151), and the doctor's and every vendor's is NEGATIVE on X — " +
                 "(-0.316, 0.316) — so those personas are mirrored as well as shrunk. Drawing the clip " +
                 "at scale 1 puts them at the wrong size and, for the vendors, the wrong way round.")]
        public float visualScaleX = 1f;

        [Tooltip("The `osn` placement's scaleY. See visualScaleX.")]
        public float visualScaleY = 1f;

        /// <summary>
        /// The persona's clip scale as a vector, with <c>1</c> substituted for either axis a stale
        /// asset leaves at <c>0</c>.
        ///
        /// <para><b>Why the guard exists.</b> A <c>0</c> here is not a legitimate scale — it is what a
        /// serialised field reads as before the persona import has run, and it is what a
        /// <c>default(NpcDefinition)</c> carries. Applied raw it collapses the sprite to nothing, so
        /// the persona would vanish the moment it started animating, which is a far more confusing
        /// symptom than a wrong size.</para>
        /// </summary>
        public Vector2 VisualScale
        {
            get
            {
                float x = Mathf.Approximately(visualScaleX, 0f) ? 1f : visualScaleX;
                float y = Mathf.Approximately(visualScaleY, 0f) ? 1f : visualScaleY;
                return new Vector2(x, y);
            }
        }

        /// <summary>The number of frames in the persona's clip, or 0 when it has none.</summary>
        public int VisualFrameCount => visualFrames?.Count ?? 0;

        /// <summary>
        /// The index of the first frame labelled <paramref name="label"/>, or <c>-1</c> when the clip
        /// has no such label.
        ///
        /// <para><b><c>-1</c> is the load-bearing return value.</b> AS3's
        /// <c>vis.osn.gotoAndPlay("move1")</c> <i>throws</i> when the label is missing — the oracle
        /// wraps every one of these calls in a <c>try/catch</c> for exactly that reason — and the
        /// observable consequence is that a persona without the label does not move. A caller must
        /// therefore treat "no label" as "leave the clip alone", never as "go to frame 1".</para>
        /// </summary>
        public int IndexOfVisualLabel(string label)
        {
            if (visualFrames == null || string.IsNullOrEmpty(label))
            {
                return -1;
            }

            for (int i = 0; i < visualFrames.Count; i++)
            {
                NpcVisualFrame frame = visualFrames[i];
                if (frame != null && string.Equals(frame.label, label, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The index of the frame whose SWF frame number is <paramref name="frameNumber"/>, or
        /// <c>-1</c>. This is what AS3's numeric form addresses — <c>vis.osn.gotoAndStop(2)</c> in the
        /// <c>openEyes</c> verb (<c>UnitNPC.as:345</c>) and the <c>gotoAndStop(1)</c> fallback at
        /// <c>:172</c> and <c>:486</c>.
        /// </summary>
        public int IndexOfVisualFrameNumber(int frameNumber)
        {
            if (visualFrames == null)
            {
                return -1;
            }

            for (int i = 0; i < visualFrames.Count; i++)
            {
                NpcVisualFrame frame = visualFrames[i];
                if (frame != null && frame.frameNumber == frameNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The frame AS3's <c>gotoAndStop("stay")</c> resolves to, or <c>-1</c>.
        ///
        /// <para><b>The fallback is the oracle's, not a convenience.</b> <c>UnitNPC.as:164-174</c> is
        /// <c>try { vis.osn.gotoAndStop("stay"); } catch(err:*) { vis.osn.gotoAndStop(1); }</c> — a
        /// persona with no <c>stay</c> label still comes to rest, on timeline frame 1. That is why this
        /// falls back to frame 1 and <see cref="IndexOfVisualLabel"/> does not.</para>
        /// </summary>
        public int IndexOfRestingFrame()
        {
            int stay = IndexOfVisualLabel("stay");
            return stay >= 0 ? stay : IndexOfVisualFrameNumber(1);
        }

        /// <summary>
        /// The frame <see cref="visualSprite"/> holds, or <c>null</c>.
        ///
        /// <para><b>Why the two must agree.</b> <c>visualSprite</c> is the picture the spawner draws
        /// before any animator exists, and it is written as the <i>resting</i> frame. Deriving it here
        /// rather than storing a second reference means the static sprite and the first frame the
        /// animator would show cannot drift apart.</para>
        /// </summary>
        public Sprite RestingVisualFrame
        {
            get
            {
                int index = IndexOfRestingFrame();
                if (index < 0 || index >= VisualFrameCount)
                {
                    return visualSprite;
                }

                return visualFrames[index].sprite != null ? visualFrames[index].sprite : visualSprite;
            }
        }

        [Tooltip("Fallback / canonical name ID for localization lookup (name attribute)")]
        public string aliasName = "";

        [Tooltip("Vendor inventory identifier (vendor attribute)")]
        public string vendorId = "";

        [Tooltip("Interaction behavior archetype: travel, doc, vdoc, adoc, vr, v, nope (inter attribute)")]
        public string interactionType = "";

        [Tooltip("Minimap or dialogue portrait icon index (ico attribute)")]
        public int iconIndex = 0;

        [Tooltip("Context user action verb 1: dial, etc. (ua1 attribute)")]
        public string userAction1 = "";

        [Tooltip("Context user action verb 2: dial, etc. (ua2 attribute)")]
        public string userAction2 = "";

        [Tooltip("Default weapon ID carried (weap attribute)")]
        public string defaultWeapon = "";

        [Tooltip("Silent dialogue toggle - no voice bark (silent='1' in AS3)")]
        public bool isSilent = false;

        [Tooltip("Legacy dialogue id played when the NPC has no vendor and no conditional dialogue " +
                 "matched — AS3 NPC.ndial (NPC.as:58-60). Read by NpcActivation's LegacyDialogue branch.")]
        public string legacyDialogueId = "";

        [Tooltip("Suppress the facing/turn update — AS3 noturn (UnitNPC.as:109-111).")]
        public bool noTurn = false;

        [Tooltip("Dialogue replica / reaction id — AS3 replic (UnitNPC.as:125-127, Unit.as:1306-1308).")]
        public string replicaId = "";

        [Tooltip("Sprite layer / z-order override — AS3 sloy (UnitNPC.as:141-143).")]
        public string layer = "";

        [Tooltip("Secondary weapon id — AS3 weap2 (UnitNPC.as:137-139).")]
        public string secondaryWeapon = "";

        [Tooltip("Skill the NPC's own action requires at a minimum level — AS3 needskill " +
                 "(NPC.as:503-507 repair, :565-569 patient).")]
        public string requiredSkill = "";

        [Tooltip("Item the NPC consumes to complete its action — AS3 needitem (NPC.as:586-588).")]
        public string requiredItemId = "";

        // IGameContent implementation
        string IGameContent.ContentId => npcId;
        ContentType IGameContent.ContentType => ContentType.Campaign;
        public override string DataId => npcId;
        public override string DisplayName => string.IsNullOrEmpty(aliasName) ? npcId : aliasName;

        [Header("Dialogue Tree")]
        [Tooltip("All conditional dialogue entries for this NPC")]
        public List<NpcDialogueEntry> dialogues = new List<NpcDialogueEntry>();

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(npcId))
            {
                Debug.LogWarning("NpcDefinition: npcId is missing!");
                return false;
            }
            return true;
        }
    }
}
