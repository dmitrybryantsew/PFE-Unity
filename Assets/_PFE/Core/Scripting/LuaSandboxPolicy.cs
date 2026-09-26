using MoonSharp.Interpreter;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Configuration policy governing security, sandboxing, and resource limits for a Lua execution context.
    /// In host-authoritative multiplayer, scripts must be constrained so malicious or broken scripts
    /// cannot crash or hang the server simulation loop.
    /// </summary>
    public sealed class LuaSandboxPolicy
    {
        /// <summary>
        /// Maximum Lua bytecode instructions allowed per execution (0 = unlimited).
        /// Prevents infinite loops like 'while true do end'.
        /// </summary>
        public int InstructionLimit { get; set; } = 50_000;

        /// <summary>
        /// MoonSharp core module preset.
        /// Strips IO, OS, Debug, and CLR dynamic reflection.
        /// </summary>
        public CoreModules AllowedModules { get; set; } = CoreModules.Preset_HardSandbox | CoreModules.Metatables | CoreModules.ErrorHandling | CoreModules.Json;

        /// <summary>
        /// Whether to redirect math.random in the Lua environment to PFE's deterministic IRngService.
        /// </summary>
        public bool BindDeterministicRng { get; set; } = true;

        /// <summary>Default policy for authoritative gameplay and trigger scripts.</summary>
        public static LuaSandboxPolicy Default => new LuaSandboxPolicy();

        /// <summary>Policy for debug/developer console with higher execution limit.</summary>
        public static LuaSandboxPolicy DevConsole => new LuaSandboxPolicy
        {
            InstructionLimit = 250_000
        };
    }
}
