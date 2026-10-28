import { useInfiniteQuery, useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
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

// Fraud Signals
export function useFraudSignals(params?: string) {
  return useQuery({
    queryKey: ["admin", "fraud-signals", params],
    queryFn: () => api.get(`/admin/fraud-signals${params ? `?${params}` : ""}`),
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
