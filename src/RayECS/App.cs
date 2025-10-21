using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RayECS
{
    public enum Stage { Conf, Init, Step, Tick, Draw, Exit }

    public sealed class Scheduler
    {
        private readonly App app;
        private readonly Dictionary<Stage, List<Action>> schedules;

        public Scheduler(App app)
        {
            this.app = app;
            schedules = [];
            foreach (var stage in Enum.GetValues(typeof(Stage)))
                schedules.Add((Stage)stage, []);
        }
    }

    public sealed class Commands(App app)
    {
        private readonly App app = app;
    }

    public interface IState
    {
        void OnEnter(Commands commands, string previous) { }
        void OnExit(Commands commands, string next) { }
    }

    public sealed class States(App app)
    {
        private readonly App app = app;
        private readonly Dictionary<Type, IState> states = [];
        private Type? current;

        public string Current => current?.Name ?? "";

        public void Add<T>() where T : class, IState, new()
        {
            var t = typeof(T);
            if (states.ContainsKey(t))
                throw new ArgumentException($"State {t.Name} already exists.");
            states[t] = new T();
        }

        public void Set<T>() where T : class, IState
        {
            var next = typeof(T);
            if (!states.TryGetValue(next, out var state))
                throw new KeyNotFoundException($"State {next.Name} not found.");
            var previous = current;
            if (next == previous)
                throw new InvalidOperationException($"Already in state {previous.Name}.");
            if (previous is not null)
                states[previous].OnExit(app.commands, next.Name);
            state.OnEnter(app.commands, previous?.Name ?? "");
            current = next;
        }

        public bool In<T>() where T : class, IState
        {
            var t = typeof(T);
            if (!states.ContainsKey(t))
                throw new KeyNotFoundException($"State {t.Name} not found.");
            return current == t;
        }
    }

    public sealed class App
    {
        public readonly States states;
        public readonly Scheduler scheduler;
        public readonly Commands commands;

        public App()
        {
            states = new(this);
            scheduler = new(this);
            commands = new(this);
        }
    }
}
