using PFE.Character.Animation;
using PFE.Data.Definitions;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Magic;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Draws a unit's spell/boss shield dome — the port of the oracle's <c>visShit</c> sibling clip.
    ///
    /// <para><b>The oracle.</b> <c>UnitAlicorn</c> builds a <c>visShit</c>/<c>visShit2</c> clip in its
    /// constructor (<c>:191-203</c>), adds it to <c>vis</c> as a <i>sibling</i> of the body, and then
    /// drives it from its own frame loop (<c>:328-342</c>):</para>
    ///
    /// <code>
    /// if(this.visshit &amp;&amp; !this.visshit.visible &amp;&amp; shithp &gt; 0) { this.visshit.visible = true;  this.visshit.gotoAndPlay(1); }
    /// if(this.visshit &amp;&amp;  this.visshit.visible &amp;&amp; shithp &lt;= 0) { this.visshit.visible = false; this.visshit.gotoAndStop(1); }
    /// </code>
    ///
    /// <para>Three things about that are load-bearing and are reproduced here:</para>
    /// <list type="number">
    /// <item><description><b>The gate is <c>shithp</c>, polled every frame.</b> Not an effect, not a
    ///     buff, not a cast event — the same plain field the damage resolver spends. That is why this
    ///     component polls <see cref="UnitStats.ShitHp"/> in <c>Update</c> rather than subscribing to
    ///     anything, and it is the same choice <c>PlayerCharacterVisual</c> makes for the player's
    ///     dome.</description></item>
    /// <item><description><b>The clip runs on its own timeline</b> at the SWF's 30 Hz
    ///     (<c>UnitAnimator.FrameRate</c>), independent of the unit's state clip, and
    ///     <c>ClampForever</c> — <c>visShit</c>'s own frame script calls <c>stop()</c> on frame 20, so
    ///     the dome materialises and then holds.</description></item>
    /// <item><description><b>It is positioned and scaled by the unit, not by the art</b>
    ///     (<c>:202-203</c>): <c>visshit.y = -50; scaleX = scaleY = 1.5;</c> — 50 px above the feet at
    ///     1.5×. Those two numbers live on the clip in the definition asset so a tier can override
    ///     them without a code change.</description></item>
    /// </list>
    ///
    /// <para><b>Not ported, and why.</b> The oracle also fires a <c>"pole"</c> particle burst where the
    /// dome was when it drops (<c>:337-341</c>). That needs the particle emitter's per-position emit
    /// surface, which this component does not have; the dome disappearing is the part that carries the
    /// information.</para>
    ///
    /// <para><b>Mirroring.</b> The unit object carries the facing as <c>localScale.x = ±1</c>
    /// (<c>RoomUnitSpawner</c> / <c>UnitController.ApplyFacingToTransform</c>), so a dome parented to it
    /// is mirrored with the body. AS3 flips the body sprite only and leaves <c>visshit</c> unflipped —
    /// but the dome is a symmetric bubble, so the two are the same picture. It would matter for an
    /// asymmetric shield and this comment is where that would be found.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitShieldOverlay : MonoBehaviour
    {
        /// <summary>
        /// The game's frame rate — AS3 <c>World.as:44</c>, <c>30</c>. Shared with
        /// <see cref="UnitAnimator.FrameRate"/> because both are the SWF's own rate rather than anything
        /// the data carries.
        /// </summary>
        public const float FrameRate = UnitAnimator.FrameRate;

        /// <summary>The dome's child object name, so a scene dump is readable.</summary>
        public const string DomeObjectName = "ShieldDome";

        static UnitShieldOverlayDefinition _definition;
        static bool _definitionProbed;
        static bool _missingAssetLogged;
        static bool _missingClipLogged;
        static bool _substitutionLogged;

        SpriteRenderer _renderer;
        UnitStats _stats;
        CharacterOverlayDefinition _clip;
        int _frame;
        bool _visible;
        float _timer;

        /// <summary>
        /// The dome asset, loaded once from <c>Resources</c>.
        ///
        /// <para>Null is a legal answer and means "the import has not been run", which is reported once
        /// rather than on every spawn. <see cref="ResetDefinitionCache"/> exists so a test — or an
        /// owner who has just run the import — does not have to restart the editor to re-probe.</para>
        /// </summary>
        public static UnitShieldOverlayDefinition Definition
        {
            get
            {
                if (!_definitionProbed)
                {
                    _definitionProbed = true;
                    _definition = Resources.Load<UnitShieldOverlayDefinition>(
                        UnitShieldOverlayDefinition.ResourcePath);

                    if (_definition == null && !_missingAssetLogged)
                    {
                        _missingAssetLogged = true;
                        Debug.LogWarning(
                            "[UnitShieldOverlay] No shield-overlay asset at Resources/" +
                            UnitShieldOverlayDefinition.ResourcePath + ", so no unit will draw a shield " +
                            "dome. The shield itself still absorbs damage — only the graphic is " +
                            "missing. Re-run the unit shield overlay import to rebuild it.");
                    }
                }

                return _definition;
            }
        }

        /// <summary>Drop the cached asset so the next read re-probes <c>Resources</c>.</summary>
        public static void ResetDefinitionCache()
        {
            _definition = null;
            _definitionProbed = false;
        }

        /// <summary>Whether this unit currently has a dome on screen.</summary>
        public bool IsShown => _visible;

        /// <summary>The playhead, or <c>-1</c> before <see cref="Initialize"/>.</summary>
        public int CurrentFrame => _frame;

        /// <summary>The clip this unit draws, or <c>null</c> when nothing resolved.</summary>
        public CharacterOverlayDefinition Clip => _clip;

        /// <summary>
        /// Bind this overlay to a unit and build its renderer.
        /// </summary>
        /// <param name="stats">
        /// The unit's stats — the source of <c>ShitHp</c>. Null disables the overlay rather than
        /// throwing: a bare test spawn has no stats, and a missing shield graphic is not a reason to
        /// fail a spawn.
        /// </param>
        /// <param name="tier">The unit's tier — AS3 <c>tr</c>. Only <c>3</c> changes the dome.</param>
        /// <param name="boss">Whether the unit is <c>UnitBossAlicorn</c> (<c>visShit3</c>).</param>
        /// <param name="body">
        /// The unit's body renderer, read for its sorting layer and order so the dome lands directly
        /// above the body. Null is legal and falls back to the map's physical-object layer.
        /// </param>
        /// <returns>True when a dome clip resolved and a renderer was built.</returns>
        public bool Initialize(UnitStats stats, int tier, bool boss, SpriteRenderer body)
        {
            _stats = stats;
            _clip = UnitShieldOverlayRules.Select(Definition, tier, boss);

            if (_clip == null)
            {
                // Nothing to draw. Two different causes, and they need different fixes, so they get
                // different messages: the asset is absent, or the asset is present without this tier's
                // clip and without the ordinary one either.
                if (!_missingClipLogged)
                {
                    _missingClipLogged = true;
                    Debug.LogWarning(
                        "[UnitShieldOverlay] No shield dome clip resolved, so units will not show a " +
                        "dome. Check that " + UnitShieldOverlayDefinition.ResourcePath + " has a " +
                        "'normal' clip with frames (visShit, symbol 3625).");
                }

                enabled = false;
                return false;
            }

            if (!UnitShieldOverlayRules.IsExactMatch(Definition, tier, boss) && !_substitutionLogged)
            {
                _substitutionLogged = true;
                Debug.LogWarning(
                    "[UnitShieldOverlay] The " +
                    UnitShieldOverlayRules.KindFor(tier, boss) +
                    " dome has no imported frames, so units that want it draw the ordinary visShit " +
                    "dome instead. Run 'PFE/Art/Import Unit Shield Overlays' to import it.");
            }

            var domeObject = new GameObject(DomeObjectName);
            domeObject.transform.SetParent(transform, false);
            domeObject.transform.localPosition = new Vector3(_clip.localPosition.x, _clip.localPosition.y, 0f);
            domeObject.transform.localScale =
                Vector3.one * (_clip.localScale > 0f ? _clip.localScale : 1f);

            _renderer = domeObject.AddComponent<SpriteRenderer>();
            _renderer.sortingLayerName = body != null
                ? body.sortingLayerName
                : MapSortingLayers.BackgroundPhysicalObjects;
            // One above the body. AS3 adds the clip to `vis` after the body, so it draws in front of it.
            //
            // NOTE the clip's own `sortingOrder` is deliberately NOT read here, and that is not an
            // oversight: on the player it is 1000, which is a *within-one-composition* rank — the body's
            // parts are drawn at 0..N and the overlay above all of them. A unit's body order is the map
            // depth (`-floor(y / TILE)`), which is negative for most of a room, so adding 1000 would lift
            // every shielded unit's dome above every other unit in the room. The dome belongs one step
            // above its own body and nowhere else.
            _renderer.sortingOrder = (body != null ? body.sortingOrder : 0) + 1;
            _renderer.enabled = false;

            _visible = false;
            _timer = 0f;
            _frame = OverlayClip.RestartFrame(_clip.frames.Length);

            return true;
        }

        /// <summary>
        /// The oracle's two blocks (<c>:328-342</c>), plus the clip's own advance.
        ///
        /// <para>The two edges are the whole gate: <see cref="SpellShield.ShouldShow"/> is the oracle's
        /// "hidden <i>and</i> <c>shithp &gt; 0</c>" and <see cref="SpellShield.ShouldHide"/> is "visible
        /// <i>and</i> <c>shithp &lt;= 0</c>". Both are edge-triggered, which is what makes the rise
        /// restart the clip rather than resume it, and what stops the dome's loop being rewound 60 times
        /// a second.</para>
        /// </summary>
        void Update()
        {
            if (_stats == null || _clip == null) return;

            float shitHp = _stats.ShitHp;

            if (SpellShield.ShouldShow(shitHp, _visible))
            {
                Rise();
            }
            else if (SpellShield.ShouldHide(shitHp, _visible))
            {
                Fall();
            }

            if (!_visible) return;

            _timer += Time.deltaTime;
            float frameDuration = 1f / FrameRate;
            while (_timer >= frameDuration)
            {
                _timer -= frameDuration;
                _frame = OverlayClip.NextFrame(_frame, _clip.frames.Length, _clip.loopMode);
                Draw();
            }
        }

        /// <summary>
        /// AS3 <c>gotoAndPlay(1)</c> — show and restart. Restarting rather than resuming is the oracle's
        /// behaviour and the reason a shield that drops and returns plays its materialise animation
        /// again instead of appearing already at full brightness.
        /// </summary>
        void Rise()
        {
            _visible = true;
            _timer = 0f;
            _frame = OverlayClip.RestartFrame(_clip.frames.Length);
            Draw();
        }

        /// <summary>
        /// AS3 <c>gotoAndStop(1)</c> — hide and park the playhead back on frame 1, so the next rise
        /// starts from the beginning.
        /// </summary>
        void Fall()
        {
            _visible = false;
            _timer = 0f;
            _frame = OverlayClip.RestartFrame(_clip.frames.Length);

            if (_renderer != null) _renderer.enabled = false;
        }

        void Draw()
        {
            if (_renderer == null || _clip == null) return;

            // Both halves matter: a hidden dome and an empty clip must each disable the renderer, or
            // sprite index 0 of a null array is what lands on screen.
            if (!OverlayClip.ShouldDraw(_visible, _clip.frames.Length))
            {
                _renderer.enabled = false;
                return;
            }

            int frame = OverlayClip.ClampFrame(_frame, _clip.frames.Length);
            Sprite sprite = frame >= 0 ? _clip.frames[frame] : null;

            _renderer.sprite = sprite;
            _renderer.enabled = sprite != null;
        }
    }
}
