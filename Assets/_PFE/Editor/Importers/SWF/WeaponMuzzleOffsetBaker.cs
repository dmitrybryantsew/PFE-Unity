#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Fills in <see cref="WeaponVisualDefinition.muzzleLocalOffset"/> by measuring each weapon's own
    /// artwork, and re-bakes the existing assets on demand.
    ///
    /// <para><b>Why this exists.</b> The field shipped with no writer and no reader: every one of the
    /// 129 weapon visuals carried <c>(0, 0)</c>, so <c>RangedWeaponController.Shoot()</c> fired from
    /// the weapon's registration point — the grip — rather than the barrel. Facing right the grip sits
    /// behind the character and the error is invisible; facing left it is on screen, which is the
    /// reported "turned left and it fires from its back". The muzzle cannot be imported, because AS3's
    /// <c>vis.emit</c> marker is not in the extracted data and the source SWF is not in the repository
    /// (see <see cref="WeaponMuzzleOffsetMath"/>), so it is measured from the pixels here.</para>
    ///
    /// <para><b>Run order matters.</b> The importer calls <see cref="TryBake"/> as it creates each
    /// visual, so a fresh import arrives with offsets already filled in. The menu item is for the 129
    /// assets that predate this code — it is idempotent and safe to re-run after an art re-export.</para>
    ///
    /// <para><b>Which frame is measured.</b> The <c>idleFrame</c> only. The muzzle is a property of
    /// the weapon, not of the animation, and the presenter publishes it from the vis transform whatever
    /// frame is currently drawn — so one measurement per weapon is both sufficient and what keeps the
    /// value stable while the gun is reloading or recoiling.</para>
    /// </summary>
    public static class WeaponMuzzleOffsetBaker
    {
        [MenuItem("PFE/Art/Derive Weapon Muzzle Offsets")]
        public static void BakeAllFromMenu()
        {
            int baked = 0, skipped = 0, failed = 0;

            try
            {
                string[] guids = AssetDatabase.FindAssets("t:WeaponVisualDefinition");

                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (i % 25 == 0)
                        EditorUtility.DisplayProgressBar("Deriving weapon muzzle offsets",
                            $"{i}/{guids.Length}  {Path.GetFileNameWithoutExtension(path)}",
                            guids.Length > 0 ? (float)i / guids.Length : 1f);

                    var def = AssetDatabase.LoadAssetAtPath<WeaponVisualDefinition>(path);
                    if (def == null) { failed++; continue; }

                    if (TryBake(def, out Vector2 offset, out string detail))
                    {
                        Debug.Log($"[WeaponMuzzleOffsetBaker] {def.symbolName}: " +
                                  $"muzzleLocalOffset = ({offset.x:0.####}, {offset.y:0.####})  — {detail}");
                        baked++;
                    }
                    else
                    {
                        // Left at whatever it was: a weapon with no measurable art is not a reason to
                        // write a wrong number over a correct hand-authored one.
                        Debug.LogWarning($"[WeaponMuzzleOffsetBaker] {def.symbolName}: not measured ({detail}). " +
                                         "muzzleLocalOffset left unchanged.");
                        skipped++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                // No StartAssetEditing/StopAssetEditing batch: it makes assets invisible to
                // LoadAssetAtPath until it ends, which is precisely the call this loop depends on.
                // 129 assets save in well under a second without it.
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"[WeaponMuzzleOffsetBaker] Done. baked={baked} skipped={skipped} failed={failed} " +
                      $"of {baked + skipped + failed} weapon visual definitions.");
        }

        /// <summary>
        /// Measure one weapon's muzzle from its idle frame and write it onto the definition.
        /// </summary>
        /// <returns><c>true</c> when the field was written; <c>false</c> leaves the asset untouched.</returns>
        public static bool TryBake(WeaponVisualDefinition def, out Vector2 offset, out string detail)
        {
            offset = Vector2.zero;
            detail = string.Empty;

            if (def == null) { detail = "null definition"; return false; }

            Sprite idle = def.IdleSprite;
            if (idle == null) { detail = "no idle sprite"; return false; }

            Texture2D texture = idle.texture;
            if (texture == null) { detail = "idle sprite has no texture"; return false; }

            string texturePath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath))
            {
                detail = $"texture not on disk ('{texturePath}')";
                return false;
            }

            // Decode from the file rather than reading the imported texture: the weapon PNGs ship with
            // `isReadable: 0`, so GetPixels32 on the asset texture throws. LoadImage builds a fresh
            // readable texture from the same bytes and needs no reimport, which matters because
            // re-importing 129 textures to read them once would be a visible stall and would churn
            // every .meta in the project.
            Color32[] pixels;
            int width, height;
            var scratch = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            try
            {
                if (!scratch.LoadImage(File.ReadAllBytes(texturePath)))
                {
                    detail = $"LoadImage failed for '{texturePath}'";
                    return false;
                }

                width  = scratch.width;
                height = scratch.height;
                pixels = scratch.GetPixels32();   // bottom row first, matching the math's contract
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }

            Rect spriteRect = idle.rect;
            var rect = new RectInt(
                Mathf.RoundToInt(spriteRect.x), Mathf.RoundToInt(spriteRect.y),
                Mathf.RoundToInt(spriteRect.width), Mathf.RoundToInt(spriteRect.height));

            if (!WeaponMuzzleOffsetMath.TryFindBarrelTipPixels(pixels, width, height, rect, out Vector2 tip))
            {
                detail = "no opaque pixels inside the sprite rect";
                return false;
            }

            float ppu = def.pixelsPerUnit > 0 ? def.pixelsPerUnit : idle.pixelsPerUnit;
            offset = WeaponMuzzleOffsetMath.ToLocalOffset(tip, idle.pivot, spriteRect.position, ppu);

            def.muzzleLocalOffset = offset;
            EditorUtility.SetDirty(def);

            detail = $"idle='{idle.name}' tex={width}x{height} rect={rect} " +
                     $"tip=({tip.x:0.#},{tip.y:0.#})px pivot=({idle.pivot.x:0.#},{idle.pivot.y:0.#})px ppu={ppu}";
            return true;
        }
    }
}
#endif
