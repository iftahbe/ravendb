﻿using System;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.TimeSeries;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Client.TimeSeries.Query
{
    public class TimeSeriesQueryTests : RavenTestBase
    {
        public TimeSeriesQueryTests(ITestOutputHelper output) : base(output)
        {
        }

        private class TimeSeriesRangeAggregation
        {
#pragma warning disable 649
            public long Count;
            public double? Max, Min, Last, First;
            public DateTime To, From;
#pragma warning restore 649
        }

        private class TimeSeriesAggregation
        {
            public long Count { get; set; }
            public TimeSeriesRangeAggregation[] Results { get; set; }
        }

        private class PeopleIndex : AbstractIndexCreationTask<Person>
        {
            public PeopleIndex()
            {
                Map = people => from person in people
                                select new
                                {
                                    person.Age
                                };
            }

            public override string IndexName => "People";
        }

        private class UsersIndex : AbstractIndexCreationTask<User>
        {
            public UsersIndex()
            {
                Map = users => from u in users
                               select new
                                {
                                    u.Age
                                };
            }

            public override string IndexName => "People";
        }

        private class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }

        [Fact]
        public unsafe void CanQueryTimeSeriesAggregation_DeclareSyntax_AllDocsQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    session.Store(new { Name = "Oren" }, "users/ayende");
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });

                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    session.SaveChanges();
                }


                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
    declare timeseries out(u) 
    {
        from u.Heartrate between $start and $end
        group by 1h
        select min(), max(), first(), last()
    }
    from @all_docs as u
    where id() == 'users/ayende'
    select out(u)
")
                        .AddParameter("start", baseline)
                        .AddParameter("end", baseline.AddDays(1));

                    var agg = query.First();

                    if (agg.Count != 3)
                    {
                        var db = GetDocumentDatabaseInstanceFor(store).Result;
                        var tss = db.DocumentsStorage.TimeSeriesStorage;
                        using (db.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                        using (ctx.OpenReadTransaction())
                        {
                            var reader = tss.GetReader(ctx, "users/ayende", "Heartrate", baseline, baseline.AddDays(1));

                            Assert.True(reader.Init());

                            Assert.NotNull(reader._tvr);

                            var key = reader._tvr.Read(0, out var size);

                            TimeSeriesValuesSegment.ParseTimeSeriesKey(key, size, out var docId, out var name, out DateTime baseline2);

                            Assert.Equal("users/ayende", docId);
                            Assert.Equal("Heartrate", name);
                            Assert.Equal(baseline.AddMinutes(61), baseline2);

                            Assert.Equal(1, reader.SegmentsOrValues().Count());

                            Assert.False(query.First().Count == 3, "Query assertion failed once and passed on second try. sanity check passed");

                            //Assert.True(false, "Query assertion failed twice. sanity check passed");
                        }
                    }

                    Assert.Equal(3, agg.Count);

                    Assert.Equal(1, agg.Results.Length);

                    var val = agg.Results[0];

                    Assert.Equal(59, val.First);
                    Assert.Equal(59, val.Min);

                    Assert.Equal(69, val.Last);
                    Assert.Equal(79, val.Max);

                    Assert.Equal(baseline.AddMinutes(60), val.From);
                    Assert.Equal(baseline.AddMinutes(120), val.To);
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_DeclareSyntax_CollectionQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Oren" }, "users/ayende");
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });

                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
    declare timeseries out(u) 
    {
        from u.Heartrate between $start and $end
        group by 1h
        select min(), max(), first(), last()
    }
    from Users as u
    where id() == 'users/ayende'
    select out(u)
")
                        .AddParameter("start", baseline)
                        .AddParameter("end", baseline.AddDays(1));

                    var agg = query.First();

                    Assert.Equal(3, agg.Count);

                    Assert.Equal(1, agg.Results.Length);

                    var val = agg.Results[0];

                    Assert.Equal(59, val.First);
                    Assert.Equal(59, val.Min);

                    Assert.Equal(69, val.Last);
                    Assert.Equal(79, val.Max);

                    Assert.Equal(baseline.AddMinutes(60), val.From);
                    Assert.Equal(baseline.AddMinutes(120), val.To);
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_DeclareSyntax_DynamicIndexQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Oren", Age = 50}, "users/ayende");
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });

                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
    declare timeseries out(u) 
    {
        from u.Heartrate between $start and $end
        group by 1h
        select min(), max(), first(), last()
    }
    from Users as u
    where u.Age > 49
    select out(u)
")
                        .AddParameter("start", baseline)
                        .AddParameter("end", baseline.AddDays(1));

                    var agg = query.First();

                    Assert.Equal(3, agg.Count);

                    Assert.Equal(1, agg.Results.Length);

                    var val = agg.Results[0];

                    Assert.Equal(59, val.First);
                    Assert.Equal(59, val.Min);

                    Assert.Equal(69, val.Last);
                    Assert.Equal(79, val.Max);

                    Assert.Equal(baseline.AddMinutes(60), val.From);
                    Assert.Equal(baseline.AddMinutes(120), val.To);
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_DeclareSyntax_StaticIndexQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Oren", Age = 50 }, "users/ayende");
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });

                    session.TimeSeriesFor("users/ayende")
                        .Append("Heartrate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    session.SaveChanges();
                }

                new UsersIndex().Execute(store);

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
    declare timeseries out(u) 
    {
        from u.Heartrate between $start and $end
        group by 1h
        select min(), max(), first(), last()
    }
    from index 'UsersIndex' as u
    where u.Age > 49
    select out(u)
")
                        .AddParameter("start", baseline)
                        .AddParameter("end", baseline.AddDays(1));

                    var agg = query.First();

                    Assert.Equal(3, agg.Count);

                    Assert.Equal(1, agg.Results.Length);

                    var val = agg.Results[0];

                    Assert.Equal(59, val.First);
                    Assert.Equal(59, val.Min);

                    Assert.Equal(69, val.Last);
                    Assert.Equal(79, val.Max);

                    Assert.Equal(baseline.AddMinutes(60), val.From);
                    Assert.Equal(baseline.AddMinutes(120), val.To);
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_SelectSyntax_CollectionQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        var id = $"people/{i}";

                        session.Store(new Person
                        {
                            Name = "Oren",
                            Age = i * 30
                        }, id);

                        var tsf = session.TimeSeriesFor(id);

                        tsf.Append("HeartRate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                        tsf.Append("HeartRate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });
                        tsf.Append("HeartRate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    }

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
from People as p 
select timeseries(
    from HeartRate between $start and $end 
    group by 1h 
    select min(), max())
")
                        .AddParameter("start", DateTime.MinValue)
                        .AddParameter("end", DateTime.MaxValue);

                    var result = query.ToList();

                    Assert.Equal(3, result.Count);

                    for (int i = 0; i < 3; i++)
                    {
                        var agg = result[i];
                        Assert.Equal(3, agg.Count);

                        Assert.Equal(1, agg.Results.Length);

                        var val = agg.Results[0];

                        Assert.Equal(59, val.Min);
                        Assert.Equal(79, val.Max);

                        Assert.Equal(baseline.AddMinutes(60), val.From);
                        Assert.Equal(baseline.AddMinutes(120), val.To);
                    }
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_SelectSyntax_DynamicIndexQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        var id = $"people/{i}";

                        session.Store(new Person
                        {
                            Name = "Oren",
                            Age = i * 30
                        }, id);

                        var tsf = session.TimeSeriesFor(id);

                        tsf.Append("HeartRate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                        tsf.Append("HeartRate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });
                        tsf.Append("HeartRate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    }

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
from People as p 
where p.Age > 49
select timeseries(
    from HeartRate between $start and $end 
    group by 1h 
    select min(), max())
")
                        .AddParameter("start", DateTime.MinValue)
                        .AddParameter("end", DateTime.MaxValue);

                    var result = query.ToList();

                    Assert.Equal(2, result.Count);

                    for (int i = 0; i < 2; i++)
                    {
                        var agg = result[i];
                        Assert.Equal(3, agg.Count);

                        Assert.Equal(1, agg.Results.Length);

                        var val = agg.Results[0];

                        Assert.Equal(59, val.Min);
                        Assert.Equal(79, val.Max);

                        Assert.Equal(baseline.AddMinutes(60), val.From);
                        Assert.Equal(baseline.AddMinutes(120), val.To);
                    }
                }
            }
        }

        [Fact]
        public void CanQueryTimeSeriesAggregation_SelectSyntax_StaticIndexQuery()
        {
            using (var store = GetDocumentStore())
            {
                var baseline = DateTime.Today;

                using (var session = store.OpenSession())
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        var id = $"people/{i}";

                        session.Store(new Person
                        {
                            Name = "Oren",
                            Age = i * 30
                        }, id);

                        var tsf = session.TimeSeriesFor(id);

                        tsf.Append("HeartRate", baseline.AddMinutes(61), "watches/fitbit", new[] { 59d });
                        tsf.Append("HeartRate", baseline.AddMinutes(62), "watches/fitbit", new[] { 79d });
                        tsf.Append("HeartRate", baseline.AddMinutes(63), "watches/fitbit", new[] { 69d });
                    }

                    session.SaveChanges();
                }

                new PeopleIndex().Execute(store);

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var query = session.Advanced.RawQuery<TimeSeriesAggregation>(@"
from index 'People'
where Age > 49
select timeseries(
    from HeartRate between $start and $end 
    group by 1h 
    select min(), max())
")
                        .AddParameter("start", DateTime.MinValue)
                        .AddParameter("end", DateTime.MaxValue);

                    var result = query.ToList();

                    Assert.Equal(2, result.Count);

                    for (int i = 0; i < 2; i++)
                    {
                        var agg = result[i];
                        Assert.Equal(3, agg.Count);

                        Assert.Equal(1, agg.Results.Length);

                        var val = agg.Results[0];

                        Assert.Equal(59, val.Min);
                        Assert.Equal(79, val.Max);

                        Assert.Equal(baseline.AddMinutes(60), val.From);
                        Assert.Equal(baseline.AddMinutes(120), val.To);
                    }
                }
            }
        }

    }
}