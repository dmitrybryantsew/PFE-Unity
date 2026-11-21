using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins the material of the NPC overhead icon — the one sprite in the unit graph that was left on
    /// the render pipeline's default.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> The icon reported from play as "vendor
    /// talk bubbles and icons are dark grey while in the original they were bright" was drawn with the
    /// correct silhouette, the correct position and the correct alpha — and no colour at all. It was a
    /// bare <c>AddComponent&lt;SpriteRenderer&gt;()</c>: this project renders 2D through URP's
    /// <c>Renderer2D</c>, whose default sprite material is <c>Sprite-Lit-Default</c>
    /// (<c>Assets/Settings/Renderer2D.asset</c>, <c>m_DefaultMaterialType: 0</c>), so a sprite on a
    /// sorting layer that no <c>Light2D</c> covers is multiplied by zero. Units live on
    /// <c>BackgroundPhysicalObjects</c>, which is not in <c>Global Light 2D</c>'s apply list, so every
    /// <c>visNPCIco</c> frame — the mint-green bracket-and-symbol line art, and the <c>dial1</c>/
    /// <c>dial2</c> speech bubbles — rendered solid black.</para>
    ///
    /// <para><b>Why this is a lint and not a behavioural test.</b> <c>EnsureBubble</c> needs a live
    /// <c>GameObject</c>, a persona and a loaded composition asset; outside the editor the harness
    /// throws <c>SecurityException</c> before the first assertion. Reading the source is the only guard
    /// that actually executes — the same reason <c>NpcPersonaArtWiringLintTests</c> is a lint.
    /// The failure mode is also invisible to every other guard: the sprite, the frame index and the
    /// sorting order are all correct, and only the material is wrong.</para>
    ///
    /// <para><b>Both the shader and the assignment are asserted.</b> <c>SpritePresenterMaterial.Get()</c>
    /// alone would pass on a tree that looks the material up and drops it on the floor; <c>sharedMaterial</c>
    /// alone would pass on any material, including the lit default. And <c>.material</c> is explicitly
    /// rejected: it instantiates a copy per NPC and takes the icon out of the body's batch.</para>
    /// </summary>
    [TestFixture]
    public sealed class NpcBubbleMaterialWiringLintTests
    {
        private const string Controller = "Entities/Units/UnitNPCController.cs";

        [Test]
        public void EnsureBubble_AssignsTheUnlitPresenterMaterial()
        {
            string source = SourceLint.ReadStripped(Controller);

            // Scoped to the one method that builds the icon, so the same call anywhere else in the file
            // — or the mention in the explanatory comment above, which the stripper removes — cannot
            // satisfy this. `MethodBody` fails loudly on a rename rather than checking a dead name.
            string body = SourceLint.MethodBody(source, "void EnsureBubble()");

            // Positive control on the body itself: if the renderer stops being created here, the two
            // assertions below would be about a method that no longer owns the sprite.
            Assert.That(body, Does.Contain("AddComponent<SpriteRenderer>()"),
                "EnsureBubble no longer creates the icon's SpriteRenderer, so this lint's premise is " +
                "gone — re-derive the guard against whatever builds the bubble now.");

            Assert.That(body, Does.Contain("SpritePresenterMaterial.Get()"),
                "the overhead icon must use the shared unlit material, or it renders solid black on " +
                "BackgroundPhysicalObjects — the reported 'dark grey talk bubbles and icons'.");

            Assert.That(body, Does.Contain("sharedMaterial"),
                "assigning `.material` would instantiate a copy per NPC and break batching with the body.");

            Assert.That(body, Does.Not.Contain(".material ="),
                "`_npcBubble.material =` is the instantiating form; the shared material is the fix.");
        }
    }
}
