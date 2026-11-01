using NUnit.Framework;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins the one number that made the S3 batch gate unattributable: how many meshes one layer of the
    /// quarter renderer submits.
    ///
    /// <para><b>Why this is a fixture and not a comment.</b> <c>TilemapRenderer</c> in
    /// <c>Mode.Chunk</c> splits a layer into a grid of chunk meshes, so the room's submitted renderer
    /// count was <c>layers × chunks</c> — and only the first factor was ever logged. At Unity's default
    /// chunk size of 32, a 40×30 room (80×60 half-cells) is <b>6</b> meshes per layer, so the HUD's
    /// Batches figure could not be compared against anything the renderer reported. These fixtures pin
    /// the rule that collapses it to one mesh per layer, and they pin the old answer too, so the
    /// difference is a number rather than a claim.</para>
    /// </summary>
    public sealed class RoomTerrainSubmissionTests
    {
        [Test]
        public void ChunkSize_CoversTheRoom_SoOneLayerIsOneMesh()
        {
            // The half-cell grid is twice the room's tile size, so the chunk has to cover 2 × max(w, h),
            // rounded up to a power of two. 40×30 → 80 → 128.
            Assert.AreEqual(128, RoomTerrainRenderer.ChunkSizeFor(40, 30));
            Assert.AreEqual(64, RoomTerrainRenderer.ChunkSizeFor(19, 10), "19×10 → 38 → 64");
            Assert.AreEqual(2, RoomTerrainRenderer.ChunkSizeFor(1, 1), "the smallest room still needs a chunk");
        }

        [Test]
        public void MeshesFor_IsOne_ForEveryRoomSizeThisRendererBuilds()
        {
            // The property, over the range of room sizes the importer produces. If ChunkSizeFor ever
            // stops covering the grid, this fails for a specific size rather than degrading quietly.
            int[,] rooms = { { 19, 10 }, { 40, 30 }, { 48, 25 }, { 64, 64 }, { 12, 40 } };

            for (int i = 0; i < rooms.GetLength(0); i++)
            {
                int width = rooms[i, 0];
                int height = rooms[i, 1];
                int chunk = RoomTerrainRenderer.ChunkSizeFor(width, height);

                Assert.AreEqual(1, RoomTerrainRenderer.MeshesFor(width, height, chunk),
                    $"{width}×{height} at chunk {chunk} must be one mesh");
            }
        }

        [Test]
        public void MeshesFor_AtTheOldDefault_SplitTheRoomIntoSix()
        {
            // This is the number the renderer was silently paying: Unity's default chunk size, which is
            // what `Mode.Chunk` used before ChunkSize existed. It is asserted rather than described
            // because "six meshes per layer" is the whole reason Batches and LayerCount disagreed.
            Assert.AreEqual(6, RoomTerrainRenderer.MeshesFor(40, 30, RoomTerrainRenderer.DefaultChunkSize),
                "3 chunks across × 2 down");
        }

        [Test]
        public void MeshesFor_RejectsAChunkSizeItCannotUse()
        {
            Assert.AreEqual(0, RoomTerrainRenderer.MeshesFor(40, 30, 0),
                "a zero chunk size is a caller bug, and 0 says so instead of dividing by it");
        }
    }
}
