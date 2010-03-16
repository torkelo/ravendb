using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Raven.Database;
using Raven.Server;
using Xunit;

namespace Raven.Client.Tests
{
    public class DocumentStoreServerTests : BaseTest , IDisposable
    {
        private string path;

        public DocumentStoreServerTests()
        {
            path = Path.GetDirectoryName(Assembly.GetAssembly(typeof(DocumentStoreServerTests)).CodeBase);
            path = Path.Combine(path, "TestDb").Substring(6);
        }

        [Fact]
        public void Should_insert_into_db_and_set_id()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path}))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();

                var session = documentStore.OpenSession();
                var entity = new Company { Name = "Company" };
                session.Store(entity);

                Assert.NotEqual(Guid.Empty.ToString(), entity.Id);
            }
        }

        [Fact]
        public void Should_update_stored_entity()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path }))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();

                var session = documentStore.OpenSession();
                var company = new Company { Name = "Company 1" };
                session.Store(company);
                var id = company.Id;
                company.Name = "Company 2";
                session.SaveChanges();
                var companyFound = session.Load<Company>(company.Id);
                Assert.Equal("Company 2", companyFound.Name);
                Assert.Equal(id, company.Id);
            }
        }

        [Fact]
        public void Should_update_retrieved_entity()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path }))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();

                var session1 = documentStore.OpenSession();
                var company = new Company { Name = "Company 1" };
                session1.Store(company);
                var companyId = company.Id;

                var session2 = documentStore.OpenSession();
                var companyFound = session2.Load<Company>(companyId);
                companyFound.Name = "New Name";
                session2.SaveChanges();

                Assert.Equal("New Name", session2.Load<Company>(companyId).Name);
            }
        }

        [Fact]
        public void Should_retrieve_all_entities()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path }))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();

                var session1 = documentStore.OpenSession();
                session1.Store(new Company { Name = "Company 1" });
                session1.Store(new Company { Name = "Company 2" });

                var session2 = documentStore.OpenSession();
                var companyFound = session2.GetAll<Company>();

                Assert.Equal(2, companyFound.Count);
            }
        }

        [Fact]
        public void Should_query_database_via_index_using_where()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path }))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();
                documentStore.DatabaseCommands.PutIndex("getByName", "from entity in docs select new { entity.type, entity.Name };");

                var session = documentStore.OpenSession();
                
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Bobs Builders" });


                var query = from company in session.Query<Company>("getByName")
                            where company.Name == "Company"
                            select company;

                // Should translate to 
                //documentStore.DatabaseCommands.Query("getByName", ":name=\"Company\"", 0, int.MaxValue);
                var resultList = query.ToList();
                Assert.Equal(2, resultList.Count);
                Assert.True(resultList.All(q => q.Name == "Company"));
            }
        }

        [Fact]
        public void Should_query_database_via_index_using_with_paging()
        {
            DivanServer.EnsureCanListenToWhenInNonAdminContext(8080);
            using (var server = new DivanServer(new RavenConfiguration { Port = 8080, DataDirectory = path }))
            {
                var documentStore = new DocumentStore("localhost", 8080);
                documentStore.Initialise();
                documentStore.DatabaseCommands.PutIndex("getByName", "from entity in docs select new { entity.type, entity.Name };");

                var session = documentStore.OpenSession();

                session.Store(new Company { Name = "Company", Address1 = "First Address"});
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company" });
                session.Store(new Company { Name = "Company", Address1 = "Last Address" });
                session.Store(new Company { Name = "Bobs Builders" });


                var query = from company in session.Query<Company>("getByName")
                            where company.Name == "Company"
                            select company;

                // Should translate to 
                //documentStore.DatabaseCommands.Query("getByName", ":name=\"Company\"", 2, 5);

                var resultList = query.Skip(1).Take(5).ToList();
                Assert.Equal(5, resultList.Count);
                Assert.False(resultList.Any(q => q.Address1 == "First Address"));
                Assert.True(resultList.All(q => q.Name == "Company"));
                Assert.False(resultList.Any(q => q.Address1 == "Last Address"));
            }
        }

        public void Dispose()
        {
            Thread.Sleep(100);
            Directory.Delete(path, true);
        }
    }
}