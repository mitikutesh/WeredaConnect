# Sprint 4 & 5 Timeline - Phase R3 (Sync Engine + First Feature)

**Duration**: 4 weeks (Mar 1 - Mar 28, 2026)
**Goal**: Make Offline-First Real

---

## 📅 Visual Timeline (Gantt Chart)

```
Week 1 (Mar 1-8): Infrastructure + Local Queue
───────────────────────────────────────────────────────────────────────
Day          │ Fri 1  │ Mon 4  │ Tue 5  │ Wed 6  │ Thu 7  │ Fri 8  │
─────────────┼────────┼────────┼────────┼────────┼────────┼────────┤
Pre-Flight   │ ████   │        │        │        │        │        │
@Infra       │        │ ████   │        │        │        │        │  A1: DynamoDB Tables
@Mobile      │        │ ████   │ ████   │ ████   │ ████   │ ████   │  A2 → B1 → B2
             │        │ (Dio)  │ (Dio)  │(Queue) │(Queue) │(Connec)│
─────────────┴────────┴────────┴────────┴────────┴────────┴────────┘

Week 2 (Mar 11-15): Sync Executor + Server Endpoint
───────────────────────────────────────────────────────────────────────
Day          │ Mon 11 │ Tue 12 │ Wed 13 │ Thu 14 │ Fri 15 │
─────────────┼────────┼────────┼────────┼────────┼────────┤
@Mobile      │ ████   │ ████   │ ████   │        │        │  C1: Executor
@Mobile      │        │        │ ████   │        │        │  C2: Integration
@Server      │        │        │ ████   │ ████   │        │  D1: SyncFunction
@Infra       │        │        │        │        │ ████   │  D2: API Gateway
─────────────┴────────┴────────┴────────┴────────┴────────┘

SPRINT 4 COMPLETE: Sync Engine Functional ✅
────────────────────────────────────────────

Week 3 (Mar 18-22): Crop Listing Flow
───────────────────────────────────────────────────────────────────────
Day          │ Mon 18 │ Tue 19 │ Wed 20 │ Thu 21 │ Fri 22 │
─────────────┼────────┼────────┼────────┼────────┼────────┤
@Mobile      │ ████   │ ████   │ ████   │        │        │  E1 → E2
             │ (BLoC) │ (UI)   │(Browse)│        │        │
@Server      │        │        │        │ ████   │ ████   │  F1: Repository + Function
@Infra       │        │        │        │        │ ████   │  F2: API Routes
─────────────┴────────┴────────┴────────┴────────┴────────┘

Week 4 (Mar 25-29): Polish + Integration Tests
───────────────────────────────────────────────────────────────────────
Day          │ Mon 25 │ Tue 26 │ Wed 27 │ Thu 28 │ Fri 29 │
─────────────┼────────┼────────┼────────┼────────┼────────┤
@Mobile      │ ████   │ ████   │        │        │        │  G1: Conflict Resolution
@Infra       │        │ ████   │ ████   │        │        │  H1: IAM Hardening
@Mobile      │        │        │        │ ████   │        │  I1: Integration Tests
@Architect   │        │        │        │        │ ████   │  I2: Documentation
All          │        │        │        │        │ ████   │  Buffer + Retrospective
─────────────┴────────┴────────┴────────┴────────┴────────┘

SPRINT 5 COMPLETE: First Feature Delivered ✅
PHASE R3 COMPLETE: Architecture Validated ✅
──────────────────────────────────────────────
```

---

## 📊 Task Dependency Graph

```
                    ┌──────────────────────┐
                    │ #1: Pre-Flight       │
                    │ (Sprint 3 A1+A2+B4)  │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #2: Infrastructure   │
                    │ (A1 DynamoDB +       │
                    │  A2 Dio Client)      │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #3: Sync Queue +     │
                    │ Connectivity Monitor │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #4: Sync Executor +  │
                    │ User Actions         │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #5: Server Sync      │
                    │ Endpoint + API Route │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #6: Crop Listing UI  │
                    │ (Create + Browse)    │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #7: Server Crop      │
                    │ Functions + Routes   │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #8: Conflict         │
                    │ Resolution Handler   │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #9: Least-Privilege  │
                    │ IAM per Lambda       │
                    └──────────┬───────────┘
                               │ blocks
                               ▼
                    ┌──────────────────────┐
                    │ #10: Integration     │
                    │ Tests + Documentation│
                    └──────────────────────┘
```

---

## 🎯 Milestones

| Date | Milestone | Exit Criteria |
|------|-----------|---------------|
| **Mar 1** | Sprint 4 Start | Pre-flight complete (A1, A2, B4 done) |
| **Mar 8** | Week 1 Complete | SyncQueue + ConnectivityMonitor functional |
| **Mar 15** | Sprint 4 Complete | Full sync loop working (mobile → server → conflict) |
| **Mar 22** | Week 3 Complete | Crop listing create + browse working |
| **Mar 28** | Sprint 5 Complete | Integration tests pass (100 cycles, <5s sync) |
| **Mar 29** | Phase R3 Complete | All 9 exit criteria met ✅ |

---

## 📈 Progress Tracking

### Sprint 4 (Week 1-2): Sync Engine Foundation

**Week 1 Goals**:
- [ ] Day 0: Pre-flight complete (Sprint 3 A1, A2, B4)
- [ ] Day 1: DynamoDB tables deployed (SyncMetadata + GSIs)
- [ ] Day 2: Dio ApiClient functional with auth
- [ ] Day 4: SyncQueue persisting operations to Isar
- [ ] Day 5: ConnectivityMonitor detecting online/offline

**Week 2 Goals**:
- [ ] Day 2: SyncExecutor executing queued operations
- [ ] Day 3: User actions auto-enqueue sync operations
- [ ] Day 5: Server /sync endpoint deployed and functional
- [ ] Milestone: Create listing offline → go online → listing syncs to server

### Sprint 5 (Week 3-4): First Feature + Polish

**Week 3 Goals**:
- [ ] Day 2: Crop listing create screen functional
- [ ] Day 3: Crop listing browse screen functional
- [ ] Day 5: Server crop endpoints deployed (GET /crops)
- [ ] Milestone: User can create and browse listings

**Week 4 Goals**:
- [ ] Day 2: Conflict resolution handling 409 responses
- [ ] Day 4: All Lambdas have least-privilege IAM
- [ ] Day 5: Integration test passes (100 offline-online cycles, zero data loss)
- [ ] Day 5: Documentation updated (ADRs, contracts, STATE.md, TASK.md)
- [ ] Milestone: Phase R3 complete, architecture validated

---

## 🚀 Success Metrics

### Sprint 4 Success Criteria
- [ ] SyncQueue operations enqueued: 100+
- [ ] Sync latency (connectivity → synced): <5s
- [ ] Retry success rate: >95%
- [ ] Unit test coverage: >80%
- [ ] Server cold start (SyncFunction): <2s
- [ ] Zero data loss in offline cycles: 100/100

### Sprint 5 Success Criteria
- [ ] Crop listings created offline: 50+
- [ ] Listings synced to server: 50/50 (100%)
- [ ] Conflict resolution success: 100%
- [ ] IAM policies least-privilege: All Lambdas
- [ ] Mobile app size: <50MB
- [ ] App load time on 3G: <3s

---

## 📋 Task Status Board

Use `/tasks` to view current task status in CLI.

| Task | Status | Owner | Blocked By | Due Date |
|------|--------|-------|------------|----------|
| #1: Pre-Flight | Pending | @Security + @Infra | None | Mar 1 |
| #2: Infrastructure | Pending | @Infra + @Mobile | #1 | Mar 5 |
| #3: Sync Queue | Pending | @Mobile | #2 | Mar 8 |
| #4: Sync Executor | Pending | @Mobile | #3 | Mar 12 |
| #5: Server Sync | Pending | @Server + @Infra | #4 | Mar 15 |
| #6: Crop UI | Pending | @Mobile | #5 | Mar 20 |
| #7: Server Crop | Pending | @Server + @Infra | #6 | Mar 22 |
| #8: Conflict Resolution | Pending | @Mobile | #7 | Mar 26 |
| #9: IAM Hardening | Pending | @Infra | #8 | Mar 27 |
| #10: Integration Tests | Pending | @Mobile + @Architect | #9 | Mar 29 |

---

## 🔄 Daily Standup Template

**What did I complete yesterday?**
- List completed tasks with checkbox updates

**What will I work on today?**
- Reference task number and specific phase

**Any blockers?**
- Check task dependencies, AWS access, SDK installations

**Update task status:**
```bash
# Start working on a task
# (Task tool will mark as in_progress when you begin)

# Mark task complete
# (Task tool will mark as completed when you finish)
```

---

## 📝 Sprint Retrospective Template (Mar 29)

**What went well? 🎉**
- List wins, successful implementations, smooth processes

**What could be improved? 🔧**
- List challenges, bottlenecks, unexpected issues

**What will we do differently in Sprint 6-8 (Phase R4)? 🚀**
- Action items, process improvements, tooling changes

**Estimate Accuracy:**
- Planned: 20-25 days
- Actual: ___ days
- Variance: ___% (over/under)

**Lessons Learned:**
- Technical decisions that worked well
- Technical decisions to reconsider
- Scope management insights

---

**Document Version**: 1.0.0
**Created**: 2026-02-28
**Last Updated**: 2026-02-28
**Next Review**: 2026-03-14 (Sprint 4 mid-point)
**Final Review**: 2026-03-29 (Sprint 5 retrospective)
