using Claudinine;

return args switch
{
    ["hook", ..] => HookRunner.Run(Console.OpenStandardInput()),
    ["get", .. var getArgs] => GetVerb.Run(getArgs),
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
            Console.Error.WriteLine("usage: claudinine <hook|get|version>");
            return 1;
        }
    }
}
