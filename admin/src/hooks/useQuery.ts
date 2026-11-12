import { useInfiniteQuery, useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import type { DisputeEvidence } from "@/lib/evidence";
import type { LedgerPage, StuckOrderPage, StuckSource } from "@/lib/legs";
import type { DeliveryTrail } from "@/lib/trail";

// Dashboard
export function useDashboardStats() {
  return useQuery({
    queryKey: ["admin", "dashboard"],
    queryFn: () => api.get("/admin/dashboard/stats"),
  });
}

// Escrows
export function useEscrows(params?: string) {
  return useQuery({
    queryKey: ["admin", "escrows", params],
    queryFn: () => api.get(`/admin/escrows${params ? `?${params}` : ""}`),
  });
}

export function useEscrowDetail(id: string) {
  return useQuery({
    queryKey: ["admin", "escrows", id],
    queryFn: () => api.get(`/admin/escrows/${id}`),
    enabled: !!id,
  });
}

export function useEscrowAudit(id: string) {
  return useQuery({
    queryKey: ["admin", "escrows", id, "audit"],
    queryFn: () => api.get(`/admin/escrows/${id}/audit`),
    enabled: !!id,
  });
}

/**
 * ESC-24: the ledger rows of an escrow (ESC-20), 50 a page in ref order. Read only. Asked only for
 * an escrow on the ledger: one of the legacy model has no rows.
 */
export function useEscrowLedger(escrowId: string, enabled: boolean) {
  return useInfiniteQuery({
    queryKey: ["admin", "escrows", escrowId, "ledger"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<LedgerPage>(
        `/admin/escrows/${encodeURIComponent(escrowId)}/ledger?limit=50${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
    enabled: enabled && !!escrowId,
  });
}

/**
 * ESC-24: the stuck orders, oldest first. "queue" is the admin stuck-order queue (ESC-06), "deadline"
 * the orders whose commitment deadline passed and nothing acted on it (ESC-16). A deadline page can
 * come back short or empty with a nextToken; the page loads the next one on demand.
 */
export function useStuckOrders(source: StuckSource) {
  return useInfiniteQuery({
    queryKey: ["admin", "orders", "queue", source],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<StuckOrderPage>(
        `/admin/orders/queue?source=${source}&limit=50${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

// Disputes. FUP-16: the server scans and then filters by status, so a page can come back short
// with a nextToken; the disputes page loads the next one on demand.
export function useDisputes() {
  return useInfiniteQuery({
    queryKey: ["admin", "disputes"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ disputes?: unknown[]; nextToken?: string | null }>(
        `/admin/disputes${pageParam ? `?nextToken=${encodeURIComponent(pageParam)}` : ""}`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

/** FUP-16: escrows in one status, page by page (the disputes page's awaiting-settlement tab). */
export function useEscrowPages(status: string) {
  return useInfiniteQuery({
    queryKey: ["admin", "escrows", "pages", status],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ escrows?: unknown[]; nextToken?: string | null }>(
        `/admin/escrows?status=${encodeURIComponent(status)}${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

/** LT-13: the trip trail of a dispute's delivery (admin group only), for the Trip trail tab. */
export function useDeliveryTrail(deliveryId: string) {
  return useQuery({
    queryKey: ["admin", "deliveries", deliveryId, "trail"],
    queryFn: () => api.get<DeliveryTrail>(`/admin/deliveries/${encodeURIComponent(deliveryId)}/trail`),
    enabled: !!deliveryId,
  });
}

/**
 * FRD-14: the evidence of one dispute (admin group only): the timeline, the handoffs and the photos.
 * Never the messages; the verdict refreshes it (the "disputes" key).
 */
export function useDisputeEvidence(escrowId: string) {
  return useQuery({
    queryKey: ["admin", "disputes", escrowId, "evidence"],
    queryFn: () => api.get<DisputeEvidence>(`/admin/disputes/${encodeURIComponent(escrowId)}/evidence`),
    enabled: !!escrowId,
  });
}

/**
 * FRD-14: the dispute's message threads, read only once the admin asks. The server audits every
 * read, so the query never runs by itself (no refetch on focus, no retry, kept out of the "disputes"
 * key the verdict refreshes) and nothing stays cached once the Messages tab closes.
 */
export function useDisputeMessages(escrowId: string, open: boolean) {
  return useQuery({
    queryKey: ["admin", "dispute-messages", escrowId],
    queryFn: () =>
      api.get<DisputeEvidence>(`/admin/disputes/${encodeURIComponent(escrowId)}/evidence?include=messages`),
    enabled: open && !!escrowId,
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}

export function useResolveDispute() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: object }) =>
      api.post(`/admin/disputes/${id}/resolve`, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "disputes"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "escrows"] });
    },
  });
}

// Fraud Signals. FRD-07: one status partition of the server's status index, newest first, 50 a
// page with the risk score of each user on it; the page loads the next one on demand.
export function useFraudSignals(status: "pending" | "reviewed") {
  return useInfiniteQuery({
    queryKey: ["admin", "fraud-signals", status],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ signals?: unknown[]; users?: unknown[]; nextToken?: string | null }>(
        `/admin/fraud-signals?status=${status}&limit=50${
          pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""
        }`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

/** FRD-07: the held queue, escrows frozen for fraud review, oldest first. */
export function useHeldEscrows() {
  return useInfiniteQuery({
    queryKey: ["admin", "escrows", "held"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ escrows?: unknown[]; nextToken?: string | null }>(
        `/admin/escrows/held?limit=50${pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""}`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

/** What the escrow routes answer: the settled escrow, or the 202 envelope of a queued payout (ESCROW_034). */
export interface HeldExitResult {
  status?: string;
  error?: { code?: string; message?: string };
}

/**
 * FRD-07: release or refund a held escrow through the escrow routes (fraud-detection-architecture.md
 * section 18.3): admin group only, with a reason of 10 to 500 characters that the server audits.
 * The release route asks for a delivery reference. Since FUP-147 (ESC-20) it must be the escrow's own
 * linked delivery when the escrow has one (else 422 ESCROW_VALIDATION); an escrow without a delivery
 * (a self-pickup order) sends its order or escrow id there, which the server does not check.
 */
export function useSettleHeldEscrow() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      escrow,
      op,
      reason,
    }: {
      escrow: { escrowId: string; deliveryId?: string; orderId?: string };
      op: "release" | "refund";
      reason: string;
    }) => {
      const idempotencyKey = `admin:${escrow.escrowId}:${op}:${Date.now()}`;
      return op === "release"
        ? api.post<HeldExitResult>("/escrow/release", {
            escrowId: escrow.escrowId,
            trigger: "admin_override",
            deliveryId: escrow.deliveryId || escrow.orderId || escrow.escrowId,
            idempotencyKey,
            reason,
          })
        : api.post<HeldExitResult>("/escrow/refund", {
            escrowId: escrow.escrowId,
            trigger: "admin_override",
            idempotencyKey,
            reason,
          });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "escrows"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "dashboard"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "transactions"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "payouts"] });
    },
  });
}

export function useFraudAction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: object }) =>
      api.post(`/admin/fraud-signals/${id}/action`, action),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "fraud-signals"] });
      // FRD-05: reject suspends the user, and the dashboard counts signals.
      queryClient.invalidateQueries({ queryKey: ["admin", "users"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "dashboard"] });
    },
  });
}

// Users
export function useUsers(params?: string) {
  return useQuery({
    queryKey: ["admin", "users", params],
    queryFn: () => api.get(`/admin/users${params ? `?${params}` : ""}`),
  });
}

export function useUserDetail(id: string) {
  return useQuery({
    queryKey: ["admin", "users", id],
    queryFn: () => api.get(`/admin/users/${id}`),
    enabled: !!id,
  });
}

export function useUserAction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: object }) =>
      api.post(`/admin/users/${id}/action`, action),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "users"] });
    },
  });
}

// Manual payout queue (ESC-02): settlements the live gateway could not make.
// Oldest first, 50 per page; the page loads the next page on demand.
export function useManualPayouts() {
  return useInfiniteQuery({
    queryKey: ["admin", "payouts"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ payouts?: unknown[]; nextToken?: string | null }>(
        `/admin/payouts/manual?limit=50${pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""}`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

export function useCompleteManualPayout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      escrowId,
      ref,
      externalRef,
    }: {
      escrowId: string;
      ref: string;
      externalRef: string;
    }) =>
      api.post(
        `/admin/payouts/${encodeURIComponent(escrowId)}/${encodeURIComponent(ref)}/complete`,
        { externalRef },
      ),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "payouts"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "escrows"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "transactions"] });
    },
  });
}

// Unapplied payments (FUP-02): captured Telebirr payments the webhook did not apply.
// The server pages through the escrow audit, so a page can be empty while more remain.
export function useUnappliedPayments() {
  return useInfiniteQuery({
    queryKey: ["admin", "unapplied-payments"],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ payments?: unknown[]; nextToken?: string | null }>(
        `/admin/payments/unapplied?limit=50${pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""}`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

export function useRecordManualRefund() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      orderId,
      body,
    }: {
      orderId: string;
      body: {
        paymentOrderId: string;
        telebirrRef: string;
        amount: number;
        reason: string;
        merchOrderId?: string;
      };
    }) =>
      api.post<{ alreadyRecorded?: boolean; amountChecked?: boolean }>(
        `/admin/payments/${encodeURIComponent(orderId)}/refund-manual`,
        body,
      ),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "unapplied-payments"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "transactions"] });
    },
  });
}

// Listing reports (FUP-67, listing-report-api.md): buyers' reports, one status at a time.
export type ListingReportStatus = "open" | "dismissed" | "actioned";

export function useListingReports(status: ListingReportStatus) {
  return useInfiniteQuery({
    queryKey: ["admin", "listing-reports", status],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      api.get<{ reports?: unknown[]; listings?: unknown[]; nextToken?: string | null }>(
        `/admin/listing-reports?status=${status}&limit=50${pageParam ? `&nextToken=${encodeURIComponent(pageParam)}` : ""}`,
      ),
    getNextPageParam: (lastPage) => lastPage.nextToken ?? null,
  });
}

export function useReviewListingReports() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      listingId,
      decision,
      note,
    }: {
      listingId: string;
      decision: "dismissed" | "actioned";
      note?: string;
    }) =>
      api.post<{ listingId: string; decision: string; reviewed: number; reviewedAt: string }>(
        `/admin/listing-reports/${encodeURIComponent(listingId)}/review`,
        note ? { decision, note } : { decision },
      ),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "listing-reports"] });
    },
  });
}

// Transaction Monitor (unified order + delivery + escrow lifecycle view)
export function useTransactions(params?: string) {
  return useQuery({
    queryKey: ["admin", "transactions", params],
    queryFn: () => api.get(`/admin/transactions${params ? `?${params}` : ""}`),
  });
}

export function useTransactionDetail(orderId: string) {
  return useQuery({
    queryKey: ["admin", "transactions", orderId, "detail"],
    queryFn: () => api.get(`/admin/transactions/${orderId}`),
    enabled: !!orderId,
  });
}

// Unified cross-entity audit feed (order/delivery/admin legs).
export function useAuditLog() {
  return useQuery({
    queryKey: ["admin", "audit", "recent"],
    queryFn: () => api.get("/admin/audit"),
  });
}

// Analytics
export function useRevenueAnalytics() {
  return useQuery({
    queryKey: ["admin", "analytics", "revenue"],
    queryFn: () => api.get("/admin/analytics/revenue"),
  });
}

export function useTransactionAnalytics() {
  return useQuery({
    queryKey: ["admin", "analytics", "transactions"],
    queryFn: () => api.get("/admin/analytics/transactions"),
  });
}

// System
export function useSystemHealth() {
  return useQuery({
    queryKey: ["admin", "health"],
    queryFn: () => api.get("/admin/health/overview"),
  });
}

export function useComplianceSummary() {
  return useQuery({
    queryKey: ["admin", "compliance"],
    queryFn: () => api.get("/admin/compliance/summary"),
  });
}
