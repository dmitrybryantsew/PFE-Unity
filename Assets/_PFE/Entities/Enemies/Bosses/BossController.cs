using System;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Security;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    [RequireComponent(typeof(BossBrain))]
    public class BossController : EnemyController
    {
        public const string ControllerId = "UnitBoss";
        public const string ControllerAlias = "boss";

        public BossBrain BossBrain => _brain as BossBrain;

        protected override void Awake()
        {
            base.Awake();
            if (_brain == null)
            {
                _brain = GetComponent<BossBrain>();
            }
        }

        /// <summary>
        /// The HP a boss is left on when it revives into its next stage — AS3
        /// <c>UnitBossAlicorn.die():713</c>'s <c>hp = maxhp - 0.1</c>, which three of the six bosses
        /// share.
        /// </summary>
        /// <remarks>
        /// <b>A virtual rather than a literal at the call site.</b> The value is a <i>rule</i> about the
        /// shield break, so it belongs with the other rules (<c>BossAlicornRules.HpAfterShieldBreak</c>)
        /// where an offline fixture can pin it; leaving the literal inline meant the fixture and the
        /// running code could disagree and nothing would notice.
        /// </remarks>
        protected virtual float HpAfterRevive(float maxHp) => Mathf.Max(1f, maxHp - 0.1f);

        /// <summary>
        /// The boss arm of AS3 <c>Unit.visDetails()</c> (<c>Unit.as:2985-2991</c>): a boss never draws
        /// its own floating <c>hpBar</c> — it pushes <c>hp / maxhp</c> into the GUI's top-centre bar
        /// instead, once per unit frame.
        /// </summary>
        /// <remarks>
        /// <b>Why <c>TickControl</c> and not <c>StepUnit</c>.</b> A spawned boss carries a
        /// <c>TilePhysicsController</c>, so it is stepped by <c>TilePhysicsController.StepMotor</c> and
        /// never reaches <c>StepUnit</c>; <c>TickControl</c> is the seam <i>both</i> drivers call
        /// (<c>TilePhysicsController.cs:957</c> and <c>UnitController.cs:1487</c>), which is where the
        /// oracle puts <c>visDetails()</c>'s counterpart — once per frame, alongside <c>control()</c>.
        /// </remarks>
        public override void TickControl()
        {
            base.TickControl();
            ReportHealthToHud();
        }

        /// <summary>
        /// One boss's <c>hp / maxhp</c> into <see cref="BossHealthBarFeed"/>. A corpse reports
        /// <c>0</c>, which the bar reads as "hide" — the same thing the Oracle's <c>param1 &lt;= 0</c>
        /// arm does, and why a boss that dies does not need a special case here.
        /// </summary>
        private void ReportHealthToHud()
        {
            if (_unitStats == null) return;

            float maxHp = _unitStats.MaxHp.Value;
            if (maxHp <= 0f) return;

            BossHealthBarFeed.Report(_unitStats.CurrentHp.Value / maxHp);
        }

        protected override void OnDeath()
        {
            if (BossBrain != null && BossBrain.TryReviveSecondLife())
            {
                if (_unitStats != null)
                {
                    _unitStats.CurrentHp.Value = HpAfterRevive(_unitStats.MaxHp.Value);
                }
                _brain.SetState(EnemyAIState.CombatChase);
                return;
            }

            // AS3 `die()` hides the bar only on the death that sticks (`:4308-4311`, inside the
            // `if (this.boss)` arm that the revive path never reaches). Placed before the hooks so a
            // subclass overriding OnFinalDeath cannot lose it.
            BossHealthBarFeed.Clear();

            OnFinalDeath();
            base.OnDeath();
        }

        /// <summary>
        /// A boss removed without dying — a room teardown, which is the normal path on every room
        /// change — must not leave its bar on screen. AS3 has the same hole and patches it from the
        /// other side: <c>World.ativateLoc</c> calls <c>gui.hpBarBoss()</c> on every location change
        /// (<c>World.as:1088</c>). Clearing here covers it without the port having to find the
        /// room-activation seam.
        /// </summary>
        protected override void OnDestroy()
        {
            BossHealthBarFeed.Clear();
            base.OnDestroy();
        }

        /// <summary>
        /// Runs on the death that <b>sticks</b> — after <see cref="BossBrain.TryReviveSecondLife"/>
        /// has refused, and before the base death work.
        /// </summary>
        /// <remarks>
        /// <b>Why this is not part of <c>OnDeath</c>.</b> A two-stage boss takes a lethal hit twice,
        /// and AS3 only plays the loot/dissolve on the second: <c>UnitBossAlicorn.die()</c> returns from
        /// the shield arm without ever reaching <c>super.die()</c>, which is what calls
        /// <c>dropLoot()</c> (<c>:734-737</c>). A hook at the top of <c>OnDeath</c> would fire the death
        /// burst and the boss's drop on the dome break, half a fight early.
        /// </remarks>
        protected virtual void OnFinalDeath()
        {
        }
    }

    [RequireComponent(typeof(BossRaiderBrain))]
    public class BossRaiderController : BossController
    {
        public new const string ControllerId = "UnitBossRaider";
        public new const string ControllerAlias = "bossraider";

        /// <summary>
        /// <c>tr == 1</c>'s weapon — <c>Weapon.create(this, "carbine")</c> (<c>UnitBossRaider.as:73</c>).
        ///
        /// <para><b>Not <c>assr</c>, even though the unit row declares one.</b> The row is
        /// <c>&lt;w id='flamer' f='1'/&gt;&lt;w id='assr' f='1'/&gt;</c> (<c>AllData.as:201-202</c>), but the
        /// constructor hard-codes the ids and never consults <c>&lt;w&gt;</c> at all — so the shipped
        /// <c>tr == 1</c> boss carries a <b>carbine</b> and the <c>assr</c> row is dead data. The code wins,
        /// as it does everywhere else in this port.</para>
        /// </summary>
        public const string CarbineWeaponId = "carbine";

        /// <summary><c>tr == 2</c>'s weapon — <c>Weapon.create(this, "flamer")</c> (<c>:78</c>).</summary>
        public const string FlamerWeaponId = "flamer";

        /// <summary>
        /// The weapon this boss holds, or <c>null</c> before the variant is known or the mount failed.
        /// AS3's <c>currentWeapon</c> (<c>:73-80</c>).
        /// </summary>
        public string EquippedWeaponId { get; private set; }

        /// <summary>
        /// The primary mount — AS3 <c>currentWeapon</c> as the brain sees it. <c>EnemyController</c> keeps
        /// mounts in a dictionary keyed by weapon id (<c>GetWeaponMount</c>), so this is the lookup for the
        /// one weapon <see cref="EquippedWeaponId"/> names.
        /// </summary>
        public EnemyWeaponMount WeaponMount =>
            EquippedWeaponId != null ? GetWeaponMount(EquippedWeaponId) : null;

        public int ReinforcementsSpawned { get; private set; }

        /// <summary>
        /// The weapon id a <c>tr</c> value selects — <c>if(this.tr == 2) { … "flamer" } else { …
        /// "carbine" }</c> (<c>UnitBossRaider.as:71-80</c>).
        /// </summary>
        /// <remarks>
        /// <b>A static, so the mapping can be pinned without a scene.</b> It is the exact counterpart of
        /// <see cref="BossRaiderVariants.VisualDefinitionId"/> and must stay in step with it: a variant
        /// that draws the carbine body and fires the flamer is the same class of bug as drawing the
        /// wrong body at all. <see cref="BossRaiderVariants"/> owns the body, this owns the gun, and the
        /// fixture asserts the two agree for every variant.
        /// </remarks>
        public static string WeaponIdFor(int variant) =>
            variant == 2 ? FlamerWeaponId : CarbineWeaponId;

        /// <summary>
        /// Build the mount for the variant's weapon — the constructor half of
        /// <c>UnitBossRaider</c> that the port was missing, which is why the boss had no gun.
        ///
        /// <para><b>Idempotent and late-safe.</b> <c>EquipWeapon</c> returns the existing mount for an id
        /// it has already built, so calling this from <c>OnWeaponServicesReady</c>, from
        /// <c>Initialize</c> and again from <c>OnPlacementApplied</c> (once <c>tr</c> is known) is the
        /// intended sequence: the services arrive before <c>Initialize</c>, and <c>tr</c> arrives after
        /// it.</para>
        /// </summary>
        public void EquipBossWeapon()
        {
            if (Stats == null || !HasWeaponServices) return;

            int variant = BossBrain is BossRaiderBrain raider ? raider.Variant : 1;
            string weaponId = WeaponIdFor(variant);

            if (EquippedWeaponId == weaponId) return;

            EquippedWeaponId = weaponId;
            EquipWeapon(weaponId);
        }

        protected override void OnWeaponServicesReady() => EquipBossWeapon();

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);
            EquipBossWeapon();
        }

        /// <summary>
        /// The definition id this boss is <b>drawn</b> with — <see cref="BossRaiderVariants"/>'s answer
        /// once the placement has been applied, <c>null</c> before.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="UnitController.Definition"/> on purpose: that is the row the unit was
        /// <i>spawned</i> from (<c>bossraider</c>), and for <c>tr == 1</c> it is the wrong body. A reader
        /// that wants "what does this boss look like" has to ask here.
        /// </remarks>
        public string VisualDefinitionId { get; private set; }

        /// <summary>
        /// Draw the body <c>tr</c> selects — the <c>vis = new visualRaiderBoss2()</c> /
        /// <c>vis = new visualRaiderBoss()</c> pair at <c>UnitBossRaider.as:54-61</c>, which the port was
        /// missing: the clip is picked by the constructor in AS3, so <c>bossraider.asset</c>'s single
        /// <c>visualClassName</c> cannot express it and every raider boss was drawn as the flamer.
        ///
        /// <para><b>Called from <c>OnPlacementApplied</c>, not <c>Initialize</c>, and the order is the
        /// whole point.</b> <c>tr</c> lives on the placement (<c>:50-53</c>), which arrives <i>after</i>
        /// <c>Initialize</c>; applying the default here would swap the spawn row's art out and back on
        /// every <c>tr == 2</c> boss. Idempotent, so a second call is free.</para>
        /// </summary>
        public void ApplyVariantVisual()
        {
            if (!(BossBrain is BossRaiderBrain raider)) return;

            int variant = raider.Variant;
            string desiredId = BossRaiderVariants.VisualDefinitionId(variant);

            if (string.Equals(VisualDefinitionId, desiredId, StringComparison.Ordinal)) return;

            // Latched before any early return: a row that is missing must not be re-resolved (and
            // re-warned) on every tick, and the caller has no way to tell "already right" from "failed".
            VisualDefinitionId = desiredId;

            // The spawn row already IS the desired body when `tr == 2`: rooms place `bossraider`, whose
            // `visualClassName` is `visualRaiderBoss2`. Nothing to swap, nothing to warn about.
            if (Definition != null && string.Equals(Definition.id, desiredId, StringComparison.Ordinal))
            {
                return;
            }

            if (!BossRaiderVariants.TryResolveVisual(variant, ResourcesUnitDefinitionProvider.Shared,
                                                    out UnitDefinition variantDefinition))
            {
                // Named, not silent: the boss keeps the row's art, which for `tr == 1` is the WRONG one,
                // and this warning is the only trace of it.
                Debug.LogWarning(
                    $"[BossRaiderController] No UnitDefinition '{desiredId}' for tr={variant} — the " +
                    $"raider boss keeps '{Definition?.id}'s body. Expected a row under " +
                    $"Resources/{ResourcesUnitDefinitionProvider.UnitsResourcesRoot}.");
                return;
            }

            UnitAnimator animator = GetComponentInChildren<UnitAnimator>(true);
            if (animator == null)
            {
                Debug.LogWarning(
                    $"[BossRaiderController] No UnitAnimator under '{name}' — '{desiredId}' cannot be " +
                    $"drawn. RoomUnitSpawner adds one to the Visual child of every unit.");
                return;
            }

            animator.Rebind(variantDefinition);
        }

        /// <summary>
        /// <c>loc.enemySpawn(true, true)</c> (<c>UnitBossRaider.as:238</c>) — one reinforcement from the
        /// room's own <c>tipSpawn</c> group, arriving pointed at the player.
        ///
        /// <para><b>Returns whether a wave actually arrived, and the brain writes <c>called</c> only on
        /// <c>true</c>.</b> AS3 increments <c>called</c> after the call without checking anything —
        /// <c>enemySpawn</c> has no return value — but it also spends a <c>kolEnSpawn</c> point on every
        /// attempt, so an attempt that finds no budget is invisible there. Here
        /// <see cref="SecurityRuntime.SpawnRoomReinforcement"/> returns "a point was spent", and a
        /// refused call must not consume one of the boss's four quarters: otherwise a room with no
        /// <c>enspawn</c> marker would burn the budget on quarter 1 and the boss would never call
        /// again.</para>
        /// </summary>
        public virtual bool SpawnReinforcementWave(int quarter)
        {
            RoomInstance room = Room;
            if (room == null) return false;

            if (!SecurityRuntime.SpawnRoomReinforcement(
                    room,
                    (Vector2)transform.position * TileQueryConstants.UnitToPixel,
                    alarmAtPlayer: true))
            {
                return false;
            }

            ReinforcementsSpawned++;
            return true;
        }

        /// <summary>
        /// <c>UnitBossRaider.dropLoot()</c> (<c>:199-227</c>): hide the held gun's sprite, then drop it as
        /// loot in <b>5–20 % condition</b>.
        ///
        /// <code>
        /// super.dropLoot();
        /// if(currentWeapon) {
        ///    if(currentWeapon.vis) currentWeapon.vis.visible = false;
        ///    id = currentWeapon.id;
        ///    if(currentWeapon.variant &gt; 0) id += "^" + currentWeapon.variant;
        ///    LootGen.lootId(loc, currentWeapon.X, currentWeapon.Y, id, 0);
        /// }
        /// </code>
        ///
        /// <para><b>Ungated, unlike the raider family.</b> <c>UnitBossRaider</c> extends <c>UnitPon</c>,
        /// which has no <c>isDropArm</c>, so the boss always drops — there is no <c>nodrop</c> attribute
        /// and no family that clears it. <c>ArmedShooterController</c>'s override therefore cannot be
        /// reused: its <c>DropsWieldedWeapon</c> guard is the raider family's, and this unit is not in
        /// that family.</para>
        ///
        /// <para><b>No <c>^N</c> variant, and that is a fact about the data.</b> The constructor's
        /// <c>if(currentWeapon.uniq) currentWeapon.updVariant(1)</c> (<c>:93-96</c>) never fires: neither
        /// <c>carbine</c> (<c>AllData.as:2690</c>) nor <c>flamer</c> (<c>:3147</c>) authors a
        /// <c>uniq</c> attribute, so <c>variant</c> stays 0 and <c>DropId</c> returns the bare id. A
        /// <c>HeldWeaponVariant</c> field here would be a number that is always 0.</para>
        ///
        /// <para><b>The three parting shots are not ported, and are unreachable in the oracle too.</b>
        /// <c>if(attackerType == 3)</c> fires three <c>currentWeapon.attack()</c> calls at random
        /// offsets (<c>:217-226</c>), but <c>attackerType</c> is declared <c>= 0</c> (<c>:30</c>) and the
        /// only assignment in the class is <c>attackerType = 0</c> in state 5's throw arm (<c>:553</c>) —
        /// the field is never set to 3 anywhere, unlike <c>UnitRaider.as:267</c> which derives it from
        /// the weapon's <c>tip</c>. So a raider leader never fires from its corpse.</para>
        /// </summary>
        protected override void DropWieldedWeapon(LootContext ctx, float centreY)
        {
            // `if(currentWeapon)` — no weapon id means no gun to hide and nothing to drop.
            if (string.IsNullOrEmpty(EquippedWeaponId)) return;

            WeaponMount?.SetHeldVisualVisible(false);        // `currentWeapon.vis.visible = false`

            // `LootGen.lootId(…, id, 0)` — `kol = 0` is the 5–20 % condition band, not "zero items".
            LootRoller.RollPlacedItem(
                EnemyWeaponDropRules.DropId(EquippedWeaponId, 0),
                ctx,
                kol: EnemyWeaponDropRules.DroppedWeaponKol,
                nx: transform.position.x,
                ny: centreY);
        }
    }

    [RequireComponent(typeof(BossEnclBrain))]
    public class BossEnclController : BossController
    {
        public new const string ControllerId = "UnitBossEncl";
        public new const string ControllerAlias = "bossencl";
    }

    [RequireComponent(typeof(BossNecrBrain))]
    public class BossNecrController : BossController
    {
        public new const string ControllerId = "UnitBossNecr";
        public new const string ControllerAlias = "bossnecr";

        /// <summary>
        /// <c>currentWeapon = Weapon.create(this,"necrbullet")</c> (<c>UnitBossNecr.as:77</c>) — a
        /// constructor literal, so the unit node lists no <c>&lt;w&gt;</c> and
        /// <c>bossnecr.asset</c>'s <c>weapons:</c> is empty by design, not by an import gap.
        /// </summary>
        public const string WeaponId = "necrbullet";

        /// <summary>
        /// <c>hp = maxhp - 0.1</c> (<c>:573</c>), from the rules so the fixture and the running code
        /// cannot disagree.
        /// </summary>
        protected override float HpAfterRevive(float maxHp) => BossNecrRules.HpAfterRevive(maxHp);

        /// <summary>
        /// The boss's ranged threat. Without this the brain's state-2 beat had nothing to fire and the
        /// fight's only damage was a direct hit that ignored walls.
        /// </summary>
        protected override void OnWeaponServicesReady()
        {
            EquipWeapon(WeaponId);
        }

        /// <summary>
        /// <c>die()</c>'s phase-2 arm (<c>UnitBossNecr.as:585-595</c>): <c>for each(_loc2_ in
        /// loc.units) if(_loc2_.mother == this) _loc2_.die();</c> — her summons die with her — plus
        /// <c>dropLoot()</c>'s override (<c>:740-745</c>): <c>newPart("necrblast")</c>,
        /// <c>Snd.ps("unreal")</c>, then <c>super.dropLoot()</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>On the death that sticks, not on the revive.</b> The phase-1 arm returns before that
        /// loop (<c>:571-584</c>), so the trio is still standing when she comes back — which is the
        /// fight's second act. <see cref="BossController.OnFinalDeath"/> is exactly that boundary, and
        /// using it is why this is a controller override rather than a brain hook: the brain cannot
        /// tell a revive from a death, because <see cref="BossBrain.TryReviveSecondLife"/> is what
        /// decides.</para>
        ///
        /// <para><b>Why the sound is here and not in <c>PlayDeathSound</c>.</b> The row carries no
        /// <c>&lt;snd die&gt;</c> — <c>bossnecr.asset</c>'s <c>deathSoundId</c> is empty — because the
        /// oracle does not use that channel: it calls <c>Snd.ps("unreal")</c> straight out of
        /// <c>dropLoot</c>. The id is in <c>SoundCatalog</c>, so this is a real clip and not a silent
        /// call.</para>
        ///
        /// <para><b>The burst is at the body's centre, not her feet.</b> <c>newPart(id)</c> is
        /// <c>Emitter.emit(id, loc, X, Y - scY/2)</c> (<c>Unit.as:2961-2967</c>) — the same offset
        /// <c>BossNecrBrain.EnterBlast</c> uses for <c>necrblast3</c>.</para>
        /// </remarks>
        protected override void OnFinalDeath()
        {
            (BossBrain as BossNecrBrain)?.KillSummons();

            Vector2 feet = (Vector2)transform.position * TileQueryConstants.UnitToPixel;
            float scY = SpriteSizePixels.y;
            if (scY <= 0f && Definition != null)
            {
                scY = Definition.Height * TileQueryConstants.UnitToPixel;
            }

            EmitParticle("necrblast", feet + new Vector2(0f, scY * 0.5f));

            Sound?.Play("unreal", transform.position);
        }
    }

    [RequireComponent(typeof(BossAlicornBrain))]
    public class BossAlicornController : BossController
    {
        public new const string ControllerId = "UnitBossAlicorn";
        public new const string ControllerAlias = "bossalicorn";

        /// <summary>
        /// <b>A one-shot dome, not a shield pool.</b> <c>UnitBossAlicorn</c> looks like the other
        /// shield bosses — it declares <c>shitMaxHp = 400</c> (<c>:32</c>), <c>t_shit = 300</c>
        /// (<c>:105</c>), <c>shitArmor = 15</c> (<c>:119</c>) and creates the <c>visShit3</c> dome
        /// (<c>:123-127</c>) — but <b>nothing in that class ever assigns <c>shithp</c></b>, and
        /// <c>t_shit</c> is only ever decremented (<c>:374-376</c>), never read. Every one of those four
        /// fields is vestigial: <c>Unit.damage()</c> reaches <c>shitArmor</c> only inside
        /// <c>if(shithp &gt; 0)</c>, which never holds, so the boss absorbs nothing.
        ///
        /// <para><b>Its dome is a one-shot, not a pool.</b> The clip is parked on frame 1 in the
        /// constructor (<c>gotoAndStop(1)</c>, <c>:126</c>) — so she arrives <i>with</i> the dome, not
        /// waiting for a pool to fill — and <c>die():709-712</c> is the only other place it is touched:
        /// <c>if(isShit) { isShit = false; visshit.gotoAndPlay(2); hp = maxhp - 0.1; ... }</c>. The
        /// first lethal hit shatters the dome and revives the boss.</para>
        ///
        /// <para><b>So this is <c>true</c>, and <c>HasSpellShieldOverlay</c> stays <c>false</c>.</b>
        /// The two flags answer different questions: "does this unit have a shield pool" (no — nothing
        /// to spend) and "does it draw a dome" (yes — the one-shot). Returning <c>true</c> here attaches
        /// <see cref="PFE.Entities.Units.UnitShieldOverlay"/>, whose driver
        /// <see cref="PFE.Entities.Units.ShieldDomeOneShotRules.IsOneShot"/> switches to the break
        /// timeline for exactly this kind, so the pool poll that would have hidden her dome on frame 1
        /// never runs.</para>
        /// </summary>
        public override bool UsesBossShieldOverlay => true;

        /// <summary>See <see cref="UsesBossShieldOverlay"/> — the boss alicorn has no shield pool.</summary>
        public override bool HasSpellShieldOverlay => false;

        /// <summary>
        /// <c>UnitBossAlicorn.as:230,323-331,489-498</c> — this boss hovers. <c>isFly</c> is written by
        /// the brain (from the tile below its feet after every teleport) and read here, because
        /// <c>UnitController.IsFlying</c> is what the physics step consults to skip gravity.
        /// </summary>
        /// <remarks>
        /// <b>Without this override the boss falls.</b> <c>IsFlying</c> is
        /// <c>protected virtual bool IsFlying =&gt; false</c> on <c>UnitController</c>, so a brain flag
        /// nobody forwards is a flag the motor never sees: the boss would teleport into the air at one
        /// of the seven points and drop to the floor, and the <c>fly</c> row would be drawn by a unit
        /// that is standing on the ground.
        /// </remarks>
        protected override bool IsFlying => BossBrain is BossAlicornBrain alicorn && alicorn.IsFlying;

        /// <summary>
        /// <c>hp = maxhp - 0.1</c> (<c>UnitBossAlicorn.as:713</c>), read from the rules class so the
        /// fixture and the running code cannot disagree.
        /// </summary>
        protected override float HpAfterRevive(float maxHp)
            => Mathf.Max(1f, BossAlicornRules.HpAfterShieldBreak(maxHp));

        /// <summary>
        /// AS3's constructor builds all four weapons unconditionally (<c>:128-130</c>):
        /// <c>weaps = [alilight2, aliblade, alipsy2, alimray]</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Not from the unit asset — and the asset is not wrong.</b> <c>bossalicorn.asset</c>
        /// carries a two-row <c>weapons:</c> list (<c>aliblade</c>, <c>alimray</c>), which is a faithful
        /// import of the node's two <c>&lt;w&gt;</c> children (<c>AllData.as:710-711</c>). But the
        /// <i>loadout</i> is a constructor literal (<c>:128</c>) that the rotation selects from by
        /// <c>attState</c>, so the data table describes what the unit drops, not what it fires. Reading
        /// it here would equip two of four and the <c>alilight2</c>/<c>alipsy2</c> beats would fire
        /// nothing.</para>
        ///
        /// <para><b>All four are equipped and only one fires.</b> The rotation names the active slot
        /// (<c>currentWeapon</c>) and the brain calls <c>FireOnce</c> on that mount only; the other
        /// three still step, because AS3's <c>childObjs</c> are all live and a weapon that is merely
        /// not selected has no reason to freeze.</para>
        ///
        /// <para><b>Every one of the four is an AUTOMATIC weapon, and that is the difference between a
        /// fight and a single shot.</b> <c>Weapon</c>'s constructor ends with
        /// <c>if(!param1.player) { this.auto = true; }</c> (<c>Weapon.as:337-340</c>) — an NPC's weapon
        /// never takes the semi-auto debounce at all. The port models that as
        /// <c>WeaponRuntimeState.AutomaticTrigger</c>, and without it
        /// <c>RangedWeaponController.RunAttack</c> takes the debounce branch on <b>every</b> tick after
        /// the first shot (the boss calls <c>attack()</c> every frame of its step-3 beat), so
        /// <c>t_auto</c> is refreshed to 3 and decayed to 2 forever and the weapon fires
        /// <b>exactly once for the whole fight</b>.</para>
        ///
        /// <para>That is the reported "she attacks too rarely — one bullet, one lightning". It hit
        /// <c>alilight2</c> (rapid 20), <c>aliblade</c> (10) and <c>alipsy2</c> (30); <c>alimray</c>
        /// (rapid 1) was already <c>auto</c> by the <c>rapid &lt;= 6</c> rule and so was the only one of
        /// the four that kept firing. The call is per-mount and idempotent, and it is here rather than
        /// in <see cref="EnemyWeaponMount.Initialize"/> because the raider family deliberately keeps
        /// the debounce (its <c>attack()</c> is <c>isrnd(0.1)</c>-gated, which is the gap the debounce
        /// needs) — see <see cref="EnemyWeaponMount.MarkAutomaticTrigger"/>.</para>
        /// </remarks>
        void EquipBossWeapons()
        {
            if (Stats == null || !HasWeaponServices) return;

            for (int i = 0; i < BossAlicornBrain.WeaponIds.Length; i++)
            {
                EnemyWeaponMount mount = EquipWeapon(BossAlicornBrain.WeaponIds[i]);
                if (mount == null) continue;

                mount.MarkAutomaticTrigger();

                // `setLevel`'s weapon half. Multiplied, and marked per index, because this method runs
                // from both `Initialize` and `OnWeaponServicesReady` — a mount that arrives on the
                // second call still has to be scaled, and one that was already there must not be
                // scaled twice. See WeaponDamageScale for which two of the four are touched.
                if (mount.Controller != null && !_weaponScaled[i])
                {
                    _weaponScaled[i] = true;
                    mount.Controller.State.DamageScale *= WeaponDamageScale(i);
                }
            }
        }

        /// <summary>
        /// One flag per slot of <see cref="BossAlicornBrain.WeaponIds"/> — "this mount has already taken
        /// its <c>setLevel</c> factor". See <see cref="EquipBossWeapons"/>.
        /// </summary>
        readonly bool[] _weaponScaled = new bool[BossAlicornBrain.WeaponIds.Length];

        /// <summary>
        /// <c>setLevel()</c>'s per-weapon damage factor (<c>UnitBossAlicorn.as:141-164</c>), for the
        /// weapon in <paramref name="weaponIndex"/>'s slot of <see cref="BossAlicornBrain.WeaponIds"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two of the four weapons scale, and the asymmetry is the oracle's.</b> The method
        /// reads:</para>
        /// <code>
        /// _loc2_ = 1 + level * 0.07;
        /// _loc3_ = 1; if(dif == 3) 1.2; if(dif == 4) 1.5;
        /// hp = maxhp = hp * _loc3_;
        /// dam *= _loc3_;
        /// if(weaps[1])         weaps[1].damage       *= _loc2_ * _loc3_;
        /// if(currentWeapon)    currentWeapon.damage  *= _loc3_;
        /// </code>
        /// <para><c>currentWeapon</c> is <c>weaps[0]</c> at construction (<c>:129</c>), so exactly
        /// <b>alilight2</b> (index 0) and <b>aliblade</b> (index 1) are scaled. <c>alipsy2</c> and
        /// <c>alimray</c> are not, and a port that scaled all four uniformly would be silently harder
        /// than the oracle on hard. <c>_loc2_</c> multiplies the blade only — and it is <c>1</c> here
        /// because the port has no NPC level (<c>BossAlicornRules.BladeLevelScale</c>'s remarks), so it
        /// is applied rather than dropped: the day a level lands, the blade is the one that follows it.
        /// </para>
        /// </remarks>
        float WeaponDamageScale(int weaponIndex)
        {
            float difficulty = BossAlicornRules.DifficultyScale(GlobalDifficulty.Value);

            switch (weaponIndex)
            {
                case 0: // alilight2 — `currentWeapon` at construction
                    return difficulty;
                case 1: // aliblade — `weaps[1]`
                    return BossAlicornRules.BladeLevelScale(NpcLevel) * difficulty;
                default: // alipsy2, alimray — untouched by `setLevel`
                    return 1f;
            }
        }

        /// <summary>
        /// AS3 <c>setLevel(param1)</c>'s level argument. The port has no NPC level system, so this is
        /// <c>0</c> — the value that makes <see cref="BossAlicornRules.BladeLevelScale"/> the identity,
        /// exactly as <c>BossAlicornRules.BladeLevelScale</c>'s own remarks predict.
        /// </summary>
        const int NpcLevel = 0;

        /// <summary>
        /// <c>setLevel()</c>'s body half — <c>hp = maxhp = hp * _loc3_; dam *= _loc3_;</c>
        /// (<c>:154-155</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is not free.</b> <c>RoomUnitSpawner</c> has already applied AS3's
        /// <i>base</i> <c>Unit.setLevel</c> HP rule — but that rule is
        /// <c>DifficultyRules.EnemyHpMultiplier</c>, which is <c>1</c> at every difficulty above 1 and
        /// only shrinks a unit on easy. So the boss's <i>own</i> 1.2 / 1.5 was the only scaling it had,
        /// and it was never applied: on Hard and Very Hard the boss spawned on the same 4500 HP and the
        /// same 35 contact damage as on Normal. That is the "she seems weaker than intended" report, and
        /// it is a straight missing statement rather than a tuning question.</para>
        ///
        /// <para><c>BossUltraController.Initialize</c> composes the same two multipliers for the
        /// sentinel and is the precedent; the difference is that this one also scales the <i>body</i>
        /// damage, because <c>UnitBossAlicorn</c> does.</para>
        /// </remarks>
        void ApplyDifficultyScaling()
        {
            float difficulty = BossAlicornRules.DifficultyScale(GlobalDifficulty.Value);
            if (Mathf.Approximately(difficulty, 1f)) return;

            if (_unitStats != null)
            {
                _unitStats.MaxHp.Value *= difficulty;
                _unitStats.CurrentHp.Value = _unitStats.MaxHp.Value;
            }

            // `dam *= _loc3_` — the INSTANCE damage, not the definition's. `MeleeDamage` is
            // `Definition.damage * _eliteDamageMultiplier`, and `Definition` is a shared asset: writing
            // it would buff every alicorn boss in every room, for the rest of the session.
            ScaleMeleeDamage(difficulty);
        }

        protected override void OnWeaponServicesReady() => EquipBossWeapons();

        /// <summary>
        /// <c>UnitBossAlicorn.setWeaponPos</c> — the per-animation-frame bone table
        /// (<c>BlitAnim.wPosAlicornBoss</c>), which <see cref="AlicornBossHoldPoint"/> now answers.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a label, not the mechanism.</b> <c>EnemyWeaponMount.ResolveHoldPoint</c>
        /// prefers an <see cref="PFE.Systems.Weapons.IWeaponHoldPointSource"/> and only falls through to
        /// <see cref="WeaponHoldRule"/> when there is none — and <c>ResolveHold</c> maps
        /// <c>WPosTable</c> to the base hold, because a rule that sees only the body cannot express a
        /// per-frame table. Naming the rule here is what keeps the <i>fallback</i> honest (it is the
        /// inherited <c>Unit.setWeaponPos</c>, the same answer the oracle's <c>catch</c> gives) and
        /// keeps the call site from claiming <c>Base</c>, which would read as "this class overrides
        /// nothing".</para>
        /// </remarks>
        protected override WeaponHoldRule ResolveHoldRule() => WeaponHoldRule.WPosTable;

        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            // `setLevel` runs at spawn, before the first `control()` — so the HP and the body damage
            // are scaled before anything can read them, and the weapons get theirs as each mount is
            // built (a mount that arrives later still lands on the right number).
            ApplyDifficultyScaling();
            EquipBossWeapons();

            // The animated anchor, from the constructor's `this.wPos = BlitAnim.wPosAlicornBoss`
            // (`UnitBossAlicorn.as:122`). Attached AFTER the weapons so the first mount's lazy
            // `ResolveHoldSource` finds it on its very first tick rather than falling back for a frame;
            // and idempotent, because a placed unit has `Initialize` called twice.
            AlicornBossHoldPoint.Attach(this);
        }

        /// <summary>
        /// <c>dropLoot()</c> (<c>UnitBossAlicorn.as:166-172</c>): <c>newPart("bloodblast");</c>
        /// <c>Snd.ps("bale_e"); currentWeapon.vis.visible = false; super.dropLoot();</c>.
        /// </summary>
        protected override void OnFinalDeath()
        {
            Vector2 world = (Vector2)transform.position * PFE.Systems.Map.TileQuery.TileQueryConstants.UnitToPixel;
            EmitParticle("bloodblast", world);

            // `Snd.ps("bale_e")` — a positional one-shot, and the id is already in the port's catalog
            // (ExplosionVisualRules uses it for the same explosion family).
            Sound?.Play("bale_e", transform.position);

            // `currentWeapon.vis.visible = false` — the boss dies with its weapon still "held"; the
            // mount's sprite is hidden so the corpse does not carry a rifle into the floor.
            EnemyController enemy = this;
            BossAlicornBrain brain = BossBrain as BossAlicornBrain;
            if (brain != null && !string.IsNullOrEmpty(brain.CurrentWeaponId))
            {
                enemy.GetWeaponMount(brain.CurrentWeaponId)?.SetHeldVisualVisible(false);
            }

            base.OnFinalDeath();
        }
    }

    [RequireComponent(typeof(BossDronBrain))]
    public class BossDronController : BossController
    {
        public new const string ControllerId = "UnitBossDron";
        public new const string ControllerAlias = "bossdron";

        /// <summary>
        /// <c>UnitBossDron.as:60</c> — <c>collisionTip = 0</c>. The megadron is a floating chassis; its
        /// contact hit does not exchange momentum. See
        /// <see cref="PFE.Entities.Units.UnitController.CollisionTip"/>.
        /// </summary>
        public override bool CollisionTip => false;
    }

    /// <summary>
    /// The Ultra Sentinel's controller — the Stable 88 core boss. Its behaviour is
    /// <see cref="BossUltraBrain"/>; this class owns what the brain cannot: the body's physical
    /// properties, the shield <i>pool</i>, the loadout and the death burst.
    /// </summary>
    /// <remarks>
    /// <para><b>The shield moved onto <see cref="UnitStats"/> and the old absorption override is
    /// gone.</b> This class used to intercept <c>TakeDamage</c>/<c>ApplyDamage</c> and spend a private
    /// <c>_shieldHp</c> field on the brain. That pool was invisible to everything else: the damage
    /// resolver could not see it, so the shield's 15-point armour never applied and EMP vulnerability
    /// could not be expressed; the dome overlay could not see it, so the boss looked unshielded while
    /// it was shielded; and the HUD could not see it. It now lives in <c>UnitStats.ShitHp</c>, where
    /// <c>DamageCalculator</c> spends it and hands the post-hit value back on
    /// <c>DamageOutcome.ShieldHpAfter</c> — the same move <c>AlicornController</c> made, for the same
    /// reason. <b>Both overrides are deleted rather than emptied</b>: the base class already does the
    /// right thing once the pool is where it can be seen.</para>
    ///
    /// <para><b>What that costs.</b> <c>UnitController.TakeDamage(float)</c> — the raw path, which
    /// subtracts through <c>UnitStats.Damage</c> — does not spend a shield for <i>any</i> unit in this
    /// port. The resolver calls <c>ApplyDamage(outcome)</c> (<c>DamageSystem.cs:559</c>), so in play the
    /// shield is always spent; a future caller of the raw path would bypass it. Recorded rather than
    /// papered over with a third writer of the pool.</para>
    /// </remarks>
    [RequireComponent(typeof(BossUltraBrain))]
    public class BossUltraController : BossController
    {
        public new const string ControllerId = "UnitBossUltra";
        public new const string ControllerAlias = "bossultra";

        /// <summary>
        /// <c>expl()</c> (<c>UnitBossUltra.as:151-154</c>) — <c>newPart("metal", 22)</c>. Twenty-two
        /// debris parts, emitted once, on the death that sticks.
        /// </summary>
        public const int MetalPartsOnDeath = 22;

        /// <summary>AS3 <c>shitMaxHp</c> after <c>setLevel</c> — the capacity, not the current pool.</summary>
        float _shieldMaxHp = BossUltraRules.ShieldMaxHpBase;

        /// <summary>
        /// The shield's capacity, which the brain needs in order to refill it
        /// (<c>castShit</c>: <c>shithp = shitMaxHp</c>) and to quadruple it at half HP
        /// (<c>:441</c>: <c>shitMaxHp * 4</c>).
        /// </summary>
        /// <remarks>
        /// <b>Not a constant 500.</b> <c>setLevel()</c> scales it by <c>1 + level*0.12</c> and by the
        /// difficulty (<c>:127</c>), so a level-10 room on Very Hard gives the sentinel a 1100-point
        /// shield — and its phase-two pool is 4400, not 2000.
        /// </remarks>
        public float ShieldMaxHp => _shieldMaxHp;

        /// <summary>The live pool — AS3 <c>shithp</c>, read from <see cref="UnitStats.ShitHp"/>.</summary>
        public float ShieldHp => UnitStats != null ? UnitStats.ShitHp : 0f;

        /// <summary>Write the pool. The only setter; the brain never touches the field directly.</summary>
        public void SetShieldHp(float value)
        {
            if (UnitStats == null) return;
            UnitStats.ShitHp = Mathf.Max(0f, value);
        }

        /// <summary>
        /// <c>isFly = true</c> in the constructor (<c>UnitBossUltra.as:88</c>) — the sentinel is a
        /// floating chassis for the whole fight and never lands.
        /// </summary>
        /// <remarks>
        /// <b>Without this override the boss falls.</b> <see cref="UnitController.IsFlying"/> is
        /// <c>protected virtual bool IsFlying =&gt; false</c>, and <c>ApplyGravity</c>'s <c>isFly</c> arm
        /// is what skips gravity and applies the flyer's speed cap and brake deadzone. A boss with
        /// <c>brake = 0.3</c> and no such arm sinks to the floor of the arena and stays there — the
        /// whole reposition ladder becomes meaningless. This is the exact bug <c>FlyerController</c>
        /// shipped with.
        /// </remarks>
        protected override bool IsFlying => true;

        /// <summary>
        /// <c>UnitBossUltra.setWeaponPos</c> (<c>:240-244</c>) — <c>weaponY = vis.y - 110</c>, the
        /// tallest hold point in the roster. See <see cref="WeaponHoldRule.BossUltra"/>.
        /// </summary>
        protected override WeaponHoldRule ResolveHoldRule() => WeaponHoldRule.BossUltra;

        /// <summary>
        /// The sentinel carries a real shield pool, so the dome overlay is worth building.
        /// </summary>
        /// <remarks>
        /// <para><b>The ordinary dome, not the boss one.</b> The constructor creates
        /// <c>new visShit()</c> (<c>:78</c>) — the base clip — scaled 1.7 and offset
        /// <c>y = -70</c> (<c>:82-83</c>). <c>visShit3</c> belongs to <c>UnitBossAlicorn</c> alone, so
        /// <see cref="UnitController.UsesBossShieldOverlay"/> stays <c>false</c> and the tier stays 0.</para>
        ///
        /// <para><b>Nothing draws today, and it is not this class's fault.</b> All three clips in
        /// <c>UnitShieldOverlayDefinition.asset</c> — <c>shit</c>, <c>shit2</c> and <c>shit3</c> —
        /// carry <c>frames: []</c>, so <c>UnitShieldOverlayRules.Select</c> answers <c>null</c> for
        /// every unit in the game, the alicorn included. The flag is still correct to return: it is a
        /// statement about the unit, and the moment the importer populates the clips the dome appears
        /// with no change here.</para>
        /// </remarks>
        public override bool HasSpellShieldOverlay => true;

        /// <summary>
        /// Build the mounts for all five weapons — the constructor half of <c>UnitBossUltra</c> that
        /// the port was missing, which is why the boss fought with nothing but its body.
        /// </summary>
        /// <remarks>
        /// <para><b>All five are equipped and one fires.</b> The rotation names the active slot and the
        /// brain calls <c>FireOnce</c> on that mount only; the other four still step, because AS3's
        /// child objects are all live.</para>
        ///
        /// <para><b><c>robogatp2</c> is built hidden.</b> <c>currentWeapon2.vis.visible</c> is
        /// <c>false</c> from construction (<c>:80</c>) until the phase change flips it
        /// (<c>:443-444</c>), so the boss does not appear to carry two gatlings for the first half of
        /// the fight. Setting it here rather than in the brain means the visual is right even on the
        /// ticks before the first <c>SimTick</c>.</para>
        ///
        /// <para><b>Both guards are load-bearing.</b> <c>RoomUnitSpawner</c> hands the weapon services
        /// over <i>before</i> <c>Initialize</c>, and <c>Faction</c> derives from <c>Stats</c>, so the
        /// early call must be a no-op and <c>Initialize</c> must be the one that works; a late
        /// <c>OnWeaponServicesReady</c> covers the reverse order. <c>EquipWeapon</c> is idempotent per
        /// id, so neither order double-equips.</para>
        /// </remarks>
        void EquipBossWeapons()
        {
            if (Stats == null || !HasWeaponServices) return;

            for (int i = 0; i < BossUltraBrain.WeaponIds.Length; i++)
            {
                EquipWeapon(BossUltraBrain.WeaponIds[i]);
            }

            GetWeaponMount(BossUltraBrain.WeaponGatlingEnhanced)?.SetHeldVisualVisible(false);
        }

        protected override void OnWeaponServicesReady() => EquipBossWeapons();

        /// <summary>
        /// <c>setLevel()</c> (<c>:113-149</c>) plus the constructor's constants (<c>:71-103</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Two HP scalings compose, and only one of them is the boss's.</b>
        /// <c>RoomUnitSpawner</c> has already multiplied <c>maxHp</c> by
        /// <c>DifficultyRules.EnemyHpMultiplier</c> — AS3's <c>Unit.setLevel</c>
        /// (<c>Unit.as:1130-1141</c>) — before handing the <see cref="UnitStats"/> over.
        /// <c>UnitBossUltra.setLevel</c> then multiplies <i>that</i> by 1.2 on Hard and 1.5 on Very Hard
        /// (<c>:116-128</c>), which is the boss's own buff. Applying both here is what makes the two
        /// files agree.</para>
        ///
        /// <para><b>The body has no level term, and that is the oracle's omission rather than a
        /// bug.</b> <c>UnitBossUltra.setLevel</c> overrides the base method and never calls it, so
        /// only the <i>shield</i> and the <i>weapon damage</i> scale with the room's
        /// <c>enemyLevel</c> — a level-10 room gives the sentinel the same 3600-point body it always had
        /// and a shield 2.2× thicker. Reproduced, not corrected.</para>
        ///
        /// <para><b>The pool starts at zero.</b> AS3 declares <c>shithp = 0</c> (<c>Unit.as:134</c>) and
        /// the constructor never assigns it; the first shield arrives through <c>castShit()</c> on the
        /// first reposition, which is at least <c>aiTCh = 80</c> ticks away. Handing it a full shield at
        /// spawn would absorb the player's opening shot with a shield the oracle would not have had.</para>
        /// </remarks>
        public override void Initialize(UnitDefinition definition, UnitStats stats)
        {
            base.Initialize(definition, stats);

            int globalDifficulty = GlobalDifficulty.Value;
            float difficultyMultiplier = BossUltraRules.DifficultyHpMultiplier(globalDifficulty);

            _shieldMaxHp = BossUltraRules.ShieldMaxHp(
                Mathf.RoundToInt(LocationDifficulty), difficultyMultiplier);

            if (stats != null)
            {
                stats.ShitArmor = BossUltraRules.ShieldArmor;
                stats.ShitHp = 0f;

                if (!Mathf.Approximately(difficultyMultiplier, 1f))
                {
                    stats.MaxHp.Value *= difficultyMultiplier;
                    stats.CurrentHp.Value = stats.MaxHp.Value;
                }
            }

            EquipBossWeapons();
        }

        /// <summary>
        /// The phase change's shield half — <c>shithp = shitMaxHp * 4</c> (<c>UnitBossUltra.as:441</c>).
        /// </summary>
        /// <remarks>
        /// <b>An assignment, not an addition.</b> A boss entering phase two with 480 points left ends
        /// up on 2000, not 2480. The <c>t_shit = 2000</c> lockout that goes with it is the brain's
        /// (<c>BossUltraBrain.EnterEnhancedPhase</c>), because it is a timer, not a pool.
        /// </remarks>
        public void EnterEnhancedPhase()
        {
            SetShieldHp(BossUltraRules.PhaseTwoShieldHp(_shieldMaxHp));
        }

        /// <summary>
        /// The death that sticks: <c>expl()</c>'s 22 debris parts and <c>dropLoot()</c>'s burst
        /// (<c>:105-111</c> and <c>:151-154</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Both hooks, in the oracle's order.</b> <c>Unit.die()</c> calls <c>expl()</c> at
        /// <c>Unit.as:4366</c> and <c>dropLoot()</c> after it, so the hull comes apart and <i>then</i>
        /// the loot burst plays.</para>
        ///
        /// <para><b>Why this is <c>OnFinalDeath</c> and not <c>OnDeath</c>.</b> This boss has no second
        /// life — <see cref="BossBrain.TryReviveSecondLife"/> is not overridden — so the two coincide
        /// today. Hooking the final one keeps the class honest if a phase were ever added, and matches
        /// <c>BossAlicornController</c>, which genuinely needs the distinction.</para>
        ///
        /// <para><b>Both gatling sprites are hidden.</b> <c>dropLoot()</c> hides
        /// <c>currentWeapon</c> only, because in AS3 the phase-two gatling was already hidden by the
        /// swap. A boss killed <i>before</i> half HP has <c>robogatp2</c> hidden too, so the two arms
        /// never both show — but hiding both explicitly is what keeps a corpse from carrying a gun into
        /// the floor whichever arm ran.</para>
        /// </remarks>
        protected override void OnFinalDeath()
        {
            Vector2 worldPixels =
                (Vector2)transform.position * PFE.Systems.Map.TileQuery.TileQueryConstants.UnitToPixel;

            for (int i = 0; i < MetalPartsOnDeath; i++)
            {
                EmitParticle("metal", worldPixels);
            }

            EmitParticle("baleblast", worldPixels);

            // `Snd.ps("bale_e")` — the same positional one-shot the alicorn's death plays, and already
            // in the port's catalog.
            Sound?.Play("bale_e", transform.position);

            GetWeaponMount(BossUltraBrain.WeaponGatling)?.SetHeldVisualVisible(false);
            GetWeaponMount(BossUltraBrain.WeaponGatlingEnhanced)?.SetHeldVisualVisible(false);

            base.OnFinalDeath();
        }
    }

    [RequireComponent(typeof(ThunderHeadBrain))]
    public class ThunderHeadController : BossController
    {
        public new const string ControllerId = "UnitThunderHead";
        public new const string ControllerAlias = "thunderhead";

        /// <summary>
        /// <c>UnitThunderHead.as:147</c> — <c>collisionTip = 0</c>. See
        /// <see cref="PFE.Entities.Units.UnitController.CollisionTip"/>.
        /// </summary>
        public override bool CollisionTip => false;
    }

    public class ThunderTurretController : EnemyController
    {
        public const string ControllerId = "UnitThunderTurret";
        public const string ControllerAlias = "ttur";
    }
}
