using System;
using UnityEngine;
using UnityEngine.UI;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The 15-row live status monitor on the device's right-hand frame — the port of
    /// <c>PipBuck.setRPanel</c> and the <c>ritems</c> construction in the device constructor.
    ///
    /// <para><b>Always visible, on every page.</b> The Oracle builds these rows once in the
    /// <c>PipBuck</c> constructor (<c>PipBuck.as:131-144</c>), as children of the <c>pr</c> clip, and
    /// they are never hidden by a page change — <c>setPage</c> only ever calls <c>setRPanel</c> to
    /// refill them (<c>PipBuck.as:435</c>). So the monitor belongs to the shell, not to a page, which
    /// is why it lives here and not under <c>Pages/</c>.</para>
    ///
    /// <para><b>It refreshes while the world is frozen.</b> The rows report the player's condition,
    /// and the device suspends the simulation — so a monitor that only updated on a sim tick would
    /// freeze at the moment it became visible. The Oracle has the same shape: <c>pip.step()</c> keeps
    /// running under <c>allStat &gt;= 1</c> while the room step does not (<c>World.as:1257-1320</c>).
    /// This view therefore refreshes from <c>LateUpdate</c>, and only while the device is open.</para>
    /// </summary>
    public sealed class PipStatusMonitorView : MonoBehaviour
    {
        private PipBuckCompositionDefinition _composition;
        private IPipStatusSource _source;
        private Font _font;

        private readonly RectTransform[] _rows = new RectTransform[PipBuckLayout.MonitorRowCount];
        private readonly Text[] _texts = new Text[PipBuckLayout.MonitorRowCount];
        private readonly Image[] _icons = new Image[PipBuckLayout.MonitorRowCount];

        private PipBuckViewModel _viewModel;
        private int _lastSignature = int.MinValue;

        /// <summary>Builds the 15 rows under <paramref name="statusPanel"/> (the <c>pr</c> clip).</summary>
        public void Build(RectTransform statusPanel, PipBuckCompositionDefinition composition,
            IPipStatusSource source, Font font, PipBuckViewModel viewModel)
        {
            _composition = composition;
            _source = source;
            _font = font;
            _viewModel = viewModel;

            for (int i = 0; i < PipBuckLayout.MonitorRowCount; i++)
            {
                BuildRow(statusPanel, i);
            }
        }

        private void BuildRow(RectTransform parent, int index)
        {
            // PipBuck.as:135-143 — a fresh visPipRItem per row, at x = 5, y = 40 + i*30, with the
            // child clip `trol` stopped on frame i+1.
            Vector2 flash = PipBuckLayout.MonitorRowPosition(index);

            var rowGo = new GameObject("RItem" + index, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rowRect = (RectTransform)rowGo.transform;
            rowRect.SetParent(parent, false);
            rowRect.anchorMin = rowRect.anchorMax = new Vector2(0f, 1f);
            rowRect.pivot = new Vector2(0f, 1f);
            rowRect.sizeDelta = PipBuckLayout.MonitorRowSize;
            rowRect.anchoredPosition = new Vector2(flash.x, -flash.y);

            var background = rowGo.GetComponent<Image>();
            background.sprite = _composition != null ? _composition.monitorRowTemplate : null;
            background.color = Color.white;
            background.raycastTarget = false;

            // The icon: `trol.gotoAndStop(i + 1)`. The composition stores trol frames 1..15 as a
            // zero-based array, so frame i+1 is index i — off by one here would shift every row's
            // glyph, which is exactly the sort of thing the row ORDER test pins.
            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.SetParent(rowRect, false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(2f, 0f);
            iconRect.sizeDelta = new Vector2(24f, 24f);

            _icons[index] = iconGo.GetComponent<Image>();
            _icons[index].sprite = IconFor(index);
            _icons[index].preserveAspect = true;
            _icons[index].raycastTarget = false;

            var textGo = new GameObject("Value", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rowRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(30f, 0f);
            textRect.offsetMax = new Vector2(-4f, 0f);

            _texts[index] = textGo.GetComponent<Text>();
            _texts[index].font = _font;
            _texts[index].fontSize = 14;
            _texts[index].color = PfeUiColors.PageR0;
            _texts[index].alignment = TextAnchor.MiddleLeft;
            _texts[index].supportRichText = true;   // the threshold colours arrive as <color> tags
            _texts[index].raycastTarget = false;
            _texts[index].text = string.Empty;

            _rows[index] = rowRect;
        }

        private Sprite IconFor(int index)
        {
            if (_composition == null || _composition.monitorIcons == null) return null;
            if (index < 0 || index >= _composition.monitorIcons.Length) return null;
            return _composition.monitorIcons[index];
        }

        private void LateUpdate()
        {
            // Nothing to update while the device is shut, and the source may not resolve until the
            // player exists.
            if (_viewModel == null || !_viewModel.Active.Value) return;
            if (_source == null) return;

            Refresh();
        }

        /// <summary>Re-reads the source and repaints every row.</summary>
        public void Refresh()
        {
            PipStatusRow[] rows = _source.Snapshot();
            if (rows == null || rows.Length != PipBuckLayout.MonitorRowCount)
            {
                // A source returning the wrong length would silently leave rows showing stale numbers.
                // Reported once per call rather than swallowed: the row count is a hard contract with
                // the `trol` icon frames.
                Debug.LogError($"[PipStatusMonitorView] Source returned " +
                               $"{(rows == null ? "null" : rows.Length.ToString())} rows, expected " +
                               $"{PipBuckLayout.MonitorRowCount} (PipBuck.kolRItems).");
                return;
            }

            // Cheap change detection: the monitor repaints at frame rate while the device is open, and
            // building a rich-text string per row per frame is pure garbage for a panel that changes
            // when the player takes damage, which is never while the world is paused.
            int signature = Signature(rows);
            if (signature == _lastSignature) return;
            _lastSignature = signature;

            for (int i = 0; i < rows.Length; i++)
            {
                if (_rows[i] == null) continue;

                bool visible = rows[i].Visible;
                if (_rows[i].gameObject.activeSelf != visible)
                {
                    _rows[i].gameObject.SetActive(visible);
                }

                if (!visible || _texts[i] == null) continue;

                _texts[i].text = Label(i) + "  " + rows[i].Format();
            }
        }

        /// <summary>
        /// The Oracle's row label. Not drawn by the Oracle — its rows are icon + number only, because
        /// the icons are unmistakable once you know them. The port draws the label too, because the
        /// imported <c>trol</c> frames are the only thing that distinguishes a head from a torso and
        /// a mis-mapped frame would otherwise be invisible. Set <see cref="ShowLabels"/> false for a
        /// pixel-faithful panel.
        /// </summary>
        public static bool ShowLabels = true;

        private static string Label(int index)
        {
            if (!ShowLabels) return string.Empty;
            return index < PipStatusRows.Labels.Length ? PipStatusRows.Labels[index] : "?";
        }

        private static int Signature(PipStatusRow[] rows)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < rows.Length; i++)
                {
                    hash = hash * 31 + (int)rows[i].Kind;
                    hash = hash * 31 + Mathf.RoundToInt(rows[i].Value * 10f);
                    hash = hash * 31 + Mathf.RoundToInt(rows[i].Max * 10f);
                }

                return hash;
            }
        }
    }
}
