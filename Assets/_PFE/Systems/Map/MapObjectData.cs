using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PFE.Systems.Map
{
    [Serializable]
    public class MapObjectAttributeData
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class MapObjectItemData
    {
        public string id;
        public List<MapObjectAttributeData> attributes = new List<MapObjectAttributeData>();

        public string GetAttribute(string key, string defaultValue = "")
        {
            return MapObjectDataUtility.GetAttribute(attributes, key, defaultValue);
        }
    }

    [Serializable]
    public class MapObjectScriptActionData
    {
        public string act;
        public string targ;
        public string val;
    }

    [Serializable]
    public class MapObjectScriptData
    {
        public string eventName;
        public List<MapObjectScriptActionData> actions = new List<MapObjectScriptActionData>();
    }

    [Serializable]
    public class MapObjectDynamicStateData
    {
        public bool isDynamic;
        public bool isGrounded;
        public bool isHeldByTelekinesis;
        public bool isThrown;
        public bool hasTelekineticTarget;
        public Vector2 velocity;
        public Vector2 telekineticTarget;
        public float throwGraceTime;
        public float lastImpactSpeed;

        /// <summary>
        /// AS3 <c>Obj.levitPoss</c> (<c>Obj.as:32</c>) — can this instance be picked up by
        /// telekinesis <b>right now</b>.
        ///
        /// <para><b>It is per instance and runtime-mutable, which is the whole point.</b> The
        /// definition-level question ("is this kind of prop telekinetic?") is a different one, already
        /// answered by <c>MapObjectDefinition.SupportsTelekinesis()</c> from the authored
        /// <c>wall</c>/<c>tip</c>/<c>massa</c> data — AS3's authored half of this flag is
        /// <c>Box.as:392</c>, <c>if(wall &gt; 0) { levitPoss = false; stay = true; }</c>. But AS3 also
        /// <i>clears</i> it at runtime, and the port has to be able to as well: a held object that has
        /// exhausted its levitation allowance (<c>UnitPlayer.as:1265-1268</c>) sets
        /// <c>levitPoss = false</c> and is dropped, and <c>Unit.as:3112</c> clears it when levitation
        /// expires.</para>
        ///
        /// <para>Default <c>true</c> is AS3's own field default, so a state written before this field
        /// existed deserialises as liftable — the same "zero is the safe sentinel" reasoning as
        /// <c>UnitDefinition.massafix</c>.</para>
        /// </summary>
        public bool levitPoss = true;

        /// <summary>
        /// AS3 <c>Obj.stay</c> — <b>at rest</b>, not "authored as fixed".
        ///
        /// <para><c>Box.as:572</c> inits it false, <c>:393</c> forces it true for <c>wall &gt; 0</c>,
        /// <c>:1104</c> sets it true on landing and <c>:965</c>/<c>:1037</c>/<c>:1119</c> clear it.
        /// AS3 also clears it <i>when it grabs</i> (<c>UnitPlayer.as:1831</c>), so it is a
        /// consequence of being picked up as much as a precondition for it.</para>
        ///
        /// <para><b>Where the oracle actually tests it.</b> <c>GUI.as:1326</c> — the HUD's "you may
        /// lift this" hint — is <c>stay &amp;&amp; levitPoss &amp;&amp; celDist &lt;= teleDist &amp;&amp;
        /// massa &lt;= maxTeleMassa</c>. The <i>action</i>, <c>UnitPlayer.actTele</c>, does
        /// <b>not</b> test <c>stay</c>: its candidate loop (<c>:1769-1783</c>) filters on
        /// <c>levitPoss</c> and <c>massa</c> only, and the gate at <c>:1790</c> adds
        /// <c>onCursor</c>/<c>celDist</c>. So AS3 will grab a prop that is still moving, and merely
        /// declines to <i>advertise</i> it.</para>
        ///
        /// <para><b>Correction (2026-10-02): the port no longer gates grabs on this.</b> This comment
        /// used to say that <c>RoomObjectPhysicsLayer.TryFindTelekineticCandidate</c> applied
        /// <c>stay</c> as a grab precondition and so refused props caught mid-flight. That was true
        /// when it was written and is not any more — the test was removed on 2026-10-02, so the
        /// candidate filter matches the oracle's <c>levitPoss</c> + <c>massa</c> pair. "Already held"
        /// is asked directly via <c>IsHeldByTelekinesis</c> instead, which is what <c>stay</c> was
        /// accidentally standing in for. The comment is kept as a correction rather than deleted
        /// because the wrong version is the more intuitive reading of the oracle.</para>
        ///
        /// <para><b>What <i>does</i> read it now: prop stacking.</b> <c>Box.checkShelf</c> requires
        /// the support to be <c>stay</c> (<c>Box.as:1198</c>) — a crate still settling is not a floor
        /// yet — and the <c>osnova</c> follow detaches when the support stops being <c>stay</c>
        /// (<c>:577-580</c>). So this field is load-bearing for towers even though it is not part of
        /// the grab gate.</para>
        ///
        /// <para>Maintained by <c>RoomObjectPhysicsLayer</c> rather than authored: it is a function of
        /// contact and speed, so it has to be recomputed as the prop moves.</para>
        /// </summary>
        public bool stay;

        /// <summary>
        /// AS3 <c>Box.osnova</c> (<c>Box.as:20</c>) — <b>the prop this one is resting on</b>, if it is
        /// resting on another prop rather than on a tile. <c>Box.checkShelf</c> assigns it
        /// (<c>Box.as:1200</c>) and the follow block reads it (<c>Box.as:573-595</c>).
        ///
        /// <para><b>Why it exists.</b> A tile never moves, so a prop standing on one needs no link. A
        /// prop standing on another prop does: when the lower crate is pushed or lifted, the upper one
        /// has to travel with it, and when the lower one is knocked out from under it the upper one has
        /// to fall. Without this link a stack is just two props that happen to be near each other, and
        /// pushing the bottom crate out from under a tower leaves the tower hanging in the air — which
        /// is exactly the shape of "I can't build a tower".</para>
        ///
        /// <para><b><c>[NonSerialized]</c> is load-bearing, not tidiness.</b> This is a reference from
        /// <c>ObjectInstance.runtimeState.dynamicState</c> back to an <c>ObjectInstance</c> — i.e. a
        /// cycle through a <c>[Serializable]</c> graph. Unity's serializer would follow it, and since
        /// two crates resting on each other in a ring is representable, it would recurse until it hit
        /// its depth limit and logged an error on every save. Excluding it is what keeps the field
        /// legal; the link is rebuilt by <c>checkShelf</c> on the next step after a load, which is
        /// where AS3 builds it too.</para>
        /// </summary>
        [NonSerialized]
        public ObjectInstance osnova;

        /// <summary>
        /// AS3 <c>Box.cdx</c> — how far this prop actually moved on the x axis during the last tick
        /// (<c>Box.as:643</c>, <c>cdx = X - stX</c>).
        ///
        /// <para><b>This is the mechanism by which a stack stays together.</b> <c>Box.as:573-595</c>
        /// translates a rider by its support's <c>cdx</c>/<c>cdy</c>, so a crate riding another crate
        /// follows the *displacement*, not the velocity — which is why a rider keeps up exactly, with
        /// no lag and no accumulation error, and why a rider is detached (rather than left behind) when
        /// the support's move is blocked.</para>
        /// </summary>
        public float cdx;

        /// <inheritdoc cref="cdx"/>
        public float cdy;

        // ── Runtime-only diagnostics: the last move this prop attempted and was refused ─────────────
        //
        // These exist because "the grab succeeded and the box still does not move" has no observable
        // inside the hold path. `UpdateHold` writes a target and `StepHeldObject` integrates a
        // velocity, and if `HasCollision` refuses every sub-step the prop simply never moves: no
        // error, no refusal, nothing in the trace. Recording the refused position (and the tick it
        // was refused on) lets `tele probe` name the blocking tile instead of leaving the next
        // session to guess between "the mover is not running" and "the mover is running and being
        // refused" -- two causes with opposite fixes.
        //
        // Written on the reject path only, so a prop that moves freely never touches them. Not
        // authored, not serialised gameplay state; a stale value is disambiguated by the tick.

        /// <summary>True once a move has been refused; the three fields below then describe it.</summary>
        public bool hasRejectedMove;

        /// <summary>True if the refused move was the vertical axis, false if horizontal.</summary>
        public bool rejectedMoveVertical;

        /// <summary>The room-local position the step tried to move to and was refused.</summary>
        public Vector2 rejectedMoveCandidate;

        /// <summary>The physics-layer tick the refusal happened on (see <c>RoomObjectPhysicsLayer.TickCount</c>).</summary>
        public int rejectedMoveTick;
    }

    [Serializable]
    public class MapObjectRuntimeStateData
    {
        public bool isDestroyed;
        public bool isOpen;
        public bool isExploded;
        public int lootState;
        public bool hasLockValue;
        public float lockValue;
        public bool hasLockLevel;
        public int lockLevel;
        public bool hasMineValue;
        public int mineValue;
        public bool hasDifficultyState;
        public int difficultyState;
        public bool hasSignState;
        public int signState;
        public MapObjectDynamicStateData dynamicState = new MapObjectDynamicStateData();

        public void InitializeFromAttributes(List<MapObjectAttributeData> attributes)
        {
            if (MapObjectDataUtility.TryGetFloatAttribute(attributes, "lock", out float parsedLock))
            {
                hasLockValue = true;
                lockValue = parsedLock;
            }

            if (MapObjectDataUtility.TryGetIntAttribute(attributes, "locklevel", out int parsedLockLevel))
            {
                hasLockLevel = true;
                lockLevel = parsedLockLevel;
            }

            if (MapObjectDataUtility.TryGetIntAttribute(attributes, "mine", out int parsedMine))
            {
                hasMineValue = true;
                mineValue = parsedMine;
            }

            dynamicState ??= new MapObjectDynamicStateData();
        }
    }

    public static class MapObjectDataUtility
    {
        private static readonly Regex LegacyParameterRegex =
            new Regex("(\\w+)\\s*=\\s*(['\"])(.*?)\\2", RegexOptions.Compiled);

        public static List<MapObjectAttributeData> CloneAttributes(List<MapObjectAttributeData> source)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectAttributeData attribute = source[i];
                if (attribute == null)
                {
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = attribute.key,
                    value = attribute.value
                });
            }

            return result;
        }

        public static List<MapObjectItemData> CloneItems(List<MapObjectItemData> source)
        {
            List<MapObjectItemData> result = new List<MapObjectItemData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectItemData item = source[i];
                if (item == null)
                {
                    continue;
                }

                result.Add(new MapObjectItemData
                {
                    id = item.id,
                    attributes = CloneAttributes(item.attributes)
                });
            }

            return result;
        }

        public static List<MapObjectScriptData> CloneScripts(List<MapObjectScriptData> source)
        {
            List<MapObjectScriptData> result = new List<MapObjectScriptData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                MapObjectScriptData script = source[i];
                if (script == null)
                {
                    continue;
                }

                MapObjectScriptData clonedScript = new MapObjectScriptData
                {
                    eventName = script.eventName
                };

                if (script.actions != null)
                {
                    for (int actionIndex = 0; actionIndex < script.actions.Count; actionIndex++)
                    {
                        MapObjectScriptActionData action = script.actions[actionIndex];
                        if (action == null)
                        {
                            continue;
                        }

                        clonedScript.actions.Add(new MapObjectScriptActionData
                        {
                            act = action.act,
                            targ = action.targ,
                            val = action.val
                        });
                    }
                }

                result.Add(clonedScript);
            }

            return result;
        }

        public static List<MapObjectAttributeData> BuildAttributes(IReadOnlyDictionary<string, string> source, params string[] excludedKeys)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            if (source == null)
            {
                return result;
            }

            HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedKeys != null)
            {
                for (int i = 0; i < excludedKeys.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(excludedKeys[i]))
                    {
                        excluded.Add(excludedKeys[i]);
                    }
                }
            }

            foreach (KeyValuePair<string, string> kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || excluded.Contains(kvp.Key))
                {
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = kvp.Key,
                    value = kvp.Value ?? string.Empty
                });
            }

            return result;
        }

        public static string BuildLegacyParameters(string code, string uid, List<MapObjectAttributeData> attributes)
        {
            StringBuilder builder = new StringBuilder();
            AppendLegacyParameter(builder, "code", code);
            AppendLegacyParameter(builder, "uid", uid);

            if (attributes != null)
            {
                for (int i = 0; i < attributes.Count; i++)
                {
                    MapObjectAttributeData attribute = attributes[i];
                    if (attribute == null || string.IsNullOrWhiteSpace(attribute.key))
                    {
                        continue;
                    }

                    AppendLegacyParameter(builder, attribute.key, attribute.value);
                }
            }

            return builder.ToString().TrimEnd();
        }

        public static List<MapObjectAttributeData> ParseLegacyParameters(string parameters, out string code, out string uid)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            code = string.Empty;
            uid = string.Empty;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                return result;
            }

            MatchCollection matches = LegacyParameterRegex.Matches(parameters);
            for (int i = 0; i < matches.Count; i++)
            {
                string key = matches[i].Groups[1].Value;
                string value = matches[i].Groups[3].Value;

                if (key.Equals("code", StringComparison.OrdinalIgnoreCase))
                {
                    code = value;
                    continue;
                }

                if (key.Equals("uid", StringComparison.OrdinalIgnoreCase))
                {
                    uid = value;
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = key,
                    value = value
                });
            }

            return result;
        }

        public static string GetAttribute(List<MapObjectAttributeData> attributes, string key, string defaultValue = "")
        {
            if (attributes == null || string.IsNullOrWhiteSpace(key))
            {
                return defaultValue;
            }

            for (int i = 0; i < attributes.Count; i++)
            {
                MapObjectAttributeData attribute = attributes[i];
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.key))
                {
                    continue;
                }

                if (string.Equals(attribute.key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute.value ?? defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Is the attribute <b>present</b> at all, regardless of its value?
        ///
        /// <para><b>Not the same question as <c>GetAttribute(...) != ""</c>, and AS3 asks this one.</b>
        /// <c>node.@shelf.length()</c> (<c>Box.as:272</c>) counts the matching attributes, so an
        /// attribute authored with an empty value is <i>present</i> and AS3 takes the branch. A
        /// non-emptiness test would take the other branch. Both questions exist in the oracle; this is
        /// the presence one, and it is separate here so a caller cannot confuse them.</para>
        /// </summary>
        public static bool HasAttribute(List<MapObjectAttributeData> attributes, string key)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            for (int i = 0; i < attributes.Count; i++)
            {
                MapObjectAttributeData attribute = attributes[i];
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.key))
                {
                    continue;
                }

                if (string.Equals(attribute.key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetIntAttribute(List<MapObjectAttributeData> attributes, string key, out int value)
        {
            string rawValue = GetAttribute(attributes, key, string.Empty);
            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryGetFloatAttribute(List<MapObjectAttributeData> attributes, string key, out float value)
        {
            string rawValue = GetAttribute(attributes, key, string.Empty);
            return float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void AppendLegacyParameter(StringBuilder builder, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value))
            {
                return;
            }

            builder.Append(key);
            builder.Append("=\"");
            builder.Append(value.Replace("\"", "\\\""));
            builder.Append("\" ");
        }
    }
}
