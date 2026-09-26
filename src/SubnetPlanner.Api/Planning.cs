using System.Text.Json;

namespace SubnetPlanner.Api;

public record FieldError(string Field, string Message);
public record AllocationFailure(string DepartmentId, string Reason);

public record ReservedRange(uint Start, uint End);
public record DepartmentRequest(string Id, long Hosts);

public record PlanRequest(
    uint ParentNetwork,
    int ParentPrefix,
    IReadOnlyList<(uint Network, int Prefix)> Occupied,
    IReadOnlyList<ReservedRange> Reserved,
    IReadOnlyList<DepartmentRequest> Departments);

public record DepartmentResult(
    string Id, string Cidr, string Network, string Broadcast,
    string FirstUsable, string LastUsable, long UsableCapacity);

public record PlanResponse(
    IReadOnlyList<DepartmentResult> Departments,
    IReadOnlyList<string> RemainingCidrs,
    ulong TotalAddresses, ulong OccupiedAddresses, ulong AllocatedAddresses, ulong RemainingAddresses);

public static class PlanRequestParser
{
    public static bool TryParse(JsonElement root, out PlanRequest? request, out List<FieldError> errors)
    {
        request = null;
        errors = new();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("$", "请求体必须是JSON对象"));
            return false;
        }

        TryGetCidr(root, "parentCidr", out var parentNetwork, out var parentPrefix, errors);

        var occupied = new List<(uint, int)>();
        if (root.TryGetProperty("occupiedSubnets", out var occEl))
        {
            if (occEl.ValueKind != JsonValueKind.Array)
                errors.Add(new("occupiedSubnets", "必须是数组"));
            else
            {
                var i = 0;
                foreach (var item in occEl.EnumerateArray())
                {
                    if (TryGetCidr(item, $"occupiedSubnets[{i}]", out var n, out var p, errors, elementIsValue: true))
                        occupied.Add((n, p));
                    i++;
                }
            }
        }

        var reserved = new List<ReservedRange>();
        if (root.TryGetProperty("reservedRanges", out var resEl))
        {
            if (resEl.ValueKind != JsonValueKind.Array)
                errors.Add(new("reservedRanges", "必须是数组"));
            else
            {
                var i = 0;
                foreach (var item in resEl.EnumerateArray())
                {
                    var field = $"reservedRanges[{i}]";
                    var hasStart = TryGetAddress(item, "start", field, out var start, errors);
                    var hasEnd = TryGetAddress(item, "end", field, out var end, errors);
                    if (hasStart && hasEnd)
                    {
                        if (start > end)
                            errors.Add(new($"{field}.end", "区间结束地址不能小于起始地址"));
                        else
                            reserved.Add(new ReservedRange(start, end));
                    }
                    i++;
                }
            }
        }

        var departments = new List<DepartmentRequest>();
        if (!root.TryGetProperty("departments", out var depEl))
        {
            errors.Add(new("departments", "缺少必填字段"));
        }
        else if (depEl.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("departments", "必须是数组"));
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var i = 0;
            foreach (var item in depEl.EnumerateArray())
            {
                var field = $"departments[{i}]";
                string? id = null;
                long hosts = 0;
                var ok = true;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new(field, "必须是对象"));
                    ok = false;
                }
                else
                {
                    if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idEl.GetString()))
                    {
                        errors.Add(new($"{field}.id", "部门ID必须是非空字符串"));
                        ok = false;
                    }
                    else id = idEl.GetString()!;
                    if (!item.TryGetProperty("hosts", out var hostsEl) || hostsEl.ValueKind != JsonValueKind.Number
                        || !hostsEl.TryGetInt64(out hosts) || hosts <= 0)
                    {
                        errors.Add(new($"{field}.hosts", "主机数必须是正整数"));
                        ok = false;
                    }
                    else if (hosts > (long)uint.MaxValue - 1)
                    {
                        errors.Add(new($"{field}.hosts", "主机数超出IPv4可分配范围"));
                        ok = false;
                    }
                }
                if (ok && id is not null)
                {
                    if (!seen.Add(id))
                        errors.Add(new($"{field}.id", $"部门ID '{id}' 重复"));
                    else
                        departments.Add(new DepartmentRequest(id, hosts));
                }
                i++;
            }
        }

        if (errors.Count > 0) return false;

        var parentStart = (ulong)parentNetwork;
        var parentEnd = parentStart + IpMath.BlockSize(parentPrefix) - 1;
        foreach (var (n, p) in occupied)
        {
            var start = (ulong)n;
            var end = start + IpMath.BlockSize(p) - 1;
            if (start < parentStart || end > parentEnd)
                errors.Add(new("occupiedSubnets", $"子网 {IpMath.Format(n)}/{p} 超出父网段范围"));
        }
        foreach (var range in reserved)
        {
            if (range.Start < parentStart || range.End > parentEnd)
                errors.Add(new("reservedRanges", $"区间 {IpMath.Format(range.Start)}-{IpMath.Format(range.End)} 超出父网段范围"));
        }
        if (errors.Count > 0) return false;

        request = new PlanRequest(parentNetwork, parentPrefix, occupied, reserved, departments);
        return true;
    }

    private static bool TryGetCidr(JsonElement el, string field,
        out uint network, out int prefix, List<FieldError> errors, bool elementIsValue = false)
    {
        network = 0;
        prefix = 0;
        var valueEl = el;
        if (!elementIsValue)
        {
            if (!el.TryGetProperty(field, out valueEl))
            {
                errors.Add(new(field, "缺少必填字段"));
                return false;
            }
        }
        if (valueEl.ValueKind != JsonValueKind.String)
        {
            errors.Add(new(field, "CIDR必须是字符串，如 10.0.0.0/24"));
            return false;
        }
        var text = valueEl.GetString()!;
        var slash = text.IndexOf('/');
        if (slash <= 0 || slash != text.LastIndexOf('/'))
        {
            errors.Add(new(field, "CIDR格式应为 地址/前缀"));
            return false;
        }
        var addrText = text[..slash];
        var prefixText = text[(slash + 1)..];
        if (!IpMath.TryParseIPv4(addrText, out var address))
        {
            errors.Add(new(field, $"'{addrText}' 不是合法的IPv4地址"));
            return false;
        }
        if (prefixText.Length == 0 || !prefixText.All(char.IsAsciiDigit)
            || (prefixText.Length > 1 && prefixText[0] == '0')
            || !int.TryParse(prefixText, out prefix) || prefix > 32)
        {
            errors.Add(new(field, $"前缀 '{prefixText}' 必须是0至32的整数"));
            return false;
        }
        if (!IpMath.IsAligned(address, prefix))
        {
            errors.Add(new(field, $"{text} 未对齐网络边界（存在主机位）"));
            return false;
        }
        network = address;
        return true;
    }

    private static bool TryGetAddress(JsonElement obj, string property, string field, out uint address, List<FieldError> errors)
    {
        address = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var el))
        {
            errors.Add(new($"{field}.{property}", "缺少必填字段"));
            return false;
        }
        if (el.ValueKind != JsonValueKind.String || !IpMath.TryParseIPv4(el.GetString(), out address))
        {
            errors.Add(new($"{field}.{property}", "必须是合法的IPv4地址字符串"));
            return false;
        }
        return true;
    }
}

public static class Planner
{
    public static bool TryPlan(PlanRequest request, out PlanResponse? response, out AllocationFailure? failure)
    {
        response = null;
        failure = null;

        var parentStart = (ulong)request.ParentNetwork;
        var parentEnd = parentStart + IpMath.BlockSize(request.ParentPrefix) - 1;

        var blocked = new List<(ulong Start, ulong End)>();
        foreach (var (n, p) in request.Occupied)
            blocked.Add((n, (ulong)n + IpMath.BlockSize(p) - 1));
        foreach (var r in request.Reserved)
            blocked.Add((r.Start, r.End));
        var mergedBlocked = Merge(blocked);
        var occupiedCount = mergedBlocked.Aggregate(0UL, (acc, b) => acc + (b.End - b.Start + 1));

        var free = Subtract(new List<(ulong, ulong)> { (parentStart, parentEnd) }, mergedBlocked);

        var ordered = request.Departments
            .Select(d => (Dept: d, Bits: BitsForHosts(d.Hosts)))
            .OrderByDescending(x => x.Bits)
            .ThenBy(x => x.Dept.Id, StringComparer.Ordinal)
            .ToList();

        var results = new List<DepartmentResult>();
        ulong allocatedCount = 0;
        foreach (var (dept, bits) in ordered)
        {
            var size = 1UL << bits;
            var prefix = 32 - bits;
            if (!TryAllocate(free, size, out var at))
            {
                failure = new AllocationFailure(dept.Id,
                    $"部门 '{dept.Id}' 需要 {size} 个地址（/{prefix}），剩余空间中不存在足够大且对齐的连续块");
                return false;
            }
            free = Subtract(free, new List<(ulong, ulong)> { (at, at + size - 1) });
            allocatedCount += size;
            var network = (uint)at;
            var broadcast = (uint)(at + size - 1);
            results.Add(new DepartmentResult(
                dept.Id,
                $"{IpMath.Format(network)}/{prefix}",
                IpMath.Format(network),
                IpMath.Format(broadcast),
                IpMath.Format(network + 1),
                IpMath.Format(broadcast - 1),
                (long)size - 2));
        }

        var remainingCidrs = free.SelectMany(ToCidrs).ToList();
        var total = IpMath.BlockSize(request.ParentPrefix);
        response = new PlanResponse(
            results.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            remainingCidrs,
            total, occupiedCount, allocatedCount, total - occupiedCount - allocatedCount);
        return true;
    }

    private static int BitsForHosts(long hosts)
    {
        var needed = (ulong)hosts + 2;
        var bits = 0;
        while ((1UL << bits) < needed) bits++;
        return bits;
    }

    private static bool TryAllocate(List<(ulong Start, ulong End)> free, ulong size, out ulong at)
    {
        at = 0;
        var found = false;
        var best = ulong.MaxValue;
        foreach (var (start, end) in free)
        {
            if (end - start + 1 < size) continue;
            var aligned = AlignUp(start, size);
            if (aligned + size - 1 <= end && aligned < best)
            {
                best = aligned;
                found = true;
            }
        }
        if (!found) return false;
        at = best;
        return true;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var rem = value % alignment;
        return rem == 0 ? value : value + alignment - rem;
    }

    private static List<(ulong Start, ulong End)> Merge(List<(ulong Start, ulong End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
        var merged = new List<(ulong, ulong)>();
        foreach (var (s, e) in sorted)
        {
            if (merged.Count > 0 && s <= merged[^1].Item2 + 1)
            {
                if (e > merged[^1].Item2) merged[^1] = (merged[^1].Item1, e);
            }
            else merged.Add((s, e));
        }
        return merged;
    }

    private static List<(ulong Start, ulong End)> Subtract(
        List<(ulong Start, ulong End)> source, List<(ulong Start, ulong End)> cuts)
    {
        var result = source;
        foreach (var (cs, ce) in cuts)
        {
            var next = new List<(ulong, ulong)>();
            foreach (var (s, e) in result)
            {
                if (ce < s || cs > e) { next.Add((s, e)); continue; }
                if (cs > s) next.Add((s, cs - 1));
                if (ce < e) next.Add((ce + 1, e));
            }
            result = next;
        }
        return result;
    }

    private static IEnumerable<string> ToCidrs((ulong Start, ulong End) range)
    {
        var current = range.Start;
        while (current <= range.End)
        {
            var remaining = range.End - current + 1;
            var maxAlignBits = current == 0 ? 64 : (int)ulong.TrailingZeroCount(current);
            var bits = 0;
            while ((1UL << (bits + 1)) <= remaining && bits + 1 <= maxAlignBits) bits++;
            var prefix = 32 - bits;
            yield return $"{IpMath.Format((uint)current)}/{prefix}";
            current += 1UL << bits;
        }
    }
}
