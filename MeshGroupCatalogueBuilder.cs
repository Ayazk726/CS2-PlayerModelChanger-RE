using System.Text.RegularExpressions;

namespace PlayerModelChanger;

/// <summary>
/// Turns the DATA block of a compiled model into the catalogue entry the plugin
/// uses: m_meshGroups holds the families and their variants, and
/// m_nDefaultMeshGroupMask says which of them the model is worn with.
/// </summary>
/// <remarks>
/// It is the C# counterpart of tools/build_meshgroups.py and the two have to
/// stay in step: the same families are locked and the same labels are built.
/// </remarks>
public static class MeshGroupCatalogueBuilder
{
    /// <summary>
    /// The families the player may not change.
    /// </summary>
    public static readonly string[] LockedPrefixes = ["first_or_third_person", "face"];
    public static readonly string[] LockedExact = ["autodefault", "defusekit"];

    private static readonly Regex ListEntry = new(
        @"^(\S+)\s+crc=0x[0-9a-f]+\s+metadatasz=\d+\s+fnumber=\d+\s+ofs=0x[0-9a-f]+\s+sz=\d+$",
        RegexOptions.Compiled);

    /// <summary>
    /// The family of a mesh group: everything in front of the "@".
    /// </summary>
    public static string FamilyOf(string group)
    {
        var i = group.IndexOf("_@", StringComparison.Ordinal);
        return i > 0 ? group[..i] : group;
    }

    /// <summary>
    /// The name the model gave the mesh group: everything behind the "#&amp;".
    /// </summary>
    public static string VariantOf(string group)
    {
        var i = group.IndexOf("_#&", StringComparison.Ordinal);
        return i > 0 ? group[(i + 3)..] : "";
    }

    /// <summary>
    /// True when the family stays on the value of the model by default.
    /// </summary>
    public static bool IsLocked(string family)
    {
        var f = family.ToLowerInvariant();
        if (LockedExact.Contains(f))
        {
            return true;
        }
        return LockedPrefixes.Any(prefix => f.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads m_meshGroups and m_nDefaultMeshGroupMask out of the DATA block the
    /// CLI printed for a model.
    /// </summary>
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

    /// <summary>
    /// The models of the VPK listing the CLI printed: the character models a
    /// catalogue can be built for.
    /// </summary>
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

    /// <summary>
    /// Builds the catalogue entry of one model out of its mesh groups.
    /// </summary>
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
