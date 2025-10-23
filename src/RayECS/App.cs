using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RayECS
{
    #region Stages

    [Flags]
    public enum Stage
    {
        None = 0,
        Conf = 1 << 0,
        Boot = 1 << 1,
        Core = 1 << 2,
        Tick = 1 << 3,
        Flow = 1 << 4,
        Draw = 1 << 5,
        Exit = 1 << 6,
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
            // Attribut directement sur la méthode ou sur la méthode de base (override)
            var attr = mi.GetCustomAttribute<StageAllowedAttribute>(inherit: false)
                ?? mi.GetBaseDefinition()?.GetCustomAttribute<StageAllowedAttribute>(inherit: false);
            // Sinon, sur une méthode d’interface implémentée
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
            // Pas d'attribut = pas de restriction
            if (attr is null) return;
            // Vérification du droit d'accès
            if ((attr.Allowed & App.Stage) == 0)
            throw new InvalidOperationException(
                $"Stage {App.Stage} not allowed. Expected: {attr.Allowed}"
            );
        }
    }

    #endregion

    #region States

    public class States(App app)
    {
        private readonly App app = app;
        private readonly List<string> list = [];
        private readonly Dictionary<string, Action> onEnter = [];
        private readonly Dictionary<string, Action> onExit = [];
        private string? current = null;
        private string? pending = null;

        public class Builder(States states, string name)
        {
            private readonly States states = states;
            private readonly string name = name;

            public Builder OnEnter(Action action)
            {
                if (states.onEnter.ContainsKey(name))
                    throw new ArgumentException($"State {name} already has an OnEnter action.");
                states.onEnter[name] = action;
                return this;
            }

            public Builder OnExit(Action action)
            {
                if (states.onExit.ContainsKey(name))
                    throw new ArgumentException($"State {name} already has an OnExit action.");
                states.onExit[name] = action;
                return this;
            }
        }

        [StageAllowed(Stage.Conf)]
        public Builder Add(string name)
        {
            StageGuard.Check();
            if (list.Contains(name))
                throw new ArgumentException($"State {name} already exists.");
            list.Add(name);
            return new Builder(this, name);
        }

        [StageAllowed(Stage.Boot | Stage.Flow)]
        public void Set(string name)
        {
            StageGuard.Check();
            if (!list.Contains(name))
                throw new KeyNotFoundException($"State {name} not found.");
            if (name == current)
                throw new InvalidOperationException($"Already in state {name}.");
            pending = name;
        }

        [StageAllowed(Stage.Core)]
        public void ApplyChange()
        {
            StageGuard.Check();
            if (pending is null) return;
            if (current is not null) onExit[current](); // TODO : à try catcher !!!
            onEnter[pending]();                         // TODO : à try catcher !!!
            current = pending;
            pending = null;
        }

        [StageAllowed(Stage.Tick | Stage.Flow | Stage.Draw)]
        public bool In(string name)
        {
            StageGuard.Check();
            if (!list.Contains(name))
                throw new KeyNotFoundException($"State {name} not found.");
            return name == current;
        }
    }

    #endregion

    #region Resources

    public enum Lifetime { Permanent, Transient }

    public interface IResource { }

    public sealed class Resources(App app)
    {
        private readonly App app = app;
        private readonly Dictionary<Type, (object res, Lifetime lifetime)> list = [];

        [StageAllowed(Stage.Boot | Stage.Tick | Stage.Flow)]
        public void Add<T>(T res, Lifetime lifetime) where T : class, IResource
        {
            StageGuard.Check();
            var t = typeof(T);
            if (list.ContainsKey(t))
                throw new InvalidOperationException($"Resource {t.Name} already exists.");
            list[t] = (res, lifetime);
        }

        [StageAllowed(Stage.Tick | Stage.Flow | Stage.Draw)]
        public bool TryGet<T>(out T res) where T : class, IResource
        {
            StageGuard.Check();
            if (list.TryGetValue(typeof(T), out var entry))
            {
                res = (T)entry.res;
                return true;
            }
            res = null!;
            return false;
        }

        [StageAllowed(Stage.Tick | Stage.Flow)]
        public bool Remove<T>() where T : class, IResource
        {
            StageGuard.Check();
            var t = typeof(T);
            if (!list.TryGetValue(t, out var entry))
                throw new KeyNotFoundException($"Resource {t.Name} does not exist.");
            if (entry.lifetime == Lifetime.Permanent)
                throw new InvalidOperationException($"Cannot remove required resource {t.Name}.");
            return list.Remove(t);
        }
    }

    #endregion

    #region Time

    public class Time : IResource
    {
        private float delta = 0f;
        private float fixedDelta = 0f;
        private float alpha = 0f;

        public float Delta
        {
            [StageAllowed(Stage.Core)]
            set {
                StageGuard.Check();
                delta = value;
            }

            [StageAllowed(Stage.Core)]
            get {
                StageGuard.Check();
                return delta;
            }
        }

        public float Fixed
        {
            [StageAllowed(Stage.Core)]
            set {
                StageGuard.Check();
                fixedDelta = value;
            }

            [StageAllowed(Stage.Core)]
            get {
                StageGuard.Check();
                return fixedDelta;
            }
        }

        public float Alpha
        {
            [StageAllowed(Stage.Core)]
            set {
                StageGuard.Check();
                alpha = value;
            }

            [StageAllowed(Stage.Draw)]
            get {
                StageGuard.Check();
                return alpha;
            }
        }

        public float DeltaTime
        {
            [StageAllowed(Stage.Tick | Stage.Flow)]
            get {
                StageGuard.Check();
                if (App.Stage == Stage.Tick)
                    return fixedDelta;
                else
                    return delta;
            }
        }
    }

    #endregion

    #region Plugins

    public interface IPlugin
    {
        void Build(App app);
    }

    public sealed class Plugins(App app)
    {
        private readonly App app = app;
        private readonly List<string> list = [];

        [StageAllowed(Stage.Conf)]
        public void Add<T>() where T : class, IPlugin, new()
        {
            StageGuard.Check();
            string name = typeof(T).Name;
            if (list.Contains(name))
                throw new ArgumentException($"Plugin {name} already added.");
            list.Add(name);
            (new T()).Build(app);
        }
    }

    #endregion

    #region App

    public sealed class App
    {
        public static Stage Stage { get; private set; } = Stage.Conf;

        public static int New(Action<App> fn)
        {
            if (Stage != Stage.Conf)
                throw new InvalidOperationException("App.New() can only be called once.");

            fn(new App());

            return 0;
        }

        public readonly Plugins plugins;
        public readonly Resources resources;
        public readonly States states;

        private App()
        {
            plugins = new(this);
            resources = new(this);
            states = new(this);
        }

        private void RunStage(Stage stage)
        {
            App.Stage = stage;
            // TODO : run stage systems
            App.Stage = Stage.Core;
        }

        public void Run(string? firstState = null)
        {
            Stage = Stage.Boot;

            if (firstState is not null)
                states.Set(firstState);

            // Init window.
            Raylib.SetConfigFlags(ConfigFlags.VSyncHint); // pacing par l’écran
            Raylib.InitWindow(800, 450, "Realmlet");
            if (Raylib.IsWindowState(ConfigFlags.VSyncHint))
                Raylib.SetTargetFPS(0);
            else
                Raylib.SetTargetFPS(60);

            // TODO : run stage systems

            Time time = new();
            resources.Add(time, Lifetime.Permanent);

            App.Stage = Stage.Core;

            // Config for fixed update
            float fixedDt = 1f / 30f;

            // Time / resource
            const float MaxFrameTime = 0.25f;
            const int MaxFixedSteps = 8;

            float dt;
            float alpha;
            float acc = 0f;
            double elapsed = 0.0;
            double last = Raylib.GetTime();
            double now;

            while (!WindowShouldClose())
            {
                now = Raylib.GetTime();
                dt = MathF.Min((float)(now - last), MaxFrameTime);
                acc = MathF.Min(acc + dt, MaxFrameTime);
                last = now;
                elapsed += dt;
                alpha = acc / fixedDt;

                //systemManager.RunUpdate(dt);

                // 2) FIXED UPDATE (0..N) — logique/physique déterministe
                int steps = 0;
                while (acc >= fixedDt && steps < MaxFixedSteps)
                {
                    //systemManager.RunFixedUpdate(fixedDt);
                    acc -= fixedDt;
                    steps++;
                }
                if (steps == MaxFixedSteps && acc >= fixedDt) {
                    acc = MathF.Min(acc, fixedDt - 1e-6f);
                }

                // 3) RENDER (1x par frame) — interpolation possible avec alpha
                // ex : var posSim = Lerp(prev.Pos, curr.Pos, alpha);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Black);
                //systemManager.RunDraw(alpha);
                Raylib.EndDrawing();
            }
            // Cleanup
            Raylib.CloseWindow();
        }
    }

    #endregion
}
