using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Web;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Database.Indexing;
using Raven.Database.Json;
using Raven.Database.Linq;

namespace Raven.Database.Storage
{
	public class IndexDefinitionStorage
	{
		private const string IndexDefDir = "IndexDefinitions";

		private readonly ConcurrentDictionary<string, AbstractViewGenerator> indexCache =
			new ConcurrentDictionary<string, AbstractViewGenerator>();

		private readonly ILog logger = LogManager.GetLogger(typeof (IndexDefinitionStorage));
		private readonly string path;

		public IndexDefinitionStorage(string path)
		{
			this.path = Path.Combine(path, IndexDefDir);

			if (Directory.Exists(this.path) == false)
				Directory.CreateDirectory(this.path);

			foreach (var index in Directory.GetFiles(this.path, "*.index"))
			{
				try
				{
					AddAndCompileIndex(
						HttpUtility.UrlDecode(Path.GetFileNameWithoutExtension(index)),
						JsonConvert.DeserializeObject<IndexDefinition>(File.ReadAllText(index), new JsonEnumConverter())
						);
				}
				catch (Exception e)
				{
					logger.Warn("Could not compile index " + index + ", skipping bad index", e);
				}
			}
		}

		public string[] IndexNames
		{
			get { return indexCache.Keys.ToArray(); }
		}

		public string AddIndex(string name, IndexDefinition indexDefinition)
		{
			DynamicViewCompiler transformer = AddAndCompileIndex(name, indexDefinition);
			File.WriteAllText(Path.Combine(path, transformer.Name + ".index"), JsonConvert.SerializeObject(indexDefinition, Formatting.Indented, new JsonEnumConverter()));
			return transformer.Name;
		}

		private DynamicViewCompiler AddAndCompileIndex(string name, IndexDefinition indexDefinition)
		{
			var transformer = new DynamicViewCompiler(name, indexDefinition);
			var generator = transformer.GenerateInstance();
			indexCache.AddOrUpdate(name, generator, (s, viewGenerator) => generator);
			logger.InfoFormat("New index {0}:\r\n{1}\r\nCompiled to:\r\n{2}", transformer.Name, transformer.CompiledQueryText,
			                  transformer.CompiledQueryText);
			return transformer;
		}

		public void RemoveIndex(string name)
		{
			AbstractViewGenerator _;
			indexCache.TryRemove(name, out _);
			File.Delete(GetIndexPath(name));
			File.Delete(GetIndexSourcePath(name));
		}

		private string GetIndexSourcePath(string name)
		{
			return Path.Combine(path, HttpUtility.UrlEncode(name) + ".index.cs");
		}

		private string GetIndexPath(string name)
		{
			return Path.Combine(path, HttpUtility.UrlEncode(name) + ".index");
		}

		public IndexDefinition GetIndexDefinition(string name)
		{
			var indexPath = GetIndexPath(name);
			if (File.Exists(indexPath) == false)
				throw new InvalidOperationException("Index file does not exists");
			return JsonConvert.DeserializeObject<IndexDefinition>(File.ReadAllText(indexPath), new JsonEnumConverter());
		}

		public AbstractViewGenerator GetViewGenerator(string name)
		{
			AbstractViewGenerator value;
			if (indexCache.TryGetValue(name, out value) == false)
				return null;
			return value;
		}

		public IndexCreationOptions FindIndexCreationOptionsOptions(string name, IndexDefinition indexDef)
		{
			if (indexCache.ContainsKey(name))
			{
				return GetIndexDefinition(name) == indexDef
					? IndexCreationOptions.Noop
					: IndexCreationOptions.Update;
			}
			return IndexCreationOptions.Create;
		}
	}
}