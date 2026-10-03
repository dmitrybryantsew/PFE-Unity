using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Magic;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// A plain <see cref="ISpellCaster"/>. Records the two mana halves separately, because the whole
    /// point of the split in <see cref="LiveSpellHost"/> is that they arrive one at a time.
    /// </summary>
    internal sealed class FakeCaster : ISpellCaster
    {
        public bool IsPlayer = true;
        public int Rat;
        public int SpellsPossible = 1;
        public bool AtkPossible = true;
        public bool RespectFlag;

        public float Mana = 1000f;
        public float ManaHp = 1000f;
        public float AllDManaMult = 1f;
        public float WarlockDManaMult = 1f;
        public float SpellDown = 1f;
        public float SpellPower = 1f;
        public float TelePower = 1f;
        public float MagicX = 10f;
        public float MagicY = 20f;
        public float AlicornShitHp = 2000f;
        public int CrystalCooldown;

        // Recorded calls.
        public float PoolSpent;
        public float OrganSpent;
        public readonly List<(string id, float value)> Effects = new List<(string, float)>();
        public float ShitHp = float.NaN;

        bool ISpellCaster.IsPlayer => IsPlayer;
        int ISpellCaster.Rat => Rat;
        int ISpellCaster.SpellsPossible => SpellsPossible;
        bool ISpellCaster.AtkPossible => AtkPossible;
        bool ISpellCaster.WeaponRespect(string spellId) => RespectFlag;

        float ISpellCaster.Mana => Mana;
        float ISpellCaster.ManaHp => ManaHp;
        float ISpellCaster.AllDManaMult => AllDManaMult;
        float ISpellCaster.WarlockDManaMult => WarlockDManaMult;
        float ISpellCaster.SpellDown => SpellDown;
        float ISpellCaster.SpellPower => SpellPower;
        float ISpellCaster.TelePower => TelePower;

        void ISpellCaster.SpendMana(float poolCost, float organCost)
        {
            PoolSpent += poolCost;
            OrganSpent += organCost;
        }

        float ISpellCaster.MagicX => MagicX;
        float ISpellCaster.MagicY => MagicY;

        void ISpellCaster.AddEffect(string effectId, float value) => Effects.Add((effectId, value));
        void ISpellCaster.SetShitHp(float value) => ShitHp = value;
        float ISpellCaster.AlicornShitHp => AlicornShitHp;

        int ISpellCaster.CrystalCooldown
        {
            get => CrystalCooldown;
            set => CrystalCooldown = value;
        }
    }

    /// <summary>A plain <see cref="ISpellWorld"/>. Records the ray so the test can check the endpoints.</summary>
    internal sealed class FakeSpellWorld : ISpellWorld
    {
        public bool Alicorn;
        public bool Blocked;

        public readonly List<(float fromX, float fromY, float toX, float toY)> Rays =
            new List<(float, float, float, float)>();
        public readonly List<string> Sounds = new List<string>();
        public readonly List<string> InfoTexts = new List<string>();
        public readonly List<string> Bulbs = new List<string>();

        bool ISpellWorld.Alicorn => Alicorn;

        bool ISpellWorld.IsBlocked(float fromX, float fromY, float toX, float toY)
        {
            Rays.Add((fromX, fromY, toX, toY));
            return Blocked;
        }

        void ISpellWorld.PlaySound(string soundId, float x, float y) => Sounds.Add(soundId);
        void ISpellWorld.ShowInfoText(string key) => InfoTexts.Add(key);
        void ISpellWorld.ShowInfoText(string key, float number) => InfoTexts.Add(key + ":" + number);
        void ISpellWorld.ShowBulb(float x, float y) => Bulbs.Add(x + "," + y);
    }

    /// <summary>
    /// The production <see cref="ISpellHost"/>, and — through it — the whole cast path end to end.
    ///
    /// <para><b>Why this fixture is not editor-only.</b> The adapter's collaborators are two
    /// interfaces, so a fake stands in for each and the mapping runs offline. That is the entire reason
    /// <see cref="ISpellHost"/> was split into <see cref="ISpellCaster"/> and <see cref="ISpellWorld"/>
    /// rather than pointed at the live unit: a <c>MonoBehaviour</c> in the constructor would have made
    /// every test below die in its arrange step.</para>
    /// </summary>
    [TestFixture]
    public class LiveSpellHostTests
    {
        private FakeCaster _caster;
        private FakeSpellWorld _world;
        private ISpellHost _host;

        [SetUp]
        public void SetUp()
        {
            _caster = new FakeCaster();
            _world = new FakeSpellWorld();
            _host = new LiveSpellHost(_caster, _world);
        }

        // ── the mapping ────────────────────────────────────────────────────────────────────────

        [Test]
        public void TheCastersOwnState_IsForwardedUnchanged()
        {
            _caster.Rat = 2;
            _caster.SpellsPossible = 0;
            _caster.AtkPossible = false;
            _caster.Mana = 321f;
            _caster.ManaHp = 654f;
            _caster.AllDManaMult = 1.5f;
            _caster.WarlockDManaMult = 0.5f;
            _caster.SpellDown = 0.25f;
            _caster.SpellPower = 3f;
            _caster.TelePower = 7f;
            _caster.MagicX = 111f;
            _caster.MagicY = 222f;
            _caster.AlicornShitHp = 2000f;
            _caster.IsPlayer = false;
            _caster.RespectFlag = true;

            Assert.IsFalse(_host.IsPlayer);
            Assert.AreEqual(2, _host.Rat);
            Assert.AreEqual(0, _host.SpellsPossible);
            Assert.IsFalse(_host.AtkPossible);
            Assert.AreEqual(321f, _host.Mana, 1e-4f);
            Assert.AreEqual(654f, _host.ManaHp, 1e-4f);
            Assert.AreEqual(1.5f, _host.AllDManaMult, 1e-4f);
            Assert.AreEqual(0.5f, _host.WarlockDManaMult, 1e-4f);
            Assert.AreEqual(0.25f, _host.SpellDown, 1e-4f);
            Assert.AreEqual(3f, _host.SpellPower, 1e-4f);
            Assert.AreEqual(7f, _host.TelePower, 1e-4f);
            Assert.AreEqual(111f, _host.MagicX, 1e-4f);
            Assert.AreEqual(222f, _host.MagicY, 1e-4f);
            Assert.AreEqual(2000f, _host.AlicornShitHp, 1e-4f);
            Assert.IsTrue(_host.WeaponRespect("sp_slow"));
        }

        [Test]
        public void AlicornComesFromTheWorld_NotTheCaster()
        {
            // `World.w.alicorn` is a global, so it must not be read off the unit — a caster that
            // answered it would make alicorn mode per-unit, which is a different game.
            _world.Alicorn = true;
            Assert.IsTrue(_host.Alicorn);

            _world.Alicorn = false;
            Assert.IsFalse(_host.Alicorn);
        }

        [Test]
        public void IsLineVisible_IsTheNegationOfTheWorldsBlockedTest_WithTheEndpointsUnchanged()
        {
            _caster.MagicX = 40f;
            _caster.MagicY = 60f;

            _world.Blocked = true;
            Assert.IsFalse(_host.IsLineVisible(40f, 60f, 400f, 600f));
            Assert.AreEqual(1, _world.Rays.Count);
            Assert.AreEqual((40f, 60f, 400f, 600f), _world.Rays[0],
                "the four coordinates are passed through untouched — a swapped pair would silently " +
                "test a different segment");

            _world.Blocked = false;
            Assert.IsTrue(_host.IsLineVisible(40f, 60f, 400f, 600f));
        }

        [Test]
        public void SpendMana_AndDamageManaOrgan_AreTheTwoHalvesOfManaSpell()
        {
            // AS3 `manaSpell(param1, param2)` does both in one statement, but `Spell.cast()` reaches it
            // as two calls with different amounts. Each must land in its own slot — swapping them would
            // debit the budget by the organ cost and wound the organ by the budget cost, which is
            // invisible at the default multipliers of 1.
            _host.SpendMana(500f);
            _host.DamageManaOrgan(30f);

            Assert.AreEqual(500f, _caster.PoolSpent, 1e-4f);
            Assert.AreEqual(30f, _caster.OrganSpent, 1e-4f);
        }

        [Test]
        public void CrystalCooldown_RoundTripsToTheCaster()
        {
            // `gg.t_cryst` is per-player state, so it cannot live on the Spell. Reading it must see the
            // caster's value and writing it must land there.
            _caster.CrystalCooldown = 5;
            Assert.AreEqual(5, _host.CrystalCooldown);

            _host.CrystalCooldown = 0;
            Assert.AreEqual(0, _caster.CrystalCooldown);
        }

        [Test]
        public void TheEffectSurface_ReachesTheCaster()
        {
            _host.AddEffect("inhibitor", 200f);
            _host.SetShitHp(150f);

            Assert.AreEqual(1, _caster.Effects.Count);
            Assert.AreEqual(("inhibitor", 200f), _caster.Effects[0]);
            Assert.AreEqual(150f, _caster.ShitHp, 1e-4f);
        }

        [Test]
        public void TheFeedback_ReachesTheWorld()
        {
            _host.PlaySound("nomagic", 1f, 2f);
            _host.ShowInfoText("noSpells");
            _host.ShowInfoText("spellCuld", 3f);
            _host.ShowBulb(4f, 5f);

            Assert.AreEqual(new[] { "nomagic" }, _world.Sounds);
            Assert.AreEqual(new[] { "noSpells", "spellCuld:3" }, _world.InfoTexts,
                "the two infoText overloads must not collapse into one");
            Assert.AreEqual(new[] { "4,5" }, _world.Bulbs);
        }

        // ── end to end: the real Spell through the real rules ───────────────────────────────────

        [Test]
        public void CastingThroughTheAdapter_SpendsBothHalvesAndRunsTheEffect()
        {
            // sp_slow's own row (AllData.as:4008): magic 500, mana 30, rad 200, culd 10.
            var data = new SpellData
            {
                hp = 60f, magic = 500f, mana = 30f, culd = 10f, rad = 200f, snd = "slow",
            };
            var spell = new Spell("sp_slow", data, _host);

            SpellCastResult result = spell.Cast(cx: 100f, cy: 200f);

            Assert.IsTrue(result.Cast);
            Assert.AreEqual(SpellCastRefusal.None, result.Refusal);
            Assert.AreEqual(500f, _caster.PoolSpent, 1e-4f, "the budget half");
            Assert.AreEqual(30f, _caster.OrganSpent, 1e-4f, "the organ half");
            Assert.AreEqual(1, _caster.Effects.Count);
            Assert.AreEqual(("inhibitor", 200f), _caster.Effects[0], "rad 200 * power 1");
            Assert.AreEqual(300, spell.CooldownTicks, "culd 10 * 30 fps, rounded");
            Assert.AreEqual(new[] { "slow" }, _world.Sounds);
        }

        [Test]
        public void CastingThroughTheAdapter_AsksTheWorldForLineOfSight_FromTheMagicOriginToTheTarget()
        {
            // sp_gwall is a `line='1'` row (AllData.as:4015), so the LOS gate runs. Its effect is not
            // ported yet, which is why the "clear" control below ends at EffectNotPorted rather than at
            // success — the point of the pair is that the LOS gate is the only thing that changed.
            var data = new SpellData
            {
                hp = 100f, dam = 10f, magic = 500f, mana = 50f, culd = 6f,
                dist = 300f, line = true, atk = true, snd = "mwall",
            };
            _caster.MagicX = 40f;
            _caster.MagicY = 60f;

            _world.Blocked = true;
            var blocked = new Spell("sp_gwall", data, _host).Cast(cx: 340f, cy: 60f);
            Assert.IsFalse(blocked.Cast);
            Assert.AreEqual(SpellCastRefusal.NoLineOfSight, blocked.Refusal);
            Assert.AreEqual((40f, 60f, 340f, 60f), _world.Rays[0],
                "the ray runs magicX/magicY -> the aim point, and is asked before the range clamp");

            _world.Blocked = false;
            var clear = new Spell("sp_gwall", data, _host).Cast(cx: 340f, cy: 60f);
            Assert.AreNotEqual(SpellCastRefusal.NoLineOfSight, clear.Refusal,
                "with the way clear the cast gets past the LOS gate");
            Assert.AreEqual(SpellCastRefusal.EffectNotPorted, clear.Refusal);
        }

        [Test]
        public void AlicornMode_RefusesThroughTheAdapter_AndIsSilent()
        {
            // The world flag reaches the gate through the adapter, and the refusal it produces is one
            // of the two AS3 leaves silent (`Spell.as:193-196`). A test that only asserted the refusal
            // would not notice a later "helpful" message being added.
            _world.Alicorn = true;
            var data = new SpellData { magic = 500f, mana = 30f, culd = 10f, rad = 200f, snd = "slow" };

            SpellCastResult result = new Spell("sp_slow", data, _host).Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.Alicorn, result.Refusal);
            Assert.AreEqual(0f, _caster.PoolSpent);
            Assert.AreEqual(0, _world.InfoTexts.Count, "alicorn refusals are silent in the oracle");
            Assert.AreEqual(0, _world.Sounds.Count);
        }

        // ── construction guards ────────────────────────────────────────────────────────────────

        [Test]
        public void ANullCollaborator_IsRejectedAtConstruction()
        {
            // A null here would surface as a NullReferenceException inside a cast, i.e. mid-combat and
            // far from the wiring that got it wrong.
            Assert.Throws<System.ArgumentNullException>(() => new LiveSpellHost(null, _world));
            Assert.Throws<System.ArgumentNullException>(() => new LiveSpellHost(_caster, null));
        }
    }
}
