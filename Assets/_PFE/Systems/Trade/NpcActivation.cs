using System;
using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Trade
{
    /// <summary>
    /// What a world interaction with an NPC resolves to — the port of <c>fe.serv.NPC.activate</c>'s
    /// branch ladder (<c>NPC.as:205-248</c>).
    /// </summary>
    public enum NpcActivationKind
    {
        /// <summary>Nothing the port models handled it (an NPC with no vendor, no dialogue, no owner).</summary>
        None = 0,

        /// <summary>A dialogue branch matched, so the dialogue runs and the interaction <b>stops</b> —
        /// <c>NPC.as:207-210</c>. This is the branch that pre-empts trade.</summary>
        Dialogue = 1,

        /// <summary><c>npcInter == "travel"</c> → the world map, page 3 (<c>NPC.as:211-216</c>).</summary>
        Travel = 2,

        /// <summary><c>doc</c>/<c>vdoc</c> → the clinic, page 6 (<c>NPC.as:217-220</c>).</summary>
        Doctor = 3,

        /// <summary><c>adoc</c> with <c>rep &lt;= 1</c> → the autodoc repairs (<c>NPC.as:221-231</c>).</summary>
        AutoDocRepair = 4,

        /// <summary><c>adoc</c> with <c>rep &gt; 1</c> → the clinic, page 6 (<c>NPC.as:227-230</c>).</summary>
        AutoDocClinic = 5,

        /// <summary><c>npcInter == "patient"</c> → the captive branch (<c>NPC.as:232-235</c>).</summary>
        Patient = 6,

        /// <summary>The NPC has a vendor → page 4 (<c>NPC.as:236-239</c>). <b>The trade path.</b></summary>
        Trade = 7,

        /// <summary><c>ndial</c> with no vendor → the legacy dialogue (<c>NPC.as:240-243</c>).</summary>
        LegacyDialogue = 8,

        /// <summary>The NPC has an owner → <c>owner.command("tell","dial")</c> (<c>NPC.as:244-247</c>).</summary>
        OwnerTell = 9
    }

    /// <summary>
    /// The inputs <c>NPC.activate</c> branches on. A struct rather than seven parameters, for the same
    /// reason <c>PipSubTabContext</c> is one.
    /// </summary>
    public readonly struct NpcActivationInput
    {
        public NpcActivationInput(string npcInter, bool hasVendor, bool dialogueMatched, int rep,
            bool hasLegacyDialogue, bool hasOwner)
        {
            NpcInter = npcInter ?? string.Empty;
            HasVendor = hasVendor;
            DialogueMatched = dialogueMatched;
            Rep = rep;
            HasLegacyDialogue = hasLegacyDialogue;
            HasOwner = hasOwner;
        }

        /// <summary>AS3 <c>NPC.npcInter</c> — <c>""</c>, <c>travel</c>, <c>v</c>, <c>vr</c>, <c>doc</c>,
        /// <c>vdoc</c>, <c>adoc</c>, <c>patient</c>.</summary>
        public string NpcInter { get; }

        /// <summary>Whether the NPC owns a <c>Vendor</c> (<c>NPC.as:236</c>).</summary>
        public bool HasVendor { get; }

        /// <summary>Whether <c>check(true)</c> found a dialogue branch — and, in the oracle, <i>ran</i> it
        /// (<c>NPC.as:207</c>).</summary>
        public bool DialogueMatched { get; }

        /// <summary>AS3 <c>NPC.rep</c> — the autodoc's repair counter.</summary>
        public int Rep { get; }

        /// <summary>AS3 <c>NPC.ndial</c> — a legacy dialogue id.</summary>
        public bool HasLegacyDialogue { get; }

        /// <summary>AS3 <c>NPC.owner</c> — the NPC belongs to a unit that can be commanded.</summary>
        public bool HasOwner { get; }
    }

    /// <summary>
    /// The port of <c>NPC.activate</c>'s ladder and <c>NPC.setStatus</c>'s verb — the two places the
    /// vendor's world presence is decided.
    ///
    /// <para><b>Order is the whole point.</b> The dialogue test is <i>first</i> and it <b>returns</b>
    /// (<c>NPC.as:207-210</c>), so a trader with an unplayed greeting shows the greeting and does not open
    /// the shop; the greeting sets its <c>dial_&lt;id&gt;</c> trigger, so the <i>next</i> interaction
    /// reaches <c>this.vendor</c>. Getting this order wrong makes trade unreachable for every NPC that has
    /// a greeting — which is all of them (<c>02_USER_MODEL_RECHECK.md</c> §12).</para>
    ///
    /// <para><b>The <c>patient</c> exemption is real.</b> The guard is
    /// <c>if(this.check(true) &amp;&amp; this.npcInter != "patient")</c>, so a captive whose dialogue
    /// matched still falls through to the <c>patient</c> branch — the dialogue does not pre-empt it.</para>
    /// </summary>
    public static class NpcActivation
    {
        /// <summary>The branch <c>NPC.activate()</c> would take.</summary>
        public static NpcActivationKind Resolve(NpcActivationInput input)
        {
            // :207-210 — the dialogue pre-empts everything except the captive branch.
            if (input.DialogueMatched && input.NpcInter != "patient") return NpcActivationKind.Dialogue;

            if (input.NpcInter == "travel") return NpcActivationKind.Travel;            // :211-216
            if (input.NpcInter == "doc" || input.NpcInter == "vdoc") return NpcActivationKind.Doctor; // :217-220

            if (input.NpcInter == "adoc")                                                // :221-231
            {
                return input.Rep <= 1 ? NpcActivationKind.AutoDocRepair : NpcActivationKind.AutoDocClinic;
            }

            if (input.NpcInter == "patient") return NpcActivationKind.Patient;           // :232-235
            if (input.HasVendor) return NpcActivationKind.Trade;                         // :236-239
            if (input.HasLegacyDialogue) return NpcActivationKind.LegacyDialogue;        // :240-243
            if (input.HasOwner) return NpcActivationKind.OwnerTell;                      // :244-247

            return NpcActivationKind.None;
        }

        /// <summary>
        /// The on-screen verb — <c>NPC.setStatus(0)</c>'s ladder (<c>NPC.as:415-464</c>). The vendor
        /// branch is <b>last but one</b> and yields <c>"trade"</c>, which <c>Interact</c> renders as
        /// <c>Res.guiText("trade")</c> (<c>Interact.as:664-666</c>).
        ///
        /// <para><c>userAction2</c> wins over everything, and a <c>doc</c>/<c>vdoc</c> says
        /// <c>"therapy"</c> even though it has a vendor — which is why a doctor opens the clinic and
        /// reaches the trade page only through the clinic's own Repair redirect
        /// (<c>PipPageVend.as:480-484</c>).</para>
        /// </summary>
        /// <param name="userAction2">AS3 <c>NPC.userAction2</c> — the authored override, or null.</param>
        /// <param name="patientTriggerIsOne">AS3 <c>trig("patient_tr2") == "1"</c>, read only for a
        /// <c>patient</c>.</param>
        public static string InteractionVerb(NpcActivationInput input, string userAction2 = null,
            bool patientTriggerIsOne = false)
        {
            if (!string.IsNullOrEmpty(userAction2)) return userAction2;                  // :415-418

            if (input.NpcInter == "doc" || input.NpcInter == "vdoc") return "therapy";   // :419-422
            if (input.NpcInter == "patient") return patientTriggerIsOne ? "dial" : "see";// :423-441
            if (input.NpcInter == "adoc") return input.Rep <= 1 ? "repair" : "therapy";  // :442-454
            if (input.HasVendor) return "trade";                                         // :455-458

            return "dial";                                                               // :459-462
        }
    }

    /// <summary>
    /// The facts a dialogue entry's conditions are tested against — one field per condition the port's
    /// <see cref="NpcDialogueEntry"/> models, plus the <c>dial_&lt;id&gt;</c> "already played" table.
    /// </summary>
    public readonly struct NpcDialogueFacts
    {
        public NpcDialogueFacts(Func<string, bool> isDialoguePlayed, Func<string, bool> isTriggerOne,
            Func<string, bool> landAccessible, Func<string, int> questState, Func<string, bool> ownsArmor,
            Func<string, bool> ownsWeapon, int level, int barterSkill,
            Func<string, string, bool> isTriggerValue = null, Func<string, bool> hasPet = null)
        {
            IsDialoguePlayed = isDialoguePlayed;
            IsTriggerOne = isTriggerOne;
            LandAccessible = landAccessible;
            QuestState = questState;
            OwnsArmor = ownsArmor;
            OwnsWeapon = ownsWeapon;
            Level = level;
            BarterSkill = barterSkill;
            IsTriggerValue = isTriggerValue;
            HasPet = hasPet;
        }

        /// <summary>AS3 <c>trig("dial_" + id)</c>.</summary>
        public Func<string, bool> IsDialoguePlayed { get; }

        /// <summary>AS3 <c>trig(name) == 1</c>, with the coercing comparison.</summary>
        public Func<string, bool> IsTriggerOne { get; }

        /// <summary>AS3 <c>trig(name) == value</c> — the <c>@n</c> arm (<c>NPC.as:284-290</c>).</summary>
        public Func<string, string, bool> IsTriggerValue { get; }

        /// <summary>AS3 <c>lands[id].access</c>.</summary>
        public Func<string, bool> LandAccessible { get; }

        /// <summary>AS3 <c>quests[id].state</c>; 0 when the quest is not in the journal.</summary>
        public Func<string, int> QuestState { get; }

        public Func<string, bool> OwnsArmor { get; }
        public Func<string, bool> OwnsWeapon { get; }

        /// <summary>AS3 <c>gg.currentPet.id == id</c> (<c>NPC.as:307-310</c>).</summary>
        public Func<string, bool> HasPet { get; }

        /// <summary>AS3 <c>World.w.pers.level</c>.</summary>
        public int Level { get; }

        /// <summary>AS3 <c>pers.getSkLevel(pers.skills["barter"])</c> — the <b>skill</b>, not
        /// <c>barterLvl</c> (<c>NPC.as:278</c>).</summary>
        public int BarterSkill { get; }
    }

    /// <summary>
    /// The port of the dialogue gate that pre-empts trade — <c>NPC.check(false)</c>'s condition chain
    /// (<c>NPC.as:262-410</c>), read for the vendor case.
    ///
    /// <para><b>Every condition the shipped data uses is evaluated.</b> A count over the 97
    /// <c>&lt;dial&gt;</c> rows of <c>GameData.as</c> finds exactly nine attributes in play —
    /// <c>id</c>, <c>imp</c>, <c>trigger</c>, <c>quest</c>, <c>sub</c>, <c>lvl</c>, <c>prev</c>,
    /// <c>land</c>, <c>armor</c> — plus three rare ones, <c>n</c> (2 rows), <c>barter</c> (2) and
    /// <c>pet</c> (1). The last three had <b>no field on <see cref="NpcDialogueEntry"/></b> until this
    /// pass, so the parser dropped them and their rows were evaluated as if ungated (rule #12).</para>
    ///
    /// <para><b>What is still not evaluated.</b> <c>@sub</c>'s <c>invis</c> flag
    /// (<c>NPC.as:312-320</c>) needs the quest sub-stage table, which the port does not model; and the
    /// <c>check(true)</c> arm additionally constructs and runs a <c>Script</c> (<c>:322-340</c>). Neither
    /// affects whether an entry <i>matches</i> beyond the <c>@sub</c> case, which is named here so the
    /// gap is a decision rather than an oversight.</para>
    /// </summary>
    public static class NpcDialogueGate
    {
        /// <summary>
        /// Finds the first dialogue entry whose conditions hold — AS3's <c>for each(xml.dial)</c> with its
        /// nested <c>continue</c>s (<c>NPC.as:271-320</c>).
        /// </summary>
        /// <returns>True when an entry matched; <paramref name="dialogueId"/> is then its id.</returns>
        public static bool TryFindGreeting(NpcDefinition npc, NpcDialogueFacts facts,
            out string dialogueId)
        {
            dialogueId = null;

            if (npc?.dialogues == null || npc.dialogues.Count == 0) return false;

            for (int i = 0; i < npc.dialogues.Count; i++)
            {
                NpcDialogueEntry entry = npc.dialogues[i];
                if (entry == null || string.IsNullOrEmpty(entry.dialogueId)) continue;

                // :271 — an already-played greeting never replays. This is the clause that makes the
                // SECOND activation of a trader reach the vendor branch.
                if (facts.IsDialoguePlayed != null && facts.IsDialoguePlayed(entry.dialogueId)) continue;

                // :273-277 — level gate. Absent (0) is not a gate; the oracle tests `.length()` first.
                if (entry.requiredLevel > 0 && entry.requiredLevel > facts.Level) continue;

                // :278-282 — the barter SKILL gate (not barterLvl).
                if (entry.requiredBarter > 0 && entry.requiredBarter > facts.BarterSkill) continue;

                // :284-298 — trigger gate: `@n` present means "must equal @n", absent means "must be 1".
                if (!string.IsNullOrEmpty(entry.requiredTrigger))
                {
                    if (!string.IsNullOrEmpty(entry.requiredTriggerValue))
                    {
                        if (facts.IsTriggerValue != null &&
                            !facts.IsTriggerValue(entry.requiredTrigger, entry.requiredTriggerValue)) continue;
                    }
                    else if (facts.IsTriggerOne != null && !facts.IsTriggerOne(entry.requiredTrigger)) continue;
                }

                // :299-300 — the previous dialogue must have played.
                if (!string.IsNullOrEmpty(entry.previousDialogueId) && facts.IsDialoguePlayed != null &&
                    !facts.IsDialoguePlayed(entry.previousDialogueId))
                {
                    continue;
                }

                // :302-305 — the land must be reachable.
                if (!string.IsNullOrEmpty(entry.requiredLand) && facts.LandAccessible != null &&
                    !facts.LandAccessible(entry.requiredLand))
                {
                    continue;
                }

                // :306-310 — the equipped armour must match.
                if (!string.IsNullOrEmpty(entry.requiredArmor) && facts.OwnsArmor != null &&
                    !facts.OwnsArmor(entry.requiredArmor))
                {
                    continue;
                }

                // :307-310 — the equipped pet must match.
                if (!string.IsNullOrEmpty(entry.requiredPet) && facts.HasPet != null &&
                    !facts.HasPet(entry.requiredPet))
                {
                    continue;
                }

                // :301-320 — a quest gate must be in progress (state 1). The @sub/invis arm is not
                // modelled; see the class summary.
                if (!string.IsNullOrEmpty(entry.requiredQuest))
                {
                    if (facts.QuestState == null || facts.QuestState(entry.requiredQuest) != 1) continue;
                }

                dialogueId = entry.dialogueId;
                return true;
            }

            return false;
        }
    }
}
