using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DotNetEnv;

namespace ICOT.Core.Extensions;

public static class PlatformDbExtensions
{
    /// <summary>
    /// Configures the DbContext with PostgreSQL using environment variables from .env
    /// </summary>
    public static IServiceCollection AddPlatformDbContext<TContext>(this IServiceCollection services, string defaultDbName, string? envDbKey = null) 
        where TContext : DbContext
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ICOT.Core.Utils.Services.IUserContextProvider, ICOT.Core.Utils.Services.HttpContextUserContextProvider>();
        services.AddSingleton<ICOT.Core.Postgres.Interceptors.PlatformSaveChangesInterceptor>(sp =>
            new ICOT.Core.Postgres.Interceptors.PlatformSaveChangesInterceptor(sp.GetRequiredService<ICOT.Core.Utils.Services.IUserContextProvider>()));

        LoadNearestEnvFile();

        var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        var host = NormalizeContainerHost(GetTrimmedEnv("POSTGRES_HOST") ?? "localhost", inContainer);
        var parsedPort = GetNullableIntEnv("POSTGRES_PORT", 1, 65535) ?? 5432;
        var user = GetTrimmedEnv("POSTGRES_USER") ?? "postgres";
        var pass = GetTrimmedEnv("POSTGRES_PASS") ?? "postgres";
        var finalDbName = ResolveDatabaseName(defaultDbName, envDbKey);

        var rawMigrationHost = GetTrimmedEnv("POSTGRES_MIGRATION_HOST");
        var rawMigrationPort = GetNullableIntEnv("POSTGRES_MIGRATION_PORT", 1, 65535);
        var hasDedicatedMigrationHost = !string.IsNullOrEmpty(rawMigrationHost);
        var migrationHost = hasDedicatedMigrationHost
            ? NormalizeContainerHost(rawMigrationHost!, inContainer)
            : host;
        var migrationPort = rawMigrationPort
            ?? (hasDedicatedMigrationHost && !string.Equals(migrationHost, host, StringComparison.OrdinalIgnoreCase)
                ? 5432
                : parsedPort);

        var usesSeparateMigrationEndpoint =
            !string.Equals(migrationHost, host, StringComparison.OrdinalIgnoreCase) || migrationPort != parsedPort;

        var noResetOnClose = GetNullableBoolEnv("POSTGRES_NO_RESET_ON_CLOSE") ?? usesSeparateMigrationEndpoint;
        var minPoolSize = PlatformApiExtensions.GetIntEnv("POSTGRES_MIN_POOL_SIZE", 0, 0, 50);
        var maxPoolSize = PlatformApiExtensions.GetIntEnv("POSTGRES_MAX_POOL_SIZE", 15, Math.Max(1, minPoolSize), 200);
        var idleLifetime = PlatformApiExtensions.GetIntEnv("POSTGRES_CONNECTION_IDLE_LIFETIME", 60, 0, 3600);
        var connectionLifetime = PlatformApiExtensions.GetIntEnv("POSTGRES_CONNECTION_LIFETIME", 600, 0, 7200);

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = parsedPort,
            Database = finalDbName,
            Username = user,
            Password = pass,
            Pooling = true,
            MinPoolSize = minPoolSize,
            MaxPoolSize = maxPoolSize,
            ConnectionIdleLifetime = idleLifetime,
            ConnectionLifetime = connectionLifetime,
            ConnectionPruningInterval = 10,
            NoResetOnClose = noResetOnClose,
            KeepAlive = 15,
            Timeout = 15,
            CommandTimeout = 30
        }.ConnectionString;

        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        if (usesSeparateMigrationEndpoint)
        {
            services.TryAddSingleton(new PlatformMigrationConnectionSettings<TContext>(
                migrationHost,
                migrationPort,
                finalDbName,
                user,
                pass));
        }

        services.AddDbContextPool<TContext>((sp, options) =>
            ConfigurePlatformNpgsqlOptions(options, dataSource, sp),
            poolSize: 1024);

        return services;
    }

    /// <summary>
    /// Applies snake_case column naming across all entity properties in the ModelBuilder.
    /// </summary>
    public static void ApplySnakeCaseColumnNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder(input.Length + 8);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && input[i - 1] != '_' &&
                    (char.IsLower(input[i - 1]) || char.IsDigit(input[i - 1]) ||
                     (i + 1 < input.Length && char.IsLower(input[i + 1]))))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Automatically applies pending migrations or creates the database if it doesn't exist.
    /// When a dedicated migration endpoint (<c>POSTGRES_MIGRATION_HOST</c>/<c>POSTGRES_MIGRATION_PORT</c>) is configured,
    /// runs migrations directly against PostgreSQL with pooling disabled so session-level EF Core advisory locks bypass transaction poolers (e.g. PgBouncer).
    /// Catches connection errors gracefully to allow service startup even if DB is temporarily unreachable.
    /// </summary>
    public static async Task ApplyPlatformMigrationsAsync<TContext>(this IServiceProvider provider) where TContext : DbContext
    {
        try
        {
            using var scope = provider.CreateScope();
            var migrationSettings = scope.ServiceProvider.GetService<PlatformMigrationConnectionSettings<TContext>>();

            if (migrationSettings is not null)
            {
                var migrationConnectionString = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = migrationSettings.Host,
                    Port = migrationSettings.Port,
                    Database = migrationSettings.Database,
                    Username = migrationSettings.Username,
                    Password = migrationSettings.Password,
                    Pooling = false,
                    KeepAlive = 15,
                    Timeout = 15,
                    CommandTimeout = 60
                }.ConnectionString;

                var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(migrationConnectionString);
                dataSourceBuilder.EnableDynamicJson();
                await using var migrationDataSource = dataSourceBuilder.Build();

                var optionsBuilder = new DbContextOptionsBuilder<TContext>();
                ConfigurePlatformNpgsqlOptions(optionsBuilder, migrationDataSource, scope.ServiceProvider);

                await using var migrationContext = ActivatorUtilities.CreateInstance<TContext>(scope.ServiceProvider, optionsBuilder.Options);
                await migrationContext.Database.MigrateAsync();
                Console.WriteLine($"✅ Database migrations checked and applied for {typeof(TContext).Name} via {migrationSettings.Host}:{migrationSettings.Port}.");
                return;
            }

            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            await context.Database.MigrateAsync();
            Console.WriteLine($"✅ Database migrations checked and applied for {typeof(TContext).Name}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [Notice] Could not auto-apply migrations for {typeof(TContext).Name} ({ex.Message}). Service will continue startup.");
        }
    }

    private static void ConfigurePlatformNpgsqlOptions(DbContextOptionsBuilder options, Npgsql.NpgsqlDataSource dataSource, IServiceProvider sp)
    {
        options.UseNpgsql(dataSource, npgsqlOptions =>
        {
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
        });
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new SnakeCaseNamingOptionsExtension());
        options.AddInterceptors(sp.GetRequiredService<ICOT.Core.Postgres.Interceptors.PlatformSaveChangesInterceptor>());
        options.ConfigureWarnings(w =>
        {
            w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning,
                Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.RowLimitingOperationWithoutOrderByWarning);
            w.Log((Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.ConnectionError, Microsoft.Extensions.Logging.LogLevel.Warning));
        });
    }

    private static void LoadNearestEnvFile()
    {
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
    }

    private static string NormalizeContainerHost(string host, bool inContainer)
    {
        if (inContainer && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1"))
        {
            return "host.docker.internal";
        }
        return host;
    }

    private static string ResolveDatabaseName(string defaultDbName, string? envDbKey)
    {
        if (string.IsNullOrEmpty(envDbKey))
        {
            return defaultDbName;
        }

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
            var val = GetTrimmedEnv(k);
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        return defaultDbName;
    }

    private static string? GetTrimmedEnv(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key) ?? Env.GetString(key);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int? GetNullableIntEnv(string key, int minValue, int maxValue)
    {
        var raw = GetTrimmedEnv(key)?.Trim('"', '\'');
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minValue, maxValue)
            : null;
    }

    private static bool? GetNullableBoolEnv(string key)
    {
        var raw = GetTrimmedEnv(key)?.Trim('"', '\'');
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw switch
        {
            "1" => true,
            "0" => false,
            _ when raw.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            _ when raw.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            _ => null
        };
    }

    private sealed record PlatformMigrationConnectionSettings<TContext>(
        string Host,
        int Port,
        string Database,
        string Username,
        string Password) where TContext : DbContext;
}

internal sealed class SnakeCaseNamingOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, SnakeCaseConventionSetPlugin>());
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension) { }
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using snake_case column naming ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(System.Collections.Generic.IDictionary<string, string> debugInfo)
        {
            debugInfo["Platform:SnakeCaseColumns"] = "1";
        }
    }
}

internal sealed class SnakeCaseConventionSetPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelFinalizingConventions.Add(new SnakeCaseColumnFinalizingConvention());
        return conventionSet;
    }
}

internal sealed class SnakeCaseColumnFinalizingConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                property.Builder.HasColumnName(PlatformDbExtensions.ToSnakeCase(property.Name));
            }
        }
    }
}
