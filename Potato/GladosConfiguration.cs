internal static class GladosConfiguration
{
    private const string DefaultEndpoint = "http://localhost:11434/v1";

    public static Uri GetEndpoint()
    {
        string endpoint = Environment.GetEnvironmentVariable("GLADOS_OPENAI_ENDPOINT") ?? DefaultEndpoint;
        return new Uri(endpoint.TrimEnd('/') + "/");
    }
}
