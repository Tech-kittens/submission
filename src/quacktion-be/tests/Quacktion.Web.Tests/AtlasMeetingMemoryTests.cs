using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Quacktion.App.Models;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Preparation;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class AtlasMeetingFactAttribute : FactAttribute
{
    public AtlasMeetingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUACKTION_TEST_ATLAS")))
            Skip = "Requires an isolated Atlas test deployment with Search and Vector Search. Local MongoDB does not exercise Atlas.";
    }
}
public sealed class AtlasMeetingMemoryTests
{
    [AtlasMeetingFact]
    public async Task AtlasExecutesLexicalVectorAndNativeFusionWithOwnerFilters()
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_ATLAS")!;
        var name = "quacktion_atlas_test_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(uri); var database = client.GetDatabase(name);
        var context = new MongoDbContext(Options.Create(new MongoDbSettings { ConnectionString = uri, Database = name }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)); var ct = timeout.Token;
        try
        {
            var vector = new float[768]; vector[0] = 1;
            var collection = context.Collection<MeetingMemoryDocument>();
            await collection.InsertManyAsync(new[] {
                Document("exact", "alice", "authentication OAuth expiry", vector),
                Document("semantic", "alice", "Session lifetime concerns", vector),
                Document("private", "bob", "authentication OAuth expiry", vector),
                Document("missing", "alice", "authentication without embedding", [])
            }, cancellationToken: ct);
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            foreach (var filename in new[] { "atlas-text-index.json", "atlas-vector-index.json" })
            {
                var definition = BsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "docs/meeting-preparation", filename), ct));
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "createSearchIndexes", "meeting_memory" }, { "indexes", new BsonArray { definition } } }, cancellationToken: ct);
            }
            var branches = MeetingMemoryPipeline.Branches("alice", new() { Query = "authentication", ParticipantEmails = ["sarah@example.com"] }, vector, new());
            while (true)
            {
                var indexes = await collection.Aggregate<BsonDocument>(new[] { new BsonDocument("$listSearchIndexes", new BsonDocument()) }).ToListAsync(ct);
                if (indexes.Count == 2 && indexes.All(x => x.GetValue("queryable", false).AsBoolean)) break;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            // Search indexing is asynchronous even after an index becomes queryable.
            List<BsonDocument> lexical;
            do
            {
                lexical = await collection.Aggregate<BsonDocument>(branches.Lexical).ToListAsync(ct);
                if (lexical.Count >= 2) break;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            } while (true);
            Assert.Contains(lexical, x => x["_id"] == "exact"); Assert.Contains(lexical, x => x["_id"] == "missing");
            Assert.DoesNotContain(lexical, x => x["OwnerAccountId"] != "alice");
            var semantic = await collection.Aggregate<BsonDocument>(branches.Vector).ToListAsync(ct);
            Assert.Contains(semantic, x => x["_id"] == "semantic");
            Assert.DoesNotContain(semantic, x => x["OwnerAccountId"] != "alice" || x["_id"] == "missing");
            var fused = await collection.Aggregate<BsonDocument>(MeetingMemoryPipeline.Native(branches.Lexical, branches.Vector, 10)).ToListAsync(ct);
            Assert.NotEmpty(fused); Assert.All(fused, x => Assert.Equal("alice", x["OwnerAccountId"].AsString));
        }
        finally { await client.DropDatabaseAsync(name); }
    }
    private static MeetingMemoryDocument Document(string id, string owner, string content, float[] vector) => new() {
        Id = id, OwnerAccountId = owner, MeetingId = id, Content = content, Active = true, ParticipantEmails = ["sarah@example.com"],
        Embedding = vector, EmbeddingModel = "gemini-embedding-001", EmbeddingDimensions = 768 };
}
