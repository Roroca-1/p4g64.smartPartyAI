using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reloaded.Mod.Interfaces;

namespace p4g64.smartPartyAI;

/// <summary>
/// Base class for Reloaded-II JSON-serialized mod configurations with live reload support.
/// </summary>
public class Configurable<TConfig> : IUpdatableConfigurable where TConfig : Configurable<TConfig>, new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private FileSystemWatcher? _watcher;

    [Browsable(false)]
    [JsonIgnore]
    public string ConfigName { get; set; } = "Default Config";

    [Browsable(false)]
    [JsonIgnore]
    public string? FilePath { get; private set; }

    [Browsable(false)]
    [JsonIgnore]
    public Action? Save { get; set; }

    public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

    public void Initialize(string filePath)
    {
        FilePath = filePath;
        Save = SaveToFile;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        if (!File.Exists(filePath))
            SaveToFile();

        StartWatcher();
    }

    public static TConfig Load(string filePath)
    {
        TConfig config;
        bool rewrite = false;
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                config = new TConfig();
                rewrite = true;
            }
            else
            {
                try
                {
                    config = JsonSerializer.Deserialize<TConfig>(json, SerializerOptions) ?? new TConfig();
                }
                catch (JsonException)
                {
                    config = new TConfig();
                    rewrite = true;
                }
            }
        }
        else
        {
            config = new TConfig();
        }

        config.Initialize(filePath);
        if (rewrite)
            config.SaveToFile();
        return config;
    }

    private void SaveToFile()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        var json = JsonSerializer.Serialize((TConfig)this, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }

    private void StartWatcher()
    {
        if (string.IsNullOrEmpty(FilePath))
            return;

        var dir = Path.GetDirectoryName(FilePath)!;
        var file = Path.GetFileName(FilePath);
        _watcher = new FileSystemWatcher(dir, file)
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += (_, _) =>
        {
            try
            {
                Thread.Sleep(50);
                var updated = JsonSerializer.Deserialize<TConfig>(File.ReadAllText(FilePath), SerializerOptions);
                if (updated != null)
                {
                    updated.FilePath = FilePath;
                    updated.Save = Save;
                    ConfigurationUpdated?.Invoke(updated);
                }
            }
            catch
            {
                // Ignore transient file lock while Reloaded-II writes the file.
            }
        };
    }
}

/// <summary>
/// Reloaded-II configurator exposing mod settings in the launcher UI.
/// </summary>
public class Configurator : IConfiguratorV1, IConfiguratorV2, IConfiguratorV3
{
    private string _configDirectory = string.Empty;
    private string _modDirectory = string.Empty;

    public void SetModDirectory(string modDirectory) => _modDirectory = modDirectory;

    public void SetConfigDirectory(string configDirectory) => _configDirectory = configDirectory;

    public void SetContext(in ConfiguratorContext context)
    {
        if (string.IsNullOrEmpty(_configDirectory) && !string.IsNullOrEmpty(context.ModConfigPath))
            _configDirectory = Path.GetDirectoryName(context.ModConfigPath) ?? _modDirectory;
    }

    public void Migrate(string oldDirectory, string newDirectory)
    {
        var oldFile = Path.Combine(oldDirectory, "Config.json");
        var newFile = Path.Combine(newDirectory, "Config.json");
        if (File.Exists(oldFile) && !File.Exists(newFile))
        {
            Directory.CreateDirectory(newDirectory);
            File.Move(oldFile, newFile);
        }
    }

    public IConfigurable[] GetConfigurations()
    {
        var dir = !string.IsNullOrEmpty(_configDirectory) ? _configDirectory : _modDirectory;
        var path = Path.Combine(dir, "Config.json");
        return new IConfigurable[] { Configurable<Config>.Load(path) };
    }

    public bool TryRunCustomConfiguration() => false;
}
