using NB.Core.Audio;
using NB.Core.Formats;

namespace NB.Core.World;

public sealed partial class SceneObject
{
    /// <summary>A music region (Showdown Town marker type 8 with flag 0x200, see <see cref="MusicRegion"/>): its radius
    /// (X/Z, an endless vertical cylinder) and region number 1–6, as edited and as stored.</summary>
    public bool IsMusicRegion;
    public float MusicRadius, MusicRadiusSaved;
    public int MusicRegionId, MusicRegionIdSaved;
    public bool MusicDirty => IsMusicRegion && (MusicRadius != MusicRadiusSaved || MusicRegionId != MusicRegionIdSaved);
}

public sealed partial class WorldScene
{
    /// <summary>Marks a marker object that is a music region and reads its radius and region number.</summary>
    void InitMusicRegion(SceneObject o, CaffFile markerCaff)
    {
        if (o.Marker is not { Type: MusicRegion.Type } r || o.MarkerSet == null) return;
        var d = markerCaff.PartsOf(o.MarkerSet.Symbol).FirstOrDefault(p => markerCaff.SectionOf(p).Name == ".data")?.Data;
        if (d == null || !MusicRegion.Is(r, d)) return;
        o.IsMusicRegion = true;
        o.MusicRadius = o.MusicRadiusSaved = MusicRegion.Radius(r, d);
        o.MusicRegionId = o.MusicRegionIdSaved = MusicRegion.Region(r, d);
        o.Name = $"music region #{r.Index}";
    }

    /// <summary>Writes an edited music region's radius and region number into its marker asset (World > Save).</summary>
    void WriteMusicRegion(SceneObject o)
    {
        var caff = o.MarkerSet!.Caff ?? Caff;
        var d = caff.PartsOf(o.MarkerSet.Symbol).First(p => caff.SectionOf(p).Name == ".data").Data;
        MusicRegion.Write(o.Marker!, d, o.MusicRadius, o.MusicRegionId);
        o.MusicRadiusSaved = o.MusicRadius; o.MusicRegionIdSaved = o.MusicRegionId;
    }
}

public sealed partial class SceneObject
{
    /// <summary>NB Studio's "world music" speaker: a stand-in object (no record) for the music a world or Act plays
    /// everywhere (its level scripts' op 0x19 / op 0x85 commands), placed at the player start.</summary>
    public bool IsWorldMusic;
}
