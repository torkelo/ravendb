using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading;
using log4net;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Newtonsoft.Json.Linq;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Linq;
using Raven.Database.Storage;
using Raven.Database.Tasks;

namespace Raven.Database.Indexing
{
    /// <summary>
    ///   This is a thread safe, single instance for a particular index.
    /// </summary>
    public class Index : IDisposable
    {
        private const string documentIdName = "__document_id";
        private const string isOnlyViewResultName = "__is_only_view_result";
        private readonly Directory directory;
        private readonly ILog log = LogManager.GetLogger(typeof(Index));
        private readonly string name;
        private CurrentIndexSearcher searcher;

        public Index(Directory directory, string name)
        {
            this.name = name;
            log.DebugFormat("Creating index for {0}", name);
            this.directory = directory;
            searcher = new CurrentIndexSearcher
            {
                Searcher = new IndexSearcher(directory)
            };
        }

        #region IDisposable Members

        public void Dispose()
        {
            searcher.Searcher.Close();
            directory.Close();
        }

        #endregion

        public IEnumerable<IndexQueryResult> Query(string query, int start, int pageSize, Reference<int> totalSize, string[] fieldsToFetch)
        {
            using (searcher.Use())
            {
                var indexSearcher = searcher.Searcher;
                if (string.IsNullOrEmpty(query) == false)
                {
                    return SearchIndex(query, indexSearcher, totalSize, start, pageSize, fieldsToFetch);
                }
                return BrowseIndex(indexSearcher, totalSize, start, pageSize, fieldsToFetch);
            }
        }

        private IEnumerable<IndexQueryResult> BrowseIndex(IndexSearcher indexSearcher, Reference<int> totalSize, int start,
                                                int pageSize, string[] fieldsToFetch)
        {
            log.DebugFormat("Browsing index {0}", name);
            var indexReader = indexSearcher.Reader;
            var maxDoc = indexReader.MaxDoc();
            totalSize.Value = Enumerable.Range(0, maxDoc).Count(i => indexReader.IsDeleted(i) == false);
            for (var i = start; i < maxDoc && (i - start) < pageSize; i++)
            {
                if (indexReader.IsDeleted(i))
                    continue;
                var document = indexReader.Document(i);
                yield return RetrieveDocument(document, fieldsToFetch);
            }
        }

        private static IndexQueryResult RetrieveDocument(Document document, string[] fieldsToFetch)
        {
            return new IndexQueryResult
            {
                Key = document.Get(documentIdName),
                Projection = fieldsToFetch == null || fieldsToFetch.Length == 0 ? null :
                    new JObject(
                        fieldsToFetch.Concat(new[] { documentIdName }).Distinct()
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

        private IEnumerable<IndexQueryResult> SearchIndex(string query, IndexSearcher indexSearcher, Reference<int> totalSize,
                                                int start, int pageSize, string[] fieldsToFetch)
        {
            log.DebugFormat("Issuing query on index {0} for: {1}", name, query);
            var luceneQuery = new QueryParser("", new StandardAnalyzer()).Parse(query);
            var search = indexSearcher.Search(luceneQuery);
            totalSize.Value = search.Length();
            for (var i = start; i < search.Length() && (i - start) < pageSize; i++)
            {
                var document = search.Doc(i);
                yield return RetrieveDocument(document, fieldsToFetch);
            }
        }

        private void Write(Func<IndexWriter, bool> action)
        {
            var indexWriter = new IndexWriter(directory, new StandardAnalyzer());
            bool shouldRcreateSearcher;
            try
            {
                shouldRcreateSearcher = action(indexWriter);
            }
            finally
            {
                indexWriter.Close();
            }
            if (shouldRcreateSearcher)
                RecreateSearcher();
        }

        public void IndexDocuments(
            AbstractViewGenerator viewGenerator, 
            IEnumerable<object> documents, 
            WorkContext context,
            DocumentStorageActions actions)
        {
            bool? isReducing = true;
            if (viewGenerator.ReduceDefinition != null)
                isReducing = false;
            int count = HandleIndexing(viewGenerator, documents, actions, context, isReducing);
            log.InfoFormat("Indexed {0} documents for {1}", count, name);
        }

        private int HandleIndexing(
            AbstractViewGenerator viewGenerator, 
            IEnumerable<object> documents, 
            DocumentStorageActions actions, 
            WorkContext context, 
            bool? isReducing)
        {
            actions.SetCurrentIndexStatsTo(name);
            var count = 0;
            Write(indexWriter =>
            {
                string currentId = null;
                var converter = new JsonToLuceneDocumentConverter();
                Document luceneDoc = null;
                var reduceKeys = new HashSet<string>();
                foreach (var doc in RobustEnumeration(documents, viewGenerator.MapDefinition, actions, context))
                {
                    count++;
                    string newDocId;
                    var fields = converter.Index(doc, out newDocId);
                    luceneDoc = FlushLuceneDocument(newDocId, currentId, luceneDoc, indexWriter, isReducing);
                    currentId = newDocId;
                    if(viewGenerator.GroupByField != null)
                    {
                        var val = luceneDoc.Get(viewGenerator.GroupByField);
                        if (val != null)
                            reduceKeys.Add(val);
                    }
                    AddFieldsToDocumentIfValueDoesnotExistsInTheDocument(luceneDoc, fields);

                    actions.IncrementSuccessIndexing();
                }

                if (luceneDoc != null)
                    indexWriter.UpdateDocument(new Term(documentIdName, currentId), luceneDoc);
                foreach (var reduceKey in reduceKeys)
                {
                    actions.AddTask(new ReduceTask
                    {
                        ReduceKey = reduceKey,
                        Index = viewGenerator.IndexName
                    });
                }
                return luceneDoc != null;
            });
            return count;
        }

        private void AddFieldsToDocumentIfValueDoesnotExistsInTheDocument(Document luceneDoc, IEnumerable<Field> fields)
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

        private static Document FlushLuceneDocument(
            string newDocId, string currentId, Document luceneDoc, IndexWriter indexWriter, bool? isReducing)
        {
            if (newDocId != currentId)
            {
                if (luceneDoc != null)
                {
                    indexWriter.UpdateDocument(new Term(documentIdName, currentId), luceneDoc);
                }

                luceneDoc = new Document();
                luceneDoc.Add(new Field(documentIdName, newDocId, Field.Store.YES, Field.Index.UN_TOKENIZED));
                if (isReducing.HasValue)
                {
                    var viewOnly = isReducing.Value ? "no" : "yes";
                    luceneDoc.Add(new Field(isOnlyViewResultName, viewOnly, Field.Store.NO,
                                            Field.Index.UN_TOKENIZED));
                }
            }
            return luceneDoc;
        }


        private IEnumerable<object> RobustEnumeration(IEnumerable<object> input, IndexingFunc func,
                                                      DocumentStorageActions actions, WorkContext context)
        {
            var wrapped = new StatefulEnumerableWrapper<dynamic>(input.GetEnumerator());
            IEnumerator<object> en = func(wrapped).GetEnumerator();
            do
            {
                var moveSuccessful = MoveNext(en, wrapped, context, actions);
                if (moveSuccessful == false)
                    yield break;
                if (moveSuccessful == true)
                    yield return en.Current;
                else
                    en = func(wrapped).GetEnumerator();
            } while (true);
        }

        private bool? MoveNext(IEnumerator en, StatefulEnumerableWrapper<object> innerEnumerator, WorkContext context,
                               DocumentStorageActions actions)
        {
            try
            {
                actions.IncrementIndexingAttempt();
                var moveNext = en.MoveNext();
                if (moveNext == false)
                    actions.DecrementIndexingAttempt();
                return moveNext;
            }
            catch (Exception e)
            {
                actions.IncrementIndexingFailure();
                context.AddError(name,
                                 TryGetDocKey(innerEnumerator.Current),
                                 e.Message
                    );
                log.WarnFormat(e, "Failed to execute indexing function on {0} on {1}", name,
                               GetDocId(innerEnumerator));
            }
            return null;
        }

        private static string TryGetDocKey(object current)
        {
            var dic = current as IDictionary<string, object>;
            if (dic == null)
                return null;
            object value;
            dic.TryGetValue(documentIdName, out value);
            if (value == null)
                return null;
            return value.ToString();
        }

        private static object GetDocId(StatefulEnumerableWrapper<object> currentInnerEnumerator)
        {
            var dictionary = currentInnerEnumerator.Current as IDictionary<string, object>;
            if (dictionary == null)
                return null;
            object docId;
            dictionary.TryGetValue(documentIdName, out docId);
            return docId;
        }

        private void RecreateSearcher()
        {
            using (searcher.Use())
            {
                searcher.MarkForDispoal();
                searcher = new CurrentIndexSearcher
                {
                    Searcher = new IndexSearcher(directory)
                };
                Thread.MemoryBarrier(); // force other threads to see this write
            }
        }

        public void Remove(string[] keys)
        {
            Write(writer =>
            {
                if (log.IsDebugEnabled)
                {
                    log.DebugFormat("Deleting ({0}) from {1}", string.Format(", ", keys), name);
                }
                writer.DeleteDocuments(keys.Select(k => new Term(documentIdName, k)).ToArray());
                return true;
            });
        }

        #region Nested type: CurrentIndexSearcher

        private class CurrentIndexSearcher
        {
            private bool shouldDisposeWhenThereAreNoUsages;
            private int useCount;
            public IndexSearcher Searcher { get; set; }


            public IDisposable Use()
            {
                Interlocked.Increment(ref useCount);
                return new CleanUp(this);
            }

            public void MarkForDispoal()
            {
                shouldDisposeWhenThereAreNoUsages = true;
            }

            #region Nested type: CleanUp

            private class CleanUp : IDisposable
            {
                private readonly CurrentIndexSearcher parent;

                public CleanUp(CurrentIndexSearcher parent)
                {
                    this.parent = parent;
                }

                #region IDisposable Members

                public void Dispose()
                {
                    var uses = Interlocked.Decrement(ref parent.useCount);
                    if (parent.shouldDisposeWhenThereAreNoUsages && uses == 0)
                        parent.Searcher.Close();
                }

                #endregion
            }

            #endregion
        }

        #endregion

        public void ReduceDocuments(AbstractViewGenerator viewGenerator, string reduceKey, WorkContext context)
        {
            context.TransactionaStorage.Batch(actions =>
            {
                int count = HandleIndexing(viewGenerator, ReduceKeyQuery(viewGenerator, reduceKey), actions, context, true);
                log.InfoFormat("Reduced to {0} documents in {1}", count, name);

                actions.Commit();
            });
        }

        private IEnumerable<dynamic> ReduceKeyQuery(AbstractViewGenerator viewGenerator, string reduceKey)
        {
            using(searcher.Use())
            {
                var matchingDocuments = new TermQuery(new Term(viewGenerator.GroupByField, reduceKey));
                var viewResults = new TermQuery(new Term(isOnlyViewResultName, "yes"));
                var query = new BooleanQuery();
                query.Add(matchingDocuments, BooleanClause.Occur.MUST);
                query.Add(viewResults, BooleanClause.Occur.MUST);

                var hits = searcher.Searcher.Search(query);

                for (int i = 0; i < hits.Length(); i++)
                {
                    var document = hits.Doc(i);
                    var expando = new ExpandoObject() as IDictionary<string,object>;
                    foreach (Field field in document.GetFields())
                    {
                        expando[field.Name()] = field.StringValue();
                    }
                    yield return expando;
                }
            }
        }
    }
}