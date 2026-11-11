using System.Runtime.CompilerServices;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FlMessage = Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage;

namespace FoundryLocalConsole;

/// <summary>
/// Microsoft.Extensions.AI IChatClient over Microsoft Foundry Local (in-process, no server).
/// Loads the model from ModelCacheDir (external drive) on first use and keeps it loaded.
/// </summary>
public sealed class FoundryPhiChatClient(
    IOptions<FoundryPhiOptions> options,
    ILogger<FoundryPhiChatClient> logger) : IChatClient
{
    private readonly FoundryPhiOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);   // one generation at a time on the local GPU

    private Func<List<FlMessage>, CancellationToken, Task<(string Text, int? In, int? Out)>>? _complete;
    private Func<List<FlMessage>, CancellationToken, IAsyncEnumerable<string>>? _stream;
    private Action<ChatOptions?>? _applySettings;
    private Func<Task>? _unload;

    /// <summary>Initialise Foundry Local and load the model (call once at startup).</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_complete is not null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_complete is not null) return;

            if (!Directory.Exists(_o.ModelCacheDir))
                throw new DirectoryNotFoundException(
                    $"ModelCacheDir '{_o.ModelCacheDir}' not found. Is the external drive mounted?");

            await FoundryLocalManager.CreateAsync(new Configuration
            {
                AppName       = _o.AppName,
                ModelCacheDir = _o.ModelCacheDir,
                LogLevel      = Microsoft.AI.Foundry.Local.LogLevel.Warning
            }, logger);
            var mgr = FoundryLocalManager.Instance;

            // Windows downloads EPs here; on macOS/Linux this returns immediately
            await mgr.DownloadAndRegisterEpsAsync((ep, pct) =>
                logger.LogDebug("EP {Ep}: {Pct:F0}%", ep, pct));

            var useLocal = !string.IsNullOrWhiteSpace(_o.LocalModelPath);
            var catalog  = await mgr.GetCatalogAsync(useLocal ? CatalogType.Local : CatalogType.Public);

            var model = useLocal
                ? await catalog.RegisterModelAsync(_o.LocalModelPath!, _o.LocalModelId, BuildMetadata())
                : await catalog.GetModelAsync(_o.ModelAlias)
                  ?? throw new InvalidOperationException($"Model '{_o.ModelAlias}' not in catalog");

            if (!useLocal)   // first run downloads into ModelCacheDir; later runs read it from the drive
                await model.DownloadAsync(p => logger.LogInformation("Downloading {Alias}: {P:F0}%", _o.ModelAlias, p));

            await model.LoadAsync();
            var chat = await model.GetChatClientAsync();
            logger.LogInformation("Foundry Local model loaded: {Id}", model.Id);

            _applySettings = o =>
            {
                chat.Settings.Temperature = o?.Temperature ?? 0.1f;
                chat.Settings.TopP        = o?.TopP ?? 0.9f;
                chat.Settings.MaxTokens   = o?.MaxOutputTokens ?? 512;
            };

            _complete = async (msgs, token) =>
            {
                var r = await chat.CompleteChatAsync(msgs, token);
                return (r.Choices[0].Message.Content ?? string.Empty,
                        r.Usage?.PromptTokens, r.Usage?.CompletionTokens);
            };

            _stream = StreamText;
            _unload = () => model.UnloadAsync();

            async IAsyncEnumerable<string> StreamText(
                List<FlMessage> msgs, [EnumeratorCancellation] CancellationToken token)
            {
                await foreach (var chunk in chat.CompleteChatStreamingAsync(msgs, token))
                {
                    var s = chunk.Choices[0].Message?.Content;
                    if (!string.IsNullOrEmpty(s)) yield return s;
                }
            }
        }
        finally { _gate.Release(); }
    }

    // Bring-your-own-model metadata, pattern from the Microsoft.AI.Foundry.Local README.
    // Check property names against the README of your installed version.
    private ModelInfoBuilder BuildMetadata()
    {
        if (!File.Exists(Path.Combine(_o.LocalModelPath!, "genai_config.json")))
            throw new FileNotFoundException("LocalModelPath must contain genai_config.json", _o.LocalModelPath);

        return new ModelInfoBuilder()
            .SetStringProperty(ModelInfoPropertyKeys.Task, "chat-completion")
            .SetStringProperty(ModelInfoPropertyKeys.DisplayName, "MediPhi-Instruct")
            .SetStringProperty(ModelInfoPropertyKeys.DeviceType, "GPU")
            .SetStringProperty(ModelInfoPropertyKeys.ExecutionProvider, "WebGpuExecutionProvider")
            .SetIntProperty(ModelInfoPropertyKeys.ContextLength, _o.ContextLength);
    }

    // ── IChatClient ──────────────────────────────────────────────

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            _applySettings!(options);
            var (text, inTok, outTok) = await _complete!(Map(messages), ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                ModelId = _o.LocalModelPath is null ? _o.ModelAlias : _o.LocalModelId,
                Usage   = new UsageDetails { InputTokenCount = inTok, OutputTokenCount = outTok }
            };
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            _applySettings!(options);
            await foreach (var piece in _stream!(Map(messages), ct))
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
        }
        finally { _gate.Release(); }
    }

    private static List<FlMessage> Map(IEnumerable<ChatMessage> messages) =>
        messages.Select(m => new FlMessage { Role = m.Role.Value, Content = m.Text }).ToList();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        try { _unload?.Invoke().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        _gate.Dispose();
    }
}
