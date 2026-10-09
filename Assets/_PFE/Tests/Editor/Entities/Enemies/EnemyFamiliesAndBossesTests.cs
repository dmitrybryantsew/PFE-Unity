using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Enemies.Bosses;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Map.Rendering;
using PFE.Tests.Editor.Common;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Enemies
{
    [TestFixture]
    public class EnemyFamiliesAndBossesTests
    {
        private List<GameObject> _spawnedObjects;

        [SetUp]
        public void SetUp()
        {
            _spawnedObjects = new List<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_spawnedObjects != null)
            {
                foreach (var obj in _spawnedObjects)
                {
                    if (obj != null)
                    {
                        UnityEngine.Object.DestroyImmediate(obj);
                    }
                }
                _spawnedObjects.Clear();
            }
        }

        private GameObject CreateGameObject(string name = "TestUnit")
        {
            var go = new GameObject(name);
            _spawnedObjects.Add(go);
            return go;
        }

        // ── 1. Registration Tests ──────────────────────────────────────────────

        [Test]
        public void RoomUnitSpawner_RegistersAllArchetypeControllers()
        {
            // Verify every family's primary controller and key aliases are registered
            var expectedIds = new[]
            {
                // ArmedShooter
                "UnitRaider", "raider", "UnitSlaver", "slaver", "UnitZebra", "zebra", "UnitRanger", "ranger",
                "UnitEncl", "encl", "UnitMerc", "merc", "UnitNecros", "necros",
                // Undead
                "UnitZombie", "zombie", "UnitGhoul", "ghoul", "UnitDead", "dead",
                // Alicorn
                "UnitAlicorn", "alicorn",
                // Robot
                "UnitRobobrain", "robobrain", "UnitDron", "dron", "UnitProtect", "protect",
                "UnitGutsy", "gutsy", "UnitSentinel", "sentinel", "UnitSpriteBot", "spritebot",
                "UnitVortex", "vortex", "UnitRoller", "roller", "UnitMsp", "msp",
                // Flyer
                "UnitBloat", "bloat", "UnitBat", "bloodwing", "bat", "UnitPhoenix", "phoenix", "UnitBloatEmitter", "ebloat",
                // Climber
                "UnitAnt", "ant", "UnitAntEmitter", "eant",
                // Beast
                "UnitMonstrik", "monstrik", "rat", "molerat", "scorp", "scorp1", "scorp2", "scorp3", "tarakan",
                "UnitHellhound", "hellhound", "hellhound1", "UnitSlime", "slime", "cryoslime", "pinkslime",
                // Aquatic
                "UnitFish", "fish", "fish1", "fish2", "fish3",
                // Trap / Stationary
                "UnitTurret", "turret", "turret0", "turret1", "turret2", "turret3", "turret4", "turret5",
                "UnitTrap", "mtrap", "UnitTrigger", "trigcans", "trigridge", "trigplate", "triglaser",
                "UnitDamager", "damshot", "damgren", "damexpl1", "UnitMWall", "mwall",
                "UnitTransmitter", "transmitter", "UnitDestr", "destr1"
            };

            foreach (var id in expectedIds)
            {
                Type type = InvokeResolveControllerType(id);
                Assert.IsNotNull(type, $"Controller ID '{id}' should resolve to a registered type.");
                Assert.IsTrue(typeof(UnitController).IsAssignableFrom(type), $"Resolved type for '{id}' must inherit UnitController.");
                Assert.AreNotEqual(typeof(UnitController), type, $"'{id}' must resolve to its specific controller subclass, not plain UnitController.");
            }
        }

        [Test]
        public void RoomUnitSpawner_RegistersAllBossControllers()
        {
            var expectedBossIds = new[]
            {
                "UnitBoss", "boss",
                "UnitBossRaider", "bossraider",
                "UnitBossEncl", "bossencl",
                "UnitBossNecr", "bossnecr",
                "UnitBossAlicorn", "bossalicorn",
                "UnitBossDron", "bossdron", "megadron",
                "UnitBossUltra", "bossultra", "ultra",
                "UnitThunderHead", "thunderhead",
                "UnitThunderTurret", "ttur"
            };

            foreach (var id in expectedBossIds)
            {
                Type type = InvokeResolveControllerType(id);
                Assert.IsNotNull(type, $"Boss ID '{id}' should resolve to a registered type.");
                Assert.IsTrue(typeof(UnitController).IsAssignableFrom(type), $"Resolved type for '{id}' must inherit UnitController.");
                Assert.AreNotEqual(typeof(UnitController), type, $"'{id}' must resolve to its specific boss controller subclass, not plain UnitController.");
            }
        }

        private Type InvokeResolveControllerType(string id)
        {
            var spawner = (RoomUnitSpawner)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RoomUnitSpawner));
            var method = typeof(RoomUnitSpawner).GetMethod("ResolveControllerType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (Type)method.Invoke(spawner, new object[] { id });
        }

        // ── 2. Component Attachment Tests ──────────────────────────────────────

        [Test]
        public void ArchetypeControllers_AutomaticallyRequireAndAttachExpectedBrains()
        {
            // ArmedShooter
            var raiderGo = CreateGameObject("Raider");
            var raider = raiderGo.AddComponent<RaiderController>();
            Assert.IsNotNull(raiderGo.GetComponent<ArmedShooterBrain>());

            // Alicorn
            var alicornGo = CreateGameObject("Alicorn");
            var alicorn = alicornGo.AddComponent<AlicornController>();
            Assert.IsNotNull(alicornGo.GetComponent<AlicornBrain>());

            // Robot
            var robobrainGo = CreateGameObject("Robobrain");
            var robobrain = robobrainGo.AddComponent<RobobrainController>();
            Assert.IsNotNull(robobrainGo.GetComponent<RobotBrain>());

            // Flyer
            var bloatGo = CreateGameObject("Bloat");
            var bloat = bloatGo.AddComponent<BloatController>();
            Assert.IsNotNull(bloatGo.GetComponent<FlyerBrain>());

            // Climber
            var antGo = CreateGameObject("Ant");
            var ant = antGo.AddComponent<AntController>();
            Assert.IsNotNull(antGo.GetComponent<ClimberBrain>());

            // Beast
            var hellhoundGo = CreateGameObject("Hellhound");
            var hellhound = hellhoundGo.AddComponent<HellhoundController>();
            var houndBrain = hellhoundGo.GetComponent<BeastBrain>();
            Assert.IsNotNull(houndBrain);
            Assert.AreEqual(BeastKind.Hound, houndBrain.Kind);

            var slimeGo = CreateGameObject("Slime");
            var slime = slimeGo.AddComponent<SlimeController>();
            var slimeBrain = slimeGo.GetComponent<BeastBrain>();
            Assert.IsNotNull(slimeBrain);
            Assert.AreEqual(BeastKind.Slime, slimeBrain.Kind);

            // Aquatic
            var fishGo = CreateGameObject("Fish");
            var fish = fishGo.AddComponent<FishController>();
            Assert.IsNotNull(fishGo.GetComponent<AquaticBrain>());

            // Trap
            var turretGo = CreateGameObject("Turret");
            var turret = turretGo.AddComponent<TurretController>();
            var turretBrain = turretGo.GetComponent<TrapBrain>();
            Assert.IsNotNull(turretBrain);
            Assert.AreEqual(TrapKind.Turret, turretBrain.Kind);

            // Bosses
            var bossRaiderGo = CreateGameObject("BossRaider");
            bossRaiderGo.AddComponent<BossRaiderController>();
            Assert.IsNotNull(bossRaiderGo.GetComponent<BossRaiderBrain>());

            var bossNecrGo = CreateGameObject("BossNecr");
            bossNecrGo.AddComponent<BossNecrController>();
            Assert.IsNotNull(bossNecrGo.GetComponent<BossNecrBrain>());

            var bossAlicornGo = CreateGameObject("BossAlicorn");
            bossAlicornGo.AddComponent<BossAlicornController>();
            Assert.IsNotNull(bossAlicornGo.GetComponent<BossAlicornBrain>());

            var bossUltraGo = CreateGameObject("BossUltra");
            bossUltraGo.AddComponent<BossUltraController>();
            Assert.IsNotNull(bossUltraGo.GetComponent<BossUltraBrain>());
        }

        // ── 3. Mechanics Tests ─────────────────────────────────────────────────

        [Test]
        public void BossNecrBrain_RevivesIntoPhaseTwoOnFirstDeath()
        {
            var go = CreateGameObject("BossNecr");
            var controller = go.AddComponent<BossNecrController>();
            var brain = go.GetComponent<BossNecrBrain>();

            var stats = new UnitStats(3200f, 100f);
            controller.Initialize(null, stats);

            Assert.AreEqual(1, brain.PhaseIndex);
            Assert.IsFalse(brain.IsSecondLifeActive);

            // Trigger death
            stats.CurrentHp.Value = 0f;
            Assert.IsFalse(controller.IsAlive);

            // Revive check
            bool revived = brain.TryReviveSecondLife();
            Assert.IsTrue(revived, "BossNecr must revive from phase 1 death");
            Assert.AreEqual(2, brain.PhaseIndex, "Phase must advance to 2");
            Assert.IsTrue(brain.IsSecondLifeActive);

            // Second death must not revive
            bool revivedSecond = brain.TryReviveSecondLife();
            Assert.IsFalse(revivedSecond, "BossNecr must not revive a second time");
        }

        [Test]
        public void BossAlicornBrain_BreaksShieldOnFirstDeath()
        {
            var go = CreateGameObject("BossAlicorn");
            var controller = go.AddComponent<BossAlicornController>();
            var brain = go.GetComponent<BossAlicornBrain>();

            var stats = new UnitStats(4500f, 100f);
            controller.Initialize(null, stats);

            Assert.AreEqual(1, brain.PhaseIndex);
            Assert.IsTrue(brain.IsShit, "Spell shield should be active initially");

            bool revived = brain.TryReviveSecondLife();
            Assert.IsTrue(revived, "BossAlicorn must revive upon shield destruction");
            Assert.IsFalse(brain.IsShit, "Shield must be broken in phase 2");
            Assert.AreEqual(2, brain.PhaseIndex);

            bool revivedSecond = brain.TryReviveSecondLife();
            Assert.IsFalse(revivedSecond, "BossAlicorn must not revive a second time");
        }

        [Test]
        public void BossUltraBrain_ActivatesUsilAtHalfHp()
        {
            var go = CreateGameObject("BossUltra");
            var controller = go.AddComponent<BossUltraController>();
            var brain = go.GetComponent<BossUltraBrain>();

            var def = ScriptableObject.CreateInstance<UnitDefinition>();
            def.health = 3600;
            var stats = new UnitStats(3600f, 100f);
            controller.Initialize(def, stats);

            Assert.AreEqual(1, brain.PhaseIndex);
            Assert.IsFalse(brain.Usil);
            Assert.AreEqual(0f, brain.ShieldHp);

            // Damage down to 40% HP
            stats.CurrentHp.Value = 1440f;
            brain.SimTick(1);

            Assert.IsTrue(brain.Usil, "Usil mode must activate when HP drops below 50%");
            Assert.AreEqual(2, brain.PhaseIndex);
            Assert.AreEqual(2000f, brain.ShieldHp, "Must gain 2000 secondary shield");
        }

        [Test]
        public void BossBrain_QuarterHpThresholds_FireMonotonically()
        {
            var go = CreateGameObject("BossRaider");
            var controller = go.AddComponent<BossRaiderController>();
            var brain = go.GetComponent<BossRaiderBrain>();

            var def = ScriptableObject.CreateInstance<UnitDefinition>();
            def.health = 2000;
            var stats = new UnitStats(2000f, 100f);
            controller.Initialize(def, stats);

            int quartersReceived = 0;
            brain.OnQuarterHpThreshold += q => quartersReceived = q;

            // 80% HP -> 0 quarters
            stats.CurrentHp.Value = 1600f;
            brain.SimTick(1);
            Assert.AreEqual(0, quartersReceived);

            // 70% HP -> 1 quarter passed
            stats.CurrentHp.Value = 1400f;
            brain.SimTick(2);
            Assert.AreEqual(1, quartersReceived);

            // 45% HP -> 2 quarters passed
            stats.CurrentHp.Value = 900f;
            brain.SimTick(3);
            Assert.AreEqual(2, quartersReceived);

            // 20% HP -> 3 quarters passed
            stats.CurrentHp.Value = 400f;
            brain.SimTick(4);
            Assert.AreEqual(3, quartersReceived);
        }

        [Test]
        public void TrapBrain_NeverPatrolsOrChases()
        {
            var go = CreateGameObject("Turret");
            var controller = go.AddComponent<TurretController>();
            var brain = go.GetComponent<TrapBrain>();

            controller.Initialize(null, new UnitStats(200f, 100f));

            // SimTick several times
            brain.SimTick(1);
            brain.SimTick(2);

            // Turret must stay stationary
            Assert.AreEqual(Vector3.zero, go.transform.position);
        }

        [Test]
        public void AllDocumentedEnemyVariants_ResolveToSpecificControllers()
        {
            var variants = new[]
            {
                // Armed Shooter
                "raider", "raider1", "raider2", "raider3", "raider4", "raider5", "raider6", "raider7", "raider8", "raider9",
                "slaver", "slaver1", "slaver2", "slaver3", "slaver4", "slaver5", "slaver6",
                "zebra", "zebra1", "zebra2", "zebra3", "zebra4", "zebra5",
                "ranger", "ranger1", "ranger2", "ranger3",
                "encl", "encl1", "encl2", "encl3", "encl4",
                "merc", "merc1", "merc2", "merc3", "merc4", "merc5",
                "necros",

                // Undead
                "zombie", "zombie0", "zombie1", "zombie2", "zombie3", "zombie4", "zombie5", "zombie6", "zombie7", "zombie8", "zombie9",
                "spectre",

                // Alicorn
                "alicorn", "alicorn1", "alicorn2", "alicorn3",

                // Robot
                "robot", "robobrain",
                "dron1", "dron2", "dron3", "dront",
                "protect", "protect1",
                "gutsy", "gutsy1",
                "sentinel", "spritebot", "vortex",
                "roller", "roller2", "msp",

                // Flyer
                "bloat", "bloat0", "bloat1", "bloat2", "bloat3", "bloat4", "bloat5", "bloat6", "bloat7", "bloat8", "bloat9", "bloat10",
                "ebloat", "bloodwing", "bloodwing2", "phoenix",

                // Climber
                "ant", "ant1", "ant2", "ant3", "eant",

                // Beast
                "rat", "molerat", "scorp1", "scorp2", "scorp3", "tarakan",
                "hellhound", "hellhound1",
                "slime", "cryoslime", "pinkslime",

                // Aquatic
                "fish1", "fish2", "fish3",

                // Trap / Stationary
                "turret", "turret0", "turret1", "turret2", "turret3", "turret4", "turret5",
                "ttur", "mtrap", "mwall",
                "trigcans", "triglaser", "trigplate", "trigridge",
                "damgren", "damshot", "damexpl1",
                "destr1", "transmitter",

                // Bosses
                "bossraider", "bossencl", "bossnecr", "bossalicorn", "bossdron", "bossultra", "thunderhead"
            };

            foreach (var variant in variants)
            {
                Type type = InvokeResolveControllerType(variant);
                Assert.IsNotNull(type, $"Variant '{variant}' must resolve to a valid controller type.");
                Assert.IsTrue(typeof(EnemyController).IsAssignableFrom(type),
                    $"Variant '{variant}' resolved to {type.Name}, which does not inherit from EnemyController!");
                Assert.AreNotEqual(typeof(UnitController), type,
                    $"Variant '{variant}' must NOT fall back to raw UnitController!");
            }
        }

        [Test]
        public void EnemyVariants_CanBeSpawned_WithRequiredBrainsAttached()
        {
            var testSample = new[]
            {
                ("raider3", typeof(ArmedShooterBrain)),
                ("zombie4", typeof(ZombieBrain)),
                ("alicorn2", typeof(AlicornBrain)),
                ("dron2", typeof(RobotBrain)),
                ("bloat5", typeof(FlyerBrain)),
                ("ant2", typeof(ClimberBrain)),
                ("rat", typeof(BeastBrain)),
                ("fish2", typeof(AquaticBrain)),
                ("turret2", typeof(TrapBrain)),
                ("bossultra", typeof(BossUltraBrain))
            };

            foreach (var (variant, expectedBrainType) in testSample)
            {
                Type ctrlType = InvokeResolveControllerType(variant);
                var go = CreateGameObject($"SpawnTest_{variant}");
                var comp = go.AddComponent(ctrlType);
                Assert.IsNotNull(comp, $"Failed to add component {ctrlType.Name} for variant {variant}");
                var brain = go.GetComponent<EnemyBrain>();
                Assert.IsNotNull(brain, $"Variant {variant} ({ctrlType.Name}) must have an EnemyBrain attached");
                Assert.IsTrue(expectedBrainType.IsAssignableFrom(brain.GetType()),
                    $"Variant {variant} expected brain {expectedBrainType.Name}, but got {brain.GetType().Name}");
            }
        }

        // ── 4. Review 2026-10-08 Issue Verification Tests ─────────────────────

        [Test]
        public void Lint_EveryBrainResolveAnimState_ReturnsOnlyValidAs3IdsOrNull()
        {
            var validSet = new HashSet<string>(AnimationSet.As3Ids);
            string projectRoot = SourceLint.ProjectRoot();
            string enemiesDir = Path.Combine(projectRoot, "Assets", "_PFE", "Entities", "Enemies");
            Assert.IsTrue(Directory.Exists(enemiesDir), $"Enemies directory missing at {enemiesDir}");

            string[] brainFiles = Directory.GetFiles(enemiesDir, "*Brain.cs", SearchOption.AllDirectories);
            Assert.That(brainFiles.Length, Is.GreaterThanOrEqualTo(16), "Expected at least 16 Brain source files");

            var returnRegex = new Regex(@"return\s+""([^""]+)"";");

            foreach (var file in brainFiles)
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("ResolveAnimState()")) continue;

                string body = SourceLint.MethodBody(text, "string ResolveAnimState()");
                var matches = returnRegex.Matches(body);
                foreach (Match match in matches)
                {
                    string animId = match.Groups[1].Value;
                    Assert.IsTrue(validSet.Contains(animId),
                        $"File '{Path.GetFileName(file)}' returns invalid animation id '{animId}' in ResolveAnimState(). Valid IDs: {string.Join(", ", AnimationSet.As3Ids)}");

                    // There used to be four `AreNotEqual` pins here banning `attack`, `derg`, `swim` and
                    // `wake`. They were the wrong fix. `attack` and `derg` are AUTHORED rows —
                    // scorp1..3 punch with `attack`, and raider/slaver/zebra fly with `derg` — so
                    // banning them deleted the animations rather than porting them; `AnimationSet` now
                    // has a field for each and this membership check accepts them. `swim` and `wake` were
                    // never AS3 ids at all (`wake` does not appear in AllData.as, and the swim id is
                    // `plav`), so they are already refused by the check above. Nothing needs pinning here
                    // now: the id set is the whole contract.
                }
            }
        }

        [Test]
        public void AquaticBrain_TargetLock_RequiresSubmergedOrInWaterTarget()
        {
            var fishGo = CreateGameObject("Fish");
            var fishCtrl = fishGo.AddComponent<FishController>();
            var brain = fishGo.GetComponent<AquaticBrain>();
            fishCtrl.Initialize(null, new UnitStats(100f, 100f));

            var targetGo = CreateGameObject("TargetPlayer");
            var targetCtrl = targetGo.AddComponent<UnitController>();
            targetCtrl.Initialize(null, new UnitStats(100f, 100f));

            brain.Blackboard.TargetUnit = targetCtrl;

            // 1. Dry target -> targetInWater is false, aiSpok decreases on 10-tick interval
            targetCtrl.SetSubmergedOverride(false);
            brain.AiSpok = 20;
            brain.SimTick(10);
            Assert.AreEqual(19, brain.AiSpok, "When target is dry, AiSpok must decrease on 10-tick intervals");

            // 2. Submerged target -> targetInWater is true, aiSpok locks to MaxSpok + 10 (40)
            targetCtrl.SetSubmergedOverride(true);
            brain.Blackboard.TargetUnit = targetCtrl;
            brain.SimTick(10);
            Assert.AreEqual(40, brain.AiSpok, "When target is submerged/in water, AiSpok must commit to MaxSpok + 10");
        }

        [Test]
        public void AlicornShield_StartsEmpty_AndRaisesOnTheCastTimer()
        {
            var alicornGo = CreateGameObject("Alicorn");
            var alicornCtrl = alicornGo.AddComponent<AlicornController>();
            alicornCtrl.Initialize(null, new UnitStats(100f, 100f));

            // AS3 `Unit.as:134` declares `shithp = 0` and the UnitAlicorn constructor never assigns it,
            // so an alicorn spawns UNARMOURED and raises its shield through the `t_shit` ladder in
            // `control()` (:571-578). The port used to hand it a full 300-point shield at spawn, which
            // is a different first exchange: the player's opening shot was absorbed by a shield the
            // oracle would not have had up yet.
            Assert.AreEqual(0f, alicornCtrl.ShieldHp, 1e-4f, "shithp starts at 0");
            Assert.AreEqual(300f, alicornCtrl.MaxShieldHp, 1e-4f, "shitMaxHp 300 on tr1/tr2 (:33)");
            Assert.AreEqual(25f, alicornCtrl.ShieldArmor, 1e-4f, "shitArmor 25 (:152)");
            Assert.AreEqual(90, alicornCtrl.ShieldCastTimerTicks, "t_shit 90 (:111)");
            Assert.AreEqual(1f, alicornCtrl.AllVulnerabilityMultiplier, 1e-4f,
                "No shield means no resistance — `allVulnerMult = shithp > 0 ? 0.6 : 1`.");

            // AS3 :571 — the decrement is gated on `aiSpok > 0 || tr == 3 && osob || t_shit > 150`.
            // `t_shit` is 90, so `> 150` is FALSE and an unaware unit's timer never moves: it has no
            // shield and never casts one. The old port's 90-tick cooldown cast regardless.
            for (int i = 0; i < 200; i++) alicornCtrl.UpdateShieldTick(alerted: false);

            Assert.AreEqual(90, alicornCtrl.ShieldCastTimerTicks,
                "An unaware unit's timer is frozen at 90.");
            Assert.AreEqual(0f, alicornCtrl.ShieldHp, 1e-4f);

            // 89 aware ticks is one short of the cast...
            for (int i = 0; i < 89; i++) alicornCtrl.UpdateShieldTick(alerted: true);

            Assert.AreEqual(1, alicornCtrl.ShieldCastTimerTicks);
            Assert.AreEqual(0f, alicornCtrl.ShieldHp, 1e-4f, "89 ticks must not have cast yet");

            // ...and the 90th is the one that does, in the same call that reaches zero.
            alicornCtrl.UpdateShieldTick(alerted: true);

            Assert.AreEqual(300f, alicornCtrl.ShieldHp, 1e-4f, "castShit fills the pool (:1090)");
            Assert.AreEqual(1000, alicornCtrl.ShieldCastTimerTicks, "castShit writes t_shit = 1000");
            Assert.AreEqual(0.6f, alicornCtrl.AllVulnerabilityMultiplier, 1e-4f,
                "UnitAlicorn.as:593 — 0.6 while the pool is positive");
        }

        [Test]
        public void AlicornShield_Tier3_UsesTheBigPoolAndTheHarderResistance()
        {
            var def = ScriptableObject.CreateInstance<UnitDefinition>();
            def.id = "alicorn3";
            def.health = 250;

            var go = CreateGameObject("Alicorn3");
            var ctrl = go.AddComponent<AlicornController>();
            var stats = new UnitStats(250f, 100f);
            ctrl.Initialize(def, stats);

            Assert.AreEqual(3, ctrl.Tier, "The tier comes from the id's trailing digit, not the health.");
            Assert.AreEqual(500f, ctrl.MaxShieldHp, 1e-4f, "UnitAlicorn.as:173");
            Assert.AreEqual(50f, ctrl.ShieldArmor, 1e-4f, "UnitAlicorn.as:172");
            Assert.AreEqual(45, ctrl.ShieldCastTimerTicks, "UnitAlicorn.as:178");

            // The dome: tr3 is the only tier that swaps visShit for visShit2 (:191).
            Assert.AreEqual(UnitShieldOverlayKind.Large,
                UnitShieldOverlayRules.KindFor(ctrl.ShieldOverlayTier, ctrl.UsesBossShieldOverlay));

            ctrl.CastShield();

            Assert.AreEqual(500f, ctrl.ShieldHp, 1e-4f);
            Assert.AreEqual(50f, stats.ShitArmor, 1e-4f,
                "The rating lands on the stats object the damage resolver reads, not on a private field.");
            Assert.AreEqual(500f, stats.ShitHp, 1e-4f, "the pool is filled, and it is the same field");

            // `t_shit` is 45 and castShit wrote 1000, so a tick from here cannot cast again.
            ctrl.UpdateShieldTick(alerted: false);

            Assert.AreEqual(0.4f, ctrl.AllVulnerabilityMultiplier, 1e-4f, "UnitAlicorn.as:589");
        }

        [Test]
        public void BossRaider_QuarterHpThreshold_TriggersReinforcementWave()
        {
            var go = CreateGameObject("BossRaider");
            var controller = go.AddComponent<BossRaiderController>();
            var brain = go.GetComponent<BossRaiderBrain>();

            var def = ScriptableObject.CreateInstance<UnitDefinition>();
            def.health = 2000;
            var stats = new UnitStats(2000f, 100f);
            controller.Initialize(def, stats);

            Assert.AreEqual(0, controller.ReinforcementsSpawned);

            // 70% HP -> 1st quarter passed
            stats.CurrentHp.Value = 1400f;
            brain.SimTick(1);
            Assert.AreEqual(1, controller.ReinforcementsSpawned, "1st quarter threshold must trigger 1st reinforcement wave");

            // 45% HP -> 2nd quarter passed
            stats.CurrentHp.Value = 900f;
            brain.SimTick(2);
            Assert.AreEqual(2, controller.ReinforcementsSpawned, "2nd quarter threshold must trigger 2nd reinforcement wave");

            // 20% HP -> 3rd quarter passed
            stats.CurrentHp.Value = 400f;
            brain.SimTick(3);
            Assert.AreEqual(3, controller.ReinforcementsSpawned, "3rd quarter threshold must trigger 3rd reinforcement wave");
        }

        [Test]
        public void Flyer_PhoenixDiesInSingleHit_AndBloatSplitsOnVariantThreshold()
        {
            // Phoenix fatal damage
            var phoenixGo = CreateGameObject("Phoenix");
            var phoenixCtrl = phoenixGo.AddComponent<PhoenixController>();
            phoenixCtrl.Initialize(null, new UnitStats(500f, 100f));
            Assert.IsTrue(phoenixCtrl.IsAlive);
            phoenixCtrl.TakeDamage(1f);
            Assert.IsFalse(phoenixCtrl.IsAlive, "Phoenix must die in a single hit regardless of damage amount");
            Assert.AreEqual(0f, phoenixCtrl.CurrentHealth);

            // Bloat split variant < 8
            var bloatSmallGo = CreateGameObject("BloatSmall");
            var bloatSmallCtrl = bloatSmallGo.AddComponent<BloatController>();
            bloatSmallCtrl.Initialize(null, new UnitStats(100f, 100f));
            bloatSmallCtrl.Variant = 1;
            bloatSmallCtrl.TakeDamage(200f);
            Assert.IsFalse(bloatSmallCtrl.IsAlive);
            Assert.AreEqual(0, bloatSmallCtrl.SplitCount, "Bloat variant < 8 must not split");

            // Bloat split variant >= 8
            var bloatBigGo = CreateGameObject("BloatBig");
            var bloatBigCtrl = bloatBigGo.AddComponent<BloatController>();
            bloatBigCtrl.Initialize(null, new UnitStats(100f, 100f));
            bloatBigCtrl.Variant = 8;
            bloatBigCtrl.TakeDamage(200f);
            Assert.IsFalse(bloatBigCtrl.IsAlive);
            Assert.AreEqual(2, bloatBigCtrl.SplitCount, "Bloat variant >= 8 must split into 2");

            // Bloat split isEmit
            var bloatEmitGo = CreateGameObject("BloatEmit");
            var bloatEmitCtrl = bloatEmitGo.AddComponent<BloatController>();
            bloatEmitCtrl.Initialize(null, new UnitStats(100f, 100f));
            bloatEmitCtrl.IsEmit = true;
            bloatEmitCtrl.TakeDamage(200f);
            Assert.IsFalse(bloatEmitCtrl.IsAlive);
            Assert.AreEqual(2, bloatEmitCtrl.SplitCount, "Bloat with IsEmit=true must split into 2");
        }

        [Test]
        public void DeathAnimation_HoundAndAnt_ReturnsDieWhenGrounded_AndDeathWhenAirborne()
        {
            // Hound
            var houndGo = CreateGameObject("Hellhound");
            var houndCtrl = houndGo.AddComponent<HellhoundController>();
            var houndBrain = houndGo.GetComponent<BeastBrain>();
            var houndStats = new UnitStats(100f, 100f);
            houndCtrl.Initialize(null, houndStats);
            houndStats.CurrentHp.Value = 0f; // Dead

            houndCtrl.SetGrounded(true);
            Assert.AreEqual("die", houndBrain.CurrentResolvedAnimState, "Grounded dead hound must return 'die'");

            houndCtrl.SetGrounded(false);
            Assert.AreEqual("death", houndBrain.CurrentResolvedAnimState, "Airborne dead hound must return 'death'");

            // Ant
            var antGo = CreateGameObject("Ant");
            var antCtrl = antGo.AddComponent<AntController>();
            var antBrain = antGo.GetComponent<ClimberBrain>();
            var antStats = new UnitStats(100f, 100f);
            antCtrl.Initialize(null, antStats);
            antStats.CurrentHp.Value = 0f; // Dead

            antCtrl.SetGrounded(true);
            Assert.AreEqual("die", antBrain.CurrentResolvedAnimState, "Grounded dead ant must return 'die'");

            antCtrl.SetGrounded(false);
            Assert.AreEqual("death", antBrain.CurrentResolvedAnimState, "Airborne dead ant must return 'death'");
        }

        [Test]
        public void Sensors_RaycastThrottle_FiresOnTickModulo10Equals1()
        {
            var sensors = new EnemySensors();
            Assert.AreEqual(10, sensors.RaycastThrottleInterval, "Raycast throttle interval must be 10");

            // Verify AS3 parity aiTCh % 10 == 1
            for (int t = 0; t < 30; t++)
            {
                bool expected = (t % 10 == 1);
                bool actual = (t % sensors.RaycastThrottleInterval == 1);
                Assert.AreEqual(expected, actual, $"Tick {t} raycast trigger mismatch");
            }
        }

        [Test]
        public void StopsToAttack_FollowsArchetypeRules_ForRobotFlyerTrap()
        {
            // Robot
            var robotGo = CreateGameObject("Robot");
            robotGo.AddComponent<RobobrainController>();
            var robotBrain = robotGo.GetComponent<RobotBrain>();
            robotBrain.Kind = RobotKind.Standard;
            Assert.IsTrue(robotBrain.CanStopToAttack, "Standard robot stops to attack");

            robotBrain.Kind = RobotKind.Drone;
            Assert.IsFalse(robotBrain.CanStopToAttack, "Drone does not stop to attack");

            robotBrain.Kind = RobotKind.Roller;
            Assert.IsFalse(robotBrain.CanStopToAttack, "Roller does not stop to attack");

            robotBrain.Kind = RobotKind.Vortex;
            Assert.IsFalse(robotBrain.CanStopToAttack, "Vortex does not stop to attack");

            // Flyer
            var flyerGo = CreateGameObject("Flyer");
            flyerGo.AddComponent<BloatController>();
            var flyerBrain = flyerGo.GetComponent<FlyerBrain>();
            Assert.IsFalse(flyerBrain.CanStopToAttack, "Flyer does not stop to attack");

            // Trap
            var turretGo = CreateGameObject("Turret");
            turretGo.AddComponent<TurretController>();
            var turretBrain = turretGo.GetComponent<TrapBrain>();
            Assert.IsFalse(turretBrain.CanStopToAttack, "Trap/Turret does not stop to attack");
        }

        [Test]
        public void Zombie_ResurrectsAfterResurrectTimerExpires()
        {
            var zombieGo = CreateGameObject("Zombie");
            var zombieCtrl = zombieGo.AddComponent<ZombieController>();
            var stats = new UnitStats(100f, 100f);
            zombieCtrl.Initialize(null, stats);
            zombieCtrl.SetCanResurrect(true, timer: 3);

            stats.CurrentHp.Value = 0f;
            Assert.IsFalse(zombieCtrl.IsAlive);

            // Tick 1
            zombieCtrl.UpdateResurrectTick();
            Assert.AreEqual(2, zombieCtrl.ResurrectTimer);
            Assert.IsFalse(zombieCtrl.IsAlive);

            // Tick 2 (hits timer <= 1, triggers Resurrect)
            zombieCtrl.UpdateResurrectTick();
            Assert.IsTrue(zombieCtrl.IsAlive, "Zombie must resurrect when resurrect timer expires");
            Assert.AreEqual(100f, zombieCtrl.CurrentHealth);
            Assert.AreEqual(300, zombieCtrl.ResurrectTimer);
        }
    }
}
