using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Effects;

namespace PFE.Tests.EditMode.Systems.Effects
{
    /// <summary>
    /// Pins the <b>live-unit</b> half of the effect system: that <see cref="UnitStats"/> is an
    /// <see cref="IEffectHost"/> that actually reacts to an effect, not merely one that holds a set.
    ///
    /// <para><b>Why this is a separate fixture from <c>ActiveEffectSetTests</c>.</b> That fixture proves
    /// the set's own state machine (add/merge/tick/splice) against a recording <c>FakeHost</c> — which
    /// is the right thing for the mover, but says nothing about whether the host on a real unit does
    /// anything. This project's recurring defect is exactly the gap between the two: a component wired
    /// up, whose host is inert. So these tests use the <b>real</b> <see cref="UnitStats"/> and assert on
    /// its <b>own</b> observable state (<c>MaxHp.Value</c>, <c>Vulnerabilities</c>, <c>skinResistance</c>)
    /// — never on a fake, which could be satisfied by the fake computing the result itself.</para>
    ///
    /// <para><b>Everything here runs headless.</b> <see cref="UnitStats"/> is a plain class and the
    /// definitions are plain objects implementing the engine-free <see cref="IEffectTemplate"/>
    /// (<see cref="ScriptableObject"/>-free — see <c>ActiveEffectSetTests</c>'s fixture remarks for why
    /// the asset type cannot be constructed outside the editor). A test whose arrange step cannot run has
    /// no way to go red.</para>
    /// </summary>
    [TestFixture]
    public class UnitStatsEffectHostTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        private sealed class TestEffectTemplate : IEffectTemplate
        {
            public string effectId { get; set; }
            public EffectType type { get; set; } = EffectType.Timed;
            public int durationTicks { get; set; } = 30;
            public bool forever { get; set; }
            public float value { get; set; }
            public bool add { get; set; }
            public string afterEffectId { get; set; }
            public int lvl1 { get; set; }
            public int lvl2 { get; set; }
            public int lvl3 { get; set; }
            public string[] deletesOnStart { get; set; }
            public EffectParam[] effects { get; set; }
        }

        private sealed class FakeDefinitions : IEffectDefinitionResolver
        {
            private readonly Dictionary<string, IEffectTemplate> _byId = new();

            public void Add(IEffectTemplate d) => _byId[d.effectId] = d;

            public IEffectTemplate Resolve(string effectId)
                => !string.IsNullOrEmpty(effectId) && _byId.TryGetValue(effectId, out var d) ? d : null;
        }

        private static IEffectTemplate Def(
            string id,
            float value = 0f,
            bool forever = false,
            int durationTicks = 30,
            int lvl1 = 0,
            EffectParam[] effects = null)
            => new TestEffectTemplate
            {
                effectId = id,
                value = value,
                forever = forever,
                durationTicks = durationTicks,
                lvl1 = lvl1,
                effects = effects,
            };

        private static EffectParam Param(string id, float v0, EffectParamRef op = EffectParamRef.Add)
            => new EffectParam { id = id, perLevel = new[] { v0 }, op = op };

        /// <summary>
        /// A param with a distinct <b>active</b> and <b>unset</b> value — the real-data shape, and the
        /// only shape in which "unsetting undoes the write" is even expressible.
        ///
        /// <para><b>Why two levels and not one.</b> The oracle replays an effect at index <b>1</b> while
        /// active and index <b>0</b> while being unset (<c>Unit.as:3494</c>,
        /// <c>setSkillParam(sk, eff.vse ? 0 : 1)</c>). So the <i>data</i> is what encodes the undo:
        /// <c>v0</c> is the neutral/unset value and <c>v1</c> the active one. A fixture with a single
        /// level would replay the same number on unset and never undo anything — it would make a broken
        /// runtime look correct, which is the failure this whole fixture exists to avoid.</para>
        /// </summary>
        private static EffectParam BiParam(string id, float v0, float v1, EffectParamRef op = EffectParamRef.Add)
            => new EffectParam { id = id, perLevel = new[] { v0, v1 }, op = op };

        private static EffectParam ResistParam(int damageIndex, float v0)
            => new EffectParam { id = damageIndex.ToString(), IsResistance = true, op = EffectParamRef.Add, perLevel = new[] { v0 } };

        /// <summary>A stats object with a wired resolver, plus the resolver for adding more defs.</summary>
        private static (UnitStats Stats, FakeDefinitions Defs) NewWired(PersMode mode = PersMode.Npc)
        {
            var defs = new FakeDefinitions();
            var stats = new UnitStats();
            stats.EnsureEffects(defs, mode);
            return (stats, defs);
        }

        // ── 1. The seam itself ────────────────────────────────────────────────

        [Test]
        public void NewUnitStats_IsResolverLess_AndRefusesEveryId()
        {
            // The pre-wiring state, pinned deliberately: a set with no resolver must refuse an id rather
            // than materialise a phantom effect. This is what makes "wired" a testable property.
            var stats = new UnitStats();
            var defs = new FakeDefinitions();
            defs.Add(Def("burning"));

            Assert.That(stats.HasEffectResolver, Is.False);
            Assert.That(stats.Effects, Is.Not.Null, "Effects is never null — it answers an empty set");
            Assert.That(stats.Effects.AddEffect("burning"), Is.Null);
            Assert.That(stats.Effects.Count, Is.EqualTo(0));
        }

        [Test]
        public void EnsureEffects_MakesTheSameIdResolvable()
        {
            // The control for the test above: the ONLY difference is the resolver, so this proves the
            // refusal was the missing resolver and not a bad effect id.
            var stats = new UnitStats();
            var defs = new FakeDefinitions();
            defs.Add(Def("burning"));

            stats.EnsureEffects(defs, PersMode.Npc);

            Assert.That(stats.HasEffectResolver, Is.True);
            Assert.That(stats.Effects.AddEffect("burning"), Is.Not.Null);
            Assert.That(stats.Effects.Count, Is.EqualTo(1));
        }

        [Test]
        public void EnsureEffects_NullResolver_LeavesTheSetResolverLess()
        {
            // A spawner passes a resolver down the chain; a null registry must not flip the flag.
            var stats = new UnitStats();
            stats.EnsureEffects(null, PersMode.Npc);

            Assert.That(stats.HasEffectResolver, Is.False);
        }

        // ── 2. The param pass actually writes (wired, not inert) ──────────────

        [Test]
        public void AddEffect_WithMaxHpParam_MovesMaxHp()
        {
            // AS3 Unit.setSkillParam's field branch. The assertion is on the real MaxHp, so a host that
            // merely recorded the param would fail this. v0=0/v1=50: active index 1 writes +50.
            var (stats, defs) = NewWired();
            defs.Add(Def("buff", effects: new[] { BiParam("maxhp", 0f, 50f) }));

            float before = stats.MaxHp.Value;
            stats.Effects.AddEffect("buff");

            Assert.That(stats.MaxHp.Value, Is.EqualTo(before + 50f).Within(0.001f),
                "Start-applied <sk> must write immediately (Effect.setEff runs the param pass)");
        }

        [Test]
        public void AddEffect_SkinParam_WritesSkinResistance()
        {
            var (stats, defs) = NewWired();
            defs.Add(Def("buff", effects: new[] { BiParam("skin", 0f, 0.25f) }));

            float before = stats.skinResistance;
            stats.Effects.AddEffect("buff");

            Assert.That(stats.skinResistance, Is.EqualTo(before + 0.25f).Within(0.0001f));
        }

        [Test]
        public void RemoveEffect_ReplaysAtTheUnsetIndex_SoAResistanceIsUndone()
        {
            // The core of the reset-then-replay contract, asserted on the channel the ORACLE resets.
            // `Unit.setEffParams` rebuilds `vulner[]` from `begvulner[]` and then replays every effect
            // at index 1 (active) or 0 (being unset) — so a tip='res' deduction is applied while the
            // effect is live and removed the moment it is unset.
            //
            // Resistance is the assertion target on purpose: it is the parameter an NPC-reachable
            // effect actually writes. `maxhp` would be the wrong target — AS3's NPC reset does NOT
            // restore derived stats (see AddEffect_DerivedStatWrite_IsNotUndoneOnTheNpcPath), so
            // asserting an undo there would pin a divergence.
            var (stats, defs) = NewWired();
            int fire = (int)DamageType.Fire;
            // v0=0 (neutral when unset), v1=0.4 (active). Two levels are what make the undo expressible.
            defs.Add(Def("ward", effects: new[]
            {
                new EffectParam { id = fire.ToString(), IsResistance = true, op = EffectParamRef.Add, perLevel = new[] { 0f, 0.4f } },
            }));

            float before = stats.Vulnerabilities.GetVulnerability(DamageType.Fire);
            stats.Effects.AddEffect("ward");
            Assert.That(stats.Vulnerabilities.GetVulnerability(DamageType.Fire),
                Is.EqualTo(before - 0.4f).Within(0.0001f), "precondition: the deduction landed");

            stats.Effects.RemoveEffect("ward");

            // No tick yet: the undo must already have happened, because the oracle's unsetEff runs the
            // param pass immediately (Effect.as:337-348). A port that deferred it to the payload tick
            // would still read the reduced value here — and then splice the effect away without ever
            // undoing it, because a vse effect is spliced, not stepped.
            Assert.That(stats.Vulnerabilities.GetVulnerability(DamageType.Fire),
                Is.EqualTo(before).Within(0.0001f),
                "the reset-then-replay must undo the deduction at unset time, not at the splice");

            stats.TickEffects(1f);
            Assert.That(stats.Effects.Count, Is.EqualTo(0), "the spliced slot is gone");
            Assert.That(stats.Vulnerabilities.GetVulnerability(DamageType.Fire),
                Is.EqualTo(before).Within(0.0001f), "and the value stays undone after the splice");
        }

        [Test]
        public void AddEffect_DerivedStatWrite_IsNotUndoneOnTheNpcPath()
        {
            // Pins the ORACLE, which is not the same as pinning "intuitive". Unit.setEffParams
            // (Unit.as:3466-3496) resets `tormoz`/`precMultCont`/`rapidMultCont` and `vulner[]` — and
            // nothing else. It does NOT restore `maxhp`/`skin`/`dexter`.
            //
            // That reset set is matched to the effect set, not an oversight: every parameter an
            // NPC-reachable effect writes is either a tip='res' index or one of those three counters
            // (checked against all 79 <eff> rows). The derived-stat effects — buck, f_hp, the f_* foods
            // — are player consumables and go through Pers.setParameters, which DOES reset
            // (defaultParams: gg.maxhp = begHP). So on a plain NPC-side UnitStats the write persists.
            //
            // This test exists so a later pass cannot "fix" the NPC path into a divergence. If you
            // find yourself deleting it, read RunEffectParamPass's remarks first.
            var (stats, defs) = NewWired();
            defs.Add(Def("buff", effects: new[] { BiParam("maxhp", 0f, 50f) }));

            float before = stats.MaxHp.Value;
            stats.Effects.AddEffect("buff");
            Assert.That(stats.MaxHp.Value, Is.EqualTo(before + 50f).Within(0.001f),
                "precondition: the write landed");

            stats.Effects.RemoveEffect("buff");
            stats.TickEffects(1f);

            Assert.That(stats.MaxHp.Value, Is.EqualTo(before + 50f).Within(0.001f),
                "AS3's NPC reset does not restore derived stats — the write persists, as in the oracle");
            Assert.That(stats.Effects.Count, Is.EqualTo(0), "but the effect itself is gone");
        }

        [Test]
        public void PlayerResetSink_RunsBeforeTheReplay_SoTheWriteDoesNotCompound()
        {
            // The PLAYER half of the reset contract, and the bug this sink was added for. AS3's
            // Pers.setParameters is reset-then-replay: defaultParams() restores gg.maxhp = begHP and
            // only then re-applies every effect at eff.lvl. The port had hoisted the replay but never
            // called the reset, so a player buff compounded on every pass (100 -> 150 -> 200 -> ...).
            //
            // The fake reset sink models CharacterStats.RecalculateStats: it restores the derived
            // baseline and re-runs the sink-free block. A second AddEffect must therefore land the same
            // +50, not +100.
            var (stats, defs) = NewWired(PersMode.Player);
            defs.Add(Def("buff", effects: new[] { Param("maxhp", 50f) }));
            defs.Add(Def("buff2", effects: new[] { Param("maxhp", 70f) }));

            float baseMaxHp = stats.MaxHp.Value;
            float applied = 0f;
            stats.EffectResetSink = () => { stats.MaxHp.Value = baseMaxHp; applied = 0f; };
            stats.EffectParamSink = (param, index, effectId) =>
            {
                if (param.id == "maxhp") applied += param.ValueForLevel(index);
                stats.MaxHp.Value = baseMaxHp + applied;
            };

            stats.Effects.AddEffect("buff");
            Assert.That(stats.MaxHp.Value, Is.EqualTo(baseMaxHp + 50f).Within(0.001f));

            // The second start triggers another reset-then-replay. Without the reset this would read
            // baseMaxHp + 50 + 50 + 70; with it, the replay rebuilds from baseMaxHp.
            stats.Effects.AddEffect("buff2");
            Assert.That(stats.MaxHp.Value, Is.EqualTo(baseMaxHp + 50f + 70f).Within(0.001f),
                "the reset must run before the replay, or each pass compounds the previous one");
        }

        [Test]
        public void ResistanceReplay_IsIdempotent_SoTheDeductionDoesNotCompound()
        {
            // The reset must actually zero the channel before each replay, or a second pass would
            // subtract the deduction twice (the "a +5 maxhp that lands 30 times a second" shape, on the
            // channel the NPC path owns). Every tip='res' row in the data is `add`, so this is the real
            // shape: v1 = 0.4 applied once while live.
            var (stats, defs) = NewWired();
            int fire = (int)DamageType.Fire;
            defs.Add(Def("ward", effects: new[]
            {
                new EffectParam { id = fire.ToString(), IsResistance = true, op = EffectParamRef.Add, perLevel = new[] { 0f, 0.4f } },
            }));
            defs.Add(Def("other", effects: new[] { Param("skin", 0.1f) }));

            float before = stats.Vulnerabilities.GetVulnerability(DamageType.Fire);
            stats.Effects.AddEffect("ward");

            float afterFirst = stats.Vulnerabilities.GetVulnerability(DamageType.Fire);
            Assert.That(afterFirst, Is.EqualTo(before - 0.4f).Within(0.0001f),
                "precondition: the deduction landed");

            // A second effect starting runs the pass again. If the reset did not zero the channel the
            // deduction would apply a second time and read before - 0.8.
            stats.Effects.AddEffect("other");

            Assert.That(stats.Vulnerabilities.GetVulnerability(DamageType.Fire),
                Is.EqualTo(before - 0.4f).Within(0.0001f),
                "the reset must zero the channel before the replay, or the deduction compounds");
        }

        // ── 3. Resistance writes survive the reset ────────────────────────────

        [Test]
        public void ResistanceParam_ReducesVulnerability_AndSurvivesTheReplay()
        {
            // tip='res' is the one special case: AS3 writes `vulner[id] -= v` on the LIVE table. A naive
            // port would have the replay reset the table from baseline and erase the deduction — so this
            // pins that the deduction is still there after a pass.
            var (stats, defs) = NewWired();
            int fire = (int)DamageType.Fire;
            defs.Add(Def("ward", effects: new[] { ResistParam(fire, 0.5f) }));

            float before = stats.Vulnerabilities.GetVulnerability(DamageType.Fire);
            stats.Effects.AddEffect("ward");

            float after = stats.Vulnerabilities.GetVulnerability(DamageType.Fire);
            Assert.That(after, Is.EqualTo(before - 0.5f).Within(0.0001f),
                "a tip='res' param subtracts from the live vulnerability table");

            // A second effect starting triggers another pass; the deduction must still be present.
            defs.Add(Def("other", effects: new[] { Param("skin", 0.1f) }));
            stats.Effects.AddEffect("other");

            Assert.That(stats.Vulnerabilities.GetVulnerability(DamageType.Fire),
                Is.EqualTo(before - 0.5f).Within(0.0001f),
                "the deduction must survive the next reset-then-replay (OverrideVulnerabilityBaseline)");
        }

        // ── 4. The payload router ─────────────────────────────────────────────

        [Test]
        public void Payload_Burning_DamagesTheOwner()
        {
            // EffectPayloads.Run is the port of Effect.secEffect's damage branch. A burning effect must
            // reach UnitStats.Damage — this is the link the "live unit is not inert" claim rests on.
            var (stats, defs) = NewWired();
            defs.Add(Def("burning", value: 3f, durationTicks: 30));

            stats.Effects.AddEffect("burning");
            float before = stats.CurrentHp.Value;

            // Tick a full second so the once-per-second payload fires (Effect.as:492, t%30==0).
            stats.TickEffects(1f);

            Assert.That(stats.CurrentHp.Value, Is.EqualTo(before - 3f).Within(0.001f),
                "burning's payload must run owner.damage(val)");
        }

        [Test]
        public void Payload_Hydra_HealsTheOwner_AndCallsTheSink()
        {
            var (stats, defs) = NewWired();
            defs.Add(Def("hydra", value: 5f, durationTicks: 30));
            stats.Damage(40f);

            int sinkCalls = 0;
            stats.EffectPayloadSink = _ => sinkCalls++;

            stats.Effects.AddEffect("hydra");
            float before = stats.CurrentHp.Value;
            stats.TickEffects(1f);

            Assert.That(stats.CurrentHp.Value, Is.EqualTo(before + 5f).Within(0.001f), "hydra heals the unit");
            Assert.That(sinkCalls, Is.GreaterThan(0), "the player-only extension sink must be invoked");
        }

        [Test]
        public void Payload_EmitterOnly_IsRecordedUnmapped_NotSilentlyDropped()
        {
            // blindness/namok/fetter/inhibitor have emitter payloads the port does not model. The
            // discipline is to record the gap, not hide it — this pins that.
            var (stats, defs) = NewWired();
            defs.Add(Def("blindness", durationTicks: 30));

            stats.Effects.AddEffect("blindness");
            stats.TickEffects(1f);

            Assert.That(stats.Effects.UnmappedParamNames.ContainsKey("payload:blindness"), Is.True,
                "an unmodelled payload must be visible in the unmapped readback");
        }

        [Test]
        public void Payload_DrunkBelowLevel4_DoesNotDamage()
        {
            // Effect.as:438-442 — the poison payload keys on level > 3, and only `drunk` escalates. So a
            // fresh drunk (level 1) must deal nothing. This is a negative control for the payload tests.
            var (stats, defs) = NewWired();
            defs.Add(Def("drunk", value: 7f, durationTicks: 300, lvl1: 2));

            stats.Effects.AddEffect("drunk");
            float before = stats.CurrentHp.Value;
            stats.TickEffects(1f);

            Assert.That(stats.CurrentHp.Value, Is.EqualTo(before).Within(0.001f),
                "a level-1 drunk deals no poison damage");
        }

        // ── 5. The param sink (the player/CharacterStats seam) ────────────────

        [Test]
        public void ParamSink_TakesPrecedenceOverTheUnitFieldWrite()
        {
            // The player's <sk> writes route through CharacterStats.ApplyNamedStat, not onto the unit's
            // own fields. Installing a sink must therefore divert the write — which is what makes the
            // player's maxhp reach the RPG block instead of the raw UnitStats field.
            var (stats, defs) = NewWired(PersMode.Player);
            defs.Add(Def("buff", effects: new[] { Param("maxhp", 50f) }));

            float unitMaxHpBefore = stats.MaxHp.Value;
            var sinked = new List<string>();
            stats.EffectParamSink = (param, index, effectId) => sinked.Add($"{param.id}@{index}");

            stats.Effects.AddEffect("buff");

            Assert.That(sinked, Contains.Item("maxhp@1"), "the player param pass uses eff.lvl as the index");
            Assert.That(stats.MaxHp.Value, Is.EqualTo(unitMaxHpBefore).Within(0.001f),
                "with a sink installed the unit's own field must NOT also be written");
        }
    }
}
