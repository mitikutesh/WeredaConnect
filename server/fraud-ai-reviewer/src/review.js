'use strict';

// FRD-19 (fraud-detection-architecture.md section 21): the AI reviewer. The advisory cron (an AOT .NET Lambda)
// sends a redacted timeline; this module checks it again, asks a Claude model on Amazon Bedrock for a verdict in a
// fixed JSON shape, and answers { ok, concern, explanation, model, inputTokens, outputTokens } or { ok: false,
// error }. It reads and writes no table: the cron validates the answer once more and writes the advisory row.

const { SYSTEM_PROMPT, VERDICT_SCHEMA } = require('./prompt');

const CONCERNS = ['none', 'low', 'medium', 'high'];
const MAX_SIGNALS = 50;
const MAX_EVENTS = 100;
const MAX_EXPLANATION = 600;
const MAX_TOKENS = 4096;

/** A server value: [A-Za-z0-9_.:-] only, at most 48 characters; anything else is dropped. */
function token(value) {
  if (typeof value !== 'string') return null;
  const kept = value.replace(/[^A-Za-z0-9_.:-]/g, '').slice(0, 48);
  return kept.length > 0 ? kept : null;
}

function int(value, min, max) {
  return Number.isInteger(value) && value >= min && value <= max ? value : null;
}

function bool(value) {
  return typeof value === 'boolean' ? value : null;
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/**
 * Rebuilds the request from the fields the timeline may hold (an allow-list), so an unexpected field never
 * reaches the model. Returns null when the shape is wrong.
 */
function sanitizeRequest(event) {
  if (!isObject(event) || !isObject(event.subject) || !Array.isArray(event.signals) || !Array.isArray(event.events)) {
    return null;
  }

  const s = event.subject;
  return {
    subject: {
      role: token(s.role) || 'unknown',
      accountAgeDays: int(s.accountAgeDays, 0, 100000),
      faydaVerified: bool(s.faydaVerified),
      riskTier: token(s.riskTier) || 'unknown',
      score: int(s.score, 0, 1000000),
      enforcementScore: int(s.enforcementScore, 0, 1000000),
      scoreTruncated: bool(s.scoreTruncated) === true,
    },
    signals: event.signals.filter(isObject).slice(0, MAX_SIGNALS).map((x) => ({
      at: token(x.at),
      type: token(x.type),
      class: token(x.class),
      weight: int(x.weight, 0, 1000),
      status: token(x.status),
      resolution: token(x.resolution),
      entity: token(x.entity),
    })),
    events: event.events.filter(isObject).slice(0, MAX_EVENTS).map((x) => ({
      at: token(x.at),
      entity: token(x.entity),
      entityType: token(x.entityType),
      action: token(x.action),
      from: token(x.from),
      to: token(x.to),
      actor: token(x.actor),
      actorRole: token(x.actorRole),
    })),
    windowDays: int(event.windowDays, 1, 365),
    signalsTruncated: bool(event.signalsTruncated) === true,
    eventsTruncated: bool(event.eventsTruncated) === true,
  };
}

/**
 * Defence in depth: a run of 9 or more digits (a phone number, a Fayda FIN or FAN, an account number) means the
 * cron's redaction missed something, so nothing is sent.
 */
function containsIdentifier(request) {
  return /\d{9,}/.test(JSON.stringify(request));
}

/** The InvokeModel body (the Messages API shape on Bedrock). The system prompt is cached between reviews. */
function buildBody(request, effort) {
  const body = {
    anthropic_version: 'bedrock-2023-05-31',
    max_tokens: MAX_TOKENS,
    system: [{ type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } }],
    messages: [{
      role: 'user',
      content: [{
        type: 'text',
        text: `<timeline>\n${JSON.stringify(request)}\n</timeline>\nGive your verdict on this user.`,
      }],
    }],
    output_config: { format: { type: 'json_schema', schema: VERDICT_SCHEMA } },
  };
  if (effort) body.output_config.effort = effort;
  return body;
}

/** The explanation as the admin will read it: no control or format characters, single spaces, cut, no long numbers. */
function cleanExplanation(text) {
  return text
    .replace(/[\p{Cc}\p{Cf}]/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, MAX_EXPLANATION)
    .replace(/\d{9,}/g, '[number]')
    .trim();
}

/** Reads the model's answer: a refusal, a cut answer or anything outside the schema is not a verdict. */
function parseModelResponse(payload) {
  if (!isObject(payload)) return { ok: false, error: 'unparsable' };
  if (payload.stop_reason === 'refusal') return { ok: false, error: 'refused' };
  if (payload.stop_reason === 'max_tokens') return { ok: false, error: 'truncated' };

  const text = (Array.isArray(payload.content) ? payload.content : [])
    .filter((block) => isObject(block) && block.type === 'text' && typeof block.text === 'string')
    .map((block) => block.text)
    .join('');
  let verdict;
  try {
    verdict = JSON.parse(text);
  } catch {
    return { ok: false, error: 'unparsable' };
  }

  if (!isObject(verdict) || !CONCERNS.includes(verdict.concern) || typeof verdict.explanation !== 'string') {
    return { ok: false, error: 'unparsable' };
  }

  const explanation = cleanExplanation(verdict.explanation);
  if (explanation.length === 0) return { ok: false, error: 'unparsable' };

  const usage = isObject(payload.usage) ? payload.usage : {};
  return {
    ok: true,
    concern: verdict.concern,
    explanation,
    inputTokens: Number.isInteger(usage.input_tokens) ? usage.input_tokens : undefined,
    outputTokens: Number.isInteger(usage.output_tokens) ? usage.output_tokens : undefined,
  };
}

/**
 * One review. deps.invoke(modelId, body) calls Bedrock and returns the parsed response body; deps.log gets one
 * line per review with the outcome and token counts, never the timeline or the answer text.
 * Kill switch: AI_REVIEW_ENABLED must be "true", or nothing is sent. Bedrock errors are thrown, so the Lambda
 * reports a function error (and its error alarm counts it).
 */
async function review(event, { env, invoke, log }) {
  const modelId = env.BEDROCK_MODEL_ID || '';
  const done = (answer) => {
    log(JSON.stringify({
      event: 'FRAUD_AI_REVIEWER',
      outcome: answer.ok ? 'ok' : answer.error,
      model: modelId || '-',
      concern: answer.concern || '-',
      inputTokens: answer.inputTokens || 0,
      outputTokens: answer.outputTokens || 0,
    }));
    return answer;
  };

  if ((env.AI_REVIEW_ENABLED || '').toLowerCase() !== 'true') return done({ ok: false, error: 'disabled' });
  if (!modelId) return done({ ok: false, error: 'unconfigured' });

  const request = sanitizeRequest(event);
  if (request === null || containsIdentifier(request)) return done({ ok: false, error: 'invalid_input' });

  const effort = env.AI_REVIEW_EFFORT === undefined ? 'low' : env.AI_REVIEW_EFFORT;
  const payload = await invoke(modelId, buildBody(request, effort));
  const answer = parseModelResponse(payload);
  return done(answer.ok ? { ...answer, model: modelId } : answer);
}

module.exports = {
  review,
  sanitizeRequest,
  containsIdentifier,
  buildBody,
  parseModelResponse,
  cleanExplanation,
  CONCERNS,
  MAX_EXPLANATION,
};
