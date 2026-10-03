using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Magic;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// The player's spell collection, the selection toggle and the two cast triggers —
    /// <c>Invent.addSpell</c> / <c>UnitPlayer.changeSpell</c> / the <c>ctr.key*</c> blocks in
    /// <c>control()</c>.
    ///
    /// <para>Every test drives the real <see cref="Spell"/> through a real
    /// <see cref="SpellCastRules"/> pass; nothing here re-derives a rule. The data values are the
    /// oracle's own (<c>AllData.as:4008-4016</c>), so a mismatch shows up as a wrong number rather than
    /// as a green test over invented inputs.</para>
    /// </summary>
    [TestFixture]
    public class SpellBookTests
    {
        // ── the nine rows, as imported (AllData.as:4008-4016) ────────────────────────────────────

        /// <summary><c>sp_slow</c>: no <c>atk</c>, no <c>line</c>, no <c>prod</c>, culd 10.</summary>
        private static SpellData Slow => new SpellData
        {
            hp = 60f, magic = 500f, mana = 30f, culd = 10f, rad = 200f, snd = "slow",
        };

        /// <summary><c>sp_cryst</c>: the only <c>prod='1'</c> row, and its own <c>culd</c> is 0.</summary>
        private static SpellData Cryst => new SpellData
        {
            magic = 50f, mana = 5f, culd = 0f, atk = true, prod = true, snd = "crystal",
        };

        /// <summary><c>sp_mshit</c>: <c>atk='1'</c>, culd 30, hp 150.</summary>
        private static SpellData MShit => new SpellData
        {
            hp = 150f, magic = 500f, mana = 100f, culd = 30f, atk = true, snd = "mshit",
        };

        private static bool TryData(string id, out SpellData data)
        {
            switch (id)
            {
                case "sp_slow":  data = Slow;  return true;
                case "sp_cryst": data = Cryst; return true;
                case "sp_mshit": data = MShit; return true;
                default:         data = default; return false;
            }
        }

        private FakeSpellHost _host;
        private SpellBook _book;
        private List<string> _factoryCalls;

        [SetUp]
        public void SetUp()
        {
            _host = new FakeSpellHost();
            _factoryCalls = new List<string>();
            _book = new SpellBook(id =>
            {
                _factoryCalls.Add(id);
                return TryData(id, out SpellData data) ? new Spell(id, data, _host) : null;
            });
        }

        // ── Invent.addSpell (:1018-1037) ────────────────────────────────────────────────────────

        [Test]
        public void GetOrAdd_ReturnsTheSameInstance_AndBuildsOnce()
        {
            Spell first = _book.GetOrAdd("sp_slow");
            Spell second = _book.GetOrAdd("sp_slow");

            Assert.AreSame(first, second, "the oracle returns the stored spell rather than rebuilding");
            Assert.AreEqual(1, _book.Count);
            Assert.AreEqual(new[] { "sp_slow" }, _factoryCalls, "the factory is called at most once per id");
        }

        [Test]
        public void GetOrAdd_WithAnEmptyId_ReturnsNull_AndBuildsNothing()
        {
            // `if(param1 == null) return null;` — the oracle's first guard. It is load-bearing for
            // changeSpell(""), which Invent.as:849-852 uses to deselect a spell.
            Assert.IsNull(_book.GetOrAdd(null));
            Assert.IsNull(_book.GetOrAdd(""));
            Assert.AreEqual(0, _book.Count);
            Assert.AreEqual(0, _factoryCalls.Count);
        }

        [Test]
        public void GetOrAdd_WhenTheFactoryDeclines_ReturnsNull_AndKeepsNothing()
        {
            // The port's factory stands in for the AllData lookup, so an id with no row is reachable
            // here — unlike the oracle's own `if(_loc2_ == null)`, which cannot fire.
            Assert.IsNull(_book.GetOrAdd("sp_nonexistent"));
            Assert.AreEqual(0, _book.Count);
            Assert.IsFalse(_book.TryGet("sp_nonexistent", out _));
        }

        // ── changeSpell (:3850-3863) ────────────────────────────────────────────────────────────

        [Test]
        public void Select_IsAToggle_NotASet()
        {
            Spell slow = _book.GetOrAdd("sp_slow");

            Assert.AreSame(slow, _book.Select("sp_slow"), "first selection");
            Assert.IsNull(_book.Select("sp_slow"), "re-selecting the same spell clears it");
            Assert.AreSame(slow, _book.Select("sp_slow"), "and a third press selects it again");
        }

        [Test]
        public void Select_SwitchesBetweenSpells_WithoutTogglingOff()
        {
            Spell slow = _book.GetOrAdd("sp_slow");
            Spell cryst = _book.GetOrAdd("sp_cryst");

            _book.Select("sp_slow");
            Assert.AreSame(cryst, _book.Select("sp_cryst"), "a different spell is a switch, not a toggle");
            Assert.AreSame(slow, _book.Select("sp_slow"));
        }

        [Test]
        public void Select_OfAnUnknownId_ClearsTheSelection()
        {
            // `spells[id]` is undefined for an unknown id, which is neither equal to currentSpell nor
            // truthy — so the oracle's else-branch assigns undefined. Reproduced: the deselect path in
            // Invent.as:849-852 depends on it.
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");
            Assert.IsNotNull(_book.Current);

            Assert.IsNull(_book.Select(""));
            Assert.IsNull(_book.Current);
        }

        [Test]
        public void Select_OfAnUnownedSpell_ClearsTheSelection()
        {
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            Assert.IsNull(_book.Select("sp_cryst"), "not in the book -> nothing selected");
        }

        // ── the per-tick sweep (UnitPlayer.step, :1613-1616) ────────────────────────────────────

        [Test]
        public void StepAll_TicksEverySpell_NotJustTheSelectedOne()
        {
            Spell slow = _book.GetOrAdd("sp_slow");     // culd 10 -> 300 frames
            Spell mshit = _book.GetOrAdd("sp_mshit");   // culd 30 -> 900 frames

            slow.Cast(0f, 0f);
            mshit.Cast(0f, 0f);
            Assert.AreEqual(300, slow.CooldownTicks);
            Assert.AreEqual(900, mshit.CooldownTicks);

            // Nothing is selected — this is the case the oracle covers by walking `invent.spells`
            // rather than `currentSpell`. A port that only ticked the selection would leave every
            // other spell frozen at its full cooldown forever.
            Assert.IsNull(_book.Current);
            _book.StepAll();

            Assert.AreEqual(299, slow.CooldownTicks, "an unselected spell still counts down");
            Assert.AreEqual(899, mshit.CooldownTicks);
        }

        [Test]
        public void StepAll_DoesNotGoNegative()
        {
            Spell cryst = _book.GetOrAdd("sp_cryst");   // culd 0 -> 0 frames
            _book.StepAll();
            _book.StepAll();

            Assert.AreEqual(0, cryst.CooldownTicks, "Spell.step() only decrements above 0");
        }

        // ── the Def key (UnitPlayer.as:2226-2247) ───────────────────────────────────────────────

        [Test]
        public void DefKey_WhenNotHeld_DoesNothing()
        {
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            SpellTriggerOutcome outcome = _book.DefKey(held: false, rat: 0, alicorn: false, aimX: 0f, aimY: 0f);

            Assert.IsFalse(outcome.Attempted);
            Assert.IsFalse(outcome.KeyStaysHeld);
            Assert.AreEqual(0f, _host.ManaSpent);
        }

        [Test]
        public void DefKey_WhileTransformed_DoesNothing_AndLeavesTheKeyHeld()
        {
            // The guard is `ctr.keyDef && this.rat == 0`, so a held key with rat != 0 falls out of the
            // whole block: no cast, and — unlike every other branch — the key is NOT cleared. A rewrite
            // that folded the rat check into the cast result would release the key here and force the
            // player to re-press after the transformation ends.
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 1, alicorn: false, aimX: 0f, aimY: 0f);

            Assert.IsFalse(outcome.Attempted);
            Assert.IsTrue(outcome.KeyStaysHeld, "the key is untouched, not cleared");
            Assert.AreEqual(0f, _host.ManaSpent);
        }

        [Test]
        public void DefKey_InAlicornMode_SubstitutesTheShieldSpell()
        {
            // `this.currentSpell = this.invent.spells["sp_mshit"]` — an assignment, not changeSpell, so
            // it never toggles the player's own selection away.
            //
            // Both alicorn flags are set together because AS3 has only one of them: `World.w.alicorn` is
            // read by the Def-key block (the substitution) and again by the spell's own gate and by
            // cast_mshit. A fixture that set them separately would let a test pass while the two halves
            // disagreed — which is exactly the bug it is supposed to catch.
            _host.Alicorn = true;
            _book.GetOrAdd("sp_slow");
            _book.GetOrAdd("sp_mshit");
            _book.Select("sp_slow");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 0, alicorn: true, aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Attempted);
            Assert.AreEqual("sp_mshit", _book.Current.Id);
            Assert.AreEqual(2000f, _host.ShitHp, 1e-4f,
                "cast_mshit's alicorn branch: the player gets pers.alicornShitHP, not hp * power");
        }

        [Test]
        public void DefKey_InAlicornMode_WhenTheShieldSpellIsNotOwned_ClearsTheKey()
        {
            // The substitution is a LOOKUP. `invent.spells["sp_mshit"]` does not create the spell, so a
            // player who never picked it up gets undefined and the key is released. A port that called
            // GetOrAdd here would conjure a spell out of nothing.
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 0, alicorn: true, aimX: 0f, aimY: 0f);

            Assert.IsFalse(outcome.Attempted);
            Assert.IsFalse(outcome.KeyStaysHeld);
            Assert.IsNull(_book.Current, "the selection is overwritten with nothing, as in AS3");
            Assert.AreEqual(1, _book.Count, "and no sp_mshit was created");
            Assert.IsFalse(_book.TryGet("sp_mshit", out _));
        }

        [Test]
        public void DefKey_OnANonProducingSpell_ReleasesTheKeyAfterASuccessfulCast()
        {
            // sp_slow has no `prod`. The cast succeeds and the key still goes false — that is
            // `if(!this.currentSpell.prod) this.ctr.keyDef = false;`, a second independent `if`.
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 0, alicorn: false, aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Attempted);
            Assert.IsTrue(outcome.Cast.Cast, "the cast itself succeeded");
            Assert.IsFalse(outcome.KeyStaysHeld, "but one press is one cast");
            Assert.AreEqual(500f, _host.ManaSpent, 1e-4f);
        }

        [Test]
        public void DefKey_OnAProducingSpell_KeepsTheKeyHeld()
        {
            // sp_cryst is the only `prod='1'` row: holding the key re-casts every tick it is legal.
            _book.GetOrAdd("sp_cryst");
            _book.Select("sp_cryst");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 0, alicorn: false, aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Cast.Cast);
            Assert.IsTrue(outcome.KeyStaysHeld, "prod -> the key survives the cast");
        }

        [Test]
        public void DefKey_WhenTheCastIsRefused_ReleasesTheKey()
        {
            // The first `if` of the pair. A refusal (here: the cooldown the previous cast armed) clears
            // the key, so a held button cannot spin on a refused spell.
            _book.GetOrAdd("sp_slow");
            _book.Select("sp_slow");

            SpellTriggerOutcome first = _book.DefKey(held: true, rat: 0, alicorn: false, aimX: 0f, aimY: 0f);
            Assert.IsTrue(first.KeyStaysHeld == false, "sp_slow never keeps the key anyway");

            SpellTriggerOutcome second = _book.DefKey(held: true, rat: 0, alicorn: false, aimX: 0f, aimY: 0f);
            Assert.IsTrue(second.Attempted, "a cast was attempted");
            Assert.IsFalse(second.Cast.Cast, "and refused by the cooldown");
            Assert.AreEqual(SpellCastRefusal.Cooldown, second.Cast.Refusal);
            Assert.IsFalse(second.KeyStaysHeld);
            Assert.AreEqual(500f, _host.ManaSpent, 1e-4f, "the refused second cast spent nothing");
        }

        // ── the favourite-spell hotkeys (UnitPlayer.as:2252-2284) ───────────────────────────────

        [Test]
        public void Hotkey_HasNoRatGuard_SoItStillCastsWhileTransformed()
        {
            // The asymmetry with DefKey, and it is the oracle's: the keySpell loop sits at :2252,
            // OUTSIDE the `if(this.rat == 0)` block that opens at :2391. Unifying the two guards would
            // silently disable hotkeys for a transformed player.
            _book.GetOrAdd("sp_slow");

            SpellTriggerOutcome outcome = _book.Hotkey(held: true, spellId: "sp_slow", aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Attempted, "no rat guard on this path");
            Assert.IsTrue(outcome.Cast.Cast);
            Assert.AreEqual(500f, _host.ManaSpent, 1e-4f);
        }

        [Test]
        public void Hotkey_WithAnUnboundOrUnownedSlot_ClearsTheKey()
        {
            // `fav[...] == null` (:2256) and an unowned id (:2264-2277) both clear the key, so an
            // unbound hotkey cannot latch and re-try every tick.
            Assert.IsFalse(_book.Hotkey(held: true, spellId: null, aimX: 0f, aimY: 0f).KeyStaysHeld);
            Assert.IsFalse(_book.Hotkey(held: true, spellId: "", aimX: 0f, aimY: 0f).KeyStaysHeld);

            SpellTriggerOutcome unowned = _book.Hotkey(held: true, spellId: "sp_mshit", aimX: 0f, aimY: 0f);
            Assert.IsFalse(unowned.Attempted);
            Assert.IsFalse(unowned.KeyStaysHeld);
        }

        [Test]
        public void Hotkey_DoesNotSubstituteInAlicornMode()
        {
            // Unlike the Def key, this path never reads World.w.alicorn, so it still targets the spell
            // the slot names — here sp_slow.
            //
            // The observable consequence is NOT a successful cast: the alicorn gate inside Spell.cast()
            // refuses every spell except sp_mshit (Spell.as:193-196), so the hotkey's sp_slow is
            // refused. That refusal IS the proof of no substitution — a substituted sp_mshit would have
            // passed the gate and granted a shield. Asserting "it casts anyway" here would have been a
            // fixture asserting a conclusion its own premise does not support: the substitution and the
            // gate are two separate facts and only the pair is checkable.
            _host.Alicorn = true;
            _book.GetOrAdd("sp_slow");
            _book.GetOrAdd("sp_mshit");

            SpellTriggerOutcome outcome = _book.Hotkey(held: true, spellId: "sp_slow", aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Attempted, "the hotkey did try to cast");
            Assert.AreEqual(SpellCastRefusal.Alicorn, outcome.Cast.Refusal,
                "and it tried sp_slow, which the alicorn gate refuses — not the exempt sp_mshit");
            Assert.IsFalse(outcome.Cast.Cast);
            Assert.IsTrue(float.IsNaN(_host.ShitHp), "no shield was granted");
            Assert.AreEqual(0, _host.Effects.Count);
        }

        [Test]
        public void DefKey_InAlicornMode_Succeeds_WhereTheHotkeyIsRefused()
        {
            // The control for the test above: same mode, same spell book, same tick — only the trigger
            // differs. The Def key substitutes the exempt sp_mshit and therefore casts.
            _host.Alicorn = true;
            _book.GetOrAdd("sp_slow");
            _book.GetOrAdd("sp_mshit");

            SpellTriggerOutcome outcome = _book.DefKey(held: true, rat: 0, alicorn: true, aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Cast.Cast);
            Assert.AreEqual(2000f, _host.ShitHp, 1e-4f);
        }

        [Test]
        public void Hotkey_OnAProducingSpell_KeepsTheKeyHeld()
        {
            _book.GetOrAdd("sp_cryst");

            SpellTriggerOutcome outcome = _book.Hotkey(held: true, spellId: "sp_cryst", aimX: 0f, aimY: 0f);

            Assert.IsTrue(outcome.Cast.Cast);
            Assert.IsTrue(outcome.KeyStaysHeld);
        }
    }
}
