using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Config;
using Raven.Server.Rachis;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json.Parsing;
using Index = Raven.Server.Documents.Indexes.Index;

namespace Raven.Server.ServerWide.Commands.Indexes
{
    public class PutIndexCommand : UpdateDatabaseCommand
    {
        public IndexDefinition Definition;

        public PutIndexCommand()
        {
            // for deserialization
        }

        public PutIndexCommand(IndexDefinition definition, string databaseName, string source, DateTime createdAt, string uniqueRequestId, int revisionsToKeep)
            : base(databaseName, uniqueRequestId)
        {
            Definition = definition;
            Source = source;
            CreatedAt = createdAt;
            RevisionsToKeep = revisionsToKeep;
        }

        public DateTime CreatedAt { get; set; }

        public string Source { get; set; }
        
        public int RevisionsToKeep { get; set; }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            try
            {
                var indexNames = record.Indexes.Select(x => x.Value.Name).ToHashSet(OrdinalIgnoreCaseStringStructComparer.Instance);

                if (indexNames.Add(Definition.Name) == false && record.Indexes.TryGetValue(Definition.Name, out var definition) == false)
                {
                    throw new InvalidOperationException($"Can not add index: {Definition.Name} because an index with the same name but different casing already exist");
                }

                var globalRollingSetting = GetGlobalRollingSetting(record);

                record.AddIndex(Definition, Source, CreatedAt, etag, RevisionsToKeep, globalRollingSetting);

            }
            catch (Exception e)
            {
                throw new RachisApplyException("Failed to update index", e);
            }
        }

        public static bool GetGlobalRollingSetting(DatabaseRecord record)
        {
            if (record.Settings.TryGetValue(RavenConfiguration.GetKey(x => x.Indexing.Rolling), out var value) == false)
                return false;

            if (bool.TryParse(value, out var result) == false)
                return false;

            return result;
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Definition)] = TypeConverter.ToBlittableSupportedType(Definition);
            json[nameof(Source)] = Source;
            json[nameof(CreatedAt)] = CreatedAt;
            json[nameof(RevisionsToKeep)] = RevisionsToKeep;
        }
    }
}
