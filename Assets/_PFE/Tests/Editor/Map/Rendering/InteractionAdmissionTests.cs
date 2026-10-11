using NUnit.Framework;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Tests for <see cref="DoorPropPresenter.AdmitsPlayerInteraction"/> — the guard that decides whether
    /// the player can <b>target</b> an object at all.
    ///
    /// <para><b>Why this needs its own fixture.</b> Two different rules have to pass before pressing E on a
    /// room object does anything. <see cref="RoomObjectVisualManager.UsesDoorPresenter"/> decides whether
    /// the object gets a <c>DoorPropPresenter</c> in the first place; this decides whether that presenter
    /// will accept the player's press. They are separate questions with separate failure modes, and a
    /// checkpoint passed the first while failing the second: it <i>was</i> given the presenter and its
    /// <c>Interact()</c> handled the checkpoint correctly, but <c>CanInteract()</c> refused the target
    /// first, so <c>Interact()</c> was never reached and E did nothing.</para>
    ///
    /// <para>The rule is a pure function of two bools precisely so a test can make it go red — the same
    /// reason <see cref="RoomObjectVisualManager.UsesDoorPresenter"/> is one.</para>
    /// </summary>
    [TestFixture]
    public class InteractionAdmissionTests
    {
        [Test]
        public void ADoorIsWorkable()
        {
            Assert.IsTrue(DoorPropPresenter.AdmitsPlayerInteraction(
                isInteractableDoor: true, isCheckpointObject: false));
        }

        [Test]
        public void ACheckpointIsWorkable()
        {
            // AS3's CheckPoint constructor hands it an Interact with actFun = activate (CheckPoint.as:87)
            // and active = true; action = 100 (:89-90). It authors no `inter` attribute and its
            // interactionMode is empty, so the door rule answers false for it — which is exactly why E on
            // a checkpoint did nothing.
            Assert.IsTrue(DoorPropPresenter.AdmitsPlayerInteraction(
                isInteractableDoor: false, isCheckpointObject: true));
        }

        [Test]
        public void AnObjectThatIsNeitherIsNotWorkable()
        {
            // Control. A plain prop keeps ObjectColliderDebugPresenter and must stay untargetable; without
            // this case a rule that returned true unconditionally would satisfy both cases above.
            Assert.IsFalse(DoorPropPresenter.AdmitsPlayerInteraction(
                isInteractableDoor: false, isCheckpointObject: false));
        }

        [Test]
        public void BothTermsTogetherAreWorkable()
        {
            Assert.IsTrue(DoorPropPresenter.AdmitsPlayerInteraction(
                isInteractableDoor: true, isCheckpointObject: true));
        }
    }
}
