namespace Assistant.Host;

public static class HealthCheckCommand
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync("http://localhost:8080/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
