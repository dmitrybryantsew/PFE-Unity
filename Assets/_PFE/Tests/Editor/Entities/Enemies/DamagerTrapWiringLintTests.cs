using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// Pins the <b>wiring</b> of the satchel charge — the links between a room script's
    /// <c>act="dam"</c> and an explosion, and between a unit id and its sprite.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> The data was complete: <c>room_1_4</c>
    /// authors three <c>expl1</c> units and a <c>detonator</c> whose script is
    /// <c>&lt;scr targ="trExpl1" act="dam"/&gt;</c>, and the room imported byte-for-byte. Four one-line
    /// omissions then made the whole thing inert, and not one of them is visible to an arithmetic
    /// fixture:</para>
    /// <list type="bullet">
    /// <item><description>nothing imported <c>&lt;un tip&gt;</c>, so a charge and a hidden shooter were
    /// the same unit;</description></item>
    /// <item><description><c>UnitController.Command</c> had no <c>dam</c> verb, so the script's target
    /// resolved and then rejected the command;</description></item>
    /// <item><description><c>TickDamager</c> gated on <c>IsArmed</c> (default true) instead of the
    /// oracle's dormant <c>status</c>, so an uncommanded damager attacked;</description></item>
    /// <item><description>the importer's visual resolution never tried the oracle's <c>vis</c> + id, so
    /// the charge drew nothing.</description></item>
    /// </list>
    ///
    /// <para><b>Why source lints and not behavioural tests.</b> Every one of these lives in a
    /// <c>MonoBehaviour</c>, and creating one outside the editor throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before an assertion
    /// is reached. A lint runs everywhere, so it is the only guard that can fail in this harness. Bodies
    /// are located by signature — which throws on a rename — and comments are stripped first, so a
    /// comment that names a call cannot satisfy the check.</para>
    /// </summary>
    [TestFixture]
    public sealed class DamagerTrapWiringLintTests
    {
        private const string BrainPath = "Entities/Enemies/Archetypes/Trap/TrapBrain.cs";
        private const string ControllerPath = "Entities/Enemies/Archetypes/Trap/TrapController.cs";
        private const string SpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";
        private const string SpriteImporterPath = "Editor/Importers/UnitSpriteImporter.cs";
        private const string UnitImporterPath = "Editor/Importers/UnitDataImporter.cs";

        /// <summary>
        /// Source with comments removed but <b>string literals kept</b> — three of the assertions below
        /// are about the literal a verb or an id is spelled with (<c>"dam"</c>, <c>"damexpl1"</c>,
        /// <c>"damagerTip"</c>), and <see cref="SourceLint.ReadStripped"/> would erase exactly the thing
        /// being asserted. Comments are still gone, so a comment naming the verb cannot satisfy them.
        /// </summary>
        private static string ReadCode(string relativePath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(),
                "Assets",
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);

            string code = SourceLint.StripCommentsKeepStrings(File.ReadAllText(fullPath));

            Assert.That(code, Does.Not.Contain("///"),
                "The comment stripper did not strip — an assertion below could then be satisfied by a " +
                "comment that names the call it checks for.");

            return code;
        }

        /// <summary>
        /// Link 1 — <b>the verb</b>. AS3's <c>UnitDamager.command("dam")</c> (<c>:374-380</c>) is the only
        /// writer of <c>status = 1</c>. Without this override the detonator's script reached
        /// <c>UnitController.Command</c>, matched nothing, and the runner logged "no object or unit
        /// matches that uid" — a fuse connected to a dead end.
        /// </summary>
        [Test]
        public void TheDamagerControllerAnswersTheDamVerb()
        {
            string source = ReadCode(ControllerPath);

            string body = SourceLint.MethodBody(source, "public override bool Command(string verb, string value)");
            Assert.That(body, Does.Contain("\"dam\""),
                "DamagerController no longer handles the `dam` verb; pressing the detonator in " +
                "rooms_begin/room_1_4 would do nothing again.");
            Assert.That(body, Does.Contain("ActivateDamager"),
                "the `dam` arm no longer reaches the brain's activate()");
        }

        /// <summary>
        /// Link 2 — <b>the gate</b>. AS3 attacks only inside <c>if(this.status == 1)</c>
        /// (<c>UnitDamager.as:407</c>). The port gated on <c>_isArmed</c>, which defaults <b>true</b>, so
        /// every charge and every hidden shooter fired from spawn.
        /// </summary>
        [Test]
        public void TheDamagerTickGatesOnTheOraclesStatusNotOnIsArmed()
        {
            string source = ReadCode(BrainPath);

            string body = SourceLint.MethodBody(source, "protected virtual void TickDamager(int tickIndex)");
            Assert.That(body, Does.Contain("DamagerTrapRules.Attacks(_damagerStatus)"),
                "TickDamager no longer consults the damager status — a dormant charge would attack");
            Assert.That(body, Does.Not.Contain("_isArmed"),
                "TickDamager is gating on IsArmed again; that flag is a bear trap's latch, defaults " +
                "true, and made every damager shoot the player from the moment the room loaded.");
        }

        /// <summary>
        /// Link 3 — <b>the tip</b>. Without this write a charge is a shooter: no explosion, a weapon it
        /// does not own, and (before link 2) an invisible attacker.
        /// </summary>
        [Test]
        public void TheBrainReadsTheImportedDamagerTip()
        {
            string source = ReadCode(BrainPath);

            string body = SourceLint.MethodBody(source, "protected virtual void ApplyDefinitionTuning()");
            Assert.That(body, Does.Contain("_damagerTip = def.damagerTip"),
                "ApplyDefinitionTuning no longer copies <un tip>; every damager would fall back to " +
                "shooter and the charge would never explode.");
        }

        /// <summary>
        /// Link 4 — <b>the explosion</b>. A tip-3 activation must reach the blast, and the blast must go
        /// through the resolver rather than straight to <c>TakeDamage</c> (which skips vulnerability,
        /// armour and skin — the documented <c>Projectile.Detonate</c> defect, not one to copy).
        /// </summary>
        [Test]
        public void TheChargeBlastReportsThroughTheDamageResolver()
        {
            string source = ReadCode(BrainPath);

            string activate = SourceLint.MethodBody(source, "public bool ActivateDamager()");
            Assert.That(activate, Does.Contain("DetonateBomb"),
                "ActivateDamager no longer routes the bomb arm to DetonateBomb");

            string blast = SourceLint.MethodBody(source, "protected virtual void DetonateBomb()");
            Assert.That(blast, Does.Contain("PendingDamage.Explosion"),
                "the charge's blast bypasses PendingDamage.Explosion — that path is what applies the " +
                "falloff, the spread and the vulnerability table.");
            Assert.That(blast, Does.Contain("ExplosionTileDamage.Apply"),
                "the tile sweep is missing; AS3 runs explDestroy before the body sweep");
            Assert.That(blast, Does.Contain("DamagerTrapRules.BombDamage"),
                "the blast magnitude must come from the cited constant, not a local literal");
        }

        /// <summary>
        /// Link 5 — <b>the controller</b>. A charge that spawns with the base <c>TrapController</c> has no
        /// <c>dam</c> verb however correct the override is.
        /// </summary>
        [Test]
        public void AChargeSpawnsWithTheDamagerController()
        {
            string source = ReadCode(SpawnerPath);

            Assert.That(source, Does.Contain("[\"damexpl1\"] = typeof(DamagerController)"),
                "damexpl1 no longer resolves to DamagerController; the charge would spawn with a " +
                "controller that cannot answer `dam`.");
        }

        /// <summary>
        /// Link 6 — <b>the art</b>. <c>Res.getVis("vis" + id)</c> is the oracle's own derivation
        /// (<c>UnitDamager.as:54</c>); the importer resolved only <c>vclass</c> and <c>blit</c>, so the
        /// charge imported with no sprite and drew nothing.
        /// </summary>
        [Test]
        public void TheSpriteImporterTriesTheDerivedVisName()
        {
            string source = ReadCode(SpriteImporterPath);

            string body = SourceLint.MethodBody(source, "static string[] ResolveVisualNames(");
            Assert.That(body, Does.Contain("TrapVisualNaming.CandidateFor(unit.id)"),
                "ResolveVisualNames no longer tries `vis` + id, so damexpl1 and the nine other units " +
                "that use the oracle's derivation import with no visual.");
            Assert.That(body, Does.Contain("FindVisualFolder(fromId)"),
                "the derived name is returned without checking that the symbol exists — that would hand " +
                "a unit a folder that is not there.");
        }

        /// <summary>
        /// Link 7 — <b>the data path</b>. The parser is only half the import: the importer has to write
        /// its answer onto the definition, or the field stays at the default.
        /// </summary>
        [Test]
        public void TheUnitImporterWritesTheDamagerTip()
        {
            string source = ReadCode(UnitImporterPath);

            Assert.That(source, Does.Contain("UnitExtrasParser.ParseDamagerTip(content)"),
                "UnitDataImporter no longer reads <un tip>; every damager imports as a shooter.");
            Assert.That(source, Does.Contain("SetPrivateField(unit, \"damagerTip\""),
                "the parsed tip is not written onto the definition.");
        }

        /// <summary>
        /// Link 8 — <b>the room data</b>. The one guard here that is about content rather than code: a
        /// re-import or a hand-edit that dropped the three charges or the detonator's script would make
        /// every link above irrelevant while all of them still passed.
        /// </summary>
        [Test]
        public void RoomOneFourStillAuthorsThreeChargesAndTheDetonatorScript()
        {
            string fullPath = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                                           "Rooms", "rooms_begin", "room_1_4.asset");
            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);

            string asset = File.ReadAllText(fullPath);

            int charges = CountOccurrences(asset, "\n    id: expl1\n");
            Assert.AreEqual(3, charges,
                "room_1_4 no longer authors three expl1 charges — the room the report is about has " +
                "lost its content.");

            Assert.That(asset, Does.Contain("uid: trExpl1"),
                "the scripted charge lost its uid; the detonator's @targ cannot resolve.");
            Assert.That(asset, Does.Contain("act: dam"),
                "the detonator's `<scr targ=\"trExpl1\" act=\"dam\"/>` is gone — the fuse has no trigger.");
            Assert.That(asset, Does.Contain("id: detonator"),
                "the detonator box itself is gone.");
        }

        /// <summary>
        /// The census — <b>every room in the game that authors a damager</b>, not just the reported one.
        ///
        /// <para><b>Why this is a set and not a count.</b> "24 damagers somewhere" survives a re-import
        /// that moves one charge from <c>room_3_0</c> to <c>взрывчатка</c>. The comparison below is over
        /// <c>(room, tip)</c> → count, so a room losing one while another gains one fails, and a room
        /// disappearing fails even when the totals hold.</para>
        ///
        /// <para><b>Provenance.</b> Each row was read from the oracle's own room files, not from the
        /// port: <c>Rooms.as</c> (room_1_4), <c>RoomsPlant.as</c> (взрывчатка), <c>RoomsProb.as</c>
        /// (sliv, turrets), <c>RoomsSerial.as</c> (room_6_1) and <c>RoomsSerial2.as</c> (room_3_0,
        /// room_4_0, room_5_0, room_5_2), by counting <c>&lt;obj id="expl1|damgren|damshot"&gt;</c>
        /// inside each <c>&lt;room&gt;</c> element. The port matched on the first run, which is the
        /// finding: the data was never the problem, so this table is a <i>snapshot of a correct import</i>
        /// and its job is to notice when one stops being correct.</para>
        ///
        /// <para><c>rooms_plant/*</c> is a wildcard because that folder's only damager room is named in
        /// Cyrillic (<c>взрывчатка</c>); matching the folder keeps this file ASCII-only while still
        /// refusing a second damager room there.</para>
        /// </summary>
        [Test]
        public void EveryRoomThatAuthorsADamagerStillAuthorsTheOraclesSet()
        {
            // {expl1, damgren, damshot} — the three UnitDamager tips (<un tip='3'|'2'|'1'>).
            var expected = new Dictionary<string, int[]>
            {
                { "rooms_art/room_5_2",   new[] { 0, 2, 0 } },
                { "rooms_begin/room_1_4", new[] { 3, 0, 0 } },
                { "rooms_hql/room_3_0",   new[] { 2, 2, 1 } },
                { "rooms_hql/room_4_0",   new[] { 0, 2, 0 } },
                { "rooms_hql/room_5_0",   new[] { 1, 0, 0 } },
                { "rooms_plant/*",        new[] { 8, 0, 0 } },
                { "rooms_prob/sliv",      new[] { 1, 0, 0 } },
                { "rooms_prob/turrets",   new[] { 0, 0, 1 } },
                { "rooms_raiders/room_6_1", new[] { 0, 1, 0 } },
            };

            string roomsRoot = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Rooms");
            Assert.IsTrue(Directory.Exists(roomsRoot), "Room assets missing: " + roomsRoot);

            var seen = new HashSet<string>();
            int totalExpl = 0, totalGren = 0, totalShot = 0, roomsFound = 0;

            foreach (string file in Directory.GetFiles(roomsRoot, "*.asset", SearchOption.AllDirectories))
            {
                string asset = File.ReadAllText(file);

                // Anchored on the whole placement line: a bare `id: expl1` also matches a reference.
                int e = CountOccurrences(asset, "\n    id: expl1\n");
                int g = CountOccurrences(asset, "\n    id: damgren\n");
                int s = CountOccurrences(asset, "\n    id: damshot\n");
                if (e + g + s == 0) continue;

                string key = file.Substring(roomsRoot.Length + 1)
                                 .Replace('\\', '/')
                                 .Replace(".asset", string.Empty);

                string match = null;
                foreach (string candidate in expected.Keys)
                {
                    if (candidate == key ||
                        (candidate.EndsWith("/*") && key.StartsWith(candidate.Substring(0, candidate.Length - 1))))
                    {
                        match = candidate;
                        break;
                    }
                }

                Assert.IsNotNull(match,
                    key + " authors a damager but is not in the oracle's census — a room was added or " +
                    "renamed without checking the original.");

                seen.Add(match);
                roomsFound++;
                totalExpl += e; totalGren += g; totalShot += s;

                int[] want = expected[match];
                Assert.AreEqual(want[0], e, key + " authors " + e + " expl1, oracle says " + want[0]);
                Assert.AreEqual(want[1], g, key + " authors " + g + " damgren, oracle says " + want[1]);
                Assert.AreEqual(want[2], s, key + " authors " + s + " damshot, oracle says " + want[2]);
            }

            CollectionAssert.AreEquivalent(expected.Keys, seen,
                "the set of rooms that author a damager no longer matches the oracle");

            // Totals, stated once so the document's numbers are pinned to something executable.
            Assert.AreEqual(9, roomsFound, "rooms with a damager");
            Assert.AreEqual(15, totalExpl, "expl1 (tip 3, satchel charge) placements");
            Assert.AreEqual(7, totalGren, "damgren (tip 2, grenade thrower) placements");
            Assert.AreEqual(2, totalShot, "damshot (tip 1, shooter) placements");
        }

        /// <summary>
        /// The <b>second</b> fuse-driven charge. <c>rooms_prob/sliv</c> is the only other room whose
        /// detonator points at a <c>tip 3</c> unit (<c>&lt;scr&gt;&lt;s targ="slivA2" act="dam"/&gt;</c>,
        /// <c>RoomsProb.as</c>), so it is the same bug in a room nobody had reported yet — and the two
        /// rooms are the entire population of "press this, something explodes" in the game.
        ///
        /// <para>It also carries a <c>knop1</c> button that opens the door <c>slivA1</c>
        /// (<c>act="open"</c>). That is asserted here only because the two scripts sit in the same room
        /// and a re-import that dropped one would very likely drop the other; the <c>open</c> verb
        /// itself is not this fixture's subject.</para>
        /// </summary>
        [Test]
        public void TheSecondFuseDrivenChargeKeepsItsDetonator()
        {
            string fullPath = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                                           "Rooms", "rooms_prob", "sliv.asset");
            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);

            string asset = File.ReadAllText(fullPath);

            Assert.AreEqual(1, CountOccurrences(asset, "\n    id: expl1\n"),
                "sliv no longer authors its single charge.");
            Assert.That(asset, Does.Contain("uid: slivA2"),
                "the charge lost the uid the detonator's @targ resolves against.");
            Assert.That(asset, Does.Contain("act: dam"),
                "sliv's detonator script is gone — the second fuse is now a dead end like the first was.");
            Assert.That(asset, Does.Contain("targ: slivA2"),
                "the detonator no longer points at the charge.");
            Assert.That(asset, Does.Contain("act: open"),
                "the knop1 door script vanished in the same room; a re-import that drops one script " +
                "usually drops its neighbours.");

            // Absent control: the same assertion applied to a room that authors a charge and NO
            // detonator. Without this, a reader that silently returned the wrong file (or a cached one)
            // would satisfy every Does.Contain above and prove nothing.
            string controlPath = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                                              "Rooms", "rooms_hql", "room_5_0.asset");
            Assert.IsTrue(File.Exists(controlPath), "Control target missing: " + controlPath);

            string control = File.ReadAllText(controlPath);
            Assert.AreEqual(1, CountOccurrences(control, "\n    id: expl1\n"),
                "the control room must still author a charge, or it is not a control for this claim");
            Assert.That(control, Does.Not.Contain("act: dam"),
                "the control room has no detonator, so `act: dam` must be absent — if it is present, " +
                "these assertions are not reading the room they name.");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
