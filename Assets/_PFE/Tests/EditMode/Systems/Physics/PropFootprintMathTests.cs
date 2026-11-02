using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Pins <see cref="PropFootprintMath"/> — AS3 <c>Box.scX</c>/<c>scY</c>
    /// (<c>fe/loc/Box.as:158-171</c>), the numbers a prop's whole physical box is built from.
    ///
    /// <para><b>The report this answers.</b> "Crates have a gap between the sprite of some crates and
    /// the physical object." The port resolved a prop's size as authored <c>scx</c> else
    /// <c>size * 40</c> — a whole number of tiles — while the sprite on top of it is the SWF symbol's
    /// own size. <c>mcrate1</c> is <b>60x60</b> of art inside an <b>80x80</b> box: 10 px of empty
    /// collider on each side, 20 px above the lid. Two crates pushed together therefore stop with a
    /// 20 px strip of floor between their sprites, and a unit standing on one floats 20 px.</para>
    ///
    /// <para><b>Why the art term cannot be folded into the footprint test.</b> <c>size</c>/<c>wid</c>
    /// are tile counts and the art is not: the two disagree in <i>both</i> directions across the
    /// catalog — <c>mcrate5</c> is 100x88 of art in a 120x120 box, while <c>vischeckpoint</c> is
    /// 130x229 of art in an 80x120 one. A rule that only ever shrinks or only ever grows would be
    /// right about half the props and wrong about the rest.</para>
    ///
    /// <para><b>What this fixture cannot reach, and says so.</b> The shipped-data half reads the
    /// <c>.asset</c> files <b>as text</b> and drives the rule with the numbers it finds; it does not
    /// call <c>ObjectInstance.GetApproximatePixelSize</c>, because building a
    /// <see cref="PFE.Data.Definitions.MapObjectDefinition"/> needs
    /// <c>ScriptableObject.CreateInstance</c> and that is an ECall no offline runner can JIT. Nor does
    /// it exercise <c>MapObjectVisualDefinition.ArtBoundsPixels</c>, whose <c>Sprite.vertices</c> read
    /// is native too — so the trimmed-bounds measurement is <b>owner-verified in the editor</b>, while
    /// the rule and the data it is fed are proven here. The same split the loot-fall fixture uses for
    /// <c>chest.asset</c>.</para>
    /// </summary>
    public class PropFootprintMathTests
    {
        const float TileSize = WorldConstants.TILE_SIZE;

        // ── The rule ────────────────────────────────────────────────────────────────────────

        [Test]
        public void AuthoredSize_WinsOverBothArtAndFootprint()
        {
            // Box.as:160-167 — `if(node.@scx.length()) scX = node.@scx`, applied AFTER
            // `scX = vis.width` at :158. An authored value is an override, not a hint.
            Vector2 size = PropFootprintMath.ResolvePixelSize(
                authoredScx: 55f, authoredScy: 33f, artPixels: new Vector2(60f, 60f),
                footprintWidthTiles: 2, footprintHeightTiles: 2);

            Assert.AreEqual(55f, size.x, 1e-3f, "authored scx must beat the 60 px sprite");
            Assert.AreEqual(33f, size.y, 1e-3f, "authored scy must beat the 60 px sprite");
        }

        [Test]
        public void ArtWinsOverFootprint_AndTheCrateIsTheCaseThatMatters()
        {
            // The shipped crate: art 60x60, footprint 2x2 tiles = 80x80.
            Vector2 size = PropFootprintMath.ResolvePixelSize(
                authoredScx: 0f, authoredScy: 0f, artPixels: new Vector2(60f, 60f),
                footprintWidthTiles: 2, footprintHeightTiles: 2);

            Assert.AreEqual(60f, size.x, 1e-3f,
                "60 px of art, not 80 px of footprint — 80 is the 10-px-per-side gap in the report");
            Assert.AreEqual(60f, size.y, 1e-3f,
                "and 20 px less tall than the 80 px footprint, which is the float above the lid");
        }

        [Test]
        public void NoArt_FallsBackToTheFootprintInTiles()
        {
            // A definition with no imported visual (or a caller that has decided the art does not
            // apply — an area trigger). The footprint is then the only number there is.
            Vector2 size = PropFootprintMath.ResolvePixelSize(0f, 0f, Vector2.zero, 3, 2);

            Assert.AreEqual(3f * TileSize, size.x, 1e-3f);
            Assert.AreEqual(2f * TileSize, size.y, 1e-3f);
        }

        [Test]
        public void ZeroFootprint_IsStillOneTileNotNothing()
        {
            // `size` is absent on some rows and reads as 0. A 0x0 box would make every overlap test
            // answer "no contact", i.e. a prop nothing can touch.
            Vector2 size = PropFootprintMath.ResolvePixelSize(0f, 0f, Vector2.zero, 0, 0);

            Assert.AreEqual(TileSize, size.x, 1e-3f);
            Assert.AreEqual(TileSize, size.y, 1e-3f);
        }

        [Test]
        public void AxesAreIndependent()
        {
            // `reactor` is the shipped row with this shape: no scx, scy=220, art 320x720.
            Vector2 size = PropFootprintMath.ResolvePixelSize(
                authoredScx: 0f, authoredScy: 220f, artPixels: new Vector2(320f, 720f),
                footprintWidthTiles: 8, footprintHeightTiles: 18);

            Assert.AreEqual(320f, size.x, 1e-3f, "no authored scx, so the sprite's 320 wins");
            Assert.AreEqual(220f, size.y, 1e-3f, "the authored scy is the override");
        }

        [Test]
        public void NegativeOrUnparseableAuthoredValue_ReadsAsAbsent()
        {
            // The caller turns a failed parse into 0; a negative is the same "not authored" answer.
            Vector2 size = PropFootprintMath.ResolvePixelSize(
                authoredScx: -5f, authoredScy: 0f, artPixels: new Vector2(70f, 70f),
                footprintWidthTiles: 2, footprintHeightTiles: 2);

            Assert.AreEqual(70f, size.x, 1e-3f);
            Assert.AreEqual(70f, size.y, 1e-3f);
        }

        [Test]
        public void TinyArt_IsClampedToTheMinimum()
        {
            // `viswturret` is a 1x1 placeholder frame. A 1 px box is a prop nothing collides with in
            // practice; the floor keeps it a solid speck.
            Vector2 size = PropFootprintMath.ResolvePixelSize(0f, 0f, new Vector2(1f, 1f), 1, 1);

            Assert.AreEqual(PropFootprintMath.MinimumPixelSize, size.x, 1e-3f);
            Assert.AreEqual(PropFootprintMath.MinimumPixelSize, size.y, 1e-3f);
        }

        // ── The shipped data, read off disk ─────────────────────────────────────────────────

        /// <summary>
        /// The reported defect, on the shipped files: <c>mcrate1</c>'s physical box must be its
        /// <b>art</b> (60x60), not its tile footprint (2x2 tiles = 80x80).
        ///
        /// <para>The footprint is asserted alongside the art so the test cannot pass vacuously: if a
        /// future import made the art 80x80, the two numbers would agree and this test would stop
        /// distinguishing the fix from the bug. It fails instead.</para>
        /// </summary>
        [Test]
        public void ShippedCrate1_IsTheSizeOfItsSprite_NotItsFootprint()
        {
            ShippedProp crate = ReadShippedProp("mcrate1");

            Assert.AreEqual(new Vector2(60f, 60f), crate.Art,
                "mcrate1's sprite is 60x60. If the art changed, the arithmetic below no longer " +
                "distinguishes 60 (right) from 80 (the gap).");
            Assert.AreEqual(80f, crate.FootprintWidthPixels, 1e-3f,
                "mcrate1 authors size=2, so the old rule gave 2 * 40 = 80 — the collider the report " +
                "is about");

            Vector2 resolved = PropFootprintMath.ResolvePixelSize(
                crate.AuthoredScx, crate.AuthoredScy, crate.Art,
                crate.SizeTiles, crate.WidTiles);

            Assert.AreEqual(new Vector2(60f, 60f), resolved,
                "the crate's physical box must be its art, 60x60 — not the 80x80 tile footprint");
        }

        /// <summary>
        /// The whole crate family, because the report says "some crates": every one of them has art
        /// that is <i>smaller</i> than its 2-tile (or 3-tile) footprint, and the sizes are all
        /// different — 60, 64, 70, 70, 100x88 — so a single hand-checked number would not have caught
        /// the pattern.
        /// </summary>
        [Test]
        public void ShippedCrateFamily_ResolvesToItsArt()
        {
            string[] family = { "mcrate1", "mcrate2", "mcrate3", "mcrate4", "mcrate5", "woodbox", "box", "safe" };

            var wrong = new List<string>();
            var theOldRuleWouldHaveFailed = new List<string>();

            foreach (string id in family)
            {
                ShippedProp prop = ReadShippedProp(id);

                Vector2 resolved = PropFootprintMath.ResolvePixelSize(
                    prop.AuthoredScx, prop.AuthoredScy, prop.Art, prop.SizeTiles, prop.WidTiles);

                if (resolved != prop.Art) wrong.Add($"{id}: art {prop.Art} but box {resolved}");

                // Positive control: this is the set the PREVIOUS rule got wrong. If it ever empties,
                // the family no longer distinguishes "art" from "footprint" and this test would pass
                // under either rule — which is the vacuous shape worth failing on.
                if (prop.FootprintWidthPixels != prop.Art.x) theOldRuleWouldHaveFailed.Add(id);
            }

            Assert.IsEmpty(wrong, "these crates still do not match their sprite: " + string.Join(", ", wrong));
            Assert.IsNotEmpty(theOldRuleWouldHaveFailed,
                "no crate in the family has art == footprint, so this test can no longer tell the " +
                "fixed rule from the footprint-only one it replaced");
        }

        /// <summary>
        /// A definition that <b>authors</b> <c>scy</c> must still win over the art — the half of
        /// <c>Box.as:160-167</c> the port never read at all, because it only ever looked at the
        /// <i>placement's</i> attributes. <c>reactor</c> authors <c>scy=220</c> against 720 px of art.
        /// </summary>
        [Test]
        public void ShippedReactor_AuthoredScyBeatsTheArt()
        {
            ShippedProp reactor = ReadShippedProp("reactor");

            Assert.AreEqual(220f, reactor.AuthoredScy, 1e-3f,
                "reactor no longer authors scy=220; this test needs a row that does");
            Assert.AreEqual(720f, reactor.Art.y, 1e-3f, "reactor's sprite is 720 px tall");

            Vector2 resolved = PropFootprintMath.ResolvePixelSize(
                reactor.AuthoredScx, reactor.AuthoredScy, reactor.Art, reactor.SizeTiles, reactor.WidTiles);

            Assert.AreEqual(220f, resolved.y, 1e-3f, "the authored scy, not the 720 px sprite");

            // The width is NOT asserted to a discriminating value: reactor's 8-tile footprint is
            // 320 px and its art is 320 px, so the two rules agree here by coincidence. Asserting
            // 320 would pass under either.
            Assert.AreEqual(320f, resolved.x, 1e-3f,
                "no authored scx — 320 comes from the sprite, which happens to equal the footprint");
        }

        /// <summary>
        /// An <c>area</c> trigger is not a <c>Box</c> in AS3 — it is a marker rectangle scaled by its
        /// authored <c>w</c>/<c>h</c>. The data facts that let the caller exclude it, and the footprint
        /// it must therefore keep, are asserted here; the exclusion itself is
        /// <c>ObjectInstance.IsAreaTrigger</c>, which no offline fixture can execute.
        /// </summary>
        [Test]
        public void ShippedAreaTrigger_IsAFamilyTheCallerExcludes()
        {
            ShippedProp area = ReadShippedProp("area");

            Assert.AreEqual(6, area.Family,
                "area.asset is no longer family 6 (AreaTrigger), so the caller's exclusion stops " +
                "applying to it");
            Assert.AreEqual(new Vector2(100f, 100f), area.Art,
                "visArea is a 100x100 marker frame — the number that must NOT become a collider");

            Vector2 withArt = PropFootprintMath.ResolvePixelSize(
                area.AuthoredScx, area.AuthoredScy, area.Art, area.SizeTiles, area.WidTiles);
            Vector2 excluded = PropFootprintMath.ResolvePixelSize(
                area.AuthoredScx, area.AuthoredScy, Vector2.zero, area.SizeTiles, area.WidTiles);

            Assert.AreEqual(new Vector2(100f, 100f), withArt,
                "with the art applied the marker frame would become the box — this is the value the " +
                "caller must avoid");
            Assert.AreEqual(new Vector2(2f * TileSize, 2f * TileSize), excluded,
                "an area keeps its 2x2-tile footprint, exactly as before this change");
        }

        // ── Reading the shipped .asset files ───────────────────────────────────────────────

        /// <summary>One prop's numbers, as the shipped files state them.</summary>
        struct ShippedProp
        {
            public int SizeTiles;
            public int WidTiles;
            public float AuthoredScx;
            public float AuthoredScy;

            /// <summary>
            /// The sprite's <b>frame</b> size, from the visual asset's <c>pixelSize</c>.
            ///
            /// <para>Production reads <c>MapObjectVisualDefinition.ArtBoundsPixels</c> instead — the
            /// <i>drawn</i> bounds, measured from the tight sprite mesh — because the export canvas is
            /// larger than the art for every symbol with empty margins (<c>vishgrate</c>: 120x120 of
            /// canvas around 120x40 of art). For every prop this fixture asserts on, the two numbers
            /// are identical: their PNGs' opaque bounding boxes fill their frames exactly (measured
            /// from the files, e.g. <c>vismcrate1</c> alpha bbox = (0,0,60,60) in a 60x60 frame). That
            /// is what lets this test state the production number without decoding pixels, and it is
            /// also why the padded props are deliberately absent from these assertions.</para>
            /// </summary>
            public Vector2 Art;

            public int Family;

            public float FootprintWidthPixels => Mathf.Max(1, SizeTiles) * TileSize;
        }

        static ShippedProp ReadShippedProp(string objectId)
        {
            string root = FindProjectRoot();
            Assert.IsNotNull(root, "could not locate the project root from " + Directory.GetCurrentDirectory());

            string definitionPath = Path.Combine(
                root, "Assets/_PFE/Data/Resources/MapObjects/Definitions/" + objectId + ".asset");
            Assert.IsTrue(File.Exists(definitionPath), "missing " + definitionPath);

            string[] lines = File.ReadAllLines(definitionPath);

            var legacy = new Dictionary<string, string>(StringComparer.Ordinal);
            string topLevelVisualId = null;
            int sizeTiles = 1;
            int widthTiles = 1;
            int family = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.StartsWith("  - key: ", StringComparison.Ordinal))
                {
                    string key = line.Substring("  - key: ".Length).Trim();
                    if (i + 1 < lines.Length && lines[i + 1].StartsWith("    value: ", StringComparison.Ordinal))
                    {
                        legacy[key] = lines[i + 1].Substring("    value: ".Length).Trim().Trim('"');
                    }

                    continue;
                }

                if (line.StartsWith("  size: ", StringComparison.Ordinal))
                {
                    sizeTiles = ParseInt(line.Substring("  size: ".Length), sizeTiles);
                }
                else if (line.StartsWith("  width: ", StringComparison.Ordinal))
                {
                    widthTiles = ParseInt(line.Substring("  width: ".Length), widthTiles);
                }
                else if (line.StartsWith("  family: ", StringComparison.Ordinal))
                {
                    family = ParseInt(line.Substring("  family: ".Length), family);
                }
                else if (line.StartsWith("  defaultVisualId: ", StringComparison.Ordinal))
                {
                    topLevelVisualId = line.Substring("  defaultVisualId: ".Length).Trim();
                }
            }

            Assert.IsFalse(string.IsNullOrEmpty(topLevelVisualId),
                objectId + ".asset has no defaultVisualId, so there is no sprite to measure");

            string visualPath = Path.Combine(
                root, "Assets/_PFE/Data/Resources/MapObjects/Visuals/" + topLevelVisualId + ".asset");
            Assert.IsTrue(File.Exists(visualPath), "missing " + visualPath);

            string pixelSizeLine = null;
            foreach (string line in File.ReadAllLines(visualPath))
            {
                if (line.StartsWith("  pixelSize: ", StringComparison.Ordinal))
                {
                    pixelSizeLine = line.Substring("  pixelSize: ".Length).Trim();
                    break;
                }
            }

            Assert.IsNotNull(pixelSizeLine, topLevelVisualId + ".asset has no pixelSize");

            var prop = new ShippedProp
            {
                SizeTiles = sizeTiles,
                WidTiles = legacy.TryGetValue("wid", out string wid) ? ParseInt(wid, widthTiles) : widthTiles,
                AuthoredScx = legacy.TryGetValue("scx", out string scx) ? ParseFloat(scx) : 0f,
                AuthoredScy = legacy.TryGetValue("scy", out string scy) ? ParseFloat(scy) : 0f,
                Art = ParsePixelSize(pixelSizeLine),
                Family = family,
            };

            return prop;
        }

        static Vector2 ParsePixelSize(string text)
        {
            // `{x: 60, y: 60}`
            int xAt = text.IndexOf("x:", StringComparison.Ordinal);
            int yAt = text.IndexOf("y:", StringComparison.Ordinal);
            Assert.IsTrue(xAt >= 0 && yAt > xAt, "unexpected pixelSize format: " + text);

            string xText = text.Substring(xAt + 2, text.IndexOf(',', xAt) - (xAt + 2));
            string yText = text.Substring(yAt + 2).TrimEnd(' ', '}');

            return new Vector2(ParseFloat(xText), ParseFloat(yText));
        }

        static float ParseFloat(string text)
            => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : 0f;

        static int ParseInt(string text, int fallback)
            => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;

        /// <summary>
        /// The project root, found from the working directory — <c>Application.dataPath</c> is a Unity
        /// ECall and an offline host cannot JIT a method that mentions it. The wall runs from the repo
        /// root, and so does the editor.
        /// </summary>
        static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 8 && directory != null; i++)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets/_PFE")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
