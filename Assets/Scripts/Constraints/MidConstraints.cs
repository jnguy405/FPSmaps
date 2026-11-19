using UnityEngine;

// mid: the biggest room, over the map center, at least a room out from each spawn,
// with its own way in from each team's half that never runs through a site
public static class MidRules
{
    // rooms that must lie between each spawn and mid, so neither team steps straight into it
    // (defenders still get there sooner through the defender lead)
    public const int AttackerGap = 1;
    public const int DefenderGap = 1;

    // attackers get a mostly direct way in: their best route on their own half, not through a site,
    // passes at most this many rooms, so it's spawn, a room or two, then a hallway into mid
    public const int AttackerReach = 2;

    // mid has to touch the map center, or sit within this fraction of the map size from it
    public const float Centering = 0.1f;

    // how much sooner defenders should reach mid, as (attacker walk - defender walk) / (both walks), 0 is even
    // a target only: drifting past the slack either way is noted in the report, never a failure
    public const float DefenderLead = 0.1f;
    public const float LeadSlack = 0.25f;
}

// mid picking, growing, routes and checks
public static partial class MapConstraintSolver
{
    // scoring weights that steer the mid pick without ruling anything out
    const float MidGapPenalty = 1f;     // room too few rooms away from a spawn
    const float MidSizeWeight = 0.5f;   // room by how far it falls short of the biggest room on the map

    // rooms that must lie between a team's spawn and mid
    static int MidGap(bool attackers) => attackers ? MidRules.AttackerGap : MidRules.DefenderGap;

    // MID PLACEMENT

    static Vector2 MapCenter(BSPLayout l) => new Vector2(l.width / 2f, l.height / 2f);

    // cells from the map center to the rect's nearest point, 0 if the rect covers the center
    static float CenterGap(BSPLayout l, RectInt r)
    {
        var center = MapCenter(l);
        float dx = Mathf.Max(r.xMin - center.x, 0f, center.x - r.xMax);
        float dy = Mathf.Max(r.yMin - center.y, 0f, center.y - r.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // center gap as a fraction of the map size, so it adds up with the other placement costs
    static float MidOffset(BSPLayout l, RectInt r) => CenterGap(l, r) / Mathf.Min(l.width, l.height);

    // mid fills its whole leaf, so it's the biggest area it can be (and covers the center if the leaf does)
    static RectInt GrownMid(BSPLayout l, int room, int pad) => GrowLimit(l, room, pad);

    // room that can best cover the map center and be the biggest area once grown, skipping both spawns
    // rooms too few steps from a spawn are pushed back, so mid never opens straight off a spawn
    static int PickMid(BSPLayout l, int atk, int def, int pad)
    {
        var center = MapCenter(l);
        var fromAtk = BSPGenerator.RoomHops(l, atk);
        var fromDef = BSPGenerator.RoomHops(l, def);

        // biggest area any room could grow to, so a mid's size is scored against the best this map allows
        int maxArea = 1;
        for (int i = 0; i < l.rooms.Count; i++) maxArea = Mathf.Max(maxArea, Area(GrowLimit(l, i, pad)));

        return ArgMin(l.rooms.Count, i =>
        {
            if (i == atk || i == def) return float.PositiveInfinity;

            // off-center and too-small both cost, as fractions so they add up
            // ties (several rooms able to reach the center) go to the room whose center is closest
            var grown = GrownMid(l, i, pad);
            float cost = MidOffset(l, grown) + MidSizeWeight * (1f - Area(grown) / (float)maxArea)
                       + 0.01f * (l.rooms[i].center - center).magnitude / Mathf.Min(l.width, l.height);

            // a penalty rather than a skip, so there's always a mid for the report to explain
            // rooms between = steps - 1
            bool tooClose = fromAtk[i] - 1 < MidGap(true) || fromDef[i] - 1 < MidGap(false);
            return tooClose ? cost + MidGapPenalty : cost;
        });
    }

    // any other room as big as mid gets trimmed under it, returns how many changed
    // spawns keep their fixed size and sites never drop under the site minimum
    static int TrimBelowMid(MapPlan plan, int minRoomSize, int width)
    {
        var l = plan.layout;
        int midArea = Area(l.rooms[plan.mid]);
        int resized = 0;
        for (int i = 0; i < l.rooms.Count; i++)
        {
            var role = plan.roles[i];
            if (i == plan.mid || role == RoomRole.AttackerSpawn || role == RoomRole.DefenderSpawn) continue;
            int minArea = role == RoomRole.Site ? SiteRules.MinArea : 0;
            resized += Resize(l, i, ShrinkBelowArea(l.rooms[i], midArea, minRoomSize, minArea), width);
        }
        return resized;
    }

    // MID ROUTES

    // a route in from each team's own half, linked far enough out to keep the spawn-to-mid gap,
    // so mid is never a dead end and neither team has to go through a site to get there
    // the attacker route also has to be short, a hallway into mid a room or two out from their spawn
    static void AddMidRoutes(MapPlan plan, Orientation o, int corridorWidth, System.Random rng)
    {
        var layout = plan.layout;
        var rooms = layout.rooms;
        int mid = plan.mid;
        foreach (bool attackers in new[] { true, false })
        {
            // linking to a room n steps from the spawn puts n rooms between the spawn and mid
            int gap = MidGap(attackers);
            int maxRooms = attackers ? MidRules.AttackerReach : int.MaxValue;
            int have = TeamRouteRooms(plan, o, mid, attackers);
            if (have >= 0 && have <= maxRooms) continue;

            var reach = TeamReach(plan, o, mid, attackers);
            int target = NearestOnSide(plan, o, mid, attackers, reach, gap, maxRooms);

            // nothing on that half links up yet: take the nearest plain room on it the team can't reach,
            // then bridge it back to the closest room the team can, keeping the route between the gap and the max
            // never to the spawn itself, so the route still leaves through a side exit
            if (target < 0)
            {
                target = ArgMin(rooms.Count, i =>
                {
                    if (reach[i] != -1 || plan.roles[i] != RoomRole.None || layout.AreConnected(mid, i)) return float.PositiveInfinity;
                    if (IsAttackerSide(plan, o, mid, i) != attackers) return float.PositiveInfinity;
                    return (rooms[i].center - rooms[mid].center).sqrMagnitude;
                });
                if (target < 0) continue;

                int bridge = target;
                int link = ArgMin(rooms.Count, i =>
                    reach[i] < Mathf.Max(1, gap - 1) || reach[i] > maxRooms - 1
                        ? float.PositiveInfinity
                        : (rooms[i].center - rooms[bridge].center).sqrMagnitude);
                if (link < 0) continue;
                BSPGenerator.ConnectPair(layout, target, link, rng.NextDouble() < 0.5, corridorWidth);
            }
            BSPGenerator.ConnectPair(layout, mid, target, rng.NextDouble() < 0.5, corridorWidth);
        }
    }

    // MID CHECKS

    // in the middle of the map, the biggest area, fair to walk to (defenders a little ahead),
    // never opening straight off either spawn, and reachable by each team without going through a site
    static void CheckMid(MapPlan plan, int[,] dist, Orientation o)
    {
        int mid = plan.mid;
        var l = plan.layout;
        float offset = MidOffset(l, l.rooms[mid]);
        int mA = dist[plan.attackerSpawn, mid], mD = dist[plan.defenderSpawn, mid];

        // lead > 0 means defenders get there first
        float lead = (mA - mD) / (float)Mathf.Max(1, mA + mD);

        // rooms between = steps - 1
        int gapA = BSPGenerator.RoomHops(l, plan.attackerSpawn)[mid] - 1;
        int gapD = BSPGenerator.RoomHops(l, plan.defenderSpawn)[mid] - 1;

        plan.report.Add($"Mid: {offset:0.00} from center, attackers {mA}, defenders {mD}, {gapA} rooms from ATK, {gapD} from DEF");
        if (offset > MidRules.Centering)
            plan.failures.Add($"Mid is {offset:0.00} from the map center, max {MidRules.Centering:0.00}");
        // the walk balance is a target, so it's only noted
        if (Mathf.Abs(lead - MidRules.DefenderLead) > MidRules.LeadSlack)
            plan.report.Add($"Mid: walk is off balance (defender lead {lead:0.00}, aiming for {MidRules.DefenderLead:0.00})");
        if (gapA < MidRules.AttackerGap)
            plan.failures.Add($"Attacker spawn is {gapA} rooms from mid, need {MidRules.AttackerGap}");
        if (gapD < MidRules.DefenderGap)
            plan.failures.Add($"Defender spawn is {gapD} rooms from mid, need {MidRules.DefenderGap}");

        // the biggest area on the map, only the first room that matches it is named
        int midArea = Area(l.rooms[mid]);
        for (int i = 0; i < l.rooms.Count; i++)
        {
            if (i == mid || Area(l.rooms[i]) < midArea) continue;
            plan.failures.Add($"Mid ({midArea} cells) isn't bigger than {plan.FullName(i) ?? "a plain room"} ({Area(l.rooms[i])} cells)");
            break;
        }

        // each team needs its own way in that stays on its half and doesn't run through a site
        // and the attacker one has to be mostly direct
        int atkRoute = TeamRouteRooms(plan, o, mid, true);
        if (atkRoute < 0)
            plan.failures.Add("Mid has no attacker route in from their half that avoids the sites");
        else if (atkRoute > MidRules.AttackerReach)
            plan.failures.Add($"Attacker route into mid passes {atkRoute} rooms, max {MidRules.AttackerReach}");
        if (!HasTeamRoute(plan, o, mid, false))
            plan.failures.Add("Mid has no defender route in from their half that avoids the sites");
    }
}
