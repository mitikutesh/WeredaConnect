import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";

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

// Disputes
export function useDisputes(params?: string) {
  return useQuery({
    queryKey: ["admin", "disputes", params],
    queryFn: () => api.get(`/admin/disputes${params ? `?${params}` : ""}`),
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
