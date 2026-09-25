using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SubnetPlanner.Tests;
public class HealthTests
{
    [Fact]
    public async Task HealthEndpointResponds()
    {
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }
}
