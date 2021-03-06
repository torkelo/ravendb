﻿using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Newtonsoft.Json.Linq;
using Raven.Database.Data;
using Raven.Database.Linq;
using Raven.Database.Storage;

namespace Raven.Database.Indexing
{
	public class SimpleIndex : Index
	{
		public SimpleIndex(Directory directory, string name, IndexDefinition indexDefinition)
			: base(directory, name, indexDefinition)
		{
		}

		public override void IndexDocuments(
			AbstractViewGenerator viewGenerator, 
			IEnumerable<object> documents,
			WorkContext context,
			DocumentStorageActions actions)
		{
			actions.SetCurrentIndexStatsTo(name);
			var count = 0;
			Write(indexWriter =>
			{
				string currentId = null;
				var converter = new AnonymousObjectToLuceneDocumentConverter();
				PropertyDescriptorCollection properties = null;
				foreach (var doc in RobustEnumeration(documents, viewGenerator.MapDefinition, actions, context))
				{
					count++;

					if (properties == null)
					{
						properties = TypeDescriptor.GetProperties(doc);
					}
					var newDocId = properties.Find("__document_id", false).GetValue(doc) as string;
					var fields = converter.Index(doc, properties, indexDefinition);
					if (currentId != newDocId) // new document id, so delete all old values matching it
					{
						indexWriter.DeleteDocuments(new Term("__document_id", newDocId));
					}

                    if (newDocId != null)
                    {
                        var luceneDoc = new Document();
                        luceneDoc.Add(new Field("__document_id", newDocId, Field.Store.YES, Field.Index.UN_TOKENIZED));

                        currentId = newDocId;
                        CopyFieldsToDocumentButRemoveDuplicateValues(luceneDoc, fields);
                    	log.DebugFormat("Indexing document {0}", luceneDoc);
                        indexWriter.AddDocument(luceneDoc);
                    }

					actions.IncrementSuccessIndexing();
				}

				return currentId != null;
			});
			log.DebugFormat("Indexed {0} documents for {1}", count, name);
		}

		private static void CopyFieldsToDocumentButRemoveDuplicateValues(Document luceneDoc, IEnumerable<Field> fields)
		{
			foreach (var field in fields)
			{
				var valueAlreadyExisting = false;
				var existingFields = luceneDoc.GetFields(field.Name());
				if (existingFields != null)
				{
					var fieldCopy = field;
					valueAlreadyExisting = existingFields.Any(existingField => existingField.StringValue() == fieldCopy.StringValue());
				}
				if (valueAlreadyExisting)
					continue;
				luceneDoc.Add(field);
			}
		}

		protected override IndexQueryResult RetrieveDocument(Document document, string[] fieldsToFetch)
		{
			return new IndexQueryResult
			{
				Key = document.Get("__document_id"),
				Projection = fieldsToFetch == null || fieldsToFetch.Length == 0
					? null
					:
						new JObject(
					fieldsToFetch.Concat(new[] {"__document_id"}).Distinct()
						.SelectMany(name => document.GetFields(name) ?? new Field[0])
						.Where(x => x != null)
						.Select(fld => new JProperty(fld.Name(), fld.StringValue()))
						.GroupBy(x => x.Name)
						.Select(g =>
						{
							if (g.Count() == 1)
								return g.First();
							return new JProperty(g.Key,
							                     g.Select(x => x.Value)
								);
						})
					)
			};
		}

		public override void Remove(string[] keys, WorkContext context)
		{
			Write(writer =>
			{
				if (log.IsDebugEnabled)
				{
					log.DebugFormat("Deleting ({0}) from {1}", string.Format(", ", keys), name);
				}
				writer.DeleteDocuments(keys.Select(k => new Term("__document_id", k)).ToArray());
				return true;
			});
		}
	}
}