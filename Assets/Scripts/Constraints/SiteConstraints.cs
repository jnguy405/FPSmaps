using System.Collections.Generic;
using UnityEngine;

// sites: A and B on opposite sides, big enough, apart from each other, with entrances from both sides plus one from mid
// the walk targets below only steer which rooms get picked and show up as notes in the report,
// they never fail a map
public static class SiteRules
{
    // entrances: at least one from each side, two wanted, and every site always gets a corridor from mid
    // that counts as a defender entrance
    public const int MinEntrancesPerSide = 1;
    public const int PreferredEntrancesPerSide = 2;

    // smallest floor area a site can have, in cells
    public const int MinArea = 36;

    // sites at least this far apart, as a fraction of the spawn-to-spawn walk, so they don't play as one site
    public const float MinSeparation = 0.3f;

    // TARGETS

    // where sites aim to sit between the attacker edge (0) and the defender edge (1):
    // just past the middle, so defenders are a bit closer without the site sitting on their spawn
    public const float Position = 0.58f;

    // defenders' walk to a site as a fraction of the attackers' walk: under the max so defenders get there first,
    // over the min so the site isn't right on top of their spawn
    public const float MinDefenderShare = 0.5f;
    public const float MaxDefenderShare = 0.9f;

    // attackers reach each site within this fraction of the spawn-to-spawn walk
    public const float MaxAttackerWalk = 0.75f;
}

// site picking, growing, entrances and checks
public static partial class MapConstraintSolver
{
    // scoring weights that steer the site pick without ruling anything out
    const float SitePositionWeight = 2f;   // per fraction of the map a site sits off its wanted position
    const float SiteShareWeight = 1f;      // per fraction the defender share falls outside its window
    const float FarSitePenalty = 2f;       // per fraction of the spawn walk a site sits past the max attacker walk
    const float SiteSpreadWeight = 0.1f;   // A and B out to their sides, C near the center line
    const float SiteEvenWeight = 0.5f;     // how uneven the walks to each site are, a mild preference only

    // SITE PLACEMENT

    // A from the left side, B from the right side, C (if any) from what's left
    // only size, sides and separation rule a set out, everything else is scored
    // (walks shift once rooms are resized and the lobbies and entrances go in, so they're checked for real at the end)
    // returns the best scoring set, or null if no set passes
    static List<int> PickSites(BSPLayout l, int[,] dist, Placement p, int count, int pad, out float bestCost)
    {
        bestCost = float.MaxValue;
        float minSep = SiteRules.MinSeparation * dist[p.atk, p.def];

        // candidates: room to grow to site size, split by side
        var left = new List<int>();
        var right = new List<int>();
        var all = new List<int>();
        for (int i = 0; i < l.rooms.Count; i++)
        {
            if (i == p.atk || i == p.def || i == p.mid) continue;
            if (Area(GrowLimit(l, i, pad)) < SiteRules.MinArea) continue;

            all.Add(i);
            if (Across(l, p.o, i) < 0f) left.Add(i);
            else if (Across(l, p.o, i) > 0f) right.Add(i);
        }

        List<int> best = null;
        foreach (var a in left)
        {
            foreach (var b in right)
            {
                if (dist[a, b] < minSep) continue;

                // two sites: just A and B
                if (count == 2)
                {
                    TryKeep(new List<int> { a, b }, dist, l, p, ref best, ref bestCost);
                    continue;
                }

                // three sites: add a C that's far enough from both
                foreach (var s in all)
                {
                    if (s == a || s == b || dist[a, s] < minSep || dist[b, s] < minSep) continue;
                    TryKeep(new List<int> { a, b, s }, dist, l, p, ref best, ref bestCost);
                }
            }
        }
        return best;
    }

    // scores one set of sites and keeps it if it beats the best so far
    static void TryKeep(List<int> sites, int[,] dist, BSPLayout l, Placement p, ref List<int> best, ref float bestCost)
    {
        // a mild nudge toward sites that are about as far as each other
        float cost = SiteEvenWeight * SiteImbalance(sites, dist, p.atk, p.def, p.mid);

        float spawnDist = Mathf.Max(1, dist[p.atk, p.def]);
        float maxWalk = spawnDist * SiteRules.MaxAttackerWalk;
        foreach (var s in sites)
        {
            // position between the spawns is stable through repair, so it carries the most weight
            cost += SitePositionWeight * Mathf.Abs(Along(l, p.o, s) - SiteRules.Position);

            // defender share outside its window, and attacker walks past the max
            float share = dist[p.def, s] / (float)Mathf.Max(1, dist[p.atk, s]);
            cost += SiteShareWeight * (Mathf.Max(0f, SiteRules.MinDefenderShare - share) + Mathf.Max(0f, share - SiteRules.MaxDefenderShare));
            if (dist[p.atk, s] > maxWalk) cost += FarSitePenalty * (dist[p.atk, s] - maxWalk) / spawnDist;
        }

        // tie-breakers: A and B pushed out to their sides, C kept near the center line
        cost -= SiteSpreadWeight * (Mathf.Abs(Across(l, p.o, sites[0])) + Mathf.Abs(Across(l, p.o, sites[1])));
        if (sites.Count > 2) cost += SiteSpreadWeight * Mathf.Abs(Across(l, p.o, sites[2]));

        if (cost < bestCost)
        {
            bestCost = cost;
            best = sites;
        }
    }

    // worst spread between sites over mid-to-site, attacker-to-site and defender-to-site walks
    // 0 when every site is exactly as far as the others on all three
    static float SiteImbalance(List<int> sites, int[,] dist, int atk, int def, int mid)
    {
        float worst = 0f;
        foreach (int from in new[] { mid, atk, def })
        {
            int min = Unreachable, max = 0;
            foreach (var s in sites)
            {
                min = Mathf.Min(min, dist[from, s]);
                max = Mathf.Max(max, dist[from, s]);
            }
            worst = Mathf.Max(worst, (max - min) / (float)Mathf.Max(1, max));
        }
        return worst;
    }

    // every site grown up to the minimum site size, returns how many changed
    static int RepairSites(MapPlan plan, int pad, int width)
    {
        var l = plan.layout;
        int resized = 0;
        foreach (var s in plan.sites)
            resized += Resize(l, s, GrowToArea(l.rooms[s], GrowLimit(l, s, pad), SiteRules.MinArea), width);
        return resized;
    }

    // SITE ENTRANCES

    static void CountEntrances(MapPlan plan, Orientation o, int site, out int attacker, out int defender, out bool fromMid)
    {
        attacker = 0;
        defender = 0;
        fromMid = false;
        foreach (var other in plan.layout.Neighbors(site))
        {
            if (other == plan.mid) fromMid = true;
            if (IsAttackerSide(plan, o, site, other)) attacker++;
            else defender++;
        }
    }

    // per site: a link to mid, a real route in for each team, then extra entrances up to the preferred count
    static void AddSiteEntrances(MapPlan plan, Orientation o, int corridorWidth, System.Random rng)
    {
        var layout = plan.layout;
        foreach (var site in plan.sites)
        {
            JoinOnce(layout, site, plan.mid, corridorWidth, rng);

            // attacker side first, then defender side
            foreach (bool attackerSide in new[] { true, false })
            {
                // the team's route comes first: link to a room on its side that it can already reach
                // without going through the site or past the other spawn
                if (!HasTeamRoute(plan, o, site, attackerSide))
                {
                    var reach = TeamReach(plan, o, site, attackerSide);
                    int target = NearestOnSide(plan, o, site, attackerSide, reach);
                    if (target >= 0) BSPGenerator.ConnectPair(layout, site, target, rng.NextDouble() < 0.5, corridorWidth);
                }

                // then extra entrances on that side until full or out of rooms
                while (true)
                {
                    CountEntrances(plan, o, site, out int atkCount, out int defCount, out _);
                    if ((attackerSide ? atkCount : defCount) >= SiteRules.PreferredEntrancesPerSide) break;

                    int target = NearestOnSide(plan, o, site, attackerSide, null);
                    if (target < 0) break;
                    BSPGenerator.ConnectPair(layout, site, target, rng.NextDouble() < 0.5, corridorWidth);
                }
            }
        }
    }

    // SITE CHECKS

    // A and B on opposite sides, and every site far enough from the others
    // how even the walks are is only reported
    static void CheckSiteLayout(MapPlan plan, int[,] dist, Orientation o)
    {
        var l = plan.layout;
        if (Across(l, o, plan.sites[0]) >= 0f || Across(l, o, plan.sites[1]) <= 0f)
            plan.failures.Add("Sites A and B are not on opposite sides");

        float imbalance = SiteImbalance(plan.sites, dist, plan.attackerSpawn, plan.defenderSpawn, plan.mid);
        plan.report.Add($"Site distance spread: {imbalance:0.00} (0 is perfectly even)");

        // sites too close together would play like one big site
        float minSep = SiteRules.MinSeparation * dist[plan.attackerSpawn, plan.defenderSpawn];
        for (int p = 0; p < plan.sites.Count; p++)
        {
            for (int q = p + 1; q < plan.sites.Count; q++)
            {
                int d = dist[plan.sites[p], plan.sites[q]];
                if (d < minSep)
                    plan.failures.Add($"Sites {plan.Label(plan.sites[p])} and {plan.Label(plan.sites[q])} only {d} cells apart");
            }
        }
    }

    // one site: size, entrances on both sides, and team routes
    // position, defender share and attacker walk are targets, so missing them is only noted
    static void CheckSite(MapPlan plan, int[,] dist, Orientation o, int s)
    {
        string name = plan.Label(s);
        int area = Area(plan.layout.rooms[s]);
        float along = Along(plan.layout, o, s);
        int dA = dist[plan.attackerSpawn, s], dD = dist[plan.defenderSpawn, s], dM = dist[plan.mid, s];
        float share = dD / (float)Mathf.Max(1, dA);
        CountEntrances(plan, o, s, out int inAtk, out int inDef, out bool fromMid);
        plan.report.Add($"Site {name}: at {along:0.00}, mid {dM}, attackers {dA}, defenders {dD} ({share:0.00}) | entrances {inAtk} atk, {inDef} def{(fromMid ? " (1 from mid)" : "")}");

        if (share > SiteRules.MaxDefenderShare)
            plan.report.Add($"Site {name}: defenders aren't much ahead of attackers");
        if (share < SiteRules.MinDefenderShare)
            plan.report.Add($"Site {name}: sits close to the defenders");
        if (dA > dist[plan.attackerSpawn, plan.defenderSpawn] * SiteRules.MaxAttackerWalk)
            plan.report.Add($"Site {name}: a long walk for attackers");

        if (area < SiteRules.MinArea)
            plan.failures.Add($"Site {name} is {area} cells, need {SiteRules.MinArea}");
        if (inAtk < SiteRules.MinEntrancesPerSide)
            plan.failures.Add($"Site {name} has {inAtk} attacker entrances, need {SiteRules.MinEntrancesPerSide}");
        if (inDef < SiteRules.MinEntrancesPerSide)
            plan.failures.Add($"Site {name} has {inDef} defender entrances, need {SiteRules.MinEntrancesPerSide}");

        // each team needs a way in from its own side that doesn't run through the other spawn
        if (!HasTeamRoute(plan, o, s, true))
            plan.failures.Add($"Site {name} has no attacker route in from the attacker side");
        if (!HasTeamRoute(plan, o, s, false))
            plan.failures.Add($"Site {name} has no defender route in from the defender side");

        // mid branches off the routes between the spawns, it can't hang behind a spawn
        if (BSPGenerator.RoomHops(plan.layout, s, plan.attackerSpawn, plan.defenderSpawn)[plan.mid] < 0)
            plan.failures.Add($"Site {name} only reaches mid through a spawn");

        // preferred counts are a guideline, so only noted
        int preferred = SiteRules.PreferredEntrancesPerSide;
        if (inAtk < preferred || inDef < preferred)
            plan.report.Add($"Site {name} is below the preferred {preferred} entrances per side");
    }
}
