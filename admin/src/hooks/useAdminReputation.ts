import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import {
  APPEAL_CHANGED,
  decisionPath,
  type ActionResult,
  type AppealQueueStatus,
  type AppealsPage,
  type DecisionResult,
  type ReputationAction,
  type ReputationDecision,
  type UserReputationPage,
} from "@/lib/reputation";

/* ESC-32 (reputation-api.md 1.4.0 section 9): the admin reputation routes. Admin group only; the
 * two reads work whatever the flags say, the two writes answer 503 REPUTATION_UNAVAILABLE while
 * reputationPenalties is off. */

/**
 * 9.1: a user's reputation, restrictions and events, 50 events a page (newest first). Every page
 * carries the summary and the restrictions again; the panel reads them from the first page.
 */
export function useUserReputation(userId: string) {
  return useInfiniteQuery({
    queryKey: ["admin", "reputation", "user", userId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<UserReputationPage>(
        `/admin/users/${encodeURIComponent(userId)}/reputation?limit=50${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
    enabled: !!userId,
    // A 404 (no such user) or 503 (table not configured) will not change on a retry.
    retry: false,
  });
}

/**
 * 9.2: the appeals queue, oldest first: party appeals ("pending") or DELIVERY_OVERDUE reviews
 * ("review"). The photo links live 10 minutes, so the queue is refetched when the admin comes back
 * to the tab (the default) and is never kept fresh for longer than that.
 */
export function useReputationAppeals(status: AppealQueueStatus) {
  return useInfiniteQuery({
    queryKey: ["admin", "reputation", "appeals", status],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<AppealsPage>(
        `/admin/reputation/appeals?status=${status}&limit=50${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
    staleTime: 0,
  });
}

/**
 * 9.3: excuse or uphold one event, with the note the party will see. An uphold spends the party's
 * one appeal, so it carries what the page showed of it (`seenAppealTextAt`: when the text arrived,
 * left out when the page showed no text). The server answers 409 REPUTATION_APPEAL_CHANGED when
 * the stored appeal differs; the lists are then refetched so the admin reads the appeal first.
 */
export function useReputationDecision() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      userId,
      eventId,
      decision,
      note,
      until,
      seenAppealTextAt,
    }: {
      userId: string;
      eventId: string;
      decision: ReputationDecision;
      note: string;
      until?: string;
      seenAppealTextAt?: string;
    }) =>
      api.post<DecisionResult>(decisionPath(userId, eventId), {
        decision,
        note,
        ...(until ? { until } : {}),
        ...(decision === "uphold" && seenAppealTextAt ? { seenAppealTextAt } : {}),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "reputation"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "audit"] });
    },
    onError: (err: unknown) => {
      if ((err as { code?: string }).code === APPEAL_CHANGED) {
        queryClient.invalidateQueries({ queryKey: ["admin", "reputation"] });
      }
    },
  });
}

/** 9.4: tag or untag "Low reputation", restrict, or lift the restriction, with a reason. */
export function useReputationAction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      userId,
      action,
      reason,
      until,
    }: {
      userId: string;
      action: ReputationAction;
      reason: string;
      until?: string;
    }) =>
      api.post<ActionResult>(
        `/admin/users/${encodeURIComponent(userId)}/reputation`,
        until ? { action, reason, until } : { action, reason },
      ),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "reputation"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "audit"] });
    },
  });
}
