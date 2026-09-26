using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Loaders;
using PFE.Core.Ids;
using PFE.Core.Rng;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// MoonSharp-backed implementation of ILuaEngine.
    /// Provides sandboxed Lua execution, deterministic RNG delegation, and safe EntityId interop.
    /// </summary>
    public sealed class MoonSharpScriptEngine : ILuaEngine
    {
        static MoonSharpScriptEngine()
        {
            Script.DefaultOptions.ScriptLoader = new FileSystemScriptLoader();
        }

        private readonly IRngService _rngService;
        private readonly IEntityRegistry _entityRegistry;
        private readonly Script _sharedScript;
        private bool _disposed;

        public MoonSharpScriptEngine(IRngService rngService = null, IEntityRegistry entityRegistry = null)
        {
            _rngService = rngService;
            _entityRegistry = entityRegistry;

            // Register core value types with MoonSharp UserData registry
            RegisterType<EntityId>();

            // Create default shared script instance with default sandbox policy
            _sharedScript = CreateScript(LuaSandboxPolicy.DevConsole);
        }

        public Script CreateScript(LuaSandboxPolicy policy = null)
        {
            policy ??= LuaSandboxPolicy.Default;

            var script = new Script(policy.AllowedModules);

            // Avoid UnityAssetsScriptLoader reflection lookups during tests and headless mode
            script.Options.ScriptLoader = new FileSystemScriptLoader();

            // Attach instruction quota limiter if specified
            if (policy.InstructionLimit > 0)
            {
                script.AttachDebugger(new InstructionQuotaDebugger(policy.InstructionLimit));
            }

            // Bind deterministic RNG to Lua math.random if enabled
            if (policy.BindDeterministicRng && _rngService != null)
            {
                BindDeterministicRngToScript(script, _rngService);
            }

            // Bind standard PFE library
            BindPfeStandardLibrary(script);

            return script;
        }

        public DynValue ExecuteString(string code, Table context = null, LuaSandboxPolicy policy = null)
        {
            if (string.IsNullOrWhiteSpace(code))
                return DynValue.Nil;

            Script scriptToUse = _sharedScript;
            if (policy != null)
            {
                scriptToUse = CreateScript(policy);
            }

            return scriptToUse.DoString(code, context);
        }

        public DynValue CallFunction(string functionName, params object[] args)
        {
            if (string.IsNullOrEmpty(functionName))
                return DynValue.Nil;

            DynValue func = _sharedScript.Globals.Get(functionName);
            if (func.Type != DataType.Function)
                throw new InvalidOperationException($"Global Lua function '{functionName}' was not found or is not callable.");

            return _sharedScript.Call(func, args);
        }

        public void SetGlobal(string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            _sharedScript.Globals[name] = value;
        }

        public void RegisterType<T>()
        {
            if (!UserData.IsTypeRegistered(typeof(T)))
            {
                UserData.RegisterType<T>();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        private static void BindDeterministicRngToScript(Script script, IRngService rng)
        {
            Table mathTable = script.Globals.Get("math").Table;
            if (mathTable != null)
            {
                mathTable["random"] = (Func<DynValue, DynValue, DynValue>)((arg1, arg2) =>
                {
                    // No arguments: random float in [0.0, 1.0)
                    if (arg1.IsNil())
                    {
                        return DynValue.NewNumber(rng.NextFloat());
                    }

                    // One argument: integer in [1, max]
                    if (arg2.IsNil())
                    {
                        int max = (int)arg1.Number;
                        if (max < 1) return DynValue.NewNumber(0);
                        return DynValue.NewNumber(rng.Range(1, max + 1));
                    }

                    // Two arguments: integer in [min, max]
                    int min = (int)arg1.Number;
                    int maxVal = (int)arg2.Number;
                    if (maxVal < min) return DynValue.NewNumber(min);
                    return DynValue.NewNumber(rng.Range(min, maxVal + 1));
                });
            }
        }

        private void BindPfeStandardLibrary(Script script)
        {
            var pfeTable = new Table(script);

            pfeTable["print"] = (Action<string>)(msg => Debug.Log($"[Lua] {msg}"));
            pfeTable["warn"] = (Action<string>)(msg => Debug.LogWarning($"[Lua] {msg}"));
            pfeTable["error"] = (Action<string>)(msg => Debug.LogError($"[Lua] {msg}"));

            // EntityId creation and lookup
            pfeTable["entity"] = (Func<string, EntityId>)(EntityId.FromString);

            // Map & Fog of war controls
            pfeTable["reveal_map"] = (Func<string>)(() =>
            {
                if (DeveloperConsoleService.Instance != null)
                {
                    return DeveloperConsoleService.Instance.RevealMap();
                }
                return "[Map] DeveloperConsoleService instance not found.";
            });

            pfeTable["toggle_fog"] = (Func<string>)(() =>
            {
                if (DeveloperConsoleService.Instance != null)
                {
                    return DeveloperConsoleService.Instance.ToggleFog();
                }
                return "[FogOfWar] DeveloperConsoleService instance not found.";
            });

            pfeTable["set_fog"] = (Func<bool, string>)(enabled =>
            {
                if (DeveloperConsoleService.Instance != null)
                {
                    return DeveloperConsoleService.Instance.SetFog(enabled);
                }
                return "[FogOfWar] DeveloperConsoleService instance not found.";
            });

            if (_rngService != null)
            {
                var rngTable = new Table(script);
                rngTable["next_float"] = (Func<float>)(() => _rngService.NextFloat());
                rngTable["next_int"] = (Func<int, int>)(max => _rngService.NextInt(max));
                rngTable["range_int"] = (Func<int, int, int>)((min, max) => _rngService.Range(min, max));
                rngTable["range_float"] = (Func<float, float, float>)((min, max) => _rngService.Range(min, max));
                rngTable["chance"] = (Func<float, bool>)(prob => _rngService.Chance(prob));
                pfeTable["rng"] = rngTable;
            }

            script.Globals["pfe"] = pfeTable;
        }

        /// <summary>
        /// Instruction quota limiter implementing IDebugger to abort infinite loops safely.
        /// </summary>
        private sealed class InstructionQuotaDebugger : IDebugger
        {
            private int _instructions;
            private readonly int _limit;

            public InstructionQuotaDebugger(int limit)
            {
                _limit = limit;
            }

            public bool IsPauseRequested()
            {
                if (_limit > 0 && ++_instructions > _limit)
                {
                    throw new ScriptRuntimeException($"Execution instruction limit of {_limit} exceeded.");
                }
                return false;
            }

            public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;
            public void SetDebugService(DebugService debugService) { }
            public void SetSourceCode(SourceCode sourceCode) { }
            public void SetByteCode(string[] byteCode) { }
            public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
            public DebuggerAction GetAction(int ip, SourceRef sourceref) => new DebuggerAction { Action = DebuggerAction.ActionType.Run };
            public void SignalExecutionEnded() => _instructions = 0;
            public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex) { }
            public List<DynamicExpression> GetWatchItems() => new List<DynamicExpression>();
            public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
        }
    }
}
