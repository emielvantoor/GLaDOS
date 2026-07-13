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
}

