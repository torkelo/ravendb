using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Web;
using log4net;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Linq;
using Raven.Database.Storage;
using Directory = System.IO.Directory;

namespace Raven.Database.Indexing
{
	/// <summary>
	/// 	Thread safe, single instance for the entire application
	/// </summary>
	public class IndexStorage : CriticalFinalizerObject, IDisposable
	{
		private readonly ConcurrentDictionary<string, Index> indexes = new ConcurrentDictionary<string, Index>();
		private readonly ILog log = LogManager.GetLogger(typeof (IndexStorage));
		private readonly string path;

		public IndexStorage(string path, IndexDefinitionStorage indexDefinitionStorage)
		{
			this.path = Path.Combine(path, "Index");
			if (Directory.Exists(this.path) == false)
				Directory.CreateDirectory(this.path);
			log.DebugFormat("Initializing index storage at {0}", this.path);
			foreach (var indexDirectory in Directory.GetDirectories(this.path))
			{
				log.DebugFormat("Loading saved index {0}", indexDirectory);
				var name = Path.GetFileName(indexDirectory);
				name = HttpUtility.UrlDecode(name);
				var indexDefinition = indexDefinitionStorage.GetIndexDefinition(name);
				if(indexDefinition == null)
					continue;
				var fsDirectory = FSDirectory.GetDirectory(indexDirectory, false);
				indexes.TryAdd(name, CreateIndexImplementation(name, indexDefinition, fsDirectory));
			}
		}

		private static Index CreateIndexImplementation(string name, IndexDefinition indexDefinition, FSDirectory fsDirectory)
		{
			return indexDefinition.IsMapReduce
				? (Index) new MapReduceIndex(fsDirectory, name, indexDefinition)
				: new SimpleIndex(fsDirectory, name, indexDefinition);
		}

		public string[] Indexes
		{
			get { return indexes.Keys.ToArray(); }
		}

		#region IDisposable Members

		public void Dispose()
		{
			foreach (var index in indexes.Values)
			{
				index.Dispose();
			}
		}

		#endregion

		public void DeleteIndex(string name)
		{
			Index value;
			if (indexes.TryGetValue(name, out value) == false)
			{
				log.InfoFormat("Ignoring delete for non existing index {0}", name);
				return;
			}
			log.InfoFormat("Deleting index {0}", name);
			value.Dispose();
			Index ignored;
			var nameOnDisk = HttpUtility.UrlEncode(name);
			var indexDir = Path.Combine(path, nameOnDisk);
			if (indexes.TryRemove(name, out ignored) && Directory.Exists(indexDir))
			{
				Directory.Delete(indexDir, true);
			}
		}

		public void CreateIndexImplementation(string name, IndexDefinition indexDefinition)
		{
			log.InfoFormat("Creating index {0}", name);

			indexes.AddOrUpdate(name, n =>
			{
				var nameOnDisk = HttpUtility.UrlEncode(name);
				var directory = FSDirectory.GetDirectory(Path.Combine(path, nameOnDisk), true);
				new IndexWriter(directory, new StandardAnalyzer()).Close(); //creating index structure
				return CreateIndexImplementation(name, indexDefinition, directory);
			}, (s, index) => index);
		}

		public IEnumerable<IndexQueryResult> Query(string index, IndexQuery query)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.DebugFormat("Query on non existing index {0}", index);
				throw new InvalidOperationException("Index " + index + " does not exists");
			}
			return value.Query(query);
		}

		public void RemoveFromIndex(string index, string[] keys, WorkContext context)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.DebugFormat("Removing from non existing index {0}, ignoring", index);
				return;
			}
			value.Remove(keys, context);
		}

		public void Index(string index, AbstractViewGenerator viewGenerator, IEnumerable<dynamic> docs, WorkContext context,
		                  DocumentStorageActions actions)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.DebugFormat("Tried to index on a non existant index {0}, ignoring", index);
				return;
			}
			value.IndexDocuments(viewGenerator, docs, context, actions);
		}

		public void Reduce(string index, AbstractViewGenerator viewGenerator, IEnumerable<object> mappedResults,
		                   WorkContext context, DocumentStorageActions actions, string reduceKey)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.DebugFormat("Tried to index on a non existant index {0}, ignoring", index);
				return;
			}
			var mapReduceIndex = value as MapReduceIndex;
			if (mapReduceIndex == null)
			{
				log.WarnFormat("Tried to reduce on an index that is not a map/reduce index: {0}, ignoring", index);
				return;
			}
			mapReduceIndex.ReduceDocuments(viewGenerator, mappedResults, context, actions, reduceKey);
		}
	}
}