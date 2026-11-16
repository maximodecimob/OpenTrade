using System.Globalization;
using OpenTrade.Bots;

const string Help = """
OpenTrade example bots (C#)

  dotnet run --project Bots -- setup [--url URL] [--allocation 10000] [--broker "Alpaca Paper"] [--keys keys.json]
      Creates the three bots in OpenTrade with one API key each and saves the keys to keys.json.
      Asks for the admin login (or reads OT_ADMIN_EMAIL / OT_ADMIN_PASSWORD).

  dotnet run --project Bots -- run <all|sma|rsi|dca> [options]
      --url URL          OpenTrade address (default http://localhost:5080, or env OT_URL)
      --dry-run          print what would be traded, place nothing
      --once             run one check and exit
      --interval SEC     seconds between checks (default 3600; DCA 900)
      --symbols A,B,C    override the symbols (single bot only)
      --amount USD       DCA: amount per symbol and purchase (default 50)
      --every-hours H    DCA: hours between purchases (default 24)
      --keys FILE        key file (default ./keys.json, or env OT_KEY for one key)

  Bots: sma = SMA Crossover, rsi = RSI Mean Reversion, dca = DCA Index Buyer
""";

IBot[] all = [new SmaCrossoverBot(), new RsiMeanReversionBot(), new DcaBot()];

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Help); return 0; }

var opt = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (var i = args[0] == "run" ? 2 : 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"Unexpected argument '{args[i]}'.\n\n{Help}"); return 2; }
    var name = args[i][2..];
    if (name is "dry-run" or "once") flags.Add(name);
    else if (i + 1 < args.Length) opt[name] = args[++i];
    else { Console.Error.WriteLine($"Missing value for --{name}."); return 2; }
}
string Get(string k, string d) => opt.TryGetValue(k, out var v) ? v : d;
decimal Dec(string k, decimal d) => decimal.TryParse(Get(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
var url = Get("url", Environment.GetEnvironmentVariable("OT_URL") ?? "http://localhost:5080");
var keysPath = Path.GetFullPath(Get("keys", "keys.json"));

try
{
    if (args[0] == "setup")
    {
        var email = Environment.GetEnvironmentVariable("OT_ADMIN_EMAIL");
        if (string.IsNullOrEmpty(email)) { Console.Write("Admin email: "); email = Console.ReadLine()?.Trim() ?? ""; }
        var password = Environment.GetEnvironmentVariable("OT_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(password)) password = Setup.ReadSecret("Admin password: ");
        using var http = Setup.Admin(url);
        var results = await Setup.Run(http, email, password, all, Dec("allocation", 10_000), opt.GetValueOrDefault("broker"), keysPath, Console.WriteLine);
        foreach (var r in results) Console.WriteLine($"- {r.Name}: {r.Status}");
        Console.WriteLine($"\nKeys saved to {keysPath} (keep this file private).\nNext:  dotnet run --project Bots -- run all --dry-run --once");
        return 0;
    }

    if (args[0] != "run" || args.Length < 2) { Console.Error.WriteLine(Help); return 2; }
    var which = args[1].ToLowerInvariant();
    var chosen = which switch
    {
        "all" => all,
        "sma" => [all[0]],
        "rsi" => [all[1]],
        "dca" => [all[2]],
        _ => null,
    };
    if (chosen is null) { Console.Error.WriteLine($"Unknown bot '{which}'. Use all, sma, rsi or dca."); return 2; }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var tasks = chosen.Select(bot =>
    {
        var symbols = (chosen.Length == 1 ? Get("symbols", bot.DefaultSymbols) : bot.DefaultSymbols)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToArray();
        var o = new BotOptions(symbols, flags.Contains("dry-run"), Dec("amount", 50), (double)Dec("every-hours", 24));
        var interval = TimeSpan.FromSeconds((double)Dec("interval", bot is DcaBot ? 900 : 3600));
        var client = new OpenTradeClient(url, KeyStore.Load(bot.Name, keysPath));
        return BotRunner.Run(bot, client, o, interval, flags.Contains("once"), cts.Token);
    }).ToArray();
    await Task.WhenAll(tasks);
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}
