using System;
using NUnit.Framework;
using PFE.Character;
using UnityEngine;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// Pins <see cref="CharacterQuadAtlas.Cache"/>'s host policy — the behaviour a fixture can
    /// reach without standing up a GPU build.
    ///
    /// <para><b>Why the policy and not the packing.</b> The cache's whole risk is lifetime, not
    /// arithmetic: a cache that holds in the editor pins megabytes of <c>HideAndDontSave</c>
    /// textures against the preview windows, and a cache the assembler disposes tears textures
    /// out from under other characters. Both rules are decided before any sprite is packed, so
    /// both are testable with an empty sprite list.</para>
    /// </summary>
    public sealed class CharacterAtlasCacheTests
    {
        [Test]
        public void Get_DoesNotCacheOutsidePlayMode()
        {
            // EditMode tests run with Application.isPlaying == false, which is exactly the host the
            // preview windows live in: the cache must decline so the caller builds (and owns) a
            // fresh atlas and the A/B toggles keep their rebuild-on-demand semantics.
            CharacterQuadAtlas.Cache.Clear();
            try
            {
                CharacterQuadAtlas atlas =
                    CharacterQuadAtlas.Cache.Get(Array.Empty<Sprite>(), bilinear: true, armorId: "test", mips: false);

                Assert.IsNull(atlas, "outside play mode the cache must decline so the caller builds fresh");
                Assert.AreEqual(0, CharacterQuadAtlas.Cache.Count,
                    "a declined build must leave no entry behind");
            }
            finally
            {
                CharacterQuadAtlas.Cache.Clear();
            }
        }

        [Test]
        public void Clear_IsSafeOnAnEmptyCache()
        {
            CharacterQuadAtlas.Cache.Clear();
            Assert.DoesNotThrow(() => CharacterQuadAtlas.Cache.Clear());
            Assert.AreEqual(0, CharacterQuadAtlas.Cache.Count);
        }
    }
}
