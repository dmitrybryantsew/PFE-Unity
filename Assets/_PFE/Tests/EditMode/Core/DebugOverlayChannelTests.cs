using NUnit.Framework;
using PFE.Core;
using UnityEngine;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The overlay channel mask. The point of these tests is the **independence contract** — the user's
    /// report was literally "units and tiles should be able to work at the same time, they toggle as an
    /// option, not one at a time" — plus the parser's promise that an unknown token fails *totally*
    /// rather than leaving some channels on.
    /// </summary>
    [TestFixture]
    public class DebugOverlayChannelTests
    {
        /// <summary>Every channel, so a new member that is not covered here is itself a test failure.</summary>
        private static readonly DebugOverlayChannel[] EveryChannel =
        {
            DebugOverlayChannel.Tiles,
            DebugOverlayChannel.Units,
            DebugOverlayChannel.Doors,
            DebugOverlayChannel.Triggers,
            DebugOverlayChannel.Transitions,
            DebugOverlayChannel.Objects,
            DebugOverlayChannel.TileQuery,
            DebugOverlayChannel.RoomData,
            DebugOverlayChannel.PoolData,
            DebugOverlayChannel.Clock,
            DebugOverlayChannel.Legend,
            DebugOverlayChannel.LowLevelPhysics,
            DebugOverlayChannel.DamageNumbers,
            DebugOverlayChannel.UnitHealth,
            DebugOverlayChannel.EnemyAI,
        };

        private PfeDebugSettings _settings;

        /// <summary>
        /// A throwaway settings instance, created on <b>first use</b> rather than in <c>[SetUp]</c>.
        ///
        /// <para><b>Why lazy, and why it matters.</b> <c>ScriptableObject.CreateInstance</c> is an
        /// engine call. With it in <c>[SetUp]</c> the whole fixture died outside the editor with
        /// <c>SecurityException: ECall methods must be packaged into a system module</c> — including the
        /// two dozen tests below that never touch a settings object at all (parsing, formatting, the
        /// catalogue round-trip). Those are precisely the guards that catch "added a channel and forgot
        /// the <c>TryMatch</c> case", and they were unverifiable offline for no reason. The ECall is
        /// scoped to the method that <i>mentions</i> it, so isolating it in
        /// <see cref="CreateSettings"/> lets every test that does not ask for settings execute anywhere,
        /// while the tests that do still run in Unity.</para>
        /// </summary>
        private PfeDebugSettings Settings
        {
            get
            {
                if (_settings == null) _settings = CreateSettings();
                return _settings;
            }
        }

        /// <summary>The only method in this file that mentions an engine call.</summary>
        private static PfeDebugSettings CreateSettings()
        {
            // A throwaway instance, never the project asset: mutating the real ScriptableObject here
            // would leak into every later test in the domain through DebugOverlays' cached reference.
            return ScriptableObject.CreateInstance<PfeDebugSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null) return;   // a test that never asked for settings has nothing to free

            // Fully qualified: `using PFE.Core;` plus `using UnityEngine;` makes a bare `Object`
            // ambiguous, and the compiler would resolve it by erroring at the call site.
            UnityEngine.Object.DestroyImmediate(_settings);
            _settings = null;
        }

        // ── Default ──────────────────────────────────────────────────────────

        [Test]
        public void Default_IsNone_SoTheGameDrawsNothing()
        {
            // "default is a game, rest is togglable from console" — the requirement, as an assertion.
            Assert.AreEqual(DebugOverlayChannel.None, Settings.EnabledOverlays);

            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                Assert.IsFalse(Settings.IsOverlayEnabled(channel), $"{channel} must default to off");
            }
        }

        [Test]
        public void Default_LeavesEveryLegacyFacadeFalse()
        {
            Assert.IsFalse(Settings.ShowTileColliderDebug);
            Assert.IsFalse(Settings.ShowUnitColliderDebug);
            Assert.IsFalse(Settings.ShowAreaTriggerDebug);
            Assert.IsFalse(Settings.ShowDoorColliderDebug);
            Assert.IsFalse(Settings.ShowObjectColliderDebug);
            Assert.IsFalse(Settings.ShowEnemyAIDebug);
            Assert.IsFalse(Settings.SimTickOverlayEnabled);
        }

        // ── Independence: the contract the user asked for ────────────────────

        [Test]
        public void TilesAndUnits_CanBeOnAtTheSameTime()
        {
            Settings.EnabledOverlays |= DebugOverlayChannel.Tiles;
            Settings.EnabledOverlays |= DebugOverlayChannel.Units;

            Assert.IsTrue(Settings.IsOverlayEnabled(DebugOverlayChannel.Tiles));
            Assert.IsTrue(Settings.IsOverlayEnabled(DebugOverlayChannel.Units));
        }

        [Test]
        public void TilesAndUnits_CanBeOnAtTheSameTime_ThroughTheLegacyFacades()
        {
            // The exact spellings the four existing presenters and the F5/F6 hotkeys use.
            Settings.ShowTileColliderDebug = true;
            Settings.ShowUnitColliderDebug = true;

            Assert.IsTrue(Settings.ShowTileColliderDebug);
            Assert.IsTrue(Settings.ShowUnitColliderDebug);
        }

        [Test]
        public void SetOverlay_TurningOneOn_LeavesEveryOtherChannelExactlyAsItWas()
        {
            // Turn them all on, then flip one on that is already on: nothing may change.
            Settings.EnabledOverlays = DebugOverlayChannel.All;

            Settings.SetOverlay(DebugOverlayChannel.Doors, true);

            Assert.AreEqual(DebugOverlayChannel.All, Settings.EnabledOverlays);
        }

        [Test]
        public void SetOverlay_TurningOneOff_LeavesEveryOtherChannelExactlyAsItWas()
        {
            Settings.EnabledOverlays = DebugOverlayChannel.All;

            Settings.SetOverlay(DebugOverlayChannel.Doors, false);

            Assert.AreEqual(DebugOverlayChannel.All & ~DebugOverlayChannel.Doors, Settings.EnabledOverlays);

            // And every sibling survived, which is the part that matters.
            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                if (channel == DebugOverlayChannel.Doors) continue;
                Assert.IsTrue(Settings.IsOverlayEnabled(channel), $"{channel} must survive turning Doors off");
            }
        }

        [Test]
        public void EveryChannel_IsAnIndependentBit()
        {
            // Walk the mask one channel at a time and assert each lands alone. A copy-paste that gave
            // two channels the same 1 << n would show up here and nowhere else.
            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                Settings.EnabledOverlays = DebugOverlayChannel.None;
                Settings.SetOverlay(channel, true);

                Assert.AreEqual(channel, Settings.EnabledOverlays,
                    $"{channel} must occupy its own bit");
            }
        }

        [Test]
        public void EachLegacyFacade_TurnsOnExactlyItsOwnChannel()
        {
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.Triggers, s => s.ShowAreaTriggerDebug = true);
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.Doors, s => s.ShowDoorColliderDebug = true);
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.Objects, s => s.ShowObjectColliderDebug = true);
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.Tiles, s => s.ShowTileColliderDebug = true);
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.Units, s => s.ShowUnitColliderDebug = true);

            // The facades are listed EXPLICITLY, not reflected over, so adding one to
            // PfeDebugSettings does not add it here — and a test called "EachLegacyFacade" that
            // silently skips the newest facade is a stale claim rather than a passing test.
            AssertFacadeTurnsOnExactly(DebugOverlayChannel.EnemyAI, s => s.ShowEnemyAIDebug = true);
        }

        [Test]
        public void SimTickOverlayEnabled_ReflectsTheClockChannel()
        {
            // Read-only by design: the clock overlay has no setter, because a second way to switch it
            // would be a second copy of the state. So it is tested as a reader, not a writer — the
            // compiler rejected the setter form of this test, which is how the distinction surfaced.
            Settings.EnabledOverlays = DebugOverlayChannel.None;
            Assert.IsFalse(Settings.SimTickOverlayEnabled);

            Settings.SetOverlay(DebugOverlayChannel.Clock, true);
            Assert.IsTrue(Settings.SimTickOverlayEnabled);

            Settings.SetOverlay(DebugOverlayChannel.Clock, false);
            Assert.IsFalse(Settings.SimTickOverlayEnabled);

            // And it tracks the Clock bit specifically, not "any overlay at all".
            Settings.SetOverlay(DebugOverlayChannel.Tiles, true);
            Assert.IsFalse(Settings.SimTickOverlayEnabled);
        }

        /// <summary>
        /// Set one facade on a clean instance and assert the mask is that channel and *nothing else* —
        /// the whole point of the facades is that they are one bit, not a parallel copy of the state.
        /// </summary>
        private void AssertFacadeTurnsOnExactly(DebugOverlayChannel expected, System.Action<PfeDebugSettings> turnOn)
        {
            Settings.EnabledOverlays = DebugOverlayChannel.None;
            turnOn(_settings);

            Assert.AreEqual(expected, Settings.EnabledOverlays,
                $"the {expected} facade must set exactly its own bit");
        }

        // ── The mask's own arithmetic ────────────────────────────────────────

        [Test]
        public void All_ContainsEveryChannel()
        {
            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                Assert.IsTrue((DebugOverlayChannel.All & channel) != 0,
                    $"All must include {channel}");
            }
        }

        [Test]
        public void All_IsExactlyTheUnionOfEveryChannel()
        {
            DebugOverlayChannel union = DebugOverlayChannel.None;
            foreach (DebugOverlayChannel channel in EveryChannel) union |= channel;

            Assert.AreEqual(union, DebugOverlayChannel.All);
        }

        [Test]
        public void None_MatchesNoChannel()
        {
            Settings.EnabledOverlays = DebugOverlayChannel.None;

            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                Assert.IsFalse(Settings.IsOverlayEnabled(channel), $"{channel} must be off under None");
            }
        }

        [Test]
        public void IsOverlayEnabled_WithNone_IsFalse()
        {
            // (x & 0) != 0 is always false; asserted so nobody "optimises" it into something else.
            Settings.EnabledOverlays = DebugOverlayChannel.All;
            Assert.IsFalse(Settings.IsOverlayEnabled(DebugOverlayChannel.None));
        }

        // ── Parsing ──────────────────────────────────────────────────────────

        [Test]
        public void TryParse_Empty_MeansAll()
        {
            foreach (string text in new[] { null, "", "   ", "\t" })
            {
                Assert.IsTrue(DebugOverlayChannels.TryParse(text, out DebugOverlayChannel channels, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(DebugOverlayChannel.All, channels);
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_AllTokens_MeanAll()
        {
            foreach (string text in new[] { "all", "*", "everything", "on", "ALL", "All" })
            {
                Assert.IsTrue(DebugOverlayChannels.TryParse(text, out DebugOverlayChannel channels, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(DebugOverlayChannel.All, channels, $"'{text}' should mean All");
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_OffTokens_MeanNone()
        {
            foreach (string text in new[] { "off", "none", "0", "false", "hide" })
            {
                Assert.IsTrue(DebugOverlayChannels.TryParse(text, out DebugOverlayChannel channels, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(DebugOverlayChannel.None, channels, $"'{text}' should mean None");
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_IsCaseInsensitive()
        {
            Assert.IsTrue(DebugOverlayChannels.TryParse("Tiles", out DebugOverlayChannel channels, out _));
            Assert.AreEqual(DebugOverlayChannel.Tiles, channels);

            Assert.IsTrue(DebugOverlayChannels.TryParse("TILES", out channels, out _));
            Assert.AreEqual(DebugOverlayChannel.Tiles, channels);
        }

        [Test]
        public void TryParse_AcceptsEverySeparatorTheConsoleMightSee()
        {
            foreach (string text in new[] { "tiles,units", "tiles units", "tiles;units", "tiles+units", "tiles|units" })
            {
                Assert.IsTrue(DebugOverlayChannels.TryParse(text, out DebugOverlayChannel channels, out string error),
                    $"'{text}' should parse: {error}");
                Assert.AreEqual(DebugOverlayChannel.Tiles | DebugOverlayChannel.Units, channels,
                    $"'{text}' should be tiles+units");
            }
        }

        [Test]
        public void TryParse_IsOrderIndependent()
        {
            DebugOverlayChannels.TryParse("tiles,units,doors", out DebugOverlayChannel a, out _);
            DebugOverlayChannels.TryParse("doors,units,tiles", out DebugOverlayChannel b, out _);
            DebugOverlayChannels.TryParse("units,doors,tiles", out DebugOverlayChannel c, out _);

            Assert.AreEqual(a, b);
            Assert.AreEqual(a, c);
        }

        [Test]
        public void TryParse_UnknownToken_FailsAndNamesTheToken()
        {
            Assert.IsFalse(DebugOverlayChannels.TryParse("triggerz", out DebugOverlayChannel channels, out string error));

            Assert.AreEqual(DebugOverlayChannel.None, channels, "a failed parse must not leave channels on");
            StringAssert.Contains("triggerz", error);
            StringAssert.Contains("Unknown", error);
        }

        [Test]
        public void TryParse_UnknownTokenAmongGoodOnes_DoesNotPartiallySucceed()
        {
            // The nasty case: "tiles,triggerz" must not silently turn tiles on and drop the typo. That
            // reads as "the overlay is on and shows nothing", which is indistinguishable from the bug
            // the overlay was opened to find.
            Assert.IsFalse(DebugOverlayChannels.TryParse("tiles,triggerz", out DebugOverlayChannel channels, out string error));

            Assert.AreEqual(DebugOverlayChannel.None, channels);
            StringAssert.Contains("triggerz", error);
        }

        [Test]
        public void TryMatch_Unknown_ReturnsFalseWithNone()
        {
            // The switch's `default` arm. Without it a new enum member could silently inherit a branch.
            Assert.IsFalse(DebugOverlayChannels.TryMatch("triggerz", out DebugOverlayChannel channel));
            Assert.AreEqual(DebugOverlayChannel.None, channel);

            Assert.IsFalse(DebugOverlayChannels.TryMatch("", out channel));
            Assert.AreEqual(DebugOverlayChannel.None, channel);
        }

        [Test]
        public void TryMatch_Help_IsNotAChannel()
        {
            // The console's shortcut dispatcher matches `help` as a verb before any channel matcher
            // sees it, so an alias for it could only ever be a name that does not do what it says.
            Assert.IsFalse(DebugOverlayChannels.TryMatch("help", out _));
        }

        // ── The catalogue and the matcher must not drift ─────────────────────

        [Test]
        public void EveryChannel_HasACanonicalNameThatParsesBackToIt()
        {
            // This is the test that catches "added an enum member and a catalogue row, forgot the
            // TryMatch case" — the name would be listed in help but rejected when typed.
            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                string name = DebugOverlayChannels.NameOf(channel);

                Assert.IsTrue(DebugOverlayChannels.TryParse(name, out DebugOverlayChannel parsed, out string error),
                    $"canonical name '{name}' for {channel} must parse: {error}");
                Assert.AreEqual(channel, parsed, $"'{name}' must parse back to {channel}");
            }
        }

        [Test]
        public void EveryChannel_AppearsInTheNameListShownToTheUser()
        {
            string list = DebugOverlayChannels.NameList();

            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                StringAssert.Contains(DebugOverlayChannels.NameOf(channel), list);
            }
        }

        [Test]
        public void Aliases_ResolveToTheExpectedChannel()
        {
            // Aliases are not politeness: they are what someone actually types. Every one of these is
            // a literal `case` label in TryMatch, so this also pins the switch against deletion.
            AssertAliases(DebugOverlayChannel.Tiles, "tiles", "tile", "t");
            AssertAliases(DebugOverlayChannel.Units, "units", "unit", "u", "npc", "npcs", "nps", "mobs");
            AssertAliases(DebugOverlayChannel.Doors, "doors", "door");
            AssertAliases(DebugOverlayChannel.Triggers, "triggers", "trigger", "areas", "area", "exit", "exits", "zones", "zone");
            AssertAliases(DebugOverlayChannel.Transitions, "transitions", "transition", "edges", "edge", "bounds", "boundary");
            AssertAliases(DebugOverlayChannel.Objects, "objects", "object", "boxes", "box", "crates", "crate",
                                                      "props", "prop", "barricades", "containers");
            AssertAliases(DebugOverlayChannel.TileQuery, "tilequery", "query", "colliderhelp");
            AssertAliases(DebugOverlayChannel.RoomData, "room", "rooms", "roomdata", "streaming");
            AssertAliases(DebugOverlayChannel.PoolData, "pool", "pooldata", "pooling");
            AssertAliases(DebugOverlayChannel.Clock, "clock", "simclock", "sim", "fps");
            AssertAliases(DebugOverlayChannel.Legend, "legend", "key");
            AssertAliases(DebugOverlayChannel.EnemyAI, "ai", "enemyai", "enemy-ai",
                                                       "enemy", "enemies", "brain", "brains");
        }

        private static void AssertAliases(DebugOverlayChannel expected, params string[] aliases)
        {
            foreach (string alias in aliases)
            {
                Assert.IsTrue(DebugOverlayChannels.TryMatch(alias, out DebugOverlayChannel channel),
                    $"alias '{alias}' should be recognised");
                Assert.AreEqual(expected, channel, $"alias '{alias}' should mean {expected}");
            }
        }

        // ── Formatting ───────────────────────────────────────────────────────

        [Test]
        public void Format_NoneAndAll_UseTheShortWords()
        {
            Assert.AreEqual("off", DebugOverlayChannels.Format(DebugOverlayChannel.None));
            Assert.AreEqual("all", DebugOverlayChannels.Format(DebugOverlayChannel.All));
        }

        [Test]
        public void Format_RoundTripsThroughTryParse()
        {
            // Whatever `col` prints as the current state must be accepted back verbatim. This is the
            // property that makes copy-pasting the status line into the next command work.
            var samples = new[]
            {
                DebugOverlayChannel.None,
                DebugOverlayChannel.All,
                DebugOverlayChannel.Tiles,
                DebugOverlayChannel.Tiles | DebugOverlayChannel.Units,
                DebugOverlayChannel.Legend,
                DebugOverlayChannel.Tiles | DebugOverlayChannel.Units | DebugOverlayChannel.Doors
                    | DebugOverlayChannel.Triggers | DebugOverlayChannel.Transitions | DebugOverlayChannel.Objects,
                DebugOverlayChannel.TileQuery | DebugOverlayChannel.RoomData
                    | DebugOverlayChannel.PoolData | DebugOverlayChannel.Clock,
            };

            foreach (DebugOverlayChannel sample in samples)
            {
                string text = DebugOverlayChannels.Format(sample);

                Assert.IsTrue(DebugOverlayChannels.TryParse(text, out DebugOverlayChannel parsed, out string error),
                    $"'{text}' should parse back: {error}");
                Assert.AreEqual(sample, parsed, $"'{text}' must round-trip to {sample}");
            }
        }

        [Test]
        public void Format_NamesEveryBitItIsGiven()
        {
            Assert.AreEqual("tiles,units", DebugOverlayChannels.Format(DebugOverlayChannel.Tiles | DebugOverlayChannel.Units));
            Assert.AreEqual("tiles", DebugOverlayChannels.Format(DebugOverlayChannel.Tiles));
        }

        // ── Status text ──────────────────────────────────────────────────────

        [Test]
        public void DescribeAll_MarksExactlyTheEnabledChannels()
        {
            string text = DebugOverlayChannels.DescribeAll(DebugOverlayChannel.Tiles | DebugOverlayChannel.Clock);

            StringAssert.Contains("[x] tiles", text);
            StringAssert.Contains("[x] clock", text);
            StringAssert.Contains("[ ] units", text);
            StringAssert.Contains("[ ] legend", text);
        }

        [Test]
        public void DescribeAll_WithNone_MarksNothingOn()
        {
            string text = DebugOverlayChannels.DescribeAll(DebugOverlayChannel.None);

            StringAssert.DoesNotContain("[x]", text);
        }

        [Test]
        public void DescribeAll_CoversEveryChannel()
        {
            string text = DebugOverlayChannels.DescribeAll(DebugOverlayChannel.None);

            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                StringAssert.Contains(DebugOverlayChannels.NameOf(channel), text);
            }
        }

        [Test]
        public void Usage_ListsEveryChannel()
        {
            string usage = DebugOverlayChannels.Usage();

            foreach (DebugOverlayChannel channel in EveryChannel)
            {
                StringAssert.Contains(DebugOverlayChannels.NameOf(channel), usage);
            }
        }
    }
}
