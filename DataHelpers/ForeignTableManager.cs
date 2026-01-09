using Microsoft.Extensions.Configuration;
using Dapper;
using Npgsql;

namespace MDR_Importer;

public class ForeignTableManager
{
    private readonly string _db_conn;
    private readonly ILoggingHelper _logging_helper;
    private readonly IConfiguration _config;


    public ForeignTableManager(Source source, ILoggingHelper logging_helper,  IConfiguration config)
    {
        _db_conn = source.db_conn ?? "";
        _logging_helper = logging_helper;
        _config = config;
    }
    
    public void EstablishForeignMonTables(ICredentials creds)
    {
        if (string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
            return;

        // Read from appsettings.json (top-level keys)
        // Fallbacks are optional but handy.
        var fdwHost = _config["host"] ?? throw new InvalidOperationException("Missing 'host' in appsettings.json");
        var fdwPort = _config.GetValue<int?>("port") ?? throw new InvalidOperationException("Missing 'port' in appsettings.json");

        // Build SQL literals once (FDW OPTIONS require string literals, not parameters)
        var hostLit = SqlLiteral(fdwHost);
        var portLit = SqlLiteral(fdwPort.ToString());
        
        using var conn = new NpgsqlConnection(_db_conn);
        conn.Open();

        _logging_helper.LogLine($"Connected to: {conn.Database} @ {conn.Host}:{conn.Port} as {conn.UserName}");

        conn.Execute(@"CREATE SCHEMA IF NOT EXISTS sd;");
        conn.Execute(@"CREATE EXTENSION IF NOT EXISTS postgres_fdw WITH SCHEMA sd;");

        
        conn.Execute($@"
            CREATE SERVER IF NOT EXISTS mon
            FOREIGN DATA WRAPPER postgres_fdw
            OPTIONS (host {hostLit}, dbname 'mon', port {portLit});
        ");

        // Option B: always enforce correct host/port (as string literals!)
        conn.Execute($@"
            ALTER SERVER mon OPTIONS (
              SET host {hostLit},
              SET dbname 'mon',
              SET port {portLit}
            );
        ");

        // FDW mapping: also must be string literals (not Dapper params)
        var uLit = SqlLiteral(creds.Username);
        var pLit = SqlLiteral(creds.Password);

        conn.Execute($@"
            CREATE USER MAPPING IF NOT EXISTS FOR CURRENT_USER
            SERVER mon
            OPTIONS (user {uLit}, password {pLit});
        ");

        conn.Execute($@"
            ALTER USER MAPPING FOR CURRENT_USER
            SERVER mon
            OPTIONS (SET user {uLit}, SET password {pLit});
        ");

        conn.Execute($@"
            DROP SCHEMA IF EXISTS mon_sf CASCADE;
            CREATE SCHEMA mon_sf;
            IMPORT FOREIGN SCHEMA sf
            FROM SERVER mon
            INTO mon_sf;
        ");
        
        // Optional: log the stored FDW server options to confirm it’s using your config
        var opts = conn.ExecuteScalar<string>(@"
        SELECT array_to_string(srvoptions, ',')
        FROM pg_foreign_server
        WHERE srvname = 'mon';
    ");
        _logging_helper.LogLine($"FDW server 'mon' options now: {opts}");
    }


    public void DropForeignMonTables()
    {
        using var conn = new NpgsqlConnection(_db_conn);
        string sql_string = @"DROP USER MAPPING IF EXISTS FOR CURRENT_USER
                 SERVER mon;";
        conn.Execute(sql_string);

        sql_string = @"DROP SERVER IF EXISTS mon CASCADE;";
        conn.Execute(sql_string);

        sql_string = @"DROP SCHEMA IF EXISTS mon_sf;";
        conn.Execute(sql_string);
        
        _logging_helper.LogLine("Foreign (mon) tables removed from database");    
    }
    
    
    public void UpdateStudiesImportedDateInMon(int importId)
    {
        string top_string = @"Update mn.source_data src
                      set last_import_id = " + importId + @", 
                      last_imported = current_timestamp
                      from 
                         (select so.sd_sid 
                         FROM sd.studies so ";
        string base_string = @" ) s
                          where s.sd_sid = src.sd_sid;";

        UpdateLastImportedDate("studies", top_string, base_string);
    }

    
    public void UpdateObjectsImportedDateInMon(int importId)
    {
        string top_string = @"UPDATE mn.source_data src
                      set last_import_id = " + importId + @", 
                      last_imported = current_timestamp
                      from 
                         (select so.sd_oid 
                          FROM sd.data_objects so ";
        string base_string = @" ) s
                          where s.sd_oid = src.sd_oid;";

        UpdateLastImportedDate("data_objects", top_string, base_string);
    }


    private void UpdateLastImportedDate(string tableName, string topSql, string baseSql)
    {
        try
        {   
            using NpgsqlConnection conn = new(_db_conn);
            string feedbackA = "Updating monitor records with import date / time,  ";
            string sqlString = $"select count(*) from sd.{tableName}";
            int recCount  = conn.ExecuteScalar<int>(sqlString);
            int recBatch = 50000;
            if (recCount > recBatch)
            {
                for (int r = 1; r <= recCount; r += recBatch)
                {
                    sqlString = topSql + 
                                 " where so.id >= " + r + " and so.id < " + (r + recBatch)
                                 + baseSql;
                    conn.Execute(sqlString);
                    string feedback = feedbackA + r + " to ";
                    feedback += (r + recBatch < recCount) ? (r + recBatch - 1).ToString() : recCount.ToString();
                    _logging_helper.LogLine(feedback);
                }
            }
            else
            {
                sqlString = topSql + baseSql;
                conn.Execute(sqlString);
                _logging_helper.LogLine(feedbackA + recCount + " records, as a single query");
            }
        }
        catch (Exception e)
        {
            string res = e.Message;
            _logging_helper.LogError("In update last imported date (" + tableName + "): " + res);
        }
    }
    
    private static string SqlLiteral(string value)
    {
        // PostgreSQL string literal escaping: single-quote doubled
        // e.g. abc'def -> 'abc''def'
        return "'" + value.Replace("'", "''") + "'";
    }

}

