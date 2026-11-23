using System;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Generation;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The local presentation half of the map bridge — everything that turns land/room state into
    /// things on screen, and nothing that decides what the next land or room should be.
    ///
    /// <para><b>Why this exists (Wave 2, thread G).</b> <c>MapBridge</c> used to interleave two jobs:
    /// the authoritative land-transition choreography and a long chain of visual handovers
    /// (damage authority, effect resolver, particle world, combat services, render assets, sim
    /// attach, room Initialize/ClearVisuals, the land <c>fon</c> backdrop plane). The handovers are
    /// pure presentation — they read no campaign state and write no simulation state — so they live
    /// here, behind one object that owns <see cref="RoomVisualController"/> and the render lookups.
    /// <c>MapBridge</c> now asks for "the room shown" (<see cref="InitializeRoom"/>) and "the sky
    /// rebound" (<see cref="RefreshLandFon"/>) without knowing which lookups or sub-systems that
    /// takes, and a headless host can run the transition choreography with this coordinator never
    /// constructed.</para>
    ///
    /// <para><b>Plain class, not a <c>MonoBehaviour</c>.</b> Every operation is synchronous — no
    /// coroutines, no Unity message — and ownership stays with <c>MapBridge</c>, which constructs
    /// the coordinator inside <c>Construct</c> and disposes it in <c>OnDestroy</c>. That keeps the
    /// object out of the scene/DI graph entirely; promoting it to an injected service is thread E's
    /// bundle work, and this shape survives that unchanged.</para>
    ///
    /// <para><b>Null-tolerant by contract.</b> <c>MapBridge</c> warns when no
    /// <see cref="RoomVisualController"/> is assigned and a scene without the particle/effect
    /// registrations still boots; every method here treats a missing collaborator as "that layer
    /// stays off" rather than throwing, mirroring the handover chain it replaced.</para>
    /// </summary>
    public sealed class RoomPresentationCoordinator : IDisposable
    {
        private readonly RoomVisualController _visuals;
        private readonly RoomRenderAssetBundle _renderAssets;
        private readonly PfeDebugSettings _debugSettings;

        /// <summary>
        /// The land's <c>fon</c> backdrop plane — one GameObject parented to the camera, rebuilt (well,
        /// re-bound) on every land transition. See <see cref="RefreshLandFon"/>.
        /// </summary>
        private LandFonRenderer _landFonRenderer;

        public RoomPresentationCoordinator(
            RoomVisualController visuals,
            RoomRenderAssetBundle renderAssets,
            PfeDebugSettings debugSettings)
        {
            _visuals = visuals;
            _renderAssets = renderAssets;
            _debugSettings = debugSettings;
        }

        /// <summary>
        /// The whole visual-service handover chain, moved verbatim from <c>MapBridge.Construct</c>.
        /// Each handover is pushed to <see cref="RoomVisualController"/> exactly as before, in the
        /// same order, with the same null guards — the chain's comments about why each layer exists
        /// live on the corresponding <c>RoomVisualController.Set*</c> method.
        /// </summary>
        /// <param name="damageSystem">The damage authority, so a spawned enemy resolves prop impacts
        /// through armour rather than raw HP.</param>
        /// <param name="registry">The content registry the effect-definition resolver is built from;
        /// null leaves every unit's effect set resolver-less (ids refused, not phantom effects).</param>
        /// <param name="particleWorld">The room-scoped particle population; null draws no particles.</param>
        /// <param name="particleCatalog">The particle art catalogue, paired with
        /// <paramref name="particleWorld"/>.</param>
        /// <param name="particleTileWater">The room-scoped water answer, paired with
        /// <paramref name="particleEmitter"/> so the water answer and the coordinate conversion can
        /// never disagree about which room they are in.</param>
        /// <param name="particleEmitter">The Unity-side emit seam; also returned to the caller, who
        /// keeps it for the prob registry's <c>inter.shine()</c>.</param>
        /// <param name="objectResolver">The container, so a unit built with <c>AddComponent</c> can
        /// still resolve the weapon and audio services; null spawns with no weapon and no voice,
        /// reported per unit.</param>
        /// <param name="simClock">The fixed-step simulation clock, attached to units when the unit-tick
        /// fix is enabled.</param>
        /// <param name="simLoop">The simulation loop the clock drives; registered with the physics
        /// world service by the choreography layer, not here.</param>
        public PFE.Systems.Particles.Adapters.RoomParticleEmitter ConfigureVisualServices(
            PFE.Systems.Combat.DamageSystem damageSystem,
            PFE.Data.ContentRegistry registry,
            PFE.Systems.Particles.ParticleWorld particleWorld,
            PFE.Systems.Particles.Rendering.ParticleSpriteCatalog particleCatalog,
            PFE.Systems.Particles.Adapters.TileQueryParticleWater particleTileWater,
            PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter,
            VContainer.IObjectResolver objectResolver,
            SimClock simClock,
            SimLoop simLoop)
        {
            if (_visuals == null)
            {
                // MapBridge.Start reports the missing assignment; a scene that never reaches Start
                // (a fixture) should not double-report it here.
                return particleEmitter;
            }

            if (_renderAssets != null)
            {
                _visuals.ConfigureRenderAssets(_renderAssets.Materials, _renderAssets.Backgrounds);
            }

            // MapBridge is the injected one; RoomVisualController is a scene component with no [Inject] of
            // its own, and RoomUnitSpawner is a plain class built with `new`. So the damage authority has
            // to be carried down that chain by hand, or every spawned enemy resolves prop impacts as raw
            // HP with no armour.
            _visuals.SetDamageSystem(damageSystem);

            // ...and hand the SAME chain the effect-definition resolver, so a spawned NPC's `effects` array
            // can resolve an id into a real effect rather than refusing every one. Built from the registry
            // the container already owns (rather than a second registration of the resolver itself), which
            // keeps one source of truth — the same shape PlayerWeaponLoadout uses for its ammo resolver.
            if (registry != null)
            {
                _visuals.SetEffectResolver(
                    new PFE.Systems.Effects.ContentRegistryEffectDefinitionResolver(registry));
            }

            // ...and the particle population, down the SAME chain, because AS3 scopes particles to the room
            // (`Emitter.as:362` adds each Part to the Location). This is what gives the room its own particle
            // view: parented to the room root, so a part is drawn in room-local pixels and is occluded by the
            // room's own overlays. Handing it to a unit instead would make muzzle smoke follow the player.
            if (particleWorld != null && particleCatalog != null)
            {
                _visuals.SetParticleWorld(particleWorld, particleCatalog);
            }

            // ...and the two room-scoped adapters, so the controller can point them at whichever room is
            // current. They are handed over as a pair because they share one dependency — the room's tile
            // query — and are pushed it from one place, which is what keeps the water answer and the
            // particle coordinate conversion from disagreeing about which room they are in.
            if (particleTileWater != null && particleEmitter != null)
            {
                _visuals.SetParticleAdapters(particleTileWater, particleEmitter);
            }

            // ...and the container itself, down the SAME chain, so a spawned enemy can resolve the weapon
            // and audio services that [Inject] cannot reach it: a unit is built with AddComponent, which
            // VContainer never observes, so the spawner is the only handover point.
            if (objectResolver != null)
            {
                _visuals.SetCombatServices(objectResolver);
            }

            // P1: hand the fixed-step simulation down the SAME chain, so a motor-less NPC steps on
            // SimLoop's capped clock instead of Unity's uncapped FixedUpdate. Gated on EITHER unit flag,
            // exactly as before: `UnitMotor` needs the clock too, and a motor re-homed without the clock
            // would run on Unity's uncapped FixedUpdate at the legacy 2x scaling.
            bool simDriveUnits = _debugSettings != null
                && (_debugSettings.SimTickUnits || _debugSettings.UnitMotor);

            if (simDriveUnits)
            {
                _visuals.AttachSimulation(simClock, simLoop);

                if (simClock != null)
                {
                    // Deliberately not gated on LogMapBridgeLifecycle: this confirms an opt-in behaviour
                    // change, and silence would be ambiguous with "the flag did nothing".
                    Debug.Log(
                        "[MapBridge] Units are sim-driven at " + simClock.TicksPerSecond +
                        " Hz (unit tick fix ON; units step on SimLoop, capped at " +
                        SimClock.MaxCatchupTicks + " ticks/frame). Driven by: " +
                        (_debugSettings.SimTickUnits ? "SimTickUnits " : "") +
                        (_debugSettings.UnitMotor ? "UnitMotor" : ""));
                }
                else
                {
                    Debug.LogWarning(
                        "[MapBridge] SimTickUnits/UnitMotor is ON but no SimClock was injected, so units " +
                        "stay on Unity's FixedUpdate. The unit tick fix is NOT active.");
                }
            }

            return particleEmitter;
        }

        /// <summary>
        /// Show <paramref name="room"/>: forwards to <c>RoomVisualController.Initialize</c>, whose
        /// first act on an already-initialized controller is <c>ClearVisuals()</c>. The boot-order
        /// invariant — <c>RoomTransitionManager</c> exists BEFORE this runs — is owned by the
        /// choreography layer and pinned by <c>MapBridgeBootOrderLintTests</c>.
        /// </summary>
        public void InitializeRoom(RoomInstance room)
        {
            _visuals?.Initialize(room);
        }

        /// <summary>Teardown of the shown room on a land transition — the same
        /// <c>ClearVisuals</c> the bridge used to call directly.</summary>
        public void ClearRoom()
        {
            _visuals?.ClearVisuals();
        }

        /// <summary>
        /// Point the land's <c>fon</c> plane at <paramref name="land"/>'s backdrop id, creating the plane
        /// on the first land. AS3 counterpart: <c>World.ativateLand</c> (<c>World.as:1058-1070</c>).
        ///
        /// <para><b>Why it is driven from the presentation layer.</b> This is the one place in the port
        /// that holds both the resolved <see cref="LandDefinition"/> and the
        /// <see cref="RoomBackgroundLookup"/> at the moment a land becomes current, which is exactly the
        /// pair <c>drawFon</c> needs. <c>RoomVisualController</c> is handed a <c>RoomInstance</c> and never
        /// sees the land; <c>CampaignManager.CurrentLand</c> is reactive but lives on a container singleton
        /// the scene's render path does not reach.</para>
        ///
        /// <para>Re-binding rather than destroying and recreating is deliberate: two consecutive lands can
        /// share a backdrop id (nine lands use <c>fonWay</c> alone), and a rebuild would blink the sky for
        /// a frame on a transition that changes nothing about it.</para>
        /// </summary>
        public void RefreshLandFon(LandDefinition land)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                // Named rather than skipped. A missing backdrop and a missing camera are the same picture
                // and different bugs, and the camera is found by tag so a scene change is what breaks it.
                Debug.LogWarning(
                    "[MapBridge] No main camera; the land's fon backdrop cannot be drawn and the sky " +
                    "rooms in this land will show the clear colour.");
                return;
            }

            string backdropId = land != null ? land.backdropId : null;

            if (_landFonRenderer == null)
            {
                _landFonRenderer = LandFonRenderer.Create(
                    mainCamera, _renderAssets != null ? _renderAssets.Backgrounds : null, backdropId);
            }
            else
            {
                _landFonRenderer.Bind(
                    mainCamera, _renderAssets != null ? _renderAssets.Backgrounds : null, backdropId);
            }
        }

        /// <summary>
        /// Called by the choreography layer's <c>OnDestroy</c>. The fon plane is parented to the camera,
        /// not to anything the bridge owns, so it is not collected by the scene teardown — and a camera
        /// that survives a scene load would carry the previous land's sky into the next one. Destroyed by
        /// hand for that reason.
        /// </summary>
        public void Dispose()
        {
            if (_landFonRenderer != null)
            {
                UnityEngine.Object.Destroy(_landFonRenderer.gameObject);
                _landFonRenderer = null;
            }
        }
    }
}
