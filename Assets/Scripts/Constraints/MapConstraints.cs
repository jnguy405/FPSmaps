using System;
using System.Collections.Generic;
using UnityEngine;

public enum RoomRole : byte { None, AttackerSpawn, DefenderSpawn, Mid, Site }

// whether to apply the attack/defense rules, and the few map options that sit alongside them
// the rules themselves are fixed constants on SpawnRules, MidRules and SiteRules, so every map plays the same way
// positions are fractions of the map size and distances are walking distances, so rules scale with the map
[Serializable]
public class MapConstraints
{
    public bool enabled = true;

    [Range(2, 3)] public int siteCount = 2;

    [Tooltip("Seed variations to try before settling for the closest attempt.")]
    [Range(1, 50)] public int maxAttempts = 20;

    // two spawns, mid, and the sites
    public int RequiredRooms => 3 + siteCount;
}

// a generated layout plus the role each room plays and how well it met the constraints
public class MapPlan
{
    public BSPLayout layout;
    public int attempt;   // 0 means the requested seed worked first try
    public RoomRole[] roles;
    public int attackerSpawn = -1, defenderSpawn = -1, mid = -1;
    public readonly int[] attackerLobbies = { -1, -1 };          // rooms out the attacker spawn's left and right exits
    public readonly List<int> sites = new List<int>();         // A (left side), B (right side), then C
    public readonly List<string> failures = new List<string>(); // broken rules, empty when solved
    public readonly List<string> report = new List<string>();   // distances and entrances for the inspector

    public bool Solved => failures.Count == 0;

    // short tag for a room, e.g. "ATK", "MID" or "B", or null for an unassigned room
    public string Label(int room)
    {
        if (roles == null || room < 0 || room >= roles.Length) return null;
        return roles[room] switch
        {
            RoomRole.AttackerSpawn => "ATK",
            RoomRole.DefenderSpawn => "DEF",
            RoomRole.Mid => "MID",
            RoomRole.Site => ((char)('A' + sites.IndexOf(room))).ToString(),
            _ => null,
        };
    }

    // readable name for the HUD, e.g. "Site B" or "Attacker Spawn", or null for an unassigned room
    public string FullName(int room)
    {
        string label = Label(room);
        if (label == null) return null;
        return roles[room] switch
        {
            RoomRole.AttackerSpawn => "Attacker Spawn",
            RoomRole.DefenderSpawn => "Defender Spawn",
            RoomRole.Mid => "Mid",
            _ => "Site " + label,
        };
    }

    // layout hash mixed with the attempt and roles, so a change in either shows up
    public int Hash
    {
        get
        {
            int h = unchecked((layout.Hash ^ attempt) * 16777619);
            if (roles != null)
                foreach (var r in roles) h = unchecked((h ^ (int)r) * 16777619);
            return h;
        }
    }
}
