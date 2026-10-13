using System.Collections.Generic;
using NUnit.Framework;
using PFE.UI.HUD;
using UnityEngine;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins <see cref="HudLayout"/> — the port of <c>GUI.resizeScreen</c>
    /// (<c>…\pfe\scripts\fe\inter\GUI.as:310-349</c>) — and the one thing the offline probe cannot
    /// reach: that <see cref="HudAnchor.Apply"/> produces the placement it claims on a real
    /// <see cref="RectTransform"/>.
    ///
    /// <para><b>Why the table needs pinning at all.</b> Every number in it is a pixel offset on a
    /// 1200 × 800 sheet, and the Oracle's Y grows <i>down</i> while uGUI's grows <i>up</i>. A sign error
    /// does not throw, does not log, and does not look obviously wrong — the bar is simply 20 px on the
    /// wrong side of the screen. There is nothing to notice it, which is exactly the class of defect
    /// this fixture exists to make loud.</para>
    ///
    /// <para><b>What is deliberately NOT asserted.</b> The Oracle never sets the position of
    /// <c>hpBar</c>, <c>manaBar</c>, <c>xpBar</c> or the effect strip — those live in the SWF symbol
    /// <c>visualGUI</c>, which the importer writes out as a flat PNG. Their rows are tagged
    /// <see cref="HudAnchorSource.PortChoice"/> and only their <i>provenance</i> is asserted here, not
    /// their value: a test that pinned an invented number would launder it into evidence.</para>
    /// </summary>
    [TestFixture]
    public class HudLayoutTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _created.Clear();
        }

        private RectTransform MakeRect(string name, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            _created.Add(go);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            return rect;
        }

        // ── The virtual canvas ────────────────────────────────────────────────

        [Test]
        public void ReferenceResolution_IsTheOracles1200x800()
        {
            // GUI.as:158 `screenX = 1200`; GUI.as:160 `screenY = 800`.
            Assert.AreEqual(1200f, HudLayout.ReferenceWidth, "screenX");
            Assert.AreEqual(800f, HudLayout.ReferenceHeight, "screenY");
            Assert.AreEqual(0.5f, HudLayout.MatchWidthOrHeight, 0.0001f,
                "match 0.5 is the only value that does not privilege one axis.");
        }

        // ── Rows taken straight from resizeScreen ─────────────────────────────

        [Test]
        public void WeaponBlock_IsPinnedToTheBottomLeft()
        {
            // GUI.as:314 `vis.textWeapon.y = screenY - 20` -> 20 px up from the bottom edge. The
            // constant part of x is 20 (GUI.as:863 is the dynamic `20 + weapon.textWidth`).
            HudAnchor a = HudLayout.AnchorFor(HudSlot.WeaponBlock);

            Assert.AreEqual(new Vector2(0f, 0f), a.Point, "bottom-left");
            Assert.AreEqual(20f, a.Offset.x, 0.0001f, "x inset");
            Assert.AreEqual(20f, a.Offset.y, 0.0001f,
                "20 px UP from the bottom — the Oracle writes screenY - 20 in a Y-down space.");
        }

        [Test]
        public void ActionPointsBar_IsPinnedToTheBottomRight()
        {
            // GUI.as:316-317 `odBar.x = screenX - 10; odBar.y = screenY - 10`.
            HudAnchor a = HudLayout.AnchorFor(HudSlot.ActionPointsBar);

            Assert.AreEqual(new Vector2(1f, 0f), a.Point, "bottom-right");
            Assert.AreEqual(new Vector2(1f, 0f), a.Pivot, "pinned by its right edge so it drains leftward");
            Assert.AreEqual(-10f, a.Offset.x, 0.0001f);
            Assert.AreEqual(10f, a.Offset.y, 0.0001f, "10 px up, not 10 px down");
        }

        [Test]
        public void SneakEye_HasTheResizeScreenDefault()
        {
            // GUI.as:318-319 `visibility.x = screenX - 10; visibility.y = screenY - 50`. Only the
            // pre-first-frame default: setVisibility re-anchors it to the player's head every frame
            // (GUI.as:981).
            HudAnchor a = HudLayout.AnchorFor(HudSlot.SneakEye);

            Assert.AreEqual(new Vector2(1f, 0f), a.Point);
            Assert.AreEqual(-10f, a.Offset.x, 0.0001f);
            Assert.AreEqual(50f, a.Offset.y, 0.0001f);
        }

        [Test]
        public void KeyHint_IsFiftyBelowCentre()
        {
            // GUI.as:347-348 `imp.x = round(screenX / 2); imp.y = round(screenY / 2 - 50)`.
            HudAnchor a = HudLayout.AnchorFor(HudSlot.KeyHint);

            Assert.AreEqual(new Vector2(0.5f, 0.5f), a.Point);
            Assert.AreEqual(HudLayout.KeyHintCentreOffsetY, a.Offset.y, 0.0001f);
            Assert.AreEqual(-50f, a.Offset.y, 0.0001f);
        }

        // ── Rows the Oracle sets in absolute pixels ───────────────────────────

        [Test]
        public void ItemReadout_IsFiftyEightDownFromTheTop()
        {
            // GUI.as:1068,1073 `vis.textItem.y = 58` — absolute, measured DOWN from the top edge.
            HudAnchor a = HudLayout.AnchorFor(HudSlot.ItemReadout);

            Assert.AreEqual(new Vector2(0f, 1f), a.Point, "top-left");
            Assert.AreEqual(-58f, a.Offset.y, 0.0001f,
                "58 down in a Y-down space is -58 in a Y-up, top-anchored one.");
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        [Test]
        public void Apply_PinsTheRectByTheCornerItAnchorsTo()
        {
            RectTransform rect = MakeRect("Weapon Block", new Vector2(220f, 32f));

            HudLayout.Apply(HudSlot.WeaponBlock, rect);

            Assert.AreEqual(new Vector2(0f, 0f), rect.anchorMin);
            Assert.AreEqual(new Vector2(0f, 0f), rect.anchorMax);
            Assert.AreEqual(new Vector2(0f, 0f), rect.pivot);
            Assert.AreEqual(new Vector2(20f, 20f), rect.anchoredPosition);
        }

        [Test]
        public void Apply_DoesNotTouchSizeDelta()
        {
            // A layout pass that resized things would fight whatever built them. The bar's 320x24 is
            // the bootstrapper's; the scaler owns only position.
            RectTransform rect = MakeRect("Health Bar", new Vector2(320f, 24f));

            HudLayout.Apply(HudSlot.HealthBar, rect);

            Assert.AreEqual(new Vector2(320f, 24f), rect.sizeDelta,
                "placement must not resize the element it places.");
        }

        [Test]
        public void Apply_IsIdempotent()
        {
            RectTransform rect = MakeRect("Ammo Counter", new Vector2(220f, 32f));

            HudLayout.Apply(HudSlot.WeaponBlock, rect);
            Vector2 first = rect.anchoredPosition;
            HudLayout.Apply(HudSlot.WeaponBlock, rect);

            Assert.AreEqual(first, rect.anchoredPosition, "re-applying the same anchor must not drift it.");
        }

        [Test]
        public void Apply_NullRect_DoesNotThrow()
        {
            // The scaler can be asked to lay out a rect that was destroyed with its page.
            Assert.DoesNotThrow(() => HudLayout.Apply(HudSlot.HealthBar, null));
        }

        // ── The scaler ────────────────────────────────────────────────────────

        [Test]
        public void Scaler_Register_PlacesImmediatelyAndDoesNotStackDuplicates()
        {
            var scalerGo = new GameObject("HUD Canvas", typeof(RectTransform));
            _created.Add(scalerGo);
            var scaler = scalerGo.AddComponent<HudLayoutScaler>();

            RectTransform first = MakeRect("Ammo Counter", new Vector2(220f, 32f));
            RectTransform second = MakeRect("Ammo Counter (rebuilt)", new Vector2(220f, 32f));

            scaler.Register(HudSlot.WeaponBlock, first);
            Assert.AreEqual(new Vector2(20f, 20f), first.anchoredPosition,
                "Register must place the rect, not merely remember it.");

            scaler.Register(HudSlot.WeaponBlock, second);
            Assert.AreEqual(1, scaler.RegisteredCount, "a rebuilt element must replace, not stack.");
            Assert.AreEqual(new Vector2(20f, 20f), second.anchoredPosition);
        }

        [Test]
        public void Scaler_ApplyAll_RePlacesEveryRegisteredRect()
        {
            // This is the resize path: OnRectTransformDimensionsChange calls ApplyAll, so a rect that
            // something else moved must be put back.
            var scalerGo = new GameObject("HUD Canvas", typeof(RectTransform));
            _created.Add(scalerGo);
            var scaler = scalerGo.AddComponent<HudLayoutScaler>();

            RectTransform health = MakeRect("Health Bar", new Vector2(320f, 24f));
            RectTransform ammo = MakeRect("Ammo Counter", new Vector2(220f, 32f));
            scaler.Register(HudSlot.HealthBar, health);
            scaler.Register(HudSlot.WeaponBlock, ammo);

            health.anchoredPosition = new Vector2(9999f, 9999f);
            ammo.anchorMin = ammo.anchorMax = new Vector2(0.5f, 0.5f);

            scaler.ApplyAll();

            Assert.AreEqual(HudLayout.AnchorFor(HudSlot.HealthBar).Offset, health.anchoredPosition);
            Assert.AreEqual(HudLayout.AnchorFor(HudSlot.WeaponBlock).Point, ammo.anchorMin);
        }

        [Test]
        public void Scaler_ApplyAll_SkipsDestroyedRects()
        {
            var scalerGo = new GameObject("HUD Canvas", typeof(RectTransform));
            _created.Add(scalerGo);
            var scaler = scalerGo.AddComponent<HudLayoutScaler>();

            RectTransform doomed = MakeRect("Hold Bar", new Vector2(240f, 14f));
            scaler.Register(HudSlot.HoldBar, doomed);

            Object.DestroyImmediate(doomed.gameObject);

            Assert.DoesNotThrow(() => scaler.ApplyAll(),
                "a scene teardown must not throw out of a layout pass.");
        }

        [Test]
        public void Scaler_Register_NullRect_IsIgnored()
        {
            var scalerGo = new GameObject("HUD Canvas", typeof(RectTransform));
            _created.Add(scalerGo);
            var scaler = scalerGo.AddComponent<HudLayoutScaler>();

            Assert.DoesNotThrow(() => scaler.Register(HudSlot.HealthBar, null));
            Assert.AreEqual(0, scaler.RegisteredCount);
        }

        // ── The pure resizeScreen helpers ─────────────────────────────────────

        [Test]
        public void ToastWidth_ClampsAtFiveHundred()
        {
            // GUI.as:325-335: width = screenX - 550 when that exceeds 500, else 500.
            Assert.AreEqual(650f, HudLayout.ToastWidth(1200f), 0.0001f);
            Assert.AreEqual(500f, HudLayout.ToastWidth(1050f), 0.0001f, "500 is not > 500");
            Assert.AreEqual(500f, HudLayout.ToastWidth(800f), 0.0001f);
            Assert.AreEqual(1370f, HudLayout.ToastWidth(1920f), 0.0001f);
        }

        [Test]
        public void DialogueScale_ShrinksBelow1200AndNeverGrows()
        {
            // GUI.as:336-345.
            Assert.AreEqual(1000f / 1200f, HudLayout.DialogueScale(1000f), 0.0001f);
            Assert.AreEqual(1f, HudLayout.DialogueScale(1200f), 0.0001f);
            Assert.AreEqual(1f, HudLayout.DialogueScale(2560f), 0.0001f);
        }

        [Test]
        public void SatsScale_IsScreenDividedByOneHundred()
        {
            // GUI.as:322-323.
            Vector2 scale = HudLayout.SatsScale(1200f, 800f);
            Assert.AreEqual(12f, scale.x, 0.0001f);
            Assert.AreEqual(8f, scale.y, 0.0001f);
        }

        // ── setOtstup ─────────────────────────────────────────────────────────

        [Test]
        public void SetOtstup_ShiftsOnlyTheThreeElementsItTouches()
        {
            // GUI.as:1067-1073 moves textItem, vItem and (via 1077/1081) textMana. Everything else in
            // the HUD must be unmoved, or a companion would drag the health bar down the screen.
            float itemNoPet = HudLayout.AnchorFor(HudSlot.ItemReadout, false, true).Offset.y;
            float itemPet = HudLayout.AnchorFor(HudSlot.ItemReadout, true, true).Offset.y;
            Assert.AreEqual(-58f, itemNoPet, 0.0001f);
            Assert.AreEqual(-88f, itemPet, 0.0001f, "58 + 30");

            float healthNoPet = HudLayout.AnchorFor(HudSlot.HealthBar, false, true).Offset.y;
            float healthPet = HudLayout.AnchorFor(HudSlot.HealthBar, true, true).Offset.y;
            Assert.AreEqual(healthNoPet, healthPet, 0.0001f, "the health bar is not in setOtstup");
        }

        [Test]
        public void SetOtstup_CollapsesTheManaLineOntoTheItemRowWhenTheIconIsHidden()
        {
            // GUI.as:1077 `textMana.y = textItem.y + 35` when the item icon shows;
            // GUI.as:1081 `textMana.y = textItem.y` when it does not.
            float iconOn = HudLayout.AnchorFor(HudSlot.ManaText, false, true).Offset.y;
            float iconOff = HudLayout.AnchorFor(HudSlot.ManaText, false, false).Offset.y;

            Assert.AreEqual(-93f, iconOn, 0.0001f, "58 + 35");
            Assert.AreEqual(-58f, iconOff, 0.0001f, "back on the item readout's own row");
        }

        // ── Provenance ────────────────────────────────────────────────────────

        [Test]
        public void EveryHudSlot_HasATableRow()
        {
            // Without a row the scaler silently falls back to "centred", and nothing reports it.
            foreach (HudSlot slot in System.Enum.GetValues(typeof(HudSlot)))
            {
                Assert.IsTrue(HudLayout.HasAnchor(slot), $"no anchor row for {slot}");
            }
        }

        [Test]
        public void ElementsTheOracleNeverPlaces_AreTaggedAsPortChoices()
        {
            // The anti-laundering test: an invented position must never be quoted as a citation.
            Assert.AreEqual(HudAnchorSource.PortChoice, HudLayout.SourceFor(HudSlot.HealthBar));
            Assert.AreEqual(HudAnchorSource.PortChoice, HudLayout.SourceFor(HudSlot.ManaBar));
            Assert.AreEqual(HudAnchorSource.PortChoice, HudLayout.SourceFor(HudSlot.ExperienceBar));
            Assert.AreEqual(HudAnchorSource.PortChoice, HudLayout.SourceFor(HudSlot.StatusEffectsStrip));

            // ...while the rows that ARE read out of resizeScreen stay tagged as such.
            Assert.AreEqual(HudAnchorSource.OracleResizeScreen, HudLayout.SourceFor(HudSlot.WeaponBlock));
            Assert.AreEqual(HudAnchorSource.OracleResizeScreen, HudLayout.SourceFor(HudSlot.ActionPointsBar));
        }
    }
}
