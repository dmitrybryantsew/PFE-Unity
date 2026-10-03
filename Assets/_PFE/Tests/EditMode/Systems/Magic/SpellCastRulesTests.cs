using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Magic;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// A plain in-memory <see cref="ISpellHost"/>. No Unity type appears here, which is the whole point
    /// of the seam: the cast path runs offline, so these tests are not editor-only.
    /// </summary>
    internal sealed class FakeSpellHost : ISpellHost
    {
        public bool IsPlayer = true;
        public int Rat;
        public bool Alicorn;
        public int SpellsPossible = 1;
        public bool AtkPossible = true;
        public bool RespectFlag;

        public float Mana = 1000f;
        public float ManaHp = 1000f;
        public float AllDManaMult = 1f;
        public float WarlockDManaMult = 1f;
        public float SpellDown = 1f;

        public float MagicX = 10f;
        public float MagicY = 20f;
        public float SpellPower = 1f;
        public float TelePower = 1f;
        public bool LineOfSight = true;

        public float AlicornShitHp = 2000f;
        public int CrystalCooldown;

        // --- recorded calls, so the tests assert on what the code DID, not on a re-derivation ---
        public readonly List<string> Sounds = new List<string>();
        public readonly List<string> InfoTexts = new List<string>();
        public readonly List<string> Bulbs = new List<string>();
        public readonly List<(string id, float value)> Effects = new List<(string, float)>();
        public float ManaSpent;
        public float ManaOrganDamaged;
        public float ShitHp = float.NaN;

        public bool IsPlayer_ => IsPlayer;
        public int Rat_ => Rat;
        public bool Alicorn_ => Alicorn;
        public int SpellsPossible_ => SpellsPossible;
        public bool AtkPossible_ => AtkPossible;

        bool ISpellHost.IsPlayer => IsPlayer;
        int ISpellHost.Rat => Rat;
        bool ISpellHost.Alicorn => Alicorn;
        int ISpellHost.SpellsPossible => SpellsPossible;
        bool ISpellHost.AtkPossible => AtkPossible;
        bool ISpellHost.WeaponRespect(string spellId) => RespectFlag;

        float ISpellHost.Mana => Mana;
        float ISpellHost.ManaHp => ManaHp;
        float ISpellHost.AllDManaMult => AllDManaMult;
        float ISpellHost.WarlockDManaMult => WarlockDManaMult;
        float ISpellHost.SpellDown => SpellDown;

        void ISpellHost.SpendMana(float amount) { ManaSpent += amount; Mana -= amount; }
        void ISpellHost.DamageManaOrgan(float amount) => ManaOrganDamaged += amount;

        float ISpellHost.MagicX => MagicX;
        float ISpellHost.MagicY => MagicY;
        float ISpellHost.SpellPower => SpellPower;
        float ISpellHost.TelePower => TelePower;
        bool ISpellHost.IsLineVisible(float fromX, float fromY, float toX, float toY) => LineOfSight;

        void ISpellHost.AddEffect(string effectId, float value) => Effects.Add((effectId, value));
        void ISpellHost.SetShitHp(float value) => ShitHp = value;
        float ISpellHost.AlicornShitHp => AlicornShitHp;
        int ISpellHost.CrystalCooldown { get => CrystalCooldown; set => CrystalCooldown = value; }

        void ISpellHost.PlaySound(string soundId, float x, float y) => Sounds.Add(soundId);
        void ISpellHost.ShowInfoText(string key) => InfoTexts.Add(key);
        void ISpellHost.ShowInfoText(string key, float number) => InfoTexts.Add(key + ":" + number);
        void ISpellHost.ShowBulb(float x, float y) => Bulbs.Add(x + "," + y);
    }

    /// <summary>
    /// The pure gate order and cost math of <c>Spell.cast()</c> (<c>Spell.as:184-300</c>). Runs offline.
    /// </summary>
    [TestFixture]
    public class SpellCastRulesTests
    {
        /// <summary>A context that passes every gate, so each test can break exactly one thing.</summary>
        private static SpellCastContext Ctx(
            bool hasEffect = true, bool isPlayer = true, string spellId = "sp_slow", bool atk = false,
            bool alicorn = false, int rat = 0, bool weaponRespect = false, int spellsPossible = 1,
            bool atkPossible = true, int cooldownTicks = 0, float ownerMana = 1000f, float manaHp = 1000f,
            float magic = 100f, float mana = 10f, float allDManaMult = 1f, bool line = false,
            bool hasOwner = true, bool lineOfSight = true)
            => new SpellCastContext(
                hasEffect, isPlayer, spellId, atk, alicorn, rat, weaponRespect, spellsPossible,
                atkPossible, cooldownTicks, ownerMana, manaHp, magic, mana, allDManaMult, line,
                hasOwner, lineOfSight);

        [Test]
        public void ACleanContext_Casts()
        {
            Assert.AreEqual(SpellCastRefusal.None, SpellCastRules.Evaluate(Ctx()));
        }

        [Test]
        public void MissingEffect_IsTheFirstRefusal()
        {
            // `if(this.cf == null) return false;` is the very first line of cast().
            Assert.AreEqual(SpellCastRefusal.NoEffect, SpellCastRules.Evaluate(Ctx(hasEffect: false)));
        }

        [Test]
        public void GateOrder_IsTheOraclesOrder()
        {
            // Every gate broken at once: the FIRST one in the oracle must win, because cast() returns
            // at the first failure. Order: alicorn, rat, respect, spellsPoss, cooldown, overMana, noMana.
            Assert.AreEqual(SpellCastRefusal.Alicorn, SpellCastRules.Evaluate(
                Ctx(alicorn: true, rat: 1, weaponRespect: true, spellsPossible: 0,
                    cooldownTicks: 5, ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.Rat, SpellCastRules.Evaluate(
                Ctx(rat: 1, weaponRespect: true, spellsPossible: 0, cooldownTicks: 5,
                    ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.WeaponRespect, SpellCastRules.Evaluate(
                Ctx(weaponRespect: true, spellsPossible: 0, cooldownTicks: 5,
                    ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.NoSpells, SpellCastRules.Evaluate(
                Ctx(spellsPossible: 0, cooldownTicks: 5, ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.Cooldown, SpellCastRules.Evaluate(
                Ctx(cooldownTicks: 5, ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.OverMana, SpellCastRules.Evaluate(
                Ctx(ownerMana: 0f, manaHp: 0f, mana: 9999f)));

            Assert.AreEqual(SpellCastRefusal.NoMana, SpellCastRules.Evaluate(
                Ctx(manaHp: 0f, mana: 9999f)));
        }

        [Test]
        public void Alicorn_RefusesEverySpellExceptSpMshit()
        {
            // `if(World.w.alicorn && this.id != "sp_mshit") return false;`
            Assert.AreEqual(SpellCastRefusal.Alicorn,
                SpellCastRules.Evaluate(Ctx(alicorn: true, spellId: "sp_slow")));
            Assert.AreEqual(SpellCastRefusal.Alicorn,
                SpellCastRules.Evaluate(Ctx(alicorn: true, spellId: "sp_mwall")));
            Assert.AreEqual(SpellCastRefusal.None,
                SpellCastRules.Evaluate(Ctx(alicorn: true, spellId: "sp_mshit")),
                "sp_mshit is the one exempt id, compared literally");
        }

        [Test]
        public void AtkFlag_GatesOnAtkPossible_ButOnlyWhenTheSpellCarriesAtk()
        {
            // `spellsPoss == 0 || this.atk && !this.gg.atkPoss`
            Assert.AreEqual(SpellCastRefusal.NoSpells,
                SpellCastRules.Evaluate(Ctx(atk: true, atkPossible: false)));
            Assert.AreEqual(SpellCastRefusal.None,
                SpellCastRules.Evaluate(Ctx(atk: false, atkPossible: false)),
                "sp_slow carries no atk, so atkPoss is never consulted");
        }

        [Test]
        public void AnNpc_SkipsTheEntirePlayerBlock_IncludingTheManaCost()
        {
            // Spell.as:222-240 is inside `if(this.player)`. An NPC with zero mana, zero spellsPoss and
            // an active cooldown still casts — the gates simply do not run.
            Assert.AreEqual(SpellCastRefusal.None, SpellCastRules.Evaluate(
                Ctx(isPlayer: false, spellsPossible: 0, cooldownTicks: 99, ownerMana: 0f, manaHp: 0f)));
        }

        [Test]
        public void LineOfSight_IsCheckedForNpcsToo()
        {
            // Spell.as:262 is OUTSIDE `if(this.player)` — only the infoText at :265 is player-only.
            Assert.AreEqual(SpellCastRefusal.NoLineOfSight, SpellCastRules.Evaluate(
                Ctx(isPlayer: false, line: true, lineOfSight: false)));
            Assert.AreEqual(SpellCastRefusal.None, SpellCastRules.Evaluate(
                Ctx(isPlayer: false, line: true, lineOfSight: true)));
            Assert.AreEqual(SpellCastRefusal.None, SpellCastRules.Evaluate(
                Ctx(line: false, lineOfSight: false)), "line=0 never consults LOS");
            Assert.AreEqual(SpellCastRefusal.None, SpellCastRules.Evaluate(
                Ctx(line: true, hasOwner: false, lineOfSight: false)), "no owner -> no LOS test");
        }

        [Test]
        public void PlayerGates_RunBeforeTheLineOfSightCheck()
        {
            // cast() runs the whole `if(this.player)` block (Spell.as:191-240) BEFORE the LOS test at
            // :262. When both would refuse, the EARLIER one is reported — so a spell that is both
            // uncastable and out of sight must not blame the sight. Found missing while choosing a
            // mutation: the LOS tests above all leave the player gates passing, so reordering the two
            // blocks would have gone unnoticed.
            Assert.AreEqual(SpellCastRefusal.NoSpells, SpellCastRules.Evaluate(
                Ctx(spellsPossible: 0, line: true, lineOfSight: false)));
            Assert.AreEqual(SpellCastRefusal.OverMana, SpellCastRules.Evaluate(
                Ctx(ownerMana: 0f, line: true, lineOfSight: false)));
            Assert.AreEqual(SpellCastRefusal.NoMana, SpellCastRules.Evaluate(
                Ctx(manaHp: 0f, mana: 9999f, line: true, lineOfSight: false)));
            Assert.AreEqual(SpellCastRefusal.Cooldown, SpellCastRules.Evaluate(
                Ctx(cooldownTicks: 5, line: true, lineOfSight: false)));
        }

        [Test]
        public void CheckMagicCost_ClampsAt999()
        {
            Assert.AreEqual(999f, SpellCastRules.CheckMagicCost(800f, 2f), 0.001f);
            Assert.AreEqual(999f, SpellCastRules.CheckMagicCost(999f, 1f), 0.001f);
            Assert.AreEqual(800f, SpellCastRules.CheckMagicCost(800f, 1f), 0.001f);
        }

        [Test]
        public void CheckManaCost_IsNotClamped()
        {
            // The 999 ceiling is applied to dmagic alone; dmana has none.
            Assert.AreEqual(2000f, SpellCastRules.CheckManaCost(1000f, 2f), 0.001f);
        }

        [Test]
        public void ManaSpend_AppliesWarlockThenAll_MatchingManaSpell()
        {
            // cast() passes `magic * warlockDManaMult` to manaSpell, which multiplies by
            // allDManaMult again (UnitPlayer.as:1712).
            Assert.AreEqual(600f, SpellCastRules.ManaSpend(100f, 2f, 3f), 0.001f);
        }

        [Test]
        public void CheckAndSpend_AgreeAtDefaults_AndDisagreeOnceWarlockRises()
        {
            // The load-bearing test for the check/spend asymmetry. At the defaults (both 1) the two
            // agree, which is why the quirk is invisible until a perk moves one.
            const float magic = 500f;
            Assert.AreEqual(SpellCastRules.CheckMagicCost(magic, 1f),
                            SpellCastRules.ManaSpend(magic, 1f, 1f), 0.001f,
                            "at the defaults the check and the spend are the same number");

            float check = SpellCastRules.CheckMagicCost(magic, 2f);       // clamped to 999
            float spend = SpellCastRules.ManaSpend(magic, 3f, 2f);        // 500*3*2 = 3000, unclamped
            Assert.AreEqual(999f, check, 0.001f);
            Assert.AreEqual(3000f, spend, 0.001f);
            Assert.Greater(spend, check,
                "a warlock spends more than the check ever compared — reproduced, not unified");
        }

        [Test]
        public void CooldownAfterCast_RoundsHalfUp_LikeAs3MathRound()
        {
            // AS3 Math.round breaks a tie toward +Infinity. .NET's Math.Round would give 2 and -2.
            Assert.AreEqual(3, SpellCastRules.CooldownAfterCast(2.5f, 1f));
            Assert.AreEqual(-2, SpellCastRules.CooldownAfterCast(-2.5f, 1f));
            Assert.AreEqual(300, SpellCastRules.CooldownAfterCast(300f, 1f));
            Assert.AreEqual(150, SpellCastRules.CooldownAfterCast(300f, 0.5f));
            Assert.AreEqual(0, SpellCastRules.CooldownAfterCast(0f, 1f));
        }

        [Test]
        public void ClampToRange_LeavesAnInRangeTargetAlone()
        {
            float x = 100f, y = 100f;
            SpellCastRules.ClampToRange(0f, 0f, 300f, ref x, ref y);
            Assert.AreEqual(100f, x, 0.0001f);
            Assert.AreEqual(100f, y, 0.0001f);
        }

        [Test]
        public void ClampToRange_PullsAnOutOfRangeTargetOntoTheCircle_SameBearing()
        {
            // Origin (0,0), target (3000,4000) -> length 5000, dist 1000.
            float x = 3000f, y = 4000f;
            SpellCastRules.ClampToRange(0f, 0f, 1000f, ref x, ref y);
            Assert.AreEqual(600f, x, 0.01f);
            Assert.AreEqual(800f, y, 0.01f);

            // Lands exactly on the circle, and keeps the bearing: (3000,4000) scaled by 1000/5000 is
            // (600,800), so the unit direction is (0.6, 0.8).
            float len = (float)System.Math.Sqrt(x * x + y * y);
            Assert.AreEqual(1000f, len, 0.01f);
            Assert.AreEqual(0.6f, x / len, 0.0001f);
            Assert.AreEqual(0.8f, y / len, 0.0001f);
        }

        [Test]
        public void ClampToRange_DistZeroOrNegative_IsANoOp()
        {
            float x = 9999f, y = -9999f;
            SpellCastRules.ClampToRange(0f, 0f, 0f, ref x, ref y);
            Assert.AreEqual(9999f, x, 0.0001f);
            SpellCastRules.ClampToRange(0f, 0f, -5f, ref x, ref y);
            Assert.AreEqual(9999f, x, 0.0001f);
        }

        [Test]
        public void ClampToRange_TargetAtTheOrigin_DoesNotDivideByZero()
        {
            // The oracle's guard is `if(this.dist > 0)` plus the squared test, so this branch is
            // unreachable — asserted so a future "simplification" that drops the guard fails here.
            float x = 0f, y = 0f;
            SpellCastRules.ClampToRange(0f, 0f, 100f, ref x, ref y);
            Assert.AreEqual(0f, x, 0.0001f);
            Assert.AreEqual(0f, y, 0.0001f);
        }
    }

    /// <summary>
    /// <see cref="Spell"/> driven through a fake host — the ported prologue and the three effects.
    /// </summary>
    [TestFixture]
    public class SpellTests
    {
        private static SpellData Data(
            float hp = 0f, float mana = 0f, float magic = 0f, float culd = 0f, float dist = 0f,
            float rad = 0f, float dam = 0f, bool line = false, bool prod = false, bool tele = false,
            bool atk = false, string snd = null)
        {
            var d = new SpellData();
            d.hp = hp; d.mana = mana; d.magic = magic; d.culd = culd; d.dist = dist;
            d.rad = rad; d.dam = dam; d.line = line; d.prod = prod; d.tele = tele;
            d.atk = atk; d.snd = snd;
            return d;
        }

        /// <summary><c>sp_slow</c>'s real row: hp 60, mana 30, magic 500, culd 10, rad 200, snd slow.</summary>
        private static SpellData SlowRow()
            => Data(hp: 60f, mana: 30f, magic: 500f, culd: 10f, rad: 200f, snd: "slow");

        [Test]
        public void Slow_AddsTheInhibitorEffect_WithRadTimesPower()
        {
            var host = new FakeSpellHost { SpellPower = 2f };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(50f, 60f);

            Assert.IsTrue(r.Cast);
            Assert.AreEqual(1, host.Effects.Count);
            Assert.AreEqual("inhibitor", host.Effects[0].id);
            Assert.AreEqual(400f, host.Effects[0].value, 0.001f, "rad 200 * power 2");
        }

        [Test]
        public void Slow_StampsTheCooldownFromCuldTimesFps()
        {
            var host = new FakeSpellHost();
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            // culd='10' seconds, converted at construction by * World.fps (30).
            Assert.AreEqual(300, r.CooldownTicks);
            Assert.AreEqual(300, spell.CooldownTicks);
        }

        [Test]
        public void Slow_PlaysItsOwnSoundOnSuccess()
        {
            var host = new FakeSpellHost();
            new Spell("sp_slow", SlowRow(), host).Cast(0f, 0f);
            CollectionAssert.Contains(host.Sounds, "slow");
        }

        [Test]
        public void Cooldown_BlocksTheSecondCast_AndTicksDown()
        {
            var host = new FakeSpellHost();
            var spell = new Spell("sp_slow", SlowRow(), host);

            Assert.IsTrue(spell.Cast(0f, 0f).Cast);
            var second = spell.Cast(0f, 0f);
            Assert.IsFalse(second.Cast);
            Assert.AreEqual(SpellCastRefusal.Cooldown, second.Refusal);
            CollectionAssert.Contains(host.Sounds, "nomagic");

            for (int i = 0; i < 300; i++) spell.Step();
            Assert.AreEqual(0, spell.CooldownTicks);
            Assert.IsTrue(spell.Cast(0f, 0f).Cast, "cooldown expired");
        }

        [Test]
        public void PlayerCast_SpendsMana_AndDamagesTheManaOrgan()
        {
            var host = new FakeSpellHost { Mana = 5000f, ManaHp = 5000f };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(500f, r.ManaSpent, 0.001f, "magic 500 at default multipliers");
            Assert.AreEqual(500f, host.ManaSpent, 0.001f);
            Assert.AreEqual(30f, host.ManaOrganDamaged, 0.001f, "mana 30");
            Assert.AreEqual(4500f, host.Mana, 0.001f);
        }

        [Test]
        public void PlayerCast_WithRaisedMultipliers_SpendsWarlockTimesAll()
        {
            // The asymmetry, end to end: the CHECK is magic*allDManaMult clamped at 999, the SPEND is
            // magic*warlockDManaMult*allDManaMult. Here the check passes at 999 and the spend is 3000.
            var host = new FakeSpellHost
            {
                Mana = 5000f, ManaHp = 5000f, AllDManaMult = 2f, WarlockDManaMult = 3f
            };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsTrue(r.Cast);
            Assert.AreEqual(3000f, r.ManaSpent, 0.001f, "500 * 3 * 2");
        }

        [Test]
        public void NpcCast_IsFree_AndStampsNoCooldown()
        {
            var host = new FakeSpellHost { IsPlayer = false, Mana = 0f, ManaHp = 0f };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsTrue(r.Cast);
            Assert.AreEqual(0f, r.ManaSpent, 0.001f, "the spend is inside `if(this.player)`");
            Assert.AreEqual(0, r.CooldownTicks, "so is the cooldown stamp");
            Assert.AreEqual(1, host.Effects.Count, "but the effect still runs");
        }

        [Test]
        public void OverMana_Refuses_WithItsOwnMessage()
        {
            var host = new FakeSpellHost { Mana = 499f, ManaHp = 5000f };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.OverMana, r.Refusal);
            CollectionAssert.Contains(host.InfoTexts, "overMana");
            Assert.AreEqual(0, host.Effects.Count, "the effect must not run");
            Assert.AreEqual(0f, host.ManaSpent, 0.001f);
        }

        [Test]
        public void NoManaOrganCapacity_Refuses_WithNoMana()
        {
            var host = new FakeSpellHost { Mana = 5000f, ManaHp = 29f };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.NoMana, r.Refusal);
            CollectionAssert.Contains(host.InfoTexts, "noMana");
        }

        [Test]
        public void NoSpellsPossible_Refuses_AndShowsTheBulb()
        {
            var host = new FakeSpellHost { SpellsPossible = 0 };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.NoSpells, r.Refusal);
            CollectionAssert.Contains(host.InfoTexts, "noSpells");
            Assert.AreEqual(1, host.Bulbs.Count);
        }

        [Test]
        public void AlicornRefusal_IsSilent()
        {
            // The oracle returns with no message and no sound. Asserted so a well-meaning "improvement"
            // that adds feedback fails here.
            var host = new FakeSpellHost { Alicorn = true };
            var spell = new Spell("sp_slow", SlowRow(), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.Alicorn, r.Refusal);
            Assert.AreEqual(0, host.InfoTexts.Count);
            Assert.AreEqual(0, host.Sounds.Count);
        }

        [Test]
        public void Cryst_FirstCast_Succeeds_AndArmsTheCrystalWindow()
        {
            var host = new FakeSpellHost { Mana = 5000f, ManaHp = 5000f, CrystalCooldown = 0 };
            var spell = new Spell("sp_cryst", Data(mana: 5f, magic: 50f, culd: 0f, atk: true, snd: "crystal"), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsTrue(r.Cast);
            Assert.AreEqual(Spell.EstSuccess, spell.Est);
            Assert.AreEqual(5, host.CrystalCooldown);
        }

        [Test]
        public void Cryst_SecondCastInsideTheWindow_IsEst2_AndChargesNothing()
        {
            // The est == 2 quirk: neither tail branch matches, so cast() returns true while spending no
            // mana and stamping no cooldown. Easy to "fix" into a normal success; asserted to prevent it.
            var host = new FakeSpellHost { Mana = 5000f, ManaHp = 5000f, CrystalCooldown = 3 };
            var spell = new Spell("sp_cryst", Data(mana: 5f, magic: 50f, culd: 60f, atk: true, snd: "crystal"), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsTrue(r.Cast, "est == 2 still returns true");
            Assert.AreEqual(Spell.EstAlternate, spell.Est);
            Assert.AreEqual(0f, r.ManaSpent, 0.001f, "est == 2 charges nothing");
            Assert.AreEqual(0, spell.CooldownTicks, "and stamps no cooldown");
            Assert.AreEqual(5, host.CrystalCooldown, "but t_cryst is still set to 5");
        }

        [Test]
        public void Mshit_SetsShitHp_ToHpTimesPower()
        {
            var host = new FakeSpellHost { SpellPower = 1.5f, Mana = 5000f, ManaHp = 5000f };
            var spell = new Spell("sp_mshit", Data(hp: 150f, mana: 100f, magic: 500f, culd: 30f, atk: true, snd: "mshit"), host);

            spell.Cast(0f, 0f);

            Assert.AreEqual(225f, host.ShitHp, 0.001f, "hp 150 * power 1.5");
        }

        [Test]
        public void Mshit_PlayerInAlicorn_UsesAlicornShitHp()
        {
            var host = new FakeSpellHost
            {
                Alicorn = true, IsPlayer = true, AlicornShitHp = 2000f, SpellPower = 3f,
                Mana = 5000f, ManaHp = 5000f
            };
            var spell = new Spell("sp_mshit", Data(hp: 150f, mana: 100f, magic: 500f, culd: 30f, atk: true, snd: "mshit"), host);

            spell.Cast(0f, 0f);

            Assert.AreEqual(2000f, host.ShitHp, 0.001f, "the alicorn branch ignores hp * power");
        }

        [Test]
        public void AnUnportedEffect_IsRefused_AndChargesNothing()
        {
            // sp_blast is one of the nine (so HasEffect) but needs the unit/knockback subsystem.
            var host = new FakeSpellHost { Mana = 5000f, ManaHp = 5000f };
            var spell = new Spell("sp_blast", Data(dam: 20f, mana: 30f, magic: 300f, culd: 10f, rad: 500f, tele: true, atk: true, snd: "blast"), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsTrue(spell.HasEffect, "it IS one of the nine");
            Assert.IsFalse(spell.IsEffectPorted, "but the port has not written it");
            Assert.AreEqual(SpellCastRefusal.EffectNotPorted, r.Refusal);
            Assert.AreEqual(0f, host.ManaSpent, 0.001f);
            Assert.AreEqual(0, spell.CooldownTicks);
        }

        [Test]
        public void AnUnknownId_IsNoEffect_NotEffectNotPorted()
        {
            // The two are different faults: NoEffect is a data problem, EffectNotPorted is known work.
            var host = new FakeSpellHost();
            var spell = new Spell("sp_not_a_spell", Data(magic: 100f), host);

            var r = spell.Cast(0f, 0f);

            Assert.IsFalse(spell.HasEffect);
            Assert.AreEqual(SpellCastRefusal.NoEffect, r.Refusal);
        }

        [Test]
        public void TeleSpell_PlayerUsesTelePower_NotSpellPower()
        {
            // `if(this.player && this.teleSpell) this.power = this.gg.pers.telePower;`
            var host = new FakeSpellHost { SpellPower = 5f, TelePower = 2f, Mana = 5000f, ManaHp = 5000f };
            // sp_mshit carries no tele, so use a row that does and is ported... none are. Assert on the
            // unported path's placement instead: Power is public, so read it after a refused cast.
            var spell = new Spell("sp_blast", Data(dam: 20f, mana: 30f, magic: 300f, culd: 10f, tele: true, atk: true), host);

            spell.Cast(0f, 0f);

            Assert.AreEqual(2f, spell.Power, 0.001f, "tele power replaces spell power for a player");
        }

        [Test]
        public void TeleSpell_NpcKeepsSpellPower()
        {
            var host = new FakeSpellHost { IsPlayer = false, SpellPower = 5f, TelePower = 2f };
            var spell = new Spell("sp_blast", Data(dam: 20f, mana: 30f, magic: 300f, culd: 10f, tele: true, atk: true), host);

            spell.Cast(0f, 0f);

            Assert.AreEqual(5f, spell.Power, 0.001f, "the telePower swap is inside `this.player`");
        }

        [Test]
        public void LineSpell_WithNoSight_IsRefused_BeforeTheEffectRuns()
        {
            var host = new FakeSpellHost { LineOfSight = false };
            var spell = new Spell("sp_slow", Data(mana: 30f, magic: 500f, culd: 10f, line: true, snd: "slow"), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.NoLineOfSight, r.Refusal);
            CollectionAssert.Contains(host.InfoTexts, "noVisible");
            Assert.AreEqual(0, host.Effects.Count);
        }

        [Test]
        public void LineSpell_WithNoSight_IsSilentForAnNpc()
        {
            var host = new FakeSpellHost { IsPlayer = false, LineOfSight = false };
            var spell = new Spell("sp_slow", Data(mana: 30f, magic: 500f, culd: 10f, line: true, snd: "slow"), host);

            var r = spell.Cast(0f, 0f);

            Assert.AreEqual(SpellCastRefusal.NoLineOfSight, r.Refusal);
            Assert.AreEqual(0, host.InfoTexts.Count, "the message is player-only (:265)");
        }
    }
}
