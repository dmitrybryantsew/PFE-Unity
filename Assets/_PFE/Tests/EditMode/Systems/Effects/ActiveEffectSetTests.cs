using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Effects;

namespace PFE.Tests.EditMode.Systems.Effects
{
    /// <summary>
    /// Pins the status-effect runtime — <see cref="ActiveEffectSet"/> — against AS3's
    /// <c>fe.unit.Effect</c> and the effect half of <c>Unit.step</c>/<c>Unit.addEffect</c>.
    ///
    /// <para><b>Every test here is written to be able to fail, and the fixtures are built to prove
    /// it.</b> The failure mode this project keeps hitting is a test that passes with the feature
    /// deleted — a guard removed from the filter but left in the gate, or a fixture that exercises the
    /// writer rather than the mover. So each assertion below names the oracle line it pins, and the
    /// helpers (<see cref="FakeHost"/>, <see cref="FakeDefinitions"/>) are deliberately minimal: they
    /// record what happened rather than reimplementing it, so an assertion cannot be satisfied by the
    /// fake doing the work.</para>
    ///
    /// <para><b>The oracle.</b> <c>fe/unit/Effect.as</c> throughout — <c>:67-137</c> (getXmlParam),
    /// <c>:139-220</c> (setEff), <c>:222-253</c> (checkT), <c>:282-403</c> (unsetEff), <c>:490-509</c>
    /// (step) — plus <c>fe/unit/Unit.as:3351-3404</c> (addEffect's merge rules) and <c>:3466-3495</c>
    /// (setEffParams' reset-then-replay).</para>
    /// </summary>
    [TestFixture]
    public class ActiveEffectSetTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        /// <summary>
        /// A definition built in code, so a test never depends on imported assets.
        ///
        /// <para><b>Why a POCO and not <c>ScriptableObject.CreateInstance</c>.</b> The asset type is a
        /// <c>ScriptableObject</c>, and a <c>ScriptableObject</c> subclass can be instantiated
        /// <i>only</i> inside a running editor: <c>CreateInstance</c> throws <c>SecurityException:
        /// ECall methods must be packaged into a system module</c>, and even
        /// <c>RuntimeHelpers.GetUninitializedObject</c> returns <c>null</c> for such a type (both were
        /// measured). So a fixture that builds a definition that way can be <b>arranged</b> only in the
        /// editor — which is how 846 of this project's tests came to "fail" headless without a single
        /// real assertion being reached. A test whose arrange step cannot run has no way to go red.</para>
        ///
        /// <para>The runtime reads only data from a template, so the fixture supplies
        /// <see cref="TestEffectTemplate"/> — a plain object implementing the same engine-free
        /// <see cref="IEffectTemplate"/> the asset implements. The runtime under test is then byte-for-
        /// byte the production code path, and it runs identically in the editor and under
        /// <c>dotnet</c>.</para>
        /// </summary>
        private static IEffectTemplate Def(
            string id,
            EffectType type = EffectType.Timed,
            int durationTicks = 30,
            bool forever = false,
            float value = 0f,
            bool add = false,
            string afterEffectId = null,
            int lvl1 = 0,
            int lvl2 = 0,
            int lvl3 = 0,
            string[] deletes = null,
            EffectParam[] effects = null)
        {
            return new TestEffectTemplate
            {
                effectId = id,
                type = type,
                durationTicks = durationTicks,
                forever = forever,
                value = value,
                add = add,
                afterEffectId = afterEffectId,
                lvl1 = lvl1,
                lvl2 = lvl2,
                lvl3 = lvl3,
                deletesOnStart = deletes,
                effects = effects,
            };
        }

        /// <summary>
        /// A plain-C# effect template — the fixture's stand-in for the <c>ScriptableObject</c> asset.
        /// Implements the same <see cref="IEffectTemplate"/> the asset does, so the runtime under test
        /// cannot tell them apart, and unlike the asset it can be constructed outside the editor.
        /// </summary>
        private sealed class TestEffectTemplate : IEffectTemplate
        {
            public string effectId { get; set; }
            public EffectType type { get; set; }
            public int durationTicks { get; set; }
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

        /// <summary>A resolver over a fixed dictionary — the <c>AllData.d.eff.(@id == id)</c> stand-in.</summary>
        private sealed class FakeDefinitions : IEffectDefinitionResolver
        {
            private readonly Dictionary<string, IEffectTemplate> _byId = new();

            public void Add(IEffectTemplate definition) => _byId[definition.effectId] = definition;

            public IEffectTemplate Resolve(string effectId)
            {
                if (string.IsNullOrEmpty(effectId))
                {
                    return null;
                }

                return _byId.TryGetValue(effectId, out IEffectTemplate d) ? d : null;
            }
        }

        /// <summary>
        /// Records callbacks without doing any work, so an assertion about a payload firing cannot be
        /// satisfied by the host computing anything itself.
        /// </summary>
        private sealed class FakeHost : IEffectHost
        {
            public readonly List<string> Started = new();
            public readonly List<string> Ended = new();
            public readonly List<string> Payloads = new();
            public readonly List<string> StepVisuals = new();

            /// <summary>
            /// Payload and step-visual callbacks interleaved in one list, so their <i>order</i> is
            /// assertable. The per-callback lists above cannot express it: a payload that ran after the
            /// step visual would leave both of them in the same state.
            /// </summary>
            public readonly List<string> PayloadAndVisualOrder = new();

            public int ParamsChangedCount;

            public void OnEffectStarted(ActiveEffect effect, ActiveEffectSet set) => Started.Add(effect.Id);

            public void OnEffectEnded(ActiveEffect effect, ActiveEffectSet set) => Ended.Add(effect.Id);

            public void OnEffectPayload(ActiveEffect effect, ActiveEffectSet set)
            {
                Payloads.Add(effect.Id);
                PayloadAndVisualOrder.Add("payload:" + effect.Id);
            }

            public void OnEffectStepVisual(ActiveEffect effect, ActiveEffectSet set)
            {
                StepVisuals.Add(effect.Id);
                PayloadAndVisualOrder.Add("visual:" + effect.Id);
            }

            public void OnEffectParamsChanged(ActiveEffectSet set) => ParamsChangedCount++;
        }

        /// <summary>A receiver that answers a fixed susceptibility set.</summary>
        private sealed class FakeReceiver : IEffectReceiver
        {
            private readonly HashSet<DamageType> _susceptible;

            public FakeReceiver(ActiveEffectSet set, params DamageType[] susceptible)
            {
                Effects = set;
                _susceptible = new HashSet<DamageType>(susceptible);
            }

            public ActiveEffectSet Effects { get; }

            public bool IsSusceptibleTo(DamageType type) => _susceptible.Contains(type);
        }

        private static (ActiveEffectSet Set, FakeHost Host, FakeDefinitions Defs) NewSet()
        {
            var defs = new FakeDefinitions();
            var host = new FakeHost();
            return (new ActiveEffectSet(host, defs), host, defs);
        }

        // ── 1. Adding ─────────────────────────────────────────────────────────

        [Test]
        public void AddEffect_CreatesInstanceSeededFromDefinition()
        {
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210, value: 2f));

            ActiveEffect e = set.AddEffect("burning");

            Assert.That(e, Is.Not.Null);
            Assert.That(set.Count, Is.EqualTo(1));
            // 210 ticks is the imported `t='7' * 30` value — Effect.as:82.
            Assert.That(e.TicksRemaining, Is.EqualTo(210));
            Assert.That(e.Value, Is.EqualTo(2f), "val from the definition when the caller passes 0");
        }

        [Test]
        public void AddEffect_WithNoDefinition_IsRefused()
        {
            // The oracle would build an Effect whose getXmlParam finds no node, leaving t=1/tip=0 — a
            // one-tick phantom. The port refuses instead, because a phantom effect is a plausible
            // object with no behaviour. This test pins that refusal.
            var (set, host, _) = NewSet();

            ActiveEffect e = set.AddEffect("no_such_effect");

            Assert.That(e, Is.Null);
            Assert.That(set.Count, Is.EqualTo(0), "a missing definition must not materialise an effect");
            Assert.That(host.Started, Is.Empty);
        }

        [Test]
        public void AddEffect_WithEmptyId_ReturnsNull()
        {
            var (set, _, _) = NewSet();

            Assert.That(set.AddEffect(null), Is.Null);
            Assert.That(set.AddEffect(""), Is.Null);
            Assert.That(set.Count, Is.EqualTo(0));
        }

        [Test]
        public void AddEffect_CallerValueWins_ButZeroFallsBack()
        {
            // Effect.as:87-90 — `if(this.val == 0) this.val = node.@val`.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", value: 2f));

            ActiveEffect overridden = set.AddEffect("burning", 9f);
            Assert.That(overridden.Value, Is.EqualTo(9f), "a non-zero caller value wins over the definition");

            ActiveEffect zero = set.AddEffect("freezing", 0f);
            // 'freezing' is not registered — this asserts the fallback path did not create one.
            Assert.That(zero, Is.Null);
        }

        // ── 2. Merge rules ────────────────────────────────────────────────────

        [Test]
        public void Add_SameId_KeepsHigherValue()
        {
            // Unit.as:3377-3380 — `if(existing.val > incoming.val) incoming.val = existing.val`.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", value: 0f));

            set.AddEffect("burning", 5f);
            ActiveEffect second = set.AddEffect("burning", 2f);

            Assert.That(second.Value, Is.EqualTo(5f), "a weaker re-application must not downgrade a strong one");
            Assert.That(set.Count, Is.EqualTo(1), "the merge reuses the slot");
        }

        [Test]
        public void Add_SameId_WithoutAddFlag_ReplacesDuration()
        {
            // Unit.as:3386 — the incoming duration replaces the old one. Note this is NOT a max: a
            // shorter re-application shortens the effect.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 300, add: false));

            set.AddEffect("burning");
            ActiveEffect second = set.AddEffect("burning");

            Assert.That(second.TicksRemaining, Is.EqualTo(300), "the new duration replaces, it does not sum");
        }

        [Test]
        public void Add_WithAddFlag_SumsAndClampsDuration()
        {
            // Unit.as:3379-3385 — `t += existing.t; if(t > 30000) t = 30000`.
            var (set, _, defs) = NewSet();
            defs.Add(Def("drunk", durationTicks: 3600, add: true));

            set.AddEffect("drunk");
            ActiveEffect stacked = set.AddEffect("drunk");

            Assert.That(stacked.TicksRemaining, Is.EqualTo(7200), "add sums the durations");

            // And the clamp.
            defs.Add(Def("bigstack", durationTicks: 20000, add: true));
            set.AddEffect("bigstack");
            ActiveEffect clamped = set.AddEffect("bigstack");
            Assert.That(clamped.TicksRemaining, Is.EqualTo(ActiveEffectSet.MaxStackedTicks),
                "the sum is clamped to 30000 — Unit.as:3381-3384");
        }

        [Test]
        public void Add_MatchingPendingAfterEffect_ReusesTheSlot()
        {
            // Unit.as:3376 — `existing.id == param1 || existing.id == incoming.post`. Applying `rage`
            // when a pending `post_rage` exists must replace it, not add a second slot.
            var (set, _, defs) = NewSet();
            defs.Add(Def("rage", durationTicks: 30));
            defs.Add(Def("post_rage", durationTicks: 30, afterEffectId: "rage"));

            set.AddEffect("post_rage");   // occupies the slot, waiting
            ActiveEffect applied = set.AddEffect("rage");

            Assert.That(applied.Id, Is.EqualTo("rage"));
            Assert.That(set.Count, Is.EqualTo(1),
                "the incoming effect matched the pending aftereffect's slot");
        }

        [Test]
        public void Add_FoodChannel_ReplacesWholesaleRegardlessOfValue()
        {
            // Unit.as:3366-3371 — the tip==3 branch runs FIRST and returns, so a food buff replaces the
            // old one outright. The value-merge and duration rules do not apply.
            var (set, _, defs) = NewSet();
            defs.Add(Def("f_hp", EffectType.Food, durationTicks: 3000, value: 100f));
            defs.Add(Def("f_speed", EffectType.Food, durationTicks: 30, value: 1f));

            set.AddEffect("f_hp");
            ActiveEffect second = set.AddEffect("f_speed");

            Assert.That(set.Count, Is.EqualTo(1), "only one food effect at a time");
            Assert.That(second.Id, Is.EqualTo("f_speed"), "the new food wins outright");
            Assert.That(second.Value, Is.EqualTo(1f),
                "the food branch does NOT take the higher value — it replaced wholesale");
            Assert.That(second.TicksRemaining, Is.EqualTo(30),
                "and it does NOT take the longer duration");
        }

        // ── 3. The `del` list ─────────────────────────────────────────────────

        [Test]
        public void Add_AppliesDeleteList_ToExistingEffects()
        {
            // Effect.as:143-156 — burning deletes freezing and vice versa.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210, deletes: new[] { "freezing" }));
            defs.Add(Def("freezing", durationTicks: 450, deletes: new[] { "burning" }));

            set.AddEffect("burning");
            set.AddEffect("freezing");

            // `del` runs the target's unsetEff, which only sets vse (Effect.as:143-156). The array is
            // not compacted until the next frame's step loop (Unit.as:3148-3157), so immediately after
            // the add BOTH are still present and the burn is merely marked. Asserting Count == 1 here
            // would assert a splice the oracle has not performed yet — the same deferral the
            // RemoveEffect test pins.
            Assert.That(set.Count, Is.EqualTo(2), "unset is deferred — the burn is marked, not yet spliced");

            ActiveEffect burn = null;
            foreach (ActiveEffect e in set.Effects)
            {
                if (e.Id == "burning") burn = e;
            }

            Assert.That(burn, Is.Not.Null, "the burn is still in the array");
            Assert.That(burn.IsBeingUnset, Is.True, "the freeze's del list marked the burn, not the reverse");
            Assert.That(burn.Tip, Is.EqualTo(EffectType.Timed));

            set.Tick();

            Assert.That(set.Count, Is.EqualTo(1), "the next frame splices the burn");
            Assert.That(set.Has("burning"), Is.False);
            Assert.That(set.Has("freezing"), Is.True);
        }

        // ── 4. Tick cadence and expiry ────────────────────────────────────────

        [Test]
        public void Tick_FiresPayloadOnCanonicalSecondBoundaries()
        {
            // Effect.as:490-509 — `if(this.t % 30 == 0) this.secEffect()` is evaluated BEFORE the
            // decrement, then `if(this.t <= 0) unsetEff()`. A 30-tick effect fires ONCE:
            //
            //    frame  1: t=30 -> fire, --t = 29
            //   frame 29: t= 2 -> --t = 1
            //   frame 30: t= 1 -> no fire, --t = 0 -> t<=0 -> unsetEff()
            //
            // There is no second fire at t=0: the t<=0 branch runs in the same frame that would have
            // carried t to 0, and the effect is spliced before it is ever stepped at t=0. (An earlier
            // version of this fixture asserted two fires — a misreading that a test able to run
            // off-editor immediately caught.)
            var (set, host, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30));

            set.AddEffect("burning");
            for (int i = 0; i < 30; i++)
            {
                set.Tick();
            }

            Assert.That(host.Payloads, Is.EqualTo(new[] { "burning" }),
                "a 30-tick effect fires its payload exactly once, at t=30 — Effect.as:490-509");
        }

        [Test]
        public void Tick_FiresTheStepVisualEveryTick_NotOncePerSecond()
        {
            // AS3 `step()` (:490-497) calls `stepEffect()` on EVERY frame and `secEffect()` only on the
            // `t % 30` boundary. The flame is on the step path (:480), so a 30-tick burn asks for 30
            // flames and exactly one payload. Merging the two cadences would draw the flame once a
            // second — a stutter that reads as an art problem rather than a scheduling one.
            var (set, host, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30));

            set.AddEffect("burning");
            for (int i = 0; i < 30; i++)
            {
                set.Tick();
            }

            Assert.That(host.StepVisuals.Count, Is.EqualTo(30),
                "stepEffect runs every tick — Effect.as:496");
            Assert.That(host.Payloads, Is.EqualTo(new[] { "burning" }),
                "while the payload still fires exactly once — Effect.as:492");
        }

        [Test]
        public void Tick_AsksForTheStepVisualForEveryEffect_EvenOneThatDrawsNothing()
        {
            // The callback is unconditional by design: the id test lives in EffectVisualRules, so this
            // layer must not learn which ids emit. A set that only asked for `burning` would put a copy
            // of the rules' id list in the scheduler — the exact duplication the rules class exists to
            // remove.
            var (set, host, defs) = NewSet();
            defs.Add(Def("blindness", durationTicks: 3));

            set.AddEffect("blindness");
            set.Tick();

            Assert.That(host.StepVisuals, Is.EqualTo(new[] { "blindness" }),
                "`blindness` draws nothing on the step path, but the scheduler still asks");
        }

        [Test]
        public void Tick_RunsTheStepVisualAfterThePayload_MatchingTheOracleOrder()
        {
            // `step()` checks `t % 30` and runs `secEffect()` FIRST, then `stepEffect()` (:492-496).
            // The order is observable for `burning`: the payload applies fire damage, and the flame's
            // own gate reads the owner's state, so drawing before the damage would show a frame of fire
            // on a unit the burn is about to kill.
            var (set, host, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30));

            set.AddEffect("burning");
            set.Tick(); // the t=30 frame, which is both a payload tick and a step tick

            Assert.That(host.PayloadAndVisualOrder, Is.EqualTo(new[] { "payload:burning", "visual:burning" }),
                "secEffect before stepEffect — Effect.as:492-496");
        }

        [Test]
        public void Tick_ExpiresAtZero_AndReportsEnded()
        {
            var (set, host, defs) = NewSet();
            defs.Add(Def("blindness", durationTicks: 3));

            set.AddEffect("blindness");
            set.Tick();   // t: 3 -> 2
            set.Tick();   // t: 2 -> 1
            Assert.That(set.Count, Is.EqualTo(1), "still live at t=1");

            set.Tick();   // t: 1 -> 0 -> unsetEff (marks vse); the splice is the NEXT frame
            Assert.That(set.Count, Is.EqualTo(1), "t<=0 marks the effect; the oracle removes it next frame");
            Assert.That(set.Effects[0].IsBeingUnset, Is.True);
            Assert.That(host.Ended, Is.Empty, "not removed yet, so no end callback yet");

            set.Tick();   // the vse effect is dropped at the top of the loop, without being stepped
            Assert.That(set.Count, Is.EqualTo(0), "spliced on the following frame — Unit.as:3148-3157");
            Assert.That(host.Ended, Is.EqualTo(new[] { "blindness" }),
                "the end callback fires where the effect actually leaves the array");
        }

        [Test]
        public void Tick_ForeverEffect_ReloopsInsteadOfExpiring()
        {
            // Effect.as:498-508 — `if(this.forever) t = 30; else unsetEff()`.
            var (set, host, defs) = NewSet();
            defs.Add(Def("stealth_armor", durationTicks: 30, forever: true));

            set.AddEffect("stealth_armor");
            for (int i = 0; i < 100; i++)
            {
                set.Tick();
            }

            Assert.That(set.Count, Is.EqualTo(1), "a permanent effect never expires");
            Assert.That(host.Ended, Is.Empty);
            Assert.That(set.Effects[0].TicksRemaining, Is.GreaterThan(0), "t relooped to 30 each window");
        }

        [Test]
        public void Tick_AtNonCanonicalRate_CountsEffectTicksInCanonicalFrames()
        {
            // The load-bearing rate test. A 30-tick effect must last 30 CANONICAL frames — one second —
            // whether the port runs at 30 Hz or 120 Hz. At 120 Hz StepScale is 0.25, so it takes 120
            // port ticks. If the set counted port ticks instead, the effect would last 0.25s and this
            // test would fail.
            var (set, host, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30, value: 2f));

            set.AddEffect("burning");

            const float stepScale120Hz = 30f / 120f;   // SimClock.StepScale at 120 Hz
            // 119 port ticks = 29.75 canonical frames, so 29 whole frames have run: t is 1.
            for (int i = 0; i < 119; i++)
            {
                set.Tick(stepScale120Hz);
            }

            Assert.That(set.Count, Is.EqualTo(1), "still live after 119/120 of a second at 120 Hz");
            Assert.That(set.Effects[0].TicksRemaining, Is.EqualTo(1),
                "119 canonical frames done, t is at 1 — the count is in CANONICAL frames, not port ticks");

            set.Tick(stepScale120Hz);   // the 120th port tick completes frame 30: t -> 0, marks vse
            Assert.That(set.Effects[0].TicksRemaining, Is.EqualTo(0), "t reached 0 after one canonical second");
            Assert.That(host.Payloads.Count, Is.EqualTo(1),
                "exactly one payload — the cadence is in canonical frames too (t%30 at t=30)");

            // The splice is deferred by CANONICAL frames, not port ticks: at 120 Hz one canonical
            // frame is four port ticks. So draining the carry takes up to 4 more ticks — the point is
            // that it is bounded by one canonical frame, not that it happens on the very next call.
            for (int i = 0; i < 4 && set.Count > 0; i++)
            {
                set.Tick(stepScale120Hz);
            }

            Assert.That(set.Count, Is.EqualTo(0), "gone within one canonical frame of t=0");
        }

        [Test]
        public void Tick_WithZeroStepScale_DoesNothing()
        {
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30));

            set.AddEffect("burning");
            set.Tick(0f);

            Assert.That(set.Effects[0].TicksRemaining, Is.EqualTo(30), "a zero step must not advance");
        }

        // ── 5. Deferred removal ───────────────────────────────────────────────

        [Test]
        public void RemoveEffect_MarksUnset_ButKeepsItForOnePass()
        {
            // Effect.as:301 / Unit.as:3389-3398 — remEffect sets vse and the splice is deferred to the
            // next tick's step. This is what lets the param pass undo the writes before the effect is
            // discarded.
            var (set, host, defs) = NewSet();
            defs.Add(Def("stealth", durationTicks: 1800));

            ActiveEffect e = set.AddEffect("stealth");
            set.RemoveEffect("stealth");

            Assert.That(e.IsBeingUnset, Is.True, "vse is set immediately");
            Assert.That(set.Count, Is.EqualTo(1), "but the effect is still in the array");
            Assert.That(host.Ended, Is.Empty, "and the end callback has not fired yet");

            set.Tick();

            Assert.That(set.Count, Is.EqualTo(0), "the splice happens on the next tick");
            Assert.That(host.Ended, Is.EqualTo(new[] { "stealth" }));
        }

        [Test]
        public void RemoveEffect_WithNoMatch_IsANoOp()
        {
            var (set, host, defs) = NewSet();
            defs.Add(Def("stealth", durationTicks: 30));

            set.AddEffect("stealth");
            set.RemoveEffect("not_present");

            Assert.That(set.Count, Is.EqualTo(1));
            Assert.That(host.Ended, Is.Empty);
        }

        // ── 6. The post transition ────────────────────────────────────────────

        [Test]
        public void Unset_WithAfterEffect_RetargetsInsteadOfRemoving()
        {
            // Effect.as:313-336 — a `postbad` does not remove the effect; it RETARGETS the same
            // instance to the comedown id, re-reads the new definition (so the new t applies) and
            // clears vse. A port that simply removed the effect would lose the comedown entirely.
            var (set, host, defs) = NewSet();
            defs.Add(Def("mint", durationTicks: 3, afterEffectId: "post_mint"));
            defs.Add(Def("post_mint", durationTicks: 900, value: 3f));

            ActiveEffect e = set.AddEffect("mint");
            set.Tick();   // 3 -> 2
            set.Tick();   // 2 -> 1
            set.Tick();   // 1 -> 0 -> unset -> retarget

            Assert.That(set.Count, Is.EqualTo(1), "the instance survived the transition");
            Assert.That(e.Id, Is.EqualTo("post_mint"), "and now represents the comedown");
            Assert.That(e.TicksRemaining, Is.EqualTo(900), "the new definition's duration was read");
            Assert.That(e.IsBeingUnset, Is.False, "vse cleared — it is active again");
            Assert.That(host.Ended, Is.Empty, "no end callback: nothing was removed");
        }

        // ── 7. Level escalation ───────────────────────────────────────────────

        [Test]
        public void CheckLevel_EscalatesOnlyWhenLvl1IsPositive()
        {
            // Effect.as:225 — `if(this.lvl1 > 0)`. That gate is why only `drunk` escalates: it is the
            // only definition in the data carrying lvl1. A port that read 0 as "escalate immediately"
            // would put every effect at level 4.
            var (set, _, defs) = NewSet();
            defs.Add(Def("naloxone", durationTicks: 9000, lvl1: 0));

            ActiveEffect e = set.AddEffect("naloxone");

            Assert.That(set.CheckLevel(e), Is.False, "no escalation without lvl1");
            Assert.That(e.Level, Is.EqualTo(1), "level 1 is the floor, and lvl1=0 is the gate");
        }

        [Test]
        public void CheckLevel_ClimbsWithDuration()
        {
            // drunk's real thresholds (AllData): lvl1=150, lvl2=350, lvl3=750, compared against t/30.
            var (set, _, defs) = NewSet();
            defs.Add(Def("drunk", durationTicks: 3600, lvl1: 150, lvl2: 350, lvl3: 750));

            ActiveEffect e = set.AddEffect("drunk");

            e.TicksRemaining = 3600;   // 120s
            set.CheckLevel(e);
            Assert.That(e.Level, Is.EqualTo(1), "120s is below lvl1=150");

            e.TicksRemaining = 150 * 30 + 30;   // just past 150s
            set.CheckLevel(e);
            Assert.That(e.Level, Is.EqualTo(2));

            e.TicksRemaining = 350 * 30 + 30;
            set.CheckLevel(e);
            Assert.That(e.Level, Is.EqualTo(3));

            e.TicksRemaining = 750 * 30 + 30;
            set.CheckLevel(e);
            Assert.That(e.Level, Is.EqualTo(4));
        }

        // ── 8. The param enumeration (the two-path divergence) ────────────────

        private static EffectParam ResParam(string damageTypeIndex, float active, float removal)
        {
            return new EffectParam
            {
                id = damageTypeIndex,
                IsResistance = true,
                op = EffectParamRef.Assign,
                perLevel = new[] { removal, active },
                hasDelta = false,
            };
        }

        [Test]
        public void EnumerateParams_NpcPath_UsesIndexOneActive_AndZeroWhenUnset()
        {
            // Unit.as:3495 — `setSkillParam(sk, eff.vse ? 0 : 1)`. The NPC path passes a HARDCODED 1,
            // so a removed effect is replayed with index 0 to undo its write.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 30, effects: new[] { ResParam("11", 0.5f, 0f) }));

            set.AddEffect("burning");
            foreach ((EffectParam _, int index) in set.EnumerateParams(PersMode.Npc))
            {
                Assert.That(index, Is.EqualTo(1), "index 1 while active");
            }

            set.RemoveEffect("burning");
            foreach ((EffectParam _, int index) in set.EnumerateParams(PersMode.Npc))
            {
                Assert.That(index, Is.EqualTo(0), "index 0 while being unset — that is the reset");
            }
        }

        [Test]
        public void EnumerateParams_PlayerPath_UsesLevel_AndSkipsUnset()
        {
            // Pers.as:2202 — `if(!eff.vse) setSkillParam(xml, eff.vse ? 0 : eff.lvl)`. The player uses
            // the live level AND skips removed effects entirely — the opposite of the NPC path. A port
            // that unified the two would lose one behaviour or the other.
            var (set, _, defs) = NewSet();
            defs.Add(Def("drunk", durationTicks: 3600, lvl1: 150, lvl2: 350, lvl3: 750,
                effects: new[] { ResParam("11", 0.5f, 0f) }));

            ActiveEffect e = set.AddEffect("drunk");
            e.TicksRemaining = 750 * 30 + 30;
            set.CheckLevel(e);

            foreach ((EffectParam _, int index) in set.EnumerateParams(PersMode.Player))
            {
                Assert.That(index, Is.EqualTo(4), "the player path passes the live level");
            }

            set.RemoveEffect("drunk");

            var count = 0;
            foreach ((EffectParam _, int _) in set.EnumerateParams(PersMode.Player))
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(0), "removed effects are skipped on the player path");
        }

        [Test]
        public void EffectParam_ValueForLevel_ReproducesTheFallbackChain()
        {
            // Unit.as:3429-3441 — vd wins; else v<index> when present; else v0.
            var withVector = new EffectParam
            {
                id = "x",
                perLevel = new[] { 10f, 20f, 30f },
                hasDelta = false,
            };

            Assert.That(withVector.ValueForLevel(0), Is.EqualTo(10f), "v0");
            Assert.That(withVector.ValueForLevel(2), Is.EqualTo(30f), "v2");
            Assert.That(withVector.ValueForLevel(9), Is.EqualTo(10f), "out of range falls back to v0");

            var withDelta = new EffectParam
            {
                id = "x",
                perLevel = new[] { 10f, 99f },
                delta = 5f,
                hasDelta = true,
            };

            Assert.That(withDelta.ValueForLevel(3), Is.EqualTo(25f),
                "vd present: v0 + index*vd = 10 + 3*5, and the vector is bypassed");
        }

        // ── 9. Receiver-side producers ────────────────────────────────────────

        [Test]
        public void Producers_WeaponDop_MapsShortCodeToEffectId()
        {
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210));
            var receiver = new FakeReceiver(set, DamageType.Fire);

            OnHitEffectProducers.Apply(
                receiver,
                weaponDopEffect: "igni",
                weaponDopDamage: 4f,
                weaponDopChance: 1f,
                dopChancePassed: false,   // ignored because chance >= 1
                ammoFireDamage: 0f,
                damageType: DamageType.PhysicalBullet,
                isExplosiveHit: false,
                wouldDie: false,
                targetIsImmuneToContusion: true);

            Assert.That(set.Count, Is.EqualTo(1));
            Assert.That(set.Has("burning"), Is.True);
            Assert.That(set.Effects[0].Value, Is.EqualTo(4f), "dopDamage is the payload");
        }

        [Test]
        public void Producers_WeaponDop_RespectsSusceptibilityGuard()
        {
            // Unit.as:3774 — `vulner[D_FIRE] > 0.1`. A fire-immune target must not catch fire from a
            // dopEffect, though it CAN from ammoFire (which has no guard — see the next test).
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210));
            var receiver = new FakeReceiver(set /* susceptible to nothing */);

            OnHitEffectProducers.Apply(
                receiver, "igni", 4f, 1f, false, 0f,
                DamageType.PhysicalBullet, false, false, true);

            Assert.That(set.Count, Is.EqualTo(0), "an immune target is not affected");
        }

        [Test]
        public void Producers_WeaponDop_BelowOneChance_UsesThePassedRollAndSpendsNoDraw()
        {
            // Unit.as:3771 — `dopCh >= 1 || Math.random() < dopCh`. A sub-1 chance honours the caller's
            // roll; a chance of exactly 1 ignores it. This is what keeps the RNG stream in one place.
            var (set, _, defs) = NewSet();
            defs.Add(Def("blindness", durationTicks: 30));
            var receiver = new FakeReceiver(set, DamageType.Laser);

            OnHitEffectProducers.Apply(
                receiver, "blind", 0f, 0.5f, dopChancePassed: false, ammoFireDamage: 0f,
                damageType: DamageType.PhysicalBullet, isExplosiveHit: false,
                wouldDie: false, targetIsImmuneToContusion: true);

            Assert.That(set.Count, Is.EqualTo(0), "the roll failed, so nothing is applied");

            OnHitEffectProducers.Apply(
                receiver, "blind", 0f, 0.5f, dopChancePassed: true, ammoFireDamage: 0f,
                damageType: DamageType.PhysicalBullet, isExplosiveHit: false,
                wouldDie: false, targetIsImmuneToContusion: true);

            Assert.That(set.Count, Is.EqualTo(1), "the roll passed");
        }

        [Test]
        public void Producers_AmmoFire_AppliesBurningWithNoSusceptibilityGuard()
        {
            // Unit.as:3830-3832 — `if(weap.ammoFire) addEffect("burning", weap.ammoFire)`. Note there is
            // NO vulner check here, unlike the dopEffect `igni` branch. A fire-immune target still
            // burns. That asymmetry is the oracle's and the test pins it so a "tidy-up" cannot close it.
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210));
            var receiver = new FakeReceiver(set /* susceptible to nothing */);

            OnHitEffectProducers.Apply(
                receiver, weaponDopEffect: null, weaponDopDamage: 0f, weaponDopChance: 1f,
                dopChancePassed: false, ammoFireDamage: 3f,
                damageType: DamageType.PhysicalBullet, isExplosiveHit: false,
                wouldDie: false, targetIsImmuneToContusion: true);

            Assert.That(set.Has("burning"), Is.True,
                "ammoFire has no susceptibility guard — a fire-immune target still burns");
            Assert.That(set.Effects[0].Value, Is.EqualTo(3f));
        }

        [Test]
        public void Producers_AmmoFire_ZeroAppliesNothing()
        {
            var (set, _, defs) = NewSet();
            defs.Add(Def("burning", durationTicks: 210));
            var receiver = new FakeReceiver(set, DamageType.Fire);

            OnHitEffectProducers.Apply(
                receiver, null, 0f, 1f, false, ammoFireDamage: 0f,
                DamageType.PhysicalBullet, false, false, true);

            Assert.That(set.Count, Is.EqualTo(0), "`if(weap.ammoFire)` is a non-zero test");
        }

        [Test]
        public void Producers_DopPoison_IsReportedNotTurnedIntoAnEffect()
        {
            // `poison`/`cut`/`stun`/`psy` are plain unit fields in the oracle (Unit.poison, Unit.cut,
            // Unit.stun), NOT effects. The producer records them as unmapped rather than inventing an
            // effect the oracle never creates.
            var (set, _, defs) = NewSet();
            var receiver = new FakeReceiver(set);

            OnHitEffectProducers.Apply(
                receiver, "poison", 5f, 1f, false, 0f,
                DamageType.PhysicalBullet, false, false, true);

            Assert.That(set.Count, Is.EqualTo(0), "no effect is created for a plain-field dop");
            Assert.That(set.UnmappedParamNames.ContainsKey("dopEffect:poison"), Is.True,
                "but the gap is recorded rather than silently dropped");
        }

        [Test]
        public void Producers_NullReceiver_DoesNothing()
        {
            Assert.DoesNotThrow(() => OnHitEffectProducers.Apply(
                null, "igni", 4f, 1f, false, 3f,
                DamageType.PhysicalBullet, true, true, false));
        }
    }
}
