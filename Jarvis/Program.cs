using Jarvis.Core.Bootstrapper;
using Jarvis.Endpoints;
using Jarvis.LLama;
using Jarvis.LLama.Bootstrapper;
using Microsoft.AspNetCore.Http.HttpResults;

// Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT", "1");
// Environment.SetEnvironmentVariable("GGML_VK_DISABLE_GRAPH_OPTIMIZE", "1");

// Environment.SetEnvironmentVariable("HIP_VISIBLE_DEVICES", "1");
// Environment.SetEnvironmentVariable("HSA_OVERRIDE_GFX_VERSION", "11.0.0");

var builder = WebApplication.CreateSlimBuilder(args);

LLamaHardwareConfigurator.Configure(builder.Configuration);

builder.Services.AddSingleton(_ => 
    LLamaHardwareConfigurator.CreateOptimizedParameters(builder.Configuration));

builder.Services.AddCoreServices();
builder.Services.AddLLamaModels();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

Todo[] sampleTodos =
[
    new(1, "Walk the dog"),
    new(2, "Do the dishes", DateOnly.FromDateTime(DateTime.Now)),
    new(3, "Do the laundry", DateOnly.FromDateTime(DateTime.Now.AddDays(1))),
    new(4, "Clean the bathroom"),
    new(5, "Clean the car", DateOnly.FromDateTime(DateTime.Now.AddDays(2)))
];

var todosApi = app.MapGroup("/todos");
todosApi.MapGet("/", () => sampleTodos)
    .WithName("GetTodos");

todosApi.MapGet("/{id}", Results<Ok<Todo>, NotFound> (int id) =>
        sampleTodos.FirstOrDefault(a => a.Id == id) is { } todo
            ? TypedResults.Ok(todo)
            : TypedResults.NotFound())
    .WithName("GetTodoById");

app.UseStaticFiles();

app.MapOpenAiEndpoints();
app.Run("http://localhost:11434");

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);