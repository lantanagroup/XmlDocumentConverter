﻿using ADOX;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Xml;
using System.Data.Common;
using System.Linq;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.InkML;
using DocumentFormat.OpenXml.Math;
using Saxon.Api;

namespace LantanaGroup.XmlDocumentConverter
{
    public class DB2Converter : BaseConverter
    {
        private string database;
        private string username;
        private string password;

        private DbConnection conn;

        public DB2Converter(string configFileName, string inputDirectory, string database, string username, string password, string moveDirectory) : 
            base(configFileName, inputDirectory, moveDirectory)
        {
            this.database = database;
            this.username = username;
            this.password = password;
        }

        protected override int InsertData(string tableName, Dictionary<MappingColumn, object> columns)
        {
            var columnsNames = columns.Keys.Select(y => y.Name);
            string insertQuery = "INSERT INTO " + tableName.ToUpper() + " (" + string.Join(", ", columnsNames) + ") VALUES (";

            List<string> values = new List<string>();

            foreach (var key in columns.Keys)
            {
                var value = columns[key];

                if (value == null)
                {
                    values.Add("null");
                }
                else if (value.GetType() == typeof(string))
                {
                    string stringValue = ((string)value).Replace("'", "''");

                    if (!key.IsNarrative && stringValue.Length > 255)
                    {
                        stringValue = stringValue.Substring(0, 254);
                        this.Log(String.Format("Value for column {0} is more than 255 characters and will be truncated. Consider using isNarrative=true on the column definition.", key.Name, key.Heading));
                    }

                    values.Add("'" + stringValue + "'");
                }
                else
                {
                    values.Add(value.ToString());
                }
            }

            insertQuery += string.Join(", ", values) + ")";

            try
            {
                DbCommand insertCommand = this.conn.CreateCommand();
                insertCommand.CommandText = insertQuery;
                insertCommand.ExecuteNonQuery();

                DbCommand getIdCommand = this.conn.CreateCommand();
                getIdCommand.CommandText = string.Format("SELECT SYSIBM.IDENTITY_VAL_LOCAL() AS ID FROM {0}", tableName.ToUpper());
                decimal ret = (decimal) getIdCommand.ExecuteScalar();
                int res = Decimal.ToInt32(ret);
                return res;
            }
            catch (Exception ex)
            {
                this.Log(String.Format("Error inserting data into {0}: {1}", tableName, ex.Message));
                return -1;
            }
        }

        private void ProcessGroup(DbConnection conn, MappingGroup groupConfig, XmlNode parentNode, XmlNamespaceManager nsManager, int parentId, string parentName)
        {
            var groupNodes = parentNode.SelectNodes(groupConfig.Context, nsManager);

            if (groupNodes.Count == 0)
                return;

            foreach (XmlElement groupNode in groupNodes)
            {
                Dictionary<MappingColumn, object> groupColumnData = new Dictionary<MappingColumn, object>();

                MappingColumn parentCol = new MappingColumn()
                {
                    Name = parentName + "Id",
                    Heading = parentName + "Id"
                };

                groupColumnData.Add(parentCol, parentId);

                foreach (var colConfig in groupConfig.Column)
                {
                    string xpath = colConfig.Value;
                    string cellValue = this.GetValue(xpath, groupNode, nsManager, colConfig.IsNarrative);
                    groupColumnData.Add(colConfig, cellValue);
                }

                int nextId = this.InsertData(groupConfig.TableName, groupColumnData);

                foreach (var childGroup in groupConfig.Group)
                {
                    this.ProcessGroup(conn, childGroup, groupNode, nsManager, nextId, groupConfig.TableName);
                }
            }
        }

        #region Schema Creation

        private void EnsureTable(DbConnection conn, string tableName, List<MappingColumn> columns, string parentTableName = null)
        {
            DbCommand existsCmd = conn.CreateCommand();
            existsCmd.CommandText = string.Format("SELECT COUNT(0) AS TOTAL FROM SYSIBM.SYSTABLES WHERE NAME = '{0}'", tableName.ToUpper());

            int existsResults = (int) existsCmd.ExecuteScalar();

            this.Log("Validating table " + tableName.ToUpper());

            // Check the definition of the table compared to what's defined in config
            if (existsResults == 1)
            {
                DbCommand definitionCmd = conn.CreateCommand();
                definitionCmd.CommandText = string.Format("SELECT NAME, COLTYPE, LENGTH FROM SYSIBM.SYSCOLUMNS WHERE TBNAME = '{0}'", tableName.ToUpper());

                DbDataReader reader = definitionCmd.ExecuteReader();
                List<MappingColumn> actualCols = new List<MappingColumn>();
                List<MappingColumn> expectedCols = new List<MappingColumn>(columns);
                expectedCols.Add(new MappingColumn() { Name = "ID" });

                if (!string.IsNullOrEmpty(parentTableName))
                    expectedCols.Add(new MappingColumn() { Name = parentTableName.ToUpper() + "ID" });

                while (reader.Read())
                {
                    string colName = reader.GetString(0);
                    string colType = reader.GetString(1);
                    short colLength = reader.GetInt16(2);

                    actualCols.Add(new MappingColumn() {
                        Name = colName,
                        IsNarrative = colType.Trim() == "LONGVAR"
                    });
                }

                expectedCols.ForEach(delegate (MappingColumn expected)
                {
                    if (actualCols.Find(actual => actual.Name == expected.Name.ToUpper() && actual.IsNarrative == expected.IsNarrative) == null)
                        throw new Exception(string.Format("Could not find correct definition of column {0} in table {1}", expected.Name.ToUpper(), tableName));
                });
            }
            else if (existsResults == 0)
            {
                string columnDefinitions = string.Empty;
                string foreignKeyCol = string.Empty;

                if (!string.IsNullOrEmpty(parentTableName))
                    foreignKeyCol = string.Format("{0}ID INTEGER NOT NULL, ", parentTableName.ToUpper());

                foreach (var col in columns.OrderBy(y => y.Name))
                {
                    string dataType = col.IsNarrative ? "LONG VARCHAR" : "VARCHAR(255)";
                    columnDefinitions += string.Format("{0} {1}, ", col.Name.ToUpper(), dataType);
                }

                DbCommand createCmd = conn.CreateCommand();
                createCmd.CommandText = string.Format("CREATE TABLE {0} (ID INTEGER GENERATED ALWAYS AS IDENTITY NOT NULL, {1}{2}PRIMARY KEY (ID))", tableName.ToUpper(), foreignKeyCol, columnDefinitions);

                createCmd.ExecuteNonQuery();
            }
        }

        private void EnsureGroup(DbConnection conn, MappingGroup group, string parentTableName)
        {
            this.EnsureTable(conn, group.TableName, group.Column, parentTableName);

            group.Group.ForEach(delegate (MappingGroup nextGroup)
            {
                this.EnsureGroup(conn, nextGroup, group.TableName);
            });
        }

        private void ValidateSchema(DbConnection conn)
        {
            List<MappingColumn> columns = new List<MappingColumn>(this.config.Column);
            columns.Insert(0, new MappingColumn()
            {
                Name = "FILENAME"
            });

            this.EnsureTable(conn, this.config.TableName, columns);

            this.config.Group.ForEach(delegate (MappingGroup nextGroup)
            {
                this.EnsureGroup(conn, nextGroup, this.config.TableName);
            });
        }

        #endregion

        protected override bool InitializeOutput()
        {
            DbProviderFactory factory = DbProviderFactories.GetFactory("IBM.Data.DB2");
            this.conn = factory.CreateConnection();
            this.conn.ConnectionString = string.Format("Database={0};UID={1};PWD={2}", this.database, this.username, this.password);
            this.conn.Open();

            try
            {
                this.ValidateSchema(conn);
            }
            catch (Exception ex)
            {
                this.Log(string.Format("Failed to validate database and cannot proceed due to: " + ex.Message));
                return false;
            }

            return true;
        }

        protected override void FinalizeOutput()
        {
            this.conn.Close();
        }
    }
}
