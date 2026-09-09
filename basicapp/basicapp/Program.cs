using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy());

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

/* AWS
 *Do not use UseHttpsRedirection() inside the container —
 * configure ForwardedHeaders so the app trusts the ALB's X-Forwarded-Proto/X-Forwarded-For instead.
 * 
 */

//app.UseHttpsRedirection();


/*app.MapGet("/", () =>
    Results.Content(
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>basicapp</title>
        </head>
        <body>
            <h1>basicapp is running</h1>
            <p>Try the sample API endpoint:</p>
            <ul>
                <li><a href="/weatherforecast">/weatherforecast</a></li>
                <li><a href="/openapi/v1.json">/openapi/v1.json</a></li>
            </ul>
        </body>
        </html>
        """,
        "text/html"));*/

string[] summaries =
[
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
];

app.MapHealthChecks("/health", new HealthCheckOptions()
{
    Predicate = _ => false // liveness: is the process up?
});
app.MapHealthChecks("/ready"); // readiness: are dependencies up?

app.MapGet("/weatherforecast", () =>
    {
        WeatherForecast[] forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}