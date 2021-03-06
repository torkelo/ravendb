using System;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace Raven.Database.Storage
{
	[CLSCompliant(false)]
	public class SchemaCreator
	{
		public const string SchemaVersion = "1.95";
		private readonly Session session;

		public SchemaCreator(Session session)
		{
			this.session = session;
		}

		public void Create(string database)
		{
			JET_DBID dbid;
			Api.JetCreateDatabase(session, database, null, out dbid, CreateDatabaseGrbit.None);
			try
			{
				using (var tx = new Transaction(session))
				{
					CreateDetailsTable(dbid);
					CreateDocumentsTable(dbid);
                    CreateDocumentsBeingModifiedByTransactionsTable(dbid);
				    CreateTransactionsTable(dbid);
					CreateTasksTable(dbid);
					CreateMapResultsTable(dbid);
					CreateIndexingStatsTable(dbid);
					CreateFilesTable(dbid);
					CreateIdentityTable(dbid);

					tx.Commit(CommitTransactionGrbit.None);
				}
			}
			finally
			{
				Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
			}
		}

		private void CreateIdentityTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "identity_table", 16, 100, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);


			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "val", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			const string indexDef = "+key\0\0";
			Api.JetCreateIndex(session, tableid, "by_key", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
							   100);
		}

		private void CreateIndexingStatsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "indexes_stats", 16, 100, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "successes", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnEscrowUpdate
			}, defaultValue, defaultValue.Length, out columnid);


			Api.JetAddColumn(session, tableid, "attempts", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "errors", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			const string indexDef = "+key\0\0";
			Api.JetCreateIndex(session, tableid, "by_key", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
			                   100);
		}

        private void CreateTransactionsTable(JET_DBID dbid)
        {
            JET_TABLEID tableid;
            Api.JetCreateTable(session, dbid, "transactions", 16, 100, out tableid);
            JET_COLUMNID columnid;

            Api.JetAddColumn(session, tableid, "tx_id", new JET_COLUMNDEF
            {
                cbMax = 16,
                coltyp = JET_coltyp.Binary,
                grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "timeout", new JET_COLUMNDEF
            {
                coltyp = JET_coltyp.DateTime,
                grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
            }, null, 0, out columnid);

            const string indexDef = "+tx_id\0\0";
            Api.JetCreateIndex(session, tableid, "by_tx_id", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
                               100);
        }

	    private void CreateDocumentsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "documents", 16, 100, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "locked_by_transaction", new JET_COLUMNDEF
            {
                cbMax = 16,
                coltyp = JET_coltyp.Binary,
                grbit = ColumndefGrbit.ColumnTagged,
            }, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);


			var indexDef = "+key\0\0";
			Api.JetCreateIndex(session, tableid, "by_key", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
			                   100);

			indexDef = "+id\0\0";
			Api.JetCreateIndex(session, tableid, "by_id", CreateIndexGrbit.IndexDisallowNull, indexDef, indexDef.Length,
			                   100);
		}

        private void CreateDocumentsBeingModifiedByTransactionsTable(JET_DBID dbid)
        {
            JET_TABLEID tableid;
            Api.JetCreateTable(session, dbid, "documents_modified_by_transaction", 16, 100, out tableid);
            JET_COLUMNID columnid;

            Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
            {
                cbMax = 255,
                coltyp = JET_coltyp.Text,
                cp = JET_CP.Unicode,
                grbit = ColumndefGrbit.ColumnTagged
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
            {
                cbMax = 16,
                coltyp = JET_coltyp.Binary,
                grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "locked_by_transaction", new JET_COLUMNDEF
            {
                cbMax = 16,
                coltyp = JET_coltyp.Binary,
                grbit = ColumndefGrbit.ColumnFixed,
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
            {
                coltyp = JET_coltyp.LongBinary,
                grbit = ColumndefGrbit.ColumnTagged
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
            {
                coltyp = JET_coltyp.LongText,
                grbit = ColumndefGrbit.ColumnTagged
            }, null, 0, out columnid);

            Api.JetAddColumn(session, tableid, "delete_document", new JET_COLUMNDEF
            {
                coltyp = JET_coltyp.Bit,
                grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
            }, null, 0, out columnid);

            var indexDef = "+key\0\0";
            Api.JetCreateIndex(session, tableid, "by_key", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
                               100);

            indexDef = "+locked_by_transaction\0\0";
            Api.JetCreateIndex(session, tableid, "by_tx", CreateIndexGrbit.IndexDisallowNull, indexDef, indexDef.Length,
                               100);
        }

		private void CreateMapResultsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "mapped_results", 16, 100, out tableid);
			JET_COLUMNID columnid;


			Api.JetAddColumn(session, tableid, "view", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "document_key", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "reduce_key", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);


			var indexDef = "+view\0+document_key\0+reduce_key\0\0";
			Api.JetCreateIndex(session, tableid, "by_pk", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
			                   100);

			indexDef = "+view\0+document_key\0\0";
			Api.JetCreateIndex(session, tableid, "by_view_and_doc_key", CreateIndexGrbit.IndexDisallowNull, indexDef,
			                   indexDef.Length,
			                   100);

			indexDef = "+view\0\0";
			Api.JetCreateIndex(session, tableid, "by_view", CreateIndexGrbit.IndexDisallowNull, indexDef, indexDef.Length,
			                   100);

			indexDef = "+view\0+reduce_key\0\0";
			Api.JetCreateIndex(session, tableid, "by_view_and_reduce_key", CreateIndexGrbit.IndexDisallowNull, indexDef,
			                   indexDef.Length,
			                   100);
		}

		private void CreateTasksTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "tasks", 16, 100, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "task", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "for_index", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);


			var indexDef = "+id\0\0";
			Api.JetCreateIndex(session, tableid, "by_id", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
			                   100);
			indexDef = "+for_index\0\0";
			Api.JetCreateIndex(session, tableid, "by_index", CreateIndexGrbit.IndexIgnoreNull, indexDef, indexDef.Length,
			                   100);
		}

		private void CreateFilesTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "files", 16, 100, out tableid);
			JET_COLUMNID columnid;


			Api.JetAddColumn(session, tableid, "name", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			const string indexDef = "+name\0\0";
			Api.JetCreateIndex(session, tableid, "by_name", CreateIndexGrbit.IndexPrimary, indexDef, indexDef.Length,
			                   100);
		}

		private void CreateDetailsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "details", 16, 100, out tableid);
			JET_COLUMNID id;
			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed
			}, null, 0, out id);

			JET_COLUMNID schemaVersion;
			Api.JetAddColumn(session, tableid, "schema_version", new JET_COLUMNDEF
			{
				cbMax = Encoding.Unicode.GetByteCount(SchemaVersion),
				cp = JET_CP.Unicode,
				coltyp = JET_coltyp.Text,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed
			}, null, 0, out schemaVersion);


			using (var update = new Update(session, tableid, JET_prep.Insert))
			{
				Api.SetColumn(session, tableid, id, Guid.NewGuid().ToByteArray());
				Api.SetColumn(session, tableid, schemaVersion, SchemaVersion, Encoding.Unicode);
				update.Save();
			}
		}
	}
}