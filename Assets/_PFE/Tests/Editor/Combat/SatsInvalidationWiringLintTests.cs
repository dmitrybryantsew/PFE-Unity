using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>§3.11 ledger</b>: that every way S.A.T.S. can drop a queued plan has a call site in
    /// the shipping code, and that no call site names a reason that is not a real trigger.
    ///
    /// <para><b>Why a lint and not a behavioural test.</b> Each trigger lives in a
    /// <c>MonoBehaviour</c> — the player's message handlers, the weapon loadout's <c>Equip</c>. Outside
    /// the editor those need a <c>GameObject</c> and a component graph and throw
    /// <c>ECall methods must be packaged into a system module</c> before reaching an assertion, so the
    /// one thing a fixture <i>cannot</i> see is whether the call is present at all. That is precisely
    /// what is missing when a plan fires after the player teleported: nothing throws, the arithmetic is
    /// perfect, and the queue simply survives something it should not. The arithmetic the triggers feed
    /// is pinned elsewhere — <c>SatsRulesTests</c> and the offline <c>satsverify</c> harness.</para>
    ///
    /// <para><b>Both directions are asserted.</b> A list of "these calls must exist" can be satisfied by
    /// a member that was invented and then wired to itself; a list of "these members must be exactly
    /// these" can be satisfied by a fixture that reads nothing. The member list is pinned to a literal
    /// and each member is then required at a named file, so deleting a call site and deleting a member
    /// both fail.</para>
    ///
    /// <para><b>Two members are deliberately unwired, and the fixture says which and why</b> — an
    /// exemption that is asserted is a decision, where an exemption that is merely absent is a bug
    /// waiting to be mistaken for one.</para>
    /// </summary>
    [TestFixture]
    public class SatsInvalidationWiringLintTests
    {
        private const string ControllerPath = "Systems/Combat/Sats/SatsController.cs";
        private const string PlayerPath = "Entities/Player/PlayerController.cs";
        private const string LoadoutPath = "Systems/Weapons/PlayerWeaponLoadout.cs";

        /// <summary>
        /// Every member of <c>SatsInvalidationReason</c>, in declaration order, as a literal.
        ///
        /// <para>This is the <b>writer</b> side of the scan pinned independently of the scan: if a
        /// trigger is added to the enum and nowhere else, the extraction below still reports it and the
        /// per-member assertion fails, but if the <i>extractor</i> silently broke and returned nothing
        /// this list would be the only thing still checking the enum's shape.</para>
        /// </summary>
        private static readonly string[] ExpectedMembers =
        {
            "Interact",
            "Teleport",
            "Grenade",
            "MagicWeapon",
            "DefendSpell",
            "SpellHotkey",
            "EmpHit",
            "WeaponChange",
            "PaintWeaponChange",
            "LocationExit",
            "WeaponInvalid",
            "CannotAfford",
            "ManualFire",
            "Reentry",
        };

        /// <summary>
        /// Members with no call site, each for a reason recorded in the enum. Asserted so that a new
        /// exemption cannot be introduced by simply deleting a call.
        /// </summary>
        private static readonly string[] DeliberatelyUnwired = { "EmpHit", "PaintWeaponChange" };

        /// <summary>Members whose call site is inside the controller itself.</summary>
        private static readonly string[] WiredInsideController = { "WeaponInvalid", "CannotAfford", "Reentry" };

        // ── 1. The enum is exactly what the ledger says it is ───────────────────

        [Test]
        public void Enum_DeclaresExactlyThePinnedMembers()
        {
            List<string> actual = ExtractEnumMembers(SourceLint.ReadStripped(ControllerPath));

            Assert.That(actual, Is.EqualTo(ExpectedMembers),
                "SatsInvalidationReason no longer matches the pinned ledger. Adding a trigger means " +
                "adding its call site in the same change; this list is the reminder, not an obstacle.");
        }

        // ── 2. Every wired member has a call site ───────────────────────────────

        [Test]
        public void EveryWiredMember_HasACallSite()
        {
            string player = SourceLint.ReadStripped(PlayerPath);
            string loadout = SourceLint.ReadStripped(LoadoutPath);
            string controller = SourceLint.ReadStripped(ControllerPath);

            foreach (string member in ExpectedMembers)
            {
                if (Array.IndexOf(DeliberatelyUnwired, member) >= 0) continue;

                string needle = "SatsInvalidationReason." + member;

                bool found =
                    player.Contains(needle) ||
                    loadout.Contains(needle) ||
                    controller.Contains(needle);

                Assert.IsTrue(found,
                    "SatsInvalidationReason." + member + " is declared but never used. A plan that " +
                    "survives the action the player took to cancel it fires anyway, silently, with no " +
                    "exception to trace — which is why this is a lint and not something a play-test " +
                    "would reliably catch.");
            }
        }

        // ── 3. Each trigger is at the handler it belongs to ─────────────────────

        [Test]
        public void PlayerTriggers_AreInPlayerController()
        {
            string player = SourceLint.ReadStripped(PlayerPath);

            foreach (string member in new[]
                     {
                         "Interact", "Teleport", "Grenade", "MagicWeapon",
                         "DefendSpell", "SpellHotkey", "LocationExit", "ManualFire",
                     })
            {
                Assert.IsTrue(player.Contains("SatsInvalidationReason." + member),
                    "SatsInvalidationReason." + member + " must be raised from PlayerController, where " +
                    "the Oracle raises it (UnitPlayer.as). Moving it elsewhere is not wrong in " +
                    "principle, but it must be a deliberate edit to this fixture rather than a quiet " +
                    "relocation that leaves the ledger pointing at the wrong file.");
            }
        }

        [Test]
        public void WeaponChange_IsInTheLoadoutEquip()
        {
            string loadout = SourceLint.ReadStripped(LoadoutPath);

            Assert.IsTrue(loadout.Contains("SatsInvalidationReason.WeaponChange"),
                "Equip() is the port's changeWeapon(). AS3 clears the plan as the first statement of " +
                "changeWeapon (:3588-3592), before the spell branch, so selecting a spell also " +
                "cancels a plan. Wiring this anywhere else loses the spell case.");
        }

        [Test]
        public void ControllerOwnedTriggers_AreInsideTheController()
        {
            string controller = SourceLint.ReadStripped(ControllerPath);

            foreach (string member in WiredInsideController)
            {
                Assert.IsTrue(controller.Contains("SatsInvalidationReason." + member),
                    "SatsInvalidationReason." + member + " is decided by the mode's own tick, so its " +
                    "call site belongs in SatsController and nowhere else.");
            }
        }

        // ── 4. Absent controls ──────────────────────────────────────────────────

        [Test]
        public void WeaponChange_IsNotAlsoRaisedFromPlayerController()
        {
            // Absent control for section 3: proves the assertions above are discriminating between
            // files rather than passing on any file that mentions the enum at all.
            string player = SourceLint.ReadStripped(PlayerPath);

            Assert.IsFalse(player.Contains("SatsInvalidationReason.WeaponChange"),
                "WeaponChange is raised from Equip(). A second site in PlayerController would mean the " +
                "trigger had been wired twice, which is a change in behaviour (two clears for one " +
                "swap) rather than redundancy.");
        }

        [Test]
        public void PlayerFile_DoesNotRaiseAControllerOwnedTrigger()
        {
            string player = SourceLint.ReadStripped(PlayerPath);

            foreach (string member in WiredInsideController)
            {
                Assert.IsFalse(player.Contains("SatsInvalidationReason." + member),
                    "SatsInvalidationReason." + member + " belongs to the mode's tick. Raising it from " +
                    "the player would clear the queue on a frame the mode has not evaluated.");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The enum's members, in order, from already-stripped source.
        ///
        /// <para>Bounded by the declaration and the first closing brace at the enum's own indent rather
        /// than by a fixed line count: the enum carries a doc comment per member and grows, so any
        /// positional read would break the day a member was documented more thoroughly.</para>
        /// </summary>
        private static List<string> ExtractEnumMembers(string strippedController)
        {
            const string header = "enum SatsInvalidationReason";
            int start = strippedController.IndexOf(header, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0,
                "Could not find `enum SatsInvalidationReason` in SatsController.cs. If the enum moved, " +
                "this fixture must be repointed rather than left to fail as a missing-header error.");

            int braceOpen = strippedController.IndexOf('{', start);
            int braceClose = strippedController.IndexOf('}', braceOpen);

            Assert.IsTrue(braceOpen > 0 && braceClose > braceOpen, "Malformed enum declaration.");

            string body = strippedController.Substring(braceOpen + 1, braceClose - braceOpen - 1);

            var members = new List<string>();

            // A member is `Name,` or `Name` on its own line. Values (`Name = 3`) are not used by this
            // enum, and if one appeared the name is still what the ledger keys on.
            foreach (Match match in Regex.Matches(body, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=[^,]*)?,?\s*$",
                         RegexOptions.Multiline))
            {
                string name = match.Groups[1].Value;
                if (name.Length > 0) members.Add(name);
            }

            Assert.IsTrue(members.Count > 0,
                "The enum extractor found no members. An empty extraction would make every per-member " +
                "assertion below vacuous while the fixture still reported green.");

            return members;
        }
    }
}
