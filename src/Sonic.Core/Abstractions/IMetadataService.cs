using Sonic.Core.Models;

namespace Sonic.Core.Abstractions;

public interface IMetadataService
{
    bool IsSupported(string path);

    /// <summary>Reads tags and audio properties. Artwork is included when <paramref name="includeArtwork"/> is true.</summary>
    TrackMetadata Read(string path, bool includeArtwork = true);

    /// <summary>
    /// Writes tags. Null values in <paramref name="metadata"/> clear the field when <paramref name="overwriteAll"/>
    /// is true; otherwise only non-null values are written and existing tags are preserved.
    /// Artwork is replaced only when <see cref="TrackMetadata.Artwork"/> is non-null
    /// (or removed when <paramref name="removeArtwork"/> is set).
    /// </summary>
    void Write(string path, TrackMetadata metadata, bool overwriteAll, bool removeArtwork = false);
}
