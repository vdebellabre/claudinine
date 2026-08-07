using Claudinine;

// Verbs: `hook` (invoked by Claude Code hooks, JSON on stdin) is the only one
// wired up today; user-facing verbs (restore, get) come with the mirror work.
return args switch
{
    ["hook", ..] => HookRunner.Run(Console.OpenStandardInput()),
    ["version", ..] or ["--version", ..] => Verbs.Version(),
    _ => Verbs.Usage(),
};

namespace Claudinine
{
    internal static class Verbs
    {
        public static int Version()
        {
            Console.WriteLine(typeof(Verbs).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            return 0;
        }

        public static int Usage()
        {
            Console.Error.WriteLine("usage: claudinine <hook|version>");
            return 1;
        }
    }
}
