using System.Globalization;

namespace OpenTrade.LoadTests;

// OpenTrade load tests. See LoadTests/README.md.
//   dotnet run -c Release --project LoadTests -- all            every scenario (~15 min)
//   dotnet run -c Release --project LoadTests -- fleet --quick  one scenario, short steps
// Internal: "server ..." is how the tool starts the app under test in a child process.
// (Not a top-level program: the web app already owns the global "Program" class that WebApplicationFactory needs.)
static class LoadTestMain
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "server") return await ServerMode.Run(ServerOptions.Parse(args));

        int Opt(string name, int def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : def; }
        var what = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "help";
        if (what == "max")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            ThreadPool.SetMinThreads(256, 256);
            await MaxDemo.Run(MaxDemo.Parse(args, Opt));
            Console.WriteLine("\nFull report (every step, per endpoint): " + Report.Write("OpenTrade maximum traffic test"));
            return 0;
        }
        var o = new RunOptions(args.Contains("--quick"), Opt("--max-users", 800), Opt("--bots", 5));
        string[] all = ["limits", "fleet", "ceiling", "orders"];
        var run = what == "all" ? all : all.Contains(what) ? [what] : [];
        if (run.Length == 0)
        {
            Console.WriteLine("usage: dotnet run -c Release --project LoadTests -- <max|all|limits|fleet|ceiling|orders> [--quick] [--max-users N] [--bots N]\n" +
                "  max  [--users 10,50,100,150,200,300] [--bots 10] [--seconds 10]   the 1-2 minute maximum-traffic demo");
            return 1;
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ThreadPool.SetMinThreads(256, 256); // the load generator needs many threads ready at once; this does not touch the server process
        Console.WriteLine($"OpenTrade load test: {string.Join(", ", run)}{(o.Quick ? " (quick)" : "")} on {Environment.ProcessorCount} logical cores");
        foreach (var s in run)
        {
            await (s switch
            {
                "limits" => Scenarios.Limits(o),
                "fleet" => Scenarios.Fleet(o),
                "ceiling" => Scenarios.Ceiling(o),
                _ => Scenarios.Orders(o),
            });
        }
        Console.WriteLine("\nReport written to " + Report.Write("OpenTrade load test results"));
        return 0;
    }
}
