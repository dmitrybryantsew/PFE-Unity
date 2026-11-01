using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// An armour frame that places nothing is not an override, and drawing it as one loses the part's
    /// colour.
    ///
    /// <para><b>The fault this guards.</b> Switching the grey pony into the jumpsuit turned her hind
    /// legs white. Nothing was wrong with the armour art: <c>Obj.setArmor</c> is
    /// <c>m.gotoAndStop(aid)</c> with <c>catch → gotoAndStop(1)</c> (<c>fe/Obj.as:67-96</c>), and a
    /// Flash frame with no <c>PlaceObject</c> of its own <i>inherits</i> the previous frame's display
    /// list. So <c>sleg1</c>(55) frame 2 — the <c>pip</c> frame — places nothing, the clip keeps its
    /// instance, and the multiply colour transform <c>Obj.setColor(this, 0)</c> put on it in
    /// <c>frame1()</c> (<c>pfe_fla/sleg10_6.as:18</c>) is still on it. The leg draws in the pony's
    /// coat colour, as if she were not wearing trousers on it.</para>
    ///
    /// <para><b>Why the port got it wrong.</b> The exporter writes every frame of a symbol as its own
    /// PNG, so the <c>pip</c> frame of <c>sleg1</c> exists on disk and is byte-identical to frame 1.
    /// "The armour has a frame here" was true; "the armour changes anything" was not, and only the
    /// second justifies an override. Overrides are drawn at <c>Color.white</c> — armour art is
    /// pre-coloured, so tinting it again would double the fur colour onto it — and that is right for
    /// real armour, which re-runs <c>setColor</c> on its own frame anyway. It is wrong for an
    /// inherited frame, where the tint is the only colour the part has.</para>
    ///
    /// <para><b>What is pinned.</b> That the four inherited frames in the shipped asset carry no
    /// override, that the aura move which <i>looks</i> like a fifth one survives, and — the control —
    /// that frames which <i>do</i> swap art still carry theirs, so the rule cannot be satisfied by
    /// emitting nothing. The exact override count is not pinned: armour art legitimately gains parts,
    /// and a test that counts them would fail for a good reason.</para>
    /// </summary>
    [TestFixture]
    public class ArmorFrameInheritanceTests
    {
        const string DefinitionPath = "Characters/PlayerAnimationDefinition";

        /// <summary>
        /// Every (armour, part) pair whose armour frame inherits frame 1's display list. Read from the
        /// SWF with a standalone tag scan (`DefineSprite` → `FrameLabel` / `PlaceObject2` /
        /// `ShowFrame`): the frame is labelled, exists, and its display-list snapshot equals frame 1's.
        /// Four in the whole asset.
        ///
        /// <para><b>Not <c>ali</c>/<c>magic</c>.</b> That override points at the same sprite as the base
        /// art too, so it looks identical by the same test — but it is emitted by the head-variant
        /// "this armour frame moves the slot" path to carry a <c>(0.123, −0.157)</c> position delta, and
        /// removing it would drop the aura back to the unarmoured spot. See
        /// <see cref="TheAuraMoveIsAnOffsetNotAnInheritedFrame"/>.</para>
        /// </summary>
        static readonly (string armorId, string partName, int symbolId)[] InheritedFrames =
        {
            ("pip", "sleg1", 55),
            ("pip", "sleg3", 319),
            ("pip", "sleg3a", 24),
            ("chitin", "fleg2", 101),
        };

        CharacterAnimationDefinition _definition;

        [SetUp]
        public void Load()
        {
            _definition = Resources.Load<CharacterAnimationDefinition>(DefinitionPath);
            Assert.IsNotNull(_definition, $"no definition at Resources/{DefinitionPath}");
        }

        [Test]
        public void AnArmourFrameThatPlacesNothingDoesNotOverrideThePart()
        {
            foreach (var (armorId, partName, symbolId) in InheritedFrames)
            {
                ArmorVisualSet set = _definition.GetArmorSet(armorId);
                Assert.IsNotNull(set, $"armour set '{armorId}' is missing from the definition");

                int index = PartIndex(partName);
                Assert.IsFalse(Contains(set, index),
                    $"'{armorId}' overrides '{partName}' (part {index}, symbol {symbolId}), but that " +
                    "armour's frame of the symbol places nothing — it inherits frame 1's display list, " +
                    "so the original keeps the fur tint and the part must not be overridden. Drawing " +
                    "the exported frame (identical to frame 1) as an override paints it at " +
                    "Color.white and the part loses its coat colour. Re-import: PFE/Art/Import " +
                    "Character From SWF → Run Full Import (CharacterAnimationDataGenerator." +
                    "FramePlacesTheSameAsFirst).");
            }
        }

        [Test]
        public void ArmourFramesThatDoSwapArtKeepTheirOverride()
        {
            // The control. Without it, the rule above is satisfied by a generator that emits no
            // overrides at all — the character would simply never wear armour.
            foreach (var (armorId, partName) in new[]
                     {
                         ("pip", "korpus"),
                         ("pip", "fleg1"),
                         ("pip", "neck"),
                         ("chitin", "fleg1"),
                     })
            {
                ArmorVisualSet set = _definition.GetArmorSet(armorId);
                Assert.IsNotNull(set, $"armour set '{armorId}' is missing from the definition");

                int index = PartIndex(partName);
                Assert.IsTrue(Contains(set, index),
                    $"'{armorId}' does not override '{partName}' (part {index}). That armour's frame " +
                    "of the part swaps the art, so the override is the only way the armour is drawn.");
            }
        }

        [Test]
        public void TheAuraMoveIsAnOffsetNotAnInheritedFrame()
        {
            // The near-miss. `ali` overrides `magic` with the same symbol the part already uses, so a
            // rule that skips "the override draws what the part draws" would delete it — and the
            // alicorn's aura would snap back to the unarmoured position. The override is not there for
            // the art; it is there for the offset the armour frame applies to that slot.
            ArmorVisualSet set = _definition.GetArmorSet("ali");
            Assert.IsNotNull(set, "the ali armour set is missing from the definition");

            int index = PartIndex("magic");
            ArmorPartOverride? entry = null;
            foreach (ArmorPartOverride candidate in set.partOverrides)
            {
                if (candidate.partIndex == index) entry = candidate;
            }

            Assert.IsTrue(entry.HasValue,
                "'ali' no longer overrides 'magic'. Its aura symbol is the default 239, so the only " +
                "reason the override exists is the position delta the armour frame applies to that " +
                "slot — dropping it puts the aura back where the unarmoured head has it.");
            Assert.Greater(entry.Value.localOffset.sqrMagnitude, 1e-4f,
                "the 'ali' aura override carries no offset, so it is now a true no-op and should be " +
                "removed deliberately rather than left behind");
        }

        [Test]
        public void ThePipSuitStillCoversMostOfTheBody()
        {
            // A floor, not a count: the jumpsuit is the starting outfit and covers torso, all four
            // legs, neck and head. Losing the three inherited legs must not be the start of a slide
            // toward an empty set, which the two tests above would still pass one part at a time.
            ArmorVisualSet set = _definition.GetArmorSet("pip");
            Assert.IsNotNull(set, "the pip armour set is missing from the definition");
            Assert.GreaterOrEqual(set.partOverrides.Length, 5,
                $"the pip armour set has only {set.partOverrides.Length} overrides; it covers torso, " +
                "four legs, neck and head, so fewer than 5 means the frame lookup or the sprite " +
                "export regressed rather than the inherited-frame rule doing its job");
        }

        int PartIndex(string partName)
        {
            for (int i = 0; i < _definition.parts.Length; i++)
            {
                if (_definition.parts[i].partName == partName) return i;
            }

            Assert.Fail($"the definition has no part named '{partName}'");
            return -1;
        }

        static bool Contains(ArmorVisualSet set, int partIndex)
        {
            foreach (ArmorPartOverride entry in set.partOverrides)
            {
                if (entry.partIndex == partIndex) return true;
            }

            return false;
        }
    }
}
