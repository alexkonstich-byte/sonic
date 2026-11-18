using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;
using Sonic.Core.Helpers;
using Sonic.Core.Models;
using TagLib;

namespace Sonic.Core.Services;

/// <summary>Reads and writes tags with TagLibSharp.</summary>
public sealed class MetadataService : IMetadataService
{
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(ILogger<MetadataService> logger)
    {
        _logger = logger;
        // ID3v2.3 has the broadest player compatibility (Windows Explorer, car stereos…).
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public bool IsSupported(string path) => FileNameHelper.IsAudioFile(path);

    public TrackMetadata Read(string path, bool includeArtwork = true)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var meta = new TrackMetadata
        {
            Title = Nullify(tag.Title),
            Artist = Nullify(tag.JoinedPerformers),
            Album = Nullify(tag.Album),
            AlbumArtist = Nullify(tag.JoinedAlbumArtists),
            TrackNumber = tag.Track > 0 ? tag.Track : null,
            Year = tag.Year > 0 ? tag.Year : null,
            Genre = Nullify(tag.JoinedGenres),
            Comment = Nullify(tag.Comment),
            DurationSeconds = file.Properties?.Duration.TotalSeconds ?? 0,
            Bitrate = file.Properties?.AudioBitrate ?? 0,
            SampleRate = file.Properties?.AudioSampleRate ?? 0,
        };

        if (includeArtwork)
        {
            var picture = tag.Pictures?.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? tag.Pictures?.FirstOrDefault();
            if (picture?.Data is { Count: > 0 } data)
            {
                meta.Artwork = data.Data;
                meta.ArtworkMimeType = string.IsNullOrEmpty(picture.MimeType) ? "image/jpeg" : picture.MimeType;
            }
        }

        return meta;
    }

    public void Write(string path, TrackMetadata metadata, bool overwriteAll, bool removeArtwork = false)
    {
        using var file = TagLib.File.Create(path);

        // Make sure a rich tag type exists for formats that support several.
        switch (file)
        {
            case TagLib.Mpeg.AudioFile or TagLib.Riff.File or TagLib.Aiff.File:
                file.GetTag(TagTypes.Id3v2, true);
                break;
            case TagLib.Flac.File or TagLib.Ogg.File:
                file.GetTag(TagTypes.Xiph, true);
                break;
            case TagLib.Mpeg4.File:
                file.GetTag(TagTypes.Apple, true);
                break;
        }

        var tag = file.Tag;
        if (overwriteAll || metadata.Title is not null) tag.Title = Nullify(metadata.Title);
        if (overwriteAll || metadata.Artist is not null) tag.Performers = Split(metadata.Artist);
        if (overwriteAll || metadata.Album is not null) tag.Album = Nullify(metadata.Album);
        if (overwriteAll || metadata.AlbumArtist is not null) tag.AlbumArtists = Split(metadata.AlbumArtist);
        if (overwriteAll || metadata.TrackNumber is not null) tag.Track = metadata.TrackNumber ?? 0;
        if (overwriteAll || metadata.Year is not null) tag.Year = metadata.Year ?? 0;
        if (overwriteAll || metadata.Genre is not null) tag.Genres = Split(metadata.Genre);
        if (overwriteAll || metadata.Comment is not null) tag.Comment = Nullify(metadata.Comment);

        if (metadata.Artwork is { Length: > 0 } art)
        {
            tag.Pictures =
            [
                new Picture(new ByteVector(art))
                {
                    Type = PictureType.FrontCover,
                    MimeType = metadata.ArtworkMimeType ?? DetectMime(art),
                    Description = "Cover",
                },
            ];
        }
        else if (removeArtwork)
        {
            tag.Pictures = [];
        }

        file.Save();
        _logger.LogDebug("Wrote tags to {Path}", path);
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Multiple artists are stored as one display string; TagLib joins arrays with "; ".
    private static string[] Split(string? s) => string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];

    private static string DetectMime(byte[] data) =>
        data.Length > 4 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
}
