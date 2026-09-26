using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;

namespace PFE.Tests.Editor.Core
{
    [TestFixture]
    public class RngTests
    {
        [Test]
        public void SameSeed_ProducesIdenticalSequence()
        {
            var rng1 = new PcgRngService(12345UL);
            var rng2 = new PcgRngService(12345UL);

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(rng1.NextUInt(), rng2.NextUInt(), $"Diverged at index {i}");
            }
        }

        [Test]
        public void DifferentSeed_ProducesDivergentSequence()
        {
            var rng1 = new PcgRngService(12345UL);
            var rng2 = new PcgRngService(54321UL);

            int matches = 0;
            for (int i = 0; i < 100; i++)
            {
                if (rng1.NextUInt() == rng2.NextUInt())
                    matches++;
            }

            Assert.Less(matches, 5, "Different seeds should produce divergent sequences");
        }

        [Test]
        public void StreamIsolation_AdvancingOneStreamDoesNotAffectOther()
        {
            var root1 = new PcgRngService(99999UL);
            var root2 = new PcgRngService(99999UL);

            var combat1 = root1.GetStream(RngStream.Combat);
            var loot1 = root1.GetStream(RngStream.Loot);

            var combat2 = root2.GetStream(RngStream.Combat);
            var loot2 = root2.GetStream(RngStream.Loot);

            // Advance combat1 by 50 calls
            for (int i = 0; i < 50; i++)
            {
                combat1.NextUInt();
            }

            // loot1 and loot2 should remain 100% bit-identical
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(loot1.NextUInt(), loot2.NextUInt(), $"Loot stream diverged at step {i}");
            }
        }

        [Test]
        public void Range_StaysWithinBounds()
        {
            var rng = new PcgRngService(42UL);

            for (int i = 0; i < 1000; i++)
            {
                int val = rng.Range(10, 20);
                Assert.GreaterOrEqual(val, 10);
                Assert.Less(val, 20);

                float fVal = rng.Range(2.5f, 7.5f);
                Assert.GreaterOrEqual(fVal, 2.5f);
                Assert.Less(fVal, 7.5f);
            }
        }

        [Test]
        public void Chance_HandlesBoundaries()
        {
            var rng = new PcgRngService(42UL);

            Assert.IsFalse(rng.Chance(0f));
            Assert.IsFalse(rng.Chance(-1f));
            Assert.IsTrue(rng.Chance(1f));
            Assert.IsTrue(rng.Chance(2f));
        }

        [Test]
        public void Shuffle_PermutesListDeterministically()
        {
            var rng1 = new PcgRngService(777UL);
            var rng2 = new PcgRngService(777UL);

            var list1 = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var list2 = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            rng1.Shuffle(list1);
            rng2.Shuffle(list2);

            CollectionAssert.AreEqual(list1, list2);
            CollectionAssert.AreNotEqual(new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, list1);
        }
    }
}
