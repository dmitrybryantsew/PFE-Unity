namespace PFE.Systems.Particles
{
    /// <summary>
    /// AS3 <c>Bullet.popadalo(mat)</c>'s <b>spark arm</b> — the burst a round or a swung blade throws
    /// off the surface it stops on (<c>fe/weapon/Bullet.as:324-390</c>, the <c>param1 == 1 || 2 || 5 ||
    /// 7</c> branch at <c>:341-365</c>).
    ///
    /// <para><b>What was missing, and why nobody noticed.</b> The port never had a <c>popadalo</c> at
    /// all. A crowbar into a wall produced a decal, a sound and a hole, and <i>no particles</i> — because
    /// the decal path (<c>TileDecalRules</c>, the oracle's <c>Grafon.dyrka</c>) is a different function
    /// that only runs when the tile is actually <i>destroyed</i>, and the spark burst is the oracle's
    /// separate, always-on contact effect. Reported as "no particle effect of hitting".</para>
    ///
    /// <para><b>Four of the seven materials spark; the rest do not.</b> AS3's arm is
    /// <c>1, 2, 5, 7</c> — metal, concrete, glass, pole. Materials <c>3</c> and <c>4</c> (wood, heavy
    /// concrete) take the neighbouring arm, which only scales the bullet's own sprite, and everything
    /// else hides it; neither emits a part. That asymmetry is the oracle's, and inventing a burst for
    /// wood would be a change nobody asked for.</para>
    ///
    /// <para><b>The gate is the weapon's decal, not the material.</b> <c>popadalo</c> opens with
    /// <c>else if(this.tipDecal &gt; 0 &amp;&amp; this.tipDecal &lt;= 6)</c> (<c>:339</c>) — the same
    /// 1..6 window <see cref="PFE.Systems.Audio.ImpactSoundResolver.PlaysMaterialSound"/> encodes for
    /// the material <i>sound</i>. A weapon whose decal is outside that window (a laser sword's 12, a
    /// plasma bolt) throws no sparks however hard it hits, which is why this is a separate predicate
    /// and not "always emit".</para>
    ///
    /// <para><b>Engine-free on purpose.</b> <c>ParticleEmit</c> and <c>ParticleSpec</c> are plain
    /// numbers, so the whole rule runs in a fixture. The call site —
    /// <c>MeleeHitVolume.SweepTilesForImpact</c> — is a <c>MonoBehaviour</c> and cannot, which is the
    /// usual split: the arithmetic here, the wiring there.</para>
    /// </summary>
    public static class BulletImpactRules
    {
        /// <summary>AS3 <c>Emitter.emit("iskr_bul", …)</c> — the row in <c>AllData.as</c>'s part table.</summary>
        public const string SparkId = "iskr_bul";

        /// <summary>AS3 <c>if(_loc2_ &gt; 20) _loc2_ = 20;</c> (<c>Bullet.as:352-355</c>).</summary>
        public const int SparkCountCap = 20;

        /// <summary>
        /// AS3 <c>Math.floor(Math.random() * 5 + this.damage / 5)</c> — the 5 is the roll's span
        /// (<c>:347</c>).
        /// </summary>
        public const float SparkRollSpan = 5f;

        /// <summary>The <c>this.damage / 5</c> divisor in the same expression.</summary>
        public const float SparkDamageDivisor = 5f;

        /// <summary>
        /// AS3 <c>if(World.w.alicorn) _loc2_ *= 0.2;</c> (<c>:348-351</c>) — the endgame's reduced
        /// particle budget, applied to the count and never to the velocity.
        /// </summary>
        public const float AlicornCountScale = 0.2f;

        /// <summary>
        /// The <c>10</c> in <c>dx: -dx / this.vel * 10</c> (<c>:356-359</c>) — the spark's launch speed
        /// in AS3 pixels per frame, along the <i>reverse</i> of the contact's motion.
        /// </summary>
        public const float SparkSpeedPx = 10f;

        /// <summary>
        /// Whether this contact throws sparks at all: the weapon's decal must be in <c>1..6</c>
        /// (<c>Bullet.as:339</c>) <b>and</b> the surface must be one of the four sparking materials
        /// (<c>:341</c>).
        /// </summary>
        /// <param name="tipDecal">AS3 <c>tipDecal</c> — the weapon's <c>vis@tipdec</c>.</param>
        /// <param name="material">AS3 <c>Tile.mat</c> — the raw index, <b>not</b> a
        /// <c>MaterialType</c>. See <c>TileMaterialRules</c>.</param>
        public static bool EmitsSparks(int tipDecal, int material)
        {
            if (tipDecal <= 0 || tipDecal > 6) return false;

            return material == 1 || material == 2 || material == 5 || material == 7;
        }

        /// <summary>
        /// How many sparks — AS3 <c>_loc2_</c>, <c>:347-355</c>:
        /// <c>floor(roll * 5 + damage / 5)</c>, times <c>0.2</c> in the alicorn's world, capped at 20.
        ///
        /// <para><b>A zero is a real answer and is passed through.</b> AS3 writes <c>kol: 0</c> into the
        /// emit params, and <c>Emitter.cast</c> tests <c>kol</c> for truthiness — so zero means "not
        /// supplied", i.e. the row's default of one particle. Reproducing the zero rather than clamping
        /// it to 1 keeps that chain intact; the clamp would be a second, invented rule.</para>
        /// </summary>
        /// <param name="damage">AS3 <c>this.damage</c> — the bullet's damage as it stands at the
        /// contact, which for a swing is the charged, combo'd, wear-scaled figure the plan carries.</param>
        /// <param name="roll">Uniform <c>0..1</c>, the caller's own draw (AS3 calls
        /// <c>Math.random()</c> inline).</param>
        /// <param name="alicorn">AS3 <c>World.w.alicorn</c>. Defaults to <c>false</c>, the ordinary
        /// world, so a caller that has no world in scope gets the un-scaled count rather than a
        /// silently reduced one.</param>
        public static int SparkCount(float damage, float roll, bool alicorn = false)
        {
            float count = (float)System.Math.Floor(roll * SparkRollSpan + damage / SparkDamageDivisor);

            if (alicorn) count *= AlicornCountScale;

            int whole = (int)count;
            if (whole < 0) whole = 0;
            if (whole > SparkCountCap) whole = SparkCountCap;
            return whole;
        }

        /// <summary>
        /// The one emit a contact produces, or <c>null</c> when this contact does not spark.
        ///
        /// <para><b>The velocity is thrown <i>back</i> along the contact's own motion</b> —
        /// <c>dx: -dx / vel * 10</c>. For a bullet that is the reverse of its flight; for a swing it is
        /// the reverse of the blade's sweep, which is what makes the sparks spray away from the arc
        /// rather than sitting on it.</para>
        ///
        /// <para><b>The deltas are AS3 room pixels, Y DOWN</b> — the same space
        /// <see cref="ParticleSpec.DX"/> is integrated in. A caller holding a Unity-space delta must
        /// mirror Y once before calling; doing it here would put the mirror in the wrong layer and the
        /// rule would then be wrong for any AS3-space caller.</para>
        /// </summary>
        /// <param name="as3DeltaX">The contact's motion this step, AS3 pixels, +X right.</param>
        /// <param name="as3DeltaY">The same, AS3 pixels, <b>+Y down</b>.</param>
        /// <returns>
        /// The emit, with offsets zero — the caller places it at the contact point — or <c>null</c> when
        /// this contact does not spark. Nullable rather than a "did it emit" flag beside an out
        /// parameter: <see cref="ParticleEmit"/> is a struct, and one nullable return says both things
        /// once.
        /// </returns>
        public static ParticleEmit? PlanSparks(int tipDecal, int material, float damage, float roll,
                                               bool alicorn, float as3DeltaX, float as3DeltaY)
        {
            if (!EmitsSparks(tipDecal, material)) return null;

            var spec = new ParticleSpec
            {
                Kol = SparkCount(damage, roll, alicorn),
            };

            // `-dx / this.vel * 10` with vel = |delta|. A zero-length step (a swing that binds the
            // same tip twice, which is exactly what the strike window's opening frame does) makes the
            // oracle divide by zero; the port leaves the overrides at 0 instead, which is the same
            // "not supplied" the zero-means-absent rule already gives, so the row's own upward burst
            // stands in.
            float speed = (float)System.Math.Sqrt(as3DeltaX * as3DeltaX + as3DeltaY * as3DeltaY);
            if (speed > 0f)
            {
                spec.DX = -as3DeltaX / speed * SparkSpeedPx;
                spec.DY = -as3DeltaY / speed * SparkSpeedPx;
            }

            return new ParticleEmit(SparkId, spec);
        }
    }
}
