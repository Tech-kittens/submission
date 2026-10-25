using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.MongoDB;
namespace Quacktion.Data.Preparation;

public sealed class GeminiMeetingEmbeddingService(IHttpClientFactory clients, IOptions<GeminiSettings> gemini,
    IOptions<PreparationSettings> settings, ILogger<GeminiMeetingEmbeddingService> logger) : IMeetingEmbeddingService
{
    public string Model => settings.Value.EmbeddingModel;
    public int Dimensions => settings.Value.EmbeddingDimensions;
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct, bool query = false)
    {
        var started = Stopwatch.GetTimestamp();
        Guard.Input(text.Length is > 0 and <= 6000, "embedding_text_invalid");
        if (Model != "gemini-embedding-001" || Dimensions is not (768 or 1536 or 3072))
            throw new AppException(503, "embedding_configuration_invalid");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Model}:embedContent");
        message.Headers.Add("x-goog-api-key", gemini.Value.ApiKey);
        message.Content = JsonContent.Create(new { model = "models/" + Model,
            content = new { parts = new[] { new { text } } }, outputDimensionality = Dimensions,
            taskType = query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT" });
        using var response = await clients.CreateClient("Gemini").SendAsync(message, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new AppException(503, "embedding_provider_unavailable");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var vector = result.RootElement.GetProperty("embedding").GetProperty("values").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (vector.Length != Dimensions || vector.Any(x => !float.IsFinite(x))) throw new AppException(502, "embedding_dimensions_invalid");
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm == 0) throw new AppException(502, "embedding_empty");
        logger.LogInformation("MeetingEmbedding {ElapsedMs}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return vector.Select(x => (float)(x / norm)).ToArray();
    }
}

public static class MeetingMemoryPipeline
{
    public static (BsonDocument[] Lexical, BsonDocument[] Vector) Branches(string owner, MeetingMemorySearchRequest request,
        float[] embedding, PreparationSettings settings)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(request.Query)
            && request.Query.Length <= 2000 && request.Limit is >= 1 and <= 30
            && request.ParticipantEmails.Length <= 30 && request.DocumentTypes.Length <= 10, "invalid_memory_search");
        var filter = new BsonDocument { { "OwnerAccountId", owner }, { "Active", true } };
        var searchFilters = new BsonArray { Eq("OwnerAccountId", owner), Eq("Active", true) };
        void In(string field, string[] values)
        {
            if (values.Length == 0) return;
            filter[field] = new BsonDocument("$in", new BsonArray(values));
            searchFilters.Add(new BsonDocument("in", new BsonDocument { { "path", field }, { "value", new BsonArray(values) } }));
        }
        In("ParticipantEmails", request.ParticipantEmails.Select(x => x.Trim().ToLowerInvariant()).ToArray());
        In("Type", request.DocumentTypes);
        if (request.MeetingType is { Length: > 0 } type) { filter["MeetingType"] = type; searchFilters.Add(Eq("MeetingType", type)); }
        if (request.From.HasValue || request.To.HasValue)
        {
            var range = new BsonDocument("path", "MeetingDate"); var match = new BsonDocument();
            if (request.From.HasValue) { range["gte"] = request.From.Value; match["$gte"] = request.From.Value; }
            if (request.To.HasValue) { range["lte"] = request.To.Value; match["$lte"] = request.To.Value; }
            filter["MeetingDate"] = match; searchFilters.Add(new BsonDocument("range", range));
        }
        var count = Math.Max(50, request.Limit * 4);
        BsonDocument[] lexical = [new("$search", new BsonDocument { { "index", settings.TextIndex },
            { "compound", new BsonDocument { { "filter", searchFilters }, { "must", new BsonArray {
                new BsonDocument("text", new BsonDocument { { "query", request.Query }, { "path", new BsonArray { "Content", "MeetingTitle", "Topics" } } }) } } } } }), new("$limit", count)];
        // Model metadata is mandatory only in the vector branch: documents awaiting embeddings remain lexical candidates.
        filter["EmbeddingModel"] = settings.EmbeddingModel; filter["EmbeddingDimensions"] = settings.EmbeddingDimensions;
        BsonDocument[] vector = [new("$vectorSearch", new BsonDocument { { "index", settings.VectorIndex }, { "path", "Embedding" },
            { "queryVector", new BsonArray(embedding.Select(x => (double)x)) }, { "numCandidates", count * 20 }, { "limit", count }, { "filter", filter } })];
        return (lexical, vector);
    }
    private static BsonDocument Eq(string path, BsonValue value) => new("equals", new BsonDocument { { "path", path }, { "value", value } });
    public static BsonDocument[] Native(BsonDocument[] lexical, BsonDocument[] vector, int limit) =>
    [new("$rankFusion", new BsonDocument { { "input", new BsonDocument("pipelines", new BsonDocument {
        { "lexical", new BsonArray(lexical) }, { "semantic", new BsonArray(vector) } }) } }),
     new("$limit", limit), new("$set", new BsonDocument("RankingScore", new BsonDocument("$meta", "score"))), new("$unset", "Embedding")];
    public static IReadOnlyList<(BsonDocument Document, double Score)> Fuse(IEnumerable<BsonDocument> lexical, IEnumerable<BsonDocument> vector, int limit)
    {
        var hits = new Dictionary<string, (BsonDocument Document, double Score)>();
        foreach (var branch in new[] { lexical, vector })
        {
            var rank = 0;
            foreach (var doc in branch)
            {
                var id = doc["_id"].AsString; var score = 1d / (60 + ++rank);
                hits[id] = (doc, score + (hits.TryGetValue(id, out var prior) ? prior.Score : 0));
            }
        }
        return hits.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Document["_id"].AsString).Take(limit).ToArray();
    }
}
public sealed class MeetingMemorySearchService(IMongoDbContext db, IMeetingEmbeddingService embeddings,
    IOptions<PreparationSettings> settings, ILogger<MeetingMemorySearchService> logger) : IMeetingMemorySearchService
{
    public async Task<MeetingMemorySearchResult> SearchAsync(string authenticatedOwner, MeetingMemorySearchRequest request, CancellationToken ct)
    {
        if (!settings.Value.MemoryEnabled) throw new AppException(503, "meeting_memory_disabled");
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var embedding = await embeddings.GenerateEmbeddingAsync(request.Query, timeout.Token, true);
        var branches = MeetingMemoryPipeline.Branches(authenticatedOwner, request, embedding, settings.Value);
        var collection = db.Collection<MeetingMemoryDocument>();
        var options = new AggregateOptions { MaxTime = TimeSpan.FromSeconds(10) };
        try
        {
            IReadOnlyList<(BsonDocument Document, double Score)>? hits = null;
            if (settings.Value.NativeRankFusion)
            {
                var build = await collection.Database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: timeout.Token);
                var version = build["versionArray"].AsBsonArray;
                logger.LogInformation("MeetingMemoryServerVersion {Major} {Minor}", version[0].AsInt32, version[1].AsInt32);
                if (version[0].AsInt32 >= 8)
                {
                    try
                    {
                        var docs = await collection.Aggregate<BsonDocument>(MeetingMemoryPipeline.Native(branches.Lexical, branches.Vector, request.Limit), options).ToListAsync(timeout.Token);
                        hits = docs.Select(x => (x, x["RankingScore"].ToDouble())).ToArray();
                    }
                    catch (MongoCommandException e) when (e.Code == 40324 || e.CodeName == "QueryFeatureNotAllowed")
                    { logger.LogInformation("NativeRankFusionUnavailable {Code}", e.Code); }
                }
            }
            if (hits is null)
            {
                // Both retrieval branches still execute on Atlas when native fusion is unsupported.
                var lexicalTask = collection.Aggregate<BsonDocument>(branches.Lexical.Append(new BsonDocument("$unset", "Embedding")).ToArray(), options).ToListAsync(timeout.Token);
                var vectorTask = collection.Aggregate<BsonDocument>(branches.Vector.Append(new BsonDocument("$unset", "Embedding")).ToArray(), options).ToListAsync(timeout.Token);
                await Task.WhenAll(lexicalTask, vectorTask);
                hits = MeetingMemoryPipeline.Fuse(await lexicalTask, await vectorTask, request.Limit);
            }
            return new(hits.Select(x => Hit(x.Document, x.Score)).ToArray(), "reciprocal_rank_fusion");
        }
        catch (MongoException) { throw new AppException(503, "atlas_meeting_search_unavailable"); }
        finally { logger.LogInformation("MeetingMemorySearch {ElapsedMs}", Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
    private static MeetingMemoryHit Hit(BsonDocument d, double score) => new(d["_id"].AsString, d["MeetingId"].AsString,
        d["MeetingTitle"].AsString, d["MeetingDate"].ToUniversalTime(), d["Type"].AsString, d["Content"].AsString,
        d["ParticipantEmails"].AsBsonArray.Select(x => x.AsString).ToArray(), d["SourceSegmentIds"].AsBsonArray.Select(x => x.AsString).ToArray(), score);
}
