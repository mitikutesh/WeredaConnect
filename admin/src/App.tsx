import { BrowserRouter, Routes, Route, Navigate } from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { LocaleProvider } from "@/hooks/useLocale";
import { AuthProvider, useAuth } from "@/hooks/useAuth";
import { AppShell } from "@/components/layout/AppShell";
import { LoginPage } from "@/pages/LoginPage";
import { DashboardPage } from "@/pages/DashboardPage";
import { EscrowsPage } from "@/pages/EscrowsPage";
import { EscrowDetailPage } from "@/pages/EscrowDetailPage";
import { DisputesPage } from "@/pages/DisputesPage";
import { ManualPayoutsPage } from "@/pages/ManualPayoutsPage";
import { FraudReviewPage } from "@/pages/FraudReviewPage";
import { UsersPage } from "@/pages/UsersPage";
import { UserDetailPage } from "@/pages/UserDetailPage";
import { RevenuePage } from "@/pages/RevenuePage";
import { TransactionsPage } from "@/pages/TransactionsPage";
import { TransactionDetailPage } from "@/pages/TransactionDetailPage";
import { AnalyticsPage } from "@/pages/AnalyticsPage";
import { AuditLogPage } from "@/pages/AuditLogPage";
import { SystemHealthPage } from "@/pages/SystemHealthPage";
import { CompliancePage } from "@/pages/CompliancePage";
import type { ReactNode } from "react";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
    },
  },
});

function ProtectedRoute({ children }: { children: ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth();

  if (isLoading) {
    return (
      <div className="flex h-screen items-center justify-center text-text-muted">
        Loading...
      </div>
    );
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
}

function AppRoutes() {
  const { isAuthenticated } = useAuth();

  return (
    <Routes>
      <Route
        path="/login"
        element={isAuthenticated ? <Navigate to="/" replace /> : <LoginPage />}
      />
      <Route
        element={
          <ProtectedRoute>
            <AppShell />
          </ProtectedRoute>
        }
      >
        <Route index element={<DashboardPage />} />
        <Route path="escrows" element={<EscrowsPage />} />
        <Route path="escrows/:id" element={<EscrowDetailPage />} />
        <Route path="disputes" element={<DisputesPage />} />
        <Route path="payouts" element={<ManualPayoutsPage />} />
        <Route path="fraud-review" element={<FraudReviewPage />} />
        <Route path="users" element={<UsersPage />} />
        <Route path="users/:id" element={<UserDetailPage />} />
        <Route path="revenue" element={<RevenuePage />} />
        <Route path="transactions" element={<TransactionsPage />} />
        <Route path="transactions/:orderId" element={<TransactionDetailPage />} />
        <Route path="analytics" element={<AnalyticsPage />} />
        <Route path="audit-log" element={<AuditLogPage />} />
        <Route path="system-health" element={<SystemHealthPage />} />
        <Route path="compliance" element={<CompliancePage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <AuthProvider>
          <BrowserRouter>
            <AppRoutes />
          </BrowserRouter>
        </AuthProvider>
      </LocaleProvider>
    </QueryClientProvider>
  );
}
