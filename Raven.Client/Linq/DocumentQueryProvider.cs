using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Raven.Database;

namespace Raven.Client.Linq
{
    public class DocumentQueryProvider : IQueryProvider
    {
        private readonly IDatabaseCommands Commands;
        private readonly string IndexName;

        public DocumentQueryProvider(IDatabaseCommands commands, string indexName)
        {
            Commands = commands;
            IndexName = indexName;
        }

        public IQueryable CreateQuery(Expression expression)
        {
            throw new NotImplementedException();
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new DocumentQuery<TElement>(this, expression);
        }

        public object Execute(Expression expression)
        {
            throw new NotImplementedException();
        }

        public TResult Execute<TResult>(Expression expression)
        {
            var methodCall = expression as MethodCallExpression;
            if ((methodCall != null) && (methodCall.Method.Name == "Where"))
            {
                var a = methodCall.Arguments[1] as UnaryExpression;
                var b = a.Operand as LambdaExpression;
                var c = b.Body as BinaryExpression;
                var propertyName = c.Left as MemberExpression;
                var constant = c.Right as ConstantExpression;
                var queryString = propertyName.Member.Name + ":" + constant.Value;
                var result = Commands.Query(IndexName, queryString, 0, int.MaxValue);

                while(result.IsStale)
                {
                    result = Commands.Query(IndexName, queryString, 0, int.MaxValue);
                    Thread.Sleep(100);
                }

                return (TResult)(object)result.Results.Select(q =>
                {
                    var id = q.Last.First.Value<string>("@id");
                    var entity = JsonConvert.DeserializeObject(q.ToString());
                    return entity;
                }).ToList();
            }
            
            throw new NotSupportedException("Method Not Supported");
        }
    }
}