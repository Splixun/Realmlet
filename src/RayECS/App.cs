using System.Diagnostics;

using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RayECS
{
    #region Core

    public enum Stage { Conf, Boot, Core, Tick, Flow, Draw, Exit }

    public static class Guard
    {
        [Conditional("DEBUG")]
        public static void StageAllowed(params Stage[] allowed)
        {
            foreach (var stage in allowed)
                if (App.Stage == stage) return;
            throw new InvalidOperationException(
                $"Stage {App.Stage} not allowed. Expected: {string.Join(", ", allowed)}"
            );
        }

        [Conditional("DEBUG")]
        public static void NamedMethod(Delegate d)
        {
            if (
                d.Target != null // Capture
                || (d.Method.DeclaringType?.Name.Contains("<>c") ?? false) // Lambda
                || (d.Method.DeclaringType?.Name.Contains("DisplayClass") ?? false) // Closure
            )
                throw new InvalidOperationException(
                    "Expected user-named method, but received lambda, closure or local function."
                );
        }
    }

    #endregion

    #region States

    public class States(App app)
    {
        private readonly App _app = app;
        private readonly List<string> _list = [];
        private readonly Dictionary<string, Action<App>> _onEnter = [];
        private readonly Dictionary<string, Action<App>> _onExit = [];
        private string? _current = null;
        private string? _pending = null;

        public class Builder(States states, string name)
        {
            private readonly States _states = states;
            private readonly string _name = name;

            public Builder OnEnter(Action<App> fn)
            {
                if (_states._onEnter.ContainsKey(_name))
                    throw new ArgumentException($"State {_name} already has an OnEnter action.");
                _states._onEnter[_name] = fn;
                return this;
            }

            public Builder OnExit(Action<App> fn)
            {
                if (_states._onExit.ContainsKey(_name))
                    throw new ArgumentException($"State {_name} already has an OnExit action.");
                _states._onExit[_name] = fn;
                return this;
            }
        }

        public Builder Add(string name)
        {
            Guard.StageAllowed(Stage.Conf);
            if (_list.Contains(name))
                throw new ArgumentException($"State {name} already exists.");
            _list.Add(name);
            return new Builder(this, name);
        }

        public void Set(string name)
        {
            Guard.StageAllowed(Stage.Boot, Stage.Flow);
            if (!_list.Contains(name))
                throw new KeyNotFoundException($"State {name} not found.");
            if (name == _current)
                throw new InvalidOperationException($"Already in state {name}.");
            _pending = name;
        }

        public void ApplyChange()
        {
            Guard.StageAllowed(Stage.Core);
            if (_pending is null) return;
            if (_current is not null && _onExit.TryGetValue(_current, out var fn))
                fn(_app); // TODO : à try catcher !!!
            if (_onEnter.TryGetValue(_pending, out fn))
                fn(_app); // TODO : à try catcher !!!
            _current = _pending;
            _pending = null;
        }

        public bool In(string name)
        {
            Guard.StageAllowed(Stage.Tick, Stage.Flow, Stage.Draw);
            if (!_list.Contains(name))
                throw new KeyNotFoundException($"State {name} not found.");
            return name == _current;
        }
    }

    #endregion

    #region Resources

    public enum Lifetime { Permanent, Transient }

    public interface IResource { }

    public sealed class Resources(App app)
    {
        private readonly App _app = app;
        private readonly Dictionary<Type, (object res, Lifetime lifetime)> _list = [];

        public void Add<T>(T res, Lifetime lifetime) where T : class, IResource
        {
            Guard.StageAllowed(Stage.Core, Stage.Tick, Stage.Flow);
            var t = typeof(T);
            if (_list.ContainsKey(t))
                throw new InvalidOperationException($"Resource {t.Name} already exists.");
            _list[t] = (res, lifetime);
        }

        // TODO : Methode pour tester l'existence d'une ressource !!!

        public bool TryGet<T>(out T res) where T : class, IResource
        {
            Guard.StageAllowed(Stage.Tick, Stage.Flow, Stage.Draw);
            if (_list.TryGetValue(typeof(T), out var entry))
            {
                res = (T)entry.res;
                return true;
            }
            res = null!;
            return false;
        }

        public bool Remove<T>() where T : class, IResource
        {
            Guard.StageAllowed(Stage.Tick, Stage.Flow);
            var t = typeof(T);
            if (!_list.TryGetValue(t, out var entry))
                throw new KeyNotFoundException($"Resource {t.Name} does not exist.");
            if (entry.lifetime == Lifetime.Permanent)
                throw new InvalidOperationException($"Cannot remove required resource {t.Name}.");
            return _list.Remove(t);
        }
    }

    #endregion

    #region Systems

    public sealed class Systems(App app)
    {
        private readonly App _app = app;
        private readonly List<Stage> _allowed = [
            Stage.Boot, Stage.Tick, Stage.Flow, Stage.Draw, Stage.Exit
        ];
        private readonly Dictionary<Stage, Dictionary<string, Action<App>>> _list = [];

        public void Add(Stage stage, Action<App> fn)
        {
            Guard.StageAllowed(Stage.Conf);
            if (!_allowed.Contains(stage))
                throw new ArgumentException(
                    $"Stage {stage} doesn't accept systems. " +
                    $"Allowed stages: {string.Join(", ", _allowed)}."
                );
            Guard.NamedMethod(fn);
            var name = fn.Method.Name;
            if (!_list.ContainsKey(stage))
                _list[stage] = [];
            if (_list[stage].ContainsKey(name))
                throw new ArgumentException($"System {name} already exists.");
            _list[stage][name] = fn;
        }

        public void Run(Stage stage)
        {
            Guard.StageAllowed(Stage.Core);
            if (!_list.TryGetValue(stage, out var systems)) return;
            foreach (var fn in systems.Values)
                fn(_app); // TODO : à try catcher !!!
        }
    }

    #endregion

    #region Time

    public class Time : IResource
    {
        private float _tickDelta = 0f;
        private float _flowDelta = 0f;
        private float _alpha = 0f;

        public float TickDelta
        {
            set {
                Guard.StageAllowed(Stage.Core);
                _tickDelta = value;
            }

            get {
                Guard.StageAllowed(Stage.Core, Stage.Tick);
                return _tickDelta;
            }
        }

        public float FlowDelta
        {
            set {
                Guard.StageAllowed(Stage.Core);
                _flowDelta = value;
            }

            get {
                Guard.StageAllowed(Stage.Core, Stage.Flow);
                return _flowDelta;
            }
        }

        public float Alpha
        {
            set {
                Guard.StageAllowed(Stage.Core);
                _alpha = value;
            }

            get {
                Guard.StageAllowed(Stage.Draw);
                return _alpha;
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
        private readonly App _app = app;
        private readonly List<string> _list = [];

        public void Add<T>() where T : class, IPlugin, new()
        {
            Guard.StageAllowed(Stage.Conf);
            string name = typeof(T).Name;
            if (_list.Contains(name))
                throw new ArgumentException($"Plugin {name} already added.");
            _list.Add(name);
            (new T()).Build(_app);
        }
    }

    #endregion

    #region App

    public sealed class App
    {
        public static Stage Stage { get; private set; } = Stage.Conf;

        public static int Run(Action<App> fn)
        {
            Guard.NamedMethod(fn);
            if (Stage != Stage.Conf)
                throw new InvalidOperationException("App.New() can only be called once.");

            var app = new App();
            fn(app);
            app.Boot();

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

        private void Boot()
        {
            Stage = Stage.Boot;

            // Init window.
            Raylib.SetConfigFlags(ConfigFlags.VSyncHint); // pacing par l’écran
            Raylib.InitWindow(800, 450, "Realmlet");
            if (Raylib.IsWindowState(ConfigFlags.VSyncHint))
                Raylib.SetTargetFPS(0);
            else
                Raylib.SetTargetFPS(60);

            // TODO : run stage systems

            App.Stage = Stage.Core;

            Time time = new() { TickDelta = 1f / 30f, FlowDelta = 0f, Alpha = 0f };
            resources.Add(time, Lifetime.Permanent);

            // Time / resource
            const float MaxFrameTime = 0.25f;
            const int MaxFixedSteps = 8;

            float acc = 0f;
            double elapsed = 0.0;
            double last = Raylib.GetTime();
            double now;

            while (!WindowShouldClose())
            {
                now = Raylib.GetTime();
                time.FlowDelta = MathF.Min((float)(now - last), MaxFrameTime);
                acc = MathF.Min(acc + time.FlowDelta, MaxFrameTime);
                last = now;
                elapsed += time.FlowDelta;
                time.Alpha = acc / time.TickDelta;

                //systemManager.RunUpdate();

                // 2) FIXED UPDATE (0..N) — logique/physique déterministe
                int steps = 0;
                while (acc >= time.TickDelta && steps < MaxFixedSteps)
                {
                    //systemManager.RunFixedUpdate();
                    acc -= time.TickDelta;
                    steps++;
                }
                if (steps == MaxFixedSteps && acc >= time.TickDelta) {
                    acc = MathF.Min(acc, time.TickDelta - 1e-6f);
                }

                // 3) RENDER (1x par frame) — interpolation possible avec alpha
                // ex : var posSim = Lerp(prev.Pos, curr.Pos, alpha);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Black);
                //systemManager.RunDraw();
                Raylib.EndDrawing();
            }
            // Cleanup
            Raylib.CloseWindow();
        }
    }

    #endregion
}
