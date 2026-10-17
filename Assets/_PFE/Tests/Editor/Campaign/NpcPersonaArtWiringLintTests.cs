using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Pins the two ends of an NPC persona's body art: the importer that <b>creates</b>
    /// <c>NpcDefinition.visualSprite</c>, and the data importer that must not <b>destroy</b> it.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> Two importers write the same asset from two
    /// different sources, and one of them was destroying the other's work silently. <c>vis</c> comes from
    /// <c>GameData.as</c> (<c>DialogueDataImporter</c>), the sprite comes from the SWF sprite export
    /// (<c>UnitSpriteImporter</c>'s NPC pass), and the data importer rebuilds each asset with
    /// <c>EditorUtility.CopySerialized(parsed, npcDef)</c> — which overwrites <b>every</b> serialised
    /// field, including the one the parser has no opinion about. So
    /// <c>PFE/Campaign/Import NPCs and Scripts</c> (and therefore <c>Import Entire Campaign</c>) nulled the
    /// sprite, and running the importers in the obvious order — art, then data — ended with every persona
    /// pointing at no art while both importers reported success. Measured in the asset mtimes of the run
    /// that produced it: art at 12:37, NPCs at 12:38, lands at 12:41.</para>
    ///
    /// <para><b>Why a source lint rather than a behavioural test.</b> <c>DialogueDataImporter</c> is
    /// <c>#if UNITY_EDITOR</c> and its whole body is <c>AssetDatabase</c> calls, so the offline harness
    /// cannot load it, let alone call it — the same reason
    /// <see cref="PFE.Tests.Editor.Map.CheckpointReturnWiringLintTests"/> is a lint. What is pinned here is
    /// the <i>shape</i> of the fix: the value is read before the copy and written back after it. The
    /// alternative — an <c>if</c> around the restore, or a restore placed before the copy — is dead code
    /// that reads as a fix, which is precisely the failure this guard is for.</para>
    ///
    /// <para><b>Both ends, because either alone is satisfiable vacuously.</b> Asserting only the restore
    /// passes on a tree where nothing ever produces the sprite; asserting only the producer passes on a
    /// tree where the next data import erases it. The producer assertion is also the positive control that
    /// keeps the field name honest — rename <c>visualSprite</c> and both tests fail rather than one
    /// silently checking a dead name.</para>
    /// </summary>
    [TestFixture]
    public sealed class NpcPersonaArtWiringLintTests
    {
        private const string SpriteImporterPath = "Editor/Importers/UnitSpriteImporter.cs";

        private const string DataImporterPath = "Editor/Importers/Campaign/DialogueDataImporter.cs";

        /// <summary>
        /// The producing end: <c>UnitSpriteImporter</c>'s NPC pass assigns the persona's frame-0 sprite.
        /// </summary>
        /// <remarks>
        /// <c>UnitNPC.as:101-108</c> resolves the art from the <i>persona</i> (<c>"visual" + vis</c>), not
        /// from the unit id — every NPC in the game shares the one <c>npc</c> unit row — so this pass is
        /// the only thing that can ever give Calamity a body. Without the assignment the field stays null
        /// and <c>UnitNPCController.ApplyPersonaArt</c> returns at its first guard, leaving the persona's
        /// weapon mount as the only thing drawn: "there is only a sniper rifle of him".
        /// </remarks>
        [Test]
        public void NpcVisualPass_StillWritesThePersonaSprite()
        {
            string importer = SourceLint.ReadStripped(SpriteImporterPath);

            // Scoped to the pass that owns the assignment, so the same field written anywhere else in
            // the file — or a mention in a comment, which the stripper removes — cannot satisfy this.
            string pass = SourceLint.MethodBody(importer,
                "static bool ImportNpcVisual(NpcDefinition npc, string visualName, Result result)");

            Assert.That(pass, Does.Contain("npc.visualSprite = "),
                "UnitSpriteImporter's NPC pass no longer assigns NpcDefinition.visualSprite, so no persona " +
                "can ever receive its body art — the pass would copy the frames to disk and wire them to " +
                "nothing. If the field was renamed, rename it here too: this assertion is also the " +
                "positive control that keeps the field name below from being a dead name.");
        }

        /// <summary>
        /// The destructive end: the data import reads the sprite before it copies over the asset, and
        /// writes it back afterwards.
        /// </summary>
        [Test]
        public void DialogueImport_RestoresThePersonaSprite_AcrossTheCopySerialized()
        {
            string source = SourceLint.ReadStripped(DataImporterPath);

            // Scoped to the one method that writes NPC assets, so the same call appearing elsewhere
            // cannot satisfy this.
            string body = SourceLint.MethodBody(source, "ImportFromContent(string content)");

            // Positive control on the clobbering call itself. If the copy stops happening the ordering
            // assertions below would be about nothing — and a data import that no longer overwrites the
            // asset is a different change that deserves its own review, not a silently greener lint.
            int copy = body.IndexOf("CopySerialized(parsed, npcDef)", StringComparison.Ordinal);
            Assert.IsTrue(copy >= 0,
                "DialogueDataImporter no longer rebuilds NPC assets with CopySerialized(parsed, npcDef). " +
                "If that changed, this lint's premise changed with it — re-derive the guard rather than " +
                "deleting it, because the hazard (a data import erasing an art field) is not specific to " +
                "CopySerialized.");

            int read = body.LastIndexOf("npcDef.visualSprite", copy, StringComparison.Ordinal);
            Assert.IsTrue(read >= 0,
                "The sprite is not read off the asset before CopySerialized overwrites it, so there is " +
                "nothing to restore and the copy nulls it. This is the defect: `PFE/Art/Import Unit " +
                "Sprites` writes the sprite, `PFE/Campaign/Import NPCs and Scripts` erases it, and both " +
                "report success.");

            int write = body.IndexOf("npcDef.visualSprite =", copy, StringComparison.Ordinal);
            Assert.IsTrue(write >= 0,
                "The sprite is read before CopySerialized but never written back after it. A read that " +
                "nothing consumes is indistinguishable from no read at all — the asset still ends the " +
                "import with visualSprite == null.");
        }
    }
}
