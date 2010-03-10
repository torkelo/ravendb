using System;
using Raven.Database.Indexing;

namespace Raven.Database.Tasks
{
    public class ReduceTask : Task
    {
        public string ReduceKey { get; set; }

        public override void Execute(WorkContext context)
        {
            var viewGenerator = context.IndexDefinitionStorage.GetViewGenerator(Index);

            context.IndexStorage.Reduce(Index, viewGenerator, ReduceKey, context);
        }
    }
}