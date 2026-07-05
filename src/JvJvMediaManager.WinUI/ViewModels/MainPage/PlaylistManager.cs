using JvJvMediaManager.Data;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.ViewModels.MainPage;

internal sealed class PlaylistManager
{
    private readonly MediaDb _db;

    public PlaylistManager(MediaDb db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Playlist>> LoadPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var playlists = await _db.GetPlaylistsAsync(cancellationToken);
        UpdateRailDisplayTexts(playlists);
        return playlists;
    }

    public Task<Playlist> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        return _db.CreatePlaylistAsync(name ?? string.Empty, cancellationToken);
    }

    public Task RenameAsync(string playlistId, string name, CancellationToken cancellationToken = default)
    {
        return _db.RenamePlaylistAsync(playlistId, name ?? string.Empty, cancellationToken);
    }

    public Task SetColorAsync(string playlistId, string? colorHex, CancellationToken cancellationToken = default)
    {
        return _db.SetPlaylistColorAsync(playlistId, colorHex, cancellationToken);
    }

    public Task DeleteAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        return _db.DeletePlaylistAsync(playlistId, cancellationToken);
    }

    public async Task UpdateOrderAsync(IReadOnlyList<Playlist> playlists, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < playlists.Count; i++)
        {
            playlists[i].SortOrder = i;
        }

        await _db.UpdatePlaylistOrderAsync(playlists.Select(item => item.Id).ToList(), cancellationToken);
    }

    public Task AddMediaAsync(string playlistId, IEnumerable<MediaItemViewModel> items, CancellationToken cancellationToken = default)
    {
        return _db.AddMediaToPlaylistAsync(playlistId, DistinctMediaIds(items), cancellationToken);
    }

    public Task RemoveMediaAsync(string playlistId, IEnumerable<MediaItemViewModel> items, CancellationToken cancellationToken = default)
    {
        return _db.RemoveMediaFromPlaylistAsync(playlistId, DistinctMediaIds(items), cancellationToken);
    }

    public Task<bool> AreAllMediaInPlaylistAsync(string playlistId, IEnumerable<string> mediaIds, CancellationToken cancellationToken = default)
    {
        return _db.AreAllMediaInPlaylistAsync(playlistId, mediaIds, cancellationToken);
    }

    private static IReadOnlyList<string> DistinctMediaIds(IEnumerable<MediaItemViewModel> items)
    {
        return items
            .Where(item => item != null)
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void UpdateRailDisplayTexts(IEnumerable<Playlist> playlists)
    {
        var playlistList = playlists as IList<Playlist> ?? playlists.ToList();
        var initialCounts = playlistList
            .Select(playlist => Playlist.GetRailInitialKey(playlist.Name))
            .Where(initialKey => !string.IsNullOrEmpty(initialKey))
            .GroupBy(initialKey => initialKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var playlist in playlistList)
        {
            var initialKey = Playlist.GetRailInitialKey(playlist.Name);
            var shouldUseTwoTextElements = !string.IsNullOrEmpty(initialKey)
                && initialCounts.TryGetValue(initialKey, out var count)
                && count > 1;

            playlist.UpdateRailDisplayText(shouldUseTwoTextElements);
        }
    }
}
