using GLaDOS.Models;
using GLaDOS.Services;
using Microsoft.AspNetCore.Mvc;

namespace GLaDOS.Endpoints;

public static class PotatoEndpoints
{
    public static void MapPotatoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/potato");

        group.MapGet("/sessions", GetSessions);
        group.MapGet("/sessions/{id}", GetSession);
        group.MapPost("/sessions", StartSession);
        group.MapPost("/sessions/events", AddEvent);
        group.MapPost("/sessions/{id}/input", AddInput);
        group.MapPost("/sessions/{id}/completions", GetCompletions);
        group.MapGet("/sessions/input/next", GetNextInput);
    }

    private static IResult GetSessions([FromServices] PotatoSessionStore store) =>
        Results.Ok(new { data = store.GetActiveSessions() });

    private static IResult GetSession([FromServices] PotatoSessionStore store, string id)
    {
        PotatoSessionDetail? session = store.GetSession(id);
        return session is null ? Results.NotFound() : Results.Ok(session);
    }

    private static IResult StartSession(
        [FromServices] PotatoSessionStore store,
        [FromBody] PotatoSessionStartRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return Results.BadRequest(new { error = "WorkingDirectory is required." });
        }

        return Results.Ok(store.StartSession(request));
    }

    private static IResult AddEvent(
        [FromServices] PotatoSessionStore store,
        [FromBody] PotatoSessionEventRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return Results.BadRequest(new { error = "WorkingDirectory is required." });
        }

        return Results.Ok(store.AddEvent(request));
    }

    private static IResult AddInput(
        [FromServices] PotatoSessionStore store,
        string id,
        [FromBody] PotatoSessionInputRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "Content is required." });
        }

        return store.EnqueueInput(id, request.Content)
            ? Results.Accepted()
            : Results.NotFound(new { error = "Potato session not found." });
    }

    private static IResult GetNextInput(
        [FromServices] PotatoSessionStore store,
        [FromQuery] string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Results.BadRequest(new { error = "WorkingDirectory is required." });
        }

        string? input = store.DequeueInput(workingDirectory);
        return input is null ? Results.NoContent() : Results.Ok(new { content = input });
    }

    private static IResult GetCompletions(
        [FromServices] PotatoSessionStore store,
        string id,
        [FromBody] PotatoSessionCompletionRequest? request)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        IReadOnlyList<PotatoSessionCompletion>? completions = store.GetCompletions(
            id,
            request.Content ?? string.Empty,
            request.CursorIndex);

        return completions is null
            ? Results.NotFound(new { error = "Potato session not found." })
            : Results.Ok(new { data = completions });
    }
}
