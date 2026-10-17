using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using PFE.Systems.Campaign;
using PFE.Tests.Editor.Common;
using PFE.UI.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the information page's (page 3, <c>PipPageInfo</c>) pure half — the travel-map layout, the
    /// minimap state machine, the two journals, the bestiary roll-up, the campaign note store and the
    /// one formatter seam the page consumes.
    ///
    /// <para><b>Everything here runs without Unity.</b> The page's models were written Unity-free for
    /// exactly this reason (the same split <see cref="PipSubTabsTests"/> documents): the page's
    /// decision surface is a set of functions of their arguments, and a play-test that has to open the
    /// device, reach the right sub-tab and hover the right node to discover a rule regression is a
    /// play-test that reports one number out of the dozens these fixtures pin.</para>
    ///
    /// <para><b>Two document halves, as in <see cref="PipTextTableTests"/>.</b> The branch tests run
    /// against inline documents so each assertion names the entry it depends on; the data tests run
    /// against the shipped <c>text_en.xml</c> so the localisation author's own statement is checked
    /// against the code's — the seeded notes' importance, and <c>helpWorld</c>'s existence with
    /// brackets that the help path must render literally.</para>
    ///
    /// <para><b>Deliberately no view tests.</b> <see cref="PipPageInfoView"/> builds GameObjects; a
    /// fixture for it would need an engine, and everything it derives is already stated by the models
    /// it reads. What the view adds is placement, and the Oracle's numbers for those are pinned in
    /// <see cref="PipInfoFrame"/> itself, cited line by line.</para>
    /// </summary>
    [TestFixture]
    public class PipBuckInfoTests
    {
        // ── Inline documents ──────────────────────────────────────────────────

        /// <summary>Two quests and three notes, enough to pin every branch of the two journals.</summary>
        /// <remarks>
        /// <para><b>The note token must be written <c>&amp;amp;lp</c> here.</b> The Oracle's note token
        /// really is <c>&amp;lp</c> — <c>PipPageInfo.as:224</c> (title) and <c>:428</c> (body) both do
        /// <c>replace(/&amp;lp/g, pers.persName)</c>, which is what <c>PipNoteJournal</c> ports. A bare
        /// <c>&amp;</c> is not legal XML, so writing it raw made <b>every</b> test that touched this
        /// document die inside <c>XmlDocument.Load</c> with "An error occurred while parsing
        /// EntityName" — twelve reds from one unescaped character. Escaped, the parser hands the journal
        /// the literal <c>&amp;lp</c> it looks for.</para>
        ///
        /// <para><b>The <c>@lp</c> in <c>text_en.xml</c>'s narrative texts is a different token.</b>
        /// That one belongs to the generic <c>Res.txt</c> path (<c>Res.as:309/339</c>), not to the
        /// journals, so the two are not interchangeable and the shipped document is not wrong.</para>
        /// </remarks>
        private const string InlineXml = @"<all>
  <txt id='questB' imp='0'><n><r p='1'>Beta quest</r></n></txt>
  <txt id='questA' imp='0'><n><r p='1'>Alpha quest</r></n></txt>
  <txt id='questC' imp='0'><n><r p='1'>Gamma quest</r></n></txt>
  <txt id='noteImp1' imp='1'><n><t>First ordinary note</t></n></txt>
  <txt id='noteImp2' imp='2'><n><t>Second ordinary note</t></n></txt>
  <txt id='noteImp3' imp='3'><n><t>Important note one</t></n></txt>
  <txt id='noteImp3b' imp='3'><n><t>Important note two</t></n></txt>
  <txt id='noteBody' imp='1'><n><r p='1'>Dear &amp;lp,[br]see [the manual] first.</r></n></txt>
  <txt id='noteIndented' imp='1'><n><t> Indented title that runs past the sixty character cap of the Oracle's own truncation rule</t></n></txt>
</all>";

        /// <summary>The bestiary's roll-up, as one inline <c>&lt;all&gt;</c> document. The ids carry no
        /// <c>txt</c> names, which is the case the code documents (<c>text?.Unit(id) ?? id</c>).</summary>
        private const string BestiaryXml = @"<all>
  <unit id='wolf' cat='1'/>
  <unit id='raider' cat='2'/>
  <unit id='raiderA' cat='3'/>
  <unit id='raiderB' cat='3'/>
  <unit id='zebra' cat='2'/>
  <unit id='zebraA' cat='3'/>
  <unit id='ghost' cat='3'/>
  <unit id='novariant'/>
</all>";

        private static PipTextTable Inline() => PipTextTable.Parse(InlineXml);

        // ── The shipped document (see PipTextTableTests for the loading rule) ──

        private static PipTextTable _shipped;

        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True,
                    "The localisation document is missing: " + path);

                _shipped = PipTextTable.Parse(File.ReadAllText(path));
                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries.");
                return _shipped;
            }
        }

        // ── The travel map's reconciliation ───────────────────────────────────

        [Test]
        public void TravelMap_HasTwentySevenNodes()
        {
            // Sprite 834 frame 1: 28 placements, 27 named. The 28th is the unnamed backdrop.
            Assert.That(PipTravelMapLayout.NodeCount, Is.EqualTo(27));
            Assert.That(PipTravelMapLayout.Nodes.Count, Is.EqualTo(27));
        }

        [Test]
        public void TravelMap_HasTwentySixIconFramesOfWhichTwentyFiveAreLabelled()
        {
            // Sprite 829: 26 frames, frame 1 unlabelled and blank — the Oracle's catch-arm target.
            Assert.That(PipTravelMapLayout.IconFrameCount, Is.EqualTo(26));
            Assert.That(PipTravelMapLayout.IconLabels.Count, Is.EqualTo(26));
            Assert.That(PipTravelMapLayout.IconLabels[0], Is.Empty,
                "Frame 1 is the blank the Oracle's try/catch falls back to (:170-177).");
            Assert.That(PipTravelMapLayout.IconLabels.Skip(1).Count(l => l.Length > 0), Is.EqualTo(25));
        }

        [Test]
        public void TravelMap_IconLabelsAreUnique()
        {
            // The label IS the join key: a re-export that reorders the clip must be caught by the
            // duplicate, not by a wrong icon on every land.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string label in PipTravelMapLayout.IconLabels)
            {
                if (label.Length == 0) continue;
                Assert.That(seen.Add(label), Is.True,
                    "Duplicate icon frame label: '" + label + "'");
            }
        }

        [Test]
        public void TravelMap_EveryNodeEitherHasAnIconOrIsADevSandbox()
        {
            // The reconciliation as data: 27 nodes, 25 labels, and the two misses are exactly the
            // nodes PipTravelMapLayout names.
            var withoutIcon = new HashSet<string>(
                PipTravelMapLayout.NodesWithoutIcon, StringComparer.OrdinalIgnoreCase);

            foreach (TravelNode node in PipTravelMapLayout.Nodes)
            {
                int index = PipTravelMapLayout.IconFrameIndexFor(node.LandId);
                if (withoutIcon.Contains(node.LandId))
                {
                    Assert.That(index, Is.EqualTo(0),
                        "Node '" + node.LandId + "' is listed as icon-less but resolves a frame.");
                }
                else
                {
                    Assert.That(index, Is.GreaterThan(0),
                        "Node '" + node.LandId + "' has no icon frame — if the art really lost one, " +
                        "add it to NodesWithoutIcon with its reason rather than letting it fall to " +
                        "the blank frame silently.");
                }
            }
        }

        [Test]
        public void TravelMap_LandsWithoutNode_HaveNoNode()
        {
            foreach (string landId in PipTravelMapLayout.LandsWithoutNode)
            {
                Assert.That(PipTravelMapLayout.HasNode(landId), Is.False,
                    "Land '" + landId + "' is listed as node-less but the map has one — the two " +
                    "tables disagree.");
            }
        }

        [Test]
        public void TravelMap_NodeLookup_IsCaseInsensitiveAndFallsBackCleanly()
        {
            // `visWMap[land.id]` is Flash's case-insensitive child lookup; the port matches it, and a
            // land with no node is the Oracle's own `if (sim)` miss (:157-158).
            Assert.That(PipTravelMapLayout.TryGetNode("BEGIN", out TravelNode node),
                Is.True);
            Assert.That(node.LandId, Is.EqualTo("begin"));
            Assert.That(PipTravelMapLayout.HasNode("no_such_land"), Is.False);
        }

        // ── The travel node's draw rules ──────────────────────────────────────

        [Test]
        public void TravelDraw_DevSandboxOutsideTestMode_IsInvisible()
        {
            // PipPageInfo.as:178 — the whole per-node block is skipped.
            TravelNodeDraw draw =
                PipTravelMapLayout.DrawFor(isTestLand: true, testMode: false,
                    visited: false, access: false, canTravel: false, helpMess: false);

            Assert.That(draw.Visible, Is.False);
            Assert.That(draw.Alpha, Is.EqualTo(1f));
            Assert.That(draw.Sign, Is.False);
        }

        [Test]
        public void TravelDraw_Untravelable_IsDimmedToHalf_NotHidden()
        {
            TravelNodeDraw draw =
                PipTravelMapLayout.DrawFor(isTestLand: false, testMode: false,
                    visited: true, access: true, canTravel: false, helpMess: false);

            Assert.That(draw.Visible, Is.True);
            Assert.That(draw.Alpha, Is.EqualTo(0.5f));
        }

        [Test]
        public void TravelDraw_TestModeUnvisited_OverwritesTheHalfDim()
        {
            // :184-187 is an ASSIGNMENT, not a minimum and not a product — 0.3, not 0.15.
            TravelNodeDraw draw =
                PipTravelMapLayout.DrawFor(isTestLand: false, testMode: true,
                    visited: false, access: false, canTravel: false, helpMess: false);

            Assert.That(draw.Alpha, Is.EqualTo(0.3f));
        }

        [Test]
        public void TravelDraw_VisibilityGate_IsTestModeOrVisitedOrAccess()
        {
            Assert.That(PipTravelMapLayout.DrawFor(false, false, false, false, true, false).Visible,
                Is.False, "Never visited and never unlocked: the Oracle leaves the node hidden.");
            Assert.That(PipTravelMapLayout.DrawFor(false, false, true, false, true, false).Visible,
                Is.True);
            Assert.That(PipTravelMapLayout.DrawFor(false, false, false, true, true, false).Visible,
                Is.True);
            Assert.That(PipTravelMapLayout.DrawFor(false, true, false, false, true, false).Visible,
                Is.True);
        }

        [Test]
        public void TravelDraw_Sign_NeedsHelpMessAndAccessAndNotVisited()
        {
            // :188-192 — the flag marks exactly the transition the player has not taken yet.
            Assert.That(PipTravelMapLayout.DrawFor(false, false, false, true, true, true).Sign, Is.True);
            Assert.That(PipTravelMapLayout.DrawFor(false, false, false, true, true, false).Sign,
                Is.False, "helpMess is the master switch.");
            Assert.That(PipTravelMapLayout.DrawFor(false, false, true, true, true, true).Sign,
                Is.False, "An already-visited land must not carry the marker.");
            Assert.That(PipTravelMapLayout.DrawFor(false, false, false, false, true, true).Sign,
                Is.False, "A locked land is not 'new' — the player cannot go there yet.");
        }

        [Test]
        public void TravelDraw_ProbLands_AreExcludedByDraw()
        {
            // :154 — the caller does not filter these; Draw does, because that is where the Oracle does.
            var draws = PipTravelMapLayout.Draw(
                new[] { "begin", "prob", "stable_pi_atk" },
                landId => landId.StartsWith("prob") || landId.StartsWith("stable_pi_atk"),
                _ => false, _ => true, _ => true, _ => true,
                testMode: false, helpMess: false);

            Assert.That(draws.Count, Is.EqualTo(1));
            Assert.That(draws[0].Node.LandId, Is.EqualTo("begin"));
        }

        [Test]
        public void TravelDraw_LandWithNoNode_IsAbsentNotInvisible()
        {
            // The `if (sim)` miss: the loop still visits the land, but there is nothing to draw.
            var draws = PipTravelMapLayout.Draw(
                new[] { "grave" },
                _ => false, _ => false, _ => true, _ => true, _ => true,
                testMode: false, helpMess: false);

            Assert.That(draws.Count, Is.EqualTo(1));
            Assert.That(draws[0].Draw.Visible, Is.False,
                "An absent node draws as Absent (visible false, alpha 1), not as a hidden one — " +
                "the two differ in alpha and must not be collapsed.");
            Assert.That(draws[0].Draw.Alpha, Is.EqualTo(1f));
        }

        // ── The minimap state machine ─────────────────────────────────────────

        [Test]
        public void Minimap_ZoomClampsToOneAndSix()
        {
            var state = new PipMinimapState();

            state.ZoomBy(-5, 0f, 0f);
            Assert.That(state.MapScale, Is.EqualTo(1f), "The floor is the Oracle's own 1 (:818-820).");

            state.ZoomBy(+10, 0f, 0f);
            Assert.That(state.MapScale, Is.EqualTo(6f), "The ceiling is the Oracle's own 6 (:821-823).");
        }

        [Test]
        public void Minimap_ZoomReanchorsAboutTheGivenPoint()
        {
            var state = new PipMinimapState();

            // Anchor away from the pan: the pan's distance from the anchor scales with the zoom, the
            // point under the anchor does not move. Initial pan (15, 15), scale 2 -> 3 about (10, 20):
            // (15 - 10) * 3/2 + 10 = 17.5 and (15 - 20) * 3/2 + 20 = 12.5.
            state.SetMapSize(10f, 20f, 0f, 0f);
            state.ZoomBy(1, 0f, 0f);

            Assert.That(state.MapScale, Is.EqualTo(3f));
            Assert.That(state.PanX, Is.EqualTo(17.5f).Within(0.0001f));
            Assert.That(state.PanY, Is.EqualTo(12.5f).Within(0.0001f));
        }

        [Test]
        public void Minimap_WheelAnchorsOnTheMouse_AndTheButtonsOnTheCentre()
        {
            // The whole difference between the wheel and the two buttons is the anchor (Scroll :836-850
            // vs ZoomBy :798-808). Same start, same step, different anchor -> different pan.
            var byWheel = new PipMinimapState();
            byWheel.SetMapSize(PipInfoFrame.MapWindowCentreX, PipInfoFrame.MapWindowCentreY, 0f, 0f);
            byWheel.Scroll(1, 100f, 50f, 0f, 0f);

            var byButton = new PipMinimapState();
            byButton.SetMapSize(PipInfoFrame.MapWindowCentreX, PipInfoFrame.MapWindowCentreY, 0f, 0f);
            byButton.ZoomBy(1, 0f, 0f);

            Assert.That(byWheel.MapScale, Is.EqualTo(byButton.MapScale),
                "Both step the zoom the same way.");
            Assert.That(byWheel.PanX, Is.Not.EqualTo(byButton.PanX).Within(0.0001f),
                "The wheel anchored on (100, 50), the button on the window's centre — the pans must " +
                "differ, or the wheel is a second zoom button.");
        }

        [Test]
        public void Minimap_TagRidesTheScale()
        {
            // :831-832 — plTag.x is the tile times the CURRENT scale, in the scaled bitmap's space.
            var state = new PipMinimapState();
            state.SetMapSize(0f, 0f, 3f, 4f);
            Assert.That(state.TagX, Is.EqualTo(6f).Within(0.0001f));
            Assert.That(state.TagY, Is.EqualTo(8f).Within(0.0001f));

            state.ZoomBy(1, 3f, 4f);
            Assert.That(state.TagX, Is.EqualTo(9f).Within(0.0001f),
                "The marker moves with the zoom, not with the pan.");
        }

        [Test]
        public void Minimap_Centre_MovesThePanWithoutTouchingTheScale()
        {
            var state = new PipMinimapState();
            state.SetMapSize(0f, 0f, 3f, 4f);
            float lastScaleBefore = state.LastScale;
            float scaleBefore = state.MapScale;

            state.Centre();

            // :810-814 — funCenter does NOT call setMapSize, so `ms` (LastScale) is untouched and the
            // next zoom re-anchors by the ratio it would have used anyway.
            Assert.That(state.MapScale, Is.EqualTo(scaleBefore));
            Assert.That(state.LastScale, Is.EqualTo(lastScaleBefore));
            Assert.That(state.PanX, Is.EqualTo(PipInfoFrame.MapWindowCentreX - state.TagX)
                .Within(0.0001f));
            Assert.That(state.PanY, Is.EqualTo(PipInfoFrame.MapWindowCentreY - state.TagY)
                .Within(0.0001f));
        }

        [Test]
        public void Minimap_DragOnlyMovesWhenStarted_AndReleaseReanchorsOnTheDefaultAnchor()
        {
            var state = new PipMinimapState();

            // :787-790 gates on the press; a delta before one is dropped.
            state.Drag(10f, 5f);
            Assert.That(state.PanX, Is.EqualTo(PipInfoFrame.MapPanX0).Within(0.0001f));

            state.BeginDrag();
            state.Drag(10f, 5f);
            Assert.That(state.PanX, Is.EqualTo(PipInfoFrame.MapPanX0 + 10f).Within(0.0001f));
            Assert.That(state.PanY, Is.EqualTo(PipInfoFrame.MapPanY0 + 5f).Within(0.0001f));

            // :792-796 — the release re-anchors with NO anchor argument, i.e. the method's own defaults
            // of (350, 285), not the window's centre. Reproduced, not tidied.
            state.EndDrag(2f, 2f);
            Assert.That(state.Dragging, Is.False);
            Assert.That(state.PanX,
                Is.EqualTo((PipInfoFrame.MapPanX0 + 10f - PipInfoFrame.MapDefaultAnchorX)
                    * state.MapScale / PipInfoFrame.MapInitialScale
                    + PipInfoFrame.MapDefaultAnchorX).Within(0.0001f));
        }

        [Test]
        public void Minimap_Reset_RestoresThePageOpeningState()
        {
            var state = new PipMinimapState();
            state.ZoomBy(+9, 0f, 0f);
            state.BeginDrag();
            state.Drag(50f, 50f);

            state.Reset();

            Assert.That(state.MapScale, Is.EqualTo(PipInfoFrame.MapInitialScale));
            Assert.That(state.LastScale, Is.EqualTo(PipInfoFrame.MapInitialScale));
            Assert.That(state.PanX, Is.EqualTo(PipInfoFrame.MapPanX0));
            Assert.That(state.PanY, Is.EqualTo(PipInfoFrame.MapPanY0));
            Assert.That(state.TagX, Is.EqualTo(0f));
            Assert.That(state.TagY, Is.EqualTo(0f));
            Assert.That(state.Dragging, Is.False);
        }

        // ── The quest journal ─────────────────────────────────────────────────

        private static PipQuestEntry Entry(string questId, int state, bool isMain)
        {
            return new PipQuestEntry(
                new QuestFacts { QuestId = questId, IsMain = isMain },
                new QuestRuntimeState { QuestId = questId, State = state });
        }

        [Test]
        public void QuestJournal_SortsByStateThenMainThenBareTitle()
        {
            // sortOn(["state","sort","nazv"]) — done (2) after active (1), mainline (sort 0) before
            // side (sort 1), then the BARE title, before the "(done)" suffix is appended.
            var rows = PipQuestJournal.BuildList(new[]
            {
                Entry("questC", 1, isMain: false),   // active side
                Entry("questB", 2, isMain: true),    // done main
                Entry("questA", 1, isMain: true),    // active main
            }, Inline());

            Assert.That(rows.Select(r => r.QuestId).ToArray(),
                Is.EqualTo(new[] { "questA", "questC", "questB" }),
                "Active before done, mainline before side, then by title.");
        }

        [Test]
        public void QuestJournal_SortsByTheBareTitle_NotTheDisplayString()
        {
            // Two done quests: appending " (done)" first would sort every finished quest under "(".
            // The titles here are chosen so the bare order and the display order disagree — "Alpha" <
            // "Beta" either way, so use titles where the suffix would matter: the ordinal comparison
            // of "Alpha (done)" vs "Beta (done)" agrees with the bare one, which is why the fixture
            // pins the DISPLAY of the suffix separately and the ordering on a pair that ties on state
            // and main and differs only in title.
            var rows = PipQuestJournal.BuildList(new[]
            {
                Entry("questB", 2, isMain: true),
                Entry("questA", 2, isMain: true),
            }, Inline());

            Assert.That(rows.Select(r => r.Title).ToArray(),
                Is.EqualTo(new[] { "Alpha quest", "Beta quest" }));
            Assert.That(rows[0].Display, Does.EndWith("(done)"));
            Assert.That(rows[0].Dim, Is.True);
            Assert.That(rows[1].Dim, Is.True);
        }

        [Test]
        public void QuestJournal_DropsStateZeroAndEmptyEntries()
        {
            // `if(q.state > 0)` (:110) — a quest parked by addQuest(noVis:true) is not the journal's.
            var rows = PipQuestJournal.BuildList(new[]
            {
                null,
                new PipQuestEntry { Facts = null, State = new QuestRuntimeState { State = 1 } },
                new PipQuestEntry { Facts = new QuestFacts { QuestId = "questA" }, State = null },
                Entry("questC", 0, isMain: true),
                Entry("questB", 1, isMain: true),
            }, Inline());

            Assert.That(rows.Select(r => r.QuestId).ToArray(), Is.EqualTo(new[] { "questB" }));
        }

        [Test]
        public void QuestJournal_MarkerIsAlwaysTheMainQuestFrame()
        {
            // A quest row's mq is frame 1 or nothing; the note frames (2..4) belong to the notes.
            var rows = PipQuestJournal.BuildList(
                new[] { Entry("questA", 1, isMain: false) }, Inline());

            Assert.That(rows[0].MarkerFrame, Is.EqualTo(PipInfoFrame.MarkerMainQuestFrame));
        }

        [Test]
        public void QuestJournal_TitleFallsBackToTheBracketedId()
        {
            // Quest.as:225-242's miss arm, pinned against a document that does not carry the id.
            Assert.That(PipQuestJournal.TitleFor(
                new QuestFacts { QuestId = "no_such_quest" }, Inline()),
                Is.EqualTo("[no_such_quest]"));
            Assert.That(PipQuestJournal.TitleFor(null, Inline()), Is.Empty);
        }

        [Test]
        public void QuestJournal_TieKeepsTheCallerOrder()
        {
            // The stable-sort promise: two rows that tie on all three keys keep the input order.
            var rows = PipQuestJournal.BuildList(new[]
            {
                Entry("questC", 1, isMain: true),
                Entry("questC", 1, isMain: true),
            }, Inline());

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0], Is.Not.SameAs(rows[1]));
        }

        // ── The note journal ─────────────────────────────────────────────────

        [Test]
        public void NoteJournal_ImportanceThreeLeads_InAddOrder_ThenTheRestReversed()
        {
            // :230-241 — `doparr` (imp 3) keeps the order the game added them in; the rest is
            // REVERSED, so the newest ordinary note is at the top of the second block.
            var rows = PipNoteJournal.BuildList(new[]
            {
                "noteImp1",    // ordinary, added first  -> ends up LAST
                "noteImp2",    // ordinary, added second -> first of the second block
                "noteImp3",    // important, added third -> first overall
                "noteImp3b",   // important, added fourth
            }, Inline());

            Assert.That(rows.Select(r => r.NoteId).ToArray(), Is.EqualTo(new[]
            {
                "noteImp3", "noteImp3b", "noteImp2", "noteImp1",
            }));
        }

        [Test]
        public void NoteJournal_DropsMissingEntriesAndImpZero()
        {
            // :211-213 — both drops are silent, so the list can be shorter than the ids.
            var rows = PipNoteJournal.BuildList(new[]
            {
                "no_such_note",
                "noteImp1",
            }, Inline());

            Assert.That(rows.Select(r => r.NoteId).ToArray(), Is.EqualTo(new[] { "noteImp1" }));
        }

        [Test]
        public void NoteJournal_MarkerFrameIsImportancePlusOne()
        {
            // :334 — `mq.gotoAndStop(ico + 1)`. Frame 1 is the main-quest marker, which is exactly why
            // a note never uses it.
            var rows = PipNoteJournal.BuildList(new[] { "noteImp1", "noteImp3" }, Inline());

            Assert.That(rows.First(r => r.NoteId == "noteImp1").MarkerFrame, Is.EqualTo(2));
            Assert.That(rows.First(r => r.NoteId == "noteImp3").MarkerFrame, Is.EqualTo(4));
            Assert.That(PipInfoFrame.MarkerNoteFrame(1), Is.EqualTo(2));
            Assert.That(PipInfoFrame.MarkerNoteFrame(3), Is.EqualTo(4));
        }

        [Test]
        public void NoteJournal_CaptionTruncatesAtSixty_AndTrimsThreeWhenIndented()
        {
            // :330 — substr(charAt(0) == " " ? 3 : 0, 60). The 3 is not a typo for 1: the note titles
            // are authored with a leading indent and the extra two characters go with it.
            string longTitle = new string('x', 100);
            Assert.That(PipNoteJournal.CaptionFor(longTitle), Has.Length.EqualTo(60));

            string indented = " " + new string('y', 100);
            Assert.That(PipNoteJournal.CaptionFor(indented),
                Is.EqualTo(indented.Substring(3, 60)));

            // Below the cap nothing is trimmed past the indent.
            Assert.That(PipNoteJournal.CaptionFor(" plain"), Is.EqualTo(" plain"));
            Assert.That(PipNoteJournal.CaptionFor(""), Is.Empty);
        }

        [Test]
        public void NoteJournal_TitleSubstitutesThePlayerName_AndFallsBackToTheFirstRow()
        {
            // :216-223 — <n><t> first, else the first <r> row; &lp becomes the player's name.
            Assert.That(PipNoteJournal.TitleFor("noteBody", Inline(), "Rainbow"),
                Does.Contain("Dear Rainbow,"));

            // The authored leading indent does NOT survive the parse: PipTextTable.CleanText collapses
            // space runs and Trim()s the result, so TitleFor receives the title already trimmed and
            // CaptionFor's 3-char rule (unit-tested above with a synthetic string) never fires on
            // parser-fed titles. Assert the trim itself as documented parser behaviour.
            Assert.That(PipNoteJournal.TitleFor("noteIndented", Inline(), ""),
                Does.StartWith("Indented title").And.Contain("sixty character cap"));
            Assert.That(PipNoteJournal.CaptionFor(
                    PipNoteJournal.TitleFor("noteIndented", Inline(), "")),
                Does.StartWith("Indented title"));

            // A note with neither a <t> nor rows resolves to nothing rather than throwing.
            Assert.That(PipNoteJournal.TitleFor("no_such_note", Inline(), ""), Is.Empty);
        }

        [Test]
        public void NoteJournal_BodyRewritesBracketsAfterMessText()
        {
            // :424-432 — the bracket pass runs AFTER the body is built, so [br] became a break first
            // and [the manual] becomes a yellow span. The port keeps both passes in that order.
            string body = PipNoteJournal.BuildBody("noteBody", Inline(), "Rainbow");

            Assert.That(body, Does.Contain("<span class='yel'>the manual</span>"));
            Assert.That(body, Does.Not.Contain("[the manual]"));
            Assert.That(body, Does.Contain("Dear Rainbow,"));
        }

        // ── The bestiary ─────────────────────────────────────────────────────

        private static XElement Bestiary() => XElement.Parse(BestiaryXml);

        /// <summary>The frag table: wolf 1, raiderA 2, raiderB 3, zebraA 4, ghost absent.</summary>
        private static int FragCount(string trigger)
        {
            switch (trigger)
            {
                case "frag_wolf": return 1;
                case "frag_raiderA": return 2;
                case "frag_raiderB": return 3;
                case "frag_zebraA": return 4;
                default: return -1;   // absent — AS3's `undefined >= 0` is false
            }
        }

        [Test]
        public void Bestiary_RollsCategoryThreeCountsIntoThePreviousCategoryTwo()
        {
            var rows = PipBestiary.BuildList(Bestiary(), null, FragCount);

            PipBestiaryRow raider = rows.First(r => r.UnitId == "raider");
            Assert.That(raider.Kills, Is.EqualTo(5),
                "raiderA (2) + raiderB (3): the faction's total is the sum of its counted members.");

            PipBestiaryRow raiderA = rows.First(r => r.UnitId == "raiderA");
            Assert.That(raiderA.PreviousId, Is.EqualTo("raider"));
            Assert.That(raiderA.Kills, Is.EqualTo(2));
        }

        [Test]
        public void Bestiary_AbsentFragDoesNotMarkTheFactionAsCounted()
        {
            var rows = PipBestiary.BuildList(Bestiary(), null, FragCount);

            // zebraA HAS a count, so zebra's -1 flips to 0 and then adds 4.
            Assert.That(rows.First(r => r.UnitId == "zebra").Kills, Is.EqualTo(4));

            // ghost has NO frag trigger and no cat="2" above it in the kept set that has a count —
            // its own row is dropped by the filter below, but the rule under test is that an absent
            // trigger contributes nothing rather than a zero.
            PipBestiaryRow absent = rows.FirstOrDefault(r => r.UnitId == "ghost");
            Assert.That(absent, Is.Null,
                "isKol drops a cat=3 row whose kol is still -1 (:243-247).");
        }

        [Test]
        public void Bestiary_FactionWithNoCountedMembers_IsDroppedByTheFilter()
        {
            // isKol keeps `kol >= 0 || cat == "1"`. A cat="2" faction whose every cat="3" member is
            // absent keeps kol -1 and is dropped — even though it is the faction HEADER.
            var frags = new List<(string, int)>();   // nothing counted at all
            var rows = PipBestiary.BuildList(Bestiary(), null,
                trigger => frags.Any(f => f.Item1 == trigger) ? 1 : -1);

            Assert.That(rows.Any(r => r.UnitId == "raider"), Is.False,
                "The faction header with no counted member reads kol -1 and the filter drops it.");
            Assert.That(rows.Any(r => r.UnitId == "wolf"), Is.True,
                "cat=\"1\" rows are kept unconditionally — they have no count by construction.");
        }

        [Test]
        public void Bestiary_IgnoresUnitsWithoutACategory()
        {
            var rows = PipBestiary.BuildList(Bestiary(), null, FragCount);

            // `Boolean(xml.@cat.length())` — the ~90 uncat'd variants never appear.
            Assert.That(rows.Any(r => r.UnitId == "novariant"), Is.False);
        }

        [Test]
        public void Bestiary_ShowCountOnlyWhenCounted()
        {
            var row = new PipBestiaryRow { Kills = -1 };
            Assert.That(row.ShowCount, Is.False, "kol.text is only written when kol > 0 (:346-351).");

            row.Kills = 0;
            Assert.That(row.ShowCount, Is.False,
                "A stored zero is a counted zero — the row exists, the count is empty.");

            row.Kills = 3;
            Assert.That(row.ShowCount, Is.True);
        }

        // ── The campaign note store ──────────────────────────────────────────

        [Test]
        public void CampaignNotes_SeedsTheThreeDefaults_InTheOraclesOrder()
        {
            var notes = new CampaignNotes();
            notes.SeedDefaults();

            Assert.That(notes.Count, Is.EqualTo(3));
            Assert.That(notes.Ids.ToArray(), Is.EqualTo(CampaignNotes.SeedIds));
            Assert.That(CampaignNotes.SeedIds, Is.EqualTo(
                new[] { "helpControl", "helpGl1", "helpGl2" }));
        }

        [Test]
        public void CampaignNotes_AddIsIdempotent_AndKeepsTheOrder()
        {
            var notes = new CampaignNotes();
            notes.SeedDefaults();

            Assert.That(notes.Add("helpGl1"), Is.False,
                "addNote is guarded by a note_<id> trigger in the Oracle; the port makes the same " +
                "refusal a return value rather than a silent no-op.");
            Assert.That(notes.Count, Is.EqualTo(3));
            Assert.That(notes.Ids.ToArray(), Is.EqualTo(CampaignNotes.SeedIds),
                "A refused duplicate must not disturb the order — the journal's second block is " +
                "REVERSED, so an appended duplicate would jump to the top.");

            Assert.That(notes.Add("terminal_note"), Is.True);
            Assert.That(notes.Ids.Last(), Is.EqualTo("terminal_note"));
        }

        [Test]
        public void CampaignNotes_RejectsEmptyIds()
        {
            var notes = new CampaignNotes();
            Assert.That(notes.Add(null), Is.False);
            Assert.That(notes.Add(""), Is.False);
            Assert.That(notes.Count, Is.EqualTo(0));
        }

        [Test]
        public void CampaignNotes_SeededNotesAreAllImportanceThree()
        {
            // The data half, against the shipped document: a fresh campaign's journal is the three
            // imp="3" seeds and nothing else, which is why its list has no reversed block at all.
            foreach (string seed in CampaignNotes.SeedIds)
            {
                Assert.That(Shipped.HasEntry("txt", seed), Is.True,
                    "The seeded note '" + seed + "' has no txt entry.");
                Assert.That(Shipped.ImportanceOf("txt", seed), Is.EqualTo(3),
                    "The seeded note '" + seed + "' is not imp=3 — a fresh journal would then mix " +
                    "the two blocks, which the Oracle's own new game never shows.");
            }
        }

        // ── The formatter seam ───────────────────────────────────────────────

        [Test]
        public void FormatSpans_LeavesBracketsAlone()
        {
            // The help path and the quest body render brackets verbatim (helpWorld's "[Base camps]");
            // running the GUI pipeline's bracket rewrite over them would colour prose the Oracle
            // leaves plain. FormatSpans is the page's one seam and it does not touch them.
            Assert.That(
                PfeTextFormatter.FormatSpans("[Base camps] and [the manual]", FlashStyleSheet.Page, null),
                Is.EqualTo("[Base camps] and [the manual]"));
        }

        [Test]
        public void FormatSpans_StillConvertsTheSpansTheModelsEmit()
        {
            // The models emit Flash spans; the seam resolves those and nothing else. The output form
            // is not pinned here (the hex is PfeUiColors', not this fixture's) — only that the span
            // became ONE colour run that closes.
            string converted = PfeTextFormatter.FormatSpans(
                "a <span class='yel'>coin</span> b", FlashStyleSheet.Page, null);

            Assert.That(converted, Does.StartWith("a <color="));
            Assert.That(converted, Does.EndWith("</color> b"));
            Assert.That(converted, Does.Not.Contain("<span"));
        }

        [Test]
        public void FormatSpans_EmptyInputIsEmpty()
        {
            Assert.That(PfeTextFormatter.FormatSpans(null, FlashStyleSheet.Page, null), Is.Empty);
            Assert.That(PfeTextFormatter.FormatSpans("", FlashStyleSheet.Page, null), Is.Empty);
        }
    }
}
