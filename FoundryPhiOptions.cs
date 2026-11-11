using System.ComponentModel.DataAnnotations;

namespace FoundryLocalConsole;

/// <summary>Microsoft Foundry Local settings for the "phi" chat model.</summary>
public sealed class FoundryPhiOptions
{
    public const string SectionName = "FoundryLocal";

    [Required] public string AppName { get; set; } = "bhrf";

    /// <summary>Where Foundry Local stores/reads models — point at the external drive.</summary>
    [Required] public string ModelCacheDir { get; set; } = string.Empty;

    /// <summary>Name of the entry in Profiles to use. Empty = use the flat fields below.</summary>
    public string? ActiveProfile { get; set; }

    /// <summary>Catalog alias, e.g. "phi-3.5-mini". Ignored when LocalModelPath is set.</summary>
    public string ModelAlias { get; set; } = "phi-3.5-mini";

    /// <summary>Folder of your own ONNX model (contains genai_config.json).</summary>
    public string? LocalModelPath { get; set; }

    /// <summary>Id used when registering LocalModelPath.</summary>
    public string LocalModelId { get; set; } = "mediphi-instruct-generic-gpu:1";

    public int ContextLength { get; set; } = 4096;

    /// <summary>Named model setups, selected with ActiveProfile.</summary>
    public Dictionary<string, FoundryModelProfile> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the active profile's values onto the flat fields.</summary>
    public void ApplyActiveProfile()
    {
        if (!string.IsNullOrWhiteSpace(ActiveProfile))
        {
            var profile = Profiles.FirstOrDefault(p =>
                string.Equals(p.Key, ActiveProfile, StringComparison.OrdinalIgnoreCase)).Value
                ?? throw new InvalidOperationException(
                    $"Profile '{ActiveProfile}' not found. Available: {string.Join(", ", Profiles.Keys)}");

            LocalModelPath = profile.LocalModelPath;
            if (!string.IsNullOrWhiteSpace(profile.ModelAlias))   ModelAlias   = profile.ModelAlias;
            if (!string.IsNullOrWhiteSpace(profile.LocalModelId)) LocalModelId = profile.LocalModelId;
            if (profile.ContextLength is > 0)                     ContextLength = profile.ContextLength.Value;
        }

        if (string.IsNullOrWhiteSpace(LocalModelPath)) LocalModelPath = null;
    }
}

public sealed class FoundryModelProfile
{
    public string? Description    { get; set; }
    public string? ModelAlias     { get; set; }   // catalog model
    public string? LocalModelPath { get; set; }   // or your own ONNX folder
    public string? LocalModelId   { get; set; }
    public int?    ContextLength  { get; set; }
}
