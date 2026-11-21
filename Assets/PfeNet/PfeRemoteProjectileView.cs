using PFE.Data.Definitions;
using PFE.Sim.State;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// One host-replicated transient drawn on a client — the display-only stand-in for
    /// <see cref="PFE.Entities.Weapons.Projectile"/> / <c>ThrownObject</c> / <c>MineObject</c>. It is
    /// the consumer half of slice 3: the host already projects every live transient as a
    /// <see cref="SimProjectileState"/> in <c>SimRoomDelta.Projectiles</c>; this class turns a record
    /// into a sprite and keeps it there until the reconciler drops it.
    ///
    /// <para><b>Display-only, for the same reason the remote body is.</b> The host simulates every
    /// round; a client-side <c>Projectile</c> would integrate its own velocity against the position
    /// the host just replicated, run its own hit sweep and spend damage the host already spent. The
    /// view never touches physics: no rigidbody, no collider, no damage system, no registry id. It
    /// writes the root transform from the record and plays the flight/impact frames.</para>
    ///
    /// <para><b>What it draws, per <see cref="SimTransientKind"/>.</b> A fired round uses the weapon's
    /// <c>projectileVisual</c> with the same <c>default_ballistic</c> fallback
    /// <c>ProjectileFactory.ResolveVisual</c> applies (so a pistol round never renders as nothing);
    /// a thrown object or a mine uses the weapon's own <c>weaponVisual</c> art, exactly the
    /// <c>vis&lt;weaponId&gt;</c> family the spawner hands those entities. Animation cadence is the
    /// visual's frame rate for a round (AS3 <c>vis</c> timeline) and the sim clock's 30 Hz for a
    /// thrown strip, matching <c>ThrownObject.UpdateThrownAnimation</c>.</para>
    ///
    /// <para><b>What it deliberately does not draw.</b> Beam stretch (<c>vis.@spring</c>) — that needs
    /// the live muzzle, which is not on the wire, so a laser draws as its round sprite. Thrown spin
    /// (<c>PhisBullet.dr</c>) — the spin rate is not captured, so a grenade does not tumble. Explosion
    /// particles and decals — impact visuals are host-side truth this build does not replicate. These
    /// are presentation gaps, not desyncs: position, facing and lifetime are the host's.</para>
    ///
    /// <para><b>Built disabled, then activated</b> — the same convention as <see cref="PfeRemoteBody"/>:
    /// every <c>Awake</c> in this repo resolves siblings with one non-retrying <c>GetComponent</c>, so
    /// the renderer child must exist before anything runs.</para>
    /// </summary>
    public sealed class PfeRemoteProjectileView : MonoBehaviour
    {
        private SpriteRenderer _renderer;
        private Sprite[] _frames;
        private float _frameRate;
        private bool _loop;
        private int _flightFrameCount;
        private bool _impacting;
        private uint _lastSequence;
        private float _frameTimer;
        private int _frameIndex;

        // Display-only interpolation buffer (F-12 Wave 2A): same discipline as PfeRemoteBody —
        // samples re-aim the buffer, Update() eases toward the target and owns the transform
        // write every render frame.
        private bool _hasInterpTarget;
        private float _prevPx, _prevPy;
        private float _targetPx, _targetPy;
        private float _interpElapsed;

        /// <summary>How many motion samples this view applied. The play-test readback for "projectile
        /// state reached the wire": a firefight in view must move this on the client.</summary>
        public int SampledStates { get; private set; }

        /// <summary>The record hash this view was built for — the reconciler sweeps by it.</summary>
        public ulong EntityHash { get; private set; }

        /// <summary>
        /// Build one view for <paramref name="kind"/>, drawing <paramref name="weapon"/>'s art (null
        /// weapon: the renderer stays disabled — an honest empty state, never a white box). The view
        /// starts at the record's first position via <see cref="ApplyState"/>; creation only builds.
        /// </summary>
        public static PfeRemoteProjectileView Create(
            ulong entityHash, byte kind, WeaponDefinition weapon, in SimProjectileState firstState)
        {
            var go = new GameObject("PfeRemote" + SimTransientKind.NameOf(kind));
            go.SetActive(false);

            var visualChild = new GameObject("Visual");
            visualChild.transform.SetParent(go.transform, false);
            var renderer = visualChild.AddComponent<SpriteRenderer>();
            renderer.enabled = false;

            var view = go.AddComponent<PfeRemoteProjectileView>();
            view.EntityHash = entityHash;
            view.Bind(renderer, kind, weapon);
            view.ApplyState(in firstState);

            go.SetActive(true);
            return view;
        }

        /// <summary>Normalize the two art families into the frame strip this view plays.</summary>
        private void Bind(SpriteRenderer renderer, byte kind, WeaponDefinition weapon)
        {
            _renderer = renderer;

            if (kind == SimTransientKind.Projectile)
            {
                // The round family: visbul art. Same fallback as ProjectileFactory.ResolveVisual — a
                // weapon with no vbul fires the oracle's visualBullet, and a remote viewer must agree.
                ProjectileVisualDefinition visual = ProjectileVisualDefaults.Resolve(
                    weapon != null ? weapon.projectileVisual : null, LoadDefaultVisual());
                if (visual == null || !visual.HasFrames)
                {
                    // No art at all (definition missing or import gap): draw nothing, track everything.
                    _frames = null;
                    return;
                }

                _frames = visual.frames;
                _frameRate = visual.frameRate;
                _loop = visual.loop;
                _flightFrameCount = visual.flightFrameCount;

                renderer.enabled = true;
                renderer.sprite = visual.FirstFrame;
                renderer.color = visual.colorTint;
                renderer.sortingOrder = visual.sortingOrder;
                visualChildTransform.localPosition = visual.localOffset;
                visualChildTransform.localRotation = Quaternion.Euler(0f, 0f, visual.localRotation);
                visualChildTransform.localScale = visual.localScale;
            }
            else
            {
                // Thrown object / mine: the weapon's own vis<weaponId> art (WThrow.as:38 vBullet =
                // vWeapon). The whole strip loops at the sim cadence, exactly ThrownObject's update.
                WeaponVisualDefinition visual = weapon != null ? weapon.weaponVisual : null;
                if (visual == null || !visual.HasFrames)
                {
                    _frames = null;
                    return;
                }

                _frames = visual.frames;
                _frameRate = PFE.Core.SimClock.FramesPerSecond;
                _loop = true;
                _flightFrameCount = 0;

                renderer.enabled = true;
                renderer.sprite = visual.frames[0];
            }
        }

        private Transform visualChildTransform => _renderer != null ? _renderer.transform : null;

        /// <summary>The oracle's <c>visualBullet</c>, loaded once for every view that needs it.</summary>
        private static ProjectileVisualDefinition _defaultVisual;
        private static bool _defaultVisualLoaded;

        private static ProjectileVisualDefinition LoadDefaultVisual()
        {
            if (!_defaultVisualLoaded)
            {
                _defaultVisual = Resources.Load<ProjectileVisualDefinition>(
                    ProjectileVisualDefaults.ResourcePath);
                _defaultVisualLoaded = true;
            }
            return _defaultVisual;
        }

        /// <summary>
        /// Client: feed the view one host motion sample. The position does NOT go to the transform
        /// here — it re-aims the interpolation buffer (<see cref="PushInterpTarget"/>), and the
        /// per-render-frame <see cref="TickVisual"/> owns the single transform write. Samples
        /// arrive on the network's clock; transform writes happen on the display's clock. A sample
        /// that is not newer than the last applied one is refused (out-of-order unreliable
        /// delivery), so a view never steps backward.
        /// </summary>
        public bool ApplyState(in SimProjectileState state)
        {
            if (!PfeRemoteProjectileRules.AcceptsSample(state.Sequence, _lastSequence)) return false;
            _lastSequence = state.Sequence;

            PushInterpTarget(state.Px, state.Py);

            // Facing along travel — the same test Projectile's view pass uses (velocity in px/s here,
            // so the epsilon is a pixel fraction, not the host's Unity-unit one).
            float speedSq = state.Vx * state.Vx + state.Vy * state.Vy;
            if (speedSq > 1f)
            {
                float angle = Mathf.Atan2(state.Vy, state.Vx) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }

            // Entering impact: jump into the impact strip if the art has one, then let Update play it
            // out. The view never despawns itself on the last impact frame — the reconciler owns the
            // lifetime, or a record the host still lists would respawn next snapshot and flicker.
            if (state.IsImpacting && !_impacting)
            {
                _impacting = true;
                if (_frames != null && _flightFrameCount > 0 && _flightFrameCount < _frames.Length)
                {
                    _frameIndex = _flightFrameCount;
                    _renderer.sprite = _frames[_frameIndex];
                }
            }

            SampledStates++;
            return true;
        }

        /// <summary>
        /// Client: re-aim the display interpolation at the newest host sample. Teleport-sized
        /// displacement snaps (a room transition, a fresh muzzle far from the old target); anything
        /// shorter opens a new window from the position the viewer actually sees, so a late sample
        /// eases instead of jumping.
        /// </summary>
        private void PushInterpTarget(float px, float py)
        {
            if (_hasInterpTarget &&
                !PfeRemoteInterpolationRules.ShouldSnap(true, _prevPx, _prevPy, px, py))
            {
                PfeRemoteInterpolationRules.Sample(
                    _prevPx, _prevPy, _targetPx, _targetPy,
                    _interpElapsed, PfeRemoteInterpolationRules.DefaultWindowSeconds,
                    out _prevPx, out _prevPy);
            }
            else
            {
                _prevPx = px;
                _prevPy = py;
            }

            _targetPx = px;
            _targetPy = py;
            _interpElapsed = 0f;
            _hasInterpTarget = true;
        }

        /// <summary>
        /// Client: advance the display interpolation one render frame and write the transform. Runs
        /// before the art guard so a view with unresolved art still moves — its counters say it did.
        /// </summary>
        private void TickVisual(float dt)
        {
            if (!_hasInterpTarget) return;

            _interpElapsed += dt;
            PfeRemoteInterpolationRules.Sample(
                _prevPx, _prevPy, _targetPx, _targetPy,
                _interpElapsed, PfeRemoteInterpolationRules.DefaultWindowSeconds,
                out float px, out float py);
            transform.position = WorldCoordinates.PixelToUnity(new Vector2(px, py));
        }

        private void Update()
        {
            TickVisual(Time.deltaTime);
            if (_renderer == null || _frames == null || _frames.Length < 2 || _frameRate <= 0f) return;

            _frameTimer += Time.deltaTime;
            float frameDuration = 1f / _frameRate;
            int next;
            while (_frameTimer >= frameDuration)
            {
                _frameTimer -= frameDuration;
                if (!PfeRemoteProjectileRules.TryAdvanceFrame(
                        _frames.Length, _flightFrameCount, _loop, _impacting, _frameIndex, out next))
                {
                    break;
                }
                _frameIndex = next;
                _renderer.sprite = _frames[_frameIndex];
            }
        }

        /// <summary>Tear one view down. Called by the reconciler's sweep and by
        /// <see cref="PfeRemoteProjectileDisplay.Clear"/>.</summary>
        public void DestroyView()
        {
            Destroy(gameObject);
        }
    }
}
