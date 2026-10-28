using MySqlConnector;

namespace EasyTolet.Shared.Database;

public sealed class MySqlDatabaseOptions
{
    public string Server { get; set; } = "localhost";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string ServerVersion { get; set; } = "8.0.0";
    public MySqlSslMode SslMode { get; set; } = MySqlSslMode.VerifyFull;
    public string? SslCa { get; set; }
    public uint ConnectionTimeoutSeconds { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public uint MaximumPoolSize { get; set; } = 50;
}
