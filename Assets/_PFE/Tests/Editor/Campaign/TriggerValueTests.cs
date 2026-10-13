using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// C0 of <c>docs/CampaignControl/05_IMPLEMENTATION_PLAN.md</c> — the trigger value type.
    ///
    /// <para><b>What this pins.</b> The oracle's table holds strings as well as ints
    /// (<c>"wait"</c> at <c>NPC.as:591</c>, a quest <c>@set</c> value at <c>Quest.as:332</c>), and the
    /// port typed it <c>int</c>, so those values were lost. Equally important, this pins the
    /// <b>corrected</b> reading of the two readers: AS3 <c>==</c>/<c>!=</c> coerce a <c>String</c>
    /// operand to <c>Number</c>, so a string <c>"1"</c> satisfies an int <c>== 1</c> and there is
    /// <b>no</b> divergence between <c>PipPage.as:1530</c> and <c>Location.as:1141</c>. An earlier
    /// edition of the research claimed there was one; <see cref="StringOne_CoercesToTheIntOne"/> is the
    /// control that would have caught it.</para>
    /// </summary>
    [TestFixture]
    public class TriggerValueTests
    {
        // ------------------------------------------------------------------ storage

        [Test]
        public void AStringValueSurvivesStorage()
        {
            TriggerValue v = TriggerValue.FromString("wait");

            Assert.That(v.Kind, Is.EqualTo(TriggerValue.TriggerKind.String),
                "Positive control: a non-numeric string is stored as a string.");
            Assert.That(v.IsSet, Is.True, "The key is present — AS3 `triggers[x] != null`.");
            Assert.That(v.RawString, Is.EqualTo("wait"));
            Assert.That(v.AsString(), Is.EqualTo("wait"));

            // Negative control: the whole point of the type. An int table could not hold this, and a
            // readout that coerced it would report 0 — a readout that lies.
            Assert.That(v.AsInt(-1), Is.EqualTo(-1),
                "`wait` has no numeric value (AS3 Number('wait') is NaN), so the caller's default is used.");
        }

        [Test]
        public void AnIntValueSurvivesStorage()
        {
            TriggerValue v = TriggerValue.FromInt(3);

            Assert.That(v.Kind, Is.EqualTo(TriggerValue.TriggerKind.Int));
            Assert.That(v.RawInt, Is.EqualTo(3));
            Assert.That(v.AsInt(0), Is.EqualTo(3));
            Assert.That(v.AsString(), Is.EqualTo("3"), "AS3 String(3) is '3'.");
        }

        [Test]
        public void AnAbsentTriggerIsNotAStoredZero()
        {
            // Project lesson #126: a *missing* collection member is a different answer from an *empty*
            // one, and the oracle asks both (`Land.as:821` tests `== null`; `Area.as:360` tests `!= 1`).
            TriggerValue absent = TriggerValue.Unset;
            TriggerValue zero = TriggerValue.FromInt(0);

            Assert.That(absent.IsSet, Is.False);
            Assert.That(zero.IsSet, Is.True, "A stored 0 is present — the oracle distinguishes these.");
            Assert.That(absent, Is.Not.EqualTo(zero));
            Assert.That(absent.AsInt(7), Is.EqualTo(7), "Absent has no number, so the default stands.");
            Assert.That(zero.AsInt(7), Is.EqualTo(0), "A stored 0 is a number, so the default is ignored.");
        }

        // ------------------------------------------------------------------ the coercing comparison (the corrected finding)

        [Test]
        public void StringOne_CoercesToTheIntOne()
        {
            // AS3 `"1" != 1` is FALSE: abstract equality coerces the String to a Number. This is why the
            // two oracle readers do NOT disagree, and why normalising "1" to 1 is harmless rather than
            // breaking one of them. If this ever fails, the coercion has been replaced by identity and
            // PipPage.as:1530's gate has started rejecting a string "1" that the oracle accepts.
            TriggerValue stringOne = TriggerValue.FromString("1");

            Assert.That(stringOne.EqualsInt(1), Is.True,
                "`\"1\" == 1` is true in AS3 (PipPage.as:1530's `!= 1` gate must PASS a string '1').");

            // Negative control: a non-numeric string must NOT equal 1, or the coercion is really an
            // "any string is 1" bug. AS3 gives `NaN == 1` -> false.
            Assert.That(TriggerValue.FromString("wait").EqualsInt(1), Is.False);
            Assert.That(TriggerValue.Unset.EqualsInt(1), Is.False, "An absent key is not 1.");
        }

        [Test]
        public void RefillSweep_MatchesOnlyTheStringWait()
        {
            // Game.as:516-522 sweeps `if(triggers[k] == "wait") triggers[k] = 1`. A value stored as the
            // int 1 must answer false — "wait" does not parse as a number — and an absent key must too.
            Assert.That(TriggerValue.FromString("wait").EqualsString("wait"), Is.True,
                "Positive control: the sweep finds its own value.");
            Assert.That(TriggerValue.FromInt(1).EqualsString("wait"), Is.False,
                "An int 1 is not the string 'wait' (AS3: 1 == NaN is false).");
            Assert.That(TriggerValue.Unset.EqualsString("wait"), Is.False);
        }

        [Test]
        public void GreaterThanZero_MatchesAs3RelationalSemantics()
        {
            Assert.That(TriggerValue.FromInt(2).IsGreaterThanZero(), Is.True);
            Assert.That(TriggerValue.FromInt(0).IsGreaterThanZero(), Is.False);

            // `"wait" > 0` is `NaN > 0` is false — NOT "0 > 0" false-by-accident. A readout that
            // coerced it to 0 would make `<= 0` true as well, which the oracle never does.
            Assert.That(TriggerValue.FromString("wait").IsGreaterThanZero(), Is.False);
            Assert.That(TriggerValue.Unset.IsGreaterThanZero(), Is.False);

            // Positive control for the coercion: a numeric string IS comparable.
            Assert.That(TriggerValue.FromString("4").IsGreaterThanZero(), Is.True,
                "`\"4\" > 0` is true in AS3 — the coercion is real, not a blanket refusal.");
        }

        [Test]
        public void ScriptCommandN_IsAlwaysAStringAndDefaultsToMinusOne()
        {
            // Script.as:71 `var _loc6_:String = "-1"`, overwritten only when @n is present and
            // non-empty (:97-99). `param1.n` is therefore never null, com's `!= null` guard (:426)
            // is always true, and the int-1 `else` arm (:432) is dead. A bare `trigger` stores "-1".
            TriggerValue bare = TriggerValue.FromScriptCommandN(null);
            Assert.That(bare.Kind, Is.EqualTo(TriggerValue.TriggerKind.String),
                "The payload is a String — analiz types `n` as String.");
            Assert.That(bare.RawString, Is.EqualTo("-1"), "The oracle's own default, not a substituted 1.");
            Assert.That(bare.IsGreaterThanZero(), Is.False,
                "`\"-1\" > 0` is false — the dead branch's int 1 WOULD have been > 0. A real difference.");

            Assert.That(TriggerValue.FromScriptCommandN(string.Empty).RawString, Is.EqualTo("-1"),
                "An empty @n has length 0, so the default stands (`if(param1.@n.length())`).");
            Assert.That(TriggerValue.FromScriptCommandN("4").RawString, Is.EqualTo("4"));
            Assert.That(TriggerValue.FromScriptCommandN("4").EqualsInt(4), Is.True,
                "Numeric @n still opens a numeric gate — the coercion does the work.");

            // The case the old int.TryParse mapping lost: this is the value `refill` sweeps for.
            Assert.That(TriggerValue.FromScriptCommandN("wait").RawString, Is.EqualTo("wait"));
            Assert.That(TriggerValue.FromScriptCommandN("wait").EqualsString("wait"), Is.True,
                "…so the sweep can find it. `int.TryParse(\"wait\")` failed, and the old port wrote the int 1.");
        }

        [Test]
        public void ToString_NeverHidesAnAbsentEntry()
        {
            Assert.That(TriggerValue.Unset.ToString(), Is.EqualTo("(unset)"),
                "A readout must not render an absent trigger as '0'.");
            Assert.That(TriggerValue.FromInt(0).ToString(), Is.EqualTo("0"));
            Assert.That(TriggerValue.FromString("wait").ToString(), Is.EqualTo("wait"));
        }

        // ------------------------------------------------------------------ the manager

        [Test]
        public void Manager_StoresAStringTriggerEndToEnd()
        {
            var manager = new CampaignManager(null, null, null, null, null);

            manager.SetTrigger("patient_tr2", TriggerValue.FromString("wait"));

            TriggerValue read = manager.GetTrigger("patient_tr2");
            Assert.That(read.Kind, Is.EqualTo(TriggerValue.TriggerKind.String));
            Assert.That(read.RawString, Is.EqualTo("wait"),
                "The string reached the table intact (NPC.as:591 writes exactly this value).");
            Assert.That(read.AsInt(0), Is.EqualTo(0));

            // Negative control: a different key is still absent, so the read above is about the write
            // and not about GetTrigger answering everything.
            Assert.That(manager.GetTrigger("never_set").IsSet, Is.False);
        }

        [Test]
        public void Manager_IntOverloadStillDefaultsToOne()
        {
            var manager = new CampaignManager(null, null, null, null, null);

            manager.SetTrigger("dial_calam2");

            Assert.That(manager.GetTrigger("dial_calam2").RawInt, Is.EqualTo(1),
                "AS3 setTrigger(name, value = 1) — the default is 1 (Game.as:676-679).");
        }

        // ------------------------------------------------------------------ the save round-trip

        [Test]
        public void SaveRoundTrip_PreservesBothAStringAndAnInt()
        {
            var table = new Dictionary<string, TriggerValue>
            {
                { "patient_tr2", TriggerValue.FromString("wait") },
                { "fin", TriggerValue.FromInt(2) },
                { "story_canter", TriggerValue.FromString("story_canter") },
            };

            TriggerTableCodec.Flatten(table, out string[] names, out int[] values,
                                      out string[] strings, out int[] kinds);

            // The writer must produce a *complete* block — a null kinds array would read as a legacy
            // save and silently downgrade every string to an int.
            Assert.That(kinds, Is.Not.Null, "The current format always writes the kind array.");
            Assert.That(kinds.Length, Is.EqualTo(names.Length));

            List<KeyValuePair<string, TriggerValue>> rebuilt =
                TriggerTableCodec.Rebuild(names, values, strings, kinds);
            var restored = new Dictionary<string, TriggerValue>();
            foreach (KeyValuePair<string, TriggerValue> pair in rebuilt) restored[pair.Key] = pair.Value;

            Assert.That(restored["patient_tr2"].RawString, Is.EqualTo("wait"),
                "The string survived the round-trip — this is the whole of C0.");
            Assert.That(restored["fin"].RawInt, Is.EqualTo(2));
            Assert.That(restored["story_canter"].RawString, Is.EqualTo("story_canter"),
                "An arbitrary @set value survives too (Quest.as:332).");

            // Negative control: an int must NOT come back as a string, or the round-trip is lossy in the
            // other direction and every numeric gate would start reading a string.
            Assert.That(restored["fin"].Kind, Is.EqualTo(TriggerValue.TriggerKind.Int));
        }

        [Test]
        public void LegacySave_WithNoKindArray_ReadsEveryEntryAsAnInt()
        {
            // A save written before the value type was widened has names + int values and NO kind array.
            // It must read as a coherent int-only table, not as a table of zeroes or of unset entries.
            string[] names = { "fin", "mbase_visited" };
            int[] values = { 2, 1 };

            List<KeyValuePair<string, TriggerValue>> rebuilt =
                TriggerTableCodec.Rebuild(names, values, null, null);

            var restored = new Dictionary<string, TriggerValue>();
            foreach (KeyValuePair<string, TriggerValue> pair in rebuilt) restored[pair.Key] = pair.Value;

            Assert.That(restored["fin"].RawInt, Is.EqualTo(2), "Legacy values are preserved, not zeroed.");
            Assert.That(restored["mbase_visited"].IsSet, Is.True,
                "A legacy entry is present — reading it as Unset would make every gate open.");
            Assert.That(restored.Count, Is.EqualTo(2));
        }

        [Test]
        public void TruncatedSave_DoesNotThrow()
        {
            // A save file is user data. A short array must degrade to unset, not crash the load.
            string[] names = { "a", "b" };
            int[] values = { 1 };            // shorter than names
            int[] kinds = { TriggerTableCodec.KindInt };

            List<KeyValuePair<string, TriggerValue>> rebuilt =
                TriggerTableCodec.Rebuild(names, values, null, kinds);

            Assert.That(rebuilt.Count, Is.EqualTo(2));
            Assert.That(rebuilt[0].Value.RawInt, Is.EqualTo(1));
            Assert.That(rebuilt[1].Value.IsSet, Is.False, "The missing tail reads as unset, not as 0.");
        }
    }
}
