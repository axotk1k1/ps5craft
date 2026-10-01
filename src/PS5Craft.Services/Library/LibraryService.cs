using System.Text.Json;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Library;

public sealed class LibraryService : ILibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private List<LibraryItem> _items = [];
    private readonly string _path;

    public LibraryService(ISettingsService settings)
    {
        _settings = settings;
        _path = Path.Combine(settings.AppDataDirectory, "library.json");
    }

    public event EventHandler? Changed;
    public IReadOnlyList<LibraryItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            lock (_gate) _items = [];
            return;
        }

        await using var fs = File.OpenRead(_path);
        var loaded = await JsonSerializer.DeserializeAsync<List<LibraryItem>>(fs, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _items = loaded ?? [];
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        List<LibraryItem> snapshot;
        lock (_gate)
        {
            snapshot = _items.ToList();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryItem> RegisterPackedImageAsync(
        string imagePath,
        GameInfo? game,
        OutputFormat format,
        string? sha256 = null,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(imagePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Packed image not found.", full);
        }

        var info = new FileInfo(full);
        LibraryItem item;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i =>
                string.Equals(i.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.FileSizeBytes = info.Length;
                existing.Format = format;
                existing.Sha256 = sha256 ?? existing.Sha256;
                existing.CreatedAt = DateTimeOffset.Now;
                if (game is not null)
                {
                    ApplyGame(existing, game);
                }

                item = existing;
            }
            else
            {
                item = new LibraryItem
                {
                    FilePath = full,
                    FileName = info.Name,
                    FileSizeBytes = info.Length,
                    Format = format,
                    Sha256 = sha256,
                    CreatedAt = DateTimeOffset.Now,
                    Console = "PS5"
                };
                if (game is not null)
                {
                    ApplyGame(item, game);
                }

                _items.Insert(0, item);
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public async Task UpdateAsync(LibraryItem item, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var idx = _items.FindIndex(i => i.Id == item.Id);
            if (idx >= 0)
            {
                _items[idx] = item;
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == id);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> PruneMissingAsync(CancellationToken cancellationToken = default)
    {
        List<LibraryItem> removed;
        lock (_gate)
        {
            removed = _items.Where(i => string.IsNullOrWhiteSpace(i.FilePath) || !File.Exists(i.FilePath)).ToList();
            if (removed.Count == 0)
            {
                return 0;
            }

            var ids = removed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            _items.RemoveAll(i => ids.Contains(i.Id));
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return removed.Count;
    }

    public LibraryItem? FindById(string id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    public IReadOnlyList<LibraryItem> GetIncompleteTransfers()
    {
        lock (_gate)
        {
            return _items.Where(i => !string.IsNullOrWhiteSpace(i.IncompleteDestinationPath)).ToList();
        }
    }

    private static void ApplyGame(LibraryItem item, GameInfo game)
    {
        item.Title = string.IsNullOrWhiteSpace(game.Title) ? item.Title : game.Title!;
        item.TitleId = game.TitleId;
        item.ContentId = game.ContentId;
        item.Region = game.Region;
        item.Version = game.Version;
        item.Console = string.IsNullOrWhiteSpace(game.Console) ? "PS5" : game.Console;
        if (game.CoverImageBytes is { Length: > 0 })
        {
            item.CoverImageBytes = game.CoverImageBytes;
        }
    }
}
