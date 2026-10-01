namespace PFE.Tests.Editor.Core
{
    using System;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Security;
    using UnityEngine;

    /// <summary>
    /// Allocates a <see cref="ScriptableObject"/>-derived value for a fixture, including when the test
    /// assembly is executed <b>outside the editor</b>.
    ///
    /// <para><b>Why this exists.</b> <c>UnityEngine.ScriptableObject</c>'s constructor is an ECall, so it
    /// throws <c>SecurityException: ECall methods must be packaged into a system module</c> wherever the
    /// native engine is not loaded. A fixture that allocates a definition or a settings holder purely to
    /// read fields off it therefore cannot run in the offline harness
    /// (<c>.workbuddy-ai/tools/agentverify/</c>) — and "cannot run offline" is how a guard becomes
    /// decoration. Measured: this one constructor was the sole blocker for <c>DamageSystemTests</c>
    /// (0/39 → 39/39 once stubbed).</para>
    ///
    /// <para><b>The stub is not equivalent to the real thing, and callers must know which way.</b>
    /// <c>CreateInstance</c> applies Unity's serialized defaults; a constructor-less instance has every
    /// field at its CLR default — null, 0, false. So a caller must write every field it reads. That is
    /// fine for the pattern this exists for (arrange a value, set the fields under test) and is
    /// <i>wrong</i> for a fixture that depends on an authored default. Those fixtures need the asset, and
    /// will still die on <c>Resources.Load</c>.</para>
    /// </summary>
    public static class OfflineScriptableObject
    {
        /// <summary>
        /// A usable instance of <typeparamref name="T"/>. Uses the real Unity allocator in the editor and
        /// a constructor-less stub when the engine is absent. Every field is at its CLR default in the
        /// stub case — set the ones under test.
        /// </summary>
        public static T Create<T>() where T : ScriptableObject
        {
            try
            {
                return ScriptableObject.CreateInstance<T>();
            }
            catch (SecurityException)
            {
                return Stub<T>();
            }
        }

        private static T Stub<T>() where T : ScriptableObject
        {
#pragma warning disable SYSLIB0050 // the obsolete formatter API is the only one Unity also has
            var stub = (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050

            // Faking the native pointer is NOT optional, and getting it wrong is the dangerous case.
            // Unity's Object.operator== asks whether the native pointer is alive, so a constructor-less
            // instance compares == null and every `value != null` guard in the code under test reads
            // false. The system then silently takes its fallback branch: measured, that produced 21
            // failures in DamageSystemTests, every one a plausible wrong NUMBER rather than an error.
            // Nothing dereferences the pointer — only the identity test reads it.
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

            if (cachedPtr == null)
            {
                throw new InvalidOperationException(
                    "UnityEngine.Object.m_CachedPtr was renamed. Without it the offline stub compares == "
                    + "null, every `!= null` gate in the code under test reads false, and the fixture "
                    + "would silently assert against the wrong code path. Refusing to return it.");
            }

            cachedPtr.SetValue(stub, new IntPtr(1));

            // ReferenceEquals, not Assert.That(..., Is.Not.Null): NUnit's null constraint reflects into
            // UnityEngine to ask whether the value is a GameObject, and that reflection is itself an
            // ECall — so it throws in exactly the environment this stub exists to serve.
            if (ReferenceEquals(stub, null))
            {
                throw new InvalidOperationException(
                    "the offline ScriptableObject stub still compares == null; refusing to return it.");
            }

            return stub;
        }
    }
}
