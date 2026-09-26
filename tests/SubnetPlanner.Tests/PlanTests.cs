using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SubnetPlanner.Tests;

public class PlanTests
{
    private static HttpClient CreateClient() => new WebApplicationFactory<Program>().CreateClient();

    private static async Task<JsonElement> Post(HttpClient client, object body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync("/plan", body);
        Assert.Equal(expected, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json;
    }

    [Fact]
    public async Task AllocatesDepartmentsAndReportsRemaining()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.0.0.0/24",
            occupiedSubnets = new[] { "10.0.0.0/26" },
            reservedRanges = new[] { new { start = "10.0.0.128", end = "10.0.0.143" } },
            departments = new[]
            {
                new { id = "eng", hosts = 50 },
                new { id = "ops", hosts = 10 },
                new { id = "lab", hosts = 2 }
            }
        }, HttpStatusCode.OK);

        var depts = json.GetProperty("departments");
        Assert.Equal(3, depts.GetArrayLength());
        var eng = depts.EnumerateArray().Single(d => d.GetProperty("id").GetString() == "eng");
        Assert.Equal("10.0.0.64/26", eng.GetProperty("cidr").GetString());
        Assert.Equal("10.0.0.64", eng.GetProperty("network").GetString());
        Assert.Equal("10.0.0.127", eng.GetProperty("broadcast").GetString());
        Assert.Equal("10.0.0.65", eng.GetProperty("firstUsable").GetString());
        Assert.Equal("10.0.0.126", eng.GetProperty("lastUsable").GetString());
        Assert.Equal(62, eng.GetProperty("usableCapacity").GetInt64());

        var ops = depts.EnumerateArray().Single(d => d.GetProperty("id").GetString() == "ops");
        Assert.Equal("10.0.0.144/28", ops.GetProperty("cidr").GetString());
        var lab = depts.EnumerateArray().Single(d => d.GetProperty("id").GetString() == "lab");
        Assert.Equal("10.0.0.160/30", lab.GetProperty("cidr").GetString());

        var summary = json.GetProperty("summary");
        Assert.Equal(256UL, summary.GetProperty("totalAddresses").GetUInt64());
        Assert.Equal(80UL, summary.GetProperty("occupiedAddresses").GetUInt64());
        Assert.Equal(84UL, summary.GetProperty("allocatedAddresses").GetUInt64());
        Assert.Equal(92UL, summary.GetProperty("remainingAddresses").GetUInt64());

        var remaining = json.GetProperty("remainingCidrs").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "10.0.0.164/30", "10.0.0.168/29", "10.0.0.176/28", "10.0.0.192/26" }, remaining);
    }

    [Fact]
    public async Task DepartmentOrderDoesNotChangeResult()
    {
        using var client = CreateClient();
        var mk = (object[] depts) => new
        {
            parentCidr = "192.168.0.0/24",
            occupiedSubnets = Array.Empty<string>(),
            reservedRanges = Array.Empty<object>(),
            departments = depts
        };
        var a = new object[] { new { id = "a", hosts = 100 }, new { id = "b", hosts = 25 }, new { id = "c", hosts = 25 } };
        var b = new object[] { new { id = "c", hosts = 25 }, new { id = "a", hosts = 100 }, new { id = "b", hosts = 25 } };
        var r1 = await Post(client, mk(a), HttpStatusCode.OK);
        var r2 = await Post(client, mk(b), HttpStatusCode.OK);
        Assert.Equal(r1.GetRawText(), r2.GetRawText());
    }

    [Fact]
    public async Task ReservedHoleForcesAllocationAroundIt()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.1.0.0/24",
            reservedRanges = new[] { new { start = "10.1.0.64", end = "10.1.0.127" } },
            departments = new[] { new { id = "big", hosts = 100 } }
        }, HttpStatusCode.OK);
        var dept = json.GetProperty("departments")[0];
        Assert.Equal("10.1.0.128/25", dept.GetProperty("cidr").GetString());
        var remaining = json.GetProperty("remainingCidrs").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "10.1.0.0/26" }, remaining);
    }

    [Fact]
    public async Task InsufficientCapacityReturns422WithDepartment()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.2.0.0/25",
            departments = new[]
            {
                new { id = "first", hosts = 100 },
                new { id = "second", hosts = 60 }
            }
        }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("second", json.GetProperty("failure").GetProperty("departmentId").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("failure").GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task FragmentedSpaceCannotBePiecedTogether()
    {
        using var client = CreateClient();
        // 两个/26空洞夹住占用段，剩余碎片各自不足以容纳/25
        var json = await Post(client, new
        {
            parentCidr = "10.3.0.0/24",
            occupiedSubnets = new[] { "10.3.0.64/26", "10.3.0.192/26" },
            departments = new[] { new { id = "needHalf", hosts = 120 } }
        }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("needHalf", json.GetProperty("failure").GetProperty("departmentId").GetString());
    }

    [Theory]
    [InlineData("10.0.0.1/24", "parentCidr")]       // 主机位未清零
    [InlineData("10.0.0.0/33", "parentCidr")]       // 非法前缀
    [InlineData("10.0.0.300/24", "parentCidr")]     // 越界地址
    [InlineData("10.0.0/24", "parentCidr")]         // 非四段
    public async Task InvalidParentCidrRejected(string cidr, string field)
    {
        using var client = CreateClient();
        var json = await Post(client, new { parentCidr = cidr, departments = Array.Empty<object>() }, HttpStatusCode.BadRequest);
        Assert.Contains(json.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == field);
    }

    [Fact]
    public async Task DuplicateDepartmentIdRejected()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.0.0.0/24",
            departments = new[] { new { id = "x", hosts = 1 }, new { id = "x", hosts = 2 } }
        }, HttpStatusCode.BadRequest);
        Assert.Contains(json.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("field").GetString() == "departments[1].id");
    }

    [Fact]
    public async Task ReversedRangeRejected()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.0.0.0/24",
            reservedRanges = new[] { new { start = "10.0.0.20", end = "10.0.0.10" } },
            departments = Array.Empty<object>()
        }, HttpStatusCode.BadRequest);
        Assert.Contains(json.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("field").GetString() == "reservedRanges[0].end");
    }

    [Fact]
    public async Task OccupiedOutsideParentRejected()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.0.0.0/25",
            occupiedSubnets = new[] { "10.0.0.128/25" },
            departments = Array.Empty<object>()
        }, HttpStatusCode.BadRequest);
        Assert.Contains(json.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("field").GetString() == "occupiedSubnets");
    }

    [Fact]
    public async Task NonPositiveHostsRejected()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "10.0.0.0/24",
            departments = new[] { new { id = "bad", hosts = 0 } }
        }, HttpStatusCode.BadRequest);
        Assert.Contains(json.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("field").GetString() == "departments[0].hosts");
    }

    [Fact]
    public async Task SlashZeroParentSupported()
    {
        using var client = CreateClient();
        var json = await Post(client, new
        {
            parentCidr = "0.0.0.0/0",
            departments = new[] { new { id = "huge", hosts = 1000000 } }
        }, HttpStatusCode.OK);
        var summary = json.GetProperty("summary");
        Assert.Equal(4294967296UL, summary.GetProperty("totalAddresses").GetUInt64());
        var dept = json.GetProperty("departments")[0];
        Assert.Equal("0.0.0.0/12", dept.GetProperty("cidr").GetString());
        Assert.Equal(1048574, dept.GetProperty("usableCapacity").GetInt64());
    }
}
