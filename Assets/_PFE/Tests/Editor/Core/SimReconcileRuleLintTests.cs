using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Sim.State;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.5 lint: <b>no replicated field without a reconciliation rule.</b> This is the acceptance
    /// criterion of §8 row 3.5, and it is the enforcement half of §10 — §10 writes the table as prose,
    /// this fixture makes a field that has no entry in it fail the build.
    ///
    /// <para><b>The defect being guarded, stated precisely.</b> A field added to <c>SimUnitState</c>
    /// crosses the wire the moment it is declared, because <c>WriteTo</c> is written by hand next to it.
    /// Nothing then decides what a client does when that field disagrees with the host, so the field
    /// inherits whatever the snapshot applier does globally — which is correct for a flag and wrong for
    /// predicted motion, a pose, or a health value with a death latch. The compiler cannot see the
    /// omission, no behavioural fixture can either (nothing applies a snapshot yet), so a source lint is
    /// the only guard that can fail in this harness. That is the same reasoning
    /// <c>AuthorityMarkerLintTests</c> records for the controllers, applied to the records.</para>
    ///
    /// <para><b>Why a source lint rather than reflection over the attributes.</b> Reflection would be
    /// shorter, but it would also be <i>green for a field it cannot see</i>: reflection over
    /// <c>GetFields</c> finds every field, so it proves the attribute exists — it does not prove the
    /// attribute is where a reviewer will read it, and it cannot check the wire. The text form lets this
    /// fixture do the one check reflection cannot: cross-read <c>WriteTo</c> and prove the field list it
    /// scans is exactly the field list that is serialized, so a scanner blind spot cannot make the whole
    /// lint vacuous.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimReconcileRuleLintTests
    {
        // The replicated records, relative to Assets/_PFE. A new record is a deliberate addition —
        // EveryFileInTheStateFolder_IsClassified fails if one appears without being registered here.
        private static readonly string[] ReplicatedRecordFiles =
        {
            "Sim/State/SimSnapshot.cs",
            "Sim/State/SimUnitState.cs",
            "Sim/State/SimMotorState.cs",
            "Sim/State/SimProjectileState.cs",
            "Sim/State/SimRoomDelta.cs",
            "Sim/State/SimTileMutation.cs",
        };

        // Everything else in Sim/State, with the reason it carries no per-field rules. Listed rather
        // than ignored so that "this file is not a replicated record" is a reviewed claim.
        private static readonly Dictionary<string, string> NotAReplicatedRecord = new Dictionary<string, string>
        {
            ["SimSnapshotProjectionRules.cs"] = "rule helpers — no fields, no wire",
            ["SimSnapshotSerializer.cs"] = "the codec itself — reads and writes the records, is not one",
            ["SimStalenessRules.cs"] = "staleness predicates — no fields, no wire",
            ["SimTransientKind.cs"] = "a byte discriminator vocabulary — consts only, carried inside SimProjectileState",
            ["SimReconcileRule.cs"] = "the vocabulary this fixture lints against",
        };

        // The records that serialize themselves. SimSnapshot is deliberately absent: its bytes are
        // written by SimSnapshotSerializer, and that pairing is pinned by
        // SimSnapshotRoundTripTests instead (a round trip fails if a header field is dropped).
        private static readonly string[] SelfSerializingRecordFiles =
        {
            "Sim/State/SimUnitState.cs",
            "Sim/State/SimMotorState.cs",
            "Sim/State/SimProjectileState.cs",
            "Sim/State/SimRoomDelta.cs",
            "Sim/State/SimTileMutation.cs",
        };

        [Test]
        public void EveryReplicatedField_CarriesAReconcileRule()
        {
            var violations = new List<string>();

            foreach (string file in ReplicatedRecordFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                foreach (UnruledField unruled in ScanForUnruledFields(stripped))
                {
                    violations.Add($"{file}:{unruled.Line}: '{unruled.FieldName}' has no [SimReconcile] " +
                        "rule — a field on the wire with no rule inherits whatever the applier does");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "Every field of a replicated record must carry exactly one " +
                "[SimReconcile(SimReconcileRule.X)] attribute (§8 row 3.5, §10). An unrouted field is a " +
                "silent host/client divergence: it is correct to overwrite a flag and wrong to " +
                "overwrite predicted motion, a pose phase, or a health value with a death latch. " +
                "Offending declarations:\n" + string.Join("\n", violations));
        }

        [Test]
        public void EveryRuleInTheVocabulary_IsAttachedToAField()
        {
            // The inverse of the rule above, and the reason the vocabulary cannot rot. A rule value no
            // field uses is dead vocabulary that reads like a legal option — and worse, it is the shape
            // a half-finished refactor leaves behind. The other direction (a field with no rule) is the
            // test above; together they make the enum and the records agree.
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in ReplicatedRecordFiles)
            {
                foreach (Match m in Regex.Matches(SourceLint.ReadStripped(file),
                             @"\[SimReconcile\(\s*SimReconcileRule\.([A-Za-z_][A-Za-z0-9_]*)\s*\)\]"))
                {
                    used.Add(m.Groups[1].Value);
                }
            }

            var unused = new List<string>();
            foreach (string name in Enum.GetNames(typeof(SimReconcileRule)))
            {
                if (!used.Contains(name)) unused.Add(name);
            }

            CollectionAssert.IsEmpty(unused,
                "These SimReconcileRule values are attached to no field of any replicated record: " +
                string.Join(", ", unused) + ". Either a field lost its rule, or the value is dead " +
                "vocabulary. Deleting it is usually right — an option nothing can take reads like a " +
                "choice a reviewer is allowed to make.");
        }

        [Test]
        public void TheVocabulary_HasNoOptOutValue()
        {
            // A `None`/`Unknown` member would make EveryReplicatedField_CarriesAReconcileRule
            // satisfiable by writing it — the lint would stay green while the field still has no
            // decision. The whole point of the attribute is that there is no "undecided" answer.
            string[] optOuts = { "None", "Unknown", "Unspecified", "Ignore", "Unreconciled", "Default" };
            foreach (string name in Enum.GetNames(typeof(SimReconcileRule)))
            {
                CollectionAssert.DoesNotContain(optOuts, name,
                    "SimReconcileRule." + name + " is an opt-out value. A field must name a real " +
                    "reconciliation decision; an escape hatch turns the row-3.5 lint into a formality.");
            }
        }

        [Test]
        public void TheScannedFieldList_IsExactlyTheSerializedFieldList()
        {
            // The one hole a source lint can have: the scanner misses a field, so the attribute on it is
            // never required. Closing it needs a second, independent statement of the field list, and
            // WriteTo already is one — it is hand-written and it is what actually puts the field on the
            // wire. Requiring the two sets to be equal means a blind spot in one is a failure in the
            // other. Bidirectional on purpose: a written field the scanner cannot see is a hole, and a
            // scanned field that is never written is a field the round-trip hash does not cover.
            var violations = new List<string>();

            foreach (string file in SelfSerializingRecordFiles)
            {
                string stripped = SourceLint.ReadStripped(file);

                var scanned = new HashSet<string>(StringComparer.Ordinal);
                foreach (FieldDeclaration f in ScanFields(stripped)) scanned.Add(f.FieldName);

                var written = new HashSet<string>(StringComparer.Ordinal);
                foreach (string body in ExtractWriteToBodies(stripped))
                {
                    foreach (Match m in Regex.Matches(body, @"writer\.Write\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)"))
                    {
                        written.Add(m.Groups[1].Value);
                    }
                    foreach (Match m in Regex.Matches(body, @"WriteArray\(\s*writer\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)"))
                    {
                        written.Add(m.Groups[1].Value);
                    }
                }

                if (written.Count == 0)
                {
                    violations.Add($"{file}: no writer.Write(...) call found — the WriteTo body could not " +
                        "be read, so the parity check below proves nothing");
                    continue;
                }

                foreach (string name in scanned)
                {
                    if (!written.Contains(name))
                    {
                        violations.Add($"{file}: '{name}' is a scanned field but never written — the " +
                            "round-trip hash does not cover it");
                    }
                }
                foreach (string name in written)
                {
                    if (!scanned.Contains(name))
                    {
                        violations.Add($"{file}: '{name}' is written to the wire but was not scanned as a " +
                            "field — the rule lint cannot see it, so it cannot require a rule");
                    }
                }
            }

            CollectionAssert.IsEmpty(violations,
                "The field list this lint scans must equal the field list WriteTo serializes. A " +
                "difference means one of the two is wrong, and either way the row-3.5 acceptance is " +
                "vacuous for the missing field:\n" + string.Join("\n", violations));
        }

        [Test]
        public void EveryFileInTheStateFolder_IsClassified()
        {
            // A new record added to the folder but not to ReplicatedRecordFiles would be silently
            // exempt from every assertion above — the defect mode this project keeps hitting is a guard
            // whose scope list quietly stops covering the code. Listing "not a record" explicitly makes
            // the omission a failure rather than a shrug.
            string folder = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Sim", "State");
            Assert.IsTrue(Directory.Exists(folder), "Sim/State folder missing: " + folder);

            var registered = new HashSet<string>(StringComparer.Ordinal);
            // Names, not paths: ReplicatedRecordFiles holds Assets/_PFE-relative paths for
            // SourceLint, while the folder scan yields bare file names. Comparing the two forms
            // directly makes every record read as unclassified — which is how this guard first
            // failed, and the loud failure is the point.
            foreach (string file in ReplicatedRecordFiles) registered.Add(Path.GetFileName(file));
            foreach (string key in NotAReplicatedRecord.Keys) registered.Add(Path.GetFileName(key));

            var unclassified = new List<string>();
            foreach (string path in Directory.GetFiles(folder, "*.cs"))
            {
                string name = Path.GetFileName(path);
                if (!registered.Contains(name)) unclassified.Add(name);
            }

            CollectionAssert.IsEmpty(unclassified,
                "These files are in Sim/State but classified as neither a replicated record nor " +
                "explicitly not one: " + string.Join(", ", unclassified) + ". If one is a replicated " +
                "record, add it to ReplicatedRecordFiles and give its fields rules; if it is not, add it " +
                "to NotAReplicatedRecord with the reason.");
        }

        [Test]
        public void MutationControl_RemovingOneAttribute_IsDetected()
        {
            // A guard that cannot fail is not a guard. Take the real source, delete exactly one
            // [SimReconcile(...)] line, and require the scanner to report exactly that field. This also
            // proves the upward attribute scan stops at the field declaration above it: if it walked
            // past a blank line into the previous field's attribute run, removing one attribute would
            // change nothing and the control would stay green.
            string file = "Sim/State/SimUnitState.cs";
            string stripped = SourceLint.ReadStripped(file);

            // Compare the *delta*, not the absolute state. An earlier version asserted the unmutated
            // file was clean as a precondition, which made this control fail with "my premise broke"
            // whenever a real field genuinely lost its rule — noise on top of the real failure, and a
            // message pointing at the control instead of the defect. A delta is correct whatever the
            // file's state, so the control only ever speaks about the mutation it performed.
            List<string> before = UnruledNames(stripped);

            Match attribute = Regex.Match(stripped, @"^[ \t]*\[SimReconcile\([^\]]*\)\][ \t]*\r?$",
                RegexOptions.Multiline);
            Assert.IsTrue(attribute.Success, "Precondition: no [SimReconcile] attribute found to remove.");

            // The field the removed attribute belonged to: the first declaration after it.
            int tailStart = attribute.Index + attribute.Length;
            string tail = stripped.Substring(tailStart);
            Match field = Regex.Match(tail, @"^[ \t]*(?:public|internal)[^\r\n]*;[ \t]*\r?$",
                RegexOptions.Multiline);
            Assert.IsTrue(field.Success, "Precondition: no field declaration follows the attribute.");

            string mutated = stripped.Remove(attribute.Index, attribute.Length);
            List<string> after = UnruledNames(mutated);

            var newlyUnruled = new List<string>(after);
            foreach (string name in before) newlyUnruled.Remove(name);

            CollectionAssert.AreEquivalent(new[] { LastIdentifierBeforeSemicolon(field.Value) }, newlyUnruled,
                "Removing one attribute must leave exactly the field it belonged to without a rule. " +
                "Anything else means either the scan is blind to that field or it is leaking a " +
                "neighbour's attribute onto it.");
        }

        [Test]
        public void MutationControl_ANewUnruledField_IsDetected()
        {
            // The other half: not "a rule went missing" but "a field arrived without one", which is the
            // real change that will happen next. Insert a declaration into real source and require it to
            // be flagged — with the rule on the field above it untouched, so this also re-proves the
            // scan is per-declaration and not per-file.
            string stripped = SourceLint.ReadStripped("Sim/State/SimMotorState.cs");
            List<string> before = UnruledNames(stripped);

            // Insert *after* an existing declaration, not before one: inserting before would land the
            // probe inside the next field's attribute run and it would inherit that rule, which is the
            // opposite of what this control is meant to simulate.
            const string anchorText = "public float Px;";
            int anchor = stripped.IndexOf(anchorText, StringComparison.Ordinal);
            Assert.That(anchor, Is.GreaterThan(0), "Precondition: could not find a declaration to follow.");

            int insertAt = anchor + anchorText.Length;
            string mutated = stripped.Insert(insertAt, "\r\n        public float UnruledProbe;");

            // Delta again, for the same reason as the control above.
            var newlyUnruled = new List<string>(UnruledNames(mutated));
            foreach (string name in before) newlyUnruled.Remove(name);

            CollectionAssert.AreEquivalent(new[] { "UnruledProbe" }, newlyUnruled,
                "An inserted field with no rule must be flagged, and only it — if the scan reported " +
                "something else, it is not reading declarations independently.");
        }

        // ── Source scanning ──────────────────────────────────────────────────────

        /// <summary>One public field declaration found in a record file.</summary>
        private readonly struct FieldDeclaration
        {
            public FieldDeclaration(int line, string fieldName)
            {
                Line = line;
                FieldName = fieldName;
            }

            public int Line { get; }
            public string FieldName { get; }
        }

        /// <summary>A field declaration with no <c>[SimReconcile]</c> attribute above it.</summary>
        private readonly struct UnruledField
        {
            public UnruledField(int line, string fieldName)
            {
                Line = line;
                FieldName = fieldName;
            }

            public int Line { get; }
            public string FieldName { get; }
        }

        private static List<FieldDeclaration> ScanFields(string stripped)
        {
            var fields = new List<FieldDeclaration>();
            string[] lines = stripped.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryGetFieldName(lines[i], out string name)) continue;
                fields.Add(new FieldDeclaration(i + 1, name));
            }
            return fields;
        }

        private static List<UnruledField> ScanForUnruledFields(string stripped)
        {
            var unruled = new List<UnruledField>();
            string[] lines = stripped.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryGetFieldName(lines[i], out string name)) continue;
                if (HasReconcileAttributeAbove(lines, i)) continue;
                unruled.Add(new UnruledField(i + 1, name));
            }
            return unruled;
        }

        /// <summary>The names of the unruled fields, in declaration order — what the mutation controls diff.</summary>
        private static List<string> UnruledNames(string stripped)
        {
            var names = new List<string>();
            foreach (UnruledField f in ScanForUnruledFields(stripped)) names.Add(f.FieldName);
            return names;
        }

        /// <summary>
        /// True when a <c>[SimReconcile(...)]</c> appears in the attribute run immediately above the
        /// field. The run is walked upward through attribute lines and the blank lines left by stripped
        /// documentation comments, and stops at the first line that is neither — which, in these files,
        /// is always the previous field's declaration. That stop is what keeps the scan per-declaration;
        /// <c>MutationControl_RemovingOneAttribute_IsDetected</c> is the proof that it holds.
        /// </summary>
        private static bool HasReconcileAttributeAbove(string[] lines, int fieldLine)
        {
            for (int j = fieldLine - 1; j >= 0; j--)
            {
                string up = lines[j].Trim();
                if (up.Length == 0) continue; // a stripped doc-comment line
                if (!up.StartsWith("[", StringComparison.Ordinal)) break;
                if (up.StartsWith("[SimReconcile(", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True for a line that declares an instance field of the record, with the field name extracted.
        /// Mirrors <c>SimStateRecordPurityLintTests.TryGetFieldType</c>: an accessibility modifier, no
        /// <c>const</c>/<c>static</c> (not per-instance), no method (no <c>(</c> before any <c>=</c>), no
        /// property or type declaration (no <c>{</c>), no expression body, and it ends a statement.
        /// </summary>
        private static bool TryGetFieldName(string rawLine, out string fieldName)
        {
            fieldName = null;
            string line = rawLine.Trim();
            if (line.Length == 0) return false;

            bool scoped = line.StartsWith("public ", StringComparison.Ordinal)
                || line.StartsWith("internal ", StringComparison.Ordinal);
            if (!scoped) return false;

            if (line.Contains(" class ") || line.Contains(" struct ") || line.Contains(" enum ")) return false;
            if (line.Contains(" const ") || line.Contains(" static ")) return false;
            if (line.Contains("=>")) return false;
            if (line.Contains("{")) return false;
            if (!line.EndsWith(";", StringComparison.Ordinal)) return false;

            int paren = line.IndexOf('(');
            int equals = line.IndexOf('=');
            if (paren >= 0 && (equals < 0 || paren < equals)) return false; // method or constructor

            string head = equals >= 0 ? line.Substring(0, equals) : line.Substring(0, line.Length - 1);
            string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // tokens: [modifiers...] [type] [name]
            if (tokens.Length < 3) return false;

            fieldName = tokens[tokens.Length - 1];
            return fieldName.Length > 0;
        }

        /// <summary>The identifier immediately before the terminating <c>;</c> of a declaration line.</summary>
        private static string LastIdentifierBeforeSemicolon(string declaration)
        {
            string line = declaration.Trim();
            int equals = line.IndexOf('=');
            string head = equals >= 0 ? line.Substring(0, equals) : line.Substring(0, line.Length - 1);
            string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length == 0 ? string.Empty : tokens[tokens.Length - 1];
        }

        /// <summary>
        /// Every <c>WriteTo</c> body in the file, brace-matched. A record file may hold more than one
        /// (<c>SimRoomDelta.cs</c> holds the delta's and the dropped item's); their written names are
        /// unioned, which is correct because the two field lists are disjoint.
        /// </summary>
        private static IEnumerable<string> ExtractWriteToBodies(string stripped)
        {
            string[] lines = stripped.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("void WriteTo(", StringComparison.Ordinal) < 0) continue;

                int depth = 0;
                bool opened = false;
                var body = new System.Text.StringBuilder();
                for (int j = i; j < lines.Length; j++)
                {
                    string line = lines[j];
                    foreach (char c in line)
                    {
                        if (c == '{') { depth++; opened = true; }
                        else if (c == '}') depth--;
                    }
                    body.AppendLine(line);
                    if (opened && depth <= 0) break;
                }

                yield return body.ToString();
            }
        }
    }
}
