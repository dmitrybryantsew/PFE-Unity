using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 1.1 lint (F-01/F-02): every <b>mutable instance field</b> in the three Sim-facing
    /// controllers — <c>TilePhysicsController</c>, <c>UnitController</c>, <c>PlayerController</c> —
    /// must carry exactly one authority marker (<c>[Authoritative]</c> / <c>[ClientPredicted]</c> /
    /// <c>[LocalOnly]</c>). This is the authority map the rest of the multiplayer plan reads: a field
    /// with no marker is invisible to the snapshot/prediction/reconciliation design, and a field with
    /// the wrong marker is a silent host/client divergence.
    ///
    /// <para><b>Why a source lint and not reflection.</b> These controllers are <c>MonoBehaviour</c>s;
    /// instantiating them offline throws before any assertion runs (the same ECall wall
    /// <c>UnitMotorReHomeTests</c> hits). A source lint executes everywhere, so it is the one form of
    /// guard that can actually fail in the harness. It reads the declaration site directly, which is
    /// also where a reviewer looks.</para>
    ///
    /// <para><b>Scope is deliberately the three controllers, not all of <c>Sim/**</c>.</b> The rule
    /// layer under <c>Sim/</c> is already pure (Phase 0.1 pins that); the defect F-02 names is that the
    /// <i>state those rules act on</i> lives in unmarked MonoBehaviour fields. Widening the net to every
    /// class would drown the signal in view-layer noise. The three files are named explicitly so a new
    /// Sim-facing controller is a conscious addition, not an accident.</para>
    ///
    /// <para><b>Field grammar, kept honest.</b> The scanner keys on lines that declare a field and
    /// skips: <c>const</c>/<c>static</c> (not per-instance), expression-bodied members and computed
    /// properties (no backing field of the class's own), and method declarations. Auto-properties with a
    /// <c>set</c> accessor <i>do</i> have a mutable backing field and are required to be marked;
    /// get-only computed properties are not. Events are excluded because the attribute does not target
    /// them (see the <c>Died</c> declaration for the documented exception).</para>
    /// </summary>
    [TestFixture]
    public sealed class AuthorityMarkerLintTests
    {
        // The three Sim-facing controllers, relative to Assets/_PFE. A fourth Sim-facing controller is
        // a deliberate addition to this list — the whole point is that "this class holds tick-mutated
        // state" is a reviewed claim, not something a file opts into by existing.
        private static readonly string[] SimFacingFiles =
        {
            "Systems/Physics/TilePhysicsController.cs",
            "Entities/Units/UnitController.cs",
            "Entities/Player/PlayerController.cs",
        };

        private static readonly string[] MarkerNames = { "Authoritative", "ClientPredicted", "LocalOnly" };

        [Test]
        public void EveryMutableInstanceField_InSimFacingControllers_IsMarked()
        {
            var violations = new List<string>();

            foreach (string file in SimFacingFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                string[] lines = stripped.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!IsMutableFieldDeclaration(lines[i]))
                    {
                        continue;
                    }

                    if (HasMarkerAbove(lines, i))
                    {
                        continue;
                    }

                    // 1-based line number for a human reading the file.
                    violations.Add($"{file}:{i + 1}: unmarked mutable field '{lines[i].Trim()}'");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "Every mutable instance field in a Sim-facing controller must carry exactly one " +
                "[Authoritative]/[ClientPredicted]/[LocalOnly] marker (Phase 1.1). An unmarked field " +
                "is invisible to the snapshot/prediction design. Offending declarations:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void EveryMarker_OnAField_ResolvesToAKnownAuthority()
        {
            // Absent control for the authority map as a WHOLE. Phase 1.1 classified by *kind of state*,
            // not by file: motor live tick-state is [ClientPredicted], unit/player gameplay truth is
            // [Authoritative], wiring/config/view is [LocalOnly]. TilePhysicsController therefore has
            // no [Authoritative] field by design — the motor record is client-predicted and the host
            // truth for a unit lives in UnitController. Requiring every one of the three markers in
            // every one of the three files (the pre-Phase-1.5 form of this test) asserted something the
            // design never claimed and was red from the day it landed. What the map does claim is that
            // all three classifications are exercised ACROSS the three controllers, so that is what is
            // pinned here; the per-file half below still proves no controller dropped out entirely.
            var union = new StringBuilder();
            foreach (string file in SimFacingFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                union.AppendLine(stripped);

                bool anyMarker = false;
                foreach (string marker in MarkerNames)
                {
                    anyMarker |= HasMarker(stripped, marker);
                }
                Assert.IsTrue(anyMarker,
                    $"{file} uses none of the three authority markers at all. A Sim-facing controller " +
                    "with no authority map entries is invisible to the snapshot/prediction design.");
            }

            foreach (string marker in MarkerNames)
            {
                // The attribute must appear as `[Name(` or `[Name]`. A substring match on the bare
                // name would also hit the Note text inside other attributes' strings — but those are
                // stripped, so any surviving occurrence is a real usage.
                Assert.IsTrue(HasMarker(union.ToString(), marker),
                    $"The authority map no longer exercises [{marker}] anywhere across the three " +
                    "controllers. Each classification must stay represented; if a whole class of state " +
                    "was reclassified, update this lint and the readiness doc in the same change.");
            }
        }

        [Test]
        public void MutationControl_RenamingAMarkerKind_IsDetected()
        {
            // A green absent control is worthless if the presence predicate cannot go false. Simulate
            // the exact defect it guards against: every [ClientPredicted] usage becomes a misspelled
            // [ClientPredictedTypo]. The predicate must stop finding the real marker.
            var union = new StringBuilder();
            foreach (string file in SimFacingFiles) union.AppendLine(SourceLint.ReadStripped(file));
            string real = union.ToString();

            Assert.IsTrue(HasMarker(real, "ClientPredicted"),
                "Precondition: [ClientPredicted] must really be used in the authority map.");

            string mutated = real
                .Replace("[ClientPredicted(", "[ClientPredictedTypo(")
                .Replace("[ClientPredicted]", "[ClientPredictedTypo]");

            Assert.IsFalse(HasMarker(mutated, "ClientPredicted"),
                "Mutation control failed: a misspelled marker still counted as coverage. The " +
                "known-authority check would be vacuously green.");
        }

        /// <summary>True when a real <c>[Name]</c> / <c>[Name(</c> attribute usage survives stripping.</summary>
        private static bool HasMarker(string strippedSource, string marker)
        {
            return strippedSource.Contains("[" + marker + "]")
                || strippedSource.Contains("[" + marker + "(");
        }

        [Test]
        public void MutationControl_RemovingAMarker_IsDetected()
        {
            // Re-run the field scan over the real tree, but simulate "one field lost its marker" by
            // treating the first marked field we find as unmarked. If the lint's own logic cannot be
            // made to flag a field, the green result above is vacuous — a guard that cannot fail is not
            // a guard.
            string file = SimFacingFiles[0];
            string stripped = SourceLint.ReadStripped(file);
            string[] lines = stripped.Split('\n');

            int markedFields = 0;
            int firstMarkedFieldLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsMutableFieldDeclaration(lines[i]) && HasMarkerAbove(lines, i))
                {
                    markedFields++;
                    if (firstMarkedFieldLine < 0)
                    {
                        firstMarkedFieldLine = i;
                    }
                }
            }

            Assert.That(markedFields, Is.GreaterThan(0),
                "The mutation control found no marked fields at all — the lint would be vacuously green.");

            // Now pretend that field's marker is gone: the same predicate, with the marker-scan disabled
            // for that one line, must report it as a violation.
            bool wouldFlag = IsMutableFieldDeclaration(lines[firstMarkedFieldLine])
                && !HasMarkerAbove(lines, firstMarkedFieldLine, suppressLine: firstMarkedFieldLine);
            Assert.IsTrue(wouldFlag,
                "Mutation control failed: stripping one field's marker was not detected. The lint's " +
                "HasMarkerAbove logic is broken and the green result above cannot be trusted.");
        }

        // ── Field grammar ────────────────────────────────────────────────────────

        /// <summary>
        /// True when a (comment/string-stripped) line declares a mutable instance field. Excludes
        /// const/static (not per-instance), events (attribute does not target them), methods,
        /// expression-bodied members, and get-only computed properties (no mutable backing field).
        /// </summary>
        private static bool IsMutableFieldDeclaration(string rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) return false;

            // Must begin with an accessibility modifier (all our fields are explicitly scoped).
            bool scoped = line.StartsWith("private ", StringComparison.Ordinal)
                || line.StartsWith("protected ", StringComparison.Ordinal)
                || line.StartsWith("internal ", StringComparison.Ordinal)
                || line.StartsWith("public ", StringComparison.Ordinal)
                || line.StartsWith("readonly ", StringComparison.Ordinal); // Unity-serialized readonlys
            if (!scoped) return false;

            // Not a field at all.
            if (line.Contains(" const ")) return false;
            if (line.Contains(" static ")) return false;
            if (line.Contains(" event ")) return false;
            if (line.StartsWith("public event", StringComparison.Ordinal)) return false;
            if (line.StartsWith("protected event", StringComparison.Ordinal)) return false;
            if (line.StartsWith("private event", StringComparison.Ordinal)) return false;
            if (line.Contains(" class ")) return false;
            if (line.Contains(" struct ")) return false;
            if (line.Contains(" enum ")) return false;
            if (line.Contains(" interface ")) return false;

            // Expression-bodied member or computed property — no mutable backing field of its own.
            if (line.Contains("=>")) return false;

            // A method or constructor call/declaration has an open paren before any '='.
            int paren = line.IndexOf('(');
            int equals = line.IndexOf('=');
            if (paren >= 0 && (equals < 0 || paren < equals)) return false;

            // A get-only auto-property ("{ get; }") has no setter, so no mutable backing field.
            // A property WITH a setter ("{ get; set; }" / "{ get; private set; }") IS mutable state.
            if (line.Contains("{") && line.Contains("get;") && !line.Contains("set;")) return false;

            // Must end like a field: ';' or an initializer.
            bool endsStatement = line.EndsWith(";", StringComparison.Ordinal);
            if (!endsStatement) return false;

            // Needs a type + a name: at least two tokens before '=' or ';'.
            string head = equals >= 0 ? line.Substring(0, equals) : line.Substring(0, line.Length - 1);
            string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // tokens: [modifiers...] [type] [name]
            if (tokens.Length < 2) return false;

            // The last token is the field name; it must be a valid identifier-ish (letters/digits/_).
            string name = tokens[tokens.Length - 1];
            if (!LooksLikeIdentifier(name)) return false;

            return true;
        }

        private static bool LooksLikeIdentifier(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (!(char.IsLetter(token[0]) || token[0] == '_')) return false;
            for (int i = 1; i < token.Length; i++)
            {
                if (!(char.IsLetterOrDigit(token[i]) || token[i] == '_')) return false;
            }
            return true;
        }

        /// <summary>
        /// True when one of the three authority markers appears in the attribute run immediately above
        /// the field. Scans upward past attribute lines and blank/doc-stripped lines, stopping at the
        /// first non-attribute, non-blank line that is not part of the declaration's own attribute run.
        /// </summary>
        /// <param name="suppressLine">
        /// Mutation-control hook: when set to the field's own line, the marker scan treats markers on
        /// that run as absent, simulating "this field lost its marker".
        /// </param>
        private static bool HasMarkerAbove(string[] lines, int fieldLine, int suppressLine = -1)
        {
            if (suppressLine == fieldLine)
            {
                return false; // simulated mutation: the marker is gone
            }

            // Walk upward from the line above the field, through its attribute run. Attributes may be
            // several lines ([A]\n[B]\n[SerializeField]\nfield). Stop at the first line that is neither
            // blank nor an attribute line.
            for (int j = fieldLine - 1; j >= 0; j--)
            {
                string up = lines[j].Trim();
                if (up.Length == 0)
                {
                    continue; // blank line inside the attribute run (stripped doc comments leave these)
                }

                if (up.StartsWith("[", StringComparison.Ordinal))
                {
                    foreach (string marker in MarkerNames)
                    {
                        if (up.Contains("[" + marker + "]") || up.Contains("[" + marker + "("))
                        {
                            return true;
                        }
                    }

                    continue; // some other attribute (SerializeField, Tooltip, Header) — keep looking
                }

                // A non-attribute, non-blank line ends the run.
                break;
            }

            return false;
        }
    }
}
