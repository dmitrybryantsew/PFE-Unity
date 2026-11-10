using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 1.3 lint (F-01/F-02): the Sim state records — <see cref="PFE.Sim.State.SimUnitState"/>,
    /// <see cref="PFE.Sim.State.SimProjectileState"/>, <see cref="PFE.Sim.State.SimRoomDelta"/> and the
    /// nested <see cref="PFE.Sim.State.SimDroppedItem"/> — must stay <b>pure value-type state</b>. The
    /// readiness doc's acceptance for row 1.3 is <i>"all fields are value types, <c>EntityId.Hash</c>,
    /// or interned ids — no <c>UnityEngine.Object</c>, no <c>Transform</c>"</i>. This is that guard.
    ///
    /// <para><b>Why this lint exists.</b> The whole point of the record is that a snapshot can be
    /// serialized, hashed and re-simulated without dragging a Unity object graph onto the wire. The
    /// moment a field of type <c>Transform</c>, <c>GameObject</c>, <c>Sprite</c>, or any
    /// <c>UnityEngine.Object</c> appears in one of these structs, the record stops being a record and
    /// becomes a view reference — the exact defect F-02 names. The compiler cannot forbid it (the field
    /// is legal C#), so a source lint is the only guard that fails in this harness.</para>
    ///
    /// <para><b>What is checked, structurally.</b> For each record file the lint (1) asserts the type is
    /// a <c>struct</c>, not a <c>class</c> — a reference type would allocate on every snapshot and break
    /// value-equality semantics; (2) scans every field declaration and rejects a field whose type names
    /// a forbidden Unity or reference shape (<c>Transform</c>, <c>GameObject</c>, <c>UnityEngine</c>,
    /// <c>MonoBehaviour</c>, <c>ScriptableObject</c>, <c>Sprite</c>, <c>Rigidbody</c>, <c>Collider</c>,
    /// or the reference types <c>string</c> / <c>object</c> / <c>List&lt;</c> / <c>Dictionary&lt;</c>);
    /// and (3) asserts the file has no <c>using UnityEngine</c> import, which would be the first step
    /// toward such a field. The one allowed reference shape is a <b>array of a value type</b>
    /// (<c>SimUnitState[]</c>, <c>ulong[]</c>) on <c>SimRoomDelta</c> — a variable-length delta payload,
    /// whose <i>elements</i> are themselves pinned as structs by check (1).</para>
    ///
    /// <para><b>Why <c>string</c> is rejected even though it is not Unity.</b> The readiness doc is
    /// explicit (§9, F-05 caveat): <c>EntityId.DebugString</c> is a <c>string</c> and is <b>not</b>
    /// blittable, so the wire carries <c>Hash</c> only and a debug-side table holds the text. A
    /// <c>string</c> field in a state record would silently put that text back on the wire. Identity
    /// here is <c>ulong</c> (<c>EntityHash</c>, <c>RoomHash</c>, <c>ItemIdHash</c>) or an interned
    /// <c>ushort</c> (<c>DefinitionId</c>) — both value types.</para>
    ///
    /// <para><b>Mutation control.</b> The lint proves it can go red by simulating the precise failure it
    /// guards: a record field typed <c>Transform</c> is run through the same predicate and asserted to
    /// be flagged. A guard that cannot fail is not a guard.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimStateRecordPurityLintTests
    {
        // The record files, relative to Assets/_PFE. A fifth state record is a deliberate addition.
        private static readonly string[] RecordFiles =
        {
            "Sim/State/SimMotorState.cs",
            "Sim/State/SimUnitState.cs",
            "Sim/State/SimProjectileState.cs",
            "Sim/State/SimRoomDelta.cs",
            "Sim/State/SimTileMutation.cs",
        };

        // Type-name fragments that mark a field as a Unity object or a non-wire reference type. A
        // field whose declared type contains any of these is rejected. Matched against the type token
        // only, never the field name, so `public float Transform;` would pass the name check but the
        // type `Transform` is what is actually rejected.
        private static readonly string[] ForbiddenTypeFragments =
        {
            "Transform", "GameObject", "UnityEngine", "MonoBehaviour", "ScriptableObject",
            "Sprite", "Rigidbody", "Collider", "Vector2", "Vector3", "Quaternion", "Rect",
            "string", "object", "List<", "Dictionary<", "Color",
        };

        [Test]
        public void EveryStateRecord_IsAStruct_NotAClass()
        {
            var violations = new List<string>();

            foreach (string file in RecordFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                // The type declaration must read "public struct X" (or "public sealed struct"). A
                // "public class X" record would allocate per snapshot and break value equality.
                if (!stripped.Contains("struct "))
                {
                    violations.Add($"{file}: declares no struct — a state record must be a value type");
                }
                if (stripped.Contains(" class "))
                {
                    violations.Add($"{file}: declares a class — a state record must be a struct, not a " +
                        "reference type (per-snapshot allocation + lost value equality)");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "Every Sim state record must be a struct (value type). Offending files:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void NoStateRecordField_HasAUnityOrReferenceType()
        {
            var violations = new List<string>();

            foreach (string file in RecordFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                string[] lines = stripped.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!TryGetFieldType(lines[i], out string typeToken))
                    {
                        continue;
                    }

                    foreach (string forbidden in ForbiddenTypeFragments)
                    {
                        if (typeToken.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
                        {
                            violations.Add($"{file}:{i + 1}: field type '{typeToken}' names forbidden " +
                                $"fragment '{forbidden}'");
                            break;
                        }
                    }
                }
            }

            CollectionAssert.IsEmpty(violations,
                "No Sim state record field may be a UnityEngine.Object, a Transform, a Unity struct " +
                "(Vector2/Vector3/Color/Rect), or a non-wire reference type (string/object/List/" +
                "Dictionary). Identity is ulong/ushort; positions are float pairs. Offending fields:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void NoStateRecord_ImportsUnityEngine()
        {
            var violations = new List<string>();

            foreach (string file in RecordFiles)
            {
                string stripped = SourceLint.ReadStripped(file);
                foreach (string raw in stripped.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("using ", StringComparison.Ordinal) &&
                        line.IndexOf("UnityEngine", StringComparison.Ordinal) >= 0)
                    {
                        violations.Add($"{file}: imports '{line}' — a state record must not touch UnityEngine");
                    }
                }
            }

            CollectionAssert.IsEmpty(violations,
                "A Sim state record must not import UnityEngine. Offending files:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void MutationControl_ATransformField_IsDetected()
        {
            // The exact failure this lint guards: a record gains `public Transform View;`. Run the same
            // predicate over that declaration and assert it is flagged. If the predicate cannot flag a
            // Transform field, the green results above are vacuous.
            bool flagged = false;
            if (TryGetFieldType("public Transform View;", out string typeToken))
            {
                foreach (string forbidden in ForbiddenTypeFragments)
                {
                    if (typeToken.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
                    {
                        flagged = true;
                        break;
                    }
                }
            }

            Assert.IsTrue(flagged,
                "Mutation control failed: a `public Transform View;` field was not detected. The " +
                "type-scan predicate is broken and the purity results above cannot be trusted.");
        }

        [Test]
        public void MutationControl_AStringField_IsDetected()
        {
            // The F-05 caveat failure: identity as a `string` instead of a `ulong` hash.
            bool flagged = false;
            if (TryGetFieldType("public string EntityId;", out string typeToken))
            {
                foreach (string forbidden in ForbiddenTypeFragments)
                {
                    if (typeToken.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
                    {
                        flagged = true;
                        break;
                    }
                }
            }

            Assert.IsTrue(flagged,
                "Mutation control failed: a `public string EntityId;` field was not detected. The wire " +
                "carries EntityId.Hash (ulong) only; a string field must be rejected.");
        }

        /// <summary>
        /// Extracts the declared type token from a line that declares a public field, or returns false
        /// for any line that is not a simple field declaration (methods, properties, comments, the type
        /// declaration itself, attribute lines, array-initializer lines).
        /// </summary>
        /// <remarks>
        /// Grammar: a field line ends with ';' (optionally after an initializer), starts with an
        /// accessibility modifier, has no '(' before any '=', and is not a property (no '{ get'). The
        /// type token is the second-to-last whitespace-separated token before the field name. Arrays of
        /// value types (<c>SimUnitState[]</c>) yield a token ending in "[]" whose element type is a
        /// struct — these are allowed, and the forbidden-fragment scan above does not match them.
        /// </remarks>
        private static bool TryGetFieldType(string rawLine, out string typeToken)
        {
            typeToken = null;
            string line = rawLine.Trim();
            if (line.Length == 0) return false;

            bool scoped = line.StartsWith("public ", StringComparison.Ordinal)
                || line.StartsWith("internal ", StringComparison.Ordinal);
            if (!scoped) return false;

            // Not a field.
            if (line.Contains(" class ") || line.Contains(" struct ") || line.Contains(" enum ")) return false;
            if (line.Contains(" const ") || line.Contains(" static ")) return false;
            if (line.Contains("=>")) return false;
            if (line.Contains("{")) return false; // property, method body, or type declaration

            int paren = line.IndexOf('(');
            int equals = line.IndexOf('=');
            if (paren >= 0 && (equals < 0 || paren < equals)) return false; // method/ctor

            if (!line.EndsWith(";", StringComparison.Ordinal)) return false;

            string head = equals >= 0 ? line.Substring(0, equals) : line.Substring(0, line.Length - 1);
            string[] tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // tokens: [modifiers...] [type] [name]  → need at least modifier + type + name.
            if (tokens.Length < 3) return false;

            typeToken = tokens[tokens.Length - 2];
            return typeToken.Length > 0;
        }
    }
}
