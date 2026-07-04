using GLaDOS.Endpoints;
using GLaDOS.Core.Bootstrapper;
using GLaDOS.LLama;
using GLaDOS.LLama.Bootstrapper;

// Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT", "1");
// Environment.SetEnvironmentVariable("GGML_VK_DISABLE_GRAPH_OPTIMIZE", "1");

// Environment.SetEnvironmentVariable("HIP_VISIBLE_DEVICES", "1");
// Environment.SetEnvironmentVariable("HSA_OVERRIDE_GFX_VERSION", "11.0.0");

var builder = WebApplication.CreateSlimBuilder(args);

LLamaHardwareConfigurator.Configure(builder.Configuration);

builder.Services.AddCoreServices();
builder.Services.AddLLamaModels(builder.Configuration);

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

app.UseStaticFiles();

app.MapOpenAiEndpoints();
app.Run("http://localhost:11434");