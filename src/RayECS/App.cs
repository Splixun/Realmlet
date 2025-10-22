using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RayECS
{
    [Flags]
    public enum Stage
    {
        None    = 0,
        Engine  = 1 << 0,
        Config  = 1 << 1,
        Startup = 1 << 2,
        Inputs  = 1 << 3,
        Cond    = 1 << 4,
        Tick    = 1 << 5,
        Update  = 1 << 6,
        Draw    = 1 << 7,
        Exit    = 1 << 8,
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class StageAllowedAttribute(Stage allowed) : Attribute
    {
        public Stage Allowed { get; } = allowed;
    }

    public static class StageGuard
    {
        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Check()
        {
            CheckImpl(new StackFrame(1).GetMethod() as MethodInfo);
        }

        [Conditional("DEBUG")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Check(MethodBase method)
        {
            CheckImpl(method as MethodInfo);
        }

        private static void CheckImpl(MethodInfo? mi)
        {
            if (mi is null) return;

            // 1) Attribut directement sur la méthode ou (??) sur la méthode de base (override)
            var attr = mi.GetCustomAttribute<StageAllowedAttribute>(inherit: false)
                ?? mi.GetBaseDefinition()?.GetCustomAttribute<StageAllowedAttribute>(inherit: false);

            // 2) Sinon, sur une méthode d’interface implémentée
            if (attr is null && mi.DeclaringType is Type t)
            {
                foreach (var itf in t.GetInterfaces())
                {
                    var map = t.GetInterfaceMap(itf);
                    for (int i = 0; i < map.TargetMethods.Length; i++)
                        if (map.TargetMethods[i] == mi)
                        {
                            var method = map.InterfaceMethods[i];
                            attr = method.GetCustomAttribute<StageAllowedAttribute>(inherit: false);
                            if (attr != null) break;
                        }
                    if (attr != null) break;
                }
            }

            if (attr is null) return; // Pas d'attribut = pas de restriction

            if ((attr.Allowed & App.Stage) == 0)
            throw new InvalidOperationException(
                $"Stage {App.Stage} not allowed. Expected: {attr.Allowed}"
            );
        }
    }

    public interface IState
    {
        void OnEnter() { }
        void OnExit() { }
    }

    public class States(App app)
    {
        private readonly App app = app;
        private readonly Dictionary<Type, IState> list = [];
        private Type? current = null;
        private Type? pending = null;

        public string Current => current?.Name ?? "";

        [StageAllowed(Stage.Config)]
        public void Add<T>() where T : class, IState, new()
        {
            StageGuard.Check();
            var state = typeof(T);
            if (list.ContainsKey(state))
                throw new ArgumentException($"State {state.Name} already exists.");
            list[state] = new T();
        }

        [StageAllowed(Stage.Update)]
        public void Set<T>() where T : class, IState
        {
            StageGuard.Check();
            var next = typeof(T);
            if (!list.ContainsKey(next))
                throw new KeyNotFoundException($"State {next.Name} not found.");
            pending = next;
        }

        [StageAllowed(Stage.Engine)]
        public void ApplyChange()
        {
            StageGuard.Check();
            if (pending is null) return;
            var previous = current;
            if (previous == pending)
                throw new InvalidOperationException($"Already in state {pending.Name}.");
            if (previous is not null)
                list[previous].OnExit();
            list[pending].OnEnter();
            current = pending;
            pending = null;
        }

        [StageAllowed(Stage.Cond)]
        public bool In<T>() where T : class, IState
        {
            StageGuard.Check();
            var state = typeof(T);
            if (!list.ContainsKey(state))
                throw new KeyNotFoundException($"State {state.Name} not found.");
            return current == state;
        }
    }

    public sealed class App
    {
        public static Stage Stage { get; private set; } = Stage.Config;

        public static void AllowedFor(params Stage[] allowed)
        {
            foreach (var stage in allowed)
                if (stage == Stage) return;
            throw new InvalidOperationException(
                $"Stage {Stage} is not allowed. Expected: {string.Join(',', allowed)}."
            );
        }

        public static int New(Action<App> fn)
        {
            if (Stage != Stage.Config)
                throw new InvalidOperationException("App.New() can only be called once.");

            fn(new App());

            return 0;
        }

        private App()
        {

        }
    }
}
