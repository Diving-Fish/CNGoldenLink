using System.Text;
using System.Text.Json;

namespace CNGoldenLink;

// Pure background adapter. Caller supplies persisted identity mappings; never use display names as IDs.
public static class CctRouteReader
{
    public sealed record Identity(string SegmentKey, string[] CheckpointKeys);
    public sealed record Result(string SegmentKey, int? SelectedIndex, JsonElement Route, int GameplayRoomCount);

    public static Result Read(string json, Func<int?, JsonElement, Identity> resolveIdentity)
    {
        if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new FormatException("route_file_too_large");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var path = doc.RootElement;
        int? selected = null;
        if (path.TryGetProperty("segments", out var segments))
        {
            var index = path.GetProperty("selectedIndex").GetInt32();
            if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() > 2000 || index < 0 || index >= segments.GetArrayLength()) throw new FormatException("invalid_segment_index");
            selected = index;
            path = segments[index].GetProperty("path");
        }
        var cps = path.GetProperty("checkpoints");
        if (cps.ValueKind != JsonValueKind.Array || cps.GetArrayLength() > 2000) throw new FormatException("invalid_checkpoints");
        var identity = resolveIdentity(selected, path.Clone());
        Key(identity.SegmentKey);
        if (identity.CheckpointKeys.Length != cps.GetArrayLength() || identity.CheckpointKeys.Distinct(StringComparer.Ordinal).Count() != cps.GetArrayLength()) throw new FormatException("route_identity_required");
        var nodes = new List<object>();
        var checkpoints = new List<object>();
        int gameplay = 0, cpIndex = 0;
        foreach (var cp in cps.EnumerateArray())
        {
            var checkpointKey = Key(identity.CheckpointKeys[cpIndex++]);
            checkpoints.Add(new { checkpointKey, name = Text(cp, "name"), abbreviation = Text(cp, "abbreviation", 64) });
            foreach (var room in cp.GetProperty("rooms").EnumerateArray())
            {
                if (nodes.Count >= 2000) throw new FormatException("route_file_too_large");
                bool nonGameplay = room.TryGetProperty("isNonGameplayRoom", out var flag) && flag.GetBoolean();
                if (!nonGameplay) gameplay++;
                nodes.Add(new { roomKey = Key(room.GetProperty("debugRoomName").GetString()), checkpointKey,
                    groupedRooms = Strings(room, "groupedRooms", 100), isNonGameplayRoom = nonGameplay,
                    customRoomName = Text(room, "customRoomName") });
            }
        }
        var route = JsonSerializer.SerializeToElement(new {
            nodes, checkpoints, ignoredRooms = Strings(path, "ignoredRooms", 2000),
            chapterSID = Text(path, "chapterSID", 512), campaignName = Text(path, "campaignName"),
            chapterName = Text(path, "chapterName"), sideName = Text(path, "sideName", 64)
        });
        return new(identity.SegmentKey, selected, route, gameplay);
    }
    private static string Key(string? value, int max = 256)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max || value.Any(c => c < 32)) throw new FormatException("invalid_route_text");
        return value;
    }
    private static string? Text(JsonElement obj, string field, int max = 256)
    {
        if (!obj.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        var value = v.GetString();
        return value == "" ? null : Key(value, max);
    }
    private static string[] Strings(JsonElement obj, string field, int max)
    {
        if (!obj.TryGetProperty(field, out var v)) return Array.Empty<string>();
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > max) throw new FormatException("invalid_route_list");
        return v.EnumerateArray().Select(x => Key(x.GetString())).ToArray();
    }
}
