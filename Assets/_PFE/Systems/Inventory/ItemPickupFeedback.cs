using System;
using UnityEngine;
using PFE.UI.Common;
using PFE.UI.HUD;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// What a take looks and sounds like once the inventory has accepted it — the payload of
    /// <see cref="ItemPickupFeedback"/>, built from AS3 <c>Invent.take</c>'s tail
    /// (<c>unit/Invent.as:1224-1300</c>).
    ///
    /// <para><b>Why the strings are already resolved here rather than in the HUD.</b> The oracle makes
    /// the decision inside the inventory — <c>World.w.gui.infoText("takeWeapon", l.nazv, …)</c> is a call
    /// <i>from</i> <c>Invent.take</c> — so "which message, with which arguments" is inventory knowledge,
    /// not HUD knowledge. The HUD is left with "draw these characters", which is the split every other
    /// HUD view in this project already has.</para>
    /// </summary>
    public struct ItemPickedUpMessage
    {
        /// <summary>The AS3 item or weapon id.</summary>
        public string ItemId;

        /// <summary>
        /// The message-list line, localised and substituted — or <c>null</c> when this take produces no
        /// line at all. Armour and spells are that case (<c>Invent.as:1119-1131</c>): they show a rising
        /// label and add nothing to the list.
        /// </summary>
        public string InfoLine;

        /// <summary>The rising label — <c>l.nazv</c> plus " (n)" when the stack is larger than one.</summary>
        public string FloatLabel;

        /// <summary>The Flash colour index for the label; see <see cref="PickupFeedbackRules.FloatColour"/>.</summary>
        public int ColourIndex;

        /// <summary>
        /// Where the label spawns — <b>the player's</b> position, because that is what the oracle passes
        /// (<c>this.gg.X, this.gg.Y</c>, <c>Invent.as:1291</c>), not the pickup's.
        /// </summary>
        public Vector3 WorldPosition;

        /// <summary>
        /// The one-time tutorial popup key, or <c>null</c> when this take shows no popup.
        ///
        /// <para>Non-null only when all three of the oracle's conditions hold
        /// (<c>Invent.as:1293-1300</c>): the row carries a <c>mess</c>, the player has not seen that key
        /// yet, and either tutorial messages are on or the item is an artefact. The <b>latch is written
        /// before</b> the message is published, so a handler that throws cannot leave the popup armed to
        /// re-fire on the next pickup.</para>
        ///
        /// <para>The key indexes the <c>item</c> text table's <c>&lt;n&gt;</c> (title) and
        /// <c>&lt;mess&gt;</c> (body) children — <c>Res.txt("i", key)</c> and
        /// <c>Res.txt("i", key, 2)</c>. It is <b>not</b> necessarily the item id: two rows may share a
        /// key and then share one popup.</para>
        /// </summary>
        public string MessKey;

        /// <summary>
        /// The icon the popup draws, or <c>null</c>.
        ///
        /// <para><b>A port choice, and a narrow one.</b> The oracle calls
        /// <c>imp.ico.gotoAndStop(nico)</c> where <c>nico</c> is the <i>mess key</i> — the SWF's
        /// <c>imp.ico</c> symbol carries one labelled frame per tutorial. That symbol was flattened by
        /// the importer and has no port, so the popup uses the <b>item's own icon</b> instead. For the
        /// screwdriver the two coincide (the frame is named after the item); for a row whose mess key
        /// names a different picture this draws the item rather than the tutorial's own art.</para>
        /// </summary>
        public Sprite Icon;
    }

    /// <summary>
    /// The seam between the inventory's take path and the HUD, for the pickup feedback the original game
    /// shows: the "Found: …" line in the corner list and the rising label over the player.
    ///
    /// <para><b>Why a static event and not a MessagePipe broker.</b> The publisher is
    /// <c>WorldItemPickup</c>, a component created with <c>AddComponent</c> when loot is spawned
    /// (<c>WorldItemPickup.SpawnCore</c>) — the container never builds it, so there is no
    /// <c>[Inject]</c> site for an <c>IPublisher&lt;T&gt;</c> and no place to register one. The port's
    /// established answer for exactly this shape is a static with a settable surface, and the precedent
    /// is documented on <c>GlobalDifficulty</c>: <i>"The readers are spread across … none of which can
    /// resolve a container-scoped service."</i> A broker that is registered but never published to would
    /// be worse than this — it accepts the subscription and delivers nothing.</para>
    ///
    /// <para><b>Nothing subscribed is not an error.</b> The HUD is optional (a test harness, a headless
    /// run, a scene with no <c>HudBootstrapper</c>), and a take must still complete in all of them, so
    /// <see cref="Publish"/> is a no-op with no listener rather than a warning per pickup.</para>
    /// </summary>
    public static class ItemPickupFeedback
    {
        /// <summary>Raised once per accepted take. The HUD subscribes; everything else ignores it.</summary>
        public static event Action<ItemPickedUpMessage> Published;

        /// <summary>How many listeners are attached, for the F2 readout and for tests.</summary>
        public static int ListenerCount => Published?.GetInvocationList().Length ?? 0;

        /// <summary>Announce an accepted take.</summary>
        public static void Publish(ItemPickedUpMessage message)
        {
            Published?.Invoke(message);
        }

        /// <summary>
        /// Builds the payload for one take, resolving the message through the localisation document.
        ///
        /// <para><b>Pure apart from the text table</b>, which is a cached parse of
        /// <c>Resources/Localisation/text_en.xml</c> — so this is directly assertable in an EditMode
        /// fixture without a scene, which is the reason it is not inlined into <c>Interact</c>.</para>
        ///
        /// <para><paramref name="conditionPercent"/> is the weapon arm's <c>@2</c>
        /// (<c>Math.round(l.sost * l.multHP * 100)</c>). The port's pickups carry a
        /// <see cref="PFE.Data.Definitions.WeaponDefinition"/> rather than an item <i>instance</i>, and a
        /// definition has no <c>sost</c> — so every dropped weapon reports 100 % and a damaged one would
        /// need a per-instance condition first. That is a gap, not a value read off the oracle.</para>
        /// </summary>
        public static ItemPickedUpMessage Build(
            string itemId,
            string displayName,
            string tip,
            bool hasSkillRows,
            bool isMoney,
            int quantity,
            int conditionPercent,
            int forcedColour,
            Vector3 playerPosition)
        {
            PickupKind kind = PickupFeedbackRules.Classify(tip, hasSkillRows);
            string key = PickupFeedbackRules.InfoKey(kind, isMoney);

            string line = null;
            if (key != null)
            {
                // `Res.txt("f", key)` — the `f` alias is the `info` table (PipTextTable.TableFor).
                string template = ResolveInfoTemplate(key);
                if (!string.IsNullOrEmpty(template))
                {
                    line = PickupFeedbackRules.Substitute(
                        template,
                        PickupFeedbackRules.InfoFirst(kind, isMoney, displayName, quantity),
                        PickupFeedbackRules.InfoSecond(kind, quantity, conditionPercent));
                }
            }

            return new ItemPickedUpMessage
            {
                ItemId = itemId,
                InfoLine = line,
                FloatLabel = PickupFeedbackRules.FloatText(displayName, quantity),
                ColourIndex = PickupFeedbackRules.FloatColour(kind, tip, forcedColour),
                WorldPosition = playerPosition,
            };
        }

        /// <summary>
        /// One <c>&lt;info id&gt;&lt;n&gt;</c> lookup. Returns <c>null</c> rather than a marker when the
        /// key is missing, because the caller's next step is "show no line" and a
        /// <c>*info_take</c> marker in the message list would be worse than silence — the same reasoning
        /// <c>PfeTextTableProvider.WeaponNameOrId</c> documents for names.
        /// </summary>
        private static string ResolveInfoTemplate(string key)
        {
            string table = PFE.UI.PipBuck.PipTextTable.TableFor("f");
            return PfeTextTableProvider.Shared.TryGetInTable(table, key, 0, out string text)
                ? text
                : null;
        }

        /// <summary>
        /// The tutorial-popup key for an <b>item</b> take — AS3 <c>l.mess</c>, which is the
        /// <c>&lt;item&gt;</c> row's <c>@mess</c>.
        ///
        /// <para><b>Asset field first, shipped row second.</b> <see cref="PFE.Data.Definitions.ItemDefinition.mess"/>
        /// is empty on every asset until the item import runs, so the row is the live source today; once
        /// the import has run the field wins and the AllData read stops mattering. That ordering is what
        /// makes the popup independent of whether the import has been run — the same arrangement the
        /// <c>&lt;sk&gt;</c> rows use for <c>possLockPick</c>.</para>
        ///
        /// <para><b>Table-explicit, and the reason is a count.</b> The Oracle's weapon take path reads
        /// a different XMLList — <c>Invent.take</c>'s <c>if(l.tip == Item.L_WEAPON)</c> branch indexes
        /// <c>AllData.d.weapon</c> (<c>Invent.as:1060</c>, <c>:1101</c>) — and <b>32 ids exist in both
        /// tables</b> (<c>bomb</c>, <c>cryogr</c>, <c>dinamit</c>, <c>fgren</c> …), so one
        /// item-first lookup would silently read the wrong row for those. Weapons therefore go through
        /// <see cref="ResolveWeaponMessKey"/> instead.</para>
        /// </summary>
        public static string ResolveItemMessKey(string itemId, string assetMess)
        {
            if (!string.IsNullOrEmpty(assetMess)) return assetMess;
            return string.IsNullOrEmpty(itemId) ? null : NullIfEmpty(PlayerInventory.ItemRowMess(itemId));
        }

        /// <summary>
        /// The tutorial-popup key for a <b>weapon</b> take — the <c>&lt;weapon&gt;</c> row's
        /// <c>@mess</c>, read the same way an item's is but from the other table.
        ///
        /// <para><b>Four of the 213 weapon rows carry one</b> — <c>mont</c>, <c>r32</c>, <c>rech</c>,
        /// <c>telebul</c> — and all four have their text in <c>text_en.xml</c>, in the <b>item</b> table
        /// keyed by the weapon id (<c>&lt;item id="r32"&gt;&lt;mess s1="keyReload"&gt;…</c>), because the
        /// <c>&lt;weapon&gt;</c> table there ships no <c>&lt;mess&gt;</c> child at all. That is the same
        /// lookup the popup already performs for an item, so nothing downstream changes.</para>
        /// </summary>
        public static string ResolveWeaponMessKey(string weaponId, string assetMess)
        {
            if (!string.IsNullOrEmpty(assetMess)) return assetMess;
            return string.IsNullOrEmpty(weaponId) ? null : NullIfEmpty(PlayerInventory.WeaponRowMess(weaponId));
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
