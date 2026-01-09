using Dapper;
using Npgsql;

namespace MDR_Importer;

public class ForeignTableManager
{
    private readonly string _db_conn;
    private readonly ILoggingHelper _logging_helper;

    public ForeignTableManager(Source source, ILoggingHelper logging_helper)
    {
        _db_conn = source.db_conn ?? "";
        _logging_helper = logging_helper;
    }

    public void EstablishForeignMonTables(ICredentials creds)
    {
        if (creds.Username is not null && creds.Password is not null)
        {
            using var conn = new NpgsqlConnection(_db_conn);
            
            // Explicit open so we can log accurate connection info (and fail early if needed)
            conn.Open();
            _logging_helper.LogLine(
                $"Connected to: {conn.Database} @ {conn.Host}:{conn.Port} as {conn.UserName}"
            );
            
            string sql_string = @"CREATE EXTENSION IF NOT EXISTS postgres_fdw
                                 schema sd;";
            conn.Execute(sql_string);

            sql_string = @"CREATE SERVER IF NOT EXISTS mon "
                         + @" FOREIGN DATA WRAPPER postgres_fdw
                        OPTIONS (host '172.22.173.218', dbname 'mon', port '5432');";
            conn.Execute(sql_string);

            sql_string = @"CREATE USER MAPPING IF NOT EXISTS FOR CURRENT_USER
                 SERVER mon 
                 OPTIONS (user '" + creds.Username + "', password '" + creds.Password + "');";
            conn.Execute(sql_string);

            sql_string = @"DROP SCHEMA IF EXISTS mon_sf cascade;
                 CREATE SCHEMA mon_sf; 
                 IMPORT FOREIGN SCHEMA sf
                 FROM SERVER mon 
                 INTO mon_sf;";
            conn.Execute(sql_string);
            
            _logging_helper.LogLine("Foreign (mon) tables established in database");
            _logging_helper.LogLine("");
        }
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
}

