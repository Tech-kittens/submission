// Run from repository root with mongosh, connected to an isolated Atlas test DB.
// PROVISION_MEETING_SEARCH=1 explicitly creates missing indexes. No index is overwritten.
const fs = require('fs');
const collection = db.getCollection('meeting_memory');
const definitions = ['atlas-text-index.json', 'atlas-vector-index.json'].map(name =>
  JSON.parse(fs.readFileSync('docs/meeting-preparation/' + name, 'utf8')));
printjson({ serverVersion: db.version(), database: db.getName() });
if (!db.getCollectionNames().includes('meeting_memory')) db.createCollection('meeting_memory');
for (const index of definitions) {
  const existing = collection.aggregate([{ $listSearchIndexes: { name: index.name } }]).toArray();
  if (!existing.length && process.env.PROVISION_MEETING_SEARCH === '1') {
    printjson(db.runCommand({ createSearchIndexes: 'meeting_memory', indexes: [index] }));
  }
}
const indexes = collection.aggregate([{ $listSearchIndexes: {} }]).toArray();
printjson(indexes.map(x => ({ name: x.name, status: x.status, queryable: x.queryable })));
for (const index of definitions) {
  if (!indexes.some(x => x.name === index.name && x.queryable)) throw new Error(index.name + ' is not queryable; wait for provisioning and retry.');
}
const vector = Array(768).fill(0); vector[0] = 1;
const lexical = [{ $search: { index: 'meeting_memory_text', compound: {
  filter: [{ equals: { path: 'OwnerAccountId', value: '__capability_probe__' } }, { equals: { path: 'Active', value: true } }],
  must: [{ text: { path: 'Content', query: 'authentication' } }]
} } }, { $limit: 5 }];
const semantic = [{ $vectorSearch: { index: 'meeting_memory_vector', path: 'Embedding', queryVector: vector,
  numCandidates: 100, limit: 5, filter: { OwnerAccountId: '__capability_probe__', Active: true,
    EmbeddingModel: 'gemini-embedding-001', EmbeddingDimensions: 768 } } }];
collection.aggregate(lexical, { maxTimeMS: 10000 }).toArray();
collection.aggregate(semantic, { maxTimeMS: 10000 }).toArray();
print('Atlas lexical and vector pipelines accepted.');
try {
  collection.aggregate([{ $rankFusion: { input: { pipelines: { lexical, semantic } } } },
    { $limit: 5 }, { $project: { score: { $meta: 'score' } } }], { maxTimeMS: 10000 }).toArray();
  print('Native rank fusion accepted; MeetingPreparation__NativeRankFusion=true is supported.');
} catch (error) {
  print('Native rank fusion unavailable; retain application RRF. Error code: ' + error.code);
}
