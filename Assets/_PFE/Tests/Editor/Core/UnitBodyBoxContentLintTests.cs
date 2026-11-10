using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Systems.Physics;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Row 0.3's content half: <b>the serialized player definition must agree with the oracle</b>.
    ///
    /// <para><b>History, kept because it explains the shape of this fixture.</b> This fixture was
    /// deliberately RED from 2026-11-08 until 2026-11-09 17:42, when the owner chose canonical option
    /// A and set the legacy asset to 0.5 / 0.7 / 0.35 in place. It is now expected to be GREEN.
    /// Phase 0.1's precedent — a red guard that names the defect is worth more than a green one that
    /// hides it — is why the numbers below are oracle literals rather than values read back off the
    /// asset.</para>
    ///
    /// <para><b>The divergence, measured.</b> The oracle's littlepip is
    /// <c>&lt;phis sX='50' sY='70'/&gt;</c> (<c>AllData.as:21</c>) with <c>sitY = stayY / 2 = 35</c>
    /// derived by <c>Unit.as:1041-1048</c>. The serialized asset says <c>width: 0.55</c> (55 px) and
    /// <c>sitHeight: 0.3</c> (30 px): the body is <b>5 px too wide</b> and crouches <b>5 px too
    /// short</b>. Height is correct.</para>
    ///
    /// <para><b>Why this is a multiplayer defect and not cosmetics.</b> The box is what collides. Two
    /// peers that disagree about it by 5 px resolve the same jump, the same gap and the same crouch
    /// differently, and the disagreement is invisible: both see a plausible game. Row 0.3 exists to make
    /// the box a content-versioned value both ends compute identically.</para>
    ///
    /// <para><b>The importer cannot fix this file — do not re-add that instruction.</b>
    /// <c>UnitDataImporter</c> writes <c>Data/Resources/Units/&lt;id&gt;.asset</c>, while this fixture
    /// and <c>Player.prefab._stats</c> read <c>Data/Resources/UnitDef_Littlepip.asset</c>. Running
    /// <c>PFE/Data/Reimport Units (Overwrite Existing)</c> reports <c>Updated: 149</c> and leaves these
    /// assertions red. Both files now agree on the three box fields; <see
    /// cref="TheCanonicalAndGeneratedDefinitions_AgreeOnTheBodyBox"/> pins that they stay in step.
    /// Asset edits and importer runs are owner-only in this project, which is why this guard reports
    /// rather than repairs.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitBodyBoxContentLintTests
    {
        private const string AssetPath = "Data/Resources/UnitDef_Littlepip.asset";

        /// <summary>The importer's output — a SECOND littlepip definition, not a duplicate (12 vs 34
        /// unique serialized keys). Option A kept <see cref="AssetPath"/> canonical, so the next
        /// overwrite re-import can silently re-split the box unless their three fields are pinned.</summary>
        private const string GeneratedAssetPath = "Data/Resources/Units/littlepip.asset";

        /// <summary>A real, on-disk definition that differs from littlepip in exactly one box field:
        /// <c>zombie0</c> is <c>&lt;phis sX='55' sY='70'/&gt;</c>, so width 0.55 vs 0.5 while height
        /// and sitHeight match. Used as the positive control for the parity comparator — a synthetic
        /// regex mutation alone cannot prove the comparator reads real asset text.</summary>
        private const string DecoyAssetPath = "Data/Resources/Units/zombie0.asset";

        /// <summary>The runtime reads five serialized numbers off the player prefab. The EditMode
        /// fixtures that inspect it need Unity's AssetDatabase/PrefabUtility and throw
        /// <c>SecurityException</c> under the offline wall, so a text-level lint is the only guard
        /// that runs on every offline run. A plain GUI save re-serializes byte-identical numbers
        /// (there is no serialization callback), which is why the owner's Inspector edit needs a
        /// guard of its own rather than being inferred from the asset lints above.</summary>
        private const string PlayerPrefabPath = "Prefabs/Player.prefab";

        // ── The oracle, cited ──────────────────────────────────────────────────────────────
        // pfe/scripts/fe/AllData.as:20-24  <unit id='littlepip'><phis sX='50' sY='70' massa='50'/>
        // pfe/scripts/fe/unit/Unit.as:1030-1048  sitX = stayX, sitY = stayY / 2 when absent
        private const float OracleWidthPx = 50f;
        private const float OracleHeightPx = 70f;
        private const float OracleSitHeightPx = 35f;

        private static string ReadAsset() => ReadAssetAt(AssetPath);

        private static string ReadAssetAt(string assetPath)
        {
            string fullPath = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE",
                assetPath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath),
                "Lint target missing: " + fullPath + ". If the asset moved, update this fixture rather " +
                "than leaving a missing-path error that reads like a broken lint.");

            return File.ReadAllText(fullPath);
        }

        /// <summary>Compare the three box fields the collision path actually consumes. Anything else in
        /// the two assets is allowed to differ — they are different content records.</summary>
        private static bool BoxesAgree(string canonical, string generated)
        {
            foreach (string field in new[] { "width", "height", "sitHeight" })
            {
                if (!ReadField(canonical, field).Equals(ReadField(generated, field)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Report all three fields for the failure message. Built from the same
        /// <see cref="ReadField"/> the comparison uses — a diagnostic that parsed the asset a different
        /// way could disagree with the verdict it is explaining.</summary>
        private static string DescribeBox(string yaml) =>
            "width=" + ReadField(yaml, "width").ToString("F3", CultureInfo.InvariantCulture) +
            " height=" + ReadField(yaml, "height").ToString("F3", CultureInfo.InvariantCulture) +
            " sitHeight=" + ReadField(yaml, "sitHeight").ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>The BoxCollider2D YAML document, scoped by its class id (<c>!u!61</c>). The prefab
        /// has other colliders with their own <c>m_Size</c>/<c>m_Offset</c>, so an unscoped regex
        /// would read the wrong box and stay green after the physics body regressed.</summary>
        private static string BoxColliderBlock(string yaml)
        {
            const string header = "--- !u!61 ";
            int start = yaml.IndexOf(header, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                "Player.prefab has no BoxCollider2D (!u!61) document. The player's tile body is gone, " +
                "or Unity changed the class id; either way this lint must not silently skip it.");

            int next = yaml.IndexOf("--- !u!", start + header.Length, StringComparison.Ordinal);
            return next < 0 ? yaml.Substring(start) : yaml.Substring(start, next - start);
        }

        /// <summary>One axis of a serialized <c>{x, y}</c> field inside the physics BoxCollider2D.</summary>
        private static float ColliderAxis(string yaml, string field, string axis)
        {
            Match match = Regex.Match(BoxColliderBlock(yaml),
                @"^[ \t]*" + field + @":[ \t]*\{[ \t]*x:[ \t]*(-?[0-9.]+)[ \t]*,[ \t]*y:[ \t]*(-?[0-9.]+)[ \t]*\}[ \t]*\r?$",
                RegexOptions.Multiline);

            Assert.IsTrue(match.Success,
                "The player's BoxCollider2D does not serialize '" + field + "' in the expected {x, y} " +
                "form. A lint that cannot parse the value must fail, not skip.");

            return float.Parse(match.Groups[axis == "x" ? 1 : 2].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>Compare all five serialized numbers the collision path consumes against the oracle
        /// literals — not against values read back out of the prefab. World units are derived from the
        /// pixel oracle (sX / 100), so there is no second copy of the number to drift.</summary>
        private static bool PrefabBoxAgrees(string yaml)
        {
            const float epsilon = 1e-3f;
            float widthWorld = OracleWidthPx / 100f;
            float heightWorld = OracleHeightPx / 100f;

            return Math.Abs(ReadField(yaml, "collisionWidth") - OracleWidthPx) <= epsilon
                && Math.Abs(ReadField(yaml, "collisionHeight") - OracleHeightPx) <= epsilon
                && Math.Abs(ReadField(yaml, "crouchedCollisionHeight") - OracleSitHeightPx) <= epsilon
                && Math.Abs(ColliderAxis(yaml, "m_Size", "x") - widthWorld) <= epsilon
                && Math.Abs(ColliderAxis(yaml, "m_Size", "y") - heightWorld) <= epsilon
                && Math.Abs(ColliderAxis(yaml, "m_Offset", "x")) <= epsilon
                && Math.Abs(ColliderAxis(yaml, "m_Offset", "y") - heightWorld / 2f) <= epsilon;
        }

        /// <summary>Diagnostic built from the same parsers the verdict uses.</summary>
        private static string DescribePrefabBox(string yaml) =>
            "motor=" + ReadField(yaml, "collisionWidth").ToString("F0", CultureInfo.InvariantCulture) +
            "x" + ReadField(yaml, "collisionHeight").ToString("F0", CultureInfo.InvariantCulture) +
            "/" + ReadField(yaml, "crouchedCollisionHeight").ToString("F0", CultureInfo.InvariantCulture) +
            " colliderSize=" + ColliderAxis(yaml, "m_Size", "x").ToString("F2", CultureInfo.InvariantCulture) +
            "x" + ColliderAxis(yaml, "m_Size", "y").ToString("F2", CultureInfo.InvariantCulture) +
            " colliderOffsetY=" + ColliderAxis(yaml, "m_Offset", "y").ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>
        /// Read one serialized float. Anchored to the start of a line so a field name inside a nested
        /// block or a comment cannot satisfy it, and parsed with the invariant culture so a machine set
        /// to a comma decimal separator reads the same value as the importer that wrote it.
        /// </summary>
        private static float ReadField(string yaml, string field)
        {
            // `\r?$`, not `$`: a Unity YAML asset is CRLF, and in .NET `$` matches before the `\n`
            // only — so `[ \t]*$` cannot span the `\r` and the pattern matches nothing at all. The
            // failure then reads as "the asset does not serialize width", which is a lie.
            Match match = Regex.Match(yaml, @"^[ \t]*" + field + @":[ \t]*(-?[0-9.]+)[ \t]*\r?$",
                RegexOptions.Multiline);

            Assert.IsTrue(match.Success,
                "The asset does not serialize '" + field + "'. The importer must write all three box " +
                "fields, or the definition falls back to a default nobody chose.");

            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [Test]
        public void LittlepipAsset_ResolvesToTheOracleBodyBox()
        {
            string yaml = ReadAsset();

            UnitBodyBox box = UnitBodyBoxRules.ResolveFromWorldUnits(
                ReadField(yaml, "width"),
                ReadField(yaml, "height"),
                ReadField(yaml, "sitHeight"));

            // One assertion per axis so a failure names which number is wrong, and reports the actual
            // value — a bare "expected 50 but was 55" makes the reader go and look it up.
            Assert.AreEqual(OracleWidthPx, box.WidthPx, 1e-3f,
                "The player's body box is " + box.WidthPx.ToString("F0") + " px wide; the oracle's " +
                "littlepip is 50 (AllData.as:21 sX='50'). A 5 px-wider body collides differently on " +
                "every peer that shares this content. Fix: re-run the unit importer — ParsePhysics " +
                "reads sX as /100, so the asset becomes 0.50.");

            Assert.AreEqual(OracleHeightPx, box.HeightPx, 1e-3f,
                "The player's body box is " + box.HeightPx.ToString("F0") + " px tall; the oracle's is " +
                "70 (AllData.as:21 sY='70').");

            Assert.AreEqual(OracleSitHeightPx, box.SitHeightPx, 1e-3f,
                "The player crouches to " + box.SitHeightPx.ToString("F0") + " px; the oracle derives " +
                "sitY = stayY / 2 = 35 (Unit.as:1041-1048). Fix: re-run the unit importer — ParsePhysics " +
                "writes sY / 100 / 2 when sitY is absent, so the asset becomes 0.35.");
        }

        /// <summary>
        /// The stored crouch must be the <i>derived</i> value, not merely a number that happens to be in
        /// range. Without this, an asset could satisfy the box check by authoring 0.35 while the importer
        /// silently stopped deriving — the two would agree today and diverge on the next content change.
        /// </summary>
        [Test]
        public void TheStoredCrouchHeight_IsWhatTheRuleDerivesFromTheStoredHeight()
        {
            string yaml = ReadAsset();

            float height = ReadField(yaml, "height");
            float sitHeight = ReadField(yaml, "sitHeight");

            Assert.AreEqual(height / 2f, sitHeight, 1e-3f,
                "The serialized sitHeight (" + sitHeight.ToString("F3") + ") is not half the serialized " +
                "height (" + height.ToString("F3") + "). littlepip has no sitY attribute, so the " +
                "importer must derive it (Unit.as:1041-1048); a hand-authored value that disagrees with " +
                "the derivation is the drift this guard exists to catch.");
        }

        /// <summary>
        /// Option A made the legacy asset canonical but left the importer's output in the tree. A
        /// later overwrite re-import rewrites 149 generated assets and never touches this fixture's
        /// target, so the two littlepip definitions can diverge again with no red anywhere.
        ///
        /// <para><b>Why the verdict comes from <see cref="BoxesAgree"/>.</b> An earlier form of this
        /// test compared the three fields inline, leaving the shared comparator — and therefore the
        /// absent control below — dead code: a comparator weakened to always return <c>true</c> would
        /// have kept this test green. The comparison the shipping guard makes must be the comparison
        /// the control can break.</para>
        /// </summary>
        [Test]
        public void TheCanonicalAndGeneratedDefinitions_AgreeOnTheBodyBox()
        {
            string canonical = ReadAsset();
            string generated = ReadAssetAt(GeneratedAssetPath);

            Assert.IsTrue(BoxesAgree(canonical, generated),
                "The two littlepip definitions disagree on width/height/sitHeight: canonical " +
                DescribeBox(canonical) + " vs generated " + DescribeBox(generated) + ". The canonical " +
                "asset feeds Player.prefab._stats and this lint; the importer output is what " +
                "PFE/Data/Reimport Units (Overwrite Existing) rewrites. A split here means two " +
                "peers can resolve different body boxes from the same authored unit.");
        }

        /// <summary>
        /// Absent control for the parity guard above: a comparison that always returns true would keep
        /// the new test green forever. Perturb exactly one field in otherwise real asset text and
        /// prove the helper sees it, while an unmodified copy compares equal.
        /// </summary>
        [Test]
        public void ParityGuard_DetectsASingleFieldDivergence()
        {
            string canonical = ReadAsset();

            Assert.IsTrue(BoxesAgree(canonical, canonical),
                "A byte-identical pair must compare equal — otherwise the parity guard is inverted.");

            string divergent = Regex.Replace(canonical, @"^([ \t]*width:[ \t]*)0\.5\b", "${1}0.55",
                RegexOptions.Multiline);
            Assert.That(divergent, Is.Not.EqualTo(canonical),
                "The mutation did not apply; the absent control would be vacuous. If the asset's " +
                "width literal changed, update the pattern rather than deleting this control.");

            Assert.IsFalse(BoxesAgree(canonical, divergent),
                "A one-field body-box divergence must be detected. If this is green, the parity " +
                "guard cannot fail and the 149-asset importer can re-split the definitions silently.");
        }

        /// <summary>
        /// Positive control against <b>real</b> asset text rather than a synthetic mutation: zombie0
        /// differs from littlepip in width only (0.55 vs 0.5). If <see cref="BoxesAgree"/> ever stops
        /// reading the files it is handed — an empty field list, a hardcoded constant, a parser that
        /// always returns the same number — both controls above can still be fooled, but this one
        /// cannot, because the two files genuinely disagree on disk.
        /// </summary>
        [Test]
        public void ParityGuard_RejectsARealOneFieldDivergence()
        {
            string canonical = ReadAsset();
            string decoy = ReadAssetAt(DecoyAssetPath);

            Assert.IsFalse(BoxesAgree(canonical, decoy),
                "A real asset with width 0.55 must not compare equal to littlepip's 0.5. If this is " +
                "green, the comparator is not reading the files and the parity guard is decorative. " +
                "If zombie0's authored sX changed, pick another decoy rather than deleting this.");
        }

        /// <summary>
        /// Row 0.3's last serialized copy: the <c>TilePhysicsController</c> fields and the
        /// <c>BoxCollider2D</c> on <c>Player.prefab</c>. The owner set these in the Inspector at
        /// 2026-11-09 21:12; a plain save cannot do it, because nothing recalculates them.
        ///
        /// <para><b>Why the verdict comes from <see cref="PrefabBoxAgrees"/>.</b> The control below
        /// mutates real prefab text through that same comparator; an inline assertion here would leave
        /// the comparator decorative and the control vacuous.</para>
        /// </summary>
        [Test]
        public void PlayerPrefab_MotorAndColliderCarryTheOracleBodyBox()
        {
            string yaml = ReadAssetAt(PlayerPrefabPath);

            Assert.IsTrue(PrefabBoxAgrees(yaml),
                "Player.prefab no longer carries the oracle's 50 x 70 / 35 body box: " +
                DescribePrefabBox(yaml) + ". The motor's collisionWidth/Height/crouchedCollisionHeight " +
                "are pixels; the BoxCollider2D's m_Size is world units (sX/100, sY/100) with m_Offset.y " +
                "= half the standing height so the body is anchored on the feet. Fix the five values in " +
                "the Inspector — saving the prefab alone re-serializes whatever is already there.");
        }

        /// <summary>
        /// Absent control for the prefab guard: a comparator that always returns true, or one scoped to
        /// the wrong collider, would keep the test above green forever. Perturb each of the five real
        /// literals back to a stale value and prove the guard sees every one of them.
        /// </summary>
        [Test]
        public void PlayerPrefabBodyBoxGuard_DetectsEachStaleNumber()
        {
            string yaml = ReadAssetAt(PlayerPrefabPath);

            Assert.IsTrue(PrefabBoxAgrees(yaml),
                "The unmodified prefab must compare equal — otherwise the guard is inverted.");

            string[,] mutants =
            {
                { "collisionWidth: 50", "collisionWidth: 30" },
                { "collisionHeight: 70", "collisionHeight: 50" },
                { "crouchedCollisionHeight: 35", "crouchedCollisionHeight: 30" },
                { "m_Offset: {x: 0, y: 0.35}", "m_Offset: {x: 0, y: 0}" },
                { "m_Size: {x: 0.5, y: 0.7}", "m_Size: {x: 0.55, y: 0.7}" },
            };

            for (int i = 0; i < mutants.GetLength(0); i++)
            {
                string before = mutants[i, 0];
                string after = mutants[i, 1];

                string mutated = yaml.Replace(before, after);
                Assert.That(mutated, Is.Not.EqualTo(yaml),
                    "The mutation '" + before + "' did not apply; this control would be vacuous. If " +
                    "Unity rewrote the serialized literal, update the pattern rather than deleting it.");

                Assert.IsFalse(PrefabBoxAgrees(mutated),
                    "The prefab guard missed '" + after + "'. If this is green, a future plain save " +
                    "can revert the body box and no offline test will notice.");
            }
        }
    }
}
