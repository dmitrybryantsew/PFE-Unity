using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins AS3's <c>sost == 3</c> — a dead unit is a <b>body in the world</b>, not a no-op.
    ///
    /// <para><b>What was broken, so a future reader knows what these assertions are defending.</b>
    /// The port had no <c>sost</c> at all and used <c>IsAlive</c> as the stand-in for two different
    /// oracle states: <c>sost &gt;= 3</c> ("not a valid target") and <c>sost == 4</c> ("removed from
    /// the world"). Every damage source gated on it, so a corpse could not be damaged, could not be
    /// penetrated, and — because the projectile's stop path is unconditional — ate an unlimited
    /// number of rounds. The single predicate is now two: <see cref="UnitController.IsAlive"/> for
    /// behaviour and <see cref="UnitController.CanBeHit"/> for "may a round interact with this".
    /// See <c>docs/AUDIT_corpse_damage_and_piercing_2026-10-18.md</c>.</para>
    ///
    /// <para><b>Why these are EditMode tests and not pure math.</b> The overkill arithmetic is pinned
    /// separately in <see cref="CorpseDamageMathTests"/>; what is left here is the <i>state machine</i>
    /// — who leaves a body, what a body answers to a damage source, and when it stops answering. That
    /// lives on <c>UnitController</c>, so the fixture has to build one. <c>Awake</c>/<c>OnEnable</c> do
    /// not run for <c>AddComponent</c> in EditMode, which is why <see cref="UnitController.Initialize"/>
    /// is called explicitly and why the rigidbody and collider are added by hand: <c>Initialize</c>
    /// resolves them and dereferences the rigidbody without a null check.</para>
    /// </summary>
    [TestFixture]
    public class CorpseLifecycleTests
    {
        private GameObject _go;
        private TestUnit _unit;
        private UnitStats _stats;
        private UnitDefinition _definition;
        private Collider2D _collider;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("CorpseLifecycleTestUnit");

            // Initialize() -> ResolvePhysicsComponents() writes _rb.bodyType unconditionally, so a
            // bare GameObject would throw in setup rather than in an assertion.
            _go.AddComponent<Rigidbody2D>();
            _collider = _go.AddComponent<BoxCollider2D>();

            _unit = _go.AddComponent<TestUnit>();

            _stats = new UnitStats(100f, 0f);

            _definition = ScriptableObject.CreateInstance<UnitDefinition>();
            _definition.bloodType = BloodType.Red;
            _definition.leavesCorpse = true;

            _unit.Initialize(_definition, _stats);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_definition != null) Object.DestroyImmediate(_definition);
        }

        /// <summary>
        /// A <c>UnitController</c> that can be driven without a scene: no loot table (so the drop path
        /// never reaches <c>AllData</c>), a death counter, and a switch for the player's game-over path.
        /// </summary>
        private sealed class TestUnit : UnitController
        {
            public int DeathCalls;
            public bool AsPlayer;

            public override bool IsPlayer => AsPlayer;

            // The loot path resolves through LootRuntime -> Resources/AllData, which is a different
            // subsystem with its own fixture. Dropping it here keeps a corpse test from failing on a
            // loot-table parse.
            protected override bool DropsLootOnDeath => false;

            protected override void OnDeath()
            {
                DeathCalls++;
                base.OnDeath();
            }
        }

        /// <summary>
        /// Kills the unit with exactly its max HP, so the death blow itself carries <b>zero</b> overkill
        /// (<c>Unit.as:3699</c>: <c>hp -= param1</c> lands on 0, not below it). That is the ordinary
        /// case: a body is left behind.
        /// </summary>
        private void KillUnit() => _unit.TakeDamage(_stats.MaxHp.Value);

        private void TickUntilRemoved()
        {
            for (int i = 0; i < CorpseDamageMath.DissolveFrames; i++)
            {
                _unit.TickCorpse();
            }
        }

        // ── What a death leaves behind (Unit.as:4302-4368) ──────────────────────

        [Test]
        public void Death_LeavesATargetableBody()
        {
            KillUnit();

            Assert.That(_unit.IsAlive, Is.False, "max HP of damage has to kill it");
            Assert.That(_unit.IsCorpse, Is.True, "trup defaults true, so an ordinary unit leaves a body");

            // THE assertion the whole fix hangs on. AS3's bullet filter tests `sost == 4` and nothing
            // else (Bullet.as:513); `sost == 3` passes it. While CanBeHit was IsAlive, every damage
            // source skipped the body entirely.
            Assert.That(_unit.CanBeHit, Is.True,
                "a corpse is sost == 3, which is inside the oracle's sost == 4 filter — a round must " +
                "still be able to hit it.");

            Assert.That(_unit.IsInWorld, Is.True, "sost < 4 — still in loc.units and still drawn");
        }

        [Test]
        public void Corpse_AccumulatesOverkill_ButStaysABody()
        {
            KillUnit();
            _unit.TakeDamage(60f);

            // AS3's hp is unclamped, so this would read -60 there. The port keeps the bar at 0 and
            // banks the part below the line separately — the two are equivalent for the only question
            // the code asks (`hp > -maxhp * 2`).
            Assert.That(_stats.CurrentHp.Value, Is.EqualTo(0f).Within(1e-3f),
                "the bar itself must not go negative; nothing in the port renders a negative HP");
            Assert.That(_stats.Overkill, Is.EqualTo(60f).Within(1e-3f),
                "every point of a hit on a body is body damage");

            Assert.That(_unit.IsCorpse, Is.True, "60 < 2 x 100, so the body is still there");
            Assert.That(_unit.CanBeHit, Is.True);
        }

        [Test]
        public void OverkillPastTwiceMaxHp_StartsTheDissolve()
        {
            KillUnit();
            _unit.TakeDamage(CorpseDamageMath.DestroyOverkillFactor * 100f);

            Assert.That(_stats.CorpseDestroyed, Is.True, "exactly 2 x maxhp reaches the threshold");

            // BloodType.Red, so die() takes `initBurn(4 + blood)` — a 40-frame burn, not an instant
            // removal. It is still a corpse on this frame, which is the part that looks like a bug
            // until you read Unit.as:4347-4353.
            Assert.That(_unit.IsCorpse, Is.True);
            Assert.That(_unit.IsCorpseRemoved, Is.False);
        }

        [Test]
        public void Dissolve_KeepsTheBodySolid_ThenRemovesIt()
        {
            KillUnit();
            _unit.TakeDamage(200f);

            int half = CorpseDamageMath.DissolveFrames / 2;
            for (int i = 0; i < half; i++) _unit.TickCorpse();

            // `initBurn` changes nothing about `sost` or `X1..X2` in the oracle: a burning body is
            // still solid and still shootable for its 40 frames.
            Assert.That(_unit.CanBeHit, Is.True, "a dissolving body is still sost == 3");
            Assert.That(_collider.enabled, Is.True, "and it is still a physics body");

            for (int i = half; i < CorpseDamageMath.DissolveFrames; i++) _unit.TickCorpse();

            // Unit.as:1839-1843 — `burn.vse` true -> exterminate().
            Assert.That(_unit.IsCorpseRemoved, Is.True);
            Assert.That(_unit.CanBeHit, Is.False, "sost == 4 is the state every damage filter excludes");
            Assert.That(_unit.IsInWorld, Is.False);
            Assert.That(_collider.enabled, Is.False,
                "exterminate() -> loc.remObj(this); the port has no unit list, so the collider going " +
                "off is what 'removed' means here.");
        }

        [Test]
        public void BloodlessBody_IsRemovedTheFrameItIsDestroyed()
        {
            _definition.bloodType = BloodType.None;

            KillUnit();
            _unit.TakeDamage(200f);

            // `trup && blood > 0` fails, so die() falls through to `expl(); exterminate()` — no burn,
            // no 40 frames, gone on the spot.
            Assert.That(_unit.IsCorpseRemoved, Is.True);
            Assert.That(_unit.CanBeHit, Is.False);
        }

        [Test]
        public void UnitThatLeavesNoCorpse_IsRemovedAtDeath()
        {
            // `trup='0'` — rat, tarakan, ant (AllData.as). AS3: expl(); exterminate() on the death
            // blow itself, with no body and no death row.
            _definition.leavesCorpse = false;

            KillUnit();

            Assert.That(_unit.IsCorpse, Is.False, "there is no body to shoot");
            Assert.That(_unit.IsCorpseRemoved, Is.True);
            Assert.That(_unit.CanBeHit, Is.False);
        }

        [Test]
        public void PlayerLeavesNoCorpse_AndKeepsItsCollider()
        {
            _unit.AsPlayer = true;

            KillUnit();

            // UnitPlayer.die() is the game-over path, not a body. This is also the trap the fix had
            // to avoid in the other direction: ApplyCorpseDamage must not treat "dead but not a body"
            // as a corpse, or a stray round after death would disable the player's collider.
            Assert.That(_unit.IsCorpse, Is.False);
            Assert.That(_unit.IsCorpseRemoved, Is.False, "the player's death is not an extermination");
            Assert.That(_unit.CanBeHit, Is.False, "and nothing may hit a dead player");
            Assert.That(_collider.enabled, Is.True);
        }

        // ── The once-only death reaction (latent bug the fix would have activated) ──

        [Test]
        public void OnDeath_IsRaisedOnce_AcrossManyCorpseHits()
        {
            KillUnit();
            Assert.That(_unit.DeathCalls, Is.EqualTo(1));

            // AS3 re-enters die() on every hit that lands on a corpse (Unit.as:3713-3745). The port's
            // OnDeath awards XP with no guard of its own — loot was guarded by `_lootDropped`, the XP
            // award was not — so enabling corpse damage without this latch would have paid the player
            // again for every round that hit a body.
            for (int i = 0; i < 10; i++) _unit.TakeDamage(5f);

            Assert.That(_unit.DeathCalls, Is.EqualTo(1),
                "a body that re-raises death re-awards XP on every hit");
            Assert.That(_unit.IsCorpse, Is.True, "50 overkill is well under the 200 threshold");
        }

        // ── The resolved-outcome path (what DamageSystem actually calls) ──────────

        [Test]
        public void ApplyDamage_OnACorpse_SpendsOnlyTheHpHalf()
        {
            KillUnit();

            bool broke = _unit.ApplyDamage(new DamageOutcome(
                hpDamage: 60f,
                armourIntegrityDamage: 25f,
                armourBroke: true));

            Assert.That(broke, Is.False, "a body has no armour left to break");
            Assert.That(_stats.Overkill, Is.EqualTo(60f).Within(1e-3f),
                "only HpDamage reaches a corpse; re-running the wear path would write condition onto " +
                "an item the body is no longer holding");
        }

        // ── Getting back up (boss second life, zombie resurrect) ─────────────────

        [Test]
        public void ReviveFromCorpse_UndoesTheRemoval()
        {
            KillUnit();
            _unit.TakeDamage(200f);
            TickUntilRemoved();

            Assert.That(_unit.CanBeHit, Is.False, "precondition: the body is gone");

            // The order the two real callers use (UnitZombie.as:495-524, UnitBossNecr.as:571-584):
            // write HP, then put the unit back. Heal pays the overkill debt before the bar, so 400
            // clears 200 of grave and fills the 100-point bar.
            _stats.Heal(400f);
            Assert.That(_stats.CurrentHp.Value, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(_stats.CorpseDestroyed, Is.False,
                "Overkill clears itself once HP is positive, so no revive caller has to reset it");

            _unit.ReviveFromCorpse();

            Assert.That(_unit.IsAlive, Is.True);
            Assert.That(_unit.CanBeHit, Is.True,
                "a revive that only wrote HP would leave an invisible, intangible unit that every " +
                "damage source skips — the same bug as the corpse, one state over");
            Assert.That(_collider.enabled, Is.True);
            Assert.That(_unit.IsInWorld, Is.True);
        }

        [Test]
        public void ReviveFromCorpse_IsANoOpOnALivingUnit()
        {
            // Guards the guard: the method early-returns on CorpseState.None, so calling it from a
            // spawn path cannot resurrect-enable something that was never dead.
            _unit.ReviveFromCorpse();

            Assert.That(_unit.IsAlive, Is.True);
            Assert.That(_unit.IsCorpse, Is.False);
            Assert.That(_collider.enabled, Is.True);
        }
    }
}
