using PFE.Data.Definitions;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The training dummy — the port of <c>fe/unit/UnitTrain.as</c>.
    ///
    /// <para><b>Why this is the first enemy to wire.</b> It is the only unit in the game with no AI, no
    /// inventory and no RPG dependency: AS3 gets that by overriding four methods so the dummy simply
    /// cannot die and never acts. So it exercises the unit + damage + armour pipeline and nothing else,
    /// which is what makes it the one enemy the unfinished RPG side does not block.</para>
    ///
    /// <para><b>What AS3 actually overrides</b> (<c>UnitTrain.as</c>, re-read rather than taken from
    /// notes): <c>control()</c> sets <c>hp = maxhp</c> every tick (<c>:54-57</c>); <c>die()</c> sets
    /// <c>hp = maxhp</c>, so it cannot die (<c>:65-68</c>); <c>setNull()</c> restores
    /// <c>armor_hp = armor_maxhp</c> (<c>:59-63</c>); <c>visDetails()</c> hides the hp bar
    /// (<c>:70-76</c>); <c>setLevel()</c> is a deliberate no-op so a dummy never scales (<c>:78-80</c>).
    /// The constructor also sets <c>doop = true</c> (<c>:51</c>).</para>
    ///
    /// <para><b>What is deliberately NOT ported, because nothing in the port would read it:</b>
    /// <c>setNull()</c> restores an <i>armour pool</i>, and this dummy has none — <c>AllData.as:8-14</c>
    /// declares <c>&lt;comb hp='500' armor='0' marmor='0'/&gt;</c>, so the armoured variant's protection
    /// is entirely <c>skin = 20</c> and not a pool. <c>visDetails()</c> hides an hp bar the port does not
    /// draw for units. <c>doop</c> (a "passive target" flag: it excludes the unit from the battle timer,
    /// aggro and crit-invisibility at <c>Unit.as:3659/3760/3783/4377</c>) has no consumer because none of
    /// those systems exist yet. <c>setLevel()</c>'s no-op is already the port's behaviour, since level
    /// scaling is unimplemented. Each is a real gap with a named owner, not an oversight.</para>
    /// </summary>
    public class TrainingDummyController : UnitController
    {
        /// <summary>AS3's controller class name — <c>AllData.as:5048</c> <c>&lt;obj … cl='UnitTrain'/&gt;</c>.</summary>
        public const string ControllerId = "UnitTrain";

        /// <summary>The armoured variant's flat natural resistance, from <c>UnitTrain.as:44</c>.</summary>
        public const float ArmoredSkin = 20f;

        public const string PlainVisualClassName = "visualTrain";
        public const string ArmoredVisualClassName = "visualTrainArmor";

        bool _armored;
        bool _fixedByPlacement;

        /// <summary>
        /// AS3 <c>UnitTrain.tr</c>, set from the placement's <c>tr</c> attribute (<c>:31-34</c>).
        /// The Camp's test ground places five dummies, and the two at <c>(41,10)</c> and <c>(41,5)</c>
        /// carry <c>tr="1"</c> — the armoured pair.
        /// </summary>
        public bool IsArmored => _armored;

        /// <summary>
        /// AS3 <c>UnitTrain</c>'s <c>fixed</c>, raised from the placement's <c>fix</c> attribute
        /// (<c>UnitTrain.as:34-37</c>) — layered on top of the definition's own
        /// <see cref="UnitDefinition.isFixed"/>, which <c>&lt;unit id='training'&gt;</c> does not author.
        /// </summary>
        /// <remarks>
        /// <b>Why the dummy needs its own flag rather than the definition field.</b> The dummy's
        /// immobility is a property of <i>where it is placed</i>, not of the unit type: of the Camp's
        /// five dummies, three are pinned (<c>fix="1"</c> — <c>RoomsCamp.as:413</c>, <c>:419</c>,
        /// <c>:420</c>) and two are free, all five sharing one <c>&lt;unit id='training'&gt;</c> row.
        /// A definition-level flag could only pin all five or none.
        ///
        /// <para><b>The presence test is the subclass's, not the base class's.</b> <c>:34</c> is
        /// <c>if(param3.@fix.length())</c> — a non-empty test, so <c>fix="0"</c> <i>pins</i>. The base
        /// class's <c>Unit.as:1116</c> is <c>node.@fixed &gt; 0</c>, a value test. Two attributes, two
        /// different comparisons, and mirroring the base here would un-pin a <c>fix="0"</c> dummy.</para>
        /// </remarks>
        public override bool IsFixed => _fixedByPlacement || base.IsFixed;

        /// <summary>
        /// The AS3 visual class this dummy uses (<c>UnitTrain.as:41-49</c>), exposed so the unit-visual
        /// pipeline has a stable hook.
        ///
        /// <para>This cannot be expressed as <c>UnitDefinition.sprite</c>: one definition carries one
        /// sprite, and the two variants are two different classes. Both sprite sets do exist in the
        /// oracle — <c>pfe/sprites/DefineSprite_3110_visualTrain</c> and
        /// <c>pfe/sprites/DefineSprite_3107_visualTrainArmor</c> — they simply have no import pipeline
        /// into <c>Resources/Units</c> yet.</para>
        /// </summary>
        public string VisualClassName => _armored ? ArmoredVisualClassName : PlainVisualClassName;

        /// <summary>
        /// Read the placement the way <c>UnitTrain.as</c>'s constructor does (<c>:14-51</c>).
        ///
        /// <para>Only the dummy-specific parts live here. <c>turn</c> → facing is handled by
        /// <see cref="UnitController.ApplyPlacement"/>, because it is base <c>Unit</c> behaviour that
        /// <c>UnitTrain</c> merely repeats.</para>
        ///
        /// <para><b>This comment used to say <c>fix</c> was deliberately unconsumed</b>, on the grounds
        /// that "the port has neither a motor nor knockback for a spawned unit yet, so porting it would
        /// invent a consumer". Both halves of that had become false — <c>UnitController.Move()</c> has
        /// been the unit motor for a long time, and knockback landed on 2026-10-01 — so the reason
        /// expired while the sentence stayed. That is the failure mode this project keeps hitting: a
        /// belief that went stale with nothing going red. It is consumed now.</para>
        /// </summary>
        public override void ApplyPlacement(UnitInstance placement)
        {
            base.ApplyPlacement(placement);

            if (placement == null)
            {
                return;
            }

            _armored = placement.GetAttribute("tr") == "1";

            // UnitTrain.as:34-37 — `if(param3.@fix.length()) { fixed = true; }`. Tested for presence,
            // not value, which is why this is a non-empty check rather than a comparison against "1";
            // GetAttribute returns "" for an absent key, so that is the same test AS3's `.length()`
            // makes. The attribute is authored on the placement row, never on the <unit> template.
            _fixedByPlacement = !string.IsNullOrEmpty(placement.GetAttribute("fix"));

            if (_armored && _unitStats != null)
            {
                // UnitTrain.as:41-45 — the armoured branch sets skin = 20 alongside its own visual.
                // This is the A7b shape: DamageCalculator.Resolve already reads
                // UnitStats.skinResistance and ArmourResolutionTests already exercises it, so the
                // channel is ported and tested — only the producer was missing.
                _unitStats.skinResistance = ArmoredSkin;
            }
        }

        /// <summary>
        /// AS3 <c>control()</c> (<c>UnitTrain.as:54-57</c>) — <c>hp = maxhp</c>, every tick.
        ///
        /// <para><b>Overrides <see cref="UnitController.StepUnit"/>, not a driver.</b> AS3's
        /// <c>control()</c> runs once per unit frame alongside <c>run()</c>, so it belongs to the
        /// unit's <i>step</i> — whichever clock owns that step. This used to override
        /// <c>FixedUpdate</c>, which was correct only while every motor-less unit was on Unity's
        /// fixed clock; once <c>SimLoop</c> owns the step, a <c>FixedUpdate</c> override would restore
        /// the dummy's health at 50 Hz while its movement ran at 30, splitting one oracle frame across
        /// two clocks.</para>
        /// </summary>
        protected override void StepUnit()
        {
            base.StepUnit();
            RestoreHealth();
        }

        /// <summary>
        /// AS3 <c>die()</c> (<c>UnitTrain.as:65-68</c>) is overridden to <c>hp = maxhp</c>, so the dummy
        /// is unkillable by construction. The port reaches death through
        /// <see cref="UnitController.OnDeath"/> instead of a <c>die()</c> method, so the override goes
        /// there — otherwise a dummy that hits 0 hp would be destroyed and the test ground would empty
        /// itself.
        /// </summary>
        protected override void OnDeath()
        {
            RestoreHealth();
        }

        /// <summary>
        /// Restore health to full, without a redundant reactive write: <c>ReactiveProperty</c> would
        /// publish on every step otherwise, which is a notification storm for a value that
        /// changes only when the dummy is hit. Behaviourally identical to AS3's unconditional assignment.
        /// </summary>
        void RestoreHealth()
        {
            if (_unitStats == null)
            {
                return;
            }

            if (_unitStats.CurrentHp.Value < _unitStats.MaxHp.Value)
            {
                _unitStats.CurrentHp.Value = _unitStats.MaxHp.Value;
            }
        }
    }
}
