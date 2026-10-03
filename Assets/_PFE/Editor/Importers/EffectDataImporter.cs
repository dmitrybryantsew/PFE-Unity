#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports <see cref="EffectDefinition"/> assets from the <c>&lt;eff&gt;</c> block of
    /// <c>AllData.as</c> — the data half of the status-effect port.
    ///
    /// <para><b>This importer was rewritten on 2026-10-03, and every change fixes a way the previous
    /// one produced plausible-looking wrong data.</b> The old version:</para>
    /// <list type="bullet">
    /// <item>stored the raw <c>t</c> attribute in <c>durationTicks</c> — the oracle multiplies by
    /// <b>30</b> (<c>Effect.as:82</c>), so every duration was 30× too short;</item>
    /// <item>selected nodes with <c>&lt;eff\s+id='…'[^&gt;]*&gt;</c> over the <i>whole file</i>, which
    /// also matches the ~39 <c>&lt;eff id='…' n1='+20%'/&gt;</c> <b>display references</b> inside
    /// items and perks — those are UI tooltips, not effects;</item>
    /// <item>wrote the <c>&lt;sk&gt;</c> payload into <c>SkillModifier[]</c>, a shared perk struct with
    /// one <c>value</c> and a guessed <c>isMultiplier</c> — losing <c>ref</c> (so <c>mult</c> and
    /// <c>add</c> both became "multiply"? no: both became the same guess), the per-level vector, the
    /// <c>tip='res'</c> discrimination, and <c>vd</c> entirely;</item>
    /// <item>parsed exactly one <c>v0</c>…<c>v5</c> value per <c>&lt;sk&gt;</c> by overwriting the same
    /// field, so a <c>&lt;sk v1='0.9' v2='0.8'&gt;</c> kept only the <b>last</b> one it happened to
    /// match;</item>
    /// <item>never read <c>del</c>, <c>post</c>/<c>postbad</c>, <c>lvl1-3</c>, <c>add</c> or
    /// <c>him</c>;</item>
    /// <item>and <b>skipped any asset that already existed</b> — so the wrong data was permanent, and
    /// its fabricated siblings were too.</item>
    /// </list>
    ///
    /// <para>Nothing consumed the old assets, which is why none of this was visible. The rewrite
    /// therefore replaces <b>skip-if-exists</b> with <b>create-or-update</b>, and additionally
    /// <b>removes orphaned assets</b> whose id has no oracle definition — the 23 fabricated rows
    /// (<c>fire</c>, <c>blade</c>, <c>necro</c>, <c>damage</c>, <c>reload</c>, …) that a previous
    /// generator invented one per stat name. Run it via
    /// <c>PFE/Data/Import Effects from AllData.as</c>.</para>
    /// </summary>
    public static class EffectDataImporter
    {
        private static string SourceFilePath => SourceImportPaths.AllDataAsPath;
        private const string OutputPath = "Assets/_PFE/Data/Resources/Effects";

        /// <summary>AS3's tick multiplier — <c>Effect.as:82</c> <c>this.t = node.@t * 30</c>.</summary>
        private const int TicksPerSecond = 30;

        /// <summary>
        /// Duration clamp for stacking — <c>Unit.as:3380-3383</c> clamps the summed <c>t</c> to
        /// <c>30000</c>. Recorded here because an importer that did not know the ceiling could not
        /// explain why <c>add</c> effects stop growing.
        /// </summary>
        private const int MaxStackedTicks = 30000;

        [MenuItem("PFE/Data/Import Effects from AllData.as")]
        public static void ImportEffects()
        {
            if (!File.Exists(SourceFilePath))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(SourceFilePath, "AllData.as"));
                return;
            }

            Directory.CreateDirectory(OutputPath);

            string content;
            using (var reader = new StreamReader(SourceFilePath, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            List<RawEffect> definitions = ExtractDefinitions(content);

            if (definitions.Count == 0)
            {
                // A "0 found" that is reported as success is the failure mode this project keeps
                // hitting. Make it loud, and make it distinguishable from "the block moved".
                Debug.LogError(
                    "Effect import found 0 definitions. The extractor depends on " +
                    "<eff id='…'> … </eff> pairs; if the format changed, fix the extractor " +
                    "rather than treating this as an empty data set.");
                return;
            }

            Debug.Log($"Found {definitions.Count} <eff> definitions in AllData.as");

            var seen = new HashSet<string>();
            int created = 0, updated = 0, failed = 0;

            foreach (RawEffect raw in definitions)
            {
                try
                {
                    string assetPath = $"{OutputPath}/{raw.Id}.asset";

                    // Create-or-update, NOT skip-if-exists. The old importer's skip is what made the
                    // wrong durations and mangled <sk> payloads permanent — see the class comment.
                    var effect = AssetDatabase.LoadAssetAtPath<EffectDefinition>(assetPath);
                    bool isNew = effect == null;
                    if (isNew)
                    {
                        effect = ScriptableObject.CreateInstance<EffectDefinition>();
                    }

                    Apply(effect, raw);

                    if (isNew)
                    {
                        AssetDatabase.CreateAsset(effect, assetPath);
                        created++;
                    }
                    else
                    {
                        EditorUtility.SetDirty(effect);
                        updated++;
                    }

                    seen.Add(raw.Id);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to import effect '{raw.Id}': {e.Message}");
                    failed++;
                }
            }

            int removed = RemoveOrphans(seen);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"Effect import complete — created: {created}, updated: {updated}, " +
                $"orphans removed: {removed}, failed: {failed}.");
        }

        // ── Extraction ────────────────────────────────────────────────────────

        /// <summary>One extracted definition: its open-tag attributes and its raw inner body.</summary>
        private struct RawEffect
        {
            public string Id;
            public string Attrs;
            public string Body;
        }

        /// <summary>
        /// Extract every <c>&lt;eff&gt;</c> <b>definition</b> — the ones written as
        /// <c>&lt;eff id='…' …&gt; … &lt;/eff&gt;</c>.
        ///
        /// <para><b>Why a forward scan rather than one regex.</b> The file contains two shapes of
        /// <c>&lt;eff&gt;</c>:</para>
        /// <list type="number">
        /// <item><b>definition</b> — a container, always closed by <c>&lt;/eff&gt;</c>;</item>
        /// <item><b>display reference</b> — <c>&lt;eff id='crio' n1='+50%'/&gt;</c>, self-closing, used
        /// inside items, perks <i>and inside other effect definitions</i> as a UI string.</item>
        /// </list>
        /// <para>A single regex cannot tell them apart: <c>burning</c> contains
        /// <c>&lt;eff id='crio' n1='+50%'/&gt;</c>, so "<c>&lt;eff&gt;</c> up to the next
        /// <c>&lt;/eff&gt;</c>" would either swallow <c>crio</c> as a child or mis-span the node.
        /// The scan therefore walks open tags in order, skips self-closing ones, and takes the next
        /// <c>&lt;/eff&gt;</c> as the body end — which is exactly the pairing the XML parser AS3 uses
        /// would produce.</para>
        /// </summary>
        private static List<RawEffect> ExtractDefinitions(string content)
        {
            var result = new List<RawEffect>();
            int pos = 0;

            while (pos < content.Length)
            {
                Match m = Regex.Match(content.Substring(pos), "<eff\\b([^>]*?)(/?)>");
                if (!m.Success)
                {
                    break;
                }

                int openEnd = pos + m.Index + m.Length;
                bool selfClosing = m.Groups[2].Value == "/";

                if (selfClosing)
                {
                    // A display reference (<eff id='crio' n1='+50%'/>), not a definition.
                    pos = openEnd;
                    continue;
                }

                int close = content.IndexOf("</eff>", openEnd, StringComparison.Ordinal);
                if (close < 0)
                {
                    // Unterminated container — stop rather than span to EOF and swallow the file.
                    Debug.LogWarning($"Unterminated <eff> at offset {pos + m.Index}; stopping extraction.");
                    break;
                }

                string attrs = m.Groups[1].Value;
                string id = Attr(attrs, "id");
                if (string.IsNullOrEmpty(id))
                {
                    Debug.LogWarning($"<eff> with no id at offset {pos + m.Index}; skipped.");
                }
                else
                {
                    result.Add(new RawEffect
                    {
                        Id = id,
                        Attrs = attrs,
                        Body = content.Substring(openEnd, close - openEnd),
                    });
                }

                pos = close + "</eff>".Length;
            }

            return result;
        }

        // ── Mapping ───────────────────────────────────────────────────────────

        /// <summary>
        /// Write one extracted node onto an asset — field for field from <c>Effect.getXmlParam</c>
        /// (<c>Effect.as:67-137</c>).
        /// </summary>
        private static void Apply(EffectDefinition effect, RawEffect raw)
        {
            effect.effectId = raw.Id;

            // tip — plain enum cast, no default: the census shows all 79 carry it, and '0' is a real
            // value (tip='0' appears once) so there is no "absent" case to invent a fallback for.
            string tip = Attr(raw.Attrs, "tip");
            effect.type = int.TryParse(tip, out int tipValue)
                ? (EffectType)tipValue
                : EffectType.Neutral;

            // Duration. The oracle reads this.t = @t * 30 and then, if the result is 0 — which is what
            // an ABSENT @t produces, since an untyped XML attribute reads as 0 — rewrites t to 30 and
            // sets forever (Effect.as:132-136). Both branches land here: 2 of 79 definitions have no
            // @t (stealth_armor, curse).
            string t = Attr(raw.Attrs, "t");
            int rawTicks = int.TryParse(t, out int tv) ? tv * TicksPerSecond : 0;
            if (rawTicks == 0)
            {
                effect.durationTicks = TicksPerSecond;
                effect.forever = true;
            }
            else
            {
                effect.durationTicks = rawTicks;
                effect.forever = false;
            }

            // val — only 8 rows carry one. A caller-supplied value (addEffect's 2nd arg) overrides it;
            // the oracle's guard is `if(this.val == 0) this.val = node.@val` (Effect.as:87-90), which
            // the runtime reproduces. The asset stores the data value.
            string val = Attr(raw.Attrs, "val");
            effect.value = float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

            // him — the addiction counter seed; @him is presence-tested in the oracle (:104-107).
            string him = Attr(raw.Attrs, "him");
            effect.him = int.TryParse(him, out int himValue) ? himValue : 0;

            // add — the stacking flag; also presence-tested (:120-123), so a present-but-0 would still
            // be true in AS3. The data only ever writes add='1', so the distinction is unobservable
            // here; recorded so a future data change is noticed.
            effect.add = raw.Attrs.Contains("add=");

            // lvl1/2/3 — the duration-escalation thresholds (:108-119).
            effect.lvl1 = ParseIntAttr(raw.Attrs, "lvl1");
            effect.lvl2 = ParseIntAttr(raw.Attrs, "lvl2");
            effect.lvl3 = ParseIntAttr(raw.Attrs, "lvl3");

            // post / postbad. The oracle reads post, then OVERWRITES it from postbad when present
            // (:95-103) — so a node with both ends up on the bad one, and postBad is the flag that
            // says so. In this data set `post` never appears: all 9 are `postbad`.
            effect.afterEffectId = null;
            effect.afterIsBad = false;
            string post = Attr(raw.Attrs, "post");
            if (!string.IsNullOrEmpty(post))
            {
                effect.afterEffectId = post;
            }
            string postBad = Attr(raw.Attrs, "postbad");
            if (!string.IsNullOrEmpty(postBad))
            {
                effect.afterEffectId = postBad;
                effect.afterIsBad = true;
            }

            // del — the mutual-exclusion list, applied when the effect STARTS (Effect.as:143-156).
            effect.deletesOnStart = ExtractDeletes(raw.Body);

            // sk — the stat writes.
            effect.effects = ExtractParams(raw.Body).ToArray();

            // Display name only. The oracle's label comes from a localisation table
            // (Res.txt("e", id)) the port does not have, and `textvar` children are per-level UI
            // strings rather than a name — so the raw id is stored, which is honest, instead of a
            // label derived from `type`. A derived label was tried and was actively wrong: it called
            // every tip=2 effect "Harmful", including tip=2 buffs like `alicorn`.
            effect.displayName = raw.Id;
        }

        /// <summary>
        /// Parse the <c>&lt;sk&gt;</c> children of a definition into <see cref="EffectParam"/>s.
        ///
        /// <para>Straight from <c>Effect.getXmlParam</c> + <c>Unit.setSkillParam</c>: only
        /// <c>tip='res'</c> is a special target, <c>ref</c> selects the operator (and <b>absent means
        /// assign</b>), and the <c>v0</c>…<c>v5</c> vector plus <c>vd</c> are carried intact so the
        /// runtime can pick a level — see <see cref="EffectParam.ValueForLevel"/> for the fallback
        /// chain and the design doc §3 for why the NPC and player paths pick different indices.</para>
        /// </summary>
        private static List<EffectParam> ExtractParams(string body)
        {
            var result = new List<EffectParam>();

            foreach (Match m in Regex.Matches(body, "<sk\\b([^>]*?)/>"))
            {
                string attrs = m.Groups[1].Value;

                string id = Attr(attrs, "id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                // v0..v5. -1 marks "attribute absent", distinct from a present 0 — the fallback chain
                // turns on that difference (`v<index>.length()` is a presence test in AS3).
                float[] perLevel = null;
                int present = 0;
                for (int i = 0; i <= 5; i++)
                {
                    string a = Attr(attrs, "v" + i);
                    if (!string.IsNullOrEmpty(a))
                    {
                        present = Math.Max(present, i + 1);
                    }
                }

                if (present > 0)
                {
                    perLevel = new float[present];
                    for (int i = 0; i < present; i++)
                    {
                        string a = Attr(attrs, "v" + i);
                        perLevel[i] = ParseFloat(a, 0f);
                    }
                }

                string vdStr = Attr(attrs, "vd");
                bool hasDelta = !string.IsNullOrEmpty(vdStr);

                string refStr = Attr(attrs, "ref");
                EffectParamRef op = refStr == "add" ? EffectParamRef.Add
                    : refStr == "mult" ? EffectParamRef.Mult
                    : EffectParamRef.Assign;   // absent → assign. AS3's fall-through; see the enum doc.

                result.Add(new EffectParam
                {
                    id = id,
                    IsResistance = Attr(attrs, "tip") == "res",
                    op = op,
                    perLevel = perLevel,
                    delta = ParseFloat(vdStr, 0f),
                    hasDelta = hasDelta,
                });
            }

            return result;
        }

        private static string[] ExtractDeletes(string body)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(body, "<del\\b([^>]*?)/>"))
            {
                string id = Attr(m.Groups[1].Value, "id");
                if (!string.IsNullOrEmpty(id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        // ── Orphan removal ────────────────────────────────────────────────────

        /// <summary>
        /// Delete assets in the output folder whose id has no oracle definition, and report each one.
        ///
        /// <para><b>Why deletion is the right call here and not for the other importers.</b> The
        /// folder is generated, and a stale row is not a harmless leftover: 23 of the 102 assets were
        /// fabricated one-per-stat-name (<c>fire</c>, <c>blade</c>, <c>necro</c>, <c>damage</c> …) with
        /// <c>type = 0</c> and <c>durationTicks = 0</c>. A runtime lookup for <c>"fire"</c> would
        /// succeed and hand back a zero-duration, typeless effect — a plausible object with no
        /// behaviour. The same reasoning as the ammo item rows: an asset that answers a lookup it
        /// should not is worse than a missing one.</para>
        /// </summary>
        private static int RemoveOrphans(HashSet<string> keep)
        {
            int removed = 0;
            string[] guids = AssetDatabase.FindAssets("t:EffectDefinition", new[] { OutputPath });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string id = Path.GetFileNameWithoutExtension(path);
                if (keep.Contains(id))
                {
                    continue;
                }

                Debug.Log($"Removing orphaned effect asset (no <eff> definition in AllData.as): {id}");
                AssetDatabase.DeleteAsset(path);
                removed++;
            }

            return removed;
        }

        // ── Small helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Read one attribute from a raw XML fragment. Single-quoted only, matching the file's style.
        /// Returns <c>null</c> when absent, so callers can distinguish "absent" from "empty".
        /// </summary>
        private static string Attr(string attrs, string name)
        {
            if (string.IsNullOrEmpty(attrs))
            {
                return null;
            }

            Match m = Regex.Match(attrs, name + "='([^']*)'");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int ParseIntAttr(string attrs, string name)
        {
            string s = Attr(attrs, name);
            return int.TryParse(s, out int v) ? v : 0;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s))
            {
                return fallback;
            }

            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }
}
#endif
