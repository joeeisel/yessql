using Dapper;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Common;
using YesSql.Sql.Schema;

namespace YesSql.Sql
{
    public class SchemaBuilder : ISchemaBuilder
    {
        private readonly ICommandInterpreter _commandInterpreter;
        private readonly ILogger _logger;

        public string TablePrefix { get; private set; }
        public ISqlDialect Dialect { get; private set; }
        public ITableNameConvention TableNameConvention { get; private set; }
        public DbConnection Connection { get; private set; }
        public DbTransaction Transaction { get; private set; }
        public bool ThrowOnError { get; private set; }
        public IdentityColumnSize IdentityColumnSize { get; set; }

        public SchemaBuilder(IConfiguration configuration, DbTransaction transaction, bool throwOnError = true)
        {
            Transaction = transaction;
            _logger = configuration.Logger;
            Connection = Transaction.Connection;
            _commandInterpreter = configuration.CommandInterpreter;
            Dialect = configuration.SqlDialect;
            TablePrefix = configuration.TablePrefix;
            ThrowOnError = throwOnError;
            TableNameConvention = configuration.TableNameConvention;
            IdentityColumnSize = configuration.IdentityColumnSize;
        }

        private void Execute(IEnumerable<string> statements)
        {
            foreach (var statement in statements)
            {
                if (string.IsNullOrEmpty(statement))
                {
                    continue;
                }

                _logger.LogTrace(statement);
                Connection.Execute(statement, null, Transaction);
            }
        }

        private string Prefix(string table)
        {
            return TablePrefix + table;
        }

        public ISchemaBuilder CreateMapIndexTable(Type indexType, Action<ICreateTableCommand> table, string collection)
        {
            try
            {
                var indexName = indexType.Name;
                var indexTable = TableNameConvention.GetIndexTable(indexType, collection); 
                var createTable = new CreateTableCommand(Prefix(indexTable));
                var documentTable = TableNameConvention.GetDocumentTable(collection);

                // NB: Identity() implies PrimaryKey()

                createTable
                    .Column(IdentityColumnSize, "Id", column => column.Identity().NotNull())
                    .Column(IdentityColumnSize, "DocumentId")
                    ;

                table(createTable);
                Execute(_commandInterpreter.CreateSql(createTable));

                CreateForeignKey("FK_" + (collection ?? "") + indexName, indexTable, new[] { "DocumentId" }, documentTable, new[] { "Id" });

                AlterTable(indexTable, table =>
                    table.CreateIndex($"IDX_FK_{indexTable}", "DocumentId")
                    );
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder CreateReduceIndexTable(Type indexType, Action<ICreateTableCommand> table, string collection = null)
        {
            try
            {
                var indexName = indexType.Name;
                var indexTable = TableNameConvention.GetIndexTable(indexType, collection);
                var createTable = new CreateTableCommand(Prefix(indexTable));
                var documentTable = TableNameConvention.GetDocumentTable(collection);

                // NB: Identity() implies PrimaryKey()

                createTable.Column(IdentityColumnSize, "Id", column => column.Identity().NotNull())
                    ;

                table(createTable);
                Execute(_commandInterpreter.CreateSql(createTable));

                var bridgeTableName = indexTable + "_" + documentTable;

                CreateTable(bridgeTableName, bridge => bridge
                    .Column(IdentityColumnSize, indexName + "Id", column => column.NotNull())
                    .Column(IdentityColumnSize, "DocumentId", column => column.NotNull())
                );

                CreateForeignKey("FK_" + bridgeTableName + "_Id", bridgeTableName, new[] { indexName + "Id" }, indexTable, new[] { "Id" });
                CreateForeignKey("FK_" + bridgeTableName + "_DocumentId", bridgeTableName, new[] { "DocumentId" }, documentTable, new[] { "Id" });

                AlterTable(bridgeTableName, table =>
                    table.CreateIndex($"IDX_FK_{bridgeTableName}", indexName + "Id", "DocumentId")
                    );
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder DropReduceIndexTable(Type indexType, string collection = null)
        {
            try
            {
                var indexTable = TableNameConvention.GetIndexTable(indexType, collection);
                var documentTable = TableNameConvention.GetDocumentTable(collection);

                var bridgeTableName = indexTable + "_" + documentTable;

                if (string.IsNullOrEmpty(Dialect.CascadeConstraintsString))
                {
                    DropForeignKey(bridgeTableName, "FK_" + bridgeTableName + "_Id");
                    DropForeignKey(bridgeTableName, "FK_" + bridgeTableName + "_DocumentId");
                }

                DropTable(bridgeTableName);
                DropTable(indexTable);
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder DropMapIndexTable(Type indexType, string collection = null)
        {
            try
            {
                var indexName = indexType.Name;
                var indexTable = TableNameConvention.GetIndexTable(indexType, collection);

                if (string.IsNullOrEmpty(Dialect.CascadeConstraintsString))
                {
                    DropForeignKey(indexTable, "FK_" + (collection ?? "") + indexName);
                }

                DropTable(indexTable);
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder CreateTable(string name, Action<ICreateTableCommand> table)
        {
            try
            {
                var createTable = new CreateTableCommand(Prefix(name));
                table(createTable);
                Execute(_commandInterpreter.CreateSql(createTable));
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder AlterTable(string name, Action<IAlterTableCommand> table)
        {
            try
            {
                var alterTable = new AlterTableCommand(Prefix(name), Dialect, TablePrefix);
                table(alterTable);
                Execute(_commandInterpreter.CreateSql(alterTable));
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder AlterIndexTable(Type indexType, Action<IAlterTableCommand> table, string collection)
        {
            var indexTable = TableNameConvention.GetIndexTable(indexType, collection);
            AlterTable(indexTable, table);
            
            return this;
        }

        public ISchemaBuilder DropTable(string name)
        {
            try
            {
                var deleteTable = new DropTableCommand(Prefix(name));
                Execute(_commandInterpreter.CreateSql(deleteTable));
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder CreateForeignKey(string name, string srcTable, string[] srcColumns, string destTable, string[] destColumns)
        {
            try
            {
                var command = new CreateForeignKeyCommand(Dialect.FormatKeyName(Prefix(name)), Prefix(srcTable), srcColumns, Prefix(destTable), destColumns);
                var sql = _commandInterpreter.CreateSql(command);
                Execute(sql);
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder DropForeignKey(string srcTable, string name)
        {
            try
            {
                var command = new DropForeignKeyCommand(Dialect.FormatKeyName(Prefix(srcTable)), Prefix(name));
                Execute(_commandInterpreter.CreateSql(command));
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }

        public ISchemaBuilder CreateSchema(string schema)
        {
            try
            {
                var createSchema = new CreateSchemaCommand(schema);
                Execute(_commandInterpreter.CreateSql(createSchema));
            }
            catch
            {
                if (ThrowOnError)
                {
                    throw;
                }
            }

            return this;
        }
    }
}
