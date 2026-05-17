using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Data;

public sealed class MediaDb
{
    public readonly record struct MediaEntryRef(string Id, string Path);

    private readonly MediaDbConnectionFactory _connections;
    private readonly MediaRepository _media;
    private readonly MediaTagRepository _tags;
    private readonly PlaylistRepository _playlists;

    public MediaDb(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "media.db");
        AppTraceLogger.Log("MediaDb", $"Constructing MediaDb. DataDir='{dataDir}', DbPath='{dbPath}'.");
        _connections = new MediaDbConnectionFactory(dbPath);
        _tags = new MediaTagRepository(_connections);
        _media = new MediaRepository(_connections, _tags);
        _playlists = new PlaylistRepository(_connections);
    }

    public void Initialize()
    {
        AppTraceLogger.Log("MediaDb", "Initialize start.");
        using var connection = _connections.OpenConnection();
        MediaDbSchema.Initialize(connection);
        AppTraceLogger.Log("MediaDb", "Initialize completed.");
    }

    public void AddMedia(MediaFile media)
    {
        _media.AddMedia(media);
    }

    public void UpsertMediaBatch(IEnumerable<MediaFile> mediaItems)
    {
        _media.UpsertMediaBatch(mediaItems);
    }

    public IReadOnlyList<MediaFile> GetAllMedia()
    {
        return _media.GetAllMedia();
    }

    public MediaPageResult QueryMediaPage(MediaQuery query)
    {
        return _media.QueryMediaPage(query);
    }

    public IReadOnlyList<MediaFolderSummary> QueryMediaFolderSummaries(MediaQuery query)
    {
        return _media.QueryMediaFolderSummaries(query);
    }

    public MediaFile? GetMediaById(string id)
    {
        return _media.GetMediaById(id);
    }

    public void DeleteMedia(string id)
    {
        _media.DeleteMedia(id);
    }

    public void DeleteMedia(IEnumerable<string> ids)
    {
        _media.DeleteMedia(ids);
    }

    public void ClearAllMedia(bool includePlaylists = false)
    {
        _media.ClearAllMedia(includePlaylists);
    }

    public IReadOnlyList<MediaEntryRef> GetMediaEntriesUnderFolders(IReadOnlyList<string> folderPaths)
    {
        return _media.GetMediaEntriesUnderFolders(folderPaths);
    }

    public void AddTag(string mediaId, string tag)
    {
        _tags.AddTag(mediaId, tag);
    }

    public void RemoveTag(string mediaId, string tag)
    {
        _tags.RemoveTag(mediaId, tag);
    }

    public IReadOnlyList<string> GetTags(string mediaId)
    {
        return _tags.GetTags(mediaId);
    }

    public IReadOnlyList<string> GetAllTags()
    {
        return _tags.GetAllTags();
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        return _playlists.GetPlaylists();
    }

    public Playlist CreatePlaylist(string name)
    {
        return _playlists.CreatePlaylist(name);
    }

    public void RenamePlaylist(string playlistId, string name)
    {
        _playlists.RenamePlaylist(playlistId, name);
    }

    public void SetPlaylistColor(string playlistId, string? colorHex)
    {
        _playlists.SetPlaylistColor(playlistId, colorHex);
    }

    public void DeletePlaylist(string playlistId)
    {
        _playlists.DeletePlaylist(playlistId);
    }

    public void UpdatePlaylistOrder(IReadOnlyList<string> playlistIds)
    {
        _playlists.UpdatePlaylistOrder(playlistIds);
    }

    public void AddMediaToPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        _playlists.AddMediaToPlaylist(playlistId, mediaIds);
    }

    public void RemoveMediaFromPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        _playlists.RemoveMediaFromPlaylist(playlistId, mediaIds);
    }

    public bool AreAllMediaInPlaylist(string playlistId, IEnumerable<string> mediaIds)
    {
        return _playlists.AreAllMediaInPlaylist(playlistId, mediaIds);
    }
}
