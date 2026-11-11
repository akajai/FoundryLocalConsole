using System.Diagnostics;
using System.Text.Json;
using FoundryLocalConsole;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ── Config & logging ───────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddCommandLine(args)                       // e.g. --FoundryLocal:ModelAlias=phi-4-mini
    .Build();

var opts = configuration.GetSection(FoundryPhiOptions.SectionName).Get<FoundryPhiOptions>()
           ?? throw new InvalidOperationException("Missing 'FoundryLocal' section in appsettings.json");
opts.ApplyActiveProfile();

if (args.Contains("--list"))
{
    foreach (var (name, p) in opts.Profiles)
        Console.WriteLine($"{name,-20} {p.Description}");
    return;
}

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("App");

Directory.CreateDirectory(opts.ModelCacheDir);
log.LogInformation("Model cache: {Dir}", opts.ModelCacheDir);
log.LogInformation("Profile: {Profile}  Model: {Model}", opts.ActiveProfile ?? "(none)", opts.LocalModelPath ?? opts.ModelAlias);

// ── Load (same IChatClient BHRF will use) ─────────────────────────
using var client = new FoundryPhiChatClient(Options.Create(opts), loggerFactory.CreateLogger<FoundryPhiChatClient>());
IChatClient chat = client;

var sw = Stopwatch.StartNew();
await client.EnsureLoadedAsync();
log.LogInformation("Model loaded in {S:F1}s", sw.Elapsed.TotalSeconds);
log.LogInformation("Tip: open Activity Monitor > Window > GPU History to watch the GPU during the tests.");

// ── Test 1: warm-up / latency ─────────────────────────────────────
sw.Restart();
var hello = await chat.GetResponseAsync("Reply with just: OK", new ChatOptions { MaxOutputTokens = 8, Temperature = 0 });
log.LogInformation("Warm-up reply '{Text}' in {S:F1}s", hello.Text.Trim(), sw.Elapsed.TotalSeconds);

// ── Test 2: voice-note style extraction to JSON ───────────────────
const string systemPrompt = """
    You extract structured care notes from a carer's spoken transcript.
    Known clients:
      * 7d3c2a10-0000-0000-0000-000000000001: Mary Thomas
      * 7d3c2a10-0000-0000-0000-000000000002: John Varghese
    Return ONLY one JSON object, no markdown, with these fields:
    {
      "clientId": "<guid from the list or null>",
      "noteType": "Daily | Incident | Medication | RiskAssessment | None",
      "summary": "<1-2 sentences>",
      "mood": "<string or null>",
      "risks": ["<string>"],
      "followUpRequired": true | false
    }
    """;

const string transcript =
    "Visited Mary Thomas this morning around nine. She was a bit low in mood, said she didn't sleep well. " +
    "She took her morning tablets with breakfast. She nearly slipped in the bathroom because the mat was wet, " +
    "no injury. I dried the floor and moved the mat. Daughter should be told about the bathroom mat.";

var options = new ChatOptions { Temperature = 0.1f, TopP = 0.9f, MaxOutputTokens = 400 };

sw.Restart();
var response = await chat.GetResponseAsync(
[
    new ChatMessage(ChatRole.System, systemPrompt),
    new ChatMessage(ChatRole.User, transcript)
], options);
var secs = sw.Elapsed.TotalSeconds;

var outTok = response.Usage?.OutputTokenCount;
log.LogInformation("Extraction in {S:F1}s  (in={In} out={Out} tok{Rate})",
    secs, response.Usage?.InputTokenCount, outTok,
    outTok is > 0 ? $", {outTok / secs:F1} tok/s" : "");

Console.WriteLine();
Console.WriteLine("── Raw model output ──");
Console.WriteLine(response.Text);
Console.WriteLine();

try
{
    var text  = response.Text;
    var start = text.IndexOf('{');
    var end   = text.LastIndexOf('}');
    if (start < 0 || end <= start) throw new JsonException("No JSON object found");
    using var doc = JsonDocument.Parse(text[start..(end + 1)]);
    Console.WriteLine("── Parsed JSON (valid) ──");
    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
}
catch (JsonException ex)
{
    log.LogWarning("Output was not valid JSON: {Msg}", ex.Message);
}

// ── Optional interactive chat: dotnet run -- --chat ───────────────
if (args.Contains("--chat"))
{
    Console.WriteLine();
    Console.WriteLine("Chat mode (empty line to exit)");
    var history = new List<ChatMessage> { new(ChatRole.System, "You are a concise, helpful assistant.") };
    while (true)
    {
        Console.Write("\n> ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) break;
        history.Add(new ChatMessage(ChatRole.User, input));

        var reply = new System.Text.StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync(history, options))
        {
            Console.Write(update.Text);
            reply.Append(update.Text);
        }
        Console.WriteLine();
        history.Add(new ChatMessage(ChatRole.Assistant, reply.ToString()));
    }
}

log.LogInformation("Done.");
