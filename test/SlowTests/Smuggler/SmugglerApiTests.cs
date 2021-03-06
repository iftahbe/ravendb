using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests;
using FastTests.Utils;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Expiration;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Tests.Core.Utils.Entities;
using SlowTests.Issues;
using Sparrow;
using Xunit;

namespace SlowTests.Smuggler
{
    public class SmugglerApiTests : RavenTestBase
    {
        private class Users_ByName : AbstractIndexCreationTask<User>
        {
            public Users_ByName()
            {
                Map = users => from u in users
                               select new
                               {
                                   u.Name
                               };

                Stores.Add(x => x.Name, FieldStorage.Yes);
            }
        }

        [Fact]
        public async Task CanExportDirectlyToRemote()
        {
            using (var store1 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_1"
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Name1", LastName = "LastName1" });
                    await session.StoreAsync(new User { Name = "Name2", LastName = "LastName2" });
                    await session.SaveChangesAsync();
                }

                await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), store2.Smuggler);

                using (var commands = store2.Commands())
                {
                    var docs = await commands.GetAsync(0, 10);
                    Assert.Equal(3, docs.Length);
                }
            }
        }

        [Fact]
        public async Task CanExportAndImport()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var store1 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_1"
                }))
                using (var store2 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_2"
                }))
                {
                    using (var session = store1.OpenSession())
                    {
                        // creating auto-indexes
                        session.Query<User>()
                            .Where(x => x.Age > 10)
                            .ToList();

                        session.Query<User>()
                            .GroupBy(x => x.Name)
                            .Select(x => new { Name = x.Key, Count = x.Count() })
                            .ToList();
                    }

                    new Users_ByName().Execute(store1);

                    using (var session = store1.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User { Name = "Name1", LastName = "LastName1" });
                        await session.StoreAsync(new User { Name = "Name2", LastName = "LastName2" });
                        await session.SaveChangesAsync();
                    }

                    await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);

                    await store2.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);

                    var stats = await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(3, stats.CountOfDocuments);
                    Assert.Equal(3, stats.CountOfIndexes);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task ShouldReturnCorrectSmugglerResult()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var store1 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_1"
                }))
                using (var store2 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_2"
                }))
                {
                    using (var session = store1.OpenSession())
                    {
                        // creating auto-indexes
                        session.Query<User>()
                            .Where(x => x.Age > 10)
                            .ToList();

                        session.Query<User>()
                            .GroupBy(x => x.Name)
                            .Select(x => new { Name = x.Key, Count = x.Count() })
                            .ToList();
                    }

                    new Users_ByName().Execute(store1);

                    using (var session = store1.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User { Name = "Name1", LastName = "LastName1" });
                        await session.StoreAsync(new User { Name = "Name2", LastName = "LastName2" });
                        await session.SaveChangesAsync();
                    }

                    var exportOperation = await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);
                    var exportResult = (SmugglerResult)exportOperation.WaitForCompletion();

                    var stats = await store1.Maintenance.SendAsync(new GetStatisticsOperation());
                    var progress = (SmugglerResult.SmugglerProgress)exportResult.Progress;

                    Assert.Equal(stats.CountOfDocuments, progress.Documents.ReadCount);
                    Assert.Equal(stats.CountOfIndexes, progress.Indexes.ReadCount);

                    var importOperation = await store2.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);
                    var importResult = (SmugglerResult)importOperation.WaitForCompletion();

                    stats = await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    progress = (SmugglerResult.SmugglerProgress)importResult.Progress;

                    Assert.Equal(stats.CountOfDocuments, progress.Documents.ReadCount);
                    Assert.Equal(stats.CountOfIndexes, progress.Indexes.ReadCount);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task SkipExpiredDocumentWhenExport()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var exportStore = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_exportStore"
                }))
                {
                    var database = await GetDocumentDatabaseInstanceFor(exportStore);

                    using (var session = exportStore.OpenAsyncSession())
                    {
                        await SetupExpiration(exportStore);
                        var person1 = new Person { Name = "Name1" };
                        await session.StoreAsync(person1).ConfigureAwait(false);
                        var metadata = session.Advanced.GetMetadataFor(person1);
                        metadata[Constants.Documents.Metadata.Expires] = database.Time.GetUtcNow().AddSeconds(10).ToString(DefaultFormat.DateTimeOffsetFormatsToWrite);

                        await session.SaveChangesAsync().ConfigureAwait(false);
                    }

                    database.Time.UtcDateTime = () => DateTime.UtcNow.AddSeconds(11);

                    await exportStore.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { IncludeExpired = false }, file).ConfigureAwait(false);
                }

                using (var importStore = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_importStore"
                }))
                {
                    await importStore.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);
                    using (var session = importStore.OpenAsyncSession())
                    {
                        var person = await session.LoadAsync<Person>("people/1").ConfigureAwait(false);
                        Assert.Null(person);
                    }
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task CanExportAndImportWithRevisionDocuments()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var store1 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_store1"
                }))
                {
                    using (var session = store1.OpenAsyncSession())
                    {
                        await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);

                        await session.StoreAsync(new Person { Name = "Name1" });
                        await session.StoreAsync(new Person { Name = "Name2" });
                        await session.StoreAsync(new Company { Name = "Hibernating Rhinos " });
                        await session.SaveChangesAsync();
                    }

                    for (int i = 0; i < 2; i++)
                    {
                        using (var session = store1.OpenAsyncSession())
                        {
                            var company = await session.LoadAsync<Company>("companies/1-A");
                            var person = await session.LoadAsync<Person>("people/1-A");
                            company.Name += " update " + i;
                            person.Name += " update " + i;
                            await session.StoreAsync(company);
                            await session.StoreAsync(person);
                            await session.SaveChangesAsync();
                        }
                    }

                    using (var session = store1.OpenAsyncSession())
                    {
                        var person = await session.LoadAsync<Person>("people/2-A");
                        Assert.NotNull(person);
                        session.Delete(person);
                        await session.SaveChangesAsync();
                    }

                    await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);

                    var stats = await store1.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(8, stats.CountOfRevisionDocuments);
                }

                using (var store2 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_store2"
                }))
                {
                    await store2.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);

                    var stats = await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(10, stats.CountOfRevisionDocuments);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task WillNotCreateMoreRevisionsAfterImport()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var store1 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_store1"
                }))
                {
                    using (var session = store1.OpenAsyncSession())
                    {
                        await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);

                        await session.StoreAsync(new Person { Name = "Name1" });
                        await session.StoreAsync(new Person { Name = "Name2" });
                        await session.StoreAsync(new Company { Name = "Hibernating Rhinos " });
                        await session.SaveChangesAsync();
                    }

                    for (int i = 0; i < 2; i++)
                    {
                        using (var session = store1.OpenAsyncSession())
                        {
                            var company = await session.LoadAsync<Company>("companies/1-A");
                            var person = await session.LoadAsync<Person>("people/1-A");
                            company.Name += " update " + i;
                            person.Name += " update " + i;
                            await session.StoreAsync(company);
                            await session.StoreAsync(person);
                            await session.SaveChangesAsync();
                        }
                    }

                    using (var session = store1.OpenAsyncSession())
                    {
                        var person = await session.LoadAsync<Person>("people/2-A");
                        Assert.NotNull(person);
                        session.Delete(person);
                        await session.SaveChangesAsync();
                    }

                    await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);

                    var stats = await store1.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(8, stats.CountOfRevisionDocuments);

                    await store1.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), file);

                    stats = await store1.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(8, stats.CountOfRevisionDocuments);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task ShouldAvoidCreatingNewRevisionsDuringImport()
        {
            var file = Path.GetTempFileName();
            try
            {
                using (var store1 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_store1"
                }))
                {
                    using (var session = store1.OpenAsyncSession())
                    {
                        await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);

                        await session.StoreAsync(new Person { Name = "Name1" });
                        await session.StoreAsync(new Person { Name = "Name2" });
                        await session.StoreAsync(new Company { Name = "Hibernating Rhinos " });
                        await session.SaveChangesAsync();
                    }

                    for (int i = 0; i < 2; i++)
                    {
                        using (var session = store1.OpenAsyncSession())
                        {
                            var company = await session.LoadAsync<Company>("companies/1-A");
                            var person = await session.LoadAsync<Person>("people/1-A");
                            company.Name += " update " + i;
                            person.Name += " update " + i;
                            await session.StoreAsync(company);
                            await session.StoreAsync(person);
                            await session.SaveChangesAsync();
                        }
                    }

                    using (var session = store1.OpenAsyncSession())
                    {
                        var person = await session.LoadAsync<Person>("people/2-A");
                        Assert.NotNull(person);
                        session.Delete(person);
                        await session.SaveChangesAsync();
                    }

                    await store1.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);

                    var stats = await store1.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(8, stats.CountOfRevisionDocuments);
                }

                using (var store2 = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => $"{s}_store2"
                }))
                {
                    await store2.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions
                    {
                        SkipRevisionCreation = true
                    }, file);

                    var stats = await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(4, stats.CountOfDocuments);
                    Assert.Equal(8, stats.CountOfRevisionDocuments);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        private async Task SetupExpiration(DocumentStore store)
        {
            using (var session = store.OpenAsyncSession())
            {
                var config = new ExpirationConfiguration
                {
                    Disabled = false,
                    DeleteFrequencyInSec = 100,
                };

                await ExpirationHelper.SetupExpiration(store, Server.ServerStore, config);

                await session.SaveChangesAsync();
            }
        }

        // Smuggler Export and Import need to work with ForDatabase method when store database name is null
        [Fact]
        public async Task Smuggler_Export_And_Import_Should_Work_With_ForDatabase()
        {

            using (var server = GetNewServer())
            {
                using (var store = new DocumentStore
                {
                    Urls = new[] { server.WebUrl }
                }.Initialize())
                {
                    var createSrcDatabase = new CreateDatabaseOperation(new DatabaseRecord("SrcDatabase"));
                    await store.Maintenance.Server.SendAsync(createSrcDatabase);

                    var createDestDatabase = new CreateDatabaseOperation(new DatabaseRecord("DestDatabase"));
                    await store.Maintenance.Server.SendAsync(createDestDatabase);

                    const int documentCount = 10000;
                    using (var session = store.OpenAsyncSession("SrcDatabase"))
                    {
                        for (var i = 0; i < documentCount; i++)
                        {
                            var user = new User { Name = $"User {i}" };
                            await session.StoreAsync(user);
                        }

                        await session.SaveChangesAsync();
                    }

                    var exportOptions = new DatabaseSmugglerExportOptions
                    {
                        OperateOnTypes = DatabaseItemType.Documents
                    };
                    var destination = store.Smuggler.ForDatabase("DestDatabase");
                    var operation = await store.Smuggler.ForDatabase("SrcDatabase").ExportAsync(exportOptions, destination);
                    await operation.WaitForCompletionAsync();


                    var stats = await store.Maintenance.ForDatabase("DestDatabase").SendAsync(new GetStatisticsOperation());
                    Assert.True(stats.CountOfDocuments >= documentCount);

                    await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord("ImportDest")));

                    using (var stream = GetDump("RavenDB_11664.1.ravendbdump"))
                    {
                        await store.Smuggler.ForDatabase("ImportDest").ImportAsync(new DatabaseSmugglerImportOptions(), stream);
                    }

                    using (var session = store.OpenAsyncSession("ImportDest"))
                    {
                        var employee = await session.LoadAsync<Employee>("employees/9-A");
                        Assert.NotNull(employee);
                    }
                }
            }
        }

        private static Stream GetDump(string name)
        {
            var assembly = typeof(RavenDB_9912).Assembly;
            return assembly.GetManifestResourceStream("SlowTests.Data." + name);
        }
    }
}
