var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Assistant.Host placeholder — replaced in Task 5.");
await app.RunAsync();

public partial class Program;
