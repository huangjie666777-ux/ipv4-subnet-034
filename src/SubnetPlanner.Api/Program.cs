using System.Text.Json;
using SubnetPlanner.Api;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/plan", async (HttpContext http) =>
{
    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(http.Request.Body);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { errors = new[] { new { field = "$", message = $"JSON解析失败: {ex.Message}" } } });
    }

    using (doc)
    {
        if (!PlanRequestParser.TryParse(doc.RootElement, out var request, out var errors))
            return Results.BadRequest(new { errors = errors.Select(e => new { field = e.Field, message = e.Message }) });

        if (!Planner.TryPlan(request!, out var response, out var failure))
            return Results.UnprocessableEntity(new
            {
                failure = new { departmentId = failure!.DepartmentId, reason = failure.Reason }
            });

        return Results.Ok(new
        {
            departments = response!.Departments.Select(d => new
            {
                id = d.Id,
                cidr = d.Cidr,
                network = d.Network,
                broadcast = d.Broadcast,
                firstUsable = d.FirstUsable,
                lastUsable = d.LastUsable,
                usableCapacity = d.UsableCapacity
            }),
            remainingCidrs = response.RemainingCidrs,
            summary = new
            {
                totalAddresses = response.TotalAddresses,
                occupiedAddresses = response.OccupiedAddresses,
                allocatedAddresses = response.AllocatedAddresses,
                remainingAddresses = response.RemainingAddresses
            }
        });
    }
});

app.Run();
public partial class Program { }
