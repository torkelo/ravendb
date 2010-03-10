using System;

namespace Raven.Database.Linq
{
    public class AbstractViewGenerator
    {
        public IndexingFunc MapDefinition { get; set; }
        public IndexingFunc ReduceDefinition { get; set; }
        public string ViewText { get; set; }
        public string IndexName { get; set; }
        public string GroupByField { get; set; }
    }
}