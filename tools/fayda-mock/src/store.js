'use strict';

// Code / token store. Every item is keyed by "code" (the table pk) and carries
// "expiresAt" (epoch seconds, the table TTL attribute). DynamoDB TTL deletes
// lazily, so reads also check expiresAt.
//
// Interface:
//   putNew(item)      -> true, or false if the key already exists
//   consume(key, now) -> the item, marking it used atomically; null if missing,
//                        expired or already used
//   get(key, now)     -> the item if present and unexpired, else null

function memoryStore() {
  const items = new Map();
  return {
    async putNew(item) {
      if (items.has(item.code)) return false;
      items.set(item.code, { ...item, used: false });
      return true;
    },
    async consume(key, now) {
      const item = items.get(key);
      if (!item || item.used || item.expiresAt <= now) return null;
      item.used = true;
      return { ...item };
    },
    async get(key, now) {
      const item = items.get(key);
      return item && item.expiresAt > now ? { ...item } : null;
    },
  };
}

function dynamoStore(tableName) {
  // Loaded lazily: the Lambda Node 24 runtime ships AWS SDK v3, local runs
  // and tests use memoryStore and never need it.
  const {
    DynamoDBClient, PutItemCommand, UpdateItemCommand, GetItemCommand,
  } = require('@aws-sdk/client-dynamodb');
  const client = new DynamoDBClient({});

  // Values are stored as one JSON string attribute to keep marshalling trivial.
  const toItem = (item) => ({
    code: { S: item.code },
    expiresAt: { N: String(item.expiresAt) },
    used: { BOOL: false },
    data: { S: JSON.stringify(item) },
  });
  const fromItem = (attrs) => ({ ...JSON.parse(attrs.data.S), used: attrs.used.BOOL });

  return {
    async putNew(item) {
      try {
        await client.send(new PutItemCommand({
          TableName: tableName,
          Item: toItem(item),
          ConditionExpression: 'attribute_not_exists(code)',
        }));
        return true;
      } catch (err) {
        if (err.name === 'ConditionalCheckFailedException') return false;
        throw err;
      }
    },
    async consume(key, now) {
      try {
        const res = await client.send(new UpdateItemCommand({
          TableName: tableName,
          Key: { code: { S: key } },
          UpdateExpression: 'SET used = :true',
          ConditionExpression: 'attribute_exists(code) AND used = :false AND expiresAt > :now',
          ExpressionAttributeValues: {
            ':true': { BOOL: true }, ':false': { BOOL: false }, ':now': { N: String(now) },
          },
          ReturnValues: 'ALL_NEW',
        }));
        return fromItem(res.Attributes);
      } catch (err) {
        if (err.name === 'ConditionalCheckFailedException') return null;
        throw err;
      }
    },
    async get(key, now) {
      const res = await client.send(new GetItemCommand({
        TableName: tableName, Key: { code: { S: key } }, ConsistentRead: true,
      }));
      if (!res.Item) return null;
      const item = fromItem(res.Item);
      return item.expiresAt > now ? item : null;
    },
  };
}

module.exports = { memoryStore, dynamoStore };
