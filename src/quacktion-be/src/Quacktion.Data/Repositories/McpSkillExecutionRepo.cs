using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.Data.MongoDB;

namespace Quacktion.Data.Repositories;

public sealed class McpSkillExecutionRepo(IMongoDbContext db) : MongoRepo<McpSkillExecution>(db), IMcpSkillExecutionRepo;
