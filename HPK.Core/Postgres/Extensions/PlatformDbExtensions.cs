using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DotNetEnv;

namespace HPK.Core.Extensions;

public static class PlatformDbExtensions
{
    /// <summary>
    /// Configures the DbContext with PostgreSQL using environment variables from .env
    /// </summary>
    public static IServiceCollection AddPlatformDbContext<TContext>(this IServiceCollection services, string defaultDbName, string? envDbKey = null) 
        where TContext : DbContext
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<HPK.Core.Postgres.Interceptors.PlatformSaveChangesInterceptor>();
        // Load .env file (assuming it is in the root directory)
        var rootDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(rootDir, ".env")) && Directory.GetParent(rootDir) != null)
        {
            rootDir = Directory.GetParent(rootDir)!.FullName;
        }
        var envPath = Path.Combine(rootDir, ".env");
        if (File.Exists(envPath))
        {
            Env.Load(envPath);
            Env.TraversePath().Load();
        }

        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? Env.GetString("POSTGRES_HOST") ?? "localhost";
        var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        if (inContainer && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1"))
        {
            host = "host.docker.internal";
        }

        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? Env.GetString("POSTGRES_PORT") ?? "5432";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? Env.GetString("POSTGRES_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("POSTGRES_PASS") ?? Env.GetString("POSTGRES_PASS") ?? "postgres";
        
        var finalDbName = defaultDbName;
        if (!string.IsNullOrEmpty(envDbKey))
        {
            var cleanKey = envDbKey.Replace("POSTGRES_", "");
            var candidateKeys = new[]
            {
                envDbKey,
                cleanKey,
                $"POSTGRES_{cleanKey}",
                envDbKey.Replace("_SERVICE_", "SERVICE_"),
                cleanKey.Replace("_SERVICE_", "SERVICE_")
            };

            foreach (var k in candidateKeys)
            {
                var val = Environment.GetEnvironmentVariable(k) ?? Env.GetString(k);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    finalDbName = val.Trim();
                    break;
                }
            }
        }

        var connectionString = $"Host={host};Port={port};Database={finalDbName};Username={user};Password={pass};";

        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContextPool<TContext>((sp, options) =>
        {
            options.UseNpgsql(dataSource);
            options.AddInterceptors(sp.GetRequiredService<HPK.Core.Postgres.Interceptors.PlatformSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }, poolSize: 1024);

        return services;
    }

    /// <summary>
    /// Automatically applies pending migrations or creates the database if it doesn't exist.
    /// Catches connection errors gracefully to allow service startup even if DB is temporarily unreachable.
    /// </summary>
    public static async Task ApplyPlatformMigrationsAsync<TContext>(this IServiceProvider provider) where TContext : DbContext
    {
        try
        {
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            await context.Database.MigrateAsync();
            Console.WriteLine($"✅ Database migrations checked and applied for {typeof(TContext).Name}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [Notice] Could not auto-apply migrations for {typeof(TContext).Name} ({ex.Message}). Service will continue startup.");
        }
    }
}
