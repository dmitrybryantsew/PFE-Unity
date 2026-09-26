using System;
using MoonSharp.Interpreter;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Core scripting engine service interface.
    /// Provides sandboxed Lua execution, safe CLR interop, and deterministic RNG binding.
    /// Registered in VContainer as a singleton.
    /// </summary>
    public interface ILuaEngine : IDisposable
    {
        /// <summary>
        /// Creates a new sandboxed Script instance configured with the specified policy.
        /// </summary>
        Script CreateScript(LuaSandboxPolicy policy = null);

        /// <summary>
        /// Executes a Lua code chunk on a shared or isolated script context.
        /// </summary>
        DynValue ExecuteString(string code, Table context = null, LuaSandboxPolicy policy = null);

        /// <summary>
        /// Calls a global function on the default script context.
        /// </summary>
        DynValue CallFunction(string functionName, params object[] args);

        /// <summary>
        /// Sets a global variable or object on the default script context.
        /// </summary>
        void SetGlobal(string name, object value);

        /// <summary>
        /// Registers a C# type for safe userdata interop within Lua scripts.
        /// </summary>
        void RegisterType<T>();
    }
}
