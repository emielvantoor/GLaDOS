using System.Text.Json.Nodes;
using GLaDOS.Core.Routing;
using Microsoft.AspNetCore.Mvc;

namespace GLaDOS.Endpoints;

public static class ToolEndpoints
{
    public static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var v1Group = app.MapGroup("/v1");

        v1Group.MapGet("/tools", GetTools);
        v1Group.MapPost("/tools/execute", ExecuteApprovedTool);
    }

    private static IResult GetTools([FromServices] ToolRegistry toolRegistry)
    {
        var tools = toolRegistry.GetDefinitions()
            .Select(tool => new
            {
                type = "function",
                source = "Internal",
                permitted = tool.Permitted,
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters
                }
            });

        return Results.Ok(new { data = tools });
    }

    private static async Task<IResult> ExecuteApprovedTool(
        [FromServices] ToolRegistry toolRegistry,
        [FromBody] ToolExecutionRequest? request,
        HttpContext context)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "Tool name is required.", type = "invalid_request_error" } });
        }

        if (!toolRegistry.TryGetInternalTool(request.Name, out var tool))
        {
            return Results.NotFound(new { error = new { message = $"Tool '{request.Name}' not found.", type = "not_found_error" } });
        }

        try
        {
            var output = await tool.ExecuteAsync(request.Arguments ?? new JsonObject());
            return Results.Ok(new ToolExecutionResponse(request.Name, output));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: $"Failed to execute tool '{request.Name}': {ex.Message}", statusCode: 500);
        }
    }

    private sealed record ToolExecutionRequest(string Name, JsonObject? Arguments);

    private sealed record ToolExecutionResponse(string Name, string Output);
}
