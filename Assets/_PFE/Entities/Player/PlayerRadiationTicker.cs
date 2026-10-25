using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Sim.Radiation;
using PFE.Systems.Audio;
using PFE.Systems.Map;
using PFE.Systems.RPG;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Drives the player's radiation block — AS3 <c>UnitPlayer.actions()</c>'s exposure half
    /// (<c>UnitPlayer.as:898-960</c>) — once per authoritative tick, and gathers the sources it reads.
    ///
    /// <para><b>Why this is its own component, like <see cref="PlayerManaTicker"/>.</b> AS3 puts the
    /// block in the player's own frame. The port has no player tick to hang it on:
    /// <see cref="PFE.Entities.Units.UnitController.SimTick"/> returns immediately for a motor-driven
    /// unit, which the player is, and <c>PlayerLocomotionController</c> runs on <c>FixedUpdate</c>,
    /// whose rate is a project setting rather than AS3's 30 Hz. So this is an <see cref="ISimTickable"/>
    /// at <see cref="SimTickOrder.PlayerMotor"/>, registered through <c>AttachSimulation</c>.
    /// An unregistered tickable silently never runs, which presents as "radiation does nothing".</para>
    ///
    /// <para><b>Where the sources come from.</b> The oracle reads three of them per frame and this
    /// class is the port of that read:</para>
    /// <list type="number">
    ///   <item><description><c>loc.rad</c>, and <c>loc.wrad</c> while <c>inWater</c> — the room's own
    ///     level (<c>UnitPlayer.as:906-910</c>).</description></item>
    ///   <item><description>Every radioactive object in the room, via <c>Obj.ggModum</c>
    ///     (<c>Obj.as:231-236</c>): <c>rad</c> strength, <c>radrad</c> radius, <c>radtip</c> type, with
    ///     a linear falloff to the rim.</description></item>
    ///   <item><description>A unit's own <c>radioactiv</c> — the glowing ghoul's aura
    ///     (<c>UnitZombie.as:1130-1146</c>). That one is pushed by the emitting unit rather than
    ///     pulled, because the oracle applies it from the <i>emitter's</i> step; see
    ///     <c>ZombieBrain.ApplyRadiationAura</c>.</description></item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerRadiationTicker : MonoBehaviour, ISimTickable
    {
        private PlayerController _controller;
        private CharacterStats _characterStats;
        private PFE.Entities.Units.UnitStats _unitStats;
        private LandMap _landMap;
        private ISoundService _soundService;

        private SimLoop _loop;
        private bool _registered;
        private bool _warnedLegacy;
        private bool _warnedNoRoom;

        /// <summary>The live exposure state. Never null after <see cref="Construct"/>.</summary>
        public PlayerRadiationState State { get; private set; } = new PlayerRadiationState();

        /// <summary>
        /// The player's own step, like the mana block — radiation is part of <c>UnitPlayer.step</c>,
        /// and the hp clamp it applies must run after the motor has moved the player into (or out of)
        /// a hazard, not before.
        /// </summary>
        public int TickOrder => SimTickOrder.PlayerMotor;

        public void Construct(
            PlayerController controller,
            CharacterStats characterStats,
            PFE.Entities.Units.UnitStats unitStats,
            LandMap landMap,
            ISoundService soundService)
        {
            _controller = controller;
            _characterStats = characterStats;
            _unitStats = unitStats;
            _landMap = landMap;
            _soundService = soundService;
        }

        /// <summary>Puts the radiation block on the sim clock. Safe to call twice.</summary>
        public void Attach(SimClock clock, SimLoop loop)
        {
            if (loop == null)
            {
                return;
            }

            _loop = loop;
            if (!_registered)
            {
                _loop.Register(this);
                _registered = true;
            }
        }

        public void SimTick(int tickIndex)
        {
            Step();
        }

        private void FixedUpdate()
        {
            if (_registered)
            {
                return;
            }

            if (!_warnedLegacy)
            {
                _warnedLegacy = true;
                Debug.LogWarning(
                    "[PlayerRadiationTicker] No SimLoop attached, so the radiation block runs on Unity's " +
                    "FixedUpdate instead of the sim clock. Exposure will accumulate at a rate set by the " +
                    "fixed step rather than AS3's 30 Hz.");
            }

            Step();
        }

        private void OnDestroy()
        {
            if (_loop != null && _registered)
            {
                _loop.Unregister(this);
                _registered = false;
            }
        }

        /// <summary>
        /// One frame: gather, then hand the arithmetic to <see cref="PlayerRadiationState"/>.
        /// </summary>
        private void Step()
        {
            if (_unitStats == null || State == null)
            {
                return;
            }

            // `UnitPlayer.as:1530-1533` — the source-immunity counter runs down every frame whatever
            // else happens.
            State.TickGrace();

            RoomInstance room = _landMap != null ? _landMap.currentRoom : null;
            if (room != null && State.NoGasFrames == 0)
            {
                ScanRadioactiveObjects(room);
            }

            float maxHp = _unitStats.MaxHp.Value;
            float hp = _unitStats.CurrentHp.Value;
            float radX = _characterStats != null ? _characterStats.radX : 1f;
            float radChild = _characterStats != null ? _characterStats.radChild : 0f;

            RadiationMath.TickResult result = State.Tick(
                maxHp,
                hp,
                radX,
                radChild,
                ambientRad: room != null ? room.environment.radiation : 0f,
                ambientWaterRad: room != null ? room.environment.waterRadiation : 0f,
                inWater: _controller != null && _controller.IsInWater,
                invulnerable: _controller != null && _controller.IsInvulnerable,
                // AS3's `World.w.godMode` has no port switch — the dev console has no god verb and no
                // cheat writes it. Passing false is the honest answer rather than inventing a setting.
                godMode: false);

            ApplyHealth(result, hp);

            if (result.GeigerEligible && result.GeigerChance > 0f && Random.value < result.GeigerChance)
            {
                // `sound("geiger")` — a three-clip group in the oracle (Snd.as:293-296), which
                // SoundCatalog models as `geiger` plus `geiger1..3`.
                _soundService?.Play("geiger", transform.position);
            }

            // `this.pet.heal(this.drad / 15, 1)` (:943). The port has no pet system, so this is the one
            // part of the frame that is computed and dropped — recorded here rather than silently
            // omitted, because the value is already correct the day pets land.
            // result.PetHeal

            PayOutElementalDamage();
        }

        /// <summary>
        /// Writes the clamped hp back and raises the ceiling that healing must respect.
        ///
        /// <para><b>Both halves are needed.</b> The clamp itself is <c>UnitPlayer.as:927-931</c>; the
        /// ceiling is the same expression used by <c>heal()</c> (<c>:3157</c>) and regen (<c>:963</c>),
        /// and without it a stimpak would heal straight through the radiation and then be clawed back
        /// on the next frame — visible as a health bar that jumps.</para>
        /// </summary>
        private void ApplyHealth(in RadiationMath.TickResult result, float hpBefore)
        {
            // The ceiling is written before the hp so a listener reacting to the hp change already
            // sees the new limit.
            _unitStats.HpCeiling = RadiationMath.HpCeiling(_unitStats.MaxHp.Value, State.Rad);

            if (result.Hp < hpBefore)
            {
                _unitStats.CurrentHp.Value = result.Hp;
            }

            if (result.Died)
            {
                _controller?.KillFromRadiation();
            }
        }

        /// <summary>
        /// <c>UnitPlayer.as:1529-1551</c> — every 30 frames the elemental accumulators are paid out as
        /// real damage. Routed through the damage pipeline (not <c>UnitStats.Damage</c>) so armour,
        /// vulnerabilities and the death check all apply, exactly like an effect tick.
        /// </summary>
        private void PayOutElementalDamage()
        {
            State.TickElemental(out float venom, out float pink, out float necrotic);

            if (_controller == null)
            {
                return;
            }

            if (venom > 0f) _controller.ApplyRadiationElementalDamage(venom, DamageType.Venom);
            if (pink > 0f) _controller.ApplyRadiationElementalDamage(pink, DamageType.Pink);
            if (necrotic > 0f) _controller.ApplyRadiationElementalDamage(necrotic, DamageType.Necrotic);
        }

        /// <summary>
        /// Walks the current room's objects and applies every radioactive one — the port of
        /// <c>Box.step</c>'s <c>if(radioactiv) { getRasst2(); ggModum(); }</c> (<c>Box.as:548-553</c>).
        ///
        /// <para><b>Per object, per frame, like the oracle.</b> There are 293 radioactive placements
        /// across the 640 rooms (radbarrel 204, radbigbarrel 48, pcloud1 37, tcloud1 3, reactor 1), so a
        /// room holds a handful; the loop is over that room's list, not the world.</para>
        ///
        /// <para><b>Placement overrides the definition for the radius only.</b> AS3 reads
        /// <c>rad</c>/<c>radtip</c> off the definition row and <c>radrad</c> off the placement when it
        /// authors one (<c>Box.as:214-222</c> definition, <c>:385-388</c> placement). Reading a
        /// placement's <c>rad</c> would be a superset the oracle never consults.</para>
        /// </summary>
        private void ScanRadioactiveObjects(RoomInstance room)
        {
            if (room.objects == null)
            {
                return;
            }

            Vector2 playerFeet = (Vector2)transform.position * 100f;
            // `UnitDefinition.Height`, not `PlayerController.Stats` — the latter is the hp holder on
            // this class (it shadows the base property), and the body height lives on the definition.
            float playerHeight = _controller != null && _controller.Definition != null
                ? _controller.Definition.Height * 100f
                : 70f;

            for (int i = 0; i < room.objects.Count; i++)
            {
                ObjectInstance obj = room.objects[i];
                if (obj == null || !obj.isActive || obj.definition == null)
                {
                    continue;
                }

                if (!TryReadFloat(obj.definition.GetAttribute("rad", "0"), out float strength) || strength <= 0f)
                {
                    continue;
                }

                float radius = RadiationMath.DefaultSourceRadius;
                if (TryReadFloat(obj.GetAttribute("radrad", string.Empty), out float authoredRadius))
                {
                    radius = authoredRadius;
                }
                else if (TryReadFloat(obj.definition.GetAttribute("radrad", string.Empty), out float definitionRadius))
                {
                    radius = definitionRadius;
                }

                int tip = 0;
                if (TryReadFloat(obj.definition.GetAttribute("radtip", "0"), out float authoredTip))
                {
                    tip = Mathf.RoundToInt(authoredTip);
                }

                // The object's centre, because `Obj.getRasst2` measures against `Y - scY/2` in Flash
                // coordinates (Y down) — i.e. the middle of the prop, not its feet.
                Vector2 feet = WorldCoordinates.LandToWorld(room.landPosition, obj.position);
                float objectHeight = obj.GetApproximatePixelSize().y;
                float objectCentreY = feet.y + objectHeight * 0.5f;

                float dx = playerFeet.x - feet.x;
                float dy = RadiationMath.VerticalOffset(playerFeet.y, playerHeight, objectCentreY);

                if (!RadiationMath.InRadius(dx, dy, radius))
                {
                    continue;
                }

                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float normalised = RadiationMath.Falloff(distance, radius);
                if (normalised <= 0f)
                {
                    continue;
                }

                if (tip == 0)
                {
                    State.AddSourceExposure(normalised, strength);
                }
                else
                {
                    State.AddSourceElemental(normalised, strength, tip);
                }
            }
        }

        private static bool TryReadFloat(string text, out float value)
        {
            value = 0f;
            return !string.IsNullOrWhiteSpace(text)
                && float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
