using LekhaBook.Data.Postgres;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// PostgreSQL. In development the connection string comes from user secrets (README).
var databaseConfigured = builder.Configuration.GetConnectionString("LekhaBook") is { Length: > 0 };
if (databaseConfigured)
{
    builder.Services.AddLekhaBookPostgres(builder.Configuration.GetConnectionString("LekhaBook")!);
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // OpenAPI document at /openapi/v1.json
    app.MapOpenApi();

    // Development convenience: bring the local database up to date on start.
    // Production applies migrations as a deployment step instead.
    if (databaseConfigured && app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PostgresDbContext>().Database.Migrate();
    }
}

app.UseExceptionHandler();
app.UseHttpsRedirection();

app.MapHealthChecks("/health");

app.Run();

// Lets the integration tests start the API with WebApplicationFactory<Program>.
public partial class Program;
