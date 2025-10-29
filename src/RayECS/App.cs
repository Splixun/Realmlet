using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RayECS
{
    #region Core

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

    public static class Guard
    {
        [Conditional("DEBUG")]
        public static void StageAllowed(Stage allowed)
        {
            if ((allowed & App.Stage) == 0)
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
    
    #region EntityManager

    public sealed class EntityManager(App app)
    {
        private readonly App app = app;
        private int sessionId = GenerateU16();
        private int counter = 0;

        public ushort SessionId => unchecked((ushort)Volatile.Read(ref sessionId));
        public uint Counter => unchecked((uint)Volatile.Read(ref counter));
        
        private static ushort GenerateU16()
            => (ushort)RandomNumberGenerator.GetInt32(0, 1 << 16);

        public EntityId Spawn()
        {
            // Incrément atomique : évite les collisions entre threads.
            uint index = unchecked((uint)Interlocked.Increment(ref counter));
            // Lorsque l'index wrappe et redevient 0, on démarre une nouvelle "session".
            // Une seule thread verra index==0, donc un seul write ici.
            if (index == 0u) 
                Volatile.Write(ref sessionId, GenerateU16());
            // Lire le SessionId courant après éventuelle mise à jour.
            ushort session = SessionId;
            // Tirer un token et vérifier l'unicité côté ComponentManager.
            // La proba de boucle est extrêmement faible, mais on boucle tant que nécessaire.
            EntityId id;
            do
            {
                ushort token = GenerateU16();
                id = EntityId.FromParts(session, token, index);
            } while (false);
            // TODO : while (app.componentManager.Exists(id));
            return id;
        }

        public override string ToString()
            => $"EntityManager(Session={SessionId:X4}, Counter={Counter})";
    }

    #endregion

    #region EntityId

    public readonly struct EntityId(ulong value) :
        IEquatable<EntityId>, IComparable<EntityId>, ISpanFormattable
    {
        public ulong Value { get; } = value;
        public ushort SessionId => (ushort)(Value >> 48);
        public ushort Token     => (ushort)((Value >> 32) & 0xFFFF);
        public uint   Index     => (uint)(Value & 0xFFFF_FFFF);

        public bool Equals(EntityId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(EntityId other) => Value.CompareTo(other.Value);
        public static bool operator ==(EntityId a, EntityId b) => a.Value == b.Value;
        public static bool operator !=(EntityId a, EntityId b) => a.Value != b.Value;

        static readonly char[] Hex = "0123456789ABCDEF".ToCharArray();
        static void WriteHexN(Span<char> dst, uint v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                dst[i] = Hex[(int)(v & 0xF)];
                v >>= 4;
            }
        }
        static void WriteHex16(Span<char> dst, ushort v) => WriteHexN(dst, v, 4);
        static void WriteHex32(Span<char> dst, uint v)  => WriteHexN(dst, v, 8);

        public static EntityId FromParts(ushort sessionId, ushort token, uint index)
        {
            ulong value = ((ulong)sessionId << 48) | ((ulong)token << 32) | index;
            return new EntityId(value);
        }


        public override string ToString()
            => string.Create(17, this, static (dst, id) =>
            {
                WriteHex16(dst[..4],  id.SessionId);
                dst[4]  = '-';
                WriteHex16(dst.Slice(5, 4), id.Token);
                dst[9]  = '-';
                WriteHex32(dst.Slice(10, 8), id.Index);
            });


        public string ToString(string? format, IFormatProvider? provider) => ToString();


        public bool TryFormat(Span<char> destination, out int charsWritten,
            ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            _ = format; _ = provider;
            if (destination.Length < 17) { charsWritten = 0; return false; }
            WriteHex16(destination[..4],  SessionId);
            destination[4] = '-';
            WriteHex16(destination.Slice(5, 4), Token);
            destination[9] = '-';
            WriteHex32(destination.Slice(10, 8), Index);
            charsWritten = 17;
            return true;
        }
        
        public static bool TryParse(string s, out EntityId id)
        {
            id = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            ReadOnlySpan<char> span = s.AsSpan().Trim();
            if (span.Length != 17 || span[4] != '-' || span[9] != '-') return false;
            var p1 = span[..4];
            var p2 = span.Slice(5, 4);
            var p3 = span.Slice(10, 8);

            if (!ushort.TryParse(p1, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var session)) return false;
            if (!ushort.TryParse(p2, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var token)) return false;
            if (!uint.TryParse  (p3, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var index)) return false;

            id = FromParts(session, token, index);
            return true;
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
            Guard.StageAllowed(Stage.Boot | Stage.Flow);
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
            Guard.StageAllowed(Stage.Tick | Stage.Flow | Stage.Draw);
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
            Guard.StageAllowed(Stage.Core | Stage.Tick | Stage.Flow);
            var t = typeof(T);
            if (_list.ContainsKey(t))
                throw new InvalidOperationException($"Resource {t.Name} already exists.");
            _list[t] = (res, lifetime);
        }

        // TODO : Methode pour tester l'existence d'une ressource !!!

        public bool TryGet<T>(out T res) where T : class, IResource
        {
            Guard.StageAllowed(Stage.Tick | Stage.Flow | Stage.Draw);
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
            Guard.StageAllowed(Stage.Tick | Stage.Flow);
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
                Guard.StageAllowed(Stage.Core | Stage.Tick);
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
                Guard.StageAllowed(Stage.Core | Stage.Flow);
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

        public readonly EntityManager entities;
        public readonly Plugins plugins;
        public readonly Resources resources;
        public readonly States states;

        private App()
        {
            entities = new(this);
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
