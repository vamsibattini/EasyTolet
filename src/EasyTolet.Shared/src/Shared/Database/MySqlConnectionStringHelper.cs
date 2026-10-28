using MySqlConnector;

namespace EasyTolet.Shared.Database;

/// <summary>Builds one service's connection string. Never log the returned value.</summary>
public static class MySqlConnectionStringHelper
{
    public static string Build(MySqlDatabaseOptions options, bool isDevelopment,
        string? connectionStringOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CommandTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Database command timeout must be between 1 and 600 seconds.");

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = string.IsNullOrWhiteSpace(connectionStringOverride)
                ? new MySqlConnectionStringBuilder
                {
                    Server = options.Server, Port = options.Port, Database = options.Database,
                    UserID = options.User, Password = options.Password, SslMode = options.SslMode,
                    SslCa = options.SslCa ?? "", ConnectionTimeout = options.ConnectionTimeoutSeconds,
                    MaximumPoolSize = options.MaximumPoolSize
                }
                : new MySqlConnectionStringBuilder(connectionStringOverride);
        }
        catch (ArgumentException)
        {
            // Do not include the input or provider exception, which may contain credentials.
            throw new InvalidOperationException("Invalid MySQL connection configuration.");
        }

        if (string.IsNullOrWhiteSpace(builder.Server) || string.IsNullOrWhiteSpace(builder.Database) ||
            string.IsNullOrWhiteSpace(builder.UserID) || string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException("Configure the database server, database, user and password through a secret provider or environment variables.");
        if (builder.Port is 0 or > 65535 || builder.ConnectionTimeout is 0 or > 120 ||
            builder.MaximumPoolSize is 0 or > 1000 || builder.MinimumPoolSize > builder.MaximumPoolSize)
            throw new InvalidOperationException("Invalid database port, connection timeout or pool size.");
        if (!isDevelopment && (builder.SslMode != MySqlSslMode.VerifyFull ||
            builder.UserID.Equals("root", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Outside Development, MySQL requires VerifyFull TLS and a dedicated service user instead of root.");

        builder.PersistSecurityInfo = false;
        builder.AllowLoadLocalInfile = false;
        builder.AllowPublicKeyRetrieval = false;
        builder.AllowUserVariables = false;
        builder.ConnectionReset = true;
        builder.Pooling = true;
        builder.DefaultCommandTimeout = (uint)options.CommandTimeoutSeconds;
        return builder.ConnectionString;
    }
}
