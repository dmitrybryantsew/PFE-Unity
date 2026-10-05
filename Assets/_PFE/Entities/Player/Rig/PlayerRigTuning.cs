using System;
using System.Globalization;
using PFE.Character;
using PFE.Entities.Units;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Player.Rig
{
    /// <summary>
    /// The single choke point between a live player's serialized tunables and a
    /// <see cref="PlayerRigSnapshot"/>.
    ///
    /// <para><b>Why the fields are <c>internal</c> rather than exposed through properties.</b> The
    /// rig components live in <c>PFE.Core.asmdef</c> — the same assembly as this class — so
    /// <c>internal</c> is exactly the required scope, and nothing outside the assembly can reach
    /// them. <c>[SerializeField]</c> serialises identically whatever the access modifier and the
    /// YAML key is the field NAME, so widening these to <c>internal</c> left every prefab and
    /// scene byte-for-byte unchanged: no migration, no re-authoring. Access is direct field
    /// access, with no property call in the physics tick.</para>
    ///
    /// <para><b>Why read AND write live in one file.</b> A read path and a write path that are
    /// written separately drift, and a drift between them is invisible — the writer would set a
    /// key the reader never looks at, and the diff would report a phantom gap forever. Keeping
    /// both key lists in one file makes the pair greppable side by side.</para>
    /// </summary>
    public static class PlayerRigTuning
    {
        // ── TilePhysicsController ─────────────────────────────────────────────
        const string TileWidth = PlayerRigSnapshot.TilePhysics + ".collisionWidth";
        const string TileHeight = PlayerRigSnapshot.TilePhysics + ".collisionHeight";
        const string TileCrouchHeight = PlayerRigSnapshot.TilePhysics + ".crouchedCollisionHeight";
        const string TileOffsetX = PlayerRigSnapshot.TilePhysics + ".colliderOffsetPixels.x";
        const string TileOffsetY = PlayerRigSnapshot.TilePhysics + ".colliderOffsetPixels.y";
        const string TileAccel = PlayerRigSnapshot.TilePhysics + ".acceleration";
        const string TileMaxSpeedX = PlayerRigSnapshot.TilePhysics + ".maxSpeedX";
        const string TileMaxSpeedY = PlayerRigSnapshot.TilePhysics + ".maxSpeedY";
        const string TileGroundFriction = PlayerRigSnapshot.TilePhysics + ".groundFriction";
        const string TileAirFriction = PlayerRigSnapshot.TilePhysics + ".airFriction";
        const string TileGravityMult = PlayerRigSnapshot.TilePhysics + ".gravityMult";
        const string TileGlobalGravity = PlayerRigSnapshot.TilePhysics + ".globalGravity";
        const string TilePlatformThreshold = PlayerRigSnapshot.TilePhysics + ".platformThreshold";
        const string TileMaxSubStep = PlayerRigSnapshot.TilePhysics + ".maxSubStepDistance";
        const string TileStepUp = PlayerRigSnapshot.TilePhysics + ".stepUpThreshold";
        const string TileStepUpAir = PlayerRigSnapshot.TilePhysics + ".stepUpThresholdWhileAirborne";
        const string TileDropDuration = PlayerRigSnapshot.TilePhysics + ".platformDropDurationSeconds";
        const string TileLadderClimb = PlayerRigSnapshot.TilePhysics + ".ladderClimbSpeed";
        const string TileLadderProbe = PlayerRigSnapshot.TilePhysics + ".ladderProbeHalfWidth";

        // ── PlayerLocomotionController ────────────────────────────────────────
        const string LocoCanDash = PlayerRigSnapshot.Locomotion + ".canDash";
        const string LocoCanCrouch = PlayerRigSnapshot.Locomotion + ".canCrouch";
        const string LocoDashDuration = PlayerRigSnapshot.Locomotion + ".dashDurationSeconds";
        const string LocoDashCooldown = PlayerRigSnapshot.Locomotion + ".dashCooldownSeconds";
        const string LocoDashSpeedMult = PlayerRigSnapshot.Locomotion + ".dashSpeedMultiplier";
        const string LocoCrouchSpeedMult = PlayerRigSnapshot.Locomotion + ".crouchSpeedMultiplier";
        const string LocoSwimSpeedMult = PlayerRigSnapshot.Locomotion + ".swimSpeedMultiplier";
        const string LocoJumpBuffer = PlayerRigSnapshot.Locomotion + ".jumpBufferSeconds";
        const string LocoCoyoteTime = PlayerRigSnapshot.Locomotion + ".coyoteTimeSeconds";
        const string LocoJumpHoldGravity = PlayerRigSnapshot.Locomotion + ".jumpHoldGravityScale";
        const string LocoJumpCutGravity = PlayerRigSnapshot.Locomotion + ".jumpCutGravityScale";
        const string LocoFallGravity = PlayerRigSnapshot.Locomotion + ".fallGravityScale";
        const string LocoSwimGravity = PlayerRigSnapshot.Locomotion + ".swimGravityScale";

        // ── PlayerLocomotionAbilities ─────────────────────────────────────────
        const string AbilCanDoubleJump = PlayerRigSnapshot.Abilities + ".canDoubleJump";
        const string AbilMaxJumpCount = PlayerRigSnapshot.Abilities + ".maxJumpCount";
        const string AbilJumpForceMult = PlayerRigSnapshot.Abilities + ".jumpForceMultiplier";
        const string AbilAirJumpRatio = PlayerRigSnapshot.Abilities + ".airJumpForceRatio";
        const string AbilInfiniteMana = PlayerRigSnapshot.Abilities + ".infiniteMana";
        const string AbilCanLevitate = PlayerRigSnapshot.Abilities + ".canLevitate";
        const string AbilLevitationMaxHeight = PlayerRigSnapshot.Abilities + ".levitationMaxHeight";
        const string AbilLevitationAccel = PlayerRigSnapshot.Abilities + ".levitationAcceleration";
        const string AbilLevitationManaTick = PlayerRigSnapshot.Abilities + ".levitationManaCostPerTick";
        const string AbilLevitationManaUp = PlayerRigSnapshot.Abilities + ".levitationManaCostUpward";
        const string AbilCanAirDash = PlayerRigSnapshot.Abilities + ".canAirDash";
        const string AbilCanWallJump = PlayerRigSnapshot.Abilities + ".canWallJump";
        const string AbilMoveSpeedMult = PlayerRigSnapshot.Abilities + ".moveSpeedMultiplier";
        const string AbilCanTeleport = PlayerRigSnapshot.Abilities + ".canTeleport";
        const string AbilTeleportCharge = PlayerRigSnapshot.Abilities + ".teleportChargeTimeSeconds";
        const string AbilTeleportManaCost = PlayerRigSnapshot.Abilities + ".teleportManaCost";
        const string AbilTeleportCooldown = PlayerRigSnapshot.Abilities + ".teleportCooldownSeconds";

        // ── WeaponMounts (presence only — a Transform reference is instance-specific) ──
        const string MountWeaponHold = PlayerRigSnapshot.Mounts + ".weaponHold";
        const string MountMagicHold = PlayerRigSnapshot.Mounts + ".magicHold";
        const string MountThrowPoint = PlayerRigSnapshot.Mounts + ".throwPoint";

        // ── Character visual. These exist because a code-built rig got them WRONG in a way that
        //    nothing else could see: the assembler's sorting layer defaults to "Default" (so the
        //    character draws behind the level), and PlayerCharacterVisual carries its OWN
        //    definition/styleData copies, so its Awake logs an error and bails when they are null
        //    even though the assembler beside it is fully configured. Both are silent on screen
        //    until you look, which is exactly what a diff is for. ──────────────────────────────
        const string CharSortingLayer = PlayerRigSnapshot.Character + ".sortingLayer";
        const string CharDefinitionAssigned = PlayerRigSnapshot.Character + ".definitionAssigned";
        const string CharStyleDataAssigned = PlayerRigSnapshot.Character + ".styleDataAssigned";

        /// <summary>
        /// Reads a live player's tunables. A missing component is not an error: a partial player
        /// yields a partial snapshot, and <see cref="PlayerRigSnapshot.Diff"/> reports the
        /// resulting absences, which is exactly the finding wanted.
        /// </summary>
        public static PlayerRigSnapshot Read(GameObject player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));

            var s = new PlayerRigSnapshot();

            var tile = player.GetComponent<TilePhysicsController>();
            if (tile != null)
            {
                s.Set(TileWidth, tile.collisionWidth);
                s.Set(TileHeight, tile.collisionHeight);
                s.Set(TileCrouchHeight, tile.crouchedCollisionHeight);
                s.Set(TileOffsetX, tile.colliderOffsetPixels.x);
                s.Set(TileOffsetY, tile.colliderOffsetPixels.y);
                s.Set(TileAccel, tile.acceleration);
                s.Set(TileMaxSpeedX, tile.maxSpeedX);
                s.Set(TileMaxSpeedY, tile.maxSpeedY);
                s.Set(TileGroundFriction, tile.groundFriction);
                s.Set(TileAirFriction, tile.airFriction);
                s.Set(TileGravityMult, tile.gravityMult);
                s.Set(TileGlobalGravity, tile.globalGravity);
                s.Set(TilePlatformThreshold, tile.platformThreshold);
                s.Set(TileMaxSubStep, tile.maxSubStepDistance);
                s.Set(TileStepUp, tile.stepUpThreshold);
                s.Set(TileStepUpAir, tile.stepUpThresholdWhileAirborne);
                s.Set(TileDropDuration, tile.platformDropDurationSeconds);
                s.Set(TileLadderClimb, tile.ladderClimbSpeed);
                s.Set(TileLadderProbe, tile.ladderProbeHalfWidth);
            }

            var loco = player.GetComponent<PlayerLocomotionController>();
            if (loco != null)
            {
                s.Set(LocoCanDash, loco.canDash);
                s.Set(LocoCanCrouch, loco.canCrouch);
                s.Set(LocoDashDuration, loco.dashDurationSeconds);
                s.Set(LocoDashCooldown, loco.dashCooldownSeconds);
                s.Set(LocoDashSpeedMult, loco.dashSpeedMultiplier);
                s.Set(LocoCrouchSpeedMult, loco.crouchSpeedMultiplier);
                s.Set(LocoSwimSpeedMult, loco.swimSpeedMultiplier);
                s.Set(LocoJumpBuffer, loco.jumpBufferSeconds);
                s.Set(LocoCoyoteTime, loco.coyoteTimeSeconds);
                s.Set(LocoJumpHoldGravity, loco.jumpHoldGravityScale);
                s.Set(LocoJumpCutGravity, loco.jumpCutGravityScale);
                s.Set(LocoFallGravity, loco.fallGravityScale);
                s.Set(LocoSwimGravity, loco.swimGravityScale);
            }

            var abil = player.GetComponent<PlayerLocomotionAbilities>();
            if (abil != null)
            {
                s.Set(AbilCanDoubleJump, abil.canDoubleJump);
                s.Set(AbilMaxJumpCount, abil.maxJumpCount);
                s.Set(AbilJumpForceMult, abil.jumpForceMultiplier);
                s.Set(AbilAirJumpRatio, abil.airJumpForceRatio);
                s.Set(AbilInfiniteMana, abil.infiniteMana);
                s.Set(AbilCanLevitate, abil.canLevitate);
                s.Set(AbilLevitationMaxHeight, abil.levitationMaxHeight);
                s.Set(AbilLevitationAccel, abil.levitationAcceleration);
                s.Set(AbilLevitationManaTick, abil.levitationManaCostPerTick);
                s.Set(AbilLevitationManaUp, abil.levitationManaCostUpward);
                s.Set(AbilCanAirDash, abil.canAirDash);
                s.Set(AbilCanWallJump, abil.canWallJump);
                s.Set(AbilMoveSpeedMult, abil.moveSpeedMultiplier);
                s.Set(AbilCanTeleport, abil.canTeleport);
                s.Set(AbilTeleportCharge, abil.teleportChargeTimeSeconds);
                s.Set(AbilTeleportManaCost, abil.teleportManaCost);
                s.Set(AbilTeleportCooldown, abil.teleportCooldownSeconds);
            }

            var mounts = player.GetComponent<WeaponMounts>();
            if (mounts != null)
            {
                s.Set(MountWeaponHold, mounts.HasWeaponHoldPoint);
                s.Set(MountMagicHold, mounts.HasMagicHoldPoint);
                s.Set(MountThrowPoint, mounts.HasThrowPoint);
            }

            var assembler = player.GetComponent<CharacterSpriteAssembler>();
            if (assembler != null)
            {
                s.Set(CharSortingLayer, assembler.SortingLayerName);
            }

            var visual = player.GetComponent<PlayerCharacterVisual>();
            if (visual != null)
            {
                s.Set(CharDefinitionAssigned, visual._definition != null);
                s.Set(CharStyleDataAssigned, visual._styleData != null);
            }

            return s;
        }

        /// <summary>
        /// Applies a snapshot onto a live player. <b>Partial snapshots are legal and keys that are
        /// absent are left alone</b> — that is what lets the rig be built with only the deltas that
        /// matter, rather than requiring a full 52-key transcription before anything runs.
        /// </summary>
        public static void Apply(GameObject player, PlayerRigSnapshot tuning)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (tuning == null) throw new ArgumentNullException(nameof(tuning));

            var tile = player.GetComponent<TilePhysicsController>();
            if (tile != null)
            {
                ApplyFloat(tuning, TileWidth, ref tile.collisionWidth);
                ApplyFloat(tuning, TileHeight, ref tile.collisionHeight);
                ApplyFloat(tuning, TileCrouchHeight, ref tile.crouchedCollisionHeight);
                ApplyFloat(tuning, TileOffsetX, ref tile.colliderOffsetPixels.x);
                ApplyFloat(tuning, TileOffsetY, ref tile.colliderOffsetPixels.y);
                ApplyFloat(tuning, TileAccel, ref tile.acceleration);
                ApplyFloat(tuning, TileMaxSpeedX, ref tile.maxSpeedX);
                ApplyFloat(tuning, TileMaxSpeedY, ref tile.maxSpeedY);
                ApplyFloat(tuning, TileGroundFriction, ref tile.groundFriction);
                ApplyFloat(tuning, TileAirFriction, ref tile.airFriction);
                ApplyFloat(tuning, TileGravityMult, ref tile.gravityMult);
                ApplyFloat(tuning, TileGlobalGravity, ref tile.globalGravity);
                ApplyFloat(tuning, TilePlatformThreshold, ref tile.platformThreshold);
                ApplyFloat(tuning, TileMaxSubStep, ref tile.maxSubStepDistance);
                ApplyFloat(tuning, TileStepUp, ref tile.stepUpThreshold);
                ApplyFloat(tuning, TileStepUpAir, ref tile.stepUpThresholdWhileAirborne);
                ApplyFloat(tuning, TileDropDuration, ref tile.platformDropDurationSeconds);
                ApplyFloat(tuning, TileLadderClimb, ref tile.ladderClimbSpeed);
                ApplyFloat(tuning, TileLadderProbe, ref tile.ladderProbeHalfWidth);
            }

            var loco = player.GetComponent<PlayerLocomotionController>();
            if (loco != null)
            {
                ApplyBool(tuning, LocoCanDash, ref loco.canDash);
                ApplyBool(tuning, LocoCanCrouch, ref loco.canCrouch);
                ApplyFloat(tuning, LocoDashDuration, ref loco.dashDurationSeconds);
                ApplyFloat(tuning, LocoDashCooldown, ref loco.dashCooldownSeconds);
                ApplyFloat(tuning, LocoDashSpeedMult, ref loco.dashSpeedMultiplier);
                ApplyFloat(tuning, LocoCrouchSpeedMult, ref loco.crouchSpeedMultiplier);
                ApplyFloat(tuning, LocoSwimSpeedMult, ref loco.swimSpeedMultiplier);
                ApplyFloat(tuning, LocoJumpBuffer, ref loco.jumpBufferSeconds);
                ApplyFloat(tuning, LocoCoyoteTime, ref loco.coyoteTimeSeconds);
                ApplyFloat(tuning, LocoJumpHoldGravity, ref loco.jumpHoldGravityScale);
                ApplyFloat(tuning, LocoJumpCutGravity, ref loco.jumpCutGravityScale);
                ApplyFloat(tuning, LocoFallGravity, ref loco.fallGravityScale);
                ApplyFloat(tuning, LocoSwimGravity, ref loco.swimGravityScale);
            }

            var abil = player.GetComponent<PlayerLocomotionAbilities>();
            if (abil != null)
            {
                ApplyBool(tuning, AbilCanDoubleJump, ref abil.canDoubleJump);
                ApplyInt(tuning, AbilMaxJumpCount, ref abil.maxJumpCount);
                ApplyFloat(tuning, AbilJumpForceMult, ref abil.jumpForceMultiplier);
                ApplyFloat(tuning, AbilAirJumpRatio, ref abil.airJumpForceRatio);
                ApplyBool(tuning, AbilInfiniteMana, ref abil.infiniteMana);
                ApplyBool(tuning, AbilCanLevitate, ref abil.canLevitate);
                ApplyFloat(tuning, AbilLevitationMaxHeight, ref abil.levitationMaxHeight);
                ApplyFloat(tuning, AbilLevitationAccel, ref abil.levitationAcceleration);
                ApplyFloat(tuning, AbilLevitationManaTick, ref abil.levitationManaCostPerTick);
                ApplyFloat(tuning, AbilLevitationManaUp, ref abil.levitationManaCostUpward);
                ApplyBool(tuning, AbilCanAirDash, ref abil.canAirDash);
                ApplyBool(tuning, AbilCanWallJump, ref abil.canWallJump);
                ApplyFloat(tuning, AbilMoveSpeedMult, ref abil.moveSpeedMultiplier);
                ApplyBool(tuning, AbilCanTeleport, ref abil.canTeleport);
                ApplyFloat(tuning, AbilTeleportCharge, ref abil.teleportChargeTimeSeconds);
                ApplyFloat(tuning, AbilTeleportManaCost, ref abil.teleportManaCost);
                ApplyFloat(tuning, AbilTeleportCooldown, ref abil.teleportCooldownSeconds);
            }

            // Only the sorting layer is applicable. The two `*Assigned` keys are diagnostics: a
            // snapshot cannot carry an asset reference, so "is it assigned" is a fact to report, not
            // a value to write. The builder is what supplies the assets, before activation.
            var assembler = player.GetComponent<CharacterSpriteAssembler>();
            if (assembler != null)
            {
                string layer;
                if (tuning.TryGet(CharSortingLayer, out layer) && !string.IsNullOrEmpty(layer))
                {
                    assembler.SortingLayerName = layer;
                }
            }
        }

        // ── Typed setters. An absent key is a no-op, and unparsable text is a no-op rather
        //    than a zero — writing 0f because a key was malformed would be a silent physics
        //    change, which is the failure this whole file exists to prevent. ──────────────

        static void ApplyFloat(PlayerRigSnapshot s, string key, ref float target)
        {
            string v;
            float parsed;
            if (s.TryGet(key, out v) &&
                float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                target = parsed;
            }
        }

        static void ApplyBool(PlayerRigSnapshot s, string key, ref bool target)
        {
            string v;
            if (s.TryGet(key, out v))
            {
                if (v == "true") target = true;
                else if (v == "false") target = false;
            }
        }

        static void ApplyInt(PlayerRigSnapshot s, string key, ref int target)
        {
            string v;
            int parsed;
            if (s.TryGet(key, out v) &&
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                target = parsed;
            }
        }
    }
}
