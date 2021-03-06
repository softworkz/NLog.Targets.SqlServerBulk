﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog.Common;
using NLog.Config;
using NLog.Targets.SqlServerBulk.Sql;

namespace NLog.Targets.SqlServerBulk
{
    /// <summary>
    /// Writes log messages to a SQL Server database, using the proprietary
    /// bulk insert API.
    ///
    /// This should result in higher throughput than the generic database
    /// target, providing that an AsyncWrapper or BufferingWrapper is used.
    ///
    /// [TODO: insert benchmarks here]
    ///
    /// This target tries reasonably hard to validate the data that
    /// is written, as any invalid rows will cause the entire batch to fail.
    /// </summary>
    [Target("SqlServerBulk")]
    public sealed class SqlServerBulkTarget : Target
    {
        private static readonly DateTime MIN_SQL_DATETIME = new DateTime(1753, 1, 1);
        private static readonly DateTime MIN_SQL_SMALLDATETIME = new DateTime(1900, 1, 1);
        private static readonly DateTime MAX_SQL_SMALLDATETIME = new DateTime(2079, 6, 6);

        private bool tableCreated;

        private IDatabase db;

        public SqlServerBulkTarget()
        {
            db = new SqlServerDatabase();
        }

        public SqlServerBulkTarget(IDatabase database)
        {
            db = database;
        }

        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        [RequiredParameter]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the connection string used for creating tables and
        /// adding columns. If not provided, regular ConnectionString is used.
        /// </summary>
        [DefaultParameter]
        public string DdlConnectionString { get; set; }

        /// <summary>
        /// Name of Schema. Will use 'dbo' schema if not set.
        /// </summary>
        [DefaultParameter]
        public string Schema { get; set; } = "dbo";

        /// <summary>
        /// Name of Table
        /// </summary>
        [RequiredParameter]
        public string Table { get; set; }

        /// <summary>
        /// Max number of rows in each bulk insert batch
        /// </summary>
        /// <value>The size of the batch.</value>
        [DefaultParameter]
        public int BatchSize { get; set; } = 5000;

        /// <summary>
        /// Whether or not to create the log table
        /// </summary>
        [DefaultParameter]
        public bool CreateTableIfNotExists { get; set; } = true;

        /// <summary>
        /// List of auto-generated columns. Useful for adding a primary key, UUID, or
        /// insertion timestamp.
        ///
        /// If datatype is INT or BIGINT, the column will be created as IDENTITY.
        /// If datatype is UNIQUEIDENTIFIER, the column will be created as DEFAULT(NEWID())
        /// If datatype is DATE, SMALLDATETIME, DATETIME, DATETIME2 or DATETIMEOFFSET, the
        /// column will be created as DEFAULT(GETUTCDATE())
        /// </summary>
        [ArrayParameter(typeof(GeneratedColumn), "generatedColumn")]
        public IList<GeneratedColumn> AutoGeneratedColumns { get; set; } = new List<GeneratedColumn>();

        /// <summary>
        /// List of columns used for logging.
        /// </summary>
        [ArrayParameter(typeof(LoggingColumn), "column")]
        public IList<LoggingColumn> LoggingColumns { get; set; } = new List<LoggingColumn>();

        /// <summary>
        /// Whether or not to allow multiple log entries per row. When true, then
        /// every column will contain multiple log entries, separated by a newline
        /// character.
        ///
        /// This is unlikely to be useful unless it is guaranteed there are no newline
        /// characters in any of the rendered columns.
        ///
        /// This option is intended for use with the JsonLayout, where there
        /// is only a single column (and no newlines).
        /// </summary>
        [DefaultParameter]
        public bool AllowMultipleLogEntriesPerRow { get; set; } = false;

        /// <summary>
        /// Create table and add columns, if necessary.
        /// </summary>
        public void CreateTable()
        {
            if (string.IsNullOrWhiteSpace(DdlConnectionString))
            {
                InternalLogger.Debug($"Setting DdlConnectionString to '{ConnectionString}'");
                DdlConnectionString = ConnectionString;
            }

            try
            {
                if (CreateTableIfNotExists)
                {
                    ExecuteCreateTableCommand(Schema, Table, AutoGeneratedColumns, LoggingColumns);
                }
                tableCreated = true;
            }
            catch (Exception e)
            {
                InternalLogger.Error(e.Message);
            }
        }

        internal void ExecuteCreateTableCommand(string schemaName,
                                                string tableName,
                                                IList<GeneratedColumn> generatedColumns,
                                                IList<LoggingColumn> loggingColumns)
        {
            var builder = new CreateTableCommandBuilder(schemaName, tableName);

            foreach (var c in generatedColumns)
                builder.AddGeneratedColumn(c);

            foreach (var c in loggingColumns)
                builder.AddLoggingColumn(c);


            using (var cmd = builder.BuildSqlCommand())
            {
                db.ExecuteSqlCommand(DdlConnectionString, cmd);
            }

        }

        private Type GetType(SqlType sqlType)
        {
            switch (sqlType)
            {
                case SqlType.VARCHAR:
                case SqlType.NVARCHAR:
                    return typeof(string);

                case SqlType.DECIMAL:
                case SqlType.NUMERIC:
                    return typeof(decimal);

                case SqlType.INT:
                    return typeof(int);

                case SqlType.BIGINT:
                    return typeof(long);

                case SqlType.UNIQUEIDENTIFIER:
                    return typeof(Guid);

                case SqlType.DATE:
                case SqlType.SMALLDATETIME:
                case SqlType.DATETIME:
                case SqlType.DATETIME2:
                    return typeof(DateTime);

                case SqlType.DATETIMEOFFSET:
                    return typeof(DateTimeOffset);

                default:
                    throw new Exception($"Unsupported SQL datatype '{sqlType}'");
            }
        }

        // TODO: test with different locales. Add format strings into the config?
        private object ConvertType(string strValue, Type type)
        {
            try
            {
                if (type == typeof(string))
                    return strValue;

                if (type == typeof(decimal))
                    return decimal.Parse(strValue);

                if (type == typeof(int))
                    return int.Parse(strValue);

                if (type == typeof(long))
                    return long.Parse(strValue);

                if (type == typeof(Guid))
                    return Guid.Parse(strValue);

                if (type == typeof(DateTime))
                    return DateTime.Parse(strValue);

                if (type == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(strValue);

                return DBNull.Value;
            }
            catch (Exception)
            {
                return DBNull.Value;
            }
        }

        /// <summary>
        /// Creates the data table. Ensure that the column types are correct.
        /// </summary>
        /// <returns>The data table.</returns>
        private DataTable CreateDataTable()
        {
            var dt = new DataTable();
            foreach (var c in LoggingColumns)
            {
                var name = c.Name;
                var sqlType = c.SqlType;
                var type = GetType(sqlType);
                dt.Columns.Add(name, type);
            }
            return dt;
        }

        private void AddRow(DataTable dataTable, LogEventInfo logEventInfo)
        {
            var row = dataTable.NewRow();

            foreach (var c in LoggingColumns)
            {
                var tableColumn = dataTable.Columns[c.Name];

                var strValue = RenderLogEvent(c.Layout, logEventInfo);

                var value = ConvertType(strValue, tableColumn.DataType);

                // Truncate string if necessary.
                if (value is string s)
                {
                    if (s == null)
                        value = DBNull.Value;
                    else if (c.Length > 0 && c.Length < s.Length)
                        value = s.Substring(0, c.Length);
                }

                // Ensure datetimes are within the range accepted by the database.
                // In particular, DATETIME must be greater than 1753-01-01 and
                // SMALLDATETIME must be between 1900-01-01 and 2079-06-06.
                // (Other SQL date types can handle the entire range of .NET datetimes)
                if (value is DateTime d)
                {
                    if ((c.SqlType == SqlType.DATETIME && d < MIN_SQL_DATETIME)
                          || (c.SqlType == SqlType.SMALLDATETIME && d < MIN_SQL_SMALLDATETIME)
                          || (c.SqlType == SqlType.SMALLDATETIME && d > MAX_SQL_SMALLDATETIME))
                        value = DBNull.Value;
                }

                row[c.Name] = value;
            }
            dataTable.Rows.Add(row);
        }

        protected override void Write(LogEventInfo logEvent)
        {
            if (!tableCreated)
                CreateTable();

            try
            {
                var dataTable = CreateDataTable();
                AddRow(dataTable, logEvent);
                db.ExecuteBulkInsert(ConnectionString, Schema, Table, BatchSize, dataTable);
            }
            catch (Exception)
            {
                throw;
            }
        }

        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            if (!tableCreated)
                CreateTable();

            var logEventBatch = new List<AsyncLogEventInfo>(BatchSize);

            foreach (var l in logEvents)
            {
                logEventBatch.Add(l);

                if (logEventBatch.Count >= BatchSize)
                {
                    WriteBatch(logEventBatch);
                    logEventBatch.Clear();
                }
            }
            if (logEventBatch.Any())
            {
                WriteBatch(logEventBatch);
            }
        }

        private void WriteBatch(IList<AsyncLogEventInfo> batch)
        {
            try
            {
                var dataTable = CreateDataTable();
                foreach (var l in batch)
                    AddRow(dataTable, l.LogEvent);

                db.ExecuteBulkInsert(ConnectionString, Schema, Table, BatchSize, dataTable);

                foreach (var l in batch)
                    l.Continuation(null);
            }
            catch (Exception ex)
            {
                foreach (var l in batch)
                    l.Continuation(ex);
            }
        }
	}
}
