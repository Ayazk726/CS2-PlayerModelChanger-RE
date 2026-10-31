using System.Text.RegularExpressions;

namespace PlayerModelChanger;

public static class MeshGroupCatalogueBuilder
{
    public static readonly string[] LockedPrefixes = ["first_or_third_person", "face"];
    public static readonly string[] LockedExact = ["autodefault", "defusekit"];

    private static readonly Regex ListEntry = new(
        @"^(\S+)\s+crc=0x[0-9a-f]+\s+metadatasz=\d+\s+fnumber=\d+\s+ofs=0x[0-9a-f]+\s+sz=\d+$",
        RegexOptions.Compiled);

    public static string FamilyOf(string group)
    {
        var i = group.IndexOf("_@", StringComparison.Ordinal);
        return i > 0 ? group[..i] : group;
    }

    public static string VariantOf(string group)
    {
        var i = group.IndexOf("_#&", StringComparison.Ordinal);
        return i > 0 ? group[(i + 3)..] : "";
    }

    public static bool IsLocked(string family)
    {
        var f = family.ToLowerInvariant();
        if (LockedExact.Contains(f))
        {
            return true;
        }
        return LockedPrefixes.Any(prefix => f.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static (List<string> Groups, ulong? DefaultMask) ParseDataBlock(string text)
    {
        var groups = new List<string>();
        ulong? defaultMask = null;
        var inGroups = false;
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (inGroups)
            {
                if (s.StartsWith(']'))
                {
                    inGroups = false;
                    continue;
                }
                s = s.TrimEnd(',');
                if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                {
                    groups.Add(s[1..^1].Replace("\\\"", "\""));
                }
                continue;
            }

            if (s.StartsWith("m_meshGroups", StringComparison.Ordinal))
            {
                groups = new List<string>();
                inGroups = true;
            }
            else if (s.StartsWith("m_nDefaultMeshGroupMask", StringComparison.Ordinal))
            {
                var equals = s.IndexOf('=');
                if (equals >= 0 && ulong.TryParse(s[(equals + 1)..].Trim(), out var mask))
                {
                    defaultMask = mask;
                }
            }
        }
        return (groups, defaultMask);
    }

    public static List<string> ParseModelList(string text)
    {
        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = ListEntry.Match(line.Trim());
            if (!match.Success) continue;
            var path = match.Groups[1].Value.Replace('\\', '/');
            if (!path.StartsWith("characters/models/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(path)) models.Add(path);
        }
        models.Sort(StringComparer.Ordinal);
        return models;
    }

    public static MeshGroupModel BuildModel(IReadOnlyList<string> groups, ulong defaultMask)
    {
        var families = new List<string>();
        var byFamily = new Dictionary<string, List<(int Index, string Group)>>(StringComparer.Ordinal);
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var family = FamilyOf(group);
            if (!byFamily.TryGetValue(family, out var members))
            {
                members = [];
                byFamily[family] = members;
                families.Add(family);
            }
            members.Add((index, group));
        }

        var model = new MeshGroupModel { DefaultMask = defaultMask };
        foreach (var family in families)
        {
            var members = byFamily[family];
            var locked = IsLocked(family);
            var category = new MeshGroupCategory
            {
                Name = family,
                Type = members.Count > 1 ? "opradio" : "multi",
            };
            foreach (var (index, group) in members)
            {
                var variant = VariantOf(group);
                category.Items.Add(new MeshGroupItem
                {
                    Label = members.Count == 1
                        ? (variant.Length > 0 ? variant : family)
                        : (variant.Length > 0 ? variant : group),
                    Index = index,
                    Group = group,
                    Enabled = !locked,
                });
            }
            model.Categories.Add(category);
        }
        return model;
    }
}
