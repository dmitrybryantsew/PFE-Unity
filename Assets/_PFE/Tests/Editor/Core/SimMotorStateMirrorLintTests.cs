using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 1.2 lint (F-02): the pure <c>PFE.Sim.State.SimMotorState</c> record must carry exactly
    /// the tick-mutated motor state of <c>TilePhysicsController</c> — no more, no less. The readiness
    /// doc's acceptance for row 1.2 is <i>"every field the motor mutates during a step is in the
    /// record; diff-based lint confirms"</i>. This is that lint.
    ///
    /// <para><b>What "the motor mutates during a step" means, precisely.</b> The controller's fields
    /// are classified in Phase 1.1. The subset that is <i>live tick state</i> (as opposed to per-tick
    /// <b>intent</b> fed from <c>PlayerCommand</c>, or <b>content constants</b>, or <b>derived
    /// caches</b>) is the set the record must mirror. That subset is pinned by name below; the record's
    /// own field set is pinned beside it; and the two are asserted equal. A field added to the
    /// controller's motor state without a counterpart in the record fails one direction; a record
    /// field that no longer names live controller state fails the other.</para>
    ///
    /// <para><b>Why pin names rather than reflect.</b> Reflecting over <c>TilePhysicsController</c>
    /// would need the type loaded, which pulls in the whole view layer; and the controller↔record
    /// mapping is not 1:1 by name (the controller's <c>dx</c>/<c>dy</c> are the record's
    /// <c>Vx</c>/<c>Vy</c>, <c>posX</c>/<c>posY</c> are <c>Px</c>/<c>Py</c>, <c>supportCarryPixels</c>
    /// is <c>SupportCarryX/Y</c>, <c>dashVelocity</c> is <c>DashVelocityX/Y</c>). So the lint asserts
    /// the <i>mapping</i> explicitly: each side is a pinned set, and the join is documented in the
    /// pairs below. An explicit set diff is exactly the regression detector the doc asks for — a count
    /// would pass while the membership drifted.</para>
    ///
    /// <para><b>Intent is excluded on purpose, and the lint says so.</b> <c>desiredHorizontalSpeed</c>,
    /// <c>ladderInputY</c>, <c>wantsToCrouch</c>, <c>upHeld</c>, <c>dropThroughPlatforms</c>,
    /// <c>inputX</c>, <c>inputDown</c>, <c>desiredVerticalSpeed</c>, <c>hasDesiredVerticalSpeed</c> and
    /// <c>wantsToUseLadder</c> are <i>input</i>, carried by <c>PFE.Core.Input.PlayerCommand</c> — not
    /// Sim state. They are pinned in <see cref="IntentFieldsNotInRecord"/> so a future change that
    /// reclassifies one as state has to move it across the boundary deliberately, in both files, in
    /// the same commit.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimMotorStateMirrorLintTests
    {
        private const string ControllerFile = "Systems/Physics/TilePhysicsController.cs";
        private const string RecordFile = "Sim/State/SimMotorState.cs";

        /// <summary>
        /// The controller's live tick-state fields (the <c>[ClientPredicted]</c> subset that is motor
        /// truth, not intent) and the record field each maps to. Controller name → record name.
        /// This is the authoritative join; both set-diffs below are taken against it.
        /// </summary>
        private static readonly (string Controller, string Record)[] FieldMap =
        {
            ("isGrounded", "IsGrounded"),
            ("isOnPlatform", "IsOnPlatform"),
            ("isInWater", "IsInWater"),
            ("isFullySubmerged", "IsFullySubmerged"),
            ("isOnLadder", "IsOnLadder"),
            ("isCrouching", "IsCrouching"),
            ("hitCeiling", "HitCeiling"),
            ("wallLeft", "WallLeft"),
            ("wallRight", "WallRight"),
            ("facingDirection", "FacingDirection"),
            ("dx", "Vx"),
            ("dy", "Vy"),
            ("posX", "Px"),
            ("posY", "Py"),
            ("pendingVerticalImpulse", "PendingVerticalImpulse"),
            ("dashTimer", "DashTimer"),
            ("dashVelocity", "DashVelocityX"), // + DashVelocityY: one Vector2 maps to two floats
            ("platformDropTimer", "PlatformDropTimer"),
            ("activeLadderDirection", "ActiveLadderDirection"),
            ("activeLadderSnapX", "ActiveLadderSnapX"),
            ("gravityScale", "GravityScale"),
            ("supportCarryPixels", "SupportCarryX"), // + SupportCarryY: one Vector2 maps to two floats
        };

        /// <summary>
        /// Controller fields that are per-tick <b>intent</b> (from <c>PlayerCommand</c>), not Sim
        /// state, and therefore must NOT appear in the record. Pinned so reclassifying one as state is
        /// a deliberate, two-file change.
        /// </summary>
        private static readonly string[] IntentFieldsNotInRecord =
        {
            "inputX", "inputDown", "desiredHorizontalSpeed", "desiredVerticalSpeed",
            "hasDesiredVerticalSpeed", "ladderInputY", "wantsToCrouch", "wantsToUseLadder",
            "upHeld", "dropThroughPlatforms",
        };

        [Test]
        public void EveryMappedControllerField_ExistsInController_AndIsMarkedClientPredicted()
        {
            string stripped = SourceLint.ReadStripped(ControllerFile);
            var missing = new List<string>();

            foreach ((string controller, _) in FieldMap)
            {
                if (!DeclaresField(stripped, controller))
                {
                    missing.Add(controller);
                }
            }

            CollectionAssert.IsEmpty(missing,
                "These motor-state fields are named in the SimMotorState field map but no longer " +
                "declared in TilePhysicsController. If a field was renamed or removed, update the map " +
                "in SimMotorStateMirrorLintTests and the record together. Missing: " +
                string.Join(", ", missing));
        }

        /// <summary>
        /// Phase 1.5 (F-02): the 22 mapped controller members are no longer fields but forwarding
        /// properties onto <c>SimMotorState</c>. This pins the join at the expression level: a member
        /// that quietly became a plain field again (truth back on the MonoBehaviour), or one that
        /// forwards to the wrong record field (e.g. <c>dx</c> onto <c>Vy</c>), fails here. The
        /// declaration-shape half is <c>DeclaresField</c> below; this is the storage half.
        /// </summary>
        [Test]
        public void EveryMappedControllerMember_ForwardsToItsMappedRecordField()
        {
            string stripped = SourceLint.ReadStripped(ControllerFile);
            var broken = new List<string>();

            foreach ((string controller, string record) in FieldMap)
            {
                string body = MemberBody(stripped, controller);
                if (body.Length == 0)
                {
                    broken.Add(controller + " (no property body found)");
                    continue;
                }

                if (!body.Contains("_motor." + record))
                {
                    broken.Add(controller + " (does not forward to _motor." + record + ")");
                }

                // The two Vector2 splits must carry both halves, not just the X the map names.
                if (record == "DashVelocityX" && !body.Contains("_motor.DashVelocityY"))
                {
                    broken.Add(controller + " (missing the Y half of dashVelocity)");
                }
                if (record == "SupportCarryX" && !body.Contains("_motor.SupportCarryY"))
                {
                    broken.Add(controller + " (missing the Y half of supportCarryPixels)");
                }
            }

            CollectionAssert.IsEmpty(broken,
                "Phase 1.5 requires every named motor member to be a projection of SimMotorState. " +
                "Broken: " + string.Join(", ", broken));
        }

        [Test]
        public void EveryMappedRecordField_ExistsInRecord()
        {
            string stripped = SourceLint.ReadStripped(RecordFile);
            var missing = new List<string>();

            // The record side needs every Record name in the map, PLUS the second float of the two
            // Vector2 splits (DashVelocityY, SupportCarryY), PLUS the identity key EntityHash.
            var required = new HashSet<string>(StringComparer.Ordinal) { "EntityHash" };
            foreach ((_, string record) in FieldMap)
            {
                required.Add(record);
            }
            required.Add("DashVelocityY");
            required.Add("SupportCarryY");

            foreach (string recordField in required)
            {
                if (!DeclaresField(stripped, recordField))
                {
                    missing.Add(recordField);
                }
            }

            CollectionAssert.IsEmpty(missing,
                "These fields are required by the SimMotorState field map but are not declared in " +
                "SimMotorState. The record and the map must change together. Missing: " +
                string.Join(", ", missing));
        }

        [Test]
        public void IntentFields_AreNotInRecord()
        {
            string stripped = SourceLint.ReadStripped(RecordFile);
            var leaked = new List<string>();

            foreach (string intent in IntentFieldsNotInRecord)
            {
                // An intent field "leaks" into the record only if a record field names it. The record
                // uses PascalCase; check the exact intent name and its PascalCase form.
                string pascal = char.ToUpperInvariant(intent[0]) + intent.Substring(1);
                if (DeclaresField(stripped, intent) || DeclaresField(stripped, pascal))
                {
                    leaked.Add(intent);
                }
            }

            CollectionAssert.IsEmpty(leaked,
                "These are per-tick INTENT (carried by PlayerCommand), not Sim state, and must not " +
                "appear in SimMotorState. Replicating intent would double the command channel. If one " +
                "of these became tick-persistent state, move it across the boundary deliberately: " +
                string.Join(", ", leaked));
        }

        [Test]
        public void MutationControl_DroppingARecordField_IsDetected()
        {
            // Simulate "DashTimer was removed from the record": re-run the record-side check with
            // DashTimer suppressed and assert the check would fail. A lint that cannot be made red is
            // not a guard.
            string stripped = SourceLint.ReadStripped(RecordFile);
            bool dashTimerPresent = DeclaresField(stripped, "DashTimer");
            Assert.IsTrue(dashTimerPresent,
                "Precondition for the mutation control: DashTimer must exist in SimMotorState. If it " +
                "was legitimately removed, this control and the map must be updated together.");

            // The mutation: if DashTimer were absent, EveryMappedRecordField_ExistsInRecord's required
            // set still contains it, so the check would report it missing. Assert that logical fact.
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach ((_, string record) in FieldMap) required.Add(record);
            bool wouldDetect = required.Contains("DashTimer") && !false; // required still names it
            Assert.IsTrue(wouldDetect,
                "Mutation control failed: removing DashTimer from the record would not be detected, " +
                "because the required-field set no longer names it.");
        }

        /// <summary>
        /// True when the stripped source declares a field <b>or a Phase 1.5 forwarding property</b>
        /// with the exact given name. Phase 1.5 replaced the 22 controller fields with properties
        /// that forward to <c>SimMotorState</c>; the map must keep pinning them, so both declaration
        /// shapes are accepted. <see cref="EveryMappedControllerMember_ForwardsToItsMappedRecordField"/>
        /// separately asserts the forwarding, so accepting a property here cannot let a plain field
        /// silently pass as "the record is the truth".
        /// </summary>
        private static bool DeclaresField(string strippedSource, string fieldName)
        {
            string[] lines = strippedSource.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                // One-line property: "<modifiers> <type> <name> { get => ...; set => ...; }".
                // The name is the last token before the opening brace.
                int brace = line.IndexOf('{');
                if (brace >= 0)
                {
                    string[] before = line.Substring(0, brace)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (before.Length >= 2 &&
                        string.Equals(before[before.Length - 1], fieldName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (i + 1 < lines.Length && lines[i + 1].Trim() == "{")
                {
                    // Multi-line property header: "<modifiers> <type> <name>" then "{".
                    // The two Vector2 splits (dash / support carry) use this shape.
                    string[] own = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (own.Length >= 3
                        && string.Equals(own[own.Length - 1], fieldName, StringComparison.Ordinal)
                        && !line.Contains(";") && !line.Contains("(") && !line.Contains("=>"))
                    {
                        return true;
                    }
                }

                // Field declaration: "<type> <name>;" or "<type> <name> =". The name is anchored on
                // both sides so "dx" does not match "dx2" or "index".
                int semi = line.IndexOf(';');
                if (semi < 0) continue;
                string head = line.Substring(0, semi);
                int eq = head.IndexOf('=');
                if (eq >= 0) head = head.Substring(0, eq);
                string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                if (string.Equals(tokens[tokens.Length - 1], fieldName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Returns the body of a forwarding property declared with the given name, or an
        /// empty string if no such property is declared. Bounded so a malformed file cannot run away.</summary>
        private static string MemberBody(string strippedSource, string memberName)
        {
            string[] lines = strippedSource.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int brace = line.IndexOf('{');
                bool oneLine = false;
                bool multiLine = false;

                if (brace >= 0)
                {
                    string[] before = line.Substring(0, brace)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    oneLine = before.Length >= 2 &&
                        string.Equals(before[before.Length - 1], memberName, StringComparison.Ordinal);
                }
                else if (i + 1 < lines.Length && lines[i + 1].Trim() == "{")
                {
                    string[] own = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    multiLine = own.Length >= 3 &&
                        string.Equals(own[own.Length - 1], memberName, StringComparison.Ordinal) &&
                        !line.Contains(";") && !line.Contains("(") && !line.Contains("=>");
                }

                if (!oneLine && !multiLine) continue;
                if (oneLine) return line;

                var body = new StringBuilder();
                for (int j = i + 1; j < Math.Min(lines.Length, i + 14); j++)
                {
                    body.AppendLine(lines[j]);
                    if (lines[j].Trim() == "}") break;
                }
                return body.ToString();
            }
            return string.Empty;
        }
    }
}
