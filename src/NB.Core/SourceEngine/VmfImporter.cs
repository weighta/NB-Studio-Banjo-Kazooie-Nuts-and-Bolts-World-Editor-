using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NB.Core.Formats;
using NB.Core.Models;
using NB.Core.Project;
using NB.Core.Textures;
using NB.Core.World;

namespace NB.Core.SourceEngine;

/// <summary>Options of a Source map import (NB Studio Tools > Import Source Map, NB.Cli vmf-import).</summary>
public enum Skybox3DMode { Port, Drop, InPlace }

public sealed class VmfImportOptions
{
    /// <summary>Game units (1 unit = 1 m) per Source unit (1 Source unit = 1 inch = 0.0254 m). The default 0.04 makes the
    /// map about 1.6 times real size: a 48-unit door is 1.9 m wide and a 128-unit corridor 5 m, so the game's vehicles
    /// fit through doors and corridors.</summary>
    public float Scale = 0.04f;
    /// <summary>Added to every position after scaling (game units, Y up).</summary>
    public Vector3 Offset;
    /// <summary>Also move the map up or down so the player start is at the town's ground height (y 0.5). The town's
    /// vehicles did not move when spawned 460 units up (gm_hide's lobby, verified in Xenia), while walking worked.</summary>
    public bool SpawnAtGroundHeight = true;
    /// <summary>Target world bundle (Showdown Town 234cec: the only world with the verified template model).</summary>
    public uint World = ShowdownTown;
    /// <summary>Folder with one image (PNG/TGA/JPG/BMP) per material, by material path ("brick/brickwall031a.png") or file
    /// name ("brickwall031a.png"). Wins over the game's own textures.</summary>
    public string? MaterialFolder;
    /// <summary>A Source game folder (Garry's Mod install, or a game folder with *_dir.vpk) whose materials (VMT/VTF in
    /// VPKs and loose files) give the textures and surface flags. Read only.</summary>
    public string? GameFolder;
    public bool IncludeProps;
    /// <summary>Folder with prop models (OBJ/FBX) by model path without "models/" ("props_c17/oildrum001.obj") or file name.</summary>
    public string? PropFolder;
    /// <summary>Prop files are Y-up (Blender's default OBJ/FBX export of a Source model); false = Source Z-up.</summary>
    public bool PropYUp = true;
    /// <summary>Boxes (32 Source units) for props whose model file is missing.</summary>
    public bool PropPlaceholders;
    public bool PropCollision = true;
    public bool Light = true, Fog = true, Water = true, Spawn = true;
    /// <summary>What happens to the 3D skybox (the sealed room around sky_camera): Port = its brushes, displacements,
    /// lights and props become real terrain at full size around the map (world = (p - sky_camera) * scale, Source's own
    /// 3D-skybox mapping); Drop = removed; InPlace = imported where it is in the map (tiny, far away).</summary>
    public Skybox3DMode Skybox = Skybox3DMode.Port;
    /// <summary>Ported skybox brushes whose full-size horizontal (X/Y) bounds lie inside the playable map's footprint are
    /// skipped: they are the scaled-down replica of the map that the 3D skybox keeps under the real one.</summary>
    public bool SkipSkyboxInsideMap = true;
    /// <summary>Lightmap luxel size of ported skybox geometry, in skybox units (x the bake's luxel size): the skybox is
    /// scaled up (16x), so its luxels are 16x coarser in the world and memory stays bounded.</summary>
    public float SkyLuxelScale = 1f;
    /// <summary>Collision of ported skybox displacements uses every n-th grid row/column (2 = a quarter of the triangles).</summary>
    public int SkyCollisionStep = 2;
    /// <summary>Brushes with a tools/toolsskybox(2d) face (the sky shell around the map) keep their collision as an
    /// invisible boundary like in Source. Off (default): the whole brush is removed (no mesh, no collision, no light
    /// blocking), so you can drive out past where the sky walls were.</summary>
    public bool KeepSkyBrushes;
    /// <summary>tools/toolsplayerclip collides. Off by default: player clips are gameplay barriers (lobby pens that
    /// triggers and teleports open in Source; gm_hide's spawn pen wedged the vehicle, verified in Xenia), and the
    /// game's vehicles are not Source players. tools/toolsclip (blocks everything) always collides.</summary>
    public bool PlayerClipCollision;
    /// <summary>Switch on the exe mods world-bounds-2048 + no-escape-reset (as the Seattle conversion).</summary>
    public bool UnlimitedBounds = true;
    /// <summary>Keep Mumbo's Motors (the garage entrance) standing at its town position.</summary>
    public bool KeepGarage;
    /// <summary>Move the town's objects (characters, props, pickups, volumes: every marker but the player spawn)
    /// <see cref="StorageDepth"/> units down onto a hidden floor, keeping their layout. Off: they stay where the town was
    /// (floating over or inside the imported map).</summary>
    public bool RemoveTownObjects = true;
    public float StorageDepth = 1000f;
    /// <summary>Texture size (texels) assumed for materials without an image, for their texture coordinates.</summary>
    public int AssumedTextureSize = 256;
    public int GeneratedTextureSize = 64;
    public int MaxTextureSize = 512;
    /// <summary>Triangles per model chunk (each chunk is one model + instance, culled as a whole).</summary>
    public int ChunkTriangles = 4000;
    public LightBakeOptions Bake = new();

    public const uint ShowdownTown = 0x234cec;
}

/// <summary>A material of the map and the texture it gets.</summary>
public sealed class VmfMaterialPlan
{
    public string Material = "";
    public string Texture = "";         // texture asset stem without "aid_texture_banjox_"
    public string? Image;               // user image file, or null
    public string? Vtf;                 // game texture (content path), or null
    public int VtfMip;                  // mip level written into the game
    public int TexW, TexH;              // size used for the texture coordinates
    public int OutW, OutH;              // size of the texture written into the game
    public int Triangles, Faces;
    public uint Colour;                 // generated colour (0xRRGGBB)
    public string Source => Image != null ? "image " + Image : Vtf != null ? "game " + Vtf : $"generated #{Colour:X6}";
}

/// <summary>How one material is used: drawn, colliding, light-blocking, water.</summary>
public sealed record VmfMaterialUse(bool Draw, bool Collide, bool Water, bool Occlude, string Why);

public sealed class VmfPropPlan
{
    public string Model = "";           // e.g. models/props_c17/oildrum001.mdl
    public string? File;                // resolved model file, or null
    public int Instances, Vertices, Triangles;
    /// <summary>LOD-0 vertex count from the game's .vvd (budget estimate when props cannot be imported), -1 unknown.</summary>
    public int GameVertices = -1;
}

/// <summary>One drawn brush face (or displacement) of the plan.</summary>
public sealed class VmfFace
{
    public BrushSurface Surface = null!;
    public VmfMaterialPlan Material = null!;
    public Vector3 Centroid;            // game coordinates
    public int Triangles => Surface.Triangles.Count / 3;
    // lightmap rectangle (luxels, without the 1-luxel border) and planar mapping
    public int Page = -1, X, Y, W, H;
    public Vector3 Origin, T1, T2;      // Source coordinates: luxel (i, j) centre = Origin + T1 * i + T2 * j (planar faces)
    public List<Vector2>? Uv2;          // per surface vertex, in texels of the page
    /// <summary>Ported 3D-skybox geometry: Surface is in skybox coordinates (lit in skybox space, scaled at output).</summary>
    public bool Sky3D;
}

/// <summary>Faces of one material that become one model.</summary>
public sealed class VmfChunk
{
    public VmfMaterialPlan Material = null!;
    public List<VmfFace> Faces = new();
    public int Page = -1;
    public int Triangles => Faces.Sum(f => f.Triangles);
}

/// <summary>Everything an import would write, with counts and the memory estimate (shown before importing).</summary>
public sealed class VmfPlan
{
    public string MapName = "";
    public int Brushes, BrushEntities, Faces, Displacements, Degenerate;
    public int RenderTriangles, RenderVertices, CollisionTriangles, OccluderTriangles, WaterTriangles;
    public List<VmfMaterialPlan> Materials = new();
    public Dictionary<string, VmfMaterialUse> MaterialUses = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> SkippedClasses = new();
    public Dictionary<string, int> PointEntities = new();
    public List<VmfPropPlan> Props = new();
    public int PropInstances, PropInstancesPlaced;
    public Vector3? Spawn; public float SpawnYaw;
    public string? SpawnSource;
    public SceneBuilder.LightDef? Light;
    public string? LightSource, FogSource;
    public string? Skybox3D;
    public int SkyboxBrushes, SkyboxEntities;
    /// <summary>Sky shell brushes removed (a tools/toolsskybox face), ported skybox brushes, skybox brushes skipped as the
    /// replica under the map, and the ported terrain's render / collision triangles and luxels.</summary>
    public int SkyShellBrushes, SkyPorted, SkySkippedReplica, SkyTriangles, SkyCollisionTriangles, SkyLuxels;
    public double SkyScale;
    public DVec3 SkyCamera;
    /// <summary>Game-space box of all collision (the world box the game builds from it) and its edges.</summary>
    public Vector3 CollisionMin, CollisionMax;
    public string? ContentSource;
    public int ContentFound, ContentMissing;
    public Vector3 Min, Max;
    /// <summary>Offset actually used (options' offset plus the spawn height correction).</summary>
    public Vector3 Offset;
    public List<string> Notes = new();
    public List<string> Warnings = new();
    public List<VmfChunk> Chunks = new();
    public int LightmapPages, Luxels;
    public List<BakeLight> Lights = new();
    /// <summary>Estimated bytes the import adds to the world bundle (geometry, textures, collision, instances).</summary>
    public long AddedBytes;
    public long GeometryBytes, TextureBytes, LightmapBytes, CollisionBytes, PropBytes;
    /// <summary>The parts of geometry / lightmaps / collision that belong to the ported skybox terrain.</summary>
    public long SkyGeometryBytes, SkyLightmapBytes, SkyCollisionBytes;
    public int MaxTextureSize; public float LuxelSize, SkyLuxelScale = 1; public int SkyCollisionStep = 1;
    public bool UnlimitedBounds;
    /// <summary>Estimate for the props if they were included (from the game's .vvd vertex counts).</summary>
    public long PropBudgetBytes; public int PropBudgetVertices;
    /// <summary>World bundle size after hiding the original scenery, before the import (measured for Showdown Town).</summary>
    public long BaselineBytes;
    /// <summary>The original world bundle size: the size known to load on a real console.</summary>
    public long BudgetBytes;
    public long EstimatedBytes => BaselineBytes + AddedBytes;
    public string BudgetState => EstimatedBytes <= BudgetBytes ? "ok" : EstimatedBytes <= BudgetBytes * 11 / 10 ? "warning" : "over";

    internal readonly List<Vector3> CollisionPositions = new();
    internal readonly List<int> CollisionTris = new();
    internal readonly List<Vector3> OccluderTris = new();   // Source coordinates, 3 per triangle
    internal readonly List<bool> OccluderSky = new();       // per occluder triangle: a sky face (sun and sky rays end there lit)
    internal readonly List<Vector3> SkyOccluderTris = new(); // ported skybox geometry, skybox coordinates
    internal readonly List<bool> SkyOccluderSky = new();
    internal List<BakeLight> SkyLights = new();
    internal readonly List<Vector3> WaterTris = new();
    internal readonly List<(VmfPropPlan Prop, Matrix4x4 World)> PropPlacements = new();
    internal readonly Dictionary<VmfPropPlan, List<ImportMesh>> PropMeshes = new();
    internal readonly Dictionary<VmfPropPlan, string> PropTexture = new();
    internal VmfMap Map = null!;
    /// <summary>The game content the plan read (VPK files stay open until disposed).</summary>
    public SourceContent? Content;
    internal Vector3? ToSun; internal Vector3 SunColour, SkyColour;
    internal Func<VmfEntity, bool> Keep = _ => true;
}

/// <summary>
/// Source engine (Hammer .vmf) import: brushes become textured, lightmapped world geometry with collision in a game world,
/// through the scene pipeline of the Seattle conversion (<see cref="SceneBuilder"/>): the import writes a scene folder
/// (scene.json, one OBJ per model chunk, PNG textures and lightmaps, collision OBJ) and builds it into the workspace.
/// <list type="bullet">
/// <item>Coordinates: game = (x, z, -y) * scale + offset (Source is Z-up, the game Y-up; both right-handed).</item>
/// <item>World brushes and solid brush entities (func_detail, func_brush, func_wall, doors ...) are drawn and collide;
/// func_illusionary is drawn only, clip brushes collide only, triggers and other volumes are skipped. Tool textures are
/// never drawn: nodraw / skybox / clip / invisible collide, hint / skip / trigger / areaportal do nothing. With game
/// content, translucent, additive, alpha-tested and decal materials are not drawn either (the template is opaque).</item>
/// <item>The 3D skybox (the room around sky_camera) is dropped with everything in it.</item>
/// <item>Faces are grouped by material, split into spatial chunks; every chunk is a new model (template: Showdown Town's
/// police-station strut, as the Seattle buildings) placed at its centre.</item>
/// <item>Lights (light, light_spot, light_environment sun + sky) are baked into lightmap pages: the template's
/// ambient-occlusion texture (UV set 2) multiplies the colour texture, so each chunk's AO slot points to its page.</item>
/// <item>All collision goes into the world's terrain collision (one Havok MOPP mesh); the town's terrain, scenery and
/// grass are removed and its objects moved onto a hidden floor below.</item>
/// <item>info_player_start moves the player spawn (marker type 4 #84), env_fog_controller sets the fog, water brushes
/// become water regions.</item>
/// <item>Props (prop_static / prop_dynamic / prop_physics) are off by default: MDL files are not converted; with a folder
/// of OBJ/FBX files they are placed. Their size is estimated from the game's .vvd files.</item>
/// </list>
/// </summary>
public static class VmfImporter
{
    public const string Template = "aid_model_banjox_background_showdowntown_showdowntownreferences_mainfeatures_policestationref_supportstrut";
    const string TemplateColour = "aid_texture_banjox_shared_materials_metal_brass1_colour_0x00b19ac5";
    const string FlatNormal = "aid_texture_banjox_shared_nuttyacres_plainnormal_0x0c670e45";
    const string SpawnAsset = "aid_marker_banjox_showdowntown_main";
    const int SpawnType = 4, SpawnIndex = 84;
    public const string WhiteTexture = "vmf_white", SpecTexture = "vmf_spec", BlackTexture = "vmf_black";

    /// <summary>Measured: Showdown Town with every scenery instance hidden, the terrain emptied and the hidden models
    /// stripped (an import of a small test map, minus its own size): 149.3 MB.</summary>
    public const long TownBaselineBytes = 156_500_000;
    /// <summary>Strongest fog an imported map gets (game light-setup fog max opacity; the town uses 0.33..0.62).</summary>
    public const float MaxFog = 0.5f;
    /// <summary>Per-unit costs used by the estimate (bytes).</summary>
    public const int BytesPerVertex = 28, BytesPerTriangle = 6, ModelOverhead = 24 * 1024, InstanceBytes = 512, CollisionBytesPerTriangle = 48;

    // ------------------------------------------------------------------ classification

    static readonly HashSet<string> SolidClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "worldspawn", "func_detail", "func_brush", "func_wall", "func_wall_toggle", "func_door", "func_door_rotating",
        "func_movelinear", "func_rotating", "func_breakable", "func_breakable_surf", "func_physbox", "func_physbox_multiplayer",
        "func_button", "func_rot_button", "func_lod", "func_tracktrain", "func_train", "func_tanktrain", "func_reflective_glass",
        "func_monitor", "func_conveyor", "func_guntarget", "func_plat", "func_platrot", "func_pendulum", "func_brush_noshadow",
        "func_static", "func_lookdoor", "func_movelinear_noshadow", "func_wall_noshadow",
    };
    static readonly HashSet<string> DrawOnlyClasses = new(StringComparer.OrdinalIgnoreCase) { "func_illusionary", "func_fish_pool" };
    static readonly HashSet<string> CollideOnlyClasses = new(StringComparer.OrdinalIgnoreCase) { "func_clip_vphysics", "func_vehicleclip", "func_playerclip" };
    static readonly HashSet<string> WaterClasses = new(StringComparer.OrdinalIgnoreCase) { "func_water_analog", "func_water" };
    /// <summary>Brushes that block light in vrad (world geometry); brush entities do not cast lightmap shadows.</summary>
    static readonly HashSet<string> ShadowClasses = new(StringComparer.OrdinalIgnoreCase) { "worldspawn", "func_detail" };
    public static readonly string[] PropClasses = { "prop_static", "prop_dynamic", "prop_dynamic_override", "prop_physics", "prop_physics_override", "prop_physics_multiplayer", "prop_detail", "prop_ragdoll", "prop_door_rotating" };
    static readonly string[] SpawnClasses = { "info_player_start", "info_player_teamspawn", "info_player_terrorist", "info_player_counterterrorist", "info_player_deathmatch", "info_player_combine", "info_player_rebel", "info_survivor_position", "info_player_axis", "info_player_allies" };

    /// <summary>What a brush owned by this class contributes: (draw, collide, water); null = skipped.</summary>
    public static (bool Draw, bool Collide, bool Water)? ClassUse(string cls)
    {
        if (SolidClasses.Contains(cls)) return (true, true, false);
        if (DrawOnlyClasses.Contains(cls)) return (true, false, false);
        if (CollideOnlyClasses.Contains(cls)) return (false, true, false);
        if (WaterClasses.Contains(cls)) return (false, false, true);
        return null;
    }

    /// <summary>What a side with this material contributes, from its name (tools textures, water) and, when the game's
    /// content is available, its VMT (translucent / additive / alpha-tested / decal / sky / water shaders).</summary>
    public static VmfMaterialUse MaterialUse(string mat, VmtInfo? vmt = null)
    {
        var m = mat.ToUpperInvariant();
        if (m.StartsWith("TOOLS/"))
        {
            var t = m[6..];
            if (t is "TOOLSBLACK" or "TOOLSBLACK_NOPORTAL") return new(true, true, false, true, "tools black");
            if (t.StartsWith("TOOLSNODRAW") || t == "TOOLSBLACKNODRAW") return new(false, true, false, true, "nodraw");
            if (t.StartsWith("TOOLSSKYBOX") || t.StartsWith("TOOLSSKYFOG")) return new(false, true, false, false, "sky");
            if (t.StartsWith("TOOLSCLIP") || t.StartsWith("TOOLSPLAYERCLIP") || t.StartsWith("TOOLSINVISIBLE") && !t.Contains("LADDER") ||
                t.StartsWith("TOOLSGRENADECLIP") || t.StartsWith("TOOLSBLOCKBULLETS") || t.StartsWith("TOOLSBLOCK_LOS"))
                return new(false, t.StartsWith("TOOLSCLIP") || t.StartsWith("TOOLSPLAYERCLIP") || t.StartsWith("TOOLSINVISIBLE"), false, false, "clip/invisible");
            return new(false, false, false, false, "tool");   // trigger, hint, skip, areaportal, occluder, fog, origin, npcclip ...
        }
        if (vmt != null)
        {
            if (vmt.IsWater) return new(false, false, true, false, "water shader");
            if (vmt.IsSky) return new(false, true, false, false, "sky shader");
            if (vmt.NoDraw) return new(false, true, false, true, "nodraw (vmt)");
            if (vmt.Decal) return new(false, true, false, false, "decal");
            if (vmt.Translucent || vmt.Additive) return new(false, true, false, false, vmt.Additive ? "additive" : "translucent");
            if (vmt.AlphaTest) return new(false, true, false, false, "alpha-tested");
            if (!vmt.Opaque) return new(false, true, false, false, vmt.Shader);
        }
        else if (m.Contains("WATER") && !m.Contains("WATERFALL") && !m.Contains("WATERTOWER") && !m.Contains("WATERTANK")) return new(false, false, true, false, "water (name)");
        else if (m.StartsWith("DECALS/") || m.StartsWith("SPRITES/")) return new(false, true, false, false, "decal/sprite (name)");
        return new(true, true, false, true, "");
    }

    // ------------------------------------------------------------------ plan

    /// <summary>Converts the map into game-space faces, chunks and lightmap layout, and estimates the memory it needs.
    /// <paramref name="world"/> (the target world bundle, optional) gives exact template sizes.</summary>
    public static VmfPlan Plan(VmfMap map, VmfImportOptions o, CaffFile? world = null, IProgress<(string, double)>? progress = null, SourceContent? content = null)
    {
        var plan = new VmfPlan { MapName = Path.GetFileNameWithoutExtension(map.Path), Map = map, Offset = o.Offset };
        float s = o.Scale;
        Vector3 G(DVec3 p) => new Vector3((float)(p.X * s), (float)(p.Z * s), (float)(-p.Y * s)) + plan.Offset;

        // game content
        content ??= OpenContent(o, map.Path, plan);
        plan.Content = content;

        // 3D skybox
        (DVec3 Min, DVec3 Max)? sky = o.Skybox != Skybox3DMode.InPlace ? Skybox3D(map, plan) : null;
        bool port = sky != null && o.Skybox == Skybox3DMode.Port;
        bool InSky(DVec3 p) => sky is { } b && p.X >= b.Min.X && p.Y >= b.Min.Y && p.Z >= b.Min.Z && p.X <= b.Max.X && p.Y <= b.Max.Y && p.Z <= b.Max.Z;
        // entities whose position does not matter (light_environment often sits in the skybox room) are always kept
        bool Global(VmfEntity e) => GlobalClasses.Contains(e.ClassName);
        plan.Keep = e => Global(e) || e.Origin is not DVec3 org || !InSky(org);
        double skyK = plan.SkyScale; var cam = plan.SkyCamera;
        DVec3 SkyToWorld(DVec3 p) => (p - cam) * skyK;
        Vector3 GS(DVec3 p) => G(SkyToWorld(p));
        if (o.SpawnAtGroundHeight && SpawnClasses.SelectMany(c => map.Entities.Where(e => e.ClassName.Equals(c, StringComparison.OrdinalIgnoreCase)))
                .Where(e => e.Origin != null && plan.Keep(e)).OrderByDescending(e => (int.TryParse(e.Get("spawnflags"), out int f) ? f : 0) & 1).FirstOrDefault() is { } sp0)
        {
            float dy = 0.5f - (float)(sp0.Origin!.Value.Z * s) - o.Offset.Y;
            if (MathF.Abs(dy) > 0.01f) { plan.Offset = o.Offset + new Vector3(0, dy, 0); plan.Notes.Add($"map moved {dy:F1} units vertically so the player start is at y 0.5"); }
        }

        var mats = new Dictionary<string, VmfMaterialPlan>(StringComparer.OrdinalIgnoreCase);
        VmfMaterialUse Use(string material)
        {
            if (plan.MaterialUses.TryGetValue(material, out var u)) return u;
            VmtInfo? vmt = null;
            if (content != null && !material.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase))
            {
                vmt = VmtInfo.Load(content, material);
                if (vmt != null) plan.ContentFound++; else plan.ContentMissing++;
            }
            u = MaterialUse(material, vmt);
            plan.MaterialUses[material] = u;
            if (u.Draw) mats[material] = ResolveMaterial(material, o, content, vmt);
            return u;
        }

        // the map first (its bounds decide which skybox brushes are the replica under it), then the skybox
        bool SolidInSky(VmfSolid x) => sky != null && x.Sides.All(sd => InSky(sd.P0) && InSky(sd.P1) && InSky(sd.P2));
        var solids = map.AllSolids.Select(x => (Solid: x, Sky: SolidInSky(x))).OrderBy(x => x.Sky).ToList();
        var faces = new List<VmfFace>();
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        var mainMin = new DVec3(double.MaxValue, double.MaxValue, double.MaxValue); var mainMax = new DVec3(double.MinValue, double.MinValue, double.MinValue);
        int done = 0;
        foreach (var (solid, inSky) in solids)
        {
            if (++done % 500 == 0) progress?.Report(($"brushes {done}/{solids.Count}", 0.5 * done / solids.Count));
            var use = ClassUse(solid.Owner);
            if (use == null) { plan.SkippedClasses[solid.Owner] = plan.SkippedClasses.GetValueOrDefault(solid.Owner) + 1; continue; }
            // sky shell: a brush with a skybox face is removed entirely (the 3D skybox room's walls always)
            if (solid.Sides.Any(x => IsSkyMaterial(x.Material)) && (!o.KeepSkyBrushes || inSky)) { plan.SkyShellBrushes++; continue; }
            if (inSky && !port) { plan.SkyboxBrushes++; continue; }
            var surfaces = BrushMesher.Mesh(solid, out int bad);
            plan.Degenerate += bad;
            if (inSky)
            {
                // full-size bounds of the skybox brush; within the map's horizontal footprint = the replica of the
                // playable area (heights are not compared: replicas are often a little thicker or lower than the map)
                var smin = new DVec3(double.MaxValue, double.MaxValue, double.MaxValue); var smax = new DVec3(double.MinValue, double.MinValue, double.MinValue);
                foreach (var q in surfaces.SelectMany(x => x.Positions)) { var w = SkyToWorld(q); smin = Min(smin, w); smax = Max(smax, w); }
                const double Eps = 8;
                if (o.SkipSkyboxInsideMap && surfaces.Count > 0 && mainMin.X < mainMax.X &&
                    smin.X >= mainMin.X - Eps && smin.Y >= mainMin.Y - Eps && smax.X <= mainMax.X + Eps && smax.Y <= mainMax.Y + Eps)
                { plan.SkySkippedReplica++; continue; }
                plan.SkyPorted++;
            }
            else foreach (var q in surfaces.SelectMany(x => x.Positions)) { mainMin = Min(mainMin, q); mainMax = Max(mainMax, q); }
            plan.Brushes++;
            Func<DVec3, Vector3> GX = inSky ? GS : G;
            // a brush with a water material anywhere is a water volume in Source (not solid, not drawn): its top face
            // becomes a water region
            bool waterBrush = use.Value.Water || solid.Sides.Any(x => x.Disp == null && Use(x.Material).Water);
            bool shadows = ShadowClasses.Contains(solid.Owner);
            foreach (var surf in surfaces)
            {
                var mu = Use(surf.Material);
                bool draw = use.Value.Draw && mu.Draw, collide = use.Value.Collide && mu.Collide, water = false, occlude = shadows && mu.Occlude;
                bool skyFace = shadows && mu.Why is "sky" or "sky shader";
                if (skyFace) occlude = true;
                if (!o.PlayerClipCollision && surf.Material.StartsWith("TOOLS/TOOLSPLAYERCLIP", StringComparison.OrdinalIgnoreCase)) collide = false;
                if (surf.Displacement)
                {
                    plan.Displacements++;
                    draw = use.Value.Draw && (mu.Draw || mu.Water); collide = use.Value.Collide; occlude = shadows;
                    if (draw && !mats.ContainsKey(surf.Material)) mats[surf.Material] = ResolveMaterial(surf.Material, o, content, null);
                }
                else if (waterBrush)
                {
                    draw = collide = occlude = false;
                    water = o.Water && (mu.Water || use.Value.Water);
                }
                if (!draw && !collide && !water && !occlude) continue;
                var pos = surf.Positions.Select(GX).ToList();
                if (water)
                {
                    // the top of a water brush becomes a water region (horizontal triangles); its other sides are skipped
                    if (!surf.Displacement && surf.Side.Normal.Z > 0.99)
                    {
                        float y = pos.Average(p => p.Y);
                        for (int k = 0; k + 2 < surf.Triangles.Count; k += 3)
                            for (int j = 0; j < 3; j++) { var p = pos[surf.Triangles[k + j]]; plan.WaterTris.Add(new Vector3(p.X, y, p.Z)); }
                        plan.WaterTriangles += surf.Triangles.Count / 3;
                    }
                    continue;
                }
                if (occlude)
                {
                    var ot = inSky ? plan.SkyOccluderTris : plan.OccluderTris; var os = inSky ? plan.SkyOccluderSky : plan.OccluderSky;
                    for (int t3 = 0; t3 + 2 < surf.Triangles.Count; t3 += 3)
                    {
                        for (int j = 0; j < 3; j++) { var p = surf.Positions[surf.Triangles[t3 + j]]; ot.Add(new Vector3((float)p.X, (float)p.Y, (float)p.Z)); }
                        os.Add(skyFace);
                    }
                    plan.OccluderTriangles += surf.Triangles.Count / 3;
                }
                if (collide)
                {
                    // far skybox terrain: displacements collide with a coarser grid
                    var (cp, ct) = inSky && surf.Displacement && o.SkyCollisionStep > 1 ? DecimateDisplacement(surf, o.SkyCollisionStep) : (surf.Positions, surf.Triangles);
                    int b0 = plan.CollisionPositions.Count;
                    plan.CollisionPositions.AddRange(ReferenceEquals(cp, surf.Positions) ? pos : cp.Select(GX));
                    foreach (var t in ct) plan.CollisionTris.Add(b0 + t);
                    plan.CollisionTriangles += ct.Count / 3;
                    if (inSky) plan.SkyCollisionTriangles += ct.Count / 3;
                }
                if (!draw) continue;
                plan.Faces++;
                foreach (var p in pos) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                var mp = mats[surf.Material];
                mp.Faces++; mp.Triangles += surf.Triangles.Count / 3;
                if (inSky) plan.SkyTriangles += surf.Triangles.Count / 3;
                faces.Add(new VmfFace { Surface = surf, Material = mp, Centroid = pos.Aggregate(Vector3.Zero, (a, b) => a + b) / pos.Count, Sky3D = inSky });
            }
        }
        plan.BrushEntities = map.Entities.Count(e => e.Solids.Count > 0 && ClassUse(e.ClassName) != null);
        foreach (var e in map.Entities.Where(e => e.Solids.Count == 0)) plan.PointEntities[e.ClassName] = plan.PointEntities.GetValueOrDefault(e.ClassName) + 1;
        plan.Materials = mats.Values.Where(m => m.Faces > 0).OrderByDescending(m => m.Triangles).ToList();
        AssignTextureNames(plan.Materials);
        plan.RenderTriangles = plan.Materials.Sum(m => m.Triangles);
        plan.RenderVertices = faces.Sum(f => f.Surface.Positions.Count);
        progress?.Report(("chunks", 0.55));

        // chunks: per material, spatial halving on face centroids
        foreach (var g in faces.GroupBy(f => f.Material))
            foreach (var part in SplitFaces(g.ToList(), Math.Max(50, o.ChunkTriangles)))
                plan.Chunks.Add(new VmfChunk { Material = g.Key, Faces = part });

        // lights + lightmap layout
        if (o.Light) plan.Light = LightFrom(map, o, plan);
        if (o.Bake.Enabled)
        {
            plan.Lights = LightBaker.LightsFrom(map, o.Bake, plan.Keep, plan.Notes);
            if (port) plan.SkyLights = LightBaker.LightsFrom(map, o.Bake, e => e.Origin is DVec3 org && InSky(org) && !Global(e), new List<string>());
            LayoutLightmaps(plan, o.Bake, o.SkyLuxelScale);
        }
        progress?.Report(("entities", 0.7));

        // spawn
        var spawn = SpawnClasses.SelectMany(c => map.Entities.Where(e => e.ClassName.Equals(c, StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.Origin != null && plan.Keep(e))
            .OrderByDescending(e => (int.TryParse(e.Get("spawnflags"), out int f) ? f : 0) & 1).FirstOrDefault();
        if (spawn != null)
        {
            plan.Spawn = G(spawn.Origin!.Value) + new Vector3(0, 0.1f, 0);
            plan.SpawnYaw = (float)spawn.Angles.Y + 90f;   // Source yaw 0 = +x; marker yaw turns +z (verified in game: see MERGE.md)
            plan.SpawnSource = $"{spawn.ClassName} #{spawn.Id} at {spawn.Get("origin")} yaw {spawn.Angles.Y:G4}";
        }
        else plan.Warnings.Add("no info_player_start: the player spawns at the town's spawn point");
        if (plan.Spawn is Vector3 sp && plan.CollisionTriangles > 0 && !HasFloorBelow(plan, sp)) plan.Warnings.Add($"no collision below the player start ({sp.X:F1}, {sp.Y:F1}, {sp.Z:F1}): the player would fall");

        // props
        bool PropInSky(VmfEntity e) => port && e.Origin is DVec3 org && InSky(org);
        var props = map.Entities.Where(e => PropClasses.Contains(e.ClassName, StringComparer.OrdinalIgnoreCase) && e.Get("model").Length > 0 && (plan.Keep(e) || PropInSky(e))).ToList();
        plan.PropInstances = props.Count;
        var byModel = new Dictionary<string, VmfPropPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in props)
        {
            var mdl = e.Get("model").Replace('\\', '/');
            if (!byModel.TryGetValue(mdl, out var pp)) byModel[mdl] = pp = new VmfPropPlan { Model = mdl, File = ResolveProp(mdl, o.PropFolder) };
            pp.Instances++;
        }
        plan.Props = byModel.Values.OrderByDescending(p => p.Instances).ToList();
        if (content != null) PropBudget(plan, content);
        if (o.IncludeProps) PrepareProps(plan, props, byModel, o, e => PropInSky(e) ? (GS(e.Origin ?? cam), skyK) : (G(e.Origin ?? DVec3.Zero), 1.0));
        else if (props.Count > 0) plan.Notes.Add($"{props.Count} prop(s) of {byModel.Count} model(s) not imported (props are off)");

        if (plan.Faces > 0) { plan.Min = mn; plan.Max = mx; }
        if (plan.CollisionPositions.Count > 0)
        {
            plan.CollisionMin = plan.CollisionPositions.Aggregate(Vector3.Min); plan.CollisionMax = plan.CollisionPositions.Aggregate(Vector3.Max);
        }
        if (port)
        {
            plan.Skybox3D = plan.Skybox3D?.Replace(" dropped", "") + $": ported at x{skyK:G3} as terrain";
            // the skybox camera's fog covers the far terrain in Source; the game has one fog, so its end reaches the farther of the two
            var skyCam = map.Entities.First(e => e.ClassName.Equals("sky_camera", StringComparison.OrdinalIgnoreCase) && e.Origin != null);
            if (o.Fog && skyCam.Get("fogenable", "0") != "0" && plan.Light?.FogEnd is float fe &&
                double.TryParse(skyCam.Get("fogend"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sfe) && sfe * o.Scale > fe)
            {
                plan.Light.FogEnd = (float)(sfe * o.Scale);
                plan.Notes.Add($"fog end {fe:G4} -> {plan.Light.FogEnd:G4} units (sky_camera fogend {sfe:G6}) so the far terrain shows");
            }
        }
        progress?.Report(("estimate", 0.9));
        Estimate(plan, o, world);
        if (plan.Degenerate > 0) plan.Notes.Add($"{plan.Degenerate} brush side(s) without area skipped (invalid or fully clipped planes)");
        if (plan.SkippedClasses.Count > 0) plan.Notes.Add("brush entities skipped: " + string.Join(", ", plan.SkippedClasses.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
        var hidden = plan.MaterialUses.Where(kv => !kv.Value.Draw && kv.Value.Why.Length > 0 && !kv.Key.StartsWith("TOOLS/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (hidden.Count > 0) plan.Notes.Add($"not drawn ({hidden.Count} materials): " + string.Join(", ", hidden.Take(12).Select(kv => $"{kv.Key} ({kv.Value.Why})")) + (hidden.Count > 12 ? " ..." : ""));
        float ext = MathF.Max(mx.X - mn.X, mx.Z - mn.Z);
        if (plan.Faces > 0 && MathF.Max(MathF.Max(MathF.Abs(mn.X), MathF.Abs(mx.X)), MathF.Max(MathF.Abs(mn.Z), MathF.Abs(mx.Z))) > 4000)
            plan.Warnings.Add("the map reaches beyond +/-4000 game units: far geometry may be past the game's camera range; lower the scale");
        if (plan.SkyShellBrushes > 0) plan.Notes.Add($"{plan.SkyShellBrushes} sky brush(es) (tools/toolsskybox) {(o.KeepSkyBrushes ? "of the 3D skybox room " : "")}removed: no mesh, no collision");
        if (plan.Faces > 0 && ext < 20) plan.Warnings.Add($"the map is only {ext:F1} units wide at this scale: raise the scale");
        if (plan.RenderTriangles == 0) plan.Warnings.Add("nothing to draw (no visible brush faces)");
        return plan;
    }

    /// <summary>Opens the game content: the option, else a Garry's Mod install the VMF sits in (e.g. ...\GarrysMod\bin\x.vmf).</summary>
    static SourceContent? OpenContent(VmfImportOptions o, string vmfPath, VmfPlan plan)
    {
        string? dir = o.GameFolder;
        if (dir == null && vmfPath.Length > 0)
        {
            var d = Path.GetDirectoryName(Path.GetFullPath(vmfPath));
            for (int i = 0; i < 4 && d != null; i++, d = Path.GetDirectoryName(d))
                if (Directory.Exists(Path.Combine(d, "garrysmod")) && Directory.Exists(Path.Combine(d, "sourceengine"))) { dir = d; break; }
        }
        if (dir == null || !Directory.Exists(dir)) return null;
        try
        {
            var c = SourceContent.ForGameFolder(dir);
            plan.ContentSource = $"{dir}: {c.Folders.Count} folder(s), {c.Archives.Count()} VPK(s)";
            return c;
        }
        catch (Exception e) { plan.Warnings.Add($"game content {dir}: {e.Message}"); return null; }
    }

    /// <summary>The 3D skybox: the room around sky_camera, found by casting rays along the six axes to the nearest
    /// tools/toolsskybox faces (its walls). Every brush and entity inside (and the walls) is dropped.</summary>
    static (DVec3, DVec3)? Skybox3D(VmfMap map, VmfPlan plan)
    {
        var cam = map.Entities.FirstOrDefault(e => e.ClassName.Equals("sky_camera", StringComparison.OrdinalIgnoreCase) && e.Origin != null);
        if (cam == null) return null;
        var c = cam.Origin!.Value;
        var dirs = new[] { new DVec3(1, 0, 0), new DVec3(-1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, -1, 0), new DVec3(0, 0, 1), new DVec3(0, 0, -1) };
        var dist = Enumerable.Repeat(double.MaxValue, 6).ToArray();
        // only sky faces count: the 3D skybox room is walled with tools/toolsskybox, its contents are not
        foreach (var solid in map.World.Solids)
            foreach (var s in BrushMesher.Mesh(solid, out _).Where(x => x.Material.StartsWith("TOOLS/TOOLSSKYBOX", StringComparison.OrdinalIgnoreCase)))
                for (int k = 0; k + 2 < s.Triangles.Count; k += 3)
                {
                    var a = s.Positions[s.Triangles[k]]; var b = s.Positions[s.Triangles[k + 1]]; var cc = s.Positions[s.Triangles[k + 2]];
                    for (int i = 0; i < 6; i++)
                    {
                        double t = RayTri(c, dirs[i], a, b, cc);
                        if (t > 0 && t < dist[i]) dist[i] = t;
                    }
                }
        if (dist.Any(x => x == double.MaxValue)) { plan.Notes.Add("sky_camera is not inside a closed room: 3D skybox not removed"); return null; }
        plan.SkyCamera = c;
        plan.SkyScale = double.TryParse(cam.Get("scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sc) && sc > 0 ? sc : 16;
        // the room plus its walls (up to 64 units thick)
        var min = new DVec3(c.X - dist[1] - 64, c.Y - dist[3] - 64, c.Z - dist[5] - 64);
        var max = new DVec3(c.X + dist[0] + 64, c.Y + dist[2] + 64, c.Z + dist[4] + 64);
        plan.Skybox3D = $"sky_camera #{cam.Id} at {cam.Get("origin")}: room {min} .. {max} dropped";
        plan.SkyboxEntities = map.Entities.Count(e => !GlobalClasses.Contains(e.ClassName) && e.Origin is DVec3 p && p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z);
        return (min, max);
    }

    static DVec3 Min(DVec3 a, DVec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    static DVec3 Max(DVec3 a, DVec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>tools/toolsskybox and tools/toolsskybox2d (any case).</summary>
    public static bool IsSkyMaterial(string m) => m.StartsWith("TOOLS/TOOLSSKYBOX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Entities whose position is irrelevant (never dropped with the 3D skybox).</summary>
    static readonly HashSet<string> GlobalClasses = new(StringComparer.OrdinalIgnoreCase) { "light_environment", "env_fog_controller", "env_sun", "shadow_control", "sky_camera", "worldspawn", "env_tonemap_controller", "water_lod_control" };

    /// <summary>Collision of a displacement on every <paramref name="step"/>-th grid row and column (the last one always),
    /// facing the side's front.</summary>
    static (List<DVec3>, List<int>) DecimateDisplacement(BrushSurface s, int step)
    {
        int n = (int)Math.Round(Math.Sqrt(s.Positions.Count));
        var idx = new List<int>();
        for (int i = 0; i < n; i += step) idx.Add(i);
        if (idx[^1] != n - 1) idx.Add(n - 1);
        int m = idx.Count;
        var pos = new List<DVec3>(m * m);
        foreach (int r in idx) foreach (int c in idx) pos.Add(s.Positions[r * n + c]);
        var tris = new List<int>();
        var front = s.Side.Normal;
        void Tri(int a, int b, int c)
        {
            var fn = DVec3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            if (DVec3.Dot(fn, front) < 0) (b, c) = (c, b);
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        for (int r = 0; r + 1 < m; r++)
            for (int c = 0; c + 1 < m; c++)
            {
                int a = r * m + c, b = (r + 1) * m + c, cc = (r + 1) * m + c + 1, d = r * m + c + 1;
                Tri(a, b, cc); Tri(a, cc, d);
            }
        return (pos, tris);
    }

    static double RayTri(DVec3 o, DVec3 d, DVec3 a, DVec3 b, DVec3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var p = DVec3.Cross(d, e2);
        double det = DVec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-12) return -1;
        var s = o - a;
        double u = DVec3.Dot(s, p) / det;
        if (u < 0 || u > 1) return -1;
        var q = DVec3.Cross(s, e1);
        double v = DVec3.Dot(d, q) / det;
        if (v < 0 || u + v > 1) return -1;
        return DVec3.Dot(e2, q) / det;
    }

    static List<List<VmfFace>> SplitFaces(List<VmfFace> faces, int maxTris)
    {
        var res = new List<List<VmfFace>>();
        var work = new Stack<List<VmfFace>>();
        work.Push(faces);
        while (work.Count > 0)
        {
            var f = work.Pop();
            int tris = f.Sum(x => x.Triangles), verts = f.Sum(x => x.Surface.Positions.Count);
            if ((tris <= maxTris && verts <= 50000) || f.Count < 2) { res.Add(f); continue; }
            var mn = f.Select(x => x.Centroid).Aggregate(Vector3.Min); var mx = f.Select(x => x.Centroid).Aggregate(Vector3.Max);
            var e = mx - mn;
            var sorted = e.X >= e.Y && e.X >= e.Z ? f.OrderBy(x => x.Centroid.X) : e.Y >= e.Z ? f.OrderBy(x => x.Centroid.Y) : f.OrderBy(x => x.Centroid.Z);
            var l = sorted.ToList();
            int half = tris / 2, acc = 0, cut = 0;
            while (cut < l.Count - 1 && acc + l[cut].Triangles <= half) acc += l[cut++].Triangles;
            cut = Math.Clamp(cut, 1, l.Count - 1);
            work.Push(l.GetRange(cut, l.Count - cut)); work.Push(l.GetRange(0, cut));
        }
        return res;
    }

    // ------------------------------------------------------------------ lightmaps

    /// <summary>Luxel rectangles per face (planar: a grid on the face plane along its texture axes; displacements: their
    /// own grid) and shelf packing per chunk into pages (a chunk never spans pages; a chunk that does not fit an empty
    /// page is split).</summary>
    static void LayoutLightmaps(VmfPlan plan, LightBakeOptions b, float skyLuxelScale = 1f)
    {
        int P = b.PageSize;
        foreach (var f in plan.Chunks.SelectMany(c => c.Faces))
        {
            var s = f.Surface;
            float lux = b.LuxelSize * (f.Sky3D ? skyLuxelScale : 1f);   // skybox units: x scale coarser in the world
            if (s.Displacement)
            {
                int n = (int)Math.Round(Math.Sqrt(s.Positions.Count));
                double lr = 0, lc = 0;
                for (int i = 0; i + 1 < n; i++) { lr += (s.Positions[(i + 1) * n] - s.Positions[i * n]).Length; lc += (s.Positions[i + 1] - s.Positions[i]).Length; }
                f.W = Math.Clamp((int)Math.Ceiling(lc / lux) + 1, 2, b.MaxFaceLuxels); f.H = Math.Clamp((int)Math.Ceiling(lr / lux) + 1, 2, b.MaxFaceLuxels);
                continue;
            }
            // planar: luxel axes along the texture's s axis projected into the plane (like vrad), orthonormalised
            var n3 = s.Side.Normal;
            var nn = new Vector3((float)n3.X, (float)n3.Y, (float)n3.Z);
            var u = new Vector3((float)s.Side.UAxis.X, (float)s.Side.UAxis.Y, (float)s.Side.UAxis.Z);
            u -= nn * Vector3.Dot(u, nn);
            if (u.LengthSquared() < 1e-6f) u = Vector3.Cross(nn, MathF.Abs(nn.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
            u = Vector3.Normalize(u);
            var v = Vector3.Cross(nn, u);
            float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
            foreach (var p in s.Positions)
            {
                var q = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                float a = Vector3.Dot(q, u), c = Vector3.Dot(q, v);
                umin = MathF.Min(umin, a); umax = MathF.Max(umax, a); vmin = MathF.Min(vmin, c); vmax = MathF.Max(vmax, c);
            }
            while (true)
            {
                f.W = Math.Max(2, (int)Math.Ceiling((umax - umin) / lux) + 1); f.H = Math.Max(2, (int)Math.Ceiling((vmax - vmin) / lux) + 1);
                if (f.W <= b.MaxFaceLuxels && f.H <= b.MaxFaceLuxels) break;
                lux *= 1.5f;
            }
            // luxel (i, j) centre; the grid covers the face's bounds exactly (first and last luxel on the edges)
            float su = (umax - umin) / (f.W - 1), sv = (vmax - vmin) / (f.H - 1);
            f.T1 = u * su; f.T2 = v * sv;
            f.Origin = nn * (float)s.Side.Distance + u * umin + v * vmin;
        }
        // pack: chunks largest first, each onto the current page or a new one
        var pages = new List<(int X, int Y, int Shelf)>();
        var queue = new Queue<VmfChunk>(plan.Chunks.OrderByDescending(c => c.Faces.Sum(f => (f.W + 2) * (f.H + 2))));
        var result = new List<VmfChunk>();
        while (queue.Count > 0)
        {
            var ch = queue.Dequeue();
            var rects = ch.Faces.OrderByDescending(f => f.H + 2).ToList();
            int pi = Math.Max(0, pages.Count - 1);
            var cur = pages.Count > 0 ? pages[pi] : (0, 0, 0);
            if (pages.Count > 0 && TryPack(rects, P, ref cur, out var pos)) { Place(pi, cur, pos); continue; }
            cur = (0, 0, 0);
            if (TryPack(rects, P, ref cur, out pos)) { pages.Add((0, 0, 0)); Place(pages.Count - 1, cur, pos); continue; }
            if (ch.Faces.Count < 2) throw new InvalidDataException("a face needs more luxels than a lightmap page holds");
            int half = ch.Faces.Count / 2;
            queue.Enqueue(new VmfChunk { Material = ch.Material, Faces = ch.Faces.Take(half).ToList() });
            queue.Enqueue(new VmfChunk { Material = ch.Material, Faces = ch.Faces.Skip(half).ToList() });

            void Place(int page, (int, int, int) cursor, List<(int X, int Y)> at)
            {
                for (int k = 0; k < rects.Count; k++) { rects[k].Page = page; rects[k].X = at[k].X + 1; rects[k].Y = at[k].Y + 1; }
                pages[page] = cursor;
                ch.Page = page;
                result.Add(ch);
            }
        }
        plan.Chunks = result;
        plan.LightmapPages = pages.Count;
        plan.Luxels = plan.Chunks.SelectMany(c => c.Faces).Sum(f => f.W * f.H);
        plan.SkyLuxels = plan.Chunks.SelectMany(c => c.Faces).Where(f => f.Sky3D).Sum(f => f.W * f.H);
    }

    /// <summary>Shelf packing of (W+2) x (H+2) rectangles continuing from a page cursor.</summary>
    static bool TryPack(List<VmfFace> rects, int P, ref (int X, int Y, int Shelf) cur, out List<(int X, int Y)> pos)
    {
        pos = new();
        var c = cur;
        foreach (var r in rects)
        {
            int w = r.W + 2, h = r.H + 2;
            if (w > P || h > P) return false;
            if (c.X + w > P) { c.Y += c.Shelf; c.X = 0; c.Shelf = 0; }
            if (c.Y + h > P) return false;
            pos.Add((c.X, c.Y));
            c.X += w; c.Shelf = Math.Max(c.Shelf, h);
        }
        cur = c;
        return true;
    }

    /// <summary>Bakes every face's luxels and returns the pages as RGBA images; sets each face's UV2.</summary>
    public static List<byte[]> BakeLightmaps(VmfPlan plan, VmfImportOptions o, IProgress<(string, double)>? progress = null)
    {
        var b = o.Bake;
        int P = b.PageSize;
        var bvh = new TriangleBvh(plan.OccluderTris, plan.OccluderSky);
        // game sun direction / colour for the division (the light setups get GameSun x the sun colour)
        Vector3 gameSun = Vector3.UnitY, gameSunCol = Vector3.One;
        if (plan.Light?.Elevation is float el && plan.Light.Azimuth is float az)
        {
            float e = el * MathF.PI / 180, a = az * MathF.PI / 180;
            gameSun = new Vector3(-MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), -MathF.Cos(e) * MathF.Cos(a));
        }
        if (plan.Light?.Sun is string sh) { uint rgb = Convert.ToUInt32(sh, 16); gameSunCol = new Vector3((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f); }
        var baker = new LightBaker(b, bvh, plan.Lights, plan.ToSun, plan.SunColour, plan.SkyColour, gameSun, gameSunCol);
        // ported skybox geometry is lit in skybox space, by the skybox's own lights and blockers (as vrad lit it)
        var skyBaker = plan.SkyOccluderTris.Count > 0 || plan.SkyLights.Count > 0
            ? new LightBaker(b, new TriangleBvh(plan.SkyOccluderTris, plan.SkyOccluderSky), plan.SkyLights, plan.ToSun, plan.SunColour, plan.SkyColour, gameSun, gameSunCol) : baker;
        var lin = new Vector3[plan.LightmapPages][];
        var nrm = new Vector3[plan.LightmapPages][];
        var used = new bool[plan.LightmapPages][];
        for (int i = 0; i < plan.LightmapPages; i++) { lin[i] = new Vector3[P * P]; nrm[i] = new Vector3[P * P]; used[i] = new bool[P * P]; }
        var all = plan.Chunks.SelectMany(c => c.Faces).ToList();
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
        {
            BakeFace(f, f.Sky3D ? skyBaker : baker, lin[f.Page], nrm[f.Page], used[f.Page], P);
            int d = Interlocked.Increment(ref done);
            if (d % 200 == 0) progress?.Report(($"lightmaps {d}/{all.Count} faces", (double)d / all.Count));
        });
        if (b.Bounce > 0)
            for (int pg = 0; pg < plan.LightmapPages; pg++) AddBounce(lin[pg], used[pg], P, b.Bounce);
        var pages = new List<byte[]>();
        for (int pg = 0; pg < plan.LightmapPages; pg++)
        {
            var px = new byte[P * P * 4];
            for (int i = 0; i < P * P; i++)
            {
                var v = used[pg][i] ? baker.Encode(Vector3.Max(lin[pg][i], new Vector3(b.MinLight)), nrm[pg][i]) : new Vector3(0.5f);
                px[4 * i] = (byte)(v.X * 255 + 0.5f); px[4 * i + 1] = (byte)(v.Y * 255 + 0.5f); px[4 * i + 2] = (byte)(v.Z * 255 + 0.5f); px[4 * i + 3] = 255;
            }
            pages.Add(px);
        }
        plan.Notes.Add($"light bake: {all.Count} faces, {plan.Luxels:N0} luxels on {plan.LightmapPages} page(s) of {P}, {baker.Rays + (skyBaker != baker ? skyBaker.Rays : 0):N0} rays, {sw.Elapsed.TotalSeconds:F1}s");
        return pages;
    }

    static void BakeFace(VmfFace f, LightBaker baker, Vector3[] lin, Vector3[] nrm, bool[] used, int P)
    {
        var s = f.Surface;
        int W = f.W, H = f.H;
        var gameN = new Vector3[W * H];
        var light = new Vector3[W * H];
        if (s.Displacement)
        {
            int n = (int)Math.Round(Math.Sqrt(s.Positions.Count));
            Vector3 P3(int r, int c) { var p = s.Positions[r * n + c]; return new((float)p.X, (float)p.Y, (float)p.Z); }
            Vector3 N3(int r, int c) { var p = s.Normals[r * n + c]; return new((float)p.X, (float)p.Y, (float)p.Z); }
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    float fr = (float)j / Math.Max(1, H - 1) * (n - 1), fc = (float)i / Math.Max(1, W - 1) * (n - 1);
                    int r0 = Math.Min((int)fr, n - 2), c0 = Math.Min((int)fc, n - 2);
                    float tr = fr - r0, tc = fc - c0;
                    // the point on the displacement's own triangles (BrushMesher alternates the cell diagonals): a bilinear
                    // point can lie under the surface and shadow itself (checkerboard lightmaps on hills)
                    Vector3 a = P3(r0, c0), pb = P3(r0 + 1, c0), pc = P3(r0 + 1, c0 + 1), pd = P3(r0, c0 + 1), p;
                    if (((r0 + c0) & 1) == 0) p = tr >= tc ? a + (pb - a) * (tr - tc) + (pc - a) * tc : a + (pc - a) * tr + (pd - a) * (tc - tr);
                    else p = tr + tc <= 1 ? a + (pb - a) * tr + (pd - a) * tc : pc + (pb - pc) * (1 - tc) + (pd - pc) * (1 - tr);
                    var nv = Vector3.Normalize(Vector3.Lerp(Vector3.Lerp(N3(r0, c0), N3(r0, c0 + 1), tc), Vector3.Lerp(N3(r0 + 1, c0), N3(r0 + 1, c0 + 1), tc), tr));
                    light[j * W + i] = baker.Direct(p, nv, (f.Page * P + f.Y + j) * P + f.X + i);
                    gameN[j * W + i] = new Vector3(nv.X, nv.Z, -nv.Y);
                }
            f.Uv2 = new List<Vector2>();
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    f.Uv2.Add(new Vector2(f.X + 0.5f + (float)c / (n - 1) * (W - 1), f.Y + 0.5f + (float)r / (n - 1) * (H - 1)));
        }
        else
        {
            var nn = new Vector3((float)s.Side.Normal.X, (float)s.Side.Normal.Y, (float)s.Side.Normal.Z);
            var poly = s.Positions.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToList();
            var gn = new Vector3(nn.X, nn.Z, -nn.Y);
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    var p = ClampToPolygon(f.Origin + f.T1 * i + f.T2 * j, poly, nn);
                    light[j * W + i] = baker.Direct(p, nn, (f.Page * P + f.Y + j) * P + f.X + i);
                    gameN[j * W + i] = gn;
                }
            float l1 = f.T1.LengthSquared(), l2 = f.T2.LengthSquared();
            f.Uv2 = poly.Select(p =>
            {
                var d = p - f.Origin;
                float a = l1 > 0 ? Vector3.Dot(d, f.T1) / l1 : 0, c = l2 > 0 ? Vector3.Dot(d, f.T2) / l2 : 0;
                return new Vector2(f.X + 0.5f + a, f.Y + 0.5f + c);
            }).ToList();
        }
        // with a 1-luxel border (edge luxels repeated) so bilinear filtering at the face edges stays inside
        for (int j = -1; j <= H; j++)
            for (int i = -1; i <= W; i++)
            {
                int si = Math.Clamp(i, 0, W - 1), sj = Math.Clamp(j, 0, H - 1);
                int x = f.X + i, y = f.Y + j;
                if (x < 0 || y < 0 || x >= P || y >= P) continue;
                lin[y * P + x] = light[sj * W + si]; nrm[y * P + x] = gameN[sj * W + si]; used[y * P + x] = true;
            }
    }

    /// <summary>Moves a point of the face plane into the convex polygon (nearest edge point, pulled 0.5 units in).</summary>
    static Vector3 ClampToPolygon(Vector3 p, List<Vector3> poly, Vector3 n)
    {
        bool inside = true;
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
            if (Vector3.Dot(Vector3.Cross(b - a, p - a), n) < -1e-3f) { inside = false; break; }
        }
        var cen = poly.Aggregate(Vector3.Zero, (x, y) => x + y) / poly.Count;
        Vector3 best = p;
        if (!inside)
        {
            float bd = float.MaxValue;
            for (int k = 0; k < poly.Count; k++)
            {
                var a = poly[k]; var b = poly[(k + 1) % poly.Count];
                var ab = b - a; float t = Math.Clamp(Vector3.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0, 1);
                var q = a + ab * t; float d = Vector3.DistanceSquared(p, q);
                if (d < bd) { bd = d; best = q; }
            }
        }
        // keep samples off the edges (they would see through the neighbouring wall)
        var toC = cen - best;
        float len = toC.Length();
        return len > 1e-3f ? best + toC / len * MathF.Min(1f, len * 0.5f) : best;
    }

    /// <summary>Crude bounce light: a wide box blur of the page's direct light (faces of a chunk are packed next to each
    /// other), scaled and added.</summary>
    static void AddBounce(Vector3[] lin, bool[] used, int P, float k)
    {
        const int R = 12;
        var tmp = new Vector3[P * P]; var cnt = new int[P * P];
        for (int y = 0; y < P; y++)
        {
            Vector3 acc = Vector3.Zero; int c = 0;
            for (int x = -R; x < P + R; x++)
            {
                int xa = x + R, xr = x - R - 1;
                if (xa < P && xa >= 0 && used[y * P + xa]) { acc += lin[y * P + xa]; c++; }
                if (xr >= 0 && xr < P && used[y * P + xr]) { acc -= lin[y * P + xr]; c--; }
                if (x >= 0 && x < P) { tmp[y * P + x] = acc; cnt[y * P + x] = c; }
            }
        }
        var outv = new Vector3[P * P];
        for (int x = 0; x < P; x++)
        {
            Vector3 acc = Vector3.Zero; int c = 0;
            for (int y = -R; y < P + R; y++)
            {
                int ya = y + R, yr = y - R - 1;
                if (ya < P && ya >= 0) { acc += tmp[ya * P + x]; c += cnt[ya * P + x]; }
                if (yr >= 0 && yr < P) { acc -= tmp[yr * P + x]; c -= cnt[yr * P + x]; }
                if (y >= 0 && y < P && used[y * P + x] && c > 0) outv[y * P + x] = acc / c;
            }
        }
        for (int i = 0; i < P * P; i++) if (used[i]) lin[i] += outv[i] * k;
    }

    // ------------------------------------------------------------------ light / spawn helpers

    static bool HasFloorBelow(VmfPlan plan, Vector3 p)
    {
        var P = plan.CollisionPositions; var T = plan.CollisionTris;
        for (int k = 0; k + 2 < T.Count; k += 3)
        {
            Vector3 a = P[T[k]], b = P[T[k + 1]], c = P[T[k + 2]];
            if (p.Y < MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - 0.01f) continue;
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(d) < 1e-9f) continue;
            float w1 = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / d, w2 = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / d;
            if (w1 < -1e-4f || w2 < -1e-4f || w1 + w2 > 1 + 1e-4f) continue;
            float h = w1 * a.Y + w2 * b.Y + (1 - w1 - w2) * c.Y;
            if (h <= p.Y + 0.5f) return true;
        }
        return false;
    }

    static SceneBuilder.LightDef? LightFrom(VmfMap map, VmfImportOptions o, VmfPlan plan)
    {
        var l = new SceneBuilder.LightDef();
        bool any = false, bake = o.Bake.Enabled;
        var env = map.Entities.FirstOrDefault(e => e.ClassName.Equals("light_environment", StringComparison.OrdinalIgnoreCase));
        if (env != null)
        {
            var (r, g, b, br) = VmfMap.Colour(env.Get("_light", "255 255 255 200"));
            double max = Math.Max(1, Math.Max(r, Math.Max(g, b)));
            l.Sun = Hex(r / max * 255, g / max * 255, b / max * 255);
            var (ar, ag, ab, abr) = VmfMap.Colour(env.Get("_ambient", "255 255 255 20"), 20);
            // vrad: light travels along (cos p cos y, cos p sin y, sin p), pitch from "pitch" when set, else angles
            double yaw = env.Angles.Y;
            double pitch = double.TryParse(env.Get("pitch"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pk) && pk != 0 ? pk : env.Angles.X;
            double py = pitch * Math.PI / 180, yy = yaw * Math.PI / 180;
            var travel = new Vector3((float)(Math.Cos(py) * Math.Cos(yy)), (float)(Math.Cos(py) * Math.Sin(yy)), (float)Math.Sin(py));
            plan.ToSun = -travel;
            plan.SunColour = new Vector3(LightBaker.Lin(r), LightBaker.Lin(g), LightBaker.Lin(b)) * (float)(br / 255.0);
            plan.SkyColour = new Vector3(LightBaker.Lin(ar), LightBaker.Lin(ag), LightBaker.Lin(ab)) * (float)(abr / 255.0);
            // direction to the sun in game axes: -(travel) mapped (x, z, -y)
            var toSun = new Vector3(-travel.X, -travel.Z, travel.Y);
            float elev = MathF.Asin(Math.Clamp(toSun.Y, -1, 1)) * 180 / MathF.PI;
            float az = MathF.Atan2(-toSun.X, -toSun.Z) * 180 / MathF.PI;
            l.Elevation = Math.Clamp(elev, 5f, 89f); l.Azimuth = az;
            if (bake)
            {
                // baked: the lightmaps carry the map's light; the game adds a flat ambient and a weak sun (LightBaker.Encode)
                l.Intensity = o.Bake.GameSun;
                l.Ambient = Hex(o.Bake.GameAmbient * 255, o.Bake.GameAmbient * 255, o.Bake.GameAmbient * 255);
            }
            else
            {
                l.Intensity = (float)Math.Clamp(1.25 * Math.Sqrt(Math.Max(0, br) / 250.0) * max / 255.0, 0.2, 3.0);
                double level = Math.Clamp(0.25 + abr / 300.0, 0.25, 0.7);
                l.Ambient = Hex(ar * level, ag * level, ab * level);
            }
            plan.LightSource = $"light_environment #{env.Id}: _light {env.Get("_light")}, _ambient {env.Get("_ambient")}, pitch {pitch:G4}, yaw {yaw:G4} -> game sun {l.Sun} x{l.Intensity:G3}, ambient {l.Ambient}, elevation {l.Elevation:G3} deg, azimuth {az:G4} deg";
            if (elev < 5) plan.Warnings.Add($"light_environment points up or along the horizon (elevation {elev:F0} deg): clamped to 5 deg");
            any = true;
        }
        else
        {
            plan.Notes.Add("no light_environment: no sun or sky light");
            if (bake) { l.Intensity = o.Bake.GameSun; l.Ambient = Hex(o.Bake.GameAmbient * 255, o.Bake.GameAmbient * 255, o.Bake.GameAmbient * 255); any = true; }
        }
        var fog = map.Entities.FirstOrDefault(e => e.ClassName.Equals("env_fog_controller", StringComparison.OrdinalIgnoreCase));
        if (fog != null && o.Fog && fog.Get("fogenable", "0") == "0")
            plan.FogSource = $"env_fog_controller #{fog.Id}: off (the world keeps its own fog)";
        else if (fog != null && o.Fog)
        {
            bool on = true;
            var (r, g, b, _) = VmfMap.Colour(fog.Get("fogcolor", "255 255 255"));
            double F(string k, double d) => double.TryParse(fog.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : d;
            l.FogColour = Hex(r, g, b);
            l.FogStart = (float)(F("fogstart", 500) * o.Scale);
            l.FogEnd = (float)Math.Max(F("fogend", 2000) * o.Scale, (l.FogStart ?? 0) + 1);
            // the game's fog closes in much faster than Source's (a Source max density of 1 is a white-out in game:
            // verified in Xenia on snowy_dream), so the strength is capped and the start pushed out a little
            l.FogMax = on ? (float)Math.Clamp(F("fogmaxdensity", 1), 0, MaxFog) : 0f;
            l.FogStart = Math.Max(l.FogStart ?? 0, (l.FogEnd ?? 0) * 0.04f);
            plan.FogSource = $"env_fog_controller #{fog.Id}: {(on ? "on" : "off (fog cleared)")}, colour {l.FogColour}, {l.FogStart:G4}..{l.FogEnd:G4} units, max {l.FogMax:G3}";
            any = true;
        }
        return any ? l : null;
    }

    static string Hex(double r, double g, double b) =>
        $"{(int)Math.Clamp(Math.Round(r), 0, 255):X2}{(int)Math.Clamp(Math.Round(g), 0, 255):X2}{(int)Math.Clamp(Math.Round(b), 0, 255):X2}";

    // ------------------------------------------------------------------ materials

    static readonly string[] ImageExts = { ".png", ".tga", ".jpg", ".jpeg", ".bmp" };

    /// <summary>Finds an image for a material: &lt;folder&gt;/&lt;material path&gt;.ext, &lt;folder&gt;/materials/&lt;path&gt;.ext,
    /// &lt;folder&gt;/&lt;file name&gt;.ext (case-insensitive on Windows).</summary>
    public static string? FindImage(string folder, string material)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var rel = material.Replace('/', Path.DirectorySeparatorChar).ToLowerInvariant();
        var name = Path.GetFileName(rel);
        foreach (var stem in new[] { rel, Path.Combine("materials", rel), name, rel.Replace(Path.DirectorySeparatorChar, '_') })
            foreach (var ext in ImageExts)
            {
                var f = Path.Combine(folder, stem + ext);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    static VmfMaterialPlan ResolveMaterial(string material, VmfImportOptions o, SourceContent? content, VmtInfo? vmt)
    {
        var mp = new VmfMaterialPlan { Material = material, TexW = o.AssumedTextureSize, TexH = o.AssumedTextureSize, Colour = ColourFor(material) };
        var img = o.MaterialFolder != null ? FindImage(o.MaterialFolder, material) : null;
        if (img != null)
        {
            try
            {
                var (w, h) = ImageSize(img);
                mp.Image = img; mp.TexW = w; mp.TexH = h;
                (mp.OutW, mp.OutH) = OutSize(w, h, o.MaxTextureSize);
                return mp;
            }
            catch (Exception) { mp.Image = null; }
        }
        if (content != null)
        {
            vmt ??= VmtInfo.Load(content, material);
            var tex = SourceContent.Norm(vmt?.BaseTexture ?? material);
            if (tex.StartsWith("materials/")) tex = tex[10..];
            if (tex.EndsWith(".vtf")) tex = tex[..^4];
            var path = "materials/" + tex + ".vtf";
            var bytes = content.Read(path);
            if (bytes != null)
            {
                try
                {
                    var info = Vtf.ReadInfo(bytes);
                    int mip = 0;
                    while (mip + 1 < info.Mips && (info.Width >> mip) > o.MaxTextureSize) mip++;
                    mp.Vtf = path; mp.VtfMip = mip; mp.TexW = info.Width; mp.TexH = info.Height;
                    (mp.OutW, mp.OutH) = OutSize(Math.Max(4, info.Width >> mip), Math.Max(4, info.Height >> mip), o.MaxTextureSize);
                    return mp;
                }
                catch (Exception) { mp.Vtf = null; }
            }
        }
        mp.OutW = mp.OutH = Pow2(o.GeneratedTextureSize, 1024);
        return mp;
    }

    static (int, int) ImageSize(string path)
    {
        if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            using var f = File.OpenRead(path);
            var h = new byte[18]; f.ReadExactly(h);
            return (h[12] | h[13] << 8, h[14] | h[15] << 8);
        }
        using var img = System.Drawing.Image.FromFile(path);
        return (img.Width, img.Height);
    }

    static int Pow2(int v, int max)
    {
        int p = 4;
        while (p * 2 <= v && p * 2 <= max) p *= 2;
        if (p * 2 <= max && v - p > p * 2 - v) p *= 2;
        return Math.Clamp(p, 4, Math.Max(4, max));
    }

    /// <summary>Game texture size for an image: powers of two, at least 64 and at most 4:1.</summary>
    static (int W, int H) OutSize(int w, int h, int max)
    {
        int W = Math.Max(64, Pow2(w, max)), H = Math.Max(64, Pow2(h, max));
        while (W > 4 * H) H *= 2;
        while (H > 4 * W) W *= 2;
        return (Math.Min(W, Math.Max(64, max)), Math.Min(H, Math.Max(64, max)));
    }

    static void AssignTextureNames(List<VmfMaterialPlan> mats)
    {
        var used = new HashSet<string>();
        foreach (var m in mats)
        {
            var sb = new StringBuilder();
            foreach (char c in m.Material.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
            var b = sb.ToString().Trim('_');
            if (b.Length == 0) b = "material";
            if (b.Length > 48) b = b[^48..].TrimStart('_');
            // a texture asset name must not end in "top" or "mip": the game's texture entries are <stem>top / <stem>mip,
            // and a stem like "vmf_wood_offdesktop" hung Showdown Town's loading (verified in Xenia, see MERGE.md)
            if (b.EndsWith("top") || b.EndsWith("mip")) b += "_m";
            string n = "vmf_" + b; int k = 2;
            while (!used.Add(n)) n = $"vmf_{b}_{k++}";
            m.Texture = n;
        }
    }

    /// <summary>A plausible colour for a Source material name (dev textures, common surface words), else a stable
    /// pastel from the name's hash.</summary>
    public static uint ColourFor(string material)
    {
        var m = material.ToLowerInvariant();
        (string Key, uint Rgb)[] table =
        {
            ("dev_measuregeneric01b", 0x8C8C8C), ("dev_measuregeneric", 0xD08A3C), ("dev_measurewall", 0xC8C8C8), ("dev_measurecrate", 0xC87832),
            ("graygrid", 0x9A9A9A), ("dev_blendmeasure", 0x7FA36B), ("reflectivity_10", 0x1A1A1A), ("reflectivity_20", 0x333333),
            ("reflectivity_30", 0x4D4D4D), ("reflectivity_40", 0x666666), ("reflectivity_50", 0x808080), ("reflectivity_60", 0x999999),
            ("reflectivity_70", 0xB3B3B3), ("reflectivity_80", 0xCCCCCC), ("reflectivity_90", 0xE6E6E6), ("toolsblack", 0x050505),
            ("orange", 0xE07A2E), ("red", 0xB83A32), ("blue", 0x3A62B8), ("green", 0x4E9A45), ("yellow", 0xD8C040), ("white", 0xE8E8E8), ("black", 0x202020),
            ("grass", 0x5E8C3A), ("moss", 0x56723A), ("leaf", 0x4E7A32), ("foliage", 0x4E7A32), ("dirt", 0x8A6A48), ("mud", 0x6A5038),
            ("sand", 0xD2BE8C), ("gravel", 0x8E8A82), ("rock", 0x7E7870), ("cliff", 0x76706A), ("stone", 0x8E8880), ("cobble", 0x7A7268),
            ("brick", 0x9A4E3A), ("concrete", 0xA0A09A), ("cement", 0xA4A49E), ("plaster", 0xD6D0C2), ("stucco", 0xD8CDB4),
            ("wood", 0x8E6440), ("plank", 0x9A7048), ("crate", 0xA07A48), ("metal", 0x7E8890), ("steel", 0x8A9298), ("rust", 0x8A5434),
            ("glass", 0x9FC4D0), ("window", 0x8FB4C8), ("tile", 0xC8C0B0), ("carpet", 0x8A3A3A), ("asphalt", 0x4A4A4C), ("road", 0x505054),
            ("roof", 0x6E4038), ("shingle", 0x5A4A44), ("snow", 0xEEF2F6), ("ice", 0xBCD8E8), ("lava", 0xE0501E), ("water", 0x3A6A9A),
            ("wall", 0xC6BEB0), ("floor", 0xA89C88), ("ceiling", 0xD8D8D0), ("trim", 0x9A9488), ("door", 0x7A5A3E), ("pipe", 0x6E7A80),
        };
        foreach (var (k, rgb) in table) if (m.Contains(k)) return rgb;
        uint h = 2166136261;
        foreach (char c in m) h = (h ^ c) * 16777619;
        double hue = h % 360 / 360.0, sat = 0.35, val = 0.75;
        return HsvToRgb(hue, sat, val);
    }

    static uint HsvToRgb(double h, double s, double v)
    {
        double r, g, b;
        int i = (int)(h * 6) % 6; double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
    }

    /// <summary>Generated material texture: the colour with light noise and darker tile edges (so the texture scale
    /// shows, like Source's dev textures); dev "measure" materials get a grid every quarter tile.</summary>
    public static byte[] GenerateTexture(uint rgb, int size, bool grid)
    {
        var px = new byte[size * size * 4];
        int R = (int)(rgb >> 16 & 255), Gc = (int)(rgb >> 8 & 255), B = (int)(rgb & 255);
        int edge = Math.Max(1, size / 64), q = size / 4;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint h = (uint)(x * 73856093 ^ y * 19349663); h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                double f = 1 + ((h & 255) / 255.0 - 0.5) * 0.08;
                bool border = x < edge || y < edge || x >= size - edge || y >= size - edge;
                bool line = grid && (x % q < edge || y % q < edge);
                if (border) f *= 0.72; else if (line) f *= 0.86;
                int o = (y * size + x) * 4;
                px[o] = (byte)Math.Clamp(R * f, 0, 255); px[o + 1] = (byte)Math.Clamp(Gc * f, 0, 255); px[o + 2] = (byte)Math.Clamp(B * f, 0, 255); px[o + 3] = 255;
            }
        return px;
    }

    // ------------------------------------------------------------------ props

    public static string? ResolveProp(string mdl, string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var rel = mdl.Replace('\\', '/');
        if (rel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) rel = rel[7..];
        if (rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) rel = rel[..^4];
        rel = rel.Replace('/', Path.DirectorySeparatorChar);
        foreach (var stem in new[] { rel, Path.Combine("models", rel), Path.GetFileName(rel) })
            foreach (var ext in new[] { ".obj", ".fbx" })
            {
                var f = Path.Combine(folder, stem + ext);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    /// <summary>Budget of the props if they were imported: LOD-0 vertex counts from the game's .vvd files (triangles
    /// assumed 1.6 per vertex), one model and one 512 texture per prop model, one instance per prop.</summary>
    static void PropBudget(VmfPlan plan, SourceContent content)
    {
        long bytes = 0; int verts = 0, known = 0;
        foreach (var p in plan.Props)
        {
            var path = SourceContent.Norm(p.Model);
            if (path.EndsWith(".mdl")) path = path[..^4];
            var vvd = content.Read(path + ".vvd");
            if (vvd == null || vvd.Length < 64 || vvd[0] != 'I' || vvd[1] != 'D' || vvd[2] != 'S' || vvd[3] != 'V') continue;
            p.GameVertices = BitConverter.ToInt32(vvd, 16);   // numLODVertexes[0]
            known++;
            verts += p.GameVertices;
            bytes += p.GameVertices * (long)BytesPerVertex + (long)(p.GameVertices * 1.6) * BytesPerTriangle + ModelOverhead + p.Instances * (long)InstanceBytes;
            bytes += DxtBytes(512, 512);
        }
        plan.PropBudgetBytes = bytes; plan.PropBudgetVertices = verts;
        if (plan.Props.Count > 0)
            plan.Notes.Add($"props if included: {known} of {plan.Props.Count} model(s) found in the game content, {verts:N0} vertices, ~{bytes / 1048576.0:F1} MB " +
                           "(MDL meshes are not converted: export them to OBJ/FBX into a prop folder)");
    }

    /// <summary>Source AngleMatrix (pitch, yaw, roll in degrees): columns = forward, left, up.</summary>
    static double[,] AngleMatrix(DVec3 ang)
    {
        double p = ang.X * Math.PI / 180, y = ang.Y * Math.PI / 180, r = ang.Z * Math.PI / 180;
        double sp = Math.Sin(p), cp = Math.Cos(p), sy = Math.Sin(y), cy = Math.Cos(y), sr = Math.Sin(r), cr = Math.Cos(r);
        return new double[,]
        {
            { cp * cy, sr * sp * cy - cr * sy, cr * sp * cy + sr * sy },
            { cp * sy, sr * sp * sy + cr * cy, cr * sp * sy - sr * cy },
            { -sp, sr * cp, cr * cp },
        };
    }

    static void PrepareProps(VmfPlan plan, List<VmfEntity> props, Dictionary<string, VmfPropPlan> byModel, VmfImportOptions o, Func<VmfEntity, (Vector3 Pos, double Scale)> place)
    {
        float s = o.Scale;
        double[,] A = { { 1, 0, 0 }, { 0, 0, 1 }, { 0, -1, 0 } };
        double[,] Ai = { { 1, 0, 0 }, { 0, 0, -1 }, { 0, 1, 0 } };
        foreach (var pp in byModel.Values)
        {
            List<ImportMesh>? meshes = null;
            if (pp.File != null)
            {
                try { meshes = ObjReader.ReadAny(pp.File); }
                catch (Exception e) { plan.Warnings.Add($"prop {pp.Model}: {e.Message}"); }
            }
            else if (o.PropPlaceholders) meshes = new() { Box(16) };
            if (meshes == null || meshes.Count == 0) continue;
            var merged = new ImportMesh { Name = "prop", Normals = new(), UVs = new() };
            foreach (var m in meshes)
            {
                int b = merged.Positions.Count;
                var nrm = m.Normals ?? ObjReader.ComputeNormals(m);
                for (int k = 0; k < m.Positions.Count; k++)
                {
                    var p = m.Positions[k]; var n = nrm[k];
                    if (!o.PropYUp || m.Name == "placeholder") { p = new(p.X, p.Z, -p.Y); n = new(n.X, n.Z, -n.Y); }
                    merged.Positions.Add(p * s); merged.Normals.Add(n);
                    merged.UVs.Add(m.UVs != null ? m.UVs[k] : Vector2.Zero);
                }
                merged.Triangles.AddRange(m.Triangles.Select(t => t + b));
            }
            if (merged.Positions.Count > 50000) { plan.Warnings.Add($"prop {pp.Model}: {merged.Positions.Count} vertices (max 50,000) skipped"); continue; }
            pp.Vertices = merged.Positions.Count; pp.Triangles = merged.Triangles.Count / 3;
            plan.PropMeshes[pp] = new() { merged };
            var mat = meshes.Select(m => m.Material).FirstOrDefault(m => m != null);
            string? img = mat?.TexturePath is string tp ? (Path.IsPathRooted(tp) ? tp : Path.Combine(mat.BaseDir ?? "", tp)) : null;
            if (img != null && !File.Exists(img)) img = null;
            if (img == null && mat != null && o.MaterialFolder != null) img = FindImage(o.MaterialFolder, mat.Name);
            uint col = mat != null && mat.Color != Vector3.One
                ? (uint)(Math.Clamp(mat.Color.X, 0, 1) * 255) << 16 | (uint)(Math.Clamp(mat.Color.Y, 0, 1) * 255) << 8 | (uint)(Math.Clamp(mat.Color.Z, 0, 1) * 255)
                : ColourFor(mat?.Name ?? pp.Model);
            var mp = new VmfMaterialPlan { Material = "prop:" + pp.Model, Image = img, Colour = col, TexW = 1, TexH = 1, Triangles = pp.Triangles * pp.Instances };
            if (img != null) { var (w, h) = ImageSize(img); (mp.OutW, mp.OutH) = OutSize(w, h, o.MaxTextureSize); }
            else mp.OutW = mp.OutH = Pow2(o.GeneratedTextureSize, 1024);
            AssignTextureNames(new() { mp });
            mp.Texture = "vmfprop_" + mp.Texture[4..];
            plan.Materials.Add(mp);
            plan.PropTexture[pp] = mp.Texture;
        }
        foreach (var e in props)
        {
            var pp = byModel[e.Get("model").Replace('\\', '/')];
            if (!plan.PropMeshes.ContainsKey(pp)) continue;
            var (t, placeScale) = place(e);
            double ms = (double.TryParse(e.Get("modelscale", e.Get("uniformscale", "1")), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1) * placeScale;
            var M = AngleMatrix(e.Angles);
            var R = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            {
                double acc = 0;
                for (int k = 0; k < 3; k++) for (int l = 0; l < 3; l++) acc += A[i, k] * M[k, l] * Ai[l, j];
                R[i, j] = acc * ms;
            }
            var world = new Matrix4x4((float)R[0, 0], (float)R[1, 0], (float)R[2, 0], 0, (float)R[0, 1], (float)R[1, 1], (float)R[2, 1], 0,
                                      (float)R[0, 2], (float)R[1, 2], (float)R[2, 2], 0, t.X, t.Y, t.Z, 1);
            plan.PropPlacements.Add((pp, world));
            plan.PropInstancesPlaced++;
            bool solid = e.Get("solid", "6") != "0" && !e.ClassName.StartsWith("prop_detail", StringComparison.OrdinalIgnoreCase);
            if (o.PropCollision && solid)
            {
                var mesh = plan.PropMeshes[pp][0];
                int b0 = plan.CollisionPositions.Count;
                plan.CollisionPositions.AddRange(mesh.Positions.Select(p => Vector3.Transform(p, world)));
                plan.CollisionTris.AddRange(mesh.Triangles.Select(x => x + b0));
                plan.CollisionTriangles += mesh.Triangles.Count / 3;
            }
        }
        int missing = byModel.Values.Count(p => !plan.PropMeshes.ContainsKey(p));
        if (missing > 0) plan.Notes.Add($"{missing} prop model(s) without a model file ({byModel.Values.Where(p => !plan.PropMeshes.ContainsKey(p)).Sum(p => p.Instances)} instance(s)) skipped{(o.PropPlaceholders ? "" : " (placeholder boxes are off)")}");
    }

    static ImportMesh Box(float h)
    {
        var m = new ImportMesh { Name = "placeholder", Normals = new(), UVs = new() };
        Vector3[] n = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (var f in n)
        {
            var u = MathF.Abs(f.Z) > 0.5f ? Vector3.UnitX : Vector3.UnitZ;
            var v = Vector3.Cross(f, u);
            int b = m.Positions.Count;
            foreach (var (a, c) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var p = (f + u * a + v * c) * h + new Vector3(0, 0, h);
                m.Positions.Add(p); m.Normals.Add(f); m.UVs.Add(new Vector2((a + 1) / 2, (c + 1) / 2));
            }
            m.Triangles.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        return m;
    }

    // ------------------------------------------------------------------ estimate

    static long DxtBytes(int w, int h)
    {
        long b = 0;
        while (true) { b += Math.Max(1, w / 4) * Math.Max(1, h / 4) * 8; if (w <= 4 && h <= 4) break; w = Math.Max(4, w / 2); h = Math.Max(4, h / 2); }
        return b + 1024;
    }

    static void Estimate(VmfPlan plan, VmfImportOptions o, CaffFile? world)
    {
        int vtx = BytesPerVertex, overhead = ModelOverhead;
        if (world != null)
        {
            int ts = world.Symbols.FindIndex(x => AssetIds.DisplayName(x) == Template) + 1;
            if (ts > 0)
            {
                try
                {
                    var m = ModelAsset.Parse(world, ts, geometry: false);
                    if (m.Draws.Count > 0) vtx = m.Draws.Max(d => d.Stride);
                    overhead = world.PartsOf(ts).Where(p => world.SectionOf(p).Name != ".gpu").Sum(p => p.Data.Length) + 256;
                }
                catch (Exception) { }
            }
        }
        plan.GeometryBytes = plan.RenderVertices * (long)vtx + plan.RenderTriangles * (long)BytesPerTriangle + plan.Chunks.Count * (long)(overhead + InstanceBytes);
        var skyFaces = plan.Chunks.SelectMany(c => c.Faces).Where(f => f.Sky3D).ToList();
        plan.SkyGeometryBytes = skyFaces.Sum(f => f.Surface.Positions.Count) * (long)vtx + plan.SkyTriangles * (long)BytesPerTriangle +
                                plan.Chunks.Count(c => c.Faces.Any(f => f.Sky3D)) * (long)(overhead + InstanceBytes);
        plan.MaxTextureSize = o.MaxTextureSize; plan.LuxelSize = o.Bake.LuxelSize; plan.SkyLuxelScale = o.SkyLuxelScale; plan.SkyCollisionStep = o.SkyCollisionStep;
        plan.UnlimitedBounds = o.UnlimitedBounds;
        plan.TextureBytes = plan.Materials.Sum(m => DxtBytes(m.OutW, m.OutH)) + 3 * 4096;
        plan.LightmapBytes = plan.LightmapPages * DxtBytes(o.Bake.PageSize, o.Bake.PageSize);
        plan.CollisionBytes = plan.CollisionTriangles * (long)CollisionBytesPerTriangle;
        plan.SkyCollisionBytes = plan.SkyCollisionTriangles * (long)CollisionBytesPerTriangle;
        plan.SkyLightmapBytes = plan.Luxels > 0 ? plan.LightmapBytes * plan.SkyLuxels / plan.Luxels : 0;
        plan.PropBytes = plan.PropMeshes.Sum(kv => kv.Key.Vertices * (long)vtx + kv.Key.Triangles * (long)BytesPerTriangle + overhead) + plan.PropPlacements.Count * (long)InstanceBytes;
        plan.AddedBytes = plan.GeometryBytes + plan.TextureBytes + plan.LightmapBytes + plan.CollisionBytes + plan.PropBytes;
        plan.BudgetBytes = o.World == VmfImportOptions.ShowdownTown ? 201_141_796 : 0;
        plan.BaselineBytes = o.World == VmfImportOptions.ShowdownTown ? TownBaselineBytes : plan.BudgetBytes;
        if (plan.BudgetBytes > 0 && plan.BudgetState != "ok")
            plan.Warnings.Add($"estimated world bundle {plan.EstimatedBytes / 1048576.0:F1} MB is above the original {plan.BudgetBytes / 1048576.0:F1} MB " +
                              "(the size known to run on a real Xbox 360; a 262 MB world crashed the console before): see the budget suggestions");
    }

    // ------------------------------------------------------------------ write + build

    public sealed class Result
    {
        public VmfPlan Plan = null!;
        public SceneBuilder.Report? Report;
        public string SceneFolder = "";
    }

    /// <summary>Writes the scene folder for <paramref name="plan"/> (textures, lightmaps, model OBJs, collision, water,
    /// scene.json); bakes the lightmaps when the bake is on.</summary>
    public static string WriteScene(VmfPlan plan, VmfImportOptions o, string folder, IProgress<(string, double)>? progress = null)
    {
        Directory.CreateDirectory(folder);
        var texDir = Path.Combine(folder, "tex"); var meshDir = Path.Combine(folder, "mesh");
        foreach (var d in new[] { texDir, meshDir })
            if (Directory.Exists(d)) foreach (var f in Directory.GetFiles(d)) File.Delete(f);
        Directory.CreateDirectory(texDir); Directory.CreateDirectory(meshDir);
        var sc = new SceneBuilder.Scene
        {
            World = o.World.ToString("x6"), FromOriginal = true, InstanceTemplate = 750,
            Hide = new SceneBuilder.HideDef { All = true, Keep = o.KeepGarage ? new() { "mumbosgarage" } : new() },
            FitCullingTree = true, CompactGpu = true,
        };
        sc.Templates["vmf"] = new SceneBuilder.TemplateDef { Model = Template, Colour = TemplateColour, Ao = WhiteTexture, Spec = SpecTexture, FlatNormal = FlatNormal };
        void Solid(string name, byte v, int size = 64)
        {
            var px = new byte[size * size * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
            ImageIO.Save(Path.Combine(texDir, name + ".png"), px, size, size);
            sc.Textures.Add(new SceneBuilder.TextureDef { Name = name, Image = $"tex/{name}.png" });
        }
        Solid(WhiteTexture, 255); Solid(SpecTexture, 64); Solid(BlackTexture, 0);
        if (o.World == VmfImportOptions.ShowdownTown) sc.ReplaceTextures.Add(new SceneBuilder.TextureDef { Name = "aid_texture_banjox_grass_showdowntown_height*", Image = $"tex/{BlackTexture}.png" });
        // material textures
        int ti = 0;
        foreach (var m in plan.Materials)
        {
            progress?.Report(($"texture {m.Texture}", 0.1 * ti++ / Math.Max(1, plan.Materials.Count)));
            byte[] px; int w, h;
            try
            {
                if (m.Image != null) (px, w, h) = ImageIO.Load(m.Image);
                else if (m.Vtf != null && plan.Content?.Read(m.Vtf) is byte[] vtf) (px, w, h) = Vtf.Decode(vtf, m.VtfMip);
                else throw new FileNotFoundException();
                if (w != m.OutW || h != m.OutH) (px, w, h) = ImageIO.Resize(px, w, h, m.OutW, m.OutH);
            }
            catch (Exception)
            {
                w = h = m.OutW = m.OutH = Pow2(o.GeneratedTextureSize, 1024);
                px = GenerateTexture(m.Colour, w, m.Material.Contains("MEASURE", StringComparison.OrdinalIgnoreCase) || m.Material.Contains("GRID", StringComparison.OrdinalIgnoreCase));
            }
            ImageIO.Save(Path.Combine(texDir, m.Texture + ".png"), px, w, h, keepAlpha: false);
            sc.Textures.Add(new SceneBuilder.TextureDef { Name = m.Texture, Image = $"tex/{m.Texture}.png" });
        }
        // lightmaps
        string stem = Sanitize(plan.MapName, 12);
        bool baked = o.Bake.Enabled && plan.LightmapPages > 0;
        if (baked)
        {
            var pages = BakeLightmaps(plan, o, Sub(progress, 0.1, 0.8));
            for (int i = 0; i < pages.Count; i++)
            {
                string name = $"vmf_lm_{stem}_{i:D2}";
                ImageIO.Save(Path.Combine(texDir, name + ".png"), pages[i], o.Bake.PageSize, o.Bake.PageSize, keepAlpha: false);
                sc.Textures.Add(new SceneBuilder.TextureDef { Name = name, Image = $"tex/{name}.png" });
            }
        }
        // brush chunks: one model each, centred on its bounds, placed at the centre
        float s = o.Scale;
        Vector3 G(DVec3 p) => new Vector3((float)(p.X * s), (float)(p.Z * s), (float)(-p.Y * s)) + plan.Offset;
        Vector3 GS(DVec3 p) => G((p - plan.SkyCamera) * plan.SkyScale);
        static Vector3 GN(DVec3 n) => Vector3.Normalize(new Vector3((float)n.X, (float)n.Z, (float)-n.Y));
        int ci = 0;
        foreach (var ch in plan.Chunks)
        {
            progress?.Report(($"model {ci}", 0.8 + 0.15 * ci / Math.Max(1, plan.Chunks.Count)));
            var mp = ch.Material;
            var mesh = new ImportMesh { Name = mp.Texture, Normals = new(), UVs = new(), UVs2 = baked ? new() : null };
            foreach (var f in ch.Faces)
            {
                var surf = f.Surface;
                int bv = mesh.Positions.Count;
                // texture coordinates in repeats; shifted by whole repeats per face so the 16-bit UVs stay precise
                var uv = surf.Texels.Select(t => new Vector2((float)(t.U / mp.TexW), (float)(t.V / mp.TexH))).ToList();
                float su = MathF.Floor(uv.Min(t => t.X)), sv = MathF.Floor(uv.Min(t => t.Y));
                for (int k = 0; k < surf.Positions.Count; k++)
                {
                    mesh.Positions.Add(f.Sky3D ? GS(surf.Positions[k]) : G(surf.Positions[k]));
                    mesh.Normals!.Add(GN(surf.Normals[k]));
                    mesh.UVs!.Add(new Vector2(uv[k].X - su, uv[k].Y - sv));
                    if (baked) mesh.UVs2!.Add(f.Uv2 != null ? f.Uv2[k] / o.Bake.PageSize : new Vector2(0.5f));
                }
                foreach (var t in surf.Triangles) mesh.Triangles.Add(bv + t);
            }
            var (cmn, cmx) = mesh.Bounds();
            var centre = (cmn + cmx) / 2;
            string name = $"vmf_{stem}_{ci:D4}";
            WriteObj(Path.Combine(meshDir, name + ".obj"), mesh, centre);
            sc.Models.Add(new SceneBuilder.ModelDef
            {
                Name = name, Template = "vmf", Obj = $"mesh/{name}.obj", Texture = mp.Texture, Collision = "none", Cull = 1e6f,
                Ao = baked ? $"vmf_lm_{stem}_{ch.Page:D2}" : null,
            });
            sc.Instances.Add(new SceneBuilder.InstanceDef { Model = name, Pos = new[] { centre.X, centre.Y, centre.Z }, Collision = false });
            ci++;
        }
        // props: one model per prop file, one instance per placement (collision is in the world mesh)
        int pi = 0;
        foreach (var (pp, meshes) in plan.PropMeshes)
        {
            string name = $"vmfp_{stem}_{pi++:D3}";
            WriteObj(Path.Combine(meshDir, name + ".obj"), meshes[0], Vector3.Zero);
            sc.Models.Add(new SceneBuilder.ModelDef { Name = name, Template = "vmf", Obj = $"mesh/{name}.obj", Texture = plan.PropTexture[pp], Collision = "none", Cull = 1e6f });
            foreach (var (p2, w) in plan.PropPlacements.Where(x => x.Prop == pp))
                sc.Instances.Add(new SceneBuilder.InstanceDef { Model = name, Collision = false, Pos = new[] { w.M41, w.M42, w.M43 },
                    Matrix = new[] { w.M11, w.M12, w.M13, w.M21, w.M22, w.M23, w.M31, w.M32, w.M33, w.M41, w.M42, w.M43 } });
        }
        progress?.Report(("collision", 0.96));
        var colP = plan.CollisionPositions; var colT = plan.CollisionTris;
        if (o.World == VmfImportOptions.ShowdownTown && o.RemoveTownObjects)
        {
            colP = new List<Vector3>(colP); colT = new List<int>(colT);
            // below the whole map (a map reaching far down gets a deeper floor)
            float depth = MathF.Max(o.StorageDepth, plan.Faces > 0 ? -plan.Min.Y + 300f : 0f);
            float y = -depth - 20f; int b = colP.Count;
            colP.AddRange(new[] { new Vector3(-800, y, -800), new Vector3(800, y, -800), new Vector3(800, y, 900), new Vector3(-800, y, 900) });
            colT.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            sc.MarkerShift.Add(new SceneBuilder.MarkerShiftDef { Asset = SpawnAsset, Offset = new[] { 0f, -depth, 0f }, Except = new() { $"{SpawnType}:{SpawnIndex}" } });
        }
        if (o.World == VmfImportOptions.ShowdownTown) sc.NoGrass = true;
        WriteCollision(Path.Combine(folder, "collision.obj"), colP, colT);
        sc.Terrain = new SceneBuilder.TerrainDef { Obj = "none", Collision = "collision.obj" };
        var water = plan.WaterTris.Count > 0 ? plan.WaterTris : new List<Vector3> { new(5000, -15000, 5000), new(5001, -15000, 5000), new(5000, -15000, 5001) };
        WriteTris(Path.Combine(folder, "water.obj"), water, ccwFromAbove: true);
        sc.Water = new() { new SceneBuilder.WaterDef { Kind = plan.WaterTris.Count > 0 ? 1 : 2, Obj = "water.obj" } };
        if (o.Spawn && plan.Spawn is Vector3 sp && o.World == VmfImportOptions.ShowdownTown)
            sc.Markers.Add(new SceneBuilder.MarkerDef { Asset = SpawnAsset, Type = SpawnType, Index = SpawnIndex, Pos = new[] { sp.X, sp.Y, sp.Z }, Yaw = plan.SpawnYaw });
        if (plan.Light != null) sc.Light = plan.Light;
        if (o.UnlimitedBounds) sc.ExeMods = new() { "world-bounds-2048", "no-escape-reset" };
        var json = JsonSerializer.Serialize(sc, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var path = Path.Combine(folder, "scene.json");
        File.WriteAllText(path, json);
        File.WriteAllLines(Path.Combine(folder, "plan.txt"), Describe(plan));
        return path;
    }

    static string Sanitize(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        var r = sb.ToString();
        return r.Length == 0 ? "map" : r.Length > max ? r[..max] : r;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>OBJ with a second UV set as "vx u v" lines (one per "v", read by <see cref="ObjReader"/>; other tools
    /// ignore the unknown statement).</summary>
    static void WriteObj(string path, ImportMesh m, Vector3 origin)
    {
        var sb = new StringBuilder();
        sb.Append("# NB Studio Source map import\no chunk\n");
        foreach (var p in m.Positions) sb.Append("v ").Append((p.X - origin.X).ToString("0.#####", Inv)).Append(' ').Append((p.Y - origin.Y).ToString("0.#####", Inv)).Append(' ').Append((p.Z - origin.Z).ToString("0.#####", Inv)).Append('\n');
        foreach (var t in m.UVs!) sb.Append("vt ").Append(t.X.ToString("0.######", Inv)).Append(' ').Append((1 - t.Y).ToString("0.######", Inv)).Append('\n');   // OBJ v is up
        foreach (var n in m.Normals!) sb.Append("vn ").Append(n.X.ToString("0.####", Inv)).Append(' ').Append(n.Y.ToString("0.####", Inv)).Append(' ').Append(n.Z.ToString("0.####", Inv)).Append('\n');
        if (m.UVs2 != null) foreach (var t in m.UVs2) sb.Append("vx ").Append(t.X.ToString("0.#######", Inv)).Append(' ').Append(t.Y.ToString("0.#######", Inv)).Append('\n');
        for (int k = 0; k + 2 < m.Triangles.Count; k += 3)
        {
            sb.Append('f');
            for (int j = 0; j < 3; j++) { int i = m.Triangles[k + j] + 1; sb.Append(' ').Append(i).Append('/').Append(i).Append('/').Append(i); }
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void WriteCollision(string path, List<Vector3> P, List<int> T)
    {
        var sb = new StringBuilder("# NB Studio Source map import: collision\n");
        var map = new Dictionary<(int, int, int), int>(); var idx = new int[P.Count]; var outP = new List<Vector3>();
        for (int i = 0; i < P.Count; i++)
        {
            var k = ((int)MathF.Round(P[i].X * 1000), (int)MathF.Round(P[i].Y * 1000), (int)MathF.Round(P[i].Z * 1000));
            if (!map.TryGetValue(k, out int j)) { j = outP.Count; map[k] = j; outP.Add(P[i]); }
            idx[i] = j;
        }
        foreach (var p in outP) sb.Append("v ").Append(p.X.ToString("0.####", Inv)).Append(' ').Append(p.Y.ToString("0.####", Inv)).Append(' ').Append(p.Z.ToString("0.####", Inv)).Append('\n');
        for (int k = 0; k + 2 < T.Count; k += 3)
        {
            int a = idx[T[k]], b = idx[T[k + 1]], c = idx[T[k + 2]];
            if (a == b || b == c || a == c) continue;
            sb.Append("f ").Append(a + 1).Append(' ').Append(b + 1).Append(' ').Append(c + 1).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void WriteTris(string path, List<Vector3> tris, bool ccwFromAbove)
    {
        var sb = new StringBuilder("# NB Studio Source map import: water\n");
        for (int k = 0; k + 2 < tris.Count; k += 3)
        {
            Vector3 a = tris[k], b = tris[k + 1], c = tris[k + 2];
            if (ccwFromAbove && Vector3.Cross(b - a, c - a).Y < 0) (b, c) = (c, b);
            foreach (var p in new[] { a, b, c }) sb.Append("v ").Append(p.X.ToString("0.####", Inv)).Append(' ').Append(p.Y.ToString("0.####", Inv)).Append(' ').Append(p.Z.ToString("0.####", Inv)).Append('\n');
        }
        for (int k = 0; k + 2 < tris.Count; k += 3) sb.Append("f ").Append(k + 1).Append(' ').Append(k + 2).Append(' ').Append(k + 3).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Plans, writes the scene folder (baking the lightmaps) and builds it into the workspace (the target world
    /// is rebuilt from its original bundle). <paramref name="sceneFolder"/>: default &lt;workspace&gt;/imports/&lt;map&gt;.</summary>
    public static Result Import(Workspace ws, AssetIndex index, string vmfPath, VmfImportOptions o, string? sceneFolder = null, IProgress<(string, double)>? progress = null)
    {
        progress?.Report(("reading " + Path.GetFileName(vmfPath), 0));
        var map = VmfMap.Load(vmfPath);
        CaffFile? world = null;
        try { world = ws.LoadResident(o.World); } catch (Exception) { }
        if (world != null && !world.Symbols.Any(x => AssetIds.DisplayName(x) == Template))
            throw new InvalidDataException($"world {o.World:x6} has no {Template} (the template model of imported geometry): choose Showdown Town (234cec)");
        var plan = Plan(map, o, world, Sub(progress, 0, 0.05));
        try
        {
            if (plan.RenderTriangles == 0 && plan.CollisionTriangles == 0) throw new InvalidDataException("the map has no brush geometry to import");
            sceneFolder ??= Path.Combine(ws.Root, "imports", Sanitize(plan.MapName, 32));
            var scene = WriteScene(plan, o, sceneFolder, Sub(progress, 0.05, 0.45));
            var rep = SceneBuilder.Build(ws, index, scene, Sub(progress, 0.45, 1.0));
            return new Result { Plan = plan, Report = rep, SceneFolder = sceneFolder };
        }
        finally { plan.Content?.Dispose(); }
    }

    static IProgress<(string, double)>? Sub(IProgress<(string, double)>? p, double a, double b) =>
        p == null ? null : new SyncProgress(x => p.Report((x.Item1, a + (b - a) * x.Item2)));

    sealed class SyncProgress : IProgress<(string, double)>
    {
        readonly Action<(string, double)> _a;
        public SyncProgress(Action<(string, double)> a) { _a = a; }
        public void Report((string, double) v) => _a(v);
    }

    /// <summary>Diagnostics: the baked light at a Source point (normal up) and what blocks the sun / sky there.</summary>
    public static string ProbeLight(VmfPlan plan, VmfImportOptions o, Vector3 p)
    {
        var bvh = new TriangleBvh(plan.OccluderTris, plan.OccluderSky);
        var baker = new LightBaker(o.Bake, bvh, plan.Lights, plan.ToSun, plan.SunColour, plan.SkyColour, Vector3.UnitY, Vector3.One);
        var n = Vector3.UnitZ;
        var lin = baker.Direct(p, n, 1);
        var sb = new StringBuilder($"light at {p}: {lin} (sun {plan.SunColour}, sky {plan.SkyColour}, to sun {plan.ToSun})");
        if (plan.ToSun is Vector3 ts) sb.Append($"; sun ray blocked {bvh.Occluded(p + n * 0.5f, Vector3.Normalize(ts), 1e6f)}; up ray blocked {bvh.Occluded(p + n * 0.5f, n, 1e6f)}");
        foreach (var l in plan.Lights.Where(l => Vector3.Distance(l.Pos, p) < l.Radius).Take(5)) sb.Append($"; {l.Source} at {Vector3.Distance(l.Pos, p):F0} units");
        return sb.ToString();
    }

    /// <summary>
    /// Memory budget of the target world bundle, part by part, against the original Showdown Town bundle (the size known
    /// to load on a real Xbox 360), with suggestions when it is over or close (CLI, plan.txt, the Studio dialog).
    /// </summary>
    public static List<string> Budget(VmfPlan p)
    {
        static string MB(long b) => $"{b / 1048576.0,6:F1} MB";
        var l = new List<string> { "memory budget (world bundle 234cec; the original " + MB(p.BudgetBytes).Trim() + " runs on a real Xbox 360):" };
        if (p.BudgetBytes <= 0) { l.Add("  (no budget for this world)"); return l; }
        bool sky = p.SkyTriangles > 0;
        l.Add($"  town without its scenery (baseline) {MB(p.BaselineBytes)}");
        l.Add($"  render geometry                     {MB(p.GeometryBytes)}  ({p.RenderTriangles:N0} triangles{(sky ? $"; skybox terrain {MB(p.SkyGeometryBytes).Trim()}, {p.SkyTriangles:N0} triangles" : "")})");
        l.Add($"  collision                           {MB(p.CollisionBytes)}  ({p.CollisionTriangles:N0} triangles{(sky ? $"; skybox terrain {MB(p.SkyCollisionBytes).Trim()}, {p.SkyCollisionTriangles:N0} triangles, every {p.SkyCollisionStep}. displacement row" : "")})");
        l.Add($"  textures                            {MB(p.TextureBytes)}  ({p.Materials.Count} materials, max {p.MaxTextureSize})");
        l.Add($"  lightmaps                           {MB(p.LightmapBytes)}  ({p.LightmapPages} page(s), {p.Luxels:N0} luxels of {p.LuxelSize:G3} units{(sky ? $"; skybox terrain {p.SkyLuxels:N0} luxels x{p.SkyLuxelScale:G3}" : "")})");
        l.Add($"  props                               {MB(p.PropBytes)}  ({p.PropInstancesPlaced} placed{(p.PropBudgetBytes > 0 && p.PropInstancesPlaced == 0 ? $"; all {p.PropInstances} would add ~{MB(p.PropBudgetBytes).Trim()}" : "")})");
        long over = p.EstimatedBytes - p.BudgetBytes;
        l.Add($"  total                               {MB(p.EstimatedBytes)}  of {MB(p.BudgetBytes).Trim()}: " +
              (over <= 0 ? $"ok ({MB(-over).Trim()} free)" : $"OVER by {MB(over).Trim()}"));
        if (over > -p.BudgetBytes / 20)
        {
            // suggestions, biggest saving first
            var sug = new List<(long Save, string Text)>
            {
                (p.LightmapBytes * 3 / 4, $"coarser lightmaps: luxel {p.LuxelSize:G3} -> {p.LuxelSize * 2:G3} (--luxel) saves ~{MB(p.LightmapBytes * 3 / 4).Trim()}"),
                (p.TextureBytes * 3 / 4, $"smaller textures: max {p.MaxTextureSize} -> {p.MaxTextureSize / 2} (--tex-size) saves ~{MB(p.TextureBytes * 3 / 4).Trim()}"),
                (p.PropBytes, $"leave props out saves ~{MB(p.PropBytes).Trim()}"),
                (p.SkyLightmapBytes * 3 / 4, $"coarser skybox terrain lightmaps (--sky-luxel {p.SkyLuxelScale * 2:G3}) saves ~{MB(p.SkyLightmapBytes * 3 / 4).Trim()}"),
                (p.SkyCollisionBytes * 3 / 4, $"simpler skybox terrain collision (--sky-collision-step {p.SkyCollisionStep * 2}) saves ~{MB(p.SkyCollisionBytes * 3 / 4).Trim()}"),
                (p.SkyGeometryBytes + p.SkyCollisionBytes + p.SkyLightmapBytes, $"drop the skybox terrain (--skybox drop) saves ~{MB(p.SkyGeometryBytes + p.SkyCollisionBytes + p.SkyLightmapBytes).Trim()}"),
            };
            foreach (var (save, text) in sug.Where(x => x.Save > 64 * 1024).OrderByDescending(x => x.Save)) l.Add("  suggestion: " + text);
        }
        return l;
    }

    /// <summary>The game's world box (the Havok broadphase, built from the level collision's bounds) and what happens past it.</summary>
    public static string WorldBox(VmfPlan p)
    {
        if (p.CollisionTriangles == 0) return "world box: no collision";
        var mn = p.CollisionMin; var mx = p.CollisionMax;
        float xz = p.UnlimitedBounds ? 2048 : 100;
        return $"world box (game units): {mn.X - xz:F0},{mn.Y - 100:F0},{mn.Z - xz:F0} .. {mx.X + xz:F0},{mx.Y + 100:F0},{mx.Z + xz:F0} (collision bounds + {xz:F0} on X/Z, 100 on Y); " +
               (p.UnlimitedBounds ? "leaving it does not reset the player (exe mod no-escape-reset); falling off the map's edge falls forever (pause menu to leave)"
                                  : $"leaving it resets the player (reset plane y {mn.Y - 100:F0})");
    }

    /// <summary>Human-readable summary of a plan (CLI, plan.txt and the Studio dialog).</summary>
    public static List<string> Describe(VmfPlan p)
    {
        var l = new List<string>
        {
            $"map {p.MapName}: {p.Brushes} brushes ({p.BrushEntities} brush entities), {p.Faces} drawn faces, {p.Displacements} displacements",
            $"render: {p.RenderTriangles:N0} triangles, {p.RenderVertices:N0} vertices, {p.Materials.Count(m => !m.Material.StartsWith("prop:"))} materials in {p.Chunks.Count} model chunk(s)",
            $"collision: {p.CollisionTriangles:N0} triangles; light blockers: {p.OccluderTriangles:N0}; water: {p.WaterTriangles} triangles",
            $"bounds (game units): {p.Min.X:F1},{p.Min.Y:F1},{p.Min.Z:F1} .. {p.Max.X:F1},{p.Max.Y:F1},{p.Max.Z:F1}",
            p.ContentSource != null ? $"game content: {p.ContentSource}; {p.ContentFound} materials found, {p.ContentMissing} missing; {p.Materials.Count(m => m.Vtf != null)} textures from the game, {p.Materials.Count(m => m.Image != null)} from images, {p.Materials.Count(m => m.Vtf == null && m.Image == null)} generated" : "game content: none (generated colours / material folder)",
            p.Skybox3D == null ? "3D skybox: none" : p.SkyPorted > 0 || p.SkySkippedReplica > 0
                ? $"3D skybox: {p.Skybox3D}; {p.SkyPorted} brushes ported ({p.SkyTriangles:N0} triangles, {p.SkyCollisionTriangles:N0} collision), {p.SkySkippedReplica} skipped as the replica under the map, {p.SkyLights.Count} skybox lights"
                : $"3D skybox: {p.Skybox3D}; {p.SkyboxBrushes} brushes and {p.SkyboxEntities} entities dropped",
            p.SpawnSource != null ? $"spawn: {p.SpawnSource} -> game ({p.Spawn!.Value.X:F2}, {p.Spawn.Value.Y:F2}, {p.Spawn.Value.Z:F2})" : "spawn: none",
            p.LightSource ?? "light: none", p.FogSource ?? "fog: none (the world keeps its fog)",
            p.LightmapPages > 0 ? $"lightmaps: {p.Lights.Count} lights, {p.Luxels:N0} luxels on {p.LightmapPages} page(s)" : "lightmaps: off",
            $"props: {p.PropInstances} in the map ({p.Props.Count} models), {p.PropInstancesPlaced} placed",
            $"size estimate: +{p.AddedBytes / 1048576.0:F2} MB (geometry {p.GeometryBytes / 1048576.0:F2}, textures {p.TextureBytes / 1048576.0:F2}, lightmaps {p.LightmapBytes / 1048576.0:F2}, collision {p.CollisionBytes / 1048576.0:F2}, props {p.PropBytes / 1048576.0:F2})" +
                (p.BudgetBytes > 0 ? $" -> world bundle ~{p.EstimatedBytes / 1048576.0:F1} MB of {p.BudgetBytes / 1048576.0:F1} MB budget ({p.BudgetState})" : ""),
        };
        l.Add(WorldBox(p));
        l.AddRange(Budget(p));
        l.AddRange(p.Notes.Select(n => "note: " + n));
        l.AddRange(p.Warnings.Select(w => "WARNING: " + w));
        return l;
    }
}
