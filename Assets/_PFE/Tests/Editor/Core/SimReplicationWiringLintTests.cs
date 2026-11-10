using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.3 (F-05 closure / F-06): the <b>wiring</b> guards for entity identity and snapshot
    /// projection.
    ///
    /// <para><b>Why a lint and not a behavioural test.</b> Every one of these claims is about a
    /// <c>MonoBehaviour</c> being handed a value by another component at construction, and this project's
    /// harness cannot construct a <c>MonoBehaviour</c> at all — <c>AddComponent</c> throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before the first
    /// assertion. The arithmetic behind these calls is pinned by <c>EntityLifetimeTests</c> and
    /// <c>SimSnapshotProjectionRulesTests</c>; what is left, and what actually breaks in practice, is
    /// whether the call is <i>there</i>. A missing hand-over is silent: the field stays empty, every
    /// offline fixture still passes, and the symptom appears as an anonymous entity in a live session.</para>
    ///
    /// <para><b>What the pre-3.3 shape looked like, for calibration.</b> <c>ProjectileSpawner.cs</c> had
    /// zero occurrences of <c>EntityId</c>; no controller held an id; and
    /// <c>RuntimeEntityIdContext.Registry</c> had no production reader at all. Each assertion below fails
    /// against that tree, which is the only thing that makes a presence lint worth having.</para>
    /// </summary>
    [TestFixture]
    public class SimReplicationWiringLintTests
    {
        private const string UnitControllerPath = "Entities/Units/UnitController.cs";
        private const string UnitSpawnerPath = "Systems/Map/Rendering/RoomUnitSpawner.cs";
        private const string ProjectilePath = "Entities/Weapons/Projectile.cs";
        private const string ThrownObjectPath = "Entities/Weapons/ThrownObject.cs";
        private const string MineObjectPath = "Entities/Weapons/MineObject.cs";
        private const string ProjectileFactoryPath = "Systems/Combat/ProjectileFactory.cs";
        private const string ProjectileSpawnerPath = "Systems/Weapons/ProjectileSpawner.cs";
        private const string ProjectorPath = "Systems/Replication/SimProjectileSnapshotProjector.cs";
        private const string ScopePath = "Core/Installers/GameLifetimeScope.cs";

        private static string Read(string path)
        {
            return SourceLint.ReadStripped(path);
        }

        private static void AssertContains(string source, string required, string why)
        {
            Assert.That(source, Does.Contain(required), why);
        }

        private static void AssertCount(string source, string needle, int expected, string why)
        {
            int count = 0;
            int index = 0;

            while ((index = source.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            Assert.AreEqual(expected, count, why);
        }

        /// <summary>
        /// Control for the whole fixture: the predicate above must reject a source that lacks the call.
        /// Without this, a typo in <c>needle</c> makes every assertion a no-op that passes.
        /// </summary>
        [Test]
        public void Control_AssertContains_RejectsASourceWithoutTheCall()
        {
            Assert.Throws<AssertionException>(
                () => AssertContains("void Spawn() { }", "AssignEntityId(", "control"),
                "A presence check that cannot fail is not a guard.");

            Assert.DoesNotThrow(() => AssertContains("void Spawn() { c.AssignEntityId(x); }", "AssignEntityId(", "control"));
        }

        // ── Controllers hold an id ───────────────────────────────────────────────────────────

        /// <summary>
        /// F-05's second gap, verbatim: "no controller holds an EntityId field". A unit's id has to be
        /// reachable from the component, because that is all a damage event or a debug overlay has.
        /// </summary>
        [Test]
        public void UnitController_HoldsAndStampsAnEntityId()
        {
            string source = Read(UnitControllerPath);

            AssertContains(source, "public EntityId EntityId", 
                "UnitController must expose the record's id; the projector, damage attribution and the "
                + "debug overlay all have a controller and nothing else.");

            string assign = SourceLint.MethodBody(source, "public void AssignEntityId(");
            AssertContains(assign, "EntityHash",
                "Assigning an id must also stamp SimUnitState.EntityHash, or UnitState and EntityId "
                + "would be two answers to one question.");

            string apply = SourceLint.MethodBody(source, "public void ApplyUnitState(");
            AssertContains(apply, "EntityHash",
                "ApplyUnitState must re-assert the hash: a restored state carries EntityHash 0 and would "
                + "otherwise blank the identity while the property still reported it.");
        }

        /// <summary>
        /// The hand-over point. <c>RefreshAll</c> registers the record after <c>Spawn</c> returns, so the
        /// spawner is the only code that can stamp the controller — there is no path back from a
        /// controller to its record.
        /// </summary>
        [Test]
        public void RoomUnitSpawner_StampsTheControllerFromTheRecord()
        {
            string source = Read(UnitSpawnerPath);
            string spawn = SourceLint.MethodBody(source, "Spawn(UnitInstance unit)");

            AssertContains(spawn, "RuntimeEntityIdContext.TryRegister(unit",
                "Spawn must register the record before describing it — TryDescribe answers only for a "
                + "record the context has seen.");

            AssertContains(spawn, "AssignEntityId(",
                "Spawn must hand the id to the controller it just built; nothing else can.");
        }

        // ── Projectile-like controllers hold an id ───────────────────────────────────────────

        /// <summary>
        /// F-05's first gap, verbatim: "<c>ProjectileSpawner.cs</c> still has 0 EntityId occurrences".
        /// Three entity kinds are covered here because all three are live, all three have no room record,
        /// and all three are therefore invisible to the record-keyed half of the registry.
        /// </summary>
        [Test]
        public void ProjectileLikeControllers_EachHoldAnEntityId()
        {
            AssertContains(Read(ProjectilePath), "public EntityId EntityId",
                "Projectile must expose its id — it is the registered entity for a shot.");

            AssertContains(Read(ThrownObjectPath), "public EntityId EntityId",
                "ThrownObject must expose its id.");

            AssertContains(Read(MineObjectPath), "public EntityId EntityId",
                "MineObject must expose its id.");
        }

        /// <summary>
        /// Minting happens at the two spawn boundaries and nowhere else. The factory is the single
        /// projectile point (both <c>Create</c> overloads funnel through <c>Spawn</c>); the spawner covers
        /// thrown objects and mines, which do not go through the factory.
        /// </summary>
        [Test]
        public void SpawnBoundaries_MintTheId()
        {
            string factory = SourceLint.MethodBody(Read(ProjectileFactoryPath), "Spawn(Projectile prefab");
            AssertContains(factory, "AllocateRuntimeId()",
                "ProjectileFactory.Spawn is the one place a projectile is created; minting anywhere else "
                + "leaves the low-level Create overload anonymous.");
            AssertContains(factory, "AssignEntityId(",
                "Minting without handing the id over produces an id nobody holds.");

            string spawner = Read(ProjectileSpawnerPath);
            AssertCount(spawner, "AssignEntityId(", 2,
                "ProjectileSpawner must stamp both the thrown object and the mine; one of the two "
                + "missing is exactly the shape of the F-05 gap this lint closes.");
            AssertContains(spawner, "AllocateRuntimeId()",
                "The spawner mints for the two kinds the factory does not build.");
        }

        /// <summary>
        /// A despawn has to leave the registry. A leaked entry is not harmless: the projector enumerates
        /// the registry every tick, so a destroyed component would be projected forever — and the
        /// registry would grow by one entry per shot for the rest of the session.
        /// </summary>
        [Test]
        public void DespawnPaths_Unregister()
        {
            AssertContains(
                SourceLint.MethodBody(Read(ProjectilePath), "public void ResetProjectile("),
                "RuntimeEntityIdContext.Unregister(this)",
                "ResetProjectile is the pool's release hook; a parked round is not a live entity.");

            AssertContains(
                SourceLint.MethodBody(Read(ThrownObjectPath), "private void OnDestroy("),
                "RuntimeEntityIdContext.Unregister(this)",
                "A destroyed throwable must leave the registry.");

            AssertContains(
                SourceLint.MethodBody(Read(MineObjectPath), "private void OnDestroy("),
                "RuntimeEntityIdContext.Unregister(this)",
                "A destroyed mine must leave the registry.");
        }

        // ── The registry finally has a production reader ─────────────────────────────────────

        /// <summary>
        /// F-05's third gap, verbatim: "<c>RuntimeEntityIdContext.Registry</c> has zero production
        /// callers — no snapshot/ownership/replication code reads the registry yet." The projector's
        /// <c>SimTick</c> is that reader.
        /// </summary>
        [Test]
        public void Projector_ReadsTheRegistryEveryTick()
        {
            string source = Read(ProjectorPath);
            string tick = SourceLint.MethodBody(source, "public void SimTick(int tickIndex)");

            AssertContains(tick, "CollectProjectiles(",
                "SimTick must actually collect; a projector that builds an empty snapshot every tick "
                + "would satisfy every other assertion here and replicate nothing.");

            string collect = SourceLint.MethodBody(source, "private void CollectProjectiles(");
            AssertContains(collect, "Registry.GetAll()",
                "CollectProjectiles is the production reader of IEntityRegistry.GetAll() — the specific "
                + "call F-05 reported as having no production caller.");
            // Was "is Projectile" before 3.3 added the other two transient kinds. The filter is now the
            // contract, because a concrete-type test here is exactly what kept thrown objects and mines
            // off the wire; EveryTransientKind_IsProjectedThroughOneContract pins the absent half.
            AssertContains(collect, "is ISimTransientStateSource",
                "The registry holds every live entity; the projector must filter to the transients it "
                + "projects, and to all of them.");
        }

        /// <summary>
        /// The snapshot must be produced from live state, with the header fields the guards consume. A
        /// projector that filled only <c>TickIndex</c> would produce a payload no guard could order.
        /// </summary>
        [Test]
        public void Projector_FillsTheStalenessHeader()
        {
            string source = Read(ProjectorPath);
            string tick = SourceLint.MethodBody(source, "public void SimTick(int tickIndex)");

            AssertContains(tick, "NextSnapshotSequence(",
                "SnapshotSequence must advance through the wrap-safe rule.");
            AssertContains(tick, "NextWorldRevision(",
                "WorldRevision must advance through the room-set rule, not unconditionally.");
            AssertContains(tick, "ProtocolVersion",
                "A snapshot without the protocol version is rejected by every peer.");

            AssertContains(source, "SimTickOrder.ViewAligned",
                "The projection must run in the last slot; sampling earlier reports a round that has "
                + "already hit something as still in flight.");
        }

        /// <summary>
        /// Registered as a tick, not merely as a singleton. This is the exact silent failure
        /// <c>SimTickRegistrar</c> exists to prevent: a container-registered service that is constructed,
        /// injected and never called.
        /// </summary>
        [Test]
        public void Projector_IsRegisteredAsATick()
        {
            string scope = Read(ScopePath);

            int registration = scope.IndexOf("SimProjectileSnapshotProjector>(Lifetime.Singleton)", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(registration, 0,
                "The projector must be registered at all; without it nothing produces a snapshot and "
                + "the registry has no production reader again.");

            string tail = scope.Substring(registration, System.Math.Min(400, scope.Length - registration));
            AssertContains(tail, "As<IAutoRegisteredSimTickable>()",
                "The registration must carry the auto-registered marker, or SimTickRegistrar never puts "
                + "it on SimLoop and the projector silently never ticks.");
        }

        // ── Units are projected too (the row-3.3 hole) ───────────────────────────────────────

        /// <summary>
        /// The projector emitted projectiles only, and the reason was structural: the identity registry
        /// is keyed on the <c>UnitInstance</c> record, so nothing walked from a record to its controller.
        /// <c>UnitViewRegistry</c> is that walk, and the two halves have to be connected in both
        /// directions — the projector must read the list, and the lifecycle must fill it.
        /// </summary>
        [Test]
        public void Projector_ProjectsUnitsThroughTheLiveViewList()
        {
            string projector = Read(ProjectorPath);

            AssertContains(SourceLint.MethodBody(projector, "public void SimTick(int tickIndex)"),
                "CollectUnits(",
                "SimTick must collect units as well as projectiles; a snapshot with no units replicates "
                + "nothing a client can render.");

            string collect = SourceLint.MethodBody(projector, "private void CollectUnits(");
            AssertContains(collect, "UnitViewRegistry.Live",
                "CollectUnits must read the live-view list; the registry cannot answer for units.");
            AssertContains(collect, "HasEntityId",
                "A controller is in the list before the spawner stamps it, so the collector must skip "
                + "the anonymous ones rather than emit EntityHash 0.");
        }

        /// <summary>
        /// The list is only correct if the lifecycle fills it. <c>OnEnable</c> rather than the spawner,
        /// because a pooled or re-enabled unit must come back.
        /// </summary>
        [Test]
        public void UnitViewRegistry_IsFilledFromTheControllerLifecycle()
        {
            string source = Read(UnitControllerPath);

            AssertContains(SourceLint.MethodBody(source, "protected virtual void OnEnable("),
                "UnitViewRegistry.Register(this)",
                "A live unit must be in the projector's list; without this every snapshot has zero units.");

            AssertContains(SourceLint.MethodBody(source, "protected virtual void OnDestroy("),
                "UnitViewRegistry.Unregister(this)",
                "A destroyed unit left in the list is a fake-null the projector would project every "
                + "tick — the same failure SimLoop's unregister exists for.");
        }

        // ── DefinitionId is produced, not 0 ──────────────────────────────────────────────────

        /// <summary>
        /// The row-3.3 hole that mattered most for correctness: a replicated <c>DefinitionId</c> of 0 is
        /// indistinguishable from "the first definition", so every unit and every round would render as
        /// whatever sorts first. The intern table exists so the field can be real; these are the four
        /// links in that chain.
        /// </summary>
        [Test]
        public void DefinitionId_IsInternedAndStampedOnBothEntityKinds()
        {
            string scope = Read(ScopePath);
            AssertContains(scope, "ContentInternContext.SetSources(",
                "The tables must be pushed from the real content registry, or every key is Uninterned.");

            string spawner = SourceLint.MethodBody(Read(UnitSpawnerPath), "Spawn(UnitInstance unit)");
            AssertContains(spawner, "AssignDefinitionId(",
                "A spawned unit must be stamped with its interned definition id.");
            AssertContains(spawner, "ContentInternContext.Units",
                "...from the unit table, not the weapon one.");

            string factory = SourceLint.MethodBody(Read(ProjectileFactoryPath), "Create(WeaponDefinition weapon");
            AssertContains(factory, "SetDefinitionId(",
                "A fired round must carry its weapon's interned id; the factory is the only place that "
                + "holds the definition.");

            AssertContains(SourceLint.MethodBody(Read(UnitControllerPath), "public void AssignDefinitionId("),
                "_sim.DefinitionId",
                "The stamp must reach the live record, or CaptureSimState projects the previous value.");

            AssertContains(SourceLint.MethodBody(Read(ProjectilePath), "public SimProjectileState CaptureSimState("),
                "DefinitionId = DefinitionId",
                "The capture must carry the stamped id, not a literal 0.");
        }

        // ── Every transient kind reaches the wire ────────────────────────────────────────────

        /// <summary>
        /// The hole named after 3.3: <i>"Thrown objects and mines have no wire record yet."</i> They
        /// held a host-minted id that <b>nothing ever read</b>, so a client would never see a grenade
        /// leave a hand or a mine appear on a floor — and no fixture could notice, because the ids were
        /// being minted correctly.
        ///
        /// <para><b>The absent control is the load-bearing assertion.</b> A projector that collects
        /// with <c>is Projectile</c> compiles, runs, passes every arithmetic test, and silently drops two
        /// of the three kinds. So the check is not only that the contract is named, but that the
        /// concrete-type test is <i>gone</i>.</para>
        /// </summary>
        [Test]
        public void EveryTransientKind_IsProjectedThroughOneContract()
        {
            string[] kinds = { ProjectilePath, ThrownObjectPath, MineObjectPath };

            foreach (string kindPath in kinds)
            {
                AssertContains(Read(kindPath), "ISimTransientStateSource",
                    kindPath + " must implement the transient contract, or the projector cannot see it.");
                // The needle is the constant's namespace, not "Kind =": the record's initialisers are
                // column-aligned, so the space between the name and the '=' varies with the longest
                // field name in the block. Pinning spacing is a test that breaks on a rename.
                AssertContains(SourceLint.MethodBody(Read(kindPath), "public SimProjectileState CaptureSimState("),
                    "SimTransientKind.",
                    kindPath + " must stamp its kind, or the receiver applies the wrong behaviour to it.");
            }

            string collect = SourceLint.MethodBody(Read(ProjectorPath), "private void CollectProjectiles()");

            AssertContains(collect, "is ISimTransientStateSource",
                "The registry walk must select by the contract. The registry holds every transient kind, "
                + "and a concrete-type test keeps the two the owner named invisible.");

            Assert.That(collect, Does.Not.Contain("is Projectile "),
                "A concrete-type test in the registry walk drops thrown objects and mines. This is the "
                + "exact shape of the defect, so its absence is the assertion.");

            // Each kind must name its OWN constant: three records that all stamp Projectile would pass
            // a presence check while telling a client that a mine is a fired round.
            AssertContains(SourceLint.MethodBody(Read(ThrownObjectPath), "public SimProjectileState CaptureSimState("),
                "SimTransientKind.ThrownObject", "A throwable must not be emitted as a fired round.");
            AssertContains(SourceLint.MethodBody(Read(MineObjectPath), "public SimProjectileState CaptureSimState("),
                "SimTransientKind.Mine", "A mine must not be emitted as a fired round.");

            // And the round itself must stamp, not lean on the record's default. Relying on the default
            // is what makes a presence-only field: it is correct until somebody reorders the struct.
            AssertContains(SourceLint.MethodBody(Read(ProjectilePath), "public SimProjectileState CaptureSimState("),
                "SimTransientKind.Projectile",
                "A fired round must stamp its kind explicitly; the record's default is a fallback for "
                + "hand-built records, not a production path.");
        }

        /// <summary>
        /// A transient record is only drawable if it says <i>which</i> weapon it is, and the spawner is
        /// the only place that holds the definition for a throw or a placement — the same reason the
        /// factory stamps a fired round.
        /// </summary>
        [Test]
        public void ThrownObjectsAndMines_AreStampedWithTheirWeapon()
        {
            string spawner = Read(ProjectileSpawnerPath);

            AssertContains(SourceLint.MethodBody(spawner, "private void SpawnThrownObject("),
                "SetDefinitionId(",
                "A thrown object with no definition id renders as whatever key 0 resolves to.");

            AssertContains(SourceLint.MethodBody(spawner, "private void SpawnMineOrDetonate("),
                "SetDefinitionId(",
                "A placed mine with no definition id is invisible to the art table.");

            AssertCount(spawner, "ContentInternContext.Weapons",
                2,
                "Both transient kinds resolve against the WEAPON table — a throwable's art is the "
                + "weapon's own (WThrow.as:38 vBullet = vWeapon), and a mine is placed by a weapon.");
        }
    }
}
