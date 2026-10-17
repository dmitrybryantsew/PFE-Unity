using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Turns an NPC id (<c>calam</c>, <c>velvet</c>, <c>askari</c> — the <c>@npc</c> attribute a room
    /// writes on a unit placement) into its <see cref="NpcDefinition"/>.
    ///
    /// <para><b>Why the unit layer needs this at all.</b> An NPC's identity is split in the oracle
    /// across two documents: the <i>unit</i> row says the unit is class <c>UnitNPC</c>
    /// (<c>AllData.as:4926</c> <c>cl='UnitNPC'</c>), and the <i>npc</i> row says which persona that
    /// instance is (<c>GameData.as:669</c> <c>&lt;npc id='calam' vis='Calam' inter='travel'
    /// weap='sniper'&gt;</c>). <c>UnitNPC</c>'s constructor joins them with
    /// <c>World.w.game.npcs[xml.@npc]</c> (<c>UnitNPC.as:79-91</c>) and reads the visual, the dialogue
    /// and the interaction type off the result. The port's unit definition carries only the first half,
    /// so without this seam an NPC would spawn as a generic collider with no art and no interaction —
    /// which is exactly the reported "there is no Calamity".</para>
    ///
    /// <para><b>An interface so a fixture can answer without <c>Resources</c>.</b> The production
    /// implementation reads the shipped catalogue; a test passes a plain dictionary. Both satisfy the
    /// same contract, so the controller's logic is exercised rather than the asset pipeline.</para>
    /// </summary>
    public interface INpcDefinitionProvider
    {
        /// <summary>
        /// Looks an NPC up by the id the placement authored.
        /// </summary>
        /// <returns><c>false</c> for an unknown or empty id — a miss, not an error. An <c>npc</c> unit
        /// placed without an <c>@npc</c> attribute is a legal shape (the base unit's own id becomes the
        /// persona, <c>UnitNPC.as:94-96</c>) and must not throw.</returns>
        bool TryGetNpc(string npcId, out NpcDefinition definition);
    }

    /// <summary>
    /// The production <see cref="INpcDefinitionProvider"/> — reads the shipped
    /// <c>Resources/CampaignCatalog</c>, which is the same asset <c>CampaignManager</c> and the
    /// container already load (<c>GameLifetimeScope.cs:272</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why the catalogue and not <c>Resources.LoadAll&lt;NpcDefinition&gt;</c>.</b> The 108 NPC
    /// assets under <c>Resources/Campaign/NPCs</c> are addressable two ways, and the catalogue is the one
    /// that already exists and is already loaded at boot — a second scan would build a second index over
    /// the same assets and could disagree with it. It also carries the one thing a bare
    /// <c>LoadAll</c> cannot: the parser's own decision about which NPCs are content.</para>
    ///
    /// <para><b>Cached, and the cache is droppable.</b> The load happens once per process;
    /// <see cref="ClearCache"/> exists for a test or a re-import, matching
    /// <c>ResourcesUnitDefinitionProvider.ClearCache</c>.</para>
    /// </remarks>
    public sealed class ResourcesNpcDefinitionProvider : INpcDefinitionProvider
    {
        /// <summary>The catalogue's path inside a <c>Resources</c> folder — the name
        /// <c>CampaignManager</c> loads.</summary>
        public const string CatalogResourcePath = "CampaignCatalog";

        static ResourcesNpcDefinitionProvider s_shared;

        /// <summary>
        /// The production instance. Lazy so a caller that injects its own provider never pays for the
        /// catalogue load.
        /// </summary>
        public static ResourcesNpcDefinitionProvider Shared =>
            s_shared ??= new ResourcesNpcDefinitionProvider();

        Dictionary<string, NpcDefinition> _byId;

        public bool TryGetNpc(string npcId, out NpcDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(npcId))
            {
                return false;
            }

            EnsureLoaded();
            return _byId.TryGetValue(npcId, out definition);
        }

        /// <summary>Drops the index so the next lookup re-reads the catalogue. For tests and re-imports.</summary>
        public void ClearCache()
        {
            _byId = null;
        }

        void EnsureLoaded()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, NpcDefinition>(StringComparer.OrdinalIgnoreCase);

            CampaignCatalog catalog = Resources.Load<CampaignCatalog>(CatalogResourcePath);
            if (catalog == null)
            {
                // Not an error path a caller can act on: the same missing asset makes CampaignManager
                // fall back to a runtime-empty catalogue (GameLifetimeScope.cs:279). Recorded so a
                // "no NPC anywhere" report has a cause rather than a mystery.
                Debug.LogWarning(
                    $"[ResourcesNpcDefinitionProvider] No CampaignCatalog at Resources/{CatalogResourcePath}. " +
                    "Every NPC lookup will miss, so no NPC unit will get its persona, art or interaction. " +
                    "Run PFE/Data/Import Campaign.");
                return;
            }

            IReadOnlyList<NpcDefinition> npcs = catalog.AllNpcs;
            for (int i = 0; i < npcs.Count; i++)
            {
                NpcDefinition npc = npcs[i];
                if (npc == null || string.IsNullOrWhiteSpace(npc.npcId))
                {
                    continue;
                }

                // Name what we dropped, for the same reason ResourcesUnitDefinitionProvider does: a
                // duplicate id means two assets claim one persona, and silently keeping whichever the
                // list happened to hold first is how the wrong dialogue tree ships.
                if (_byId.TryGetValue(npc.npcId, out NpcDefinition incumbent) && incumbent != npc)
                {
                    Debug.LogWarning(
                        $"[ResourcesNpcDefinitionProvider] Two NpcDefinitions claim id '{npc.npcId}': " +
                        $"keeping '{incumbent.name}', dropping '{npc.name}'. A duplicate NPC id is a data bug.");
                    continue;
                }

                _byId[npc.npcId] = npc;
            }
        }
    }
}
