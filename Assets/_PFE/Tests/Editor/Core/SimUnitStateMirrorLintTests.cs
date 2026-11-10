using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 1.5 / F-02, unit slice: pins the <c>UnitController</c> ↔ <c>SimUnitState</c> join.
    ///
    /// <para><b>Why a source lint.</b> The unit slice is the first Phase 1.5 change with <i>no</i>
    /// golden trace covering it — the <c>TileCollision</c> oracle drives <c>TilePhysicsController</c>,
    /// not <c>UnitController</c>. A source lint is the only offline guard for the wiring (§11 item 6:
    /// "a MonoBehaviour defect escapes every offline harness"). It asserts the <i>storage</i>, not
    /// just the names: each of the seven members must be a property whose body reads and writes
    /// <c>_sim</c>, so a future edit that quietly turns one back into a plain field (truth back on the
    /// MonoBehaviour) or forwards it to the wrong record field fails here.</para>
    ///
    /// <para><b>Deliberately partial.</b> Only the motor-less unit's live motion/gait half moved in
    /// this slice. Elite/hero, corpse/dissolve, noise/stealth/suspicion, speech, contact-invulnerability
    /// and placement/content fields remain on the controller marked <c>[Authoritative]</c>; the
    /// readiness doc §8 row 1.5 records why for each group. This lint pins the seven that moved and
    /// does not pretend the rest did.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimUnitStateMirrorLintTests
    {
        private const string ControllerFile = "Entities/Units/UnitController.cs";
        private const string RecordFile = "Sim/State/SimUnitState.cs";

        /// <summary>Controller member → the record field its getter/setter forwards to. The two
        /// Vector2 splits name their X half; the lint checks the Y half explicitly.</summary>
        private static readonly (string Controller, string Record)[] FieldMap =
        {
            ("_velocity", "Vx"),                       // + Vy
            ("_isGrounded", "IsGrounded"),
            ("_dropThroughPlatforms", "DropThroughPlatforms"),
            ("_facingDirection", "FacingDirection"),
            ("_overhangLeft", "OverhangLeft"),
            ("_overhangRight", "OverhangRight"),
            ("_groundPropCarryPixels", "SupportCarryX"), // + SupportCarryY
        };

        [Test]
        public void EveryMappedControllerMember_IsAForwardingPropertyOntoTheRecord()
        {
            string stripped = SourceLint.ReadStripped(ControllerFile);
            var broken = new List<string>();

            foreach ((string controller, string record) in FieldMap)
            {
                string window = FindMemberWindow(stripped, controller);
                if (window.Length == 0)
                {
                    broken.Add(controller + " (declaration not found)");
                    continue;
                }
                if (!LooksLikeProperty(window))
                {
                    broken.Add(controller + " (reverted to a plain field — truth is back on the MonoBehaviour)");
                    continue;
                }
                if (!window.Contains("_sim." + record))
                {
                    broken.Add(controller + " (does not forward to _sim." + record + ")");
                }
                if (record == "Vx" && !window.Contains("_sim.Vy"))
                {
                    broken.Add(controller + " (missing the Y half of _velocity)");
                }
                if (record == "SupportCarryX" && !window.Contains("_sim.SupportCarryY"))
                {
                    broken.Add(controller + " (missing the Y half of _groundPropCarryPixels)");
                }
            }

            CollectionAssert.IsEmpty(broken,
                "Phase 1.5 requires every mapped UnitController member to project SimUnitState. Broken: " +
                string.Join(", ", broken));
        }

        [Test]
        public void EveryMappedRecordField_ExistsInRecord()
        {
            string stripped = SourceLint.ReadStripped(RecordFile);
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach ((_, string record) in FieldMap) required.Add(record);
            required.Add("Vy");
            required.Add("SupportCarryY");

            var missing = new List<string>();
            foreach (string field in required)
            {
                if (!DeclaresField(stripped, field)) missing.Add(field);
            }

            CollectionAssert.IsEmpty(missing,
                "These fields are required by the UnitController <-> SimUnitState map but are not " +
                "declared in SimUnitState. Missing: " + string.Join(", ", missing));
        }

        [Test]
        public void LiveRecord_AndProjectionSurface_Exist()
        {
            string stripped = SourceLint.ReadStripped(ControllerFile);

            Assert.That(stripped, Does.Contain("private SimUnitState _sim"),
                "UnitController no longer holds a live SimUnitState record. The seven forwarding " +
                "properties need a single owner; without it the record is only a computed view.");
            Assert.That(stripped, Does.Contain("public SimUnitState UnitState"),
                "The read-only projection (snapshot capture / host comparison) disappeared.");
            Assert.That(stripped, Does.Contain("ApplyUnitState"),
                "A host correction / snapshot restore has no way back into the live record.");
        }

        [Test]
        public void MutationControl_APlainField_IsDetected()
        {
            // A green lint that cannot go red is not a guard. The exact regression this fixture exists
            // to catch — someone restoring `_velocity` as a plain field — must fail LooksLikeProperty.
            Assert.IsFalse(LooksLikeProperty("        protected Vector2 _velocity;"),
                "Mutation control failed: a plain field declaration was accepted as a forwarding " +
                "property. Every green assertion above would then be vacuous.");
            Assert.IsTrue(LooksLikeProperty(
                    "        protected Vector2 _velocity\n        {\n            get => new Vector2(_sim.Vx, _sim.Vy);\n        }"),
                "Positive control failed: a real forwarding property was not recognised.");
        }

        [Test]
        public void MutationControl_AnAliasProperty_IsNotMistakenForTheDeclaration()
        {
            // Regression control for a real false red: UnitController:3705 exposes
            // `public Vector2 Velocity => _velocity;`. The first grammar matched that line (access
            // modifier + the name + a `;`), treated it as a plain field, and reported "_velocity
            // reverted to a plain field" against a perfectly good forwarding property. A lint that
            // points at the wrong line is worse than no lint.
            const string synthetic =
                "public Vector2 Velocity => _velocity;\n" +
                "protected Vector2 _velocity\n" +
                "{\n" +
                "    get => new Vector2(_sim.Vx, _sim.Vy);\n" +
                "    set { _sim.Vx = value.x; _sim.Vy = value.y; }\n" +
                "}\n";

            string window = FindMemberWindow(synthetic, "_velocity");

            Assert.IsNotEmpty(window, "The real declaration was not found at all.");
            string[] lines = window.Split('\n');
            Assert.IsTrue(lines[0].Trim().StartsWith("protected Vector2 _velocity", StringComparison.Ordinal),
                "The decoy alias was preferred over the declaration; window begins: " + lines[0]);
            Assert.That(window, Does.Contain("_sim.Vx"), "The X half of the join was lost.");
            Assert.That(window, Does.Contain("_sim.Vy"), "The Y half of the join was lost.");
        }

        // ── Declaration grammar (kept local so the lint reads the shipped source, not a copy) ────

        /// <summary>Returns the declaration line plus its bounded body for a named member, or an empty
        /// string when no declaration exists. The name is anchored as the <b>last token of the
        /// declaration head</b>, not merely "mentioned on the line": UnitController also exposes
        /// <c>public Vector2 Velocity =&gt; _velocity;</c>, and a contains-based grammar mistakes that
        /// alias for the declaration. Same token grammar as SimMotorStateMirrorLintTests.MemberBody.</summary>
        private static string FindMemberWindow(string strippedSource, string memberName)
        {
            string[] lines = strippedSource.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!(line.StartsWith("protected ", StringComparison.Ordinal)
                      || line.StartsWith("private ", StringComparison.Ordinal)
                      || line.StartsWith("public ", StringComparison.Ordinal)))
                {
                    continue;
                }

                // One-line property: "<modifiers> <type> <name> { get => ...; set => ...; }".
                int brace = line.IndexOf('{');
                if (brace >= 0)
                {
                    string[] before = line.Substring(0, brace)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (before.Length >= 2 &&
                        string.Equals(before[before.Length - 1], memberName, StringComparison.Ordinal))
                    {
                        return line;
                    }
                }
                else if (i + 1 < lines.Length && lines[i + 1].Trim() == "{")
                {
                    // Multi-line property header: "<modifiers> <type> <name>" then "{". The two
                    // Vector2 splits (_velocity / _groundPropCarryPixels) use this shape.
                    string[] own = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (own.Length >= 3
                        && string.Equals(own[own.Length - 1], memberName, StringComparison.Ordinal)
                        && !line.Contains(";") && !line.Contains("(") && !line.Contains("=>"))
                    {
                        var window = new StringBuilder();
                        window.AppendLine(line);
                        for (int j = i + 1; j < Math.Min(lines.Length, i + 14); j++)
                        {
                            window.AppendLine(lines[j]);
                            if (lines[j].Trim() == "}") break;
                        }
                        return window.ToString();
                    }
                }

                // Plain field: "<modifiers> <type> <name>;" — accepted so the lint can *report* a
                // reverted field rather than claiming the member vanished. An expression-bodied
                // alias (">= name;") is explicitly excluded, or it would shadow the real declaration.
                int semi = line.IndexOf(';');
                if (semi < 0 || line.Contains("=>") || line.Contains("{")) continue;
                string head = line.Substring(0, semi);
                int eq = head.IndexOf('=');
                if (eq >= 0) head = head.Substring(0, eq);
                string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 &&
                    string.Equals(tokens[tokens.Length - 1], memberName, StringComparison.Ordinal))
                {
                    return line;
                }
            }
            return string.Empty;
        }

        /// <summary>True when the first non-blank line of a member window opens a property body —
        /// either on the same line (<c>... { get => ...; }</c>) or on the next line, with a getter
        /// visible in the bounded body that follows.</summary>
        private static bool LooksLikeProperty(string window)
        {
            string[] lines = window.Split('\n');
            int first = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length > 0) { first = i; break; }
            }
            if (first < 0) return false;

            string line = lines[first].Trim();
            if (line.Contains("{") && line.Contains("get")) return true;

            if (first + 1 < lines.Length && lines[first + 1].Trim() == "{")
            {
                for (int j = first + 2; j < lines.Length; j++)
                {
                    if (lines[j].Contains("get")) return true;
                }
            }
            return false;
        }

        /// <summary>True when the stripped record source declares a public field with this exact name.</summary>
        private static bool DeclaresField(string strippedSource, string fieldName)
        {
            foreach (string raw in strippedSource.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("public ", StringComparison.Ordinal)) continue;
                int semi = line.IndexOf(';');
                if (semi < 0) continue;
                string head = line.Substring(0, semi);
                int eq = head.IndexOf('=');
                if (eq >= 0) head = head.Substring(0, eq);
                string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 &&
                    string.Equals(tokens[tokens.Length - 1], fieldName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
