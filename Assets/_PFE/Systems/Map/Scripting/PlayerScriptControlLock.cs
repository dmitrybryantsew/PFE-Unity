using System;
using MessagePipe;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Entities.Player;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// Applies the cutscene input lock — the consumer for <see cref="ScriptControlLockMessage"/>.
    ///
    /// <para><b>Why a component rather than another parameter on <c>PlayerController.Construct</c>.</b>
    /// That method is an <c>[Inject]</c> with eleven parameters and it is called directly by the offline
    /// fixtures; every added parameter is a compile break in every one of them, for a behaviour the
    /// player does not own — the <i>script</i> owns it. It is also why this type resolves the player
    /// rather than the other way round: a lock is pushed at whatever the player currently is, so a
    /// respawn that replaces the player cannot leave the new one locked.</para>
    ///
    /// <para><b>The last value is held, not just forwarded.</b> A script can say <c>control off</c>
    /// before the player exists (a room built during world assembly, with the player spawned a frame
    /// later) and <c>control on</c> minutes afterwards. Remembering the latest value and re-applying it
    /// on each successful resolve is what makes the lock survive that ordering instead of being dropped
    /// on the floor — the same reason <c>HudBootstrapper</c> polls for its data sources rather than
    /// binding once.</para>
    /// </summary>
    [LocalOnly]
    public sealed class PlayerScriptControlLock : MonoBehaviour
    {
        private IObjectResolver _resolver;
        private IDisposable _subscription;

        /// <summary>Whether control is currently granted. True is the default, matching
        /// <see cref="PlayerController.ScriptControlEnabled"/>.</summary>
        private bool _enabled = true;

        private PlayerController _player;

        /// <summary>How many lock messages have been received. For tests and the debug overlay.</summary>
        public int MessageCount { get; private set; }

        /// <summary>The last value a script asked for. For tests.</summary>
        public bool RequestedEnabled => _enabled;

        [Inject]
        public void Construct(IObjectResolver resolver, ISubscriber<ScriptControlLockMessage> subscriber)
        {
            _resolver = resolver;

            _subscription?.Dispose();
            _subscription = subscriber?.Subscribe(OnControlLock);
        }

        private void OnControlLock(ScriptControlLockMessage message)
        {
            _enabled = message.Enabled;
            MessageCount++;
            Apply();
        }

        private void Update()
        {
            // Cheap steady state: two reference comparisons and a bool compare once the player is
            // cached and alive. A respawn replaces the component, so the null check is what re-resolves.
            if (_player == null)
            {
                _player = ResolvePlayer();
                if (_player == null) return;
                Apply();
                return;
            }

            if (_player.ScriptControlEnabled != _enabled)
            {
                Apply();
            }
        }

        private void Apply()
        {
            if (_player == null)
            {
                return;
            }

            _player.ScriptControlEnabled = _enabled;

            // ...and the half that is not about input at all. AS3's `controlOff()` is
            // `ctr.clearAll(); ggControl = false; dropTeleObj(); actionObj = null; invulner = true;
            // isRun = false; walk = 0; currentWeapon.vis.visible = false;` (UnitPlayer.as:2037-2049),
            // and `controlOn()` clears `invulner` again (:2053-2065). The port models `ggControl` as
            // ScriptControlEnabled; `invulner` was the piece with no writer, and it is load-bearing:
            // `Unit.findCel`'s player arm refuses an invulnerable player (Unit.as:4658), which is what
            // stops a raider walking up and shooting the player through a cutscene. Without it the
            // surf room's opening dialogue left the player targetable.
            //
            // The other fields `controlOff` writes are deliberately not mirrored here: `ctr.clearAll()`
            // and `isRun`/`walk` are input state the controller already clears when the lock is applied,
            // `dropTeleObj()`/`actionObj` are the telekinesis hold, and the weapon visibility belongs to
            // the weapon presenter.
            _player.RuntimeInvulnerable = !_enabled;
        }

        private PlayerController ResolvePlayer()
        {
            if (_resolver != null && _resolver.TryResolve(out PlayerController resolved) && resolved != null)
            {
                return resolved;
            }

            return FindFirstObjectByType<PlayerController>();
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
            _subscription = null;

            // Never leave a scene object locked after this component is gone — the lock is released by
            // a script command, and a script that is torn down with the object would otherwise strand
            // the player unable to move with nothing left to release it. The invulnerability goes with
            // the lock for the same reason and by the oracle's own pairing: `controlOn()` clears both
            // (`UnitPlayer.as:2053-2065`), so a player left permanently untargetable is the mirror of a
            // player left permanently unable to move.
            if (_player != null)
            {
                _player.ScriptControlEnabled = true;
                _player.RuntimeInvulnerable = false;
            }
        }
    }
}
