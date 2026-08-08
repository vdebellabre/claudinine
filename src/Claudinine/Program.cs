using Claudinine;

return args switch
{
    ["hook", ..] => HookRunner.Run(Console.OpenStandardInput()),
    ["get", .. var getArgs] => GetVerb.Run(getArgs),
    ["clone", .. var cloneArgs] => CloneVerb.Run(cloneArgs),
    ["restore-compaction-on", .. var onArgs] => RestoreVerb.Run(onArgs, compactionOn: true),
    ["restore-compaction-off", .. var offArgs] => RestoreVerb.Run(offArgs, compactionOn: false),
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
            Console.Error.WriteLine(
                "usage: claudinine <hook|get|clone|restore-compaction-on|restore-compaction-off|version>");
            return 1;
        }
    }
}
